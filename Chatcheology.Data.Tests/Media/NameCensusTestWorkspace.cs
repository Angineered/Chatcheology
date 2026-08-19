using System.Globalization;
using Chatcheology.Data.Media;
using Chatcheology.Data.Tests.Workspace;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Media
{
    /// <summary>
    /// A synthetic schema-v2 workspace holding media rows only, for the file name census.
    /// </summary>
    /// <remarks>
    /// Every name here is invented. No recovered file name, path or hash from a real archive appears
    /// in this project.
    /// <para>
    /// <c>FileDate</c> and <c>Extension</c> default to whatever the committed Phase 5 classifier
    /// would have written for the name, so a fixture cannot accidentally describe a workspace that
    /// Phase 5 could never have produced. Both can be overridden, which is how the deliberately
    /// inconsistent rows are built.
    /// </para>
    /// </remarks>
    internal sealed class NameCensusTestWorkspace : IDisposable
    {
        /// <summary>Passed where a test means "leave the default derivation alone".</summary>
        internal const string Derive = "\u0000derive";

        private const string ImportedDateTimeUtcText = "2026-08-19T08:00:00.0000000Z";

        private readonly TemporaryWorkspaceDatabase _workspace = new();
        private readonly Dictionary<long, string> _sourceTypes = [];

        private SqliteConnection? _connection;
        private int _fileNumber;

        internal NameCensusTestWorkspace()
        {
            WorkspaceDatabase.Initialise(_workspace.DatabasePath);

            _connection = WorkspaceDatabase.OpenConnection(_workspace.DatabasePath);
        }

        internal string DatabasePath => _workspace.DatabasePath;

        internal string DirectoryPath => _workspace.DirectoryPath;

        internal long AddMediaSource(
            string sourceType = MediaSourceTypes.WhatsAppMediaDirectory,
            string displayName = "Synthetic source")
        {
            Execute(
                $"""
                INSERT INTO MediaSource (DisplayName, SourceType, RootPath, ImportedDateTimeUtc)
                VALUES ('{displayName}', '{sourceType}', 'MediaRoot/{displayName}',
                        '{ImportedDateTimeUtcText}');
                """);

            var mediaSourceID = ScalarLong("SELECT MAX(MediaSourceID) FROM MediaSource;");
            _sourceTypes[mediaSourceID] = sourceType;

            return mediaSourceID;
        }

        internal long AddMediaAsset(string sha256, long sizeBytes = 1024)
        {
            Execute(
                $"""
                INSERT INTO MediaAsset (SHA256, MediaType, SizeBytes)
                VALUES ('{sha256}', 'Image', {sizeBytes});
                """);

            return ScalarLong("SELECT MAX(MediaAssetID) FROM MediaAsset;");
        }

        /// <summary>
        /// Adds one physical file carrying <paramref name="fileName"/> and links it to its asset.
        /// </summary>
        /// <param name="fileDate">
        /// <see cref="Derive"/> to store what the committed classifier derives, null to store no
        /// date, or explicit text to store something else.
        /// </param>
        /// <param name="extension">
        /// <see cref="Derive"/> to store what the committed classifier derives, null to store none,
        /// or explicit text to store something the name does not actually end with.
        /// </param>
        internal long AddMediaFile(
            long mediaSourceID,
            long? mediaAssetID,
            string sha256,
            string fileName,
            string? fileDate = Derive,
            string? extension = Derive,
            long sizeBytes = 1024,
            string? storedSHA256 = null,
            bool link = true)
        {
            _fileNumber++;

            var derivedDate = MediaClassification.DeriveFileDate(
                _sourceTypes[mediaSourceID], fileName);

            var storedDate = ReferenceEquals(fileDate, Derive)
                ? derivedDate is { } date
                    ? $"'{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'"
                    : "NULL"
                : fileDate is null ? "NULL" : $"'{fileDate}'";

            var derivedExtension = MediaClassification.NormaliseExtension(fileName);

            var storedExtension = ReferenceEquals(extension, Derive)
                ? derivedExtension is null ? "NULL" : $"'{derivedExtension}'"
                : extension is null ? "NULL" : $"'{extension}'";

            var hash = storedSHA256 is null
                ? $"'{sha256}'"
                : storedSHA256.Length == 0 ? "NULL" : $"'{storedSHA256}'";

            Execute(
                $"""
                INSERT INTO MediaFile (
                    MediaSourceID, RelativePath, FileName, Extension, SizeBytes, SHA256,
                    MediaType, FileDate, IsSent)
                VALUES (
                    {mediaSourceID}, 'folder/{_fileNumber}-{fileName}', '{fileName}',
                    {storedExtension}, {sizeBytes}, {hash}, 'Image', {storedDate}, NULL);
                """);

            var mediaFileID = ScalarLong("SELECT MAX(MediaFileID) FROM MediaFile;");

            if (link && mediaAssetID is { } assetID)
            {
                Execute(
                    $"""
                    INSERT INTO MediaAssetFile (MediaAssetID, MediaFileID)
                    VALUES ({assetID}, {mediaFileID});
                    """);
            }

            return mediaFileID;
        }

        /// <summary>Adds an asset with one file carrying <paramref name="fileName"/>.</summary>
        internal long AddAssetWithFile(
            long mediaSourceID, string sha256, string fileName, long sizeBytes = 1024)
        {
            var mediaAssetID = AddMediaAsset(sha256, sizeBytes);

            AddMediaFile(mediaSourceID, mediaAssetID, sha256, fileName, sizeBytes: sizeBytes);

            return mediaAssetID;
        }

        /// <summary>
        /// Replaces <c>MediaAssetFile</c> with a copy carrying no unique constraint.
        /// </summary>
        /// <remarks>
        /// Schema v2 makes one file's second asset link unrepresentable, so a workspace this code
        /// wrote can never hold one. The census still has to refuse that shape rather than count a
        /// physical file twice, and this is the only way to build it: a table as some other tool
        /// might have created it, in a database that still reports schema version 2.
        /// </remarks>
        internal void RemoveAssetLinkUniqueConstraint()
        {
            Execute(
                """
                CREATE TABLE MediaAssetFileRebuilt (
                    MediaAssetFileID INTEGER PRIMARY KEY,
                    MediaAssetID     INTEGER NOT NULL,
                    MediaFileID      INTEGER NOT NULL
                );

                INSERT INTO MediaAssetFileRebuilt (MediaAssetFileID, MediaAssetID, MediaFileID)
                SELECT MediaAssetFileID, MediaAssetID, MediaFileID FROM MediaAssetFile;

                DROP TABLE MediaAssetFile;

                ALTER TABLE MediaAssetFileRebuilt RENAME TO MediaAssetFile;
                """);
        }

        /// <summary>Runs SQL with foreign keys turned off, to build a state the schema forbids.</summary>
        internal void ExecuteWithoutForeignKeys(string sql)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                ForeignKeys = false,
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        internal void Execute(string sql)
        {
            using var command = RequireConnection().CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        internal long ScalarLong(string sql)
        {
            using var command = RequireConnection().CreateCommand();
            command.CommandText = sql;

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        internal long ScalarLongReadOnly(string sql)
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Closes the building connection and clears the pool, so the file can be hashed or deleted
        /// without a live handle on it.
        /// </summary>
        internal void CloseBuildingConnection()
        {
            _connection?.Dispose();
            _connection = null;

            SqliteConnection.ClearAllPools();
        }

        public void Dispose()
        {
            _connection?.Dispose();
            _connection = null;

            _workspace.Dispose();
        }

        private SqliteConnection RequireConnection() =>
            _connection
            ?? throw new InvalidOperationException(
                "The building connection has been closed. Build the workspace before closing it.");
    }
}
