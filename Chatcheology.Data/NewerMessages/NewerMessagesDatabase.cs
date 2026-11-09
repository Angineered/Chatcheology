using System.Globalization;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.NewerMessages
{
    /// <summary>
    /// The newer-messages file (CH-71 step 3, D1): messages of the pinned conversation that are newer
    /// than the main workspace, read from the WhatsApp msgstore and kept beside the main workspace under
    /// the fixed name <see cref="FileName"/>, the way <c>older-exports.db</c> sits there for older history.
    /// </summary>
    /// <remarks>
    /// <para>Not a workspace: its own tables, its own marker row (<c>NewerMessagesInfo</c>) and its own
    /// <c>user_version</c> (<see cref="UserVersion"/>, distinct from every workspace schema version and
    /// from the memories file's), so neither the main workspace nor any other database can be opened as
    /// this file, and this file can never be opened as a workspace.</para>
    /// <para><strong>Append only.</strong> Rows are only ever inserted, by <see cref="NewerMessagesWriter"/>,
    /// one refresh run per transaction. The only update is inside that same transaction: the run's own new
    /// attachment rows get their low-quality link set after all of them are inserted. No row of an earlier
    /// run is ever updated, and nothing is deleted, so every identifier and every state stays what it was
    /// when its run committed (CH-71 D2 and D7: the phone keeps stars by sequence number and attachment id).</para>
    /// <para><strong>Message identity</strong> is (<c>FromMe</c>, <c>KeyID</c>), as in WhatsApp: the same key_id
    /// may occur once in each direction.</para>
    /// <para>No Phase 7 table, no decision and no review lives here (D4): an attachment is Restored only by
    /// an exact SHA-256 match with msgstore's <c>file_hash</c>, else Unmatched; never Missing.</para>
    /// </remarks>
    public static class NewerMessagesDatabase
    {
        /// <summary>The fixed file name, in the same folder as the main workspace.</summary>
        public const string FileName = "newer-messages.db";

        /// <summary>The value of <c>NewerMessagesInfo.Purpose</c>.</summary>
        internal const string MarkerPurpose = "NewerMessages";

        /// <summary>The table layout this build creates and accepts.</summary>
        public const int FormatVersion = 1;

        /// <summary>
        /// The SQLite <c>user_version</c> of a newer-messages file. Distinct from every workspace schema
        /// version (1-6) and from the memories file's (4201).
        /// </summary>
        internal const int UserVersion = 7101;

        /// <summary>The time zone every local time in this file is computed in (Angie, 2026-11-08).</summary>
        public const string TimeZoneName = NewerMessagesTimeZone.Name;

        /// <summary>The tables of format 1, in creation order.</summary>
        internal static readonly string[] TableNames =
        [
            "NewerMessagesInfo",
            "MediaRoot",
            "RefreshRun",
            "NewerMessage",
            "NewerAttachment",
        ];

        /// <summary>Kinds of <c>NewerMessage.Kind</c>.</summary>
        public const string KindUser = "User";

        /// <summary>A WhatsApp system row, shown only as a fixed wording.</summary>
        public const string KindSystem = "System";

        /// <summary>A message the archive cannot show yet (D6), shown only as a fixed wording.</summary>
        public const string KindPlaceholder = "Placeholder";

        /// <summary>States of <c>NewerAttachment.State</c>. Never Missing (D4).</summary>
        public const string StateRestored = "Restored";

        /// <summary>No file under the media root whose SHA-256 equals msgstore's <c>file_hash</c>.</summary>
        public const string StateUnmatched = "Unmatched";

        /// <summary>First-run hand-off: the workspace's last day lined up with the msgstore (D2).</summary>
        public const string HandOffWorkspace = "Workspace";

        /// <summary>Later-run hand-off: the previous run's last key_id found again in the msgstore (D2).</summary>
        public const string HandOffPreviousRun = "PreviousRun";

        private static readonly string[] Statements =
        [
            $"""
            CREATE TABLE NewerMessagesInfo (
                NewerMessagesInfoID INTEGER PRIMARY KEY,
                Purpose             TEXT NOT NULL,
                FormatVersion       INTEGER NOT NULL,
                ConversationID      INTEGER NOT NULL,
                LocalParticipantID  INTEGER NOT NULL,
                OtherParticipantID  INTEGER NOT NULL,
                ChatRowID           INTEGER NOT NULL,
                WorkspaceLastDay    TEXT NOT NULL,
                TimeZone            TEXT NOT NULL,
                CreatedDateTimeUtc  TEXT NOT NULL,

                CHECK (NewerMessagesInfoID = 1),
                CHECK (Purpose = '{MarkerPurpose}'),
                CHECK (FormatVersion = {FormatVersion}),
                CHECK (LocalParticipantID <> OtherParticipantID),
                CHECK (length(WorkspaceLastDay) = 10),
                CHECK (TimeZone = '{TimeZoneName}')
            );
            """,

            """
            CREATE TABLE MediaRoot (
                MediaRootID        INTEGER PRIMARY KEY AUTOINCREMENT,
                RootPath           TEXT NOT NULL UNIQUE,
                RegisteredDateTimeUtc TEXT NOT NULL,

                CHECK (length(trim(RootPath)) > 0)
            );
            """,

            $"""
            CREATE TABLE RefreshRun (
                RunID                INTEGER PRIMARY KEY AUTOINCREMENT,
                RunDateTimeUtc       TEXT NOT NULL,
                ManifestSha256       TEXT NOT NULL UNIQUE,
                MsgstoreSizeBytes    INTEGER NOT NULL,
                MsgstoreUserVersion  INTEGER NOT NULL,
                MsgstoreSchemaSha256 TEXT NOT NULL,
                HandOff              TEXT NOT NULL,
                HandOffKeyID         TEXT NOT NULL,
                HandOffFromMe        INTEGER NOT NULL,
                MediaRootID          INTEGER NULL,
                Messages             INTEGER NOT NULL,
                UserMessages         INTEGER NOT NULL,
                SystemMessages       INTEGER NOT NULL,
                Placeholders         INTEGER NOT NULL,
                Attachments          INTEGER NOT NULL,
                Restored             INTEGER NOT NULL,
                Unmatched            INTEGER NOT NULL,
                LastKeyID            TEXT NULL,
                LastFromMe           INTEGER NULL,
                LastSortID           INTEGER NULL,

                CHECK (length(ManifestSha256) = 64),
                CHECK (HandOff IN ('{HandOffWorkspace}', '{HandOffPreviousRun}')),
                CHECK (length(HandOffKeyID) > 0),
                CHECK (HandOffFromMe IN (0, 1)),
                CHECK ((LastKeyID IS NULL) = (LastFromMe IS NULL)),
                CHECK (LastFromMe IS NULL OR LastFromMe IN (0, 1)),
                CHECK (Messages = UserMessages + SystemMessages + Placeholders),
                CHECK (Attachments = Restored + Unmatched),
                CHECK ((Messages = 0) = (LastKeyID IS NULL)),
                CHECK (Messages >= 0 AND Attachments >= 0 AND Restored >= 0 AND Unmatched >= 0),

                FOREIGN KEY (MediaRootID) REFERENCES MediaRoot (MediaRootID)
            );
            """,

            $"""
            CREATE TABLE NewerMessage (
                NewerMessageID      INTEGER PRIMARY KEY AUTOINCREMENT,
                RunID               INTEGER NOT NULL,
                KeyID               TEXT NOT NULL,
                MsgstoreRowID       INTEGER NOT NULL,
                SortID              INTEGER NOT NULL,
                FromMe              INTEGER NOT NULL,
                SenderParticipantID INTEGER NULL,
                TimestampUtcMs      INTEGER NOT NULL,
                DateTimeLocal       TEXT NOT NULL,
                MessageType         INTEGER NULL,
                Kind                TEXT NOT NULL,
                PlaceholderKind     TEXT NULL,
                Text                TEXT NULL,
                Caption             TEXT NULL,
                IsForwarded         INTEGER NULL,
                ForwardScore        INTEGER NULL,

                CHECK (length(KeyID) > 0),
                CHECK (FromMe IN (0, 1)),
                CHECK (Kind IN ('{KindUser}', '{KindSystem}', '{KindPlaceholder}')),
                CHECK ((Kind = '{KindPlaceholder}') = (PlaceholderKind IS NOT NULL)),
                CHECK ((Kind = '{KindSystem}') = (SenderParticipantID IS NULL)),
                CHECK (Kind = '{KindUser}' OR Caption IS NULL),
                CHECK (Kind = '{KindUser}' OR Text IS NOT NULL),
                CHECK (Caption IS NULL OR length(Caption) > 0),
                CHECK (IsForwarded IS NULL OR IsForwarded IN (0, 1)),
                CHECK (ForwardScore IS NULL OR (IsForwarded = 1 AND ForwardScore >= 0)),
                CHECK (length(DateTimeLocal) = 19),

                UNIQUE (FromMe, KeyID),

                FOREIGN KEY (RunID) REFERENCES RefreshRun (RunID)
            );
            """,

            $"""
            CREATE TABLE NewerAttachment (
                NewerAttachmentID   INTEGER PRIMARY KEY AUTOINCREMENT,
                NewerMessageID      INTEGER NOT NULL UNIQUE,
                FileName            TEXT NULL,
                FileSizeBytes       INTEGER NULL,
                MimeType            TEXT NULL,
                FileHash            TEXT NULL,
                RelativePath        TEXT NULL,
                State               TEXT NOT NULL,
                MediaRootID         INTEGER NULL,
                Sha256              TEXT NULL,
                LowQualityCopyOfAttachmentID INTEGER NULL,

                CHECK (State IN ('{StateRestored}', '{StateUnmatched}')),
                CHECK ((State = '{StateRestored}') = (Sha256 IS NOT NULL)),
                CHECK ((State = '{StateRestored}') = (MediaRootID IS NOT NULL)),
                CHECK (State = '{StateUnmatched}' OR RelativePath IS NOT NULL),
                CHECK (Sha256 IS NULL OR (length(Sha256) = 64 AND Sha256 NOT GLOB '*[^0-9a-f]*')),
                CHECK (RelativePath IS NULL OR (length(RelativePath) > 0 AND substr(RelativePath, 1, 1) <> '/'
                       AND instr(RelativePath, char(92)) = 0)),
                CHECK (FileName IS NULL OR (instr(FileName, '/') = 0 AND instr(FileName, char(92)) = 0)),
                CHECK (LowQualityCopyOfAttachmentID IS NULL OR LowQualityCopyOfAttachmentID <> NewerAttachmentID),

                FOREIGN KEY (NewerMessageID) REFERENCES NewerMessage (NewerMessageID),
                FOREIGN KEY (MediaRootID) REFERENCES MediaRoot (MediaRootID),
                FOREIGN KEY (LowQualityCopyOfAttachmentID) REFERENCES NewerAttachment (NewerAttachmentID)
            );
            """,

            "CREATE INDEX IX_NewerMessage_RunID ON NewerMessage (RunID);",
        ];

        /// <summary>The newer-messages path that belongs beside the main workspace.</summary>
        public static string PathBeside(string mainWorkspacePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(mainWorkspacePath);

            var folder = Path.GetDirectoryName(Path.GetFullPath(mainWorkspacePath))
                ?? throw new ArgumentException("The main workspace path has no containing folder.", nameof(mainWorkspacePath));

            return Path.Combine(folder, FileName);
        }

        /// <summary>
        /// Creates the tables, the marker row and the <c>user_version</c> on an open connection to a new,
        /// empty file, inside the caller's transaction (so a first refresh run is one transaction).
        /// </summary>
        internal static void CreateIn(SqliteConnection connection, SqliteTransaction transaction, NewerMessagesInfo info)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            foreach (var statement in Statements)
            {
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }

            command.CommandText =
                """
                INSERT INTO NewerMessagesInfo (
                    NewerMessagesInfoID, Purpose, FormatVersion, ConversationID, LocalParticipantID,
                    OtherParticipantID, ChatRowID, WorkspaceLastDay, TimeZone, CreatedDateTimeUtc)
                VALUES (1, $purpose, $format, $conversation, $local, $other, $chat, $lastDay, $zone, $created);
                """;
            command.Parameters.AddWithValue("$lastDay", FormatDay(info.WorkspaceLastDay));
            command.Parameters.AddWithValue("$purpose", MarkerPurpose);
            command.Parameters.AddWithValue("$format", FormatVersion);
            command.Parameters.AddWithValue("$conversation", info.ConversationID);
            command.Parameters.AddWithValue("$local", info.LocalParticipantID);
            command.Parameters.AddWithValue("$other", info.OtherParticipantID);
            command.Parameters.AddWithValue("$chat", info.ChatRowID);
            command.Parameters.AddWithValue("$zone", TimeZoneName);
            command.Parameters.AddWithValue("$created", FormatUtc(info.CreatedDateTimeUtc));
            command.ExecuteNonQuery();

            command.Parameters.Clear();
            command.CommandText = $"PRAGMA user_version = {UserVersion.ToString(CultureInfo.InvariantCulture)};";
            command.ExecuteNonQuery();
        }

        /// <summary>Opens an existing newer-messages file read-only, after proving it is one.</summary>
        /// <exception cref="ArgumentException">The file name is not <see cref="FileName"/>.</exception>
        /// <exception cref="FileNotFoundException">There is no file. Nothing is created.</exception>
        /// <exception cref="InvalidOperationException">It is not a newer-messages file of this format.</exception>
        public static SqliteConnection OpenReadOnly(string databasePath)
        {
            RequireFixedFileName(databasePath);

            if (!File.Exists(databasePath))
            {
                throw new FileNotFoundException(
                    "There is no newer-messages file at the supplied path. Nothing has been created.");
            }

            var connection = WorkspaceDatabase.OpenReadOnlyConnection(databasePath);

            try
            {
                RequireNewerMessagesFile(connection);
            }
            catch
            {
                connection.Dispose();
                throw;
            }

            return connection;
        }

        /// <summary>
        /// Throws unless <paramref name="connection"/> is open on a newer-messages file of this format:
        /// the <c>user_version</c>, every table, and exactly one valid marker row.
        /// </summary>
        /// <exception cref="InvalidOperationException">It is not one. Nothing is written.</exception>
        internal static void RequireNewerMessagesFile(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";

            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != UserVersion)
            {
                throw new InvalidOperationException(
                    "The database does not carry the newer-messages file's user_version, so it is not treated as " +
                    "a newer-messages file (a workspace is never opened as one). Nothing has been written.");
            }

            var tables = WorkspaceDatabase.ReadApplicationTableNames(connection);

            if (!TableNames.All(tables.Contains))
            {
                throw new InvalidOperationException(
                    "The newer-messages file lacks one of its tables. Nothing has been written.");
            }

            command.CommandText =
                """
                SELECT (SELECT COUNT(*) FROM NewerMessagesInfo),
                       (SELECT COUNT(*) FROM NewerMessagesInfo
                        WHERE NewerMessagesInfoID = 1 AND Purpose = $purpose AND FormatVersion = $format);
                """;
            command.Parameters.AddWithValue("$purpose", MarkerPurpose);
            command.Parameters.AddWithValue("$format", FormatVersion);

            using var reader = command.ExecuteReader();
            reader.Read();

            if (reader.GetInt64(0) != 1 || reader.GetInt64(1) != 1)
            {
                throw new InvalidOperationException(
                    "The newer-messages file does not carry exactly one valid marker row of format " +
                    $"{FormatVersion.ToString(CultureInfo.InvariantCulture)}. Nothing has been written.");
            }
        }

        internal static void RequireFixedFileName(string databasePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

            if (!string.Equals(Path.GetFileName(databasePath), FileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"The newer-messages file must be named '{FileName}'. Any other name is refused so that the " +
                    "main workspace cannot be mistaken for it.",
                    nameof(databasePath));
            }
        }

        internal static string FormatDay(DateOnly day) => day.ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);

        internal static string FormatUtc(DateTime utc) =>
            utc.ToUniversalTime().ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
    }
}
