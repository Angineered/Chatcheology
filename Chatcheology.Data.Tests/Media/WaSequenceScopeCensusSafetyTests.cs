using System.Security.Cryptography;
using Chatcheology.Data.Media;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Media
{
    /// <summary>
    /// Tests what the sequence scope census refuses to run against, and that it changes nothing.
    /// </summary>
    /// <remarks>
    /// Every refusal here aborts rather than degrading. A census that described a half-hashed workspace,
    /// or grouped by a date its own names do not encode, would be worse than no census at all — so none
    /// is returned, not a partial one and not one marked untrusted.
    /// </remarks>
    public class WaSequenceScopeCensusSafetyTests
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

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(1, census.Reconciliation.SupportedFileCount);
        }

        [Fact]
        public void UnhashedMediaFile_IsRefusedAsIncompleteHashing()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            var asset = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(
                sourceID, asset, HashA, "IMG-20260724-WA0004.jpg", storedSHA256: string.Empty);

            var exception = Assert.Throws<InvalidOperationException>(
                () => WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1)));

            Assert.Contains("media hashing is incomplete", exception.Message);
        }

        [Fact]
        public void HashedFileWithNoAssetLink_IsRefused()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            var asset = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(sourceID, asset, HashA, "IMG-20260724-WA0004.jpg", link: false);

            var exception = Assert.Throws<InvalidOperationException>(
                () => WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1)));

            Assert.Contains("linked to no MediaAsset", exception.Message);
        }

        [Fact]
        public void FileAndAssetRecordingDifferentHashes_IsRefused()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            var asset = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(
                sourceID, asset, HashA, "IMG-20260724-WA0004.jpg", storedSHA256: HashB);

            var exception = Assert.Throws<InvalidOperationException>(
                () => WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1)));

            Assert.Contains("record different", exception.Message);
        }

        [Fact]
        public void HashesDifferingOnlyByLetterCase_AreTheSameHash()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            var asset = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(
                sourceID, asset, HashA, "IMG-20260724-WA0004.jpg",
                storedSHA256: HashA.ToLowerInvariant());

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(1, census.Reconciliation.SupportedFileCount);
        }

        /// <remarks>
        /// Schema v2 makes a second asset link for one file unrepresentable, so this shape can only be
        /// built by rebuilding the table without its constraint. The census still has to refuse it rather
        /// than count one physical file as two, which would inflate every collision figure it produces.
        /// </remarks>
        [Fact]
        public void AFileCarryingTwoAssetLinks_IsRefusedRatherThanCountedTwice()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            var first = workspace.AddMediaAsset(HashA);
            var second = workspace.AddMediaAsset(HashB);
            var fileID = workspace.AddMediaFile(sourceID, first, HashA, "IMG-20260724-WA0004.jpg");

            workspace.RemoveAssetLinkUniqueConstraint();
            workspace.Execute(
                $"""
                INSERT INTO MediaAssetFile (MediaAssetID, MediaFileID)
                VALUES ({second}, {fileID});
                """);

            var exception = Assert.Throws<InvalidOperationException>(
                () => WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1)));

            Assert.Contains("more than one MediaAsset", exception.Message);
        }

        // ---------------------------------------------------------------------------------------
        // Dates and markers must agree.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The grouping key of the whole census is the naming-derived date, so a workspace where the
        /// stored date is not the date the name encodes cannot be grouped by it. This is the equivalence
        /// check Stage A did not have: Stage A proved only that a marker was locatable.
        /// </remarks>
        [Fact]
        public void AStoredDateThatIsNotTheNamesOwnDate_IsRefused()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            var asset = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(
                sourceID, asset, HashA, "IMG-20260724-WA0004.jpg", fileDate: "2020-01-01");

            var exception = Assert.Throws<InvalidOperationException>(
                () => WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1)));

            Assert.Contains("not the date its own name encodes", exception.Message);
        }

        [Fact]
        public void APersistedDateInAFormatTheWorkspaceDoesNotWrite_IsRefused()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            var asset = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(
                sourceID, asset, HashA, "IMG-20260724-WA0004.jpg", fileDate: "24/07/2026");

            var exception = Assert.Throws<InvalidOperationException>(
                () => WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1)));

            Assert.Contains("could not have produced", exception.Message);
        }

        /// <remarks>
        /// A name the committed classifier dates, stored with no date at all, means the two disagree about
        /// the same characters. Refused only for a source the classifier reads dates from, because for a
        /// source of another type a null date is what the classifier legitimately returns.
        /// </remarks>
        [Fact]
        public void AWhatsAppNameStoredWithNoDate_IsRefused()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            var asset = workspace.AddMediaAsset(HashA);
            workspace.AddMediaFile(
                sourceID, asset, HashA, "IMG-20260724-WA0004.jpg", fileDate: null);

            var exception = Assert.Throws<InvalidOperationException>(
                () => WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1)));

            Assert.Contains("locatable", exception.Message);
        }

        [Fact]
        public void AWhatsAppNameInASourceOfAnotherType_IsUndatedWithoutBeingRefused()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource(sourceType: "UnknownLayout");
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");

            var census = WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(1, census.Reconciliation.UndatedFileCount);
            Assert.Equal(0, census.Reconciliation.DatedFileCount);
        }

        // ---------------------------------------------------------------------------------------
        // The workspace itself.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AWorkspaceThatIsNotAtTheCurrentSchemaVersion_IsRefused()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithFile(sourceID, HashA, "IMG-20260724-WA0004.jpg");
            workspace.Execute("PRAGMA user_version = 1;");

            Assert.Throws<InvalidOperationException>(
                () => WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1)));
        }

        [Fact]
        public void AWorkspaceThatDoesNotExist_IsRefusedWithoutCreatingOne()
        {
            var directory = Directory.CreateTempSubdirectory("ChatcheologySequenceScope");

            try
            {
                var path = Path.Combine(directory.FullName, "absent.db");

                Assert.Throws<FileNotFoundException>(
                    () => new WaSequenceScopeCensusService().Analyse(
                        new WaSequenceScopeCensusRequest
                        {
                            DatabasePath = path,
                            DeviceGroups = [new DeviceGroupAssignment
                            {
                                MediaSourceID = 1, DeviceGroupID = 1,
                            }],
                        }));

                Assert.False(File.Exists(path));
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }

        [Fact]
        public void ARequestNamingNoWorkspace_IsRefused() =>
            Assert.Throws<ArgumentException>(
                () => new WaSequenceScopeCensusService().Analyse(new WaSequenceScopeCensusRequest
                {
                    DatabasePath = "   ",
                    DeviceGroups = [new DeviceGroupAssignment
                    {
                        MediaSourceID = 1, DeviceGroupID = 1,
                    }],
                }));

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

            WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

            Assert.Equal(before, DescribeWorkspaceState(workspace));
            Assert.Equal(2, workspace.ScalarLongReadOnly("PRAGMA user_version;"));
        }

        /// <remarks>
        /// The pool is cleared before each hash because a disposed read-only connection can still hold the
        /// file open, and a hash taken through a live handle would prove less than it appears to.
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

            WaSequenceScopeTestRunner.Analyse(workspace, (sourceID, 1));

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
                () => WaSequenceScopeTestRunner.Analyse(
                    workspace, cancellation.Token, (sourceID, 1)));
        }

        /// <remarks>
        /// The empty-workspace case is the one a per-row check alone would miss: with nothing to iterate, a
        /// census could otherwise be returned from a call that was cancelled before it began.
        /// </remarks>
        [Fact]
        public void CancellationOnAWorkspaceWithNoMedia_StillThrows()
        {
            using var workspace = new NameCensusTestWorkspace();
            var sourceID = workspace.AddMediaSource();

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => WaSequenceScopeTestRunner.Analyse(
                    workspace, cancellation.Token, (sourceID, 1)));
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
                    () => WaSequenceScopeTestRunner.Analyse(
                        workspace, cancellation.Token, (sourceID, 1)));

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

        private static string DescribeWorkspaceState(NameCensusTestWorkspace workspace)
        {
            var counts = new[]
            {
                "SELECT COUNT(*) FROM MediaSource;",
                "SELECT COUNT(*) FROM MediaFile;",
                "SELECT COUNT(*) FROM MediaAsset;",
                "SELECT COUNT(*) FROM MediaAssetFile;",
                "SELECT COUNT(*) FROM Attachment;",
                "SELECT COUNT(*) FROM Message;",
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';",
            }.Select(workspace.ScalarLongReadOnly);

            return string.Join(",", counts);
        }

        private static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);

            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
}
