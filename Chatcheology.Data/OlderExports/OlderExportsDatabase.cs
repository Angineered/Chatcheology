using System.Globalization;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.OlderExports
{
    /// <summary>
    /// Creates and opens the older-exports file: a second SQLite workspace, kept beside the main
    /// workspace under the fixed name <see cref="FileName"/>, that holds the older WhatsApp exports
    /// as one conversation each.
    /// </summary>
    /// <remarks>
    /// The file is an ordinary workspace at <see cref="WorkspaceDatabase.SchemaVersion"/>, created
    /// through <see cref="WorkspaceDatabase.Initialise"/>, so the existing importer, schema guard and
    /// readers accept it unchanged. It adds four tables of its own and nothing else: the
    /// <c>OlderExportsInfo</c> marker, <c>ExportRole</c>, <c>ExportOrder</c> and
    /// <c>ExportAttachmentName</c>. Its <c>user_version</c> stays
    /// <see cref="WorkspaceDatabase.SchemaVersion"/>; the main workspace's schema and code paths are
    /// not touched by anything here.
    /// <para>
    /// The marker is what makes the two files impossible to confuse. Every write path opens through
    /// <see cref="OpenConnection"/>, which refuses any database without a valid marker row — the
    /// main workspace above all — and <see cref="Create"/> refuses any path where a file already
    /// exists, so the main workspace can neither be opened as this file nor turned into one.
    /// </para>
    /// <para>
    /// No Phase 7 table is used in this file. They exist, empty, because the workspace schema
    /// creates them.
    /// </para>
    /// </remarks>
    public static class OlderExportsDatabase
    {
        /// <summary>
        /// The fixed file name, in the same folder as the main workspace.
        /// </summary>
        public const string FileName = "older-exports.db";

        /// <summary>The value of <c>OlderExportsInfo.Purpose</c> in every older-exports file.</summary>
        internal const string MarkerPurpose = "OlderExports";

        /// <summary>The older-exports table layout this build creates and accepts.</summary>
        internal const int MarkerFormatVersion = 1;

        /// <summary>The tables this file adds to the workspace schema, in creation order.</summary>
        internal static readonly string[] AddedTableNames =
        [
            "OlderExportsInfo",
            "ExportRole",
            "ExportOrder",
            "ExportAttachmentName",
        ];

        /// <summary>
        /// The added tables, one statement per element, in dependency order.
        /// </summary>
        /// <remarks>
        /// Vocabularies and invariants are <c>CHECK</c> constraints so the file states its own
        /// rules: one marker row only, roles <c>Local</c>/<c>Other</c> with at most one of each per
        /// conversation, a positive unique timeline position, a 0/1 shown flag, and an attachment
        /// name that is non-empty and carries no path separator.
        /// <para>
        /// <c>ExportRole</c> references <c>ConversationParticipant</c> by its composite key, so a
        /// role can only be given to a participant of that same conversation.
        /// </para>
        /// </remarks>
        private static readonly string[] AddedTableStatements =
        [
            $"""
            CREATE TABLE OlderExportsInfo (
                OlderExportsInfoID INTEGER PRIMARY KEY,
                Purpose            TEXT NOT NULL,
                FormatVersion      INTEGER NOT NULL,
                CreatedDateTimeUtc TEXT NOT NULL,

                CHECK (OlderExportsInfoID = 1),
                CHECK (Purpose = '{MarkerPurpose}'),
                CHECK (FormatVersion = {MarkerFormatVersion})
            );
            """,

            """
            CREATE TABLE ExportRole (
                ConversationID INTEGER NOT NULL,
                ParticipantID  INTEGER NOT NULL,
                Role           TEXT NOT NULL,

                PRIMARY KEY (ConversationID, ParticipantID),
                UNIQUE (ConversationID, Role),

                CHECK (Role IN ('Local', 'Other')),

                FOREIGN KEY (ConversationID, ParticipantID)
                    REFERENCES ConversationParticipant (ConversationID, ParticipantID)
            );
            """,

            """
            CREATE TABLE ExportOrder (
                ConversationID INTEGER PRIMARY KEY,
                Position       INTEGER NOT NULL,
                Shown          INTEGER NOT NULL,

                UNIQUE (Position),

                CHECK (Position > 0),
                CHECK (Shown IN (0, 1)),

                FOREIGN KEY (ConversationID) REFERENCES Conversation (ConversationID)
            );
            """,

            """
            CREATE TABLE ExportAttachmentName (
                AttachmentID INTEGER PRIMARY KEY,
                FileName     TEXT NOT NULL,

                CHECK (length(trim(FileName)) > 0),
                CHECK (instr(FileName, '/') = 0),
                CHECK (instr(FileName, char(92)) = 0),

                FOREIGN KEY (AttachmentID) REFERENCES Attachment (AttachmentID)
            );
            """,
        ];

        /// <summary>
        /// The older-exports path that belongs beside the main workspace at
        /// <paramref name="mainWorkspacePath"/>.
        /// </summary>
        public static string PathBeside(string mainWorkspacePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(mainWorkspacePath);

            var folder = Path.GetDirectoryName(Path.GetFullPath(mainWorkspacePath))
                ?? throw new ArgumentException(
                    "The main workspace path has no containing folder.", nameof(mainWorkspacePath));

            return Path.Combine(folder, FileName);
        }

        /// <summary>
        /// Creates a new older-exports file at <paramref name="databasePath"/>: the current
        /// workspace schema, the added tables and the marker row.
        /// </summary>
        /// <param name="databasePath">
        /// Where to create it. The file name must be <see cref="FileName"/> and no file may exist
        /// there yet.
        /// </param>
        /// <param name="createdDateTimeUtc">Recorded in the marker. Must be UTC.</param>
        /// <remarks>
        /// Two steps, because <see cref="WorkspaceDatabase.Initialise"/> commits its own transaction:
        /// the workspace schema, then the added tables and marker in one transaction. If the second
        /// step fails the half-made file is deleted, so no file without a marker is left behind;
        /// one would be refused by <see cref="OpenConnection"/> anyway.
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// The file name is not <see cref="FileName"/>, or the timestamp is not UTC.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// A file already exists at the path. Nothing is written to it.
        /// </exception>
        public static void Create(string databasePath, DateTime createdDateTimeUtc)
        {
            RequireFixedFileName(databasePath);

            if (createdDateTimeUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "The creation timestamp must be DateTimeKind.Utc.", nameof(createdDateTimeUtc));
            }

            if (File.Exists(databasePath) || Directory.Exists(databasePath))
            {
                throw new InvalidOperationException(
                    "A file already exists at the older-exports path. Creating never takes over an " +
                    "existing file, whether it is an older-exports file or any other database, so " +
                    "nothing has been written.");
            }

            try
            {
                WorkspaceDatabase.Initialise(databasePath);

                using var connection = WorkspaceDatabase.OpenConnection(databasePath);
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;

                foreach (var statement in AddedTableStatements)
                {
                    command.CommandText = statement;
                    command.ExecuteNonQuery();
                }

                command.CommandText =
                    """
                    INSERT INTO OlderExportsInfo (
                        OlderExportsInfoID, Purpose, FormatVersion, CreatedDateTimeUtc)
                    VALUES (1, $purpose, $formatVersion, $created);
                    """;
                command.Parameters.AddWithValue("$purpose", MarkerPurpose);
                command.Parameters.AddWithValue("$formatVersion", MarkerFormatVersion);
                command.Parameters.AddWithValue(
                    "$created",
                    createdDateTimeUtc.ToString(
                        WorkspaceDatabase.UtcTimestampFormat, CultureInfo.InvariantCulture));
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

        /// <summary>
        /// Opens an existing older-exports file for writing, after proving it is one.
        /// </summary>
        /// <returns>An open connection with foreign keys enforced. The caller disposes it.</returns>
        /// <exception cref="ArgumentException">The file name is not <see cref="FileName"/>.</exception>
        /// <exception cref="FileNotFoundException">
        /// There is no file at the path. Nothing is created.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The database is not at the current workspace schema version, or does not carry the
        /// older-exports tables and a valid marker row (a main workspace, for example). Nothing has
        /// been written.
        /// </exception>
        public static SqliteConnection OpenConnection(string databasePath)
        {
            RequireFixedFileName(databasePath);

            // Checked first because the read-write connection would otherwise create an empty file
            // purely to reject it.
            if (!File.Exists(databasePath))
            {
                throw new FileNotFoundException(
                    "There is no older-exports file at the supplied path. Opening never creates " +
                    "one; use OlderExportsDatabase.Create. No file has been created.");
            }

            var connection = WorkspaceDatabase.OpenConnection(databasePath);

            try
            {
                RequireOlderExportsFile(connection);
            }
            catch
            {
                connection.Dispose();
                throw;
            }

            return connection;
        }

        /// <summary>
        /// Throws unless <paramref name="connection"/> is open on an older-exports file: the current
        /// workspace schema version, every added table, and exactly one valid marker row.
        /// </summary>
        /// <exception cref="InvalidOperationException">It is not one. Nothing is written.</exception>
        internal static void RequireOlderExportsFile(SqliteConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(connection, "an older-exports operation");

            var tables = WorkspaceDatabase.ReadApplicationTableNames(connection);

            if (!AddedTableNames.All(tables.Contains))
            {
                throw new InvalidOperationException(
                    "The database is a workspace but not an older-exports file: it does not carry " +
                    "the older-exports tables. A main workspace is never opened or written as the " +
                    "older-exports file. Nothing has been written.");
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*)
                FROM OlderExportsInfo
                WHERE OlderExportsInfoID = 1
                  AND Purpose = $purpose
                  AND FormatVersion = $formatVersion;
                """;
            command.Parameters.AddWithValue("$purpose", MarkerPurpose);
            command.Parameters.AddWithValue("$formatVersion", MarkerFormatVersion);

            var validRows = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

            command.CommandText = "SELECT COUNT(*) FROM OlderExportsInfo;";
            command.Parameters.Clear();

            var allRows = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

            if (validRows != 1 || allRows != 1)
            {
                throw new InvalidOperationException(
                    "The database does not carry exactly one valid older-exports marker row, so it " +
                    "is not treated as an older-exports file. Nothing has been written.");
            }
        }

        private static void RequireFixedFileName(string databasePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

            if (!string.Equals(Path.GetFileName(databasePath), FileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"The older-exports file must be named '{FileName}'. Any other name is refused so " +
                    $"that the main workspace cannot be mistaken for it.",
                    nameof(databasePath));
            }
        }
    }
}
