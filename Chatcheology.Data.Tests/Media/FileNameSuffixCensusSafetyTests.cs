using System.Security.Cryptography;
using Chatcheology.Data.Media;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Media
{
    /// <summary>
    /// Tests what the file name census refuses to run against, and that it changes nothing.
    /// </summary>
    /// <remarks>
    /// The census reads names, which is the one boundary Stage A deliberately opens. Everything else
    /// stays where the matching engine left it: the connection is read-only at SQLite's own level,
    /// nothing is written, and a media state that Phase 5 has not finished with is refused rather
    /// than described as though it were complete.
    /// </remarks>
    public class FileNameSuffixCensusSafetyTests
    {
        private const string HashA = "AAAA000000000000000000000000000000000000000000000000000000000001";
        private const string HashB = "BBBB000000000000000000000000000000000000000000000000000000000002";

        // ---------------------------------------------------------------------------------------
        // Completed-Phase-5 validation.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void FullyHashedAndLinkedMedia_IsAccepted()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");

            Assert.Equal(1, Analyse(workspace).MediaFileCount);
        }

        [Fact]
        public void UnhashedMediaFile_IsRefusedAsIncompleteHashing()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            workspace.AddMediaFile(
                sourceID, null, HashA, "IMG-20260724-WA0004.jpg", storedSHA256: "", link: false);

            var error = Assert.Throws<InvalidOperationException>(() => Analyse(workspace));

            Assert.Contains("media hashing is incomplete", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void HashedFileWithNoAssetLink_IsRefused()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            var assetID = workspace.AddMediaAsset(HashA);

            workspace.AddMediaFile(
                sourceID, assetID, HashA, "IMG-20260724-WA0004.jpg", link: false);

            var error = Assert.Throws<InvalidOperationException>(() => Analyse(workspace));

            Assert.Contains("linked to no MediaAsset", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void LinkPointingAtAnAssetThatDoesNotExist_IsRefused()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            var mediaFileID = workspace.AddMediaFile(
                sourceID, null, HashA, "IMG-20260724-WA0004.jpg", link: false);

            workspace.CloseBuildingConnection();

            workspace.ExecuteWithoutForeignKeys(
                $"""
                INSERT INTO MediaAssetFile (MediaAssetID, MediaFileID)
                VALUES (4242, {mediaFileID});
                """);

            var error = Assert.Throws<InvalidOperationException>(() => Analyse(workspace));

            Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void HashesDifferingOnlyByLetterCase_AreTheSameHash()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            var assetID = workspace.AddMediaAsset(HashA);

            workspace.AddMediaFile(
                sourceID,
                assetID,
                HashA,
                "IMG-20260724-WA0004.jpg",
                storedSHA256: HashA.ToLowerInvariant());

            Assert.Equal(1, Analyse(workspace).MediaFileCount);
        }

        [Fact]
        public void FileAndAssetRecordingDifferentHashes_IsRefused()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            var assetID = workspace.AddMediaAsset(HashA);

            workspace.AddMediaFile(
                sourceID, assetID, HashA, "IMG-20260724-WA0004.jpg", storedSHA256: HashB);

            var error = Assert.Throws<InvalidOperationException>(() => Analyse(workspace));

            Assert.Contains("different SHA-256", error.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// Schema v2 makes a second asset link for one file unrepresentable, so this fixture rebuilds
        /// the link table without its unique constraint — a workspace as some other tool might have
        /// written it. The census must refuse it: counting the file once per link would inflate every
        /// figure that follows, quietly and plausibly.
        /// </remarks>
        [Fact]
        public void AFileCarryingTwoAssetLinks_IsRefusedRatherThanCountedTwice()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            var assetID = workspace.AddMediaAsset(HashA);

            var mediaFileID = workspace.AddMediaFile(
                sourceID, assetID, HashA, "IMG-20260724-WA0004.jpg");

            workspace.RemoveAssetLinkUniqueConstraint();

            workspace.Execute(
                $"""
                INSERT INTO MediaAssetFile (MediaAssetID, MediaFileID)
                VALUES ({assetID}, {mediaFileID});
                """);

            var error = Assert.Throws<InvalidOperationException>(() => Analyse(workspace));

            Assert.Contains("more than one MediaAsset", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AWorkspaceThatIsNotAtTheCurrentSchemaVersion_IsRefused()
        {
            using var workspace = new NameCensusTestWorkspace();
            workspace.Execute("PRAGMA user_version = 1;");

            Assert.Throws<InvalidOperationException>(() => Analyse(workspace));
        }

        [Fact]
        public void AWorkspaceThatDoesNotExist_IsRefusedWithoutCreatingOne()
        {
            using var workspace = new NameCensusTestWorkspace();

            var missingPath = Path.Combine(workspace.DirectoryPath, "absent.db");

            Assert.Throws<FileNotFoundException>(
                () => new FileNameSuffixCensusService().Analyse(missingPath));

            Assert.False(File.Exists(missingPath));
        }

        // ---------------------------------------------------------------------------------------
        // Nothing is written.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TheCensus_LeavesRowCountsAndSchemaUnchanged()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");

            var before = DescribeWorkspaceState(workspace);

            Analyse(workspace);

            Assert.Equal(before, DescribeWorkspaceState(workspace));
            Assert.Equal(2, workspace.ScalarLongReadOnly("PRAGMA user_version;"));
        }

        /// <remarks>
        /// The pool is cleared before each hash because a disposed read-only connection can still
        /// hold the file open, and a hash taken through a live handle would prove less than it
        /// appears to.
        /// </remarks>
        [Fact]
        public void TheCensus_LeavesTheDatabaseFileByteIdentical()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");
            workspace.AddAssetWithFile(sourceID, HashB, "holiday photo.png");

            workspace.CloseBuildingConnection();

            var before = HashFile(workspace.DatabasePath);

            Analyse(workspace);

            SqliteConnection.ClearAllPools();

            Assert.Equal(before, HashFile(workspace.DatabasePath));
            Assert.Equal([workspace.DatabasePath], Directory.GetFiles(workspace.DirectoryPath));
        }

        // ---------------------------------------------------------------------------------------
        // Cancellation.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void CancellationBeforeAnyWork_Throws()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => new FileNameSuffixCensusService().Analyse(
                    workspace.DatabasePath, cancellation.Token));
        }

        /// <remarks>
        /// The empty-workspace case is the one a per-row check alone would miss: with nothing to
        /// iterate, a census could otherwise be returned from a call that was cancelled before it
        /// began.
        /// </remarks>
        [Fact]
        public void CancellationOnAWorkspaceWithNoMedia_StillThrows()
        {
            using var workspace = new NameCensusTestWorkspace();

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => new FileNameSuffixCensusService().Analyse(
                    workspace.DatabasePath, cancellation.Token));
        }

        [Fact]
        public void AfterCancellation_TheWorkspaceCanStillBeReopenedAndDeleted()
        {
            var workspace = new NameCensusTestWorkspace();

            try
            {
                var sourceID = workspace.AddMediaSource();
                workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");

                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                Assert.Throws<OperationCanceledException>(
                    () => new FileNameSuffixCensusService().Analyse(
                        workspace.DatabasePath, cancellation.Token));

                Assert.Equal(1, workspace.ScalarLongReadOnly("SELECT COUNT(*) FROM MediaFile;"));

                workspace.CloseBuildingConnection();
            }
            finally
            {
                workspace.Dispose();
            }
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private static FileNameSuffixCensus Analyse(NameCensusTestWorkspace workspace) =>
            new FileNameSuffixCensusService().Analyse(workspace.DatabasePath);

        private static string DescribeWorkspaceState(NameCensusTestWorkspace workspace)
        {
            string[] tables = ["MediaSource", "MediaFile", "MediaAsset", "MediaAssetFile", "Attachment"];

            var counts = tables.Select(
                table => $"{table}={workspace.ScalarLongReadOnly($"SELECT COUNT(*) FROM {table};")}");

            var tableCount = workspace.ScalarLongReadOnly(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';");

            return string.Join("|", [..counts, $"tables={tableCount}"]);
        }

        private static string HashFile(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }
}
