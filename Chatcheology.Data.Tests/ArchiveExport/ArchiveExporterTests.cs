using System.Text;
using Chatcheology.Archive;
using Chatcheology.Core.Models;
using Chatcheology.Data.ArchiveExport;
using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Tests.Matching;
using Chatcheology.Data.Tests.Phase7;
using Chatcheology.Data.Viewer;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.ArchiveExport.ArchiveExportFixture;

namespace Chatcheology.Data.Tests.ArchiveExport
{
    /// <summary>
    /// The archive exporter (CH-16) over a synthetic workspace: what it writes matches what the
    /// workspace readers report, every guard refuses before writing, and a stop leaves nothing.
    /// </summary>
    public sealed class ArchiveExporterTests : IDisposable
    {
        private const long Local = MatchingTestWorkspace.LocalParticipantID;
        private const long Other = MatchingTestWorkspace.OtherParticipantID;

        private const string ExportSha = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
        private const string ExternalBasis = "The synthetic message store names this payload for this message.";

        private readonly ArchiveExportFixture _fixture = new();

        public void Dispose() => _fixture.Dispose();

        /// <summary>The standard conversation's attachments and payloads.</summary>
        private sealed record Scenario(
            long Single,
            long SharedFirst,
            long SharedSecond,
            long Missing,
            long Unmatched,
            long LowQualityCopy,
            (long AssetID, string Sha256, byte[] Bytes) SinglePayload,
            (long AssetID, string Sha256, byte[] Bytes) SharedPayload,
            long RunID,
            long ForwardedText,
            long NotForwardedText,
            long UnknownText);

        /// <summary>
        /// Three restored attachments (two sharing one payload), one reviewed missing, two unmatched,
        /// a system message, a second day, msgstore facts and a low-quality link, and three text
        /// messages: one recorded as forwarded, one recorded as not forwarded, one with no fact.
        /// </summary>
        private Scenario Build()
        {
            var forwardedText = _fixture.AddText("Good morning");
            var single = _fixture.AddPhoto(Other);
            _fixture.AddText("Synthetic system notice", sender: null);
            var sharedFirst = _fixture.AddPhoto(Local);
            var sharedSecond = _fixture.AddPhoto(Other);
            var missing = _fixture.AddPhoto(Local);
            var unmatched = _fixture.AddPhoto(Other);
            var lowQualityCopy = _fixture.AddSecondAttachment(unmatched);
            var notForwardedText = _fixture.AddText("Next day", Other, Day.AddDays(1));
            var unknownText = _fixture.AddText("No recorded fact", Local, Day.AddDays(1));

            var singlePayload = _fixture.AddPayload("alpha payload", ("photo-alpha.jpg", Copy.Good));

            var sharedPayload = _fixture.AddPayload(
                "beta payload", ("Shared-First.JPG", Copy.Good), ("shared-second.jpg", Copy.Good));

            // Candidates on the day, so the run evaluates the outgoing photos against something.
            _fixture.Decisions.Evidence.AddTokenAsset(Day, isSent: true, 1);
            _fixture.Decisions.Evidence.AddTokenAsset(Day, isSent: true, 2);

            _fixture.SetImportSource(ExportSha, "Synthetic/Zone");

            var run = _fixture.RunEvidence();
            _fixture.Review(missing, run);

            _fixture.Confirm(single, singlePayload.AssetID);
            _fixture.Confirm(
                sharedFirst, sharedPayload.AssetID, ExternalBasis, Phase7IndependentBasisType.ExternalProvenanceOrGroundTruth);
            _fixture.Confirm(sharedSecond, sharedPayload.AssetID, actor: null);

            _fixture.Facts(
                new MsgstoreFactRow(single, true, 4, null),
                new MsgstoreFactRow(missing, false, null, null),
                new MsgstoreFactRow(lowQualityCopy, false, null, single));

            _fixture.MessageFacts(
                new MessageMsgstoreFactRow(forwardedText, true, 2),
                new MessageMsgstoreFactRow(notForwardedText, false, null));

            return new Scenario(
                single, sharedFirst, sharedSecond, missing, unmatched, lowQualityCopy, singlePayload, sharedPayload, run,
                forwardedText, notForwardedText, unknownText);
        }

        private ArchiveExportResult Export(string output, int resolved = 3, int missing = 1, int? pageSize = null)
        {
            var exporter = pageSize is { } size ? new ArchiveExporter { PageSize = size } : new ArchiveExporter();

            return exporter.Export(_fixture.Request(output, resolved, missing));
        }

        private static Dictionary<long, ArchiveAttachment> Attachments(ArchiveReader reader) =>
            reader.ReadFirstPage(ArchiveReader.MaximumPageSize).Messages
                .SelectMany(m => m.Attachments)
                .ToDictionary(a => a.AttachmentID);

        // ---------------------------------------------------------------------------------------
        // Round trip.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void RoundTrip_EveryMessageParticipantAndAttachment_MatchesTheWorkspaceReaders()
        {
            var scenario = Build();
            var output = _fixture.Output();

            // Two messages a page, so the export reads the workspace across several pages.
            var result = Export(output, pageSize: 2);

            using var workspace = WorkspaceDatabase.OpenReadOnlyConnection(_fixture.DatabasePath);
            var conversationReader = new ConversationReader();

            var summary = conversationReader.ReadConversations(workspace)
                .Single(c => c.ConversationID == MatchingTestWorkspace.ConversationID);

            var expected = conversationReader
                .ReadFirstPage(workspace, MatchingTestWorkspace.ConversationID, ConversationReader.MaximumPageSize)
                .Messages;

            var ids = expected.SelectMany(m => m.Attachments).Select(a => a.AttachmentID).ToList();
            var states = new Phase7ReconstructionReader().Read(workspace, ids).ToDictionary(s => s.AttachmentID);

            var messageFacts = new MessageMsgstoreFactReader()
                .Read(workspace, expected.Select(m => m.MessageID).ToList());

            Assert.Equal(2, messageFacts.Count);

            using var reader = ArchiveReader.Open(output);

            Assert.Equal(summary.Title, reader.Info.ConversationTitle);
            Assert.Equal(ExportSha.ToLowerInvariant(), reader.Info.SourceExportSha256);
            Assert.Equal("Synthetic/Zone", reader.Info.TimeZoneID);
            Assert.Equal(Local, reader.Info.LocalParticipantID);
            Assert.Equal(ArchiveExporter.ProducerVersion, reader.Info.ProducerVersion);

            Assert.Equal(
                summary.Participants.Select(p => (p.ParticipantID, p.DisplayName, p.ParticipantID == Local)),
                reader.ReadParticipants().Select(p => (p.ParticipantID, p.DisplayName, p.IsLocal)));

            var actual = reader.ReadFirstPage(ArchiveReader.MaximumPageSize).Messages;

            Assert.Equal(expected.Count, actual.Count);
            Assert.Equal(expected.Count, result.Counts.Messages);

            foreach (var (w, a) in expected.Zip(actual))
            {
                Assert.Equal(w.MessageID, a.MessageID);
                Assert.Equal(w.SequenceNumber, a.SequenceNumber);
                Assert.Equal(w.MessageDateTimeLocal, a.DateTimeLocal);
                Assert.Equal(w.SenderParticipantID, a.SenderParticipantID);
                Assert.Equal(
                    w.MessageType == MessageType.System ? ArchiveMessageKind.System : ArchiveMessageKind.User, a.Kind);
                Assert.Equal(w.MessageContent, a.Text);

                messageFacts.TryGetValue(w.MessageID, out var fact);
                Assert.Equal(fact?.IsForwarded, a.IsForwarded);
                Assert.Equal(fact?.ForwardScore, a.ForwardScore);

                Assert.Equal(w.Attachments.Select(x => (x.AttachmentID, x.Ordinal)), a.Attachments.Select(x => (x.AttachmentID, x.Ordinal)));

                foreach (var attachment in a.Attachments)
                {
                    var state = states[attachment.AttachmentID];

                    var expectedState = state.Status == Phase7ResolutionStatus.Resolved
                        ? ArchiveAttachmentState.Restored
                        : state.HasCurrentReviewedInsufficientEvidence
                            ? ArchiveAttachmentState.Missing
                            : ArchiveAttachmentState.Unmatched;

                    Assert.Equal(expectedState, attachment.State);
                }
            }

            Assert.Contains(actual, m => m.Kind == ArchiveMessageKind.System && m.SenderParticipantID is null);

            var byID = Attachments(reader);
            Assert.Equal(ArchiveAttachmentState.Restored, byID[scenario.Single].State);
            Assert.Equal(ArchiveAttachmentState.Missing, byID[scenario.Missing].State);
            Assert.Equal(ArchiveAttachmentState.Unmatched, byID[scenario.Unmatched].State);
            Assert.Equal(ArchiveAttachmentState.Unmatched, byID[scenario.LowQualityCopy].State);

            Assert.Equal(new ArchiveCounts(2, expected.Count, 6, 3, 1, 2, 2), result.Counts);
            Assert.True(ArchiveContents.Check(output).Ok);
        }

        [Fact]
        public void RestoredMedia_IsTheRecordedPayload_WithItsRecordedDescription()
        {
            var scenario = Build();
            var output = _fixture.Output();
            Export(output);

            using var reader = ArchiveReader.Open(output);
            var page = reader.ReadFirstPage(ArchiveReader.MaximumPageSize);
            var single = page.Messages.SelectMany(m => m.Attachments).Single(a => a.AttachmentID == scenario.Single);

            Assert.Equal(scenario.SinglePayload.Sha256, single.MediaSha256);
            Assert.Equal(scenario.SinglePayload.Bytes, File.ReadAllBytes(reader.MediaFullPath(single)!));

            var media = page.Media[scenario.SinglePayload.Sha256];
            Assert.Equal("Image", media.MediaType);
            Assert.Equal(scenario.SinglePayload.Bytes.Length, media.SizeBytes);
        }

        // ---------------------------------------------------------------------------------------
        // Forwarded and low-quality.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ForwardedAndLowQualityLinks_AreCarriedForAttachments()
        {
            var scenario = Build();
            var output = _fixture.Output();
            var result = Export(output);

            using var reader = ArchiveReader.Open(output);
            var attachments = Attachments(reader);

            Assert.True(attachments[scenario.Single].IsForwarded);
            Assert.Equal(4, attachments[scenario.Single].ForwardScore);
            Assert.False(attachments[scenario.Missing].IsForwarded);
            Assert.Null(attachments[scenario.Missing].ForwardScore);
            Assert.Equal(scenario.Single, attachments[scenario.LowQualityCopy].LowQualityCopyOfAttachmentID);

            // No fact recorded: not known, never "not forwarded".
            Assert.Null(attachments[scenario.SharedFirst].IsForwarded);
            Assert.Null(attachments[scenario.SharedFirst].LowQualityCopyOfAttachmentID);

            Assert.Equal(0, result.LowQualityLinksOutsideConversation);
        }

        [Fact]
        public void MessageForwarded_ComesFromTheMessageFact_AndNoFactStaysUnknown()
        {
            var scenario = Build();
            var output = _fixture.Output();
            Export(output);

            using var reader = ArchiveReader.Open(output);
            var messages = reader.ReadFirstPage(ArchiveReader.MaximumPageSize).Messages.ToDictionary(m => m.MessageID);

            Assert.True(messages[scenario.ForwardedText].IsForwarded);
            Assert.Equal(2, messages[scenario.ForwardedText].ForwardScore);

            Assert.False(messages[scenario.NotForwardedText].IsForwarded);
            Assert.Null(messages[scenario.NotForwardedText].ForwardScore);

            // No row: not known, never "not forwarded". Media and system messages carry no fact either.
            Assert.Null(messages[scenario.UnknownText].IsForwarded);
            Assert.Null(messages[scenario.UnknownText].ForwardScore);
            Assert.Equal(2, messages.Values.Count(m => m.IsForwarded is not null));

            // Filling the columns does not change the format version.
            Assert.Equal(1, reader.Info.FormatVersion);
        }

        [Fact]
        public void ALowQualityLinkToAnotherConversation_IsLeftOutAndCounted()
        {
            _fixture.AddText("Before");
            var copy = _fixture.AddPhoto(Other);
            var elsewhere = _fixture.Decisions.Evidence.AddOutgoingInOtherConversation(Day);

            _fixture.RunEvidence();
            _fixture.Facts(new MsgstoreFactRow(copy, true, 1, elsewhere));

            var output = _fixture.Output();
            var result = Export(output, resolved: 0, missing: 0);

            Assert.Equal(1, result.LowQualityLinksOutsideConversation);

            using var reader = ArchiveReader.Open(output);
            var attachment = Attachments(reader)[copy];

            Assert.Null(attachment.LowQualityCopyOfAttachmentID);
            Assert.True(attachment.IsForwarded);
        }

        // ---------------------------------------------------------------------------------------
        // Basis.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Basis_IsTheCurrentConfirmation()
        {
            var scenario = Build();
            var output = _fixture.Output();
            Export(output);

            using var reader = ArchiveReader.Open(output);
            var attachments = Attachments(reader);

            var history = _fixture.Decisions.History(scenario.SharedFirst).Single();
            var basis = attachments[scenario.SharedFirst].Basis!;

            Assert.Equal("ExternalProvenanceOrGroundTruth", basis.BasisType);
            Assert.Equal(ExternalBasis, basis.BasisText);
            Assert.Equal("Reviewer A", basis.Actor);
            Assert.Equal(Seconds(history.OccurredAtUtc), basis.ConfirmedUtc);

            Assert.Equal("HumanContentOrContextInspection", attachments[scenario.Single].Basis!.BasisType);
            Assert.Null(attachments[scenario.SharedSecond].Basis!.Actor);

            Assert.Null(attachments[scenario.Missing].Basis);
            Assert.Null(attachments[scenario.Unmatched].Basis);
        }

        [Fact]
        public void Basis_AfterARevokeAndAReconfirm_IsTheNewConfirmation()
        {
            var scenario = Build();

            var first = _fixture.Decisions.History(scenario.Single).Single();
            _fixture.Decisions.Revoke(scenario.Single, first.AttachmentResolutionEventID);

            const string Again = "Looked again with the synthetic sender and recognised it this time.";
            _fixture.Confirm(scenario.Single, scenario.SinglePayload.AssetID, Again, actor: "Reviewer B");

            var output = _fixture.Output();
            Export(output);

            using var reader = ArchiveReader.Open(output);
            var basis = Attachments(reader)[scenario.Single].Basis!;

            Assert.Equal(Again, basis.BasisText);
            Assert.Equal("Reviewer B", basis.Actor);
        }

        [Fact]
        public void Basis_AfterAReplacement_IsTheReplacement()
        {
            var scenario = Build();

            var current = _fixture.Decisions.History(scenario.Single).Single();

            const string Replaced = "The synthetic store names the shared payload for this message instead.";
            _fixture.Decisions.Replace(
                scenario.Single, scenario.SharedPayload.AssetID, current.AttachmentResolutionEventID, description: Replaced);

            var output = _fixture.Output();

            // The single payload is no longer anyone's, so one payload remains.
            var result = Export(output);
            Assert.Equal(1, result.Counts.MediaFiles);

            using var reader = ArchiveReader.Open(output);
            var attachment = Attachments(reader)[scenario.Single];

            Assert.Equal(Replaced, attachment.Basis!.BasisText);
            Assert.Equal(scenario.SharedPayload.Sha256, attachment.MediaSha256);
        }

        // ---------------------------------------------------------------------------------------
        // Media.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ASharedPayload_IsStoredOnce_UnderTheNameOfTheCopyUsed()
        {
            var scenario = Build();
            var output = _fixture.Output();
            var result = Export(output);

            var mediaFiles = Directory.GetFiles(Path.Combine(output, ArchiveFormat.MediaFolderName))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToList();

            Assert.Equal(
                new[] { scenario.SinglePayload.Sha256 + ".jpg", scenario.SharedPayload.Sha256 + ".jpg" }
                    .Order(StringComparer.Ordinal),
                mediaFiles);

            Assert.Equal(2, result.Counts.MediaFiles);

            using var reader = ArchiveReader.Open(output);
            var attachments = Attachments(reader);

            // Both attachments name the first recorded copy, whose name keeps its own spelling.
            Assert.Equal("Shared-First.JPG", attachments[scenario.SharedFirst].OriginalFileName);
            Assert.Equal("Shared-First.JPG", attachments[scenario.SharedSecond].OriginalFileName);
            Assert.Equal(
                reader.MediaFullPath(attachments[scenario.SharedFirst]),
                reader.MediaFullPath(attachments[scenario.SharedSecond]));
            Assert.Equal(0, result.FromLaterCopy);
        }

        [Fact]
        public void AnAbsentOrWrongCopy_FallsThroughToTheNextGoodOne()
        {
            _fixture.AddText("Before");
            var attachment = _fixture.AddPhoto(Other);

            var payload = _fixture.AddPayload(
                "gamma payload",
                ("first-absent.jpg", Copy.Absent),
                ("second-wrong.png", Copy.WrongBytes),
                ("third-good.webp", Copy.Good));

            _fixture.RunEvidence();
            _fixture.Confirm(attachment, payload.AssetID);

            var output = _fixture.Output();
            var result = Export(output, resolved: 1, missing: 0);

            Assert.Equal(1, result.UnreadableCopies);
            Assert.Equal(1, result.DifferentCopies);
            Assert.Equal(1, result.FromLaterCopy);

            using var reader = ArchiveReader.Open(output);
            var restored = Attachments(reader)[attachment];

            Assert.Equal("third-good.webp", restored.OriginalFileName);
            Assert.Equal(payload.Bytes, File.ReadAllBytes(reader.MediaFullPath(restored)!));
            Assert.Equal([payload.Sha256 + ".webp"], Directory.GetFiles(Path.Combine(output, "media")).Select(Path.GetFileName));
        }

        [Fact]
        public void ARestoredAttachmentWithNoGoodFile_StopsTheExport_AndLeavesNothing()
        {
            _fixture.AddText("Before");
            var good = _fixture.AddPhoto(Other);
            var bad = _fixture.AddPhoto(Local);

            var goodPayload = _fixture.AddPayload("good payload", ("good.jpg", Copy.Good));
            var badPayload = _fixture.AddPayload(
                "bad payload", ("bad-absent.jpg", Copy.Absent), ("bad-wrong.jpg", Copy.WrongBytes));

            _fixture.RunEvidence();
            _fixture.Confirm(good, goodPayload.AssetID);
            _fixture.Confirm(bad, badPayload.AssetID);

            var before = _fixture.WorkspaceFingerprint();
            var output = _fixture.Output();

            var stop = Assert.Throws<ArchiveExportStoppedException>(() => Export(output, resolved: 2, missing: 0));

            Assert.Contains("1 of 2", stop.Message, StringComparison.Ordinal);
            Assert.Empty(_fixture.ExportsContent());
            Assert.Equal(before, _fixture.WorkspaceFingerprint());
        }

        // ---------------------------------------------------------------------------------------
        // Guards.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData(2, 1)]
        [InlineData(4, 1)]
        [InlineData(3, 0)]
        [InlineData(3, 2)]
        public void AWrongExpectedCount_IsRefused_AndNothingIsWritten(int resolved, int missing)
        {
            Build();
            var output = _fixture.Output();

            var refusal = Assert.Throws<ArchiveExportRefusedException>(() => Export(output, resolved, missing));

            Assert.Contains(resolved != 3 ? "ExpectResolved" : "ExpectMissingReviewed", refusal.Message, StringComparison.Ordinal);
            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void AnExistingOutput_IsRefused_AndLeftAsFound()
        {
            Build();
            var output = _fixture.Output();
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "keep.txt"), "keep");

            Assert.Throws<ArchiveExportRefusedException>(() => Export(output));

            Assert.Equal([Path.GetFileName(output), Path.Combine(Path.GetFileName(output), "keep.txt")], _fixture.ExportsContent());
        }

        [Fact]
        public void AnExistingPartialFolder_IsRefused_AndLeftAsFound()
        {
            Build();
            var output = _fixture.Output();
            Directory.CreateDirectory(output + ArchiveExporter.PartialSuffix);

            Assert.Throws<ArchiveExportRefusedException>(() => Export(output));

            Assert.Equal([Path.GetFileName(output) + ArchiveExporter.PartialSuffix], _fixture.ExportsContent());
        }

        [Fact]
        public void AnOutputWhoseParentDoesNotExist_IsRefused()
        {
            Build();

            Assert.Throws<ArchiveExportRefusedException>(
                () => Export(Path.Combine(_fixture.ExportsFolder, "no-such-folder", "archive")));

            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void AnOutputInsideARepository_IsRefused()
        {
            Build();

            // A synthetic repository: any folder holding a .git entry counts.
            var repository = Path.Combine(_fixture.ExportsFolder, "some-repository");
            Directory.CreateDirectory(Path.Combine(repository, ".git"));

            var refusal = Assert.Throws<ArchiveExportRefusedException>(
                () => Export(Path.Combine(repository, "archive")));

            Assert.Contains("repository", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(["some-repository", Path.Combine("some-repository", ".git")], _fixture.ExportsContent());
        }

        [Fact]
        public void AnOutputInsideThisRepository_IsRefused()
        {
            Build();

            // Refused before anything is created, so naming a path in the repository writes nothing there.
            var inside = Path.Combine(AppContext.BaseDirectory, "never-created-archive");

            Assert.Throws<ArchiveExportRefusedException>(() => Export(inside));
            Assert.False(Directory.Exists(inside));
        }

        [Fact]
        public void AnOutputInsideAMediaSourceFolder_IsRefused()
        {
            Build();
            var inside = Path.Combine(_fixture.MediaRoot, "archive");
            var before = Directory.GetFileSystemEntries(_fixture.MediaRoot, "*", SearchOption.AllDirectories).Order().ToList();

            var refusal = Assert.Throws<ArchiveExportRefusedException>(() => Export(inside));

            Assert.Contains("media source", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(before, Directory.GetFileSystemEntries(_fixture.MediaRoot, "*", SearchOption.AllDirectories).Order());
        }

        [Fact]
        public void ALocalParticipantOutsideTheConversation_IsRefused()
        {
            Build();

            Assert.Throws<ArchiveExportRefusedException>(
                () => new ArchiveExporter().Export(
                    _fixture.Request(_fixture.Output(), 3, 1, MatchingTestWorkspace.OutsiderParticipantID)));

            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void AMissingWorkspace_IsRefused_WithoutNamingThePath()
        {
            var request = new ArchiveExportRequest
            {
                WorkspacePath = Path.Combine(_fixture.ExportsFolder, "absent.db"),
                ConversationID = 1,
                LocalParticipantID = 1,
                OutputPath = _fixture.Output(),
                ExpectResolved = 0,
                ExpectMissingReviewed = 0,
            };

            var refusal = Assert.Throws<ArchiveExportRefusedException>(() => new ArchiveExporter().Export(request));

            Assert.DoesNotContain(_fixture.ExportsFolder, refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void APartialFolderAppearingDuringTheRun_IsLeftAlone()
        {
            Build();
            var output = _fixture.Output();

            var exporter = new ArchiveExporter
            {
                BeforePartialCreatedForTests = partial =>
                {
                    Directory.CreateDirectory(partial);
                    File.WriteAllText(Path.Combine(partial, "someone-else.txt"), "theirs");
                },
            };

            Assert.Throws<ArchiveExportStoppedException>(() => exporter.Export(_fixture.Request(output, 3, 1)));

            Assert.True(File.Exists(Path.Combine(output + ArchiveExporter.PartialSuffix, "someone-else.txt")));
            Assert.False(Directory.Exists(output));
        }

        [Fact]
        public void AStrayTemporaryFile_StopsTheExport_AndLeavesNothing()
        {
            Build();
            var output = _fixture.Output();

            var exporter = new ArchiveExporter
            {
                AfterMediaCopiedForTests = partial =>
                    File.WriteAllText(Path.Combine(partial, ArchiveFormat.MediaFolderName, "left.jpg.tmp"), "x"),
            };

            Assert.Throws<ArchiveExportStoppedException>(() => exporter.Export(_fixture.Request(output, 3, 1)));

            Assert.Empty(_fixture.ExportsContent());
        }

        // ---------------------------------------------------------------------------------------
        // Review fixes: stops after writing began, media destination failures, every media root.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData(typeof(IOException))]
        [InlineData(typeof(UnauthorizedAccessException))]
        [InlineData(typeof(SqliteException))]
        public void AnyFaultAfterWritingBegan_StopsWithAFixedMessage_AndLeavesNothing(Type faultType)
        {
            Build();
            var output = _fixture.Output();
            const string Secret = "C:\\synthetic-private-folder\\file.jpg";

            Exception Fault() => faultType == typeof(SqliteException)
                ? new SqliteException(Secret, 10)
                : (Exception)Activator.CreateInstance(faultType, Secret)!;

            var exporter = new ArchiveExporter { AfterMediaCopiedForTests = _ => throw Fault() };

            var stop = Assert.Throws<ArchiveExportStoppedException>(() => exporter.Export(_fixture.Request(output, 3, 1)));

            Assert.DoesNotContain("synthetic-private-folder", stop.Message, StringComparison.Ordinal);
            Assert.Contains(faultType.Name, stop.Message, StringComparison.Ordinal);
            Assert.IsType(faultType, stop.InnerException);
            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void AMediaFileThatCannotBeWritten_StopsTheExport_InsteadOfFallingThroughToAnotherCopy()
        {
            _fixture.AddText("Before");
            var attachment = _fixture.AddPhoto(Other);

            // Two good copies with different extensions, so they would be stored under different names.
            var payload = _fixture.AddPayload("delta payload", ("first.jpg", Copy.Good), ("second.png", Copy.Good));

            _fixture.RunEvidence();
            _fixture.Confirm(attachment, payload.AssetID);

            var output = _fixture.Output();

            // A folder where the first copy's temporary file would go: the destination cannot be written.
            var exporter = new ArchiveExporter
            {
                BeforeMediaCopiedForTests = media =>
                    Directory.CreateDirectory(Path.Combine(media, payload.Sha256 + ".jpg.tmp")),
            };

            var stop = Assert.Throws<ArchiveExportStoppedException>(
                () => exporter.Export(_fixture.Request(output, 1, 0)));

            Assert.Contains("could not be written", stop.Message, StringComparison.Ordinal);
            Assert.IsType<ArchiveDestinationWriteException>(stop.InnerException);
            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void AnOutputInsideAMediaSourceWithNoUsedCopy_IsRefused()
        {
            Build();

            var unusedRoot = Path.Combine(_fixture.ExportsFolder, "unused-source");
            Directory.CreateDirectory(unusedRoot);

            _fixture.Decisions.ExecuteDirect(
                $"""
                INSERT INTO MediaSource (DisplayName, SourceType, RootPath, ImportedDateTimeUtc)
                VALUES ('Unused synthetic source', 'SyntheticMediaDirectory', '{unusedRoot.Replace("'", "''")}',
                        '2026-08-17T16:00:00.0000000Z');
                """);

            var refusal = Assert.Throws<ArchiveExportRefusedException>(
                () => Export(Path.Combine(unusedRoot, "archive")));

            Assert.Contains("media source", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(["unused-source"], _fixture.ExportsContent());
        }

        // ---------------------------------------------------------------------------------------
        // Review additions: decision histories and link order.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ARevokedAttachmentNeverReconfirmed_IsUnmatched()
        {
            var scenario = Build();

            var first = _fixture.Decisions.History(scenario.Single).Single();
            _fixture.Decisions.Revoke(scenario.Single, first.AttachmentResolutionEventID);

            var output = _fixture.Output();
            var result = Export(output, resolved: 2, missing: 1);

            Assert.Equal(3, result.Counts.Unmatched);

            using var reader = ArchiveReader.Open(output);
            var attachment = Attachments(reader)[scenario.Single];

            Assert.Equal(ArchiveAttachmentState.Unmatched, attachment.State);
            Assert.Null(attachment.MediaSha256);
            Assert.Null(attachment.Basis);
        }

        [Fact]
        public void ARevokedAttachmentReviewedAgainstItsCurrentRun_IsMissing()
        {
            var scenario = Build();

            var first = _fixture.Decisions.History(scenario.Single).Single();
            _fixture.Decisions.Revoke(scenario.Single, first.AttachmentResolutionEventID);
            _fixture.Review(scenario.Single, scenario.RunID);

            var output = _fixture.Output();
            var result = Export(output, resolved: 2, missing: 2);

            Assert.Equal(2, result.Counts.Missing);

            using var reader = ArchiveReader.Open(output);
            Assert.Equal(ArchiveAttachmentState.Missing, Attachments(reader)[scenario.Single].State);
        }

        [Fact]
        public void AReviewedAttachmentLaterConfirmed_IsRestored_AndNotCountedAsMissing()
        {
            var scenario = Build();

            _fixture.Confirm(scenario.Missing, scenario.SinglePayload.AssetID);

            // The old review still exists; the count of reviewed-missing must not include it.
            Assert.Throws<ArchiveExportRefusedException>(() => Export(_fixture.Output("refused"), 4, 1));

            var output = _fixture.Output();
            var result = Export(output, resolved: 4, missing: 0);

            Assert.Equal(4, result.Counts.Restored);
            Assert.Equal(0, result.Counts.Missing);

            using var reader = ArchiveReader.Open(output);
            var attachment = Attachments(reader)[scenario.Missing];

            Assert.Equal(ArchiveAttachmentState.Restored, attachment.State);
            Assert.Equal(Phase7DecisionFixture.HumanBasis, attachment.Basis!.BasisText);
        }

        [Fact]
        public void ALowQualityCopyEarlierInSequenceThanItsFullCopy_IsLinked()
        {
            _fixture.AddText("Before");
            var copy = _fixture.AddPhoto(Other);
            _fixture.AddText("Between");
            var full = _fixture.AddPhoto(Other);

            _fixture.RunEvidence();
            _fixture.Facts(new MsgstoreFactRow(copy, false, null, full));

            var output = _fixture.Output();
            Export(output, resolved: 0, missing: 0);

            using var reader = ArchiveReader.Open(output);
            var page = reader.ReadFirstPage(ArchiveReader.MaximumPageSize);
            var attachments = page.Messages.SelectMany(m => m.Attachments).ToList();

            var copyIndex = attachments.FindIndex(a => a.AttachmentID == copy);
            var fullIndex = attachments.FindIndex(a => a.AttachmentID == full);

            Assert.True(copyIndex < fullIndex);
            Assert.Equal(full, attachments[copyIndex].LowQualityCopyOfAttachmentID);
        }

        // ---------------------------------------------------------------------------------------
        // What the archive does not hold.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TheArchive_HoldsOnlyTheFormatsTables_AndNoSourcePathAnywhere()
        {
            Build();
            var output = _fixture.Output();
            Export(output);

            var database = Path.Combine(output, ArchiveFormat.DatabaseFileName);

            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                   {
                       DataSource = database,
                       Mode = SqliteOpenMode.ReadOnly,
                       Pooling = false,
                   }.ToString()))
            {
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";

                var tables = new List<string>();

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }

                Assert.Equal(ArchiveFormat.TableNames.Order(StringComparer.Ordinal), tables);
            }

            // Byte search of the whole file, in both encodings SQLite could have used.
            var bytes = File.ReadAllBytes(database);
            var relativePaths = _fixture.Decisions.Evidence.Rows("SELECT RelativePath FROM MediaFile;");

            Assert.NotEmpty(relativePaths);

            foreach (var needle in relativePaths.Append(_fixture.MediaRoot).Append("folder/").Append("Synthetic files"))
            {
                Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(needle)), "A source path or name is in the archive.");
                Assert.False(Contains(bytes, Encoding.Unicode.GetBytes(needle)), "A source path or name is in the archive.");
            }

            // And nothing but the format's files is in the folder.
            Assert.Equal(
                [ArchiveFormat.ContentsFileName, ArchiveFormat.DatabaseFileName, ArchiveFormat.ManifestFileName],
                Directory.GetFiles(output).Select(Path.GetFileName).Order(StringComparer.Ordinal));

            Assert.Equal([Path.Combine(output, ArchiveFormat.MediaFolderName)], Directory.GetDirectories(output));
        }

        // ---------------------------------------------------------------------------------------
        // Read-only, deterministic, progress.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TheWorkspace_IsUnchangedByteForByte()
        {
            Build();
            var before = _fixture.WorkspaceFingerprint();
            var mediaBefore = Directory.GetFiles(_fixture.MediaRoot, "*", SearchOption.AllDirectories)
                .Order().Select(ArchiveHashing.Sha256OfFile).ToList();

            Export(_fixture.Output());

            Assert.Equal(before, _fixture.WorkspaceFingerprint());
            Assert.Equal(
                mediaBefore,
                Directory.GetFiles(_fixture.MediaRoot, "*", SearchOption.AllDirectories).Order().Select(ArchiveHashing.Sha256OfFile));
        }

        [Fact]
        public void TwoExports_HaveTheSameRowsAndMediaBytes_ExceptTheCreationTime()
        {
            Build();
            var first = _fixture.Output("first");
            var second = _fixture.Output("second");

            Export(first);
            Export(second, pageSize: 3);

            Assert.Equal(DatabaseRows(first), DatabaseRows(second));

            foreach (var relative in ArchiveContents.RelativeFiles(first)
                         .Where(r => r.StartsWith(ArchiveFormat.MediaFolderName + "/", StringComparison.Ordinal)))
            {
                Assert.Equal(
                    File.ReadAllBytes(ArchiveContents.FullPath(first, relative)),
                    File.ReadAllBytes(ArchiveContents.FullPath(second, relative)));
            }

            Assert.Equal(
                ArchiveContents.RelativeFiles(first),
                ArchiveContents.RelativeFiles(second));
        }

        [Fact]
        public void Progress_IsReportedForEveryStage_AndEndsComplete()
        {
            Build();
            var reports = new List<ArchiveExportProgress>();

            new ArchiveExporter { PageSize = 2 }.Export(
                _fixture.Request(_fixture.Output(), 3, 1), new ImmediateProgress(reports.Add));

            foreach (var stage in Enum.GetValues<ArchiveExportStage>())
            {
                var last = reports.Last(r => r.Stage == stage);

                Assert.Equal(last.Total, last.Done);
                Assert.True(last.Total > 0);
            }

            Assert.True(reports.Count(r => r.Stage == ArchiveExportStage.ReadingMessages) > 1);
        }

        [Fact]
        public void TheManifest_RecordsTheCounts()
        {
            Build();
            var output = _fixture.Output();
            var result = Export(output);

            var manifest = ArchiveManifest.Parse(File.ReadAllBytes(Path.Combine(output, ArchiveFormat.ManifestFileName)));

            Assert.Equal(result.Counts, manifest.Counts);
            Assert.Equal(ArchiveFormat.FormatVersion, manifest.FormatVersion);
            Assert.Equal(ArchiveExporter.ProducerVersion, manifest.ProducerVersion);

            using var reader = ArchiveReader.Open(output);
            Assert.Equal(reader.Info.CreatedUtc, manifest.CreatedUtc);
        }

        // ---------------------------------------------------------------------------------------
        // Preview and cancellation (CH-18).
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Preview_CountsWhatTheExportWillHold_AndWritesNothing()
        {
            Build();
            var before = _fixture.WorkspaceFingerprint();

            var preview = new ArchiveExporter().Preview(
                _fixture.DatabasePath, MatchingTestWorkspace.ConversationID, Local);

            Assert.Equal(new ArchiveExportPreview(9, 3, 1, 2, 2, "alpha payload".Length + "beta payload".Length), preview);
            Assert.Equal(before, _fixture.WorkspaceFingerprint());
            Assert.Empty(_fixture.ExportsContent());

            // The counts it shows are the counts the export then accepts.
            var result = new ArchiveExporter().Export(
                _fixture.Request(_fixture.Output(), preview.Restored, preview.Missing));

            Assert.Equal(preview.Messages, result.Counts.Messages);
            Assert.Equal(preview.Restored, result.Counts.Restored);
            Assert.Equal(preview.Missing, result.Counts.Missing);
            Assert.Equal(preview.Unmatched, result.Counts.Unmatched);
            Assert.Equal(preview.MediaFiles, result.Counts.MediaFiles);
        }

        [Fact]
        public void Preview_RefusesAParticipantOutsideTheConversation()
        {
            Build();

            Assert.Throws<ArchiveExportRefusedException>(
                () => new ArchiveExporter().Preview(
                    _fixture.DatabasePath, MatchingTestWorkspace.ConversationID, MatchingTestWorkspace.OutsiderParticipantID));
        }

        [Fact]
        public void AnAlreadyCancelledExport_WritesNothing()
        {
            Build();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(
                () => new ArchiveExporter().Export(_fixture.Request(_fixture.Output(), 3, 1), null, cancellation.Token));

            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void ACancelWhileReadingMessages_WritesNothing()
        {
            Build();
            using var cancellation = new CancellationTokenSource();

            var progress = new ImmediateProgress(report =>
            {
                if (report.Stage == ArchiveExportStage.ReadingMessages)
                {
                    cancellation.Cancel();
                }
            });

            Assert.ThrowsAny<OperationCanceledException>(
                () => new ArchiveExporter { PageSize = 2 }.Export(
                    _fixture.Request(_fixture.Output(), 3, 1), progress, cancellation.Token));

            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void ACancelWhileCopyingMedia_RemovesThePartialFolder()
        {
            Build();
            using var cancellation = new CancellationTokenSource();
            var sawPartial = false;

            var exporter = new ArchiveExporter
            {
                BeforeMediaCopiedForTests = folder =>
                {
                    sawPartial = Directory.Exists(folder);
                    cancellation.Cancel();
                },
            };

            Assert.ThrowsAny<OperationCanceledException>(
                () => exporter.Export(_fixture.Request(_fixture.Output(), 3, 1), null, cancellation.Token));

            Assert.True(sawPartial);
            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void ACancelAfterTheMediaAreCopied_RemovesThePartialFolder()
        {
            Build();
            using var cancellation = new CancellationTokenSource();

            var exporter = new ArchiveExporter { AfterMediaCopiedForTests = _ => cancellation.Cancel() };

            Assert.ThrowsAny<OperationCanceledException>(
                () => exporter.Export(_fixture.Request(_fixture.Output(), 3, 1), null, cancellation.Token));

            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void ACancelWhileWritingMessages_RemovesThePartialFolder()
        {
            Build();
            using var cancellation = new CancellationTokenSource();

            var progress = new ImmediateProgress(report =>
            {
                if (report.Stage == ArchiveExportStage.WritingArchive)
                {
                    cancellation.Cancel();
                }
            });

            // The last progress report of the stage comes after the last message, so the cancel is
            // seen by the final check before the rename.
            Assert.ThrowsAny<OperationCanceledException>(
                () => new ArchiveExporter().Export(_fixture.Request(_fixture.Output(), 3, 1), progress, cancellation.Token));

            Assert.Empty(_fixture.ExportsContent());
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private static DateTime Seconds(DateTime value) =>
            new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        private static bool Contains(byte[] haystack, byte[] needle) =>
            haystack.AsSpan().IndexOf(needle) >= 0;

        /// <summary>Every row of every table, except the creation time.</summary>
        private static List<string> DatabaseRows(string folder)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(folder, ArchiveFormat.DatabaseFileName),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());

            connection.Open();

            var rows = new List<string>();

            foreach (var table in ArchiveFormat.TableNames)
            {
                using var command = connection.CreateCommand();
                command.CommandText = table == "ArchiveInfo"
                    ? $"SELECT * FROM ArchiveInfo WHERE Key <> '{ArchiveInfo.Keys.CreatedUtc}' ORDER BY 1;"
                    : $"SELECT * FROM {table} ORDER BY 1;";

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    rows.Add(table + ":" + string.Join(
                        "|",
                        Enumerable.Range(0, reader.FieldCount)
                            .Select(i => reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
                }
            }

            return rows;
        }

        /// <summary>A progress sink that records on the calling thread, unlike <see cref="Progress{T}"/>.</summary>
        private sealed class ImmediateProgress(Action<ArchiveExportProgress> report) : IProgress<ArchiveExportProgress>
        {
            public void Report(ArchiveExportProgress value) => report(value);
        }
    }
}
