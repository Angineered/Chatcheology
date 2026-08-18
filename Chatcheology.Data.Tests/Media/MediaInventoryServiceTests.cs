using Chatcheology.Data.Media;
using Chatcheology.Data.Tests.Workspace;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Media.MediaTestData;

namespace Chatcheology.Data.Tests.Media
{
    /// <summary>
    /// Tests for registering a media source and recording the files beneath it.
    /// </summary>
    public class MediaInventoryServiceTests
    {
        [Fact]
        public void Inventory_NestedTree_RecordsEveryFileWithCanonicalRelativePaths()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("WhatsApp Images/IMG-20260105-WA0001.jpg", "one");
            media.CreateFile("WhatsApp Images/Sent/IMG-20260106-WA0002.jpg", "two");
            media.CreateFile("WhatsApp Video/Sent/deeper/VID-20260107-WA0003.mp4", "three");
            media.CreateFile("top-level.txt", "four");

            var result = new MediaInventoryService()
                .Inventory(CreateWorkspace(workspace), CreateRequest(media.RootPath));

            Assert.Equal(4, result.Summary.FileCount);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(1, CountRows(connection, "MediaSource"));
            Assert.Equal(4, CountRows(connection, "MediaFile"));

            var files = ReadMediaFilesByPath(connection);

            // Forward slashes, no leading separator, no volume, no traversal, source spelling kept.
            Assert.Equal(
                [
                    "WhatsApp Images/IMG-20260105-WA0001.jpg",
                    "WhatsApp Images/Sent/IMG-20260106-WA0002.jpg",
                    "WhatsApp Video/Sent/deeper/VID-20260107-WA0003.mp4",
                    "top-level.txt",
                ],
                files.Keys.Order(StringComparer.Ordinal));

            foreach (var file in files.Values)
            {
                Assert.DoesNotContain('\\', file.RelativePath);
                Assert.False(Path.IsPathRooted(file.RelativePath));
                Assert.DoesNotContain("..", file.RelativePath, StringComparison.Ordinal);
                Assert.All(result.Summary.UnknownExtensionCounts, entry => Assert.NotEqual(0, entry.Count));
            }

            var nested = files["WhatsApp Video/Sent/deeper/VID-20260107-WA0003.mp4"];

            Assert.Equal("VID-20260107-WA0003.mp4", nested.FileName);
            Assert.Equal(".mp4", nested.Extension);
            Assert.Equal(5, nested.SizeBytes);
        }

        /// <remarks>
        /// Discovery is metadata only. Content identity, duration and dimensions all belong to
        /// later passes, and a column left null is how the workspace says "not measured" rather
        /// than "measured as nothing".
        /// </remarks>
        [Fact]
        public void Inventory_LeavesHashAndMediaMetadataUnset()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("WhatsApp Images/IMG-20260105-WA0001.jpg");

            new MediaInventoryService()
                .Inventory(CreateWorkspace(workspace), CreateRequest(media.RootPath));

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            var file = ReadMediaFiles(connection).Single();

            Assert.Null(file.SHA256);
            Assert.False(file.HasDurationMS);
            Assert.False(file.HasWidth);
            Assert.False(file.HasHeight);

            Assert.Equal(0, CountRows(connection, "MediaAsset"));
            Assert.Equal(0, CountRows(connection, "MediaAssetFile"));
        }

        [Fact]
        public void Inventory_StoresNormalisedExtensionsAndClassifications()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("picture.JPG", "image");
            media.CreateFile("clip.MP4", "video");
            media.CreateFile("voice.opus", "audio");
            media.CreateFile("notes.pdf", "document");
            media.CreateFile("mystery.thumbdata", "unknown");
            media.CreateFile("README", "extensionless");

            var result = new MediaInventoryService()
                .Inventory(CreateWorkspace(workspace), CreateRequest(media.RootPath));

            Assert.Equal(1, result.Summary.ImageCount);
            Assert.Equal(1, result.Summary.VideoCount);
            Assert.Equal(1, result.Summary.AudioCount);
            Assert.Equal(1, result.Summary.DocumentCount);
            Assert.Equal(2, result.Summary.UnknownCount);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            var files = ReadMediaFilesByPath(connection);

            Assert.Equal(".jpg", files["picture.JPG"].Extension);
            Assert.Equal("Image", files["picture.JPG"].MediaType);
            Assert.Equal(".mp4", files["clip.MP4"].Extension);
            Assert.Equal("Video", files["clip.MP4"].MediaType);
            Assert.Equal("Audio", files["voice.opus"].MediaType);
            Assert.Equal("Document", files["notes.pdf"].MediaType);
            Assert.Equal("Unknown", files["mystery.thumbdata"].MediaType);

            // The file's own name is preserved; only the stored extension is normalised.
            Assert.Equal("picture.JPG", files["picture.JPG"].FileName);

            // No extension is a null, not an empty string.
            Assert.Null(files["README"].Extension);
            Assert.Equal("Unknown", files["README"].MediaType);
        }

        /// <remarks>
        /// The extensions added to the table after real archives were seen to contain them, checked
        /// end to end rather than only as map entries: what matters is the value that reaches the
        /// <c>MediaFile</c> row. The empty <c>.svg</c> is here to show the size rule still wins over
        /// a newly recognised extension.
        /// </remarks>
        [Fact]
        public void Inventory_EvidenceLedExtensions_AreClassifiedAndStored()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("drawing.svg", "image");
            media.CreateFile("artwork.eps", "image");
            media.CreateFile("bundle.7z", "archive");
            media.CreateFile("bundle.rar", "archive");
            media.CreateFile("data.json", "structured");
            media.CreateFile("notes.md", "text");
            media.CreateFile("dump.sql", "text");
            media.CreateEmptyFile("empty.svg");

            // Deliberately still unclassified: not media, whatever an archive happens to hold.
            media.CreateFile("installer.exe", "binary");
            media.CreateFile("package.ipa", "binary");
            media.CreateFile("marker.was", "binary");

            var result = new MediaInventoryService()
                .Inventory(CreateWorkspace(workspace), CreateRequest(media.RootPath));

            Assert.Equal(2, result.Summary.ImageCount);
            Assert.Equal(5, result.Summary.DocumentCount);
            Assert.Equal(4, result.Summary.UnknownCount);
            Assert.Equal(1, result.Summary.ZeroByteFileCount);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            var files = ReadMediaFilesByPath(connection);

            Assert.Equal("Image", files["drawing.svg"].MediaType);
            Assert.Equal("Image", files["artwork.eps"].MediaType);
            Assert.Equal("Document", files["bundle.7z"].MediaType);
            Assert.Equal("Document", files["bundle.rar"].MediaType);
            Assert.Equal("Document", files["data.json"].MediaType);
            Assert.Equal("Document", files["notes.md"].MediaType);
            Assert.Equal("Document", files["dump.sql"].MediaType);

            // The size rule takes precedence over the extension table.
            Assert.Equal("Unknown", files["empty.svg"].MediaType);
            Assert.Equal(".svg", files["empty.svg"].Extension);

            Assert.Equal("Unknown", files["installer.exe"].MediaType);
            Assert.Equal("Unknown", files["package.ipa"].MediaType);
            Assert.Equal("Unknown", files["marker.was"].MediaType);
        }

        [Fact]
        public void Inventory_WhatsAppLayout_ReadsDirectionAndNamingDates()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("WhatsApp Images/Sent/IMG-20260724-WA0004.jpg");
            media.CreateFile("WhatsApp Images/IMG-20220128-WA0003.jpg");
            media.CreateFile("WhatsApp Images/Sentimental/holiday.jpg");
            media.CreateFile("WhatsApp Images/IMG-20261332-WA0009.jpg");

            var result = new MediaInventoryService()
                .Inventory(CreateWorkspace(workspace), CreateRequest(media.RootPath));

            Assert.Equal(1, result.Summary.SentCount);
            Assert.Equal(3, result.Summary.NonSentCount);
            Assert.Equal(0, result.Summary.DirectionUnknownCount);
            Assert.Equal(2, result.Summary.FileDateCount);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            var files = ReadMediaFilesByPath(connection);

            Assert.True(files["WhatsApp Images/Sent/IMG-20260724-WA0004.jpg"].IsSent);
            Assert.Equal("2026-07-24", files["WhatsApp Images/Sent/IMG-20260724-WA0004.jpg"].FileDate);

            Assert.False(files["WhatsApp Images/IMG-20220128-WA0003.jpg"].IsSent);
            Assert.Equal("2022-01-28", files["WhatsApp Images/IMG-20220128-WA0003.jpg"].FileDate);

            // Sentimental is not Sent, and an impossible date is no date.
            Assert.False(files["WhatsApp Images/Sentimental/holiday.jpg"].IsSent);
            Assert.Null(files["WhatsApp Images/Sentimental/holiday.jpg"].FileDate);
            Assert.Null(files["WhatsApp Images/IMG-20261332-WA0009.jpg"].FileDate);
        }

        /// <remarks>
        /// A layout this build knows no conventions for is inventoried in full, but yields no
        /// direction and no naming-derived date. Null, not false and not a guessed date.
        /// </remarks>
        [Fact]
        public void Inventory_UnknownSourceType_RecordsNoDirectionOrNamingDate()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("Sent/IMG-20260724-WA0004.jpg");

            var result = new MediaInventoryService().Inventory(
                CreateWorkspace(workspace),
                CreateRequest(media.RootPath, GenericSourceType));

            Assert.Equal(1, result.Summary.FileCount);
            Assert.Equal(0, result.Summary.SentCount);
            Assert.Equal(0, result.Summary.NonSentCount);
            Assert.Equal(1, result.Summary.DirectionUnknownCount);
            Assert.Equal(0, result.Summary.FileDateCount);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            var file = ReadMediaFiles(connection).Single();

            Assert.Null(file.IsSent);
            Assert.Null(file.FileDate);
        }

        [Fact]
        public void Inventory_EmptyFile_IsUnknownWhateverItsExtension()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateEmptyFile("empty.jpg");
            media.CreateEmptyFile("empty.mp4");
            media.CreateFile("real.jpg", "bytes");

            var result = new MediaInventoryService()
                .Inventory(CreateWorkspace(workspace), CreateRequest(media.RootPath));

            Assert.Equal(2, result.Summary.ZeroByteFileCount);
            Assert.Equal(2, result.Summary.UnknownCount);
            Assert.Equal(1, result.Summary.ImageCount);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            var files = ReadMediaFilesByPath(connection);

            Assert.Equal("Unknown", files["empty.jpg"].MediaType);
            Assert.Equal("Unknown", files["empty.mp4"].MediaType);

            // The extension is still recorded; it is the classification that declines to trust it.
            Assert.Equal(".jpg", files["empty.jpg"].Extension);
            Assert.Equal(0, files["empty.jpg"].SizeBytes);
        }

        /// <remarks>
        /// Hidden and system files are part of the archive. Skipping them would leave a record of a
        /// copy quietly shorter than the copy itself.
        /// </remarks>
        [Fact]
        public void Inventory_HiddenAndSystemFiles_AreIncludedAndCounted()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("visible.jpg", "one");

            var hidden = media.CreateFile("hidden.jpg", "two");
            File.SetAttributes(hidden, FileAttributes.Hidden);

            var system = media.CreateFile("system.jpg", "three");
            File.SetAttributes(system, FileAttributes.System);

            var result = new MediaInventoryService()
                .Inventory(CreateWorkspace(workspace), CreateRequest(media.RootPath));

            Assert.Equal(3, result.Summary.FileCount);
            Assert.Equal(1, result.Summary.HiddenFileCount);
            Assert.Equal(1, result.Summary.SystemFileCount);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(3, CountRows(connection, "MediaFile"));
        }

        [Fact]
        public void Discover_UnknownExtensions_AreReportedAsADeterministicHistogram()
        {
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("a.thumbdata", "x");
            media.CreateFile("b.thumbdata", "x");
            media.CreateFile("c.thumbdata", "x");
            media.CreateFile("d.dat", "x");
            media.CreateFile("e.dat", "x");
            media.CreateFile("README", "x");
            media.CreateFile("keep.jpg", "x");

            var summary = new MediaInventoryService()
                .Discover(media.RootPath, MediaSourceTypes.WhatsAppMediaDirectory);

            // Most frequent first, then by extension. Recognised extensions do not appear.
            Assert.Equal(
                [".thumbdata", ".dat", MediaClassification.NoExtensionLabel],
                summary.UnknownExtensionCounts.Select(entry => entry.Extension));

            Assert.Equal([3, 2, 1], summary.UnknownExtensionCounts.Select(entry => entry.Count));
        }

        /// <remarks>
        /// A junction inside a source root must not extend the user's choice of what to read to
        /// somewhere else on the disk. Skipped rather than failed where the machine will not create
        /// one, so the suite never requires administrator rights; the enumeration rule itself is
        /// unconditional in production.
        /// </remarks>
        [Fact]
        public void Inventory_JunctionInsideRoot_IsNotFollowed()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("inside.jpg", "inside");

            var outside = Path.Combine(media.ContainerPath, "Outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "outside.jpg"), "outside");

            if (!media.TryCreateJunction("Linked", outside))
            {
                return;
            }

            var result = new MediaInventoryService()
                .Inventory(CreateWorkspace(workspace), CreateRequest(media.RootPath));

            Assert.Equal(1, result.Summary.FileCount);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(["inside.jpg"], ReadMediaFilesByPath(connection).Keys);
        }

        [Fact]
        public void Inventory_SameRootTwice_IsRejectedWithoutDuplicatingAnything()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("only.jpg");

            var databasePath = CreateWorkspace(workspace);
            var service = new MediaInventoryService();

            service.Inventory(databasePath, CreateRequest(media.RootPath));

            // Trailing separator and a traversal that resolves back to the same directory: both
            // normalise to the root already registered.
            Assert.Throws<InvalidOperationException>(() =>
                service.Inventory(databasePath, CreateRequest(media.RootPath + Path.DirectorySeparatorChar)));

            Assert.Throws<InvalidOperationException>(() =>
                service.Inventory(
                    databasePath,
                    CreateRequest(Path.Combine(media.RootPath, "sub", ".."))));

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(1, CountRows(connection, "MediaSource"));
            Assert.Equal(1, CountRows(connection, "MediaFile"));
        }

        [Fact]
        public void Inventory_RootInsideAnAlreadyRegisteredRoot_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("Images/only.jpg");

            var databasePath = CreateWorkspace(workspace);
            var service = new MediaInventoryService();

            service.Inventory(databasePath, CreateRequest(media.RootPath));

            Assert.Throws<InvalidOperationException>(() =>
                service.Inventory(
                    databasePath,
                    CreateRequest(media.ResolveRelative("Images"))));

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(1, CountRows(connection, "MediaSource"));
            Assert.Equal(1, CountRows(connection, "MediaFile"));
        }

        [Fact]
        public void Inventory_RootContainingAnAlreadyRegisteredRoot_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("Images/only.jpg");

            var databasePath = CreateWorkspace(workspace);
            var service = new MediaInventoryService();

            service.Inventory(databasePath, CreateRequest(media.ResolveRelative("Images")));

            Assert.Throws<InvalidOperationException>(() =>
                service.Inventory(databasePath, CreateRequest(media.RootPath)));

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(1, CountRows(connection, "MediaSource"));
        }

        /// <remarks>
        /// The case the separator-aware comparison exists for. <c>Media</c> and <c>Media2</c> share
        /// a string prefix but no directory, and registering both is exactly what several media
        /// sources are for.
        /// </remarks>
        [Fact]
        public void Inventory_SiblingRootsWithASharedNamePrefix_AreBothAccepted()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            var first = Path.Combine(media.ContainerPath, "Media");
            var second = Path.Combine(media.ContainerPath, "Media2");

            Directory.CreateDirectory(second);
            File.WriteAllText(Path.Combine(first, "one.jpg"), "one");
            File.WriteAllText(Path.Combine(second, "two.jpg"), "two");

            var databasePath = CreateWorkspace(workspace);
            var service = new MediaInventoryService();

            service.Inventory(databasePath, CreateRequest(first));
            service.Inventory(databasePath, CreateRequest(second));

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(2, CountRows(connection, "MediaSource"));
            Assert.Equal(2, CountRows(connection, "MediaFile"));
        }

        /// <remarks>
        /// An empty root is far more likely to be the wrong directory than a meaningful part of a
        /// reconstruction, and recording it would leave a source permanently claiming an archive
        /// holds nothing.
        /// </remarks>
        [Fact]
        public void Inventory_EmptyRoot_IsRejectedWithoutCreatingASource()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            Directory.CreateDirectory(media.ResolveRelative("EmptySubdirectory"));

            Assert.Throws<InvalidOperationException>(() => new MediaInventoryService()
                .Inventory(CreateWorkspace(workspace), CreateRequest(media.RootPath)));

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(0, CountRows(connection, "MediaSource"));
            Assert.Equal(0, CountRows(connection, "MediaFile"));
        }

        /// <remarks>
        /// Discovery reports an empty directory as empty. Only storing it is refused, because only
        /// then does it become a claim the workspace makes.
        /// </remarks>
        [Fact]
        public void Discover_EmptyRoot_ReportsNothingRatherThanFailing()
        {
            using var media = new TemporaryMediaDirectory();

            var summary = new MediaInventoryService()
                .Discover(media.RootPath, MediaSourceTypes.WhatsAppMediaDirectory);

            Assert.Equal(0, summary.FileCount);
            Assert.Equal(0, summary.TotalSizeBytes);
            Assert.Empty(summary.UnknownExtensionCounts);
        }

        [Fact]
        public void Inventory_RootThatDoesNotExist_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            Assert.Throws<DirectoryNotFoundException>(() => new MediaInventoryService()
                .Inventory(
                    CreateWorkspace(workspace),
                    CreateRequest(Path.Combine(media.RootPath, "NotThere"))));

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(0, CountRows(connection, "MediaSource"));
        }

        [Fact]
        public void Inventory_RootThatIsAFile_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            var filePath = media.CreateFile("not-a-directory.jpg");

            Assert.Throws<DirectoryNotFoundException>(() => new MediaInventoryService()
                .Inventory(CreateWorkspace(workspace), CreateRequest(filePath)));
        }

        [Fact]
        public void Inventory_NonUtcImportedDateTime_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("only.jpg");

            var request = new MediaSourceRequest
            {
                DisplayName = DisplayName,
                SourceType = MediaSourceTypes.WhatsAppMediaDirectory,
                RootPath = media.RootPath,
                ImportedDateTimeUtc = new DateTime(2026, 8, 18, 9, 30, 0, DateTimeKind.Local),
            };

            var exception = Assert.Throws<ArgumentException>(() => new MediaInventoryService()
                .Inventory(CreateWorkspace(workspace), request));

            Assert.Contains(nameof(DateTimeKind.Local), exception.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// The inventory service opens the workspace connection with <c>ReadWriteCreate</c>, so
        /// without an existence check it would create an empty database purely in order to reject
        /// it.
        /// </remarks>
        [Fact]
        public void Inventory_DatabaseThatDoesNotExist_IsRejectedWithoutCreatingAFile()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("only.jpg");

            Assert.Throws<FileNotFoundException>(() => new MediaInventoryService()
                .Inventory(workspace.DatabasePath, CreateRequest(media.RootPath)));

            SqliteConnection.ClearAllPools();

            Assert.False(File.Exists(workspace.DatabasePath));
            Assert.Empty(Directory.GetFiles(workspace.DirectoryPath));
        }

        [Fact]
        public void Inventory_GenuineVersionOneDatabase_IsRejectedWithoutMigratingIt()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("only.jpg");

            SyntheticVersionOneWorkspace.Create(workspace.DatabasePath);

            Assert.Throws<InvalidOperationException>(() => new MediaInventoryService()
                .Inventory(workspace.DatabasePath, CreateRequest(media.RootPath)));

            using var connection = SyntheticVersionOneWorkspace.OpenPlainConnection(
                workspace.DatabasePath);

            Assert.Equal(1, WorkspaceDatabase.ReadSchemaVersion(connection));

            // Not migrated as a side effect of the attempt.
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'MediaSource';";

            Assert.Equal(0L, (long)command.ExecuteScalar()!);
        }

        [Fact]
        public void Inventory_DatabaseWithNoWorkspaceSchema_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("only.jpg");

            using (var setup = WorkspaceDatabase.OpenConnection(workspace.DatabasePath))
            {
                using var command = setup.CreateCommand();
                command.CommandText = "CREATE TABLE Unrelated (Value TEXT NOT NULL);";
                command.ExecuteNonQuery();
            }

            Assert.Throws<InvalidOperationException>(() => new MediaInventoryService()
                .Inventory(workspace.DatabasePath, CreateRequest(media.RootPath)));
        }

        [Fact]
        public void Inventory_StoresSourceMetadataAsSupplied()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var media = new TemporaryMediaDirectory();

            media.CreateFile("only.jpg");

            new MediaInventoryService()
                .Inventory(CreateWorkspace(workspace), CreateRequest(media.RootPath));

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT DisplayName, SourceType, RootPath, DeviceDescription, ImportedDateTimeUtc
                FROM MediaSource;
                """;

            using var reader = command.ExecuteReader();

            Assert.True(reader.Read());
            Assert.Equal(DisplayName, reader.GetString(0));
            Assert.Equal(MediaSourceTypes.WhatsAppMediaDirectory, reader.GetString(1));
            Assert.Equal(media.RootPath, reader.GetString(2));
            Assert.Equal(DeviceDescription, reader.GetString(3));
            Assert.Equal("2026-08-18T09:30:00.0000000Z", reader.GetString(4));
        }
    }
}
