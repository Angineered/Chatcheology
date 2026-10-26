using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Chatcheology.Data.OlderExports;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.OlderExports
{
    public class OlderExportImporterTests
    {
        private const string Alex = "Alex Example";
        private const string Sam = "Sam Sample";

        private static readonly DateTime Now = new(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);

        /// <summary>A shape-B export from 2016-03-26 to 2016-12-08 with every kind of line.</summary>
        private const string MiddleChat =
            """
            26/03/2016, 10:00 - Messages to this chat are now secured.
            26/03/2016, 10:01 - Alex Example: blorp.jpg (file attached)
            a caption
            27/03/2016, 11:00 - Sam Sample: Blorp.JPG (file attached)
            28/03/2016, 12:00 - Sam Sample: elsewhere.jpg (file attached)
            29/03/2016, 13:00 - Alex Example: <Media omitted>
            08/12/2016, 23:00 - Sam Sample: zorp wibble
            """;

        [Fact]
        public void Import_WritesOneConversationWithRolesOrderAndAttachments()
        {
            using var folders = new SyntheticExportFolders();
            var folder = folders.CreateExport("20161209", MiddleChat, ("blorp.jpg", "pixels-one"));
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            var result = Import(folders, folder, position: 4, shown: true);

            Assert.Equal(6, result.MessageCount);
            Assert.Equal(1, result.MediaOmittedAttachmentCount);
            Assert.Equal(3, result.FileAttachedAttachmentCount);
            Assert.Equal(1, result.ResolvedInOwnFolderCount);
            Assert.Equal(0, result.ResolvedInFallbackFolderCount);
            Assert.Equal(2, result.UnresolvedFileAttachedCount);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(folders.OlderExportsPath);

            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM Conversation;"));
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM ImportSource;"));
            Assert.Equal(
                SyntheticExportFolders.ChatFileName,
                Scalar(connection, "SELECT OriginalFileName FROM ImportSource;"));
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, SyntheticExportFolders.ChatFileName)))),
                Scalar(connection, "SELECT SHA256 FROM ImportSource;"));

            Assert.Equal(
                "Local",
                Scalar(connection, $"SELECT r.Role FROM ExportRole r JOIN Participant p ON p.ParticipantID = r.ParticipantID WHERE p.DisplayName = '{Alex}';"));
            Assert.Equal(
                "Other",
                Scalar(connection, $"SELECT r.Role FROM ExportRole r JOIN Participant p ON p.ParticipantID = r.ParticipantID WHERE p.DisplayName = '{Sam}';"));

            Assert.Equal(4L, Scalar(connection, "SELECT Position FROM ExportOrder;"));
            Assert.Equal(1L, Scalar(connection, "SELECT Shown FROM ExportOrder;"));

            Assert.Equal(4L, Scalar(connection, "SELECT COUNT(*) FROM Attachment;"));
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM Attachment WHERE ResolutionStatus = 'Resolved';"));
            Assert.Equal(3L, Scalar(connection, "SELECT COUNT(*) FROM ExportAttachmentName;"));

            // Exact-name resolution: blorp.jpg resolves, Blorp.JPG (case mismatch) does not.
            Assert.Equal(
                "blorp.jpg",
                Scalar(connection,
                    """
                    SELECT n.FileName FROM ExportAttachmentName n
                    JOIN Attachment a ON a.AttachmentID = n.AttachmentID
                    WHERE a.ResolutionStatus = 'Resolved';
                    """));
            Assert.Equal(
                "Unresolved",
                Scalar(connection,
                    """
                    SELECT a.ResolutionStatus FROM ExportAttachmentName n
                    JOIN Attachment a ON a.AttachmentID = n.AttachmentID
                    WHERE n.FileName = 'Blorp.JPG';
                    """));

            // The media omitted attachment is unresolved and has no name.
            Assert.Equal(
                1L,
                Scalar(connection,
                    """
                    SELECT COUNT(*) FROM Attachment a
                    JOIN Message m ON m.MessageID = a.MessageID
                    WHERE m.MessageContent = '<Media omitted>' AND a.ResolutionStatus = 'Unresolved'
                      AND a.AttachmentID NOT IN (SELECT AttachmentID FROM ExportAttachmentName);
                    """));

            // The resolved file is referenced with its hash and size, under the export folder.
            var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("pixels-one")));
            Assert.Equal(expectedHash, Scalar(connection, "SELECT SHA256 FROM MediaFile;"));
            Assert.Equal(10L, Scalar(connection, "SELECT SizeBytes FROM MediaFile;"));
            Assert.Equal("blorp.jpg", Scalar(connection, "SELECT RelativePath FROM MediaFile;"));
            Assert.Equal(expectedHash, Scalar(connection, "SELECT SHA256 FROM MediaAsset;"));
            Assert.Equal("Image", Scalar(connection, "SELECT MediaType FROM MediaAsset;"));
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM MediaAssetFile;"));
            Assert.Equal(Path.GetFullPath(folder), Scalar(connection, "SELECT RootPath FROM MediaSource;"));

            // The caption stays content; the message text is unchanged.
            Assert.Equal(
                "blorp.jpg (file attached)\na caption",
                Scalar(connection, "SELECT MessageContent FROM Message WHERE SequenceNumber = 2;"));
        }

        [Fact]
        public void Import_LeavesTheExportFolderUntouched()
        {
            using var folders = new SyntheticExportFolders();
            var folder = folders.CreateExport("20161209", MiddleChat, ("blorp.jpg", "pixels-one"));
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);
            var before = Snapshot(folder);

            Import(folders, folder, position: 1, shown: true);

            Assert.Equal(before, Snapshot(folder));
        }

        [Fact]
        public void Import_SupersetResolvesAbsentNamesInTheFallbackFolderOnly()
        {
            using var folders = new SyntheticExportFolders();
            var fallback = folders.CreateExport(
                "20161209",
                "26/03/2016, 10:01 - Alex Example: x\n26/03/2016, 10:02 - Sam Sample: y",
                ("elsewhere.jpg", "pixels-two"),
                ("blorp.jpg", "fallback copy"));
            var superset = folders.CreateExport("20170906", MiddleChat, ("blorp.jpg", "pixels-one"));
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            var result = Import(folders, superset, position: 5, shown: true, fallbackFolder: fallback);

            Assert.Equal(1, result.ResolvedInOwnFolderCount);
            Assert.Equal(1, result.ResolvedInFallbackFolderCount);
            Assert.Equal(1, result.UnresolvedFileAttachedCount);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(folders.OlderExportsPath);

            // Own folder wins: blorp.jpg is the export's own copy, not the fallback's.
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("pixels-one"))),
                Scalar(connection,
                    """
                    SELECT s.SHA256 FROM ExportAttachmentName n
                    JOIN Attachment a ON a.AttachmentID = n.AttachmentID
                    JOIN MediaAsset s ON s.MediaAssetID = a.ResolvedMediaAssetID
                    WHERE n.FileName = 'blorp.jpg';
                    """));
            Assert.Equal(
                Path.GetFullPath(fallback),
                Scalar(connection,
                    """
                    SELECT ms.RootPath FROM MediaFile f
                    JOIN MediaSource ms ON ms.MediaSourceID = f.MediaSourceID
                    WHERE f.FileName = 'elsewhere.jpg';
                    """));
        }

        [Fact]
        public void Import_ChatFilesAreNeverMediaCandidates_InEitherFolder()
        {
            using var folders = new SyntheticExportFolders();
            var fallback = folders.CreateExport("20161209", "26/03/2016, 10:01 - Alex Example: x");
            var superset = folders.CreateExport(
                "20170906",
                $"26/03/2016, 10:01 - Alex Example: {SyntheticExportFolders.ChatFileName} (file attached)\n" +
                "26/03/2016, 10:02 - Sam Sample: y");
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            var result = Import(folders, superset, position: 5, shown: true, fallbackFolder: fallback);

            Assert.Equal(1, result.FileAttachedAttachmentCount);
            Assert.Equal(0, result.ResolvedInOwnFolderCount);
            Assert.Equal(0, result.ResolvedInFallbackFolderCount);
            Assert.Equal(1, result.UnresolvedFileAttachedCount);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(folders.OlderExportsPath);
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM MediaFile;"));
        }

        [Fact]
        public void Import_WithoutFallback_DoesNotLookInOtherFolders()
        {
            using var folders = new SyntheticExportFolders();
            folders.CreateExport("20161209", "26/03/2016, 10:01 - Alex Example: x", ("elsewhere.jpg", "p"));
            var superset = folders.CreateExport("20170906", MiddleChat);
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            var result = Import(folders, superset, position: 5, shown: true);

            Assert.Equal(0, result.ResolvedInOwnFolderCount + result.ResolvedInFallbackFolderCount);
            Assert.Equal(3, result.UnresolvedFileAttachedCount);
        }

        [Fact]
        public void Import_SameFolderAsFallbackForASecondExport_ReusesMediaRows()
        {
            using var folders = new SyntheticExportFolders();
            var inner = folders.CreateExport(
                "20161209",
                "26/03/2016, 10:01 - Alex Example: elsewhere.jpg (file attached)\n26/03/2016, 10:02 - Sam Sample: y",
                ("elsewhere.jpg", "pixels-two"));
            var superset = folders.CreateExport("20170906", MiddleChat);
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, inner, position: 4, shown: false);
            Import(folders, superset, position: 5, shown: true, fallbackFolder: inner);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(folders.OlderExportsPath);
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM MediaSource;"));
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM MediaFile;"));
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM MediaAsset;"));
            Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM Attachment WHERE ResolutionStatus = 'Resolved';"));
        }

        [Fact]
        public void Import_ThirdSender_StopsBeforeWriting()
        {
            using var folders = new SyntheticExportFolders();
            var folder = folders.CreateExport(
                "x",
                "26/03/2016, 10:01 - Alex Example: a\n26/03/2016, 10:02 - Sam Sample: b\n26/03/2016, 10:03 - oldhandle42: c");
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            var exception = Assert.Throws<ArgumentException>(() => Import(folders, folder, 1, true));

            Assert.Contains("3 distinct sender names", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("oldhandle42", exception.Message, StringComparison.Ordinal);
            AssertNothingImported(folders);
        }

        [Fact]
        public void Import_SingleSender_IsRefused()
        {
            using var folders = new SyntheticExportFolders();
            var folder = folders.CreateExport("x", "26/03/2016, 10:01 - Alex Example: a");
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            var exception = Assert.Throws<ArgumentException>(
                () => Import(folders, folder, 1, true, roles: new() { [Alex] = OlderExportRole.Local }));

            Assert.Contains("1 distinct sender names", exception.Message, StringComparison.Ordinal);
            AssertNothingImported(folders);
        }

        public static TheoryData<Dictionary<string, OlderExportRole>> BadRoles => new()
        {
            new Dictionary<string, OlderExportRole> { [Alex] = OlderExportRole.Local },
            new Dictionary<string, OlderExportRole> { [Alex] = OlderExportRole.Local, ["Sam sample"] = OlderExportRole.Other },
            new Dictionary<string, OlderExportRole>
            {
                [Alex] = OlderExportRole.Local, [Sam] = OlderExportRole.Other, ["oldhandle42"] = OlderExportRole.Other,
            },
            new Dictionary<string, OlderExportRole> { [Alex] = OlderExportRole.Local, [Sam] = OlderExportRole.Local },
            new Dictionary<string, OlderExportRole> { [Alex] = OlderExportRole.Local, [Sam] = (OlderExportRole)7 },
        };

        [Theory]
        [MemberData(nameof(BadRoles))]
        public void Import_RolesMissingUnknownOrInvalid_StopBeforeWriting(Dictionary<string, OlderExportRole> roles)
        {
            using var folders = new SyntheticExportFolders();
            var folder = folders.CreateExport("x", MiddleChat);
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            var exception = Assert.Throws<ArgumentException>(() => Import(folders, folder, 1, true, roles: roles));

            Assert.DoesNotContain("Sam", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Alex", exception.Message, StringComparison.Ordinal);
            AssertNothingImported(folders);
        }

        [Fact]
        public void Import_ShownExportOverlappingAShownExport_IsRefused()
        {
            using var folders = new SyntheticExportFolders();
            var superset = folders.CreateExport("20170906", MiddleChat.Replace("08/12/2016", "06/09/2017", StringComparison.Ordinal));
            var inner = folders.CreateExport("20161209", MiddleChat.Replace("zorp wibble", "zorp", StringComparison.Ordinal));
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, superset, position: 5, shown: true);

            var exception = Assert.Throws<InvalidOperationException>(
                () => Import(folders, inner, position: 4, shown: true));

            Assert.Contains("overlaps 1 export", exception.Message, StringComparison.Ordinal);

            using (var connection = WorkspaceDatabase.OpenReadOnlyConnection(folders.OlderExportsPath))
            {
                Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM Conversation;"));
            }

            // Registered as not shown, the same export is accepted as evidence.
            Import(folders, inner, position: 4, shown: false);

            using var after = WorkspaceDatabase.OpenReadOnlyConnection(folders.OlderExportsPath);
            Assert.Equal(2L, Scalar(after, "SELECT COUNT(*) FROM Conversation;"));
            Assert.Equal(0L, Scalar(after, "SELECT Shown FROM ExportOrder WHERE Position = 4;"));
        }

        [Fact]
        public void Import_OverlapWithANotShownExport_IsAllowed()
        {
            using var folders = new SyntheticExportFolders();
            var inner = folders.CreateExport("20161209", MiddleChat);
            var superset = folders.CreateExport("20170906", MiddleChat.Replace("zorp wibble", "zorp", StringComparison.Ordinal));
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, inner, position: 4, shown: false);
            Import(folders, superset, position: 5, shown: true);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(folders.OlderExportsPath);
            Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM Conversation;"));
        }

        [Fact]
        public void Import_AdjacentShownExports_AreAllowed()
        {
            using var folders = new SyntheticExportFolders();
            var earlier = folders.CreateExport(
                "20160326",
                "08/08/2015, 09:00 - Alex Example: a\n25/03/2016, 21:00 - Sam Sample: b");
            var later = folders.CreateExport("20161209", MiddleChat);
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, later, position: 4, shown: true);
            Import(folders, earlier, position: 3, shown: true);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(folders.OlderExportsPath);
            Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM ExportOrder WHERE Shown = 1;"));
        }

        [Fact]
        public void Import_SameChatFileTwice_IsRefused()
        {
            using var folders = new SyntheticExportFolders();
            var folder = folders.CreateExport("20161209", MiddleChat);
            var copy = folders.CreateExport("copy", MiddleChat);
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, folder, position: 4, shown: false);

            var exception = Assert.Throws<InvalidOperationException>(
                () => Import(folders, copy, position: 6, shown: false));

            Assert.Contains("already been imported", exception.Message, StringComparison.Ordinal);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(folders.OlderExportsPath);
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM ImportSource;"));
        }

        [Fact]
        public void Import_TakenPosition_IsRefused()
        {
            using var folders = new SyntheticExportFolders();
            var first = folders.CreateExport("a", "08/08/2015, 09:00 - Alex Example: a\n09/08/2015, 09:00 - Sam Sample: b");
            var second = folders.CreateExport("b", MiddleChat);
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, first, position: 2, shown: true);

            Assert.Throws<InvalidOperationException>(() => Import(folders, second, position: 2, shown: true));
        }

        [Fact]
        public void Import_IntoAMainWorkspace_IsRefusedAndWritesNothing()
        {
            using var folders = new SyntheticExportFolders();
            var folder = folders.CreateExport("x", MiddleChat);
            WorkspaceDatabase.Initialise(folders.OlderExportsPath);

            Assert.Throws<InvalidOperationException>(() => Import(folders, folder, 1, true));

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(folders.OlderExportsPath);
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM Message;"));
            Assert.DoesNotContain("ExportOrder", WorkspaceDatabase.ReadApplicationTableNames(connection));
        }

        [Fact]
        public void Import_FailureAfterRowsWereWritten_RollsBackEverything()
        {
            using var folders = new SyntheticExportFolders();
            var folder = folders.CreateExport("20161209", MiddleChat, ("blorp.jpg", "pixels-one"));
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            // Holding the media file open exclusively makes hashing it fail, which happens after
            // the conversation, messages, roles and order rows have been written.
            string message;

            using (new FileStream(Path.Combine(folder, "blorp.jpg"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                message = Assert.Throws<InvalidOperationException>(() => Import(folders, folder, 1, true)).Message;
            }

            Assert.Contains("rolled back", message, StringComparison.Ordinal);
            Assert.DoesNotContain("blorp", message, StringComparison.Ordinal);
            Assert.DoesNotContain(folders.RootPath, message, StringComparison.OrdinalIgnoreCase);
            AssertNothingImported(folders);
        }

        [Fact]
        public void Import_RequiresAnExistingFallbackFolderOtherThanItsOwn()
        {
            using var folders = new SyntheticExportFolders();
            var folder = folders.CreateExport("x", MiddleChat);
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Assert.Throws<ArgumentException>(() => Import(folders, folder, 1, true, fallbackFolder: folder));
            Assert.Throws<ArgumentException>(
                () => Import(folders, folder, 1, true, fallbackFolder: Path.Combine(folders.RootPath, "nowhere")));
            AssertNothingImported(folders);
        }

        private static OlderExportImportResult Import(
            SyntheticExportFolders folders,
            string folder,
            int position,
            bool shown,
            string? fallbackFolder = null,
            Dictionary<string, OlderExportRole>? roles = null) =>
            new OlderExportImporter().Import(
                folders.OlderExportsPath,
                new OlderExportImportRequest
                {
                    Export = OlderExportSource.Load(folder),
                    Roles = roles ?? new Dictionary<string, OlderExportRole>
                    {
                        [Alex] = OlderExportRole.Local,
                        [Sam] = OlderExportRole.Other,
                    },
                    Position = position,
                    Shown = shown,
                    FallbackMediaFolder = fallbackFolder,
                    ImportedDateTimeUtc = Now,
                });

        private static void AssertNothingImported(SyntheticExportFolders folders)
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(folders.OlderExportsPath);

            foreach (var table in new[]
            {
                "ImportSource", "Conversation", "Participant", "ConversationParticipant", "Message",
                "Attachment", "MediaSource", "MediaFile", "MediaAsset", "MediaAssetFile",
                "ExportRole", "ExportOrder", "ExportAttachmentName",
            })
            {
                Assert.Equal(0L, Scalar(connection, $"SELECT COUNT(*) FROM {table};"));
            }
        }

        private static object? Scalar(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            return command.ExecuteScalar();
        }

        private static List<string> Snapshot(string folder) =>
            Directory.EnumerateFiles(folder)
                .Order(StringComparer.Ordinal)
                .Select(path => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Path.GetFileName(path)}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}"))
                .ToList();
    }
}
