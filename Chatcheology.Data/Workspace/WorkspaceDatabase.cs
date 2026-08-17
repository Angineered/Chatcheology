using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Workspace
{
    /// <summary>
    /// Creates and opens the workspace SQLite database and owns its schema definition.
    /// </summary>
    /// <remarks>
    /// The workspace database holds reconstruction state. It is deliberately created and opened
    /// through this one type so that every connection is built the same way and foreign-key
    /// enforcement can never be accidentally left off.
    /// <para>
    /// The schema version lives in SQLite's own <c>PRAGMA user_version</c> header field rather than
    /// in a table of our own, so a workspace file describes itself without needing to be queried
    /// through application code. There is no migration engine in this phase: version 0 is created,
    /// version <see cref="SchemaVersion"/> is accepted, and anything else is rejected.
    /// </para>
    /// <para>
    /// Write-ahead logging is deliberately not enabled, so a workspace is a single file with no
    /// <c>-wal</c> or <c>-shm</c> companions.
    /// </para>
    /// </remarks>
    public static class WorkspaceDatabase
    {
        /// <summary>
        /// The workspace schema version this build creates and accepts.
        /// </summary>
        public const int SchemaVersion = 1;

        /// <summary>
        /// The <c>user_version</c> of a SQLite database that has never had a workspace schema
        /// applied to it. This is also the value of a brand-new empty file.
        /// </summary>
        private const int UninitialisedSchemaVersion = 0;

        /// <summary>
        /// The complete version-1 schema, one statement per element, in creation order.
        /// </summary>
        /// <remarks>
        /// The order matters: a table is created only after the tables its foreign keys reference,
        /// which keeps the definition readable and keeps a partially applied schema impossible to
        /// mistake for a complete one.
        /// <para>
        /// Identifiers are unquoted because none of them needs quoting, and the constraints are
        /// spelled out in the table definitions rather than applied later, so a table can never
        /// exist in a less-constrained intermediate state.
        /// </para>
        /// </remarks>
        private static readonly string[] SchemaStatements =
        [
            """
            CREATE TABLE ImportSource (
                ImportSourceID      INTEGER PRIMARY KEY,
                SourceType          TEXT NOT NULL,
                DisplayName         TEXT NOT NULL,
                OriginalFileName    TEXT NULL,
                SHA256              TEXT NULL,
                ImportedDateTimeUtc TEXT NOT NULL,
                SourceTimeZoneID    TEXT NULL
            );
            """,

            """
            CREATE TABLE Conversation (
                ConversationID     INTEGER PRIMARY KEY,
                Title              TEXT NOT NULL,
                CreatedDateTimeUtc TEXT NOT NULL
            );
            """,

            """
            CREATE TABLE Participant (
                ParticipantID INTEGER PRIMARY KEY,
                DisplayName   TEXT NOT NULL
            );
            """,

            // DisplayName is deliberately not unique: two unrelated conversations may legitimately
            // contain the same display name, and identity resolution across imports does not exist
            // yet. The composite unique key is what makes a participant's conversation membership
            // a fact the database can enforce.
            """
            CREATE TABLE ConversationParticipant (
                ConversationParticipantID INTEGER PRIMARY KEY,
                ConversationID            INTEGER NOT NULL,
                ParticipantID             INTEGER NOT NULL,

                UNIQUE (ConversationID, ParticipantID),

                FOREIGN KEY (ConversationID) REFERENCES Conversation (ConversationID),
                FOREIGN KEY (ParticipantID) REFERENCES Participant (ParticipantID)
            );
            """,

            // The sender relationship is a composite foreign key into ConversationParticipant
            // rather than a plain reference to Participant, so a message cannot name a participant
            // who belongs only to a different conversation. SQLite compares composite foreign keys
            // with MATCH SIMPLE semantics, so a system message's null SenderParticipantID needs no
            // parent row and no second nullable relationship has to be modelled.
            """
            CREATE TABLE Message (
                MessageID            INTEGER PRIMARY KEY,
                ConversationID       INTEGER NOT NULL,
                ImportSourceID       INTEGER NOT NULL,
                SequenceNumber       INTEGER NOT NULL,
                MessageDateTimeLocal TEXT NOT NULL,
                SenderParticipantID  INTEGER NULL,
                MessageType          TEXT NOT NULL,
                MessageContent       TEXT NOT NULL,
                RawContent           TEXT NOT NULL,
                SourceLineStart      INTEGER NOT NULL,
                SourceLineEnd        INTEGER NOT NULL,

                UNIQUE (ConversationID, SequenceNumber),

                CHECK (SequenceNumber > 0),
                CHECK (SourceLineStart > 0),
                CHECK (SourceLineEnd >= SourceLineStart),
                CHECK (MessageType IN ('User', 'System')),
                CHECK (
                    (MessageType = 'User' AND SenderParticipantID IS NOT NULL)
                    OR (MessageType = 'System' AND SenderParticipantID IS NULL)
                ),

                FOREIGN KEY (ConversationID) REFERENCES Conversation (ConversationID),
                FOREIGN KEY (ImportSourceID) REFERENCES ImportSource (ImportSourceID),
                FOREIGN KEY (ConversationID, SenderParticipantID)
                    REFERENCES ConversationParticipant (ConversationID, ParticipantID)
            );
            """,
        ];

        /// <summary>
        /// Builds the connection string for a file-backed workspace database.
        /// </summary>
        /// <param name="databasePath">
        /// The workspace database file path, supplied by the caller. No location is assumed or
        /// invented here.
        /// </param>
        /// <remarks>
        /// Built through <see cref="SqliteConnectionStringBuilder"/> rather than by concatenation,
        /// so a path containing a quote, a semicolon or an equals sign cannot change the meaning of
        /// the connection string.
        /// <para>
        /// <c>Foreign Keys=True</c> is part of the connection string itself, which makes
        /// <c>PRAGMA foreign_keys = ON</c> run on every open. Setting it afterwards by hand would
        /// be fragile, because SQLite silently ignores that pragma while a transaction is open.
        /// </para>
        /// </remarks>
        public static string BuildConnectionString(string databasePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,

                // SQLite may create the database file itself. It does not create missing parent
                // directories, and no directory structure is invented on the caller's behalf.
                Mode = SqliteOpenMode.ReadWriteCreate,

                ForeignKeys = true,
            };

            return builder.ToString();
        }

        /// <summary>
        /// Opens a workspace connection with foreign-key enforcement active.
        /// </summary>
        /// <returns>An open connection. The caller owns it and must dispose it.</returns>
        public static SqliteConnection OpenConnection(string databasePath)
        {
            var connection = new SqliteConnection(BuildConnectionString(databasePath));

            try
            {
                connection.Open();
            }
            catch
            {
                // Nothing else can dispose it yet, and leaking it would keep a pooled handle on
                // the file.
                connection.Dispose();
                throw;
            }

            return connection;
        }

        /// <summary>
        /// Reads <c>PRAGMA user_version</c> from an open connection.
        /// </summary>
        /// <returns>
        /// <see cref="SchemaVersion"/> for an initialised workspace, 0 for a database that has
        /// never had a workspace schema applied, or any other value written by a build that is not
        /// this one.
        /// </returns>
        public static int ReadSchemaVersion(SqliteConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";

            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Ensures the workspace database at <paramref name="databasePath"/> carries schema version
        /// <see cref="SchemaVersion"/>, creating it if the database has no workspace schema yet.
        /// </summary>
        /// <remarks>
        /// Creation is a single transaction covering both the table definitions and the
        /// <c>user_version</c> write, and SQLite treats both as transactional work. A failure part
        /// way through therefore rolls back to a database with no workspace tables and
        /// <c>user_version</c> still 0: there is no state in which a partially created schema claims
        /// to be version <see cref="SchemaVersion"/>.
        /// <para>
        /// An already-initialised workspace is accepted and left completely untouched, so calling
        /// this repeatedly cannot destroy existing data.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The database reports a schema version this build does not support.
        /// </exception>
        /// <exception cref="SqliteException">
        /// The schema could not be created — for example because the database already contains an
        /// object whose name collides with a workspace table, which is a malformed workspace rather
        /// than a supported starting point. The database is left as it was found.
        /// </exception>
        public static void Initialise(string databasePath)
        {
            using var connection = OpenConnection(databasePath);

            var existingVersion = ReadSchemaVersion(connection);

            if (existingVersion == SchemaVersion)
            {
                return;
            }

            if (existingVersion != UninitialisedSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"The workspace database reports schema version {existingVersion}, which this " +
                    $"build does not support. The only supported version is {SchemaVersion}.");
            }

            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;

                foreach (var statement in SchemaStatements)
                {
                    command.CommandText = statement;
                    command.ExecuteNonQuery();
                }

                // A pragma value cannot be parameterised. The interpolated value is a private
                // integer constant, not caller input.
                command.CommandText = $"PRAGMA user_version = {SchemaVersion};";
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }
}
