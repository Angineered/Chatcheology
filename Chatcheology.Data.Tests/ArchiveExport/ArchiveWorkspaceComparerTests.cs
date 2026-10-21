using Chatcheology.Archive;
using Chatcheology.Data.ArchiveExport;
using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Tests.Phase7;
using Chatcheology.Data.Tests.Matching;
using static Chatcheology.Data.Tests.ArchiveExport.ArchiveExportFixture;

namespace Chatcheology.Data.Tests.ArchiveExport
{
    /// <summary>
    /// The archive against the workspace (CH-17): a fresh export matches, and each change made in the
    /// workspace after the export is caught as exactly the mismatch it is, with nothing private in the
    /// result.
    /// </summary>
    public sealed class ArchiveWorkspaceComparerTests : IDisposable
    {
        private const long Local = MatchingTestWorkspace.LocalParticipantID;
        private const long Other = MatchingTestWorkspace.OtherParticipantID;

        private const string ExportSha = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
        private const string AttachmentBatch = "synthetic-batch";

        private readonly ArchiveExportFixture _fixture = new();

        public void Dispose() => _fixture.Dispose();

        private sealed record Scenario(
            long Single,
            long SharedFirst,
            long Missing,
            long Unmatched,
            long LowQualityCopy,
            long SinglePayloadAsset,
            long RunID,
            long UnknownText,
            string Output);

        /// <summary>
        /// Three restored attachments (two sharing a payload), one reviewed missing, two unmatched, a
        /// system message, msgstore facts with a low-quality link, and text messages with and without a
        /// forwarded fact; then one export of it.
        /// </summary>
        private Scenario BuildAndExport()
        {
            var forwardedText = _fixture.AddText("Good morning");
            var single = _fixture.AddPhoto(Other);
            _fixture.AddText("Synthetic system notice", sender: null);
            var sharedFirst = _fixture.AddPhoto(Local);
            var sharedSecond = _fixture.AddPhoto(Other);
            var missing = _fixture.AddPhoto(Local);
            var unmatched = _fixture.AddPhoto(Other);
            var lowQualityCopy = _fixture.AddSecondAttachment(unmatched);
            var unknownText = _fixture.AddText("No recorded fact", Local, Day.AddDays(1));

            var singlePayload = _fixture.AddPayload("alpha payload", ("photo-alpha.jpg", Copy.Good));
            var sharedPayload = _fixture.AddPayload("beta payload", ("Shared-First.JPG", Copy.Good));

            _fixture.Decisions.Evidence.AddTokenAsset(Day, isSent: true, 1);
            _fixture.SetImportSource(ExportSha, "Synthetic/Zone");

            var run = _fixture.RunEvidence();
            _fixture.Review(missing, run);

            _fixture.Confirm(single, singlePayload.AssetID);
            _fixture.Confirm(sharedFirst, sharedPayload.AssetID);
            _fixture.Confirm(sharedSecond, sharedPayload.AssetID, actor: null);

            _fixture.Facts(
                new MsgstoreFactRow(single, true, 4, null),
                new MsgstoreFactRow(missing, false, null, null),
                new MsgstoreFactRow(lowQualityCopy, false, null, single));

            _fixture.MessageFacts(new MessageMsgstoreFactRow(forwardedText, true, 2));

            var output = _fixture.Output();
            new ArchiveExporter().Export(_fixture.Request(output, 3, 1));

            return new Scenario(
                single, sharedFirst, missing, unmatched, lowQualityCopy, singlePayload.AssetID, run, unknownText, output);
        }

        private ArchiveComparisonResult Compare(string archive, int? pageSize = null)
        {
            var comparer = pageSize is { } size ? new ArchiveWorkspaceComparer { PageSize = size } : new ArchiveWorkspaceComparer();

            return comparer.Compare(new ArchiveComparisonRequest
            {
                WorkspacePath = _fixture.DatabasePath,
                ConversationID = MatchingTestWorkspace.ConversationID,
                LocalParticipantID = Local,
                ArchivePath = archive,
            });
        }

        /// <summary>The sequence number of the message carrying an attachment.</summary>
        private long SequenceOf(long attachmentID) =>
            long.Parse(
                _fixture.ScalarText(
                    $"""
                    SELECT CAST(m.SequenceNumber AS TEXT) FROM Message AS m
                    INNER JOIN Attachment AS a ON a.MessageID = m.MessageID WHERE a.AttachmentID = {attachmentID};
                    """),
                System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Exactly these kinds differ, with these counts; every other kind matches.</summary>
        private static void AssertOnly(ArchiveComparisonResult result, params (ArchiveMismatchKind Kind, int Count)[] expected)
        {
            Assert.False(result.Passed);

            foreach (var kind in Enum.GetValues<ArchiveMismatchKind>())
            {
                var count = expected.Where(e => e.Kind == kind).Select(e => e.Count).SingleOrDefault();
                Assert.True(count == result.MismatchCounts[kind], $"{kind}: expected {count}, got {result.MismatchCounts[kind]}.");
            }
        }

        // ---------------------------------------------------------------------------------------
        // A fresh export.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AFreshExport_MatchesTheWorkspace_AndPassesItsSelfCheck()
        {
            var scenario = BuildAndExport();

            // Two messages a page, so the workspace is read across several pages.
            var result = Compare(scenario.Output, pageSize: 2);

            Assert.True(result.Passed);
            Assert.Empty(result.FirstMismatches);
            Assert.Equal(Enum.GetValues<ArchiveMismatchKind>().Length, result.MismatchCounts.Count);
            Assert.All(result.MismatchCounts.Values, count => Assert.Equal(0, count));

            Assert.Equal(2, result.ParticipantsCompared);
            Assert.Equal(8, result.MessagesCompared);
            Assert.Equal(6, result.AttachmentsCompared);
            Assert.Equal((3, 1, 2), (result.Restored, result.Missing, result.Unmatched));

            Assert.True(ArchiveValidator.Validate(scenario.Output).Passed);
        }

        [Fact]
        public void TheComparison_LeavesTheWorkspaceAndTheArchiveUnchanged()
        {
            var scenario = BuildAndExport();
            var workspace = _fixture.WorkspaceFingerprint();
            var archive = ArchiveContents.Build(scenario.Output);

            Compare(scenario.Output);

            Assert.Equal(workspace, _fixture.WorkspaceFingerprint());
            Assert.Equal(archive, ArchiveContents.Build(scenario.Output));
            Assert.True(ArchiveContents.Check(scenario.Output).Ok);
        }

        // ---------------------------------------------------------------------------------------
        // The workspace changed after the export.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ANewConfirmation_IsAStateMediaAndBasisDifference()
        {
            var scenario = BuildAndExport();

            _fixture.Confirm(scenario.Unmatched, scenario.SinglePayloadAsset);

            var result = Compare(scenario.Output);

            AssertOnly(
                result,
                (ArchiveMismatchKind.AttachmentStateDiffers, 1),
                (ArchiveMismatchKind.AttachmentMediaDiffers, 1),
                (ArchiveMismatchKind.AttachmentBasisDiffers, 1));

            Assert.Contains(
                new ArchiveMismatch(ArchiveMismatchKind.AttachmentStateDiffers, (int)SequenceOf(scenario.Unmatched), scenario.Unmatched),
                result.FirstMismatches);

            Assert.Equal(4, result.Restored);
        }

        [Fact]
        public void ARevoke_IsAStateMediaAndBasisDifference_AndAPayloadNoLongerUsed()
        {
            var scenario = BuildAndExport();

            var confirmation = _fixture.Decisions.History(scenario.Single).Single();
            _fixture.Decisions.Revoke(scenario.Single, confirmation.AttachmentResolutionEventID);

            var result = Compare(scenario.Output);

            // The single payload was this attachment's alone, so the workspace no longer names it.
            AssertOnly(
                result,
                (ArchiveMismatchKind.AttachmentStateDiffers, 1),
                (ArchiveMismatchKind.AttachmentMediaDiffers, 1),
                (ArchiveMismatchKind.AttachmentBasisDiffers, 1),
                (ArchiveMismatchKind.MediaDetailsDiffer, 1));

            Assert.Equal((2, 1, 3), (result.Restored, result.Missing, result.Unmatched));
        }

        [Fact]
        public void ANewReview_IsAStateDifference()
        {
            var scenario = BuildAndExport();

            _fixture.Review(scenario.Unmatched, scenario.RunID);

            var result = Compare(scenario.Output);

            AssertOnly(result, (ArchiveMismatchKind.AttachmentStateDiffers, 1));
            Assert.Equal(2, result.Missing);
        }

        [Fact]
        public void AChangedAttachmentForwardedFact_IsAForwardedDifference()
        {
            var scenario = BuildAndExport();

            MsgstoreFactWriter.DeleteBatch(_fixture.DatabasePath, AttachmentBatch);
            _fixture.Facts(
                new MsgstoreFactRow(scenario.Single, false, null, null),
                new MsgstoreFactRow(scenario.Missing, false, null, null),
                new MsgstoreFactRow(scenario.LowQualityCopy, false, null, scenario.Single));

            var result = Compare(scenario.Output);

            AssertOnly(result, (ArchiveMismatchKind.AttachmentForwardedDiffers, 1));
            Assert.Equal(
                [new ArchiveMismatch(ArchiveMismatchKind.AttachmentForwardedDiffers, (int)SequenceOf(scenario.Single), scenario.Single)],
                result.FirstMismatches);
        }

        [Fact]
        public void AChangedLowQualityLink_IsALinkDifference()
        {
            var scenario = BuildAndExport();

            MsgstoreFactWriter.DeleteBatch(_fixture.DatabasePath, AttachmentBatch);
            _fixture.Facts(
                new MsgstoreFactRow(scenario.Single, true, 4, null),
                new MsgstoreFactRow(scenario.Missing, false, null, null),
                new MsgstoreFactRow(scenario.LowQualityCopy, false, null, scenario.SharedFirst));

            AssertOnly(Compare(scenario.Output), (ArchiveMismatchKind.AttachmentLowQualityLinkDiffers, 1));
        }

        [Fact]
        public void ANewMessageForwardedFact_IsAMessageForwardedDifference()
        {
            var scenario = BuildAndExport();

            MessageMsgstoreFactWriter.InsertBatch(
                _fixture.DatabasePath,
                "synthetic-later-batch",
                [new MessageMsgstoreFactRow(scenario.UnknownText, true, 1)],
                new DateTime(2026, 10, 21, 8, 0, 0, DateTimeKind.Utc));

            AssertOnly(Compare(scenario.Output), (ArchiveMismatchKind.MessageForwardedDiffers, 1));
        }

        [Fact]
        public void AnEditedMessageText_IsATextDifference()
        {
            var scenario = BuildAndExport();

            _fixture.Decisions.ExecuteDirect(
                $"UPDATE Message SET MessageContent = 'Edited later' WHERE MessageID = {scenario.UnknownText};");

            AssertOnly(Compare(scenario.Output), (ArchiveMismatchKind.MessageTextDiffers, 1));
        }

        [Fact]
        public void ManyDifferences_AreAllCounted_ButOnlyTheFirstFewListed()
        {
            var scenario = BuildAndExport();

            _fixture.Decisions.ExecuteDirect(
                """
                UPDATE Message SET MessageContent = MessageContent || ' edited',
                                   MessageDateTimeLocal = replace(MessageDateTimeLocal, ':00', ':01');
                """);

            var result = Compare(scenario.Output);

            AssertOnly(result, (ArchiveMismatchKind.MessageTextDiffers, 8), (ArchiveMismatchKind.MessageTimeDiffers, 8));
            Assert.Equal(ArchiveWorkspaceComparer.MaximumListedMismatches, result.FirstMismatches.Count);
        }

        [Fact]
        public void TheWrongConversation_IsCaughtAsMessagesOnBothSides()
        {
            var scenario = BuildAndExport();

            // The other conversation has no messages, and its only participant is the outsider.
            var result = new ArchiveWorkspaceComparer().Compare(new ArchiveComparisonRequest
            {
                WorkspacePath = _fixture.DatabasePath,
                ConversationID = MatchingTestWorkspace.OtherConversationID,
                LocalParticipantID = MatchingTestWorkspace.OutsiderParticipantID,
                ArchivePath = scenario.Output,
            });

            Assert.False(result.Passed);
            Assert.Equal(8, result.MismatchCounts[ArchiveMismatchKind.MessageNotInWorkspace]);
            Assert.Equal(6, result.MismatchCounts[ArchiveMismatchKind.AttachmentNotInWorkspace]);
            Assert.Equal(0, result.MessagesCompared);
        }

        // ---------------------------------------------------------------------------------------
        // Refusals and privacy.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AMissingWorkspaceOrArchive_IsRefused_WithoutNamingThePath()
        {
            var scenario = BuildAndExport();
            var absent = Path.Combine(_fixture.ExportsFolder, "absent");

            var noWorkspace = Assert.Throws<ArchiveComparisonRefusedException>(() =>
                new ArchiveWorkspaceComparer().Compare(new ArchiveComparisonRequest
                {
                    WorkspacePath = absent + ".db",
                    ConversationID = MatchingTestWorkspace.ConversationID,
                    LocalParticipantID = Local,
                    ArchivePath = scenario.Output,
                }));

            var noArchive = Assert.Throws<ArchiveComparisonRefusedException>(() => Compare(absent));

            foreach (var message in new[] { noWorkspace.Message, noArchive.Message })
            {
                Assert.DoesNotContain(_fixture.ExportsFolder, message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("absent", message, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void TheResult_CarriesNoTextNameFileNameOrPath()
        {
            var scenario = BuildAndExport();

            _fixture.Confirm(scenario.Unmatched, scenario.SinglePayloadAsset, "Synthetic later basis line");
            _fixture.Decisions.ExecuteDirect(
                $"UPDATE Message SET MessageContent = 'Edited later' WHERE MessageID = {scenario.UnknownText};");
            _fixture.Decisions.ExecuteDirect(
                $"UPDATE Participant SET DisplayName = 'Alex Renamed' WHERE ParticipantID = {Local};");

            var result = Compare(scenario.Output);

            Assert.False(result.Passed);
            Assert.Equal(1, result.MismatchCounts[ArchiveMismatchKind.ParticipantDiffers]);

            var text = string.Join(
                "\n",
                result.FirstMismatches.Select(m => m.ToString())
                    .Concat(result.MismatchCounts.Select(p => p.ToString()))
                    .Append($"{result.ParticipantsCompared} {result.MessagesCompared} {result.AttachmentsCompared}"));

            foreach (var secret in new[]
                     {
                         "Alex", "Sam", "Good morning", "No recorded fact", "Edited later", "Synthetic later basis",
                         Phase7DecisionFixture.HumanBasis, "photo-alpha", "Shared-First", "Reviewer", _fixture.MediaRoot,
                         _fixture.ExportsFolder, scenario.Output,
                     })
            {
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            }
        }
    }
}
