using System.Security.Cryptography;
using System.Text;
using Chatcheology.Data.Media;
using Chatcheology.Data.Tests.Workspace;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Media.MediaTestData;

namespace Chatcheology.Data.Tests.Media
{
    /// <summary>
    /// Tests for streaming SHA-256 hashing, exact-content deduplication, and the guarantees that
    /// make an interrupted run safe to resume.
    /// </summary>
    public class MediaHashingServiceTests
    {
        /// <summary>The SHA-256 of the content the fixtures write, as the workspace stores it.</summary>
        private static string HashOf(string content) =>
            Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(content)));

        [Fact]
        public void HashPendingFiles_HashesEveryPendingFileAndLinksItToAnAsset()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("one.jpg", "alpha");
            media.CreateFile("nested/two.mp4", "beta");

            var databasePath = CreateWorkspace(workspace);

            new MediaInventoryService().Inventory(databasePath, CreateRequest(media.RootPath));

            var result = new MediaHashingService().HashPendingFiles(databasePath);

            Assert.Equal(2, result.PendingAtStart);
            Assert.Equal(2, result.SuccessfullyHashed);
            Assert.Equal(2, result.NewAssets);
            Assert.Equal(0, result.ExistingAssetLinks);
            Assert.Equal(0, result.FailedFiles);
            Assert.Equal(0, result.ClassificationConflicts);
            Assert.Equal(0, result.RemainingUnhashed);
            Assert.Equal(9, result.PhysicalBytesHashed);
            Assert.False(result.WasCancelled);

            using var connection = WorkspaceDatabase.OpenConnection(databasePath);

            Assert.Equal(2, CountRows(connection, "MediaAsset"));
            Assert.Equal(2, CountRows(connection, "MediaAssetFile"));

            var files = ReadMediaFilesByPath(connection);

            Assert.Equal(HashOf("alpha"), files["one.jpg"].SHA256);
            Assert.Equal(HashOf("beta"), files["nested/two.mp4"].SHA256);

            // Upper-case 64-character hexadecimal, the project's canonical form.
            Assert.All(files.Values, file =>
            {
                Assert.Equal(64, file.SHA256!.Length);
                Assert.Equal(file.SHA256, file.SHA256.ToUpperInvariant());
            });

            AssertHashLinkInvariant(connection);
        }

        [Fact]
        public void HashPendingFiles_IdenticalContentInOneSource_BecomesOneAsset()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("first.jpg", "same bytes");
            media.CreateFile("Sent/second.jpg", "same bytes");
            media.CreateFile("different.jpg", "other bytes");

            var databasePath = CreateWorkspace(workspace);

            new MediaInventoryService().Inventory(databasePath, CreateRequest(media.RootPath));

            var result = new MediaHashingService().HashPendingFiles(databasePath);

            Assert.Equal(3, result.SuccessfullyHashed);
            Assert.Equal(2, result.NewAssets);
            Assert.Equal(1, result.ExistingAssetLinks);

            using var connection = WorkspaceDatabase.OpenConnection(databasePath);

            Assert.Equal(2, CountRows(connection, "MediaAsset"));
            Assert.Equal(3, CountRows(connection, "MediaAssetFile"));

            var files = ReadMediaFilesByPath(connection);
            var links = ReadAssetLinks(connection);

            // Two physical files, one payload: both rows survive and point at the same asset.
            Assert.Equal(
                links[files["first.jpg"].MediaFileID],
                links[files["Sent/second.jpg"].MediaFileID]);

            Assert.NotEqual(
                links[files["first.jpg"].MediaFileID],
                links[files["different.jpg"].MediaFileID]);

            AssertHashLinkInvariant(connection);
        }

        [Fact]
        public void HashPendingFiles_IdenticalContentAcrossTwoSources_BecomesOneAsset()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            var first = Path.Combine(media.ContainerPath, "PhoneOne");
            var second = Path.Combine(media.ContainerPath, "PhoneTwo");

            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            File.WriteAllText(Path.Combine(first, "IMG-20260105-WA0001.jpg"), "shared payload");
            File.WriteAllText(Path.Combine(second, "IMG-20260105-WA0001.jpg"), "shared payload");

            var databasePath = CreateWorkspace(workspace);
            var inventory = new MediaInventoryService();

            inventory.Inventory(databasePath, CreateRequest(first, displayName: "Phone one"));
            inventory.Inventory(databasePath, CreateRequest(second, displayName: "Phone two"));

            var result = new MediaHashingService().HashPendingFiles(databasePath);

            Assert.Equal(2, result.SuccessfullyHashed);
            Assert.Equal(1, result.NewAssets);
            Assert.Equal(1, result.ExistingAssetLinks);

            using var connection = WorkspaceDatabase.OpenConnection(databasePath);

            Assert.Equal(2, CountRows(connection, "MediaSource"));
            Assert.Equal(2, CountRows(connection, "MediaFile"));
            Assert.Equal(1, CountRows(connection, "MediaAsset"));
            Assert.Equal(2, CountRows(connection, "MediaAssetFile"));

            AssertHashLinkInvariant(connection);
        }

        /// <remarks>
        /// The same name is not the same file. Deduplication is by content and only by content.
        /// </remarks>
        [Fact]
        public void HashPendingFiles_SameFileNameDifferentContent_StaysTwoAssets()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("Camera/IMG-20260105-WA0001.jpg", "one payload");
            media.CreateFile("Sent/IMG-20260105-WA0001.jpg", "another payload");

            var databasePath = CreateWorkspace(workspace);

            new MediaInventoryService().Inventory(databasePath, CreateRequest(media.RootPath));

            var result = new MediaHashingService().HashPendingFiles(databasePath);

            Assert.Equal(2, result.NewAssets);
            Assert.Equal(0, result.ExistingAssetLinks);

            using var connection = WorkspaceDatabase.OpenConnection(databasePath);

            Assert.Equal(2, CountRows(connection, "MediaAsset"));

            AssertHashLinkInvariant(connection);
        }

        /// <remarks>
        /// Every empty file has the same SHA-256. Because an empty file is classified
        /// <c>Unknown</c> whatever its extension says, they deduplicate into one asset instead of
        /// each new extension being reported as a classification conflict.
        /// </remarks>
        [Fact]
        public void HashPendingFiles_EmptyFilesWithDifferentExtensions_BecomeOneUnknownAsset()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateEmptyFile("empty.jpg");
            media.CreateEmptyFile("empty.mp4");
            media.CreateEmptyFile("empty.pdf");

            var databasePath = CreateWorkspace(workspace);

            new MediaInventoryService().Inventory(databasePath, CreateRequest(media.RootPath));

            var result = new MediaHashingService().HashPendingFiles(databasePath);

            Assert.Equal(3, result.SuccessfullyHashed);
            Assert.Equal(1, result.NewAssets);
            Assert.Equal(2, result.ExistingAssetLinks);
            Assert.Equal(0, result.ClassificationConflicts);
            Assert.Equal(0, result.PhysicalBytesHashed);

            using var connection = WorkspaceDatabase.OpenConnection(databasePath);

            var asset = ReadMediaAssets(connection).Values.Single();

            Assert.Equal("Unknown", asset.MediaType);
            Assert.Equal(0, asset.SizeBytes);
            Assert.Equal(HashOf(string.Empty), asset.SHA256);

            Assert.Equal(3, CountRows(connection, "MediaAssetFile"));

            AssertHashLinkInvariant(connection);
        }

        /// <remarks>
        /// The conflict rule for real content, which the empty-file rule deliberately does not
        /// reach. Identical non-empty bytes under extensions meaning different things is a
        /// statement about the archive, not something to resolve by guessing; the second file is
        /// left untouched and reported.
        /// </remarks>
        [Fact]
        public void HashPendingFiles_SameContentUnderConflictingMediaTypes_LeavesTheSecondFileAlone()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("a-image.jpg", "identical payload");
            media.CreateFile("b-video.mp4", "identical payload");

            var databasePath = CreateWorkspace(workspace);

            new MediaInventoryService().Inventory(databasePath, CreateRequest(media.RootPath));

            var result = new MediaHashingService().HashPendingFiles(databasePath);

            Assert.Equal(1, result.SuccessfullyHashed);
            Assert.Equal(1, result.NewAssets);
            Assert.Equal(1, result.ClassificationConflicts);
            Assert.Equal(0, result.FailedFiles);
            Assert.Equal(1, result.RemainingUnhashed);

            using var connection = WorkspaceDatabase.OpenConnection(databasePath);

            // One asset, and no second asset carrying the same hash under the other type.
            Assert.Equal(1, CountRows(connection, "MediaAsset"));
            Assert.Equal(1, CountRows(connection, "MediaAssetFile"));

            var files = ReadMediaFilesByPath(connection);
            var asset = ReadMediaAssets(connection).Values.Single();

            Assert.Equal("Image", asset.MediaType);
            Assert.NotNull(files["a-image.jpg"].SHA256);

            // The conflicting file keeps its own classification and stays unhashed and unlinked.
            Assert.Null(files["b-video.mp4"].SHA256);
            Assert.Equal("Video", files["b-video.mp4"].MediaType);

            Assert.Equal([files["b-video.mp4"].MediaFileID], result.ConflictedMediaFileIDs);

            AssertHashLinkInvariant(connection);
        }

        /// <remarks>
        /// The defensive branch. Identical bytes of different lengths does not happen, so it is
        /// staged by seeding an asset directly: what is being tested is that the guard refuses
        /// rather than that the situation arises.
        /// </remarks>
        [Fact]
        public void HashPendingFiles_ExistingAssetWithAContradictorySize_IsTreatedAsAFailure()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("photo.jpg", "payload");

            var databasePath = CreateWorkspace(workspace);

            new MediaInventoryService().Inventory(databasePath, CreateRequest(media.RootPath));

            using (var setup = WorkspaceDatabase.OpenConnection(databasePath))
            {
                using var command = setup.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO MediaAsset (SHA256, MediaType, SizeBytes)
                    VALUES ($sha256, 'Image', 999);
                    """;

                command.Parameters.AddWithValue("$sha256", HashOf("payload"));
                command.ExecuteNonQuery();
            }

            var result = new MediaHashingService().HashPendingFiles(databasePath);

            Assert.Equal(0, result.SuccessfullyHashed);
            Assert.Equal(1, result.FailedFiles);
            Assert.Equal(0, result.ClassificationConflicts);
            Assert.Equal(1, result.RemainingUnhashed);

            using var connection = WorkspaceDatabase.OpenConnection(databasePath);

            // The existing asset is not modified, and no link is created.
            Assert.Equal(999, ReadMediaAssets(connection).Values.Single().SizeBytes);
            Assert.Equal(0, CountRows(connection, "MediaAssetFile"));
            Assert.Null(ReadMediaFiles(connection).Single().SHA256);
        }

        [Fact]
        public void HashPendingFiles_FileRemovedAfterInventory_IsReportedAndLeftUnhashed()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("kept.jpg", "kept");
            var removed = media.CreateFile("removed.jpg", "removed");

            var databasePath = CreateWorkspace(workspace);

            new MediaInventoryService().Inventory(databasePath, CreateRequest(media.RootPath));

            File.Delete(removed);

            var result = new MediaHashingService().HashPendingFiles(databasePath);

            Assert.Equal(1, result.SuccessfullyHashed);
            Assert.Equal(1, result.FailedFiles);
            Assert.Equal(1, result.RemainingUnhashed);

            using var connection = WorkspaceDatabase.OpenConnection(databasePath);

            var files = ReadMediaFilesByPath(connection);

            Assert.NotNull(files["kept.jpg"].SHA256);
            Assert.Null(files["removed.jpg"].SHA256);
            Assert.Equal([files["removed.jpg"].MediaFileID], result.FailedMediaFileIDs);

            AssertHashLinkInvariant(connection);
        }

        /// <remarks>
        /// The snapshot is not quietly re-measured. Hashing a file whose size no longer matches
        /// would attach one file's content identity to another file's inventory record, and
        /// updating the record would erase the evidence that the source changed at all.
        /// </remarks>
        [Fact]
        public void HashPendingFiles_FileSizeChangedAfterInventory_IsReportedAndLeftUnhashed()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("grown.jpg", "small");

            var databasePath = CreateWorkspace(workspace);

            new MediaInventoryService().Inventory(databasePath, CreateRequest(media.RootPath));

            media.CreateFile("grown.jpg", "considerably larger content");

            var result = new MediaHashingService().HashPendingFiles(databasePath);

            Assert.Equal(0, result.SuccessfullyHashed);
            Assert.Equal(1, result.FailedFiles);
            Assert.Equal(0, result.PhysicalBytesHashed);

            using var connection = WorkspaceDatabase.OpenConnection(databasePath);

            var file = ReadMediaFiles(connection).Single();

            Assert.Null(file.SHA256);
            Assert.Equal(5, file.SizeBytes);
            Assert.Equal(0, CountRows(connection, "MediaAsset"));
        }

        [Fact]
        public void HashPendingFiles_RunAgainAfterCompletion_DoesNothing()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("one.jpg", "alpha");
            media.CreateFile("two.jpg", "alpha");
            media.CreateFile("three.mp4", "beta");

            var databasePath = CreateWorkspace(workspace);

            new MediaInventoryService().Inventory(databasePath, CreateRequest(media.RootPath));

            var service = new MediaHashingService();

            service.HashPendingFiles(databasePath);

            long assetsAfterFirstRun;
            long linksAfterFirstRun;

            using (var connection = WorkspaceDatabase.OpenConnection(databasePath))
            {
                assetsAfterFirstRun = CountRows(connection, "MediaAsset");
                linksAfterFirstRun = CountRows(connection, "MediaAssetFile");
            }

            var second = service.HashPendingFiles(databasePath);

            Assert.Equal(0, second.PendingAtStart);
            Assert.Equal(0, second.SuccessfullyHashed);
            Assert.Equal(0, second.NewAssets);
            Assert.Equal(0, second.RemainingUnhashed);
            Assert.Equal(0, second.PhysicalBytesHashed);

            using var after = WorkspaceDatabase.OpenConnection(databasePath);

            Assert.Equal(assetsAfterFirstRun, CountRows(after, "MediaAsset"));
            Assert.Equal(linksAfterFirstRun, CountRows(after, "MediaAssetFile"));

            AssertHashLinkInvariant(after);
        }

        /// <summary>
        /// Builds the tree the resume comparisons use: two sources whose payloads repeat within
        /// each of them and across both.
        /// </summary>
        /// <remarks>
        /// The repetition is the point. Deduplication has to reach the same answer whether the two
        /// sources were hashed together or one at a time, and it can only be shown to do that if
        /// there is something to deduplicate across the boundary between instalments.
        /// </remarks>
        private static void BuildTwoSourceTree(
            TemporaryMediaDirectory media, out string firstRoot, out string secondRoot)
        {
            firstRoot = Path.Combine(media.ContainerPath, "PhoneOne");
            secondRoot = Path.Combine(media.ContainerPath, "PhoneTwo");

            Directory.CreateDirectory(Path.Combine(firstRoot, "Sent"));
            Directory.CreateDirectory(Path.Combine(secondRoot, "Sent"));

            File.WriteAllText(Path.Combine(firstRoot, "IMG-20260105-WA0001.jpg"), "payload A");
            File.WriteAllText(Path.Combine(firstRoot, "Sent", "IMG-20260106-WA0002.jpg"), "payload B");
            File.WriteAllText(Path.Combine(firstRoot, "IMG-20260107-WA0003.jpg"), "payload A");
            File.WriteAllText(Path.Combine(firstRoot, "empty.jpg"), string.Empty);

            File.WriteAllText(Path.Combine(secondRoot, "IMG-20260105-WA0001.jpg"), "payload A");
            File.WriteAllText(Path.Combine(secondRoot, "Sent", "IMG-20260108-WA0004.jpg"), "payload C");
            File.WriteAllText(Path.Combine(secondRoot, "empty.mp4"), string.Empty);
        }

        /// <summary>
        /// Inventories both roots and returns the first source's identifier.
        /// </summary>
        private static long InventoryBothSources(
            string databasePath, string firstRoot, string secondRoot)
        {
            var inventory = new MediaInventoryService();

            var first = inventory.Inventory(
                databasePath, CreateRequest(firstRoot, displayName: "Phone one"));

            inventory.Inventory(databasePath, CreateRequest(secondRoot, displayName: "Phone two"));

            return first.MediaSourceID;
        }

        /// <summary>
        /// Every media file as the pair that identifies it, with the hash it ended up carrying.
        /// </summary>
        /// <remarks>
        /// Source and path together, because the two sources deliberately share file names and
        /// either half alone would not identify a row.
        /// </remarks>
        private static List<string> HashesBySourceAndPath(SqliteConnection connection) =>
            ReadMediaFiles(connection)
                .Select(file => $"{file.MediaSourceID}|{file.RelativePath}|{file.SHA256}")
                .ToList();

        /// <remarks>
        /// The property that makes an 80-gigabyte run survivable: a workspace hashed in instalments
        /// and one hashed in a single pass must end up the same. Logical equivalence, not
        /// byte-for-byte database identity — identifiers may legitimately be assigned in a
        /// different order.
        /// <para>
        /// The instalments are driven by hashing one source and then the rest, which leaves exactly
        /// the state an interruption leaves: some files hashed and linked, others still pending,
        /// and assets the remaining files will have to deduplicate against.
        /// </para>
        /// </remarks>
        [Fact]
        public void HashPendingFiles_HashedInInstalments_ReachesTheSameStateAsOneRun()
        {
            using var singleRunWorkspace = new TemporaryWorkspaceDatabase();
            using var singleRunMedia = new TemporaryMediaDirectory();

            BuildTwoSourceTree(singleRunMedia, out var singleFirst, out var singleSecond);

            var singleRunPath = CreateWorkspace(singleRunWorkspace);

            InventoryBothSources(singleRunPath, singleFirst, singleSecond);

            var singleRun = new MediaHashingService().HashPendingFiles(singleRunPath);

            Assert.Equal(7, singleRun.SuccessfullyHashed);
            Assert.Equal(0, singleRun.RemainingUnhashed);

            using var resumedWorkspace = new TemporaryWorkspaceDatabase();
            using var resumedMedia = new TemporaryMediaDirectory();

            BuildTwoSourceTree(resumedMedia, out var resumedFirst, out var resumedSecond);

            var resumedPath = CreateWorkspace(resumedWorkspace);

            var firstSourceID = InventoryBothSources(resumedPath, resumedFirst, resumedSecond);

            var service = new MediaHashingService();

            // First instalment: one source only, in batches small enough to span several commits.
            var partial = service.HashPendingFiles(resumedPath, firstSourceID, batchSize: 2);

            Assert.Equal(4, partial.SuccessfullyHashed);
            Assert.Equal(0, partial.RemainingUnhashed);

            using (var midway = WorkspaceDatabase.OpenConnection(resumedPath))
            {
                // Committed work is real, and the rest is genuinely still waiting.
                Assert.Equal(4, CountRows(midway, "MediaAssetFile"));
                Assert.Equal(3, ReadMediaFiles(midway).Count(file => file.SHA256 is null));

                AssertHashLinkInvariant(midway);
            }

            // Second instalment: everything still pending, including files whose payloads the
            // first instalment already recorded.
            var remainder = service.HashPendingFiles(resumedPath, batchSize: 2);

            Assert.Equal(3, remainder.SuccessfullyHashed);
            Assert.Equal(0, remainder.RemainingUnhashed);
            Assert.Equal(0, remainder.FailedFiles);

            using var singleRunConnection = WorkspaceDatabase.OpenConnection(singleRunPath);
            using var resumed = WorkspaceDatabase.OpenConnection(resumedPath);

            Assert.Equal(
                CountRows(singleRunConnection, "MediaFile"), CountRows(resumed, "MediaFile"));
            Assert.Equal(
                CountRows(singleRunConnection, "MediaAsset"), CountRows(resumed, "MediaAsset"));
            Assert.Equal(
                CountRows(singleRunConnection, "MediaAssetFile"),
                CountRows(resumed, "MediaAssetFile"));

            // The same payloads, deduplicated the same way, whatever order they were reached in.
            Assert.Equal(
                ReadMediaAssets(singleRunConnection).Keys.Order(StringComparer.OrdinalIgnoreCase),
                ReadMediaAssets(resumed).Keys.Order(StringComparer.OrdinalIgnoreCase));

            Assert.Equal(
                HashesBySourceAndPath(singleRunConnection), HashesBySourceAndPath(resumed));

            AssertHashLinkInvariant(resumed);
        }

        /// <remarks>
        /// Cancelling must leave committed work intact and add nothing of its own — never a hash
        /// recorded without the asset and link that give it meaning. Running again finishes the
        /// job, and reaches the state an uncancelled run would have.
        /// </remarks>
        [Fact]
        public void HashPendingFiles_CancelledAfterEarlierWorkWasCommitted_KeepsItAndResumesCleanly()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            BuildTwoSourceTree(media, out var firstRoot, out var secondRoot);

            var databasePath = CreateWorkspace(workspace);

            var firstSourceID = InventoryBothSources(databasePath, firstRoot, secondRoot);

            var service = new MediaHashingService();

            var committed = service.HashPendingFiles(databasePath, firstSourceID);

            Assert.Equal(4, committed.SuccessfullyHashed);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var cancelled = service.HashPendingFiles(
                databasePath, cancellationToken: cancellation.Token);

            Assert.True(cancelled.WasCancelled);
            Assert.Equal(3, cancelled.PendingAtStart);
            Assert.Equal(0, cancelled.SuccessfullyHashed);
            Assert.Equal(0, cancelled.PhysicalBytesHashed);

            // Cancelling is not a failure, and must not be counted as one.
            Assert.Equal(0, cancelled.FailedFiles);
            Assert.Empty(cancelled.FailedMediaFileIDs);
            Assert.Equal(3, cancelled.RemainingUnhashed);

            using (var afterCancel = WorkspaceDatabase.OpenConnection(databasePath))
            {
                // The earlier run's work is exactly as it was, and the cancelled run added nothing.
                Assert.Equal(4, CountRows(afterCancel, "MediaAssetFile"));

                AssertHashLinkInvariant(afterCancel);
            }

            var resumed = service.HashPendingFiles(databasePath);

            Assert.False(resumed.WasCancelled);
            Assert.Equal(3, resumed.SuccessfullyHashed);
            Assert.Equal(0, resumed.RemainingUnhashed);

            using var connection = WorkspaceDatabase.OpenConnection(databasePath);

            Assert.Equal(7, CountRows(connection, "MediaAssetFile"));
            Assert.All(ReadMediaFiles(connection), file => Assert.NotNull(file.SHA256));

            AssertHashLinkInvariant(connection);
        }

        /// <remarks>
        /// A file that cannot be hashed must not stop the run reaching the rest of the archive. The
        /// pagination advances past everything already attempted, so a permanent failure costs one
        /// attempt per run rather than looping forever on the same page.
        /// </remarks>
        [Fact]
        public void HashPendingFiles_FailureInAFullBatch_DoesNotStallTheRun()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("a-missing.jpg", "gone");
            media.CreateFile("b-present.jpg", "present");
            media.CreateFile("c-present.jpg", "another");

            var databasePath = CreateWorkspace(workspace);

            new MediaInventoryService().Inventory(databasePath, CreateRequest(media.RootPath));

            File.Delete(media.ResolveRelative("a-missing.jpg"));

            // A batch size of one puts the failing file alone in the first page: if pagination
            // depended on progress being made, the run would never reach the other two.
            var result = new MediaHashingService()
                .HashPendingFiles(databasePath, batchSize: 1);

            Assert.Equal(2, result.SuccessfullyHashed);
            Assert.Equal(1, result.FailedFiles);
            Assert.Equal(1, result.RemainingUnhashed);

            using var connection = WorkspaceDatabase.OpenConnection(databasePath);

            AssertHashLinkInvariant(connection);
        }

        [Fact]
        public void HashPendingFiles_RestrictedToOneSource_LeavesOtherSourcesPending()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            var first = Path.Combine(media.ContainerPath, "PhoneOne");
            var second = Path.Combine(media.ContainerPath, "PhoneTwo");

            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            File.WriteAllText(Path.Combine(first, "one.jpg"), "one");
            File.WriteAllText(Path.Combine(second, "two.jpg"), "two");

            var databasePath = CreateWorkspace(workspace);
            var inventory = new MediaInventoryService();

            var firstResult = inventory.Inventory(
                databasePath, CreateRequest(first, displayName: "Phone one"));

            inventory.Inventory(databasePath, CreateRequest(second, displayName: "Phone two"));

            var result = new MediaHashingService()
                .HashPendingFiles(databasePath, firstResult.MediaSourceID);

            Assert.Equal(1, result.PendingAtStart);
            Assert.Equal(1, result.SuccessfullyHashed);
            Assert.Equal(0, result.RemainingUnhashed);

            using var connection = WorkspaceDatabase.OpenConnection(databasePath);

            var files = ReadMediaFilesByPath(connection);

            Assert.NotNull(files["one.jpg"].SHA256);
            Assert.Null(files["two.jpg"].SHA256);

            AssertHashLinkInvariant(connection);
        }

        /// <remarks>
        /// Streaming rather than loading whole files is what makes a multi-gigabyte video hashable
        /// at all. A file several times the read buffer proves the chunk loop assembles one hash
        /// across many reads.
        /// </remarks>
        [Fact]
        public void HashPendingFiles_FileLargerThanTheReadBuffer_IsHashedCorrectly()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            var content = new string('x', (1024 * 1024 * 2) + 12345);

            media.CreateFile("large.mp4", content);

            var databasePath = CreateWorkspace(workspace);

            new MediaInventoryService().Inventory(databasePath, CreateRequest(media.RootPath));

            var result = new MediaHashingService().HashPendingFiles(databasePath);

            Assert.Equal(1, result.SuccessfullyHashed);
            Assert.Equal(content.Length, result.PhysicalBytesHashed);

            using var connection = WorkspaceDatabase.OpenConnection(databasePath);

            Assert.Equal(HashOf(content), ReadMediaFiles(connection).Single().SHA256);
        }

        [Fact]
        public void HashPendingFiles_DatabaseThatDoesNotExist_IsRejectedWithoutCreatingAFile()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            Assert.Throws<FileNotFoundException>(() => new MediaHashingService()
                .HashPendingFiles(workspace.DatabasePath));

            SqliteConnection.ClearAllPools();

            Assert.False(File.Exists(workspace.DatabasePath));
            Assert.Empty(Directory.GetFiles(workspace.DirectoryPath));
        }

        [Fact]
        public void HashPendingFiles_GenuineVersionOneDatabase_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionOneWorkspace.Create(workspace.DatabasePath);

            Assert.Throws<InvalidOperationException>(() => new MediaHashingService()
                .HashPendingFiles(workspace.DatabasePath));

            using var connection = SyntheticVersionOneWorkspace.OpenPlainConnection(
                workspace.DatabasePath);

            Assert.Equal(1, WorkspaceDatabase.ReadSchemaVersion(connection));
        }

        [Fact]
        public void HashPendingFiles_BatchSizeBelowOne_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            var databasePath = CreateWorkspace(workspace);

            Assert.Throws<ArgumentOutOfRangeException>(() => new MediaHashingService()
                .HashPendingFiles(databasePath, batchSize: 0));
        }

        /// <remarks>
        /// This phase records what media exists. It does not decide which message owns it, so no
        /// attachment may change as a side effect of hashing.
        /// </remarks>
        [Fact]
        public void HashPendingFiles_DoesNotTouchAttachments()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("one.jpg", "alpha");

            var databasePath = CreateWorkspace(workspace);

            new MediaInventoryService().Inventory(databasePath, CreateRequest(media.RootPath));
            new MediaHashingService().HashPendingFiles(databasePath);

            using var connection = WorkspaceDatabase.OpenConnection(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM Attachment WHERE ResolvedMediaAssetID IS NOT NULL;";

            Assert.Equal(0L, (long)command.ExecuteScalar()!);
        }
    }
}
