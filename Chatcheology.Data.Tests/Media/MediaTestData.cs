using Chatcheology.Data.Media;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Media
{
    /// <summary>
    /// Shared fixtures and small query helpers for the media inventory and hashing tests.
    /// </summary>
    /// <remarks>
    /// Every value here is fictional. No real media, file name, path or device is referenced
    /// anywhere in this project.
    /// </remarks>
    internal static class MediaTestData
    {
        internal const string DisplayName = "Synthetic media root";

        internal const string DeviceDescription = "Synthetic test device";

        /// <summary>
        /// A source type this build recognises no conventions for, used to prove that direction and
        /// naming-derived dates are read only where they are known to mean something.
        /// </summary>
        internal const string GenericSourceType = "GenericMediaDirectory";

        /// <summary>
        /// The fixed UTC instant every test supplies as the registration time, so no test reads the
        /// clock.
        /// </summary>
        internal static readonly DateTime ImportedDateTimeUtc =
            new(2026, 8, 18, 9, 30, 0, DateTimeKind.Utc);

        internal static MediaSourceRequest CreateRequest(
            string rootPath,
            string sourceType = MediaSourceTypes.WhatsAppMediaDirectory,
            string displayName = DisplayName) =>
            new()
            {
                DisplayName = displayName,
                SourceType = sourceType,
                RootPath = rootPath,
                DeviceDescription = DeviceDescription,
                ImportedDateTimeUtc = ImportedDateTimeUtc,
            };

        /// <summary>Creates an initialised current-schema workspace and returns its path.</summary>
        internal static string CreateWorkspace(
            Workspace.TemporaryWorkspaceDatabase workspace)
        {
            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            return workspace.DatabasePath;
        }

        internal static long CountRows(SqliteConnection connection, string tableName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {tableName};";

            return (long)command.ExecuteScalar()!;
        }

        /// <summary>
        /// Reads every <c>MediaFile</c> row, in the order hashing would process them.
        /// </summary>
        /// <remarks>
        /// A list rather than a dictionary keyed by relative path, because two sources may
        /// legitimately hold the same relative path — two phones each with their own copy of one
        /// picture is the ordinary case, not a corner one — and keying by it would silently merge
        /// the two rows and hide exactly the duplication these tests exist to check.
        /// </remarks>
        internal static List<MediaFileRow> ReadMediaFiles(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    MediaFileID,
                    MediaSourceID,
                    RelativePath,
                    FileName,
                    Extension,
                    SizeBytes,
                    SHA256,
                    MediaType,
                    FileDate,
                    IsSent,
                    DurationMS,
                    Width,
                    Height
                FROM MediaFile
                ORDER BY MediaSourceID, RelativePath;
                """;

            var rows = new List<MediaFileRow>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                rows.Add(new MediaFileRow
                {
                    MediaFileID = reader.GetInt64(0),
                    MediaSourceID = reader.GetInt64(1),
                    RelativePath = reader.GetString(2),
                    FileName = reader.GetString(3),
                    Extension = reader.IsDBNull(4) ? null : reader.GetString(4),
                    SizeBytes = reader.GetInt64(5),
                    SHA256 = reader.IsDBNull(6) ? null : reader.GetString(6),
                    MediaType = reader.GetString(7),
                    FileDate = reader.IsDBNull(8) ? null : reader.GetString(8),
                    IsSent = reader.IsDBNull(9) ? null : reader.GetInt64(9) == 1,
                    HasDurationMS = !reader.IsDBNull(10),
                    HasWidth = !reader.IsDBNull(11),
                    HasHeight = !reader.IsDBNull(12),
                });
            }

            return rows;
        }

        /// <summary>
        /// Reads every <c>MediaFile</c> row keyed by relative path, for tests with one source.
        /// </summary>
        /// <remarks>
        /// Throws if two rows share a path, so a test that quietly grew a second source cannot go
        /// on silently asserting against whichever of the two happened to be read last.
        /// </remarks>
        internal static Dictionary<string, MediaFileRow> ReadMediaFilesByPath(
            SqliteConnection connection) =>
            ReadMediaFiles(connection).ToDictionary(
                row => row.RelativePath, StringComparer.Ordinal);

        /// <summary>Reads every <c>MediaAsset</c> row, keyed by its hash.</summary>
        internal static Dictionary<string, MediaAssetRow> ReadMediaAssets(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT MediaAssetID, SHA256, MediaType, SizeBytes FROM MediaAsset ORDER BY MediaAssetID;";

            var rows = new Dictionary<string, MediaAssetRow>(StringComparer.OrdinalIgnoreCase);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var row = new MediaAssetRow
                {
                    MediaAssetID = reader.GetInt64(0),
                    SHA256 = reader.GetString(1),
                    MediaType = reader.GetString(2),
                    SizeBytes = reader.GetInt64(3),
                };

                rows[row.SHA256] = row;
            }

            return rows;
        }

        /// <summary>
        /// The <c>MediaAssetID</c> each hashed <c>MediaFileID</c> is linked to.
        /// </summary>
        internal static Dictionary<long, long> ReadAssetLinks(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MediaFileID, MediaAssetID FROM MediaAssetFile;";

            var links = new Dictionary<long, long>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                links[reader.GetInt64(0)] = reader.GetInt64(1);
            }

            return links;
        }

        /// <summary>
        /// Asserts the invariant that makes a hashed workspace trustworthy: a file has a hash if and
        /// only if it is linked to an asset carrying that same hash.
        /// </summary>
        /// <remarks>
        /// Checked after every hashing test rather than only where it is the subject, because it is
        /// the one property an interruption at the wrong moment could break, and an interruption can
        /// happen anywhere.
        /// </remarks>
        internal static void AssertHashLinkInvariant(SqliteConnection connection)
        {
            var files = ReadMediaFiles(connection);
            var assets = ReadMediaAssets(connection);
            var links = ReadAssetLinks(connection);

            var assetsByID = assets.Values.ToDictionary(asset => asset.MediaAssetID);

            foreach (var file in files)
            {
                if (file.SHA256 is null)
                {
                    Assert.False(
                        links.ContainsKey(file.MediaFileID),
                        "An unhashed media file is linked to an asset.");

                    continue;
                }

                Assert.True(
                    links.TryGetValue(file.MediaFileID, out var mediaAssetID),
                    "A hashed media file has no asset link.");

                var asset = assetsByID[mediaAssetID];

                Assert.Equal(file.SHA256, asset.SHA256, ignoreCase: true);
                Assert.Equal(file.SizeBytes, asset.SizeBytes);
            }

            Assert.Equal(files.Count(file => file.SHA256 is not null), links.Count);
        }

        internal sealed class MediaFileRow
        {
            internal required long MediaFileID { get; init; }

            internal required long MediaSourceID { get; init; }

            internal required string RelativePath { get; init; }

            internal required string FileName { get; init; }

            internal required string? Extension { get; init; }

            internal required long SizeBytes { get; init; }

            internal required string? SHA256 { get; init; }

            internal required string MediaType { get; init; }

            internal required string? FileDate { get; init; }

            internal required bool? IsSent { get; init; }

            internal required bool HasDurationMS { get; init; }

            internal required bool HasWidth { get; init; }

            internal required bool HasHeight { get; init; }
        }

        internal sealed class MediaAssetRow
        {
            internal required long MediaAssetID { get; init; }

            internal required string SHA256 { get; init; }

            internal required string MediaType { get; init; }

            internal required long SizeBytes { get; init; }
        }
    }
}
