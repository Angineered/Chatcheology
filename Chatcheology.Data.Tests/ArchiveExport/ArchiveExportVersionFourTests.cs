using System.Text;
using Chatcheology.Archive;
using Chatcheology.Data.ArchiveExport;
using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Tests.Matching;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.ArchiveExport.ArchiveExportFixture;

namespace Chatcheology.Data.Tests.ArchiveExport
{
    /// <summary>
    /// Archive format version 4 from the exporter (CH-71 step 1b): each workspace attachment's caption from
    /// <c>AttachmentCaptionFact</c>, and the workspace's fixed archive id from the file beside it, made once and
    /// reused, refused when damaged; and what the comparer says about both.
    /// </summary>
    /// <remarks>Invented text, ids and bytes only.</remarks>
    public sealed class ArchiveExportVersionFourTests : IDisposable
    {
        private const long Local = MatchingTestWorkspace.LocalParticipantID;
        private const long Other = MatchingTestWorkspace.OtherParticipantID;

        private readonly ArchiveExportFixture _fixture = new();

        public void Dispose() => _fixture.Dispose();

        private sealed record Scenario(long Restored, long Missing, long Unmatched, long Plain);

        /// <summary>A restored, a reviewed-missing, an unmatched and a plain unmatched picture; captions on the first three.</summary>
        private Scenario Build(bool captions = true)
        {
            var restored = _fixture.AddPhoto(Other);
            var missing = _fixture.AddPhoto(Local);
            var unmatched = _fixture.AddPhoto(Other);
            var plain = _fixture.AddPhoto(Local);

            var payload = _fixture.AddPayload("invented caption payload", ("caption-photo.jpg", Copy.Good));

            var run = _fixture.RunEvidence();
            _fixture.Review(missing, run);
            _fixture.Confirm(restored, payload.AssetID);

            if (captions)
            {
                _fixture.Captions(
                    "synthetic-caption-batch",
                    new AttachmentCaptionFactRow(restored, "Invented caption with www.example.org/three", false),
                    new AttachmentCaptionFactRow(missing, "Invented caption\non two lines", true),
                    new AttachmentCaptionFactRow(unmatched, " invented, spaces kept ", false));
            }

            return new Scenario(restored, missing, unmatched, plain);
        }

        private ArchiveExportResult Export(string output) =>
            new ArchiveExporter().Export(_fixture.Request(output, 1, 1));

        private ArchiveComparisonResult Compare(string archive) =>
            new ArchiveWorkspaceComparer().Compare(new ArchiveComparisonRequest
            {
                WorkspacePath = _fixture.DatabasePath,
                ConversationID = MatchingTestWorkspace.ConversationID,
                LocalParticipantID = Local,
                ArchivePath = archive,
            });

        [Fact]
        public void Captions_AreExportedAsRecorded_AndTheArchiveValidatesAndCompares()
        {
            var scenario = Build();
            var output = _fixture.Output();

            var result = Export(output);

            Assert.Equal(3, result.Counts.Captions);
            Assert.Equal(0, result.Counts.MsgstoreMessages);

            var validation = ArchiveValidator.Validate(output);
            Assert.True(validation.Passed, string.Join(", ", validation.FirstProblems));

            using (var reader = ArchiveReader.Open(output))
            {
                Assert.Equal(ArchiveFormat.FormatVersion, reader.Info.FormatVersion);

                var attachments = reader.ReadAllPages().SelectMany(p => p.Messages).SelectMany(m => m.Attachments).ToDictionary(a => a.AttachmentID);

                Assert.Equal("Invented caption with www.example.org/three", attachments[scenario.Restored].Caption);
                Assert.Equal(ArchiveAttachmentState.Restored, attachments[scenario.Restored].State);
                Assert.Equal("Invented caption\non two lines", attachments[scenario.Missing].Caption);
                Assert.Equal(ArchiveAttachmentState.Missing, attachments[scenario.Missing].State);
                Assert.Equal(" invented, spaces kept ", attachments[scenario.Unmatched].Caption);
                Assert.Null(attachments[scenario.Plain].Caption);
            }

            var comparison = Compare(output);
            Assert.True(comparison.Passed, string.Join(", ", comparison.FirstMismatches));
            Assert.Equal(3, comparison.Captions);
        }

        [Fact]
        public void AWorkspaceWithoutCaptions_ExportsNone()
        {
            Build(captions: false);
            var output = _fixture.Output();

            Assert.Equal(0, Export(output).Counts.Captions);
            Assert.True(ArchiveValidator.Validate(output).Passed);
            Assert.True(Compare(output).Passed);
        }

        [Fact]
        public void TheArchiveId_IsMadeOnTheFirstExport_AndReusedForEveryLaterOne()
        {
            Build();
            Assert.False(File.Exists(_fixture.ArchiveIdPath));

            var first = Export(_fixture.Output("first"));

            Assert.True(ArchiveFormat.IsArchiveId(first.ArchiveId));
            Assert.True(File.Exists(_fixture.ArchiveIdPath));

            var stored = File.ReadAllText(_fixture.ArchiveIdPath);
            Assert.Contains("\"format\": \"chatcheology-archive-id\"", stored, StringComparison.Ordinal);
            Assert.Contains($"\"conversationID\": {MatchingTestWorkspace.ConversationID}", stored, StringComparison.Ordinal);
            Assert.Contains(first.ArchiveId, stored, StringComparison.Ordinal);

            var bytes = File.ReadAllBytes(_fixture.ArchiveIdPath);
            var second = Export(_fixture.Output("second"));

            Assert.Equal(first.ArchiveId, second.ArchiveId);
            Assert.Equal(bytes, File.ReadAllBytes(_fixture.ArchiveIdPath));

            using (var one = ArchiveReader.Open(_fixture.Output("first")))
            using (var two = ArchiveReader.Open(_fixture.Output("second")))
            {
                Assert.Equal(first.ArchiveId, one.Info.ArchiveId);
                Assert.Equal(first.ArchiveId, two.Info.ArchiveId);
            }

            Assert.True(Compare(_fixture.Output("first")).Passed);
            Assert.True(Compare(_fixture.Output("second")).Passed);
        }

        [Fact]
        public void ThePreviewAndTheComparer_NeverCreateTheIdFile()
        {
            Build();

            new ArchiveExporter().Preview(_fixture.DatabasePath, MatchingTestWorkspace.ConversationID, Local);
            Assert.False(File.Exists(_fixture.ArchiveIdPath));

            var output = _fixture.Output();
            Export(output);
            File.Delete(_fixture.ArchiveIdPath);

            // The archive's id is now one the workspace no longer records: a difference, and still no file.
            var comparison = Compare(output);

            Assert.Equal(1, comparison.MismatchCounts[ArchiveMismatchKind.ArchiveIdDiffers]);
            Assert.Equal(1, comparison.MismatchCounts.Values.Sum());
            Assert.False(File.Exists(_fixture.ArchiveIdPath));
        }

        public static TheoryData<string> DamagedFiles => new()
        {
            "",
            "not json at all",
            "[]",
            "{}",
            "{\"format\": \"chatcheology-archive-id\", \"conversationID\": 1}",
            "{\"format\": \"chatcheology-archive-id\", \"conversationID\": 1, \"archiveId\": \"not-a-guid\"}",
            "{\"format\": \"chatcheology-archive-id\", \"conversationID\": 1, \"archiveId\": \"6B2E9F10-4C7A-4D3B-8E21-5F0A9C3D7E64\"}",
            "{\"format\": \"chatcheology-archive-id\", \"conversationID\": 1, \"archiveId\": \"00000000-0000-0000-0000-000000000000\"}",
            "{\"format\": \"something-else\", \"conversationID\": 1, \"archiveId\": \"6b2e9f10-4c7a-4d3b-8e21-5f0a9c3d7e64\"}",
            "{\"format\": \"chatcheology-archive-id\", \"conversationID\": \"1\", \"archiveId\": \"6b2e9f10-4c7a-4d3b-8e21-5f0a9c3d7e64\"}",
            "{\"format\": \"chatcheology-archive-id\", \"conversationID\": 1, \"archiveId\": \"6b2e9f10-4c7a-4d3b-8e21-5f0a9c3d7e64\", \"extra\": 1}",
            "{\"format\": \"chatcheology-archive-id\", \"conversationID\": 1, \"archiveId\": \"6b2e9f10-4c7a-4d3b-8e21-5f0a9c3d7e64\", \"archiveId\": \"6b2e9f10-4c7a-4d3b-8e21-5f0a9c3d7e65\"}",
        };

        [Theory]
        [MemberData(nameof(DamagedFiles))]
        public void ADamagedIdFile_RefusesTheExport_WritesNothing_AndIsLeftAsFound(string content)
        {
            Build();
            File.WriteAllText(_fixture.ArchiveIdPath, content, new UTF8Encoding(false));
            var before = File.ReadAllBytes(_fixture.ArchiveIdPath);

            var refusal = Assert.Throws<ArchiveExportRefusedException>(() => Export(_fixture.Output()));

            Assert.Contains("archive id file", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("damaged", refusal.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_fixture.ArchiveIdPath, refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(_fixture.ExportsContent());
            Assert.Equal(before, File.ReadAllBytes(_fixture.ArchiveIdPath));

            // The comparer refuses the same file rather than comparing against a guess.
            Assert.Throws<ArchiveComparisonRefusedException>(() => Compare(_fixture.Output()));
        }

        [Fact]
        public void AnIdFileOfAnotherConversation_RefusesTheExport()
        {
            Build();
            File.WriteAllBytes(_fixture.ArchiveIdPath, ArchiveIdFile.Bytes(MatchingTestWorkspace.ConversationID + 1, ArchiveFormat.NewArchiveId()));

            var refusal = Assert.Throws<ArchiveExportRefusedException>(() => Export(_fixture.Output()));

            Assert.Contains("another conversation", refusal.Message, StringComparison.Ordinal);
            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void AnIdFileWrittenByHand_IsReadAsItsId()
        {
            Build();
            const string Chosen = "6b2e9f10-4c7a-4d3b-8e21-5f0a9c3d7e64";

            File.WriteAllText(
                _fixture.ArchiveIdPath,
                $"{{ \"archiveId\": \"{Chosen}\", \"conversationID\": {MatchingTestWorkspace.ConversationID}, \"format\": \"chatcheology-archive-id\" }}");

            Assert.Equal(Chosen, Export(_fixture.Output()).ArchiveId);
        }

        [Fact]
        public void TheComparer_CatchesACaptionChangedAfterTheExport()
        {
            var scenario = Build();
            var output = _fixture.Output();
            Export(output);

            // The batch is undone and recorded again with one caption different and one left out.
            SqliteConnection.ClearAllPools();
            AttachmentCaptionFactWriter.DeleteBatch(_fixture.DatabasePath, "synthetic-caption-batch");
            _fixture.Captions(
                "synthetic-caption-batch-2",
                new AttachmentCaptionFactRow(scenario.Restored, "Invented caption, changed", false),
                new AttachmentCaptionFactRow(scenario.Missing, "Invented caption\non two lines", true));

            var comparison = Compare(output);

            Assert.Equal(2, comparison.MismatchCounts[ArchiveMismatchKind.AttachmentCaptionDiffers]);
            Assert.Equal(2, comparison.MismatchCounts.Values.Sum());
            Assert.Contains(comparison.FirstMismatches, m => m.AttachmentID == scenario.Restored);
            Assert.Contains(comparison.FirstMismatches, m => m.AttachmentID == scenario.Unmatched);
        }

        [Fact]
        public void TheComparer_CatchesAnArchiveOfADifferentId()
        {
            Build();
            var output = _fixture.Output();
            Export(output);

            File.WriteAllBytes(_fixture.ArchiveIdPath, ArchiveIdFile.Bytes(MatchingTestWorkspace.ConversationID, ArchiveFormat.NewArchiveId()));

            var comparison = Compare(output);

            Assert.Equal(1, comparison.MismatchCounts[ArchiveMismatchKind.ArchiveIdDiffers]);
            Assert.Equal(1, comparison.MismatchCounts.Values.Sum());
        }
    }
}
