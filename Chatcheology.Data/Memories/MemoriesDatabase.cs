using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Memories
{
    /// <summary>
    /// Creates and opens the memories file: a SQLite database of its own, kept beside the main
    /// workspace under the fixed name <see cref="FileName"/>, that holds collections of dated
    /// entries (imported activity and personal memories). Images live in
    /// <see cref="MediaFolderName"/> beside it, one file per SHA-256.
    /// </summary>
    /// <remarks>
    /// The file is not a workspace: it carries none of the workspace tables and the main workspace
    /// schema is not touched. Every write path opens through <see cref="OpenConnection"/>, which
    /// refuses any database without exactly one valid <c>MemoriesInfo</c> marker row, and refuses a
    /// main workspace or an older-exports file by name before that. <see cref="Create"/> never
    /// takes over an existing file.
    /// </remarks>
    public static class MemoriesDatabase
    {
        /// <summary>The fixed file name, in the same folder as the main workspace.</summary>
        public const string FileName = "memories.db";

        /// <summary>The folder beside the memories file holding images as <c>&lt;sha256&gt;.&lt;ext&gt;</c>.</summary>
        public const string MediaFolderName = "memories-media";

        /// <summary>The value of <c>MemoriesInfo.Purpose</c> in every memories file.</summary>
        internal const string MarkerPurpose = "Memories";

        /// <summary>The memories table layout this build creates and accepts.</summary>
        internal const int MarkerFormatVersion = 1;

        /// <summary>Collection kinds.</summary>
        public const string KindImported = "Imported";

        /// <summary>Collection kinds.</summary>
        public const string KindPersonal = "Personal";

        /// <summary>Entry date precisions.</summary>
        public const string PrecisionSecond = "Second";

        /// <summary>Entry date precisions.</summary>
        public const string PrecisionDay = "Day";

        /// <summary>Entry date precisions.</summary>
        public const string PrecisionMonth = "Month";

        /// <summary>The tables of this file, in creation order.</summary>
        internal static readonly string[] TableNames =
        [
            "MemoriesInfo",
            "MediaFile",
            "Collection",
            "Entry",
            "EntryPeriod",
            "EntryItem",
            "EntryLink",
        ];

        // GLOB patterns that pin the stored spellings: local wall clock without zone, UTC with Z.
        private const string LocalGlob =
            "'[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9]'";

        private const string UtcGlob =
            "'[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9]Z'";

        private const string Sha256Glob =
            "'[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]" +
            "[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]" +
            "[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]" +
            "[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]" +
            "[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]" +
            "[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]" +
            "[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]" +
            "[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]'";

        private static readonly string[] TableStatements =
        [
            $"""
            CREATE TABLE MemoriesInfo (
                MemoriesInfoID     INTEGER PRIMARY KEY,
                Purpose            TEXT NOT NULL,
                FormatVersion      INTEGER NOT NULL,
                CreatedDateTimeUtc TEXT NOT NULL,

                CHECK (MemoriesInfoID = 1),
                CHECK (Purpose = '{MarkerPurpose}'),
                CHECK (FormatVersion = {MarkerFormatVersion}),
                CHECK (CreatedDateTimeUtc GLOB {UtcGlob})
            );
            """,

            $"""
            CREATE TABLE MediaFile (
                Sha256    TEXT PRIMARY KEY,
                Extension TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,

                CHECK (Sha256 GLOB {Sha256Glob}),
                CHECK (Extension IN ('jpg', 'png', 'gif', 'webp')),
                CHECK (SizeBytes > 0)
            );
            """,

            $"""
            CREATE TABLE Collection (
                CollectionID      INTEGER PRIMARY KEY,
                Kind              TEXT NOT NULL,
                Name              TEXT NOT NULL,
                SourceDescription TEXT NULL,
                ShownByDefault    INTEGER NOT NULL,
                PeriodGapMinutes  INTEGER NULL,

                UNIQUE (Kind, Name),

                CHECK (Kind IN ('{KindImported}', '{KindPersonal}')),
                CHECK (length(trim(Name)) > 0),
                CHECK (ShownByDefault IN (0, 1)),
                CHECK ((Kind = '{KindImported}' AND PeriodGapMinutes IS NOT NULL AND PeriodGapMinutes > 0)
                    OR (Kind = '{KindPersonal}' AND PeriodGapMinutes IS NULL))
            );
            """,

            $"""
            CREATE TABLE Entry (
                EntryID        INTEGER PRIMARY KEY,
                CollectionID   INTEGER NOT NULL REFERENCES Collection (CollectionID),
                SourceKey      TEXT NULL,
                Title          TEXT NOT NULL,
                StartLocal     TEXT NOT NULL,
                EndLocal       TEXT NULL,
                StartUtc       TEXT NULL,
                EndUtc         TEXT NULL,
                DatePrecision  TEXT NOT NULL,
                Summary        TEXT NULL,
                ImageSha256    TEXT NULL REFERENCES MediaFile (Sha256),
                ImageSourceUrl TEXT NULL,
                Source         TEXT NULL,
                DayLevel       TEXT NULL,

                UNIQUE (CollectionID, SourceKey),

                CHECK (length(trim(Title)) > 0),
                CHECK (SourceKey IS NULL OR length(SourceKey) > 0),
                CHECK (DatePrecision IN ('{PrecisionSecond}', '{PrecisionDay}', '{PrecisionMonth}')),
                CHECK (StartLocal GLOB {LocalGlob}),
                CHECK (EndLocal IS NULL OR (EndLocal GLOB {LocalGlob} AND EndLocal >= StartLocal)),
                CHECK (StartUtc IS NULL OR StartUtc GLOB {UtcGlob}),
                CHECK (EndUtc IS NULL OR (EndUtc GLOB {UtcGlob} AND StartUtc IS NOT NULL AND EndUtc >= StartUtc))
            );
            """,

            $"""
            CREATE TABLE EntryPeriod (
                EntryID        INTEGER NOT NULL REFERENCES Entry (EntryID),
                PeriodIndex    INTEGER NOT NULL,
                StartUtc       TEXT NOT NULL,
                EndUtc         TEXT NOT NULL,
                StartLocal     TEXT NOT NULL,
                EndLocal       TEXT NOT NULL,
                Label          TEXT NOT NULL,
                LocalItemCount INTEGER NOT NULL,
                OtherItemCount INTEGER NOT NULL,

                PRIMARY KEY (EntryID, PeriodIndex),

                CHECK (PeriodIndex >= 0),
                CHECK (StartUtc GLOB {UtcGlob} AND EndUtc GLOB {UtcGlob} AND EndUtc >= StartUtc),
                CHECK (StartLocal GLOB {LocalGlob} AND EndLocal GLOB {LocalGlob}),
                CHECK (Label IN ('{PeriodLabels.Strong}', '{PeriodLabels.Supporting}', '{PeriodLabels.SamePeriod}', '{PeriodLabels.OneAccount}')),
                CHECK (LocalItemCount >= 0 AND OtherItemCount >= 0 AND LocalItemCount + OtherItemCount > 0),
                CHECK ((Label = '{PeriodLabels.OneAccount}') = (LocalItemCount = 0 OR OtherItemCount = 0))
            );
            """,

            $"""
            CREATE TABLE EntryItem (
                EntryItemID INTEGER PRIMARY KEY,
                EntryID     INTEGER NOT NULL,
                PeriodIndex INTEGER NOT NULL,
                Role        TEXT NOT NULL,
                SetId       TEXT NOT NULL,
                ItemId      TEXT NOT NULL,
                ItemType    TEXT NOT NULL,
                TimeUtc     TEXT NOT NULL,
                TimeLocal   TEXT NOT NULL,
                Name        TEXT NULL,
                IconSha256  TEXT NULL REFERENCES MediaFile (Sha256),

                FOREIGN KEY (EntryID, PeriodIndex) REFERENCES EntryPeriod (EntryID, PeriodIndex),

                CHECK (Role IN ('{MemoryRoles.Local}', '{MemoryRoles.Other}')),
                CHECK (length(SetId) > 0 AND length(ItemId) > 0 AND length(ItemType) > 0),
                CHECK (TimeUtc GLOB {UtcGlob}),
                CHECK (TimeLocal GLOB {LocalGlob})
            );
            """,

            """
            CREATE TABLE EntryLink (
                EntryLinkID             INTEGER PRIMARY KEY,
                FromEntryID             INTEGER NOT NULL REFERENCES Entry (EntryID),
                ToEntryID               INTEGER NULL REFERENCES Entry (EntryID),
                ToMessageSequenceNumber INTEGER NULL,

                CHECK ((ToEntryID IS NULL) <> (ToMessageSequenceNumber IS NULL)),
                CHECK (ToMessageSequenceNumber IS NULL OR ToMessageSequenceNumber > 0),
                CHECK (ToEntryID IS NULL OR ToEntryID <> FromEntryID)
            );
            """,

            // A link starts at a personal entry only; CHECK cannot look across tables.
            $"""
            CREATE TRIGGER EntryLink_FromPersonalOnly_Insert
            BEFORE INSERT ON EntryLink
            WHEN (SELECT c.Kind FROM Entry e JOIN Collection c ON c.CollectionID = e.CollectionID
                  WHERE e.EntryID = NEW.FromEntryID) IS NOT '{KindPersonal}'
            BEGIN
                SELECT RAISE(ABORT, 'A link must start at a personal entry.');
            END;
            """,

            $"""
            CREATE TRIGGER EntryLink_FromPersonalOnly_Update
            BEFORE UPDATE OF FromEntryID ON EntryLink
            WHEN (SELECT c.Kind FROM Entry e JOIN Collection c ON c.CollectionID = e.CollectionID
                  WHERE e.EntryID = NEW.FromEntryID) IS NOT '{KindPersonal}'
            BEGIN
                SELECT RAISE(ABORT, 'A link must start at a personal entry.');
            END;
            """,

            "CREATE INDEX IX_Entry_Collection ON Entry (CollectionID);",
            "CREATE INDEX IX_Entry_ImageSourceUrl ON Entry (ImageSourceUrl);",
            "CREATE INDEX IX_EntryItem_Entry ON EntryItem (EntryID, PeriodIndex);",
        ];

        /// <summary>The memories file path beside a main workspace.</summary>
        public static string PathBeside(string mainWorkspacePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(mainWorkspacePath);

            var folder = Path.GetDirectoryName(Path.GetFullPath(mainWorkspacePath))
                ?? throw new ArgumentException(
                    "The main workspace path has no containing folder.", nameof(mainWorkspacePath));

            return Path.Combine(folder, FileName);
        }

        /// <summary>The image folder beside a memories file.</summary>
        public static string MediaFolderFor(string memoriesPath)
        {
            RequireFixedFileName(memoriesPath);

            var folder = Path.GetDirectoryName(Path.GetFullPath(memoriesPath))
                ?? throw new ArgumentException("The memories path has no containing folder.", nameof(memoriesPath));

            return Path.Combine(folder, MediaFolderName);
        }

        /// <summary>
        /// Creates a new memories file: every table and the marker row, in one transaction.
        /// </summary>
        /// <exception cref="ArgumentException">Wrong file name, or a timestamp that is not UTC.</exception>
        /// <exception cref="InvalidOperationException">A file already exists there. Nothing is written.</exception>
        public static void Create(string databasePath, DateTime createdDateTimeUtc)
        {
            RequireFixedFileName(databasePath);

            if (createdDateTimeUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("The creation timestamp must be DateTimeKind.Utc.", nameof(createdDateTimeUtc));
            }

            if (File.Exists(databasePath) || Directory.Exists(databasePath))
            {
                throw new InvalidOperationException(
                    "A file already exists at the memories path. Creating never takes over an existing " +
                    "file, so nothing has been written.");
            }

            try
            {
                using var connection = WorkspaceDatabase.OpenConnection(databasePath);
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;

                foreach (var statement in TableStatements)
                {
                    command.CommandText = statement;
                    command.ExecuteNonQuery();
                }

                command.CommandText =
                    """
                    INSERT INTO MemoriesInfo (MemoriesInfoID, Purpose, FormatVersion, CreatedDateTimeUtc)
                    VALUES (1, $purpose, $formatVersion, $created);
                    """;
                command.Parameters.AddWithValue("$purpose", MarkerPurpose);
                command.Parameters.AddWithValue("$formatVersion", MarkerFormatVersion);
                command.Parameters.AddWithValue("$created", FormatUtc(createdDateTimeUtc));
                command.ExecuteNonQuery();

                transaction.Commit();
            }
            catch
            {
                // The file did not exist before this call, so removing it restores what was found.
                SqliteConnection.ClearAllPools();

                if (File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }

                throw;
            }
        }

        /// <summary>Opens an existing memories file for writing, after proving it is one.</summary>
        /// <returns>An open connection with foreign keys enforced. The caller disposes it.</returns>
        /// <exception cref="ArgumentException">The file name is not <see cref="FileName"/>.</exception>
        /// <exception cref="FileNotFoundException">There is no file. Nothing is created.</exception>
        /// <exception cref="InvalidOperationException">It is not a memories file. Nothing is written.</exception>
        public static SqliteConnection OpenConnection(string databasePath)
        {
            RequireFixedFileName(databasePath);

            if (!File.Exists(databasePath))
            {
                throw new FileNotFoundException(
                    "There is no memories file at the supplied path. Opening never creates one; use " +
                    "MemoriesDatabase.Create. No file has been created.");
            }

            var connection = WorkspaceDatabase.OpenConnection(databasePath);

            try
            {
                RequireMemoriesFile(connection);
            }
            catch
            {
                connection.Dispose();
                throw;
            }

            return connection;
        }

        /// <summary>
        /// Throws unless the connection is open on a memories file: every table and exactly one
        /// valid marker row. A main workspace or an older-exports file is refused by name.
        /// </summary>
        internal static void RequireMemoriesFile(SqliteConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);

            var tables = WorkspaceDatabase.ReadApplicationTableNames(connection);

            if (tables.Contains("OlderExportsInfo"))
            {
                throw new InvalidOperationException(
                    "The database is an older-exports file, not a memories file. Nothing has been written.");
            }

            if (tables.Contains("Message") || tables.Contains("Conversation"))
            {
                throw new InvalidOperationException(
                    "The database is a workspace, not a memories file. A main workspace is never opened " +
                    "or written as the memories file. Nothing has been written.");
            }

            if (!TableNames.All(tables.Contains))
            {
                throw new InvalidOperationException(
                    "The database does not carry the memories tables, so it is not treated as a " +
                    "memories file. Nothing has been written.");
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    (SELECT COUNT(*) FROM MemoriesInfo),
                    (SELECT COUNT(*) FROM MemoriesInfo
                     WHERE MemoriesInfoID = 1 AND Purpose = $purpose AND FormatVersion = $formatVersion);
                """;
            command.Parameters.AddWithValue("$purpose", MarkerPurpose);
            command.Parameters.AddWithValue("$formatVersion", MarkerFormatVersion);

            using var reader = command.ExecuteReader();
            reader.Read();
            var allRows = reader.GetInt64(0);
            var validRows = reader.GetInt64(1);

            if (allRows != 1 || validRows != 1)
            {
                throw new InvalidOperationException(
                    "The database does not carry exactly one valid memories marker row (format version " +
                    $"{MarkerFormatVersion.ToString(CultureInfo.InvariantCulture)}), so it is not " +
                    "treated as a memories file. Nothing has been written.");
            }
        }

        /// <summary>A UTC moment in the stored spelling (whole seconds, trailing Z).</summary>
        internal static string FormatUtc(DateTime value) =>
            value.ToString(ArchiveFormat.UtcDateTimeFormat, CultureInfo.InvariantCulture);

        /// <summary>A local wall-clock reading in the stored spelling (no zone, no fraction).</summary>
        internal static string FormatLocal(DateTime value) =>
            value.ToString(ArchiveFormat.LocalDateTimeFormat, CultureInfo.InvariantCulture);

        private static void RequireFixedFileName(string databasePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

            if (!string.Equals(Path.GetFileName(databasePath), FileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"The memories file must be named '{FileName}'. Any other name is refused so that the " +
                    "main workspace or the older-exports file cannot be mistaken for it.",
                    nameof(databasePath));
            }
        }
    }

    /// <summary>The two chat roles an account or a participant is mapped to.</summary>
    public static class MemoryRoles
    {
        /// <summary>The local participant.</summary>
        public const string Local = "Local";

        /// <summary>The other participant.</summary>
        public const string Other = "Other";
    }

    /// <summary>Period labels, exactly as the CH-42 design table names them.</summary>
    public static class PeriodLabels
    {
        /// <summary>Holds a source matching pair at most 60 s apart.</summary>
        public const string Strong = "strong";

        /// <summary>Holds a source matching pair, none within 60 s.</summary>
        public const string Supporting = "supporting";

        /// <summary>Both accounts earned trophies in it, no matching pair.</summary>
        public const string SamePeriod = "same-period";

        /// <summary>Only one account earned trophies in it.</summary>
        public const string OneAccount = "one-account";

        /// <summary>All four, in table order.</summary>
        public static IReadOnlyList<string> All { get; } = [Strong, Supporting, SamePeriod, OneAccount];
    }
}
