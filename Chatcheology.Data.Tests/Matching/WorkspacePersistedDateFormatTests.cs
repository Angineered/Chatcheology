using Chatcheology.Data.Media;
using Chatcheology.Data.Tests.Media;
using Chatcheology.Data.Tests.Workspace;
using Chatcheology.Data.Workspace;
using static Chatcheology.Data.Tests.Matching.MatchingTestData;
using static Chatcheology.Data.Tests.Workspace.WorkspaceTestData;

namespace Chatcheology.Data.Tests.Matching
{
    /// <summary>
    /// Tests that the two persisted date formats are unchanged now that they have one owner, and
    /// that matching reads them back exactly.
    /// </summary>
    /// <remarks>
    /// Phase 6 is the first code to read either value back, so both formats moved to a single
    /// shared constant rather than being spelled out a second time in a reader. These tests exist to
    /// prove the move changed nothing: the workspace still writes exactly what it wrote before, and
    /// a workspace written by an earlier build still reads correctly.
    /// <para>
    /// The expectations are written as literal text rather than by formatting a date through the
    /// same constant the code uses, which would agree with itself whatever it held.
    /// </para>
    /// </remarks>
    public class WorkspacePersistedDateFormatTests
    {
        [Fact]
        public void ImportedMessageTimestamp_IsStillStoredInTheDocumentedFormat()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            new WorkspaceImporter().Import(
                workspace.DatabasePath,
                CreateRequest(
                    [CreateUserMessage(1, messageDateTime: new DateTime(2026, 1, 5, 14, 3, 0))]));

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(
                "2026-01-05T14:03:00",
                ScalarRequiredText(connection, "SELECT MessageDateTimeLocal FROM Message;"));
        }

        [Fact]
        public void InventoriedFileDate_IsStillStoredInTheDocumentedFormat()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("WhatsApp Images/IMG-20260105-WA0001.jpg", "one");

            new MediaInventoryService().Inventory(
                MediaTestData.CreateWorkspace(workspace),
                MediaTestData.CreateRequest(media.RootPath));

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(
                "2026-01-05",
                ScalarRequiredText(connection, "SELECT FileDate FROM MediaFile;"));
        }

        /// <remarks>
        /// Both stored spellings, read back by the analysis, producing a candidate on the day the
        /// message was sent. This is the round trip the shared constants exist to guarantee.
        /// </remarks>
        [Fact]
        public void MatchingReadsBothStoredFormatsExactly()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(new DateOnly(2026, 1, 5));

            var sourceID = workspace.AddMediaSource();

            workspace.Execute(
                $"""
                INSERT INTO MediaAsset (SHA256, MediaType, SizeBytes)
                VALUES ('{Hash(1)}', 'Image', 1024);

                INSERT INTO MediaFile (
                    MediaSourceID, RelativePath, FileName, Extension, SizeBytes, SHA256,
                    MediaType, FileDate, IsSent)
                VALUES (
                    {sourceID}, 'folder/one.jpg', 'one.jpg', '.jpg', 1024, '{Hash(1)}',
                    'Image', '2026-01-05', NULL);

                INSERT INTO MediaAssetFile (MediaAssetID, MediaFileID)
                VALUES (
                    (SELECT MediaAssetID FROM MediaAsset),
                    (SELECT MediaFileID FROM MediaFile));
                """);

            var analysis = AnalyseOne(workspace);

            Assert.Equal(new DateOnly(2026, 1, 5), analysis.MessageDate);
            Assert.Equal(new DateTime(2026, 1, 5, 14, 3, 0), analysis.MessageDateTimeLocal);
            Assert.Single(analysis.ExactDateCandidates);
        }
    }
}
