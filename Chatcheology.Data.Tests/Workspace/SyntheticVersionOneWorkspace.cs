using Chatcheology.Core.Models;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Workspace.WorkspaceTestData;

namespace Chatcheology.Data.Tests.Workspace
{
    /// <summary>
    /// Builds a genuine schema-version-1 workspace database for the migration tests.
    /// </summary>
    /// <remarks>
    /// The version-1 schema is written out here rather than obtained from
    /// <see cref="Data.Workspace.WorkspaceDatabase"/>, deliberately. A fixture produced by the code
    /// under test could only ever prove that code consistent with itself; the migration has to be
    /// shown to work on a database built the way version 1 actually built them, which is what this
    /// copy is. It is frozen: nothing about version 1 changes again, so it will not drift.
    /// <para>
    /// The statements are byte-identical to the version-1 release, including the constraints, so a
    /// migrated fixture and a real version-1 workspace present the migration with the same starting
    /// point.
    /// </para>
    /// </remarks>
    internal static class SyntheticVersionOneWorkspace
    {
        /// <summary>The tables a version-1 workspace has, and the only ones it has.</summary>
        internal static readonly string[] Tables =
        [
            "ImportSource",
            "Conversation",
            "Participant",
            "ConversationParticipant",
            "Message",
        ];

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
        /// The message contents the fixture stores, in sequence order.
        /// </summary>
        /// <remarks>
        /// Two of these are the interesting ones. Sequence 3 is exactly the media placeholder and
        /// must produce one attachment. Sequence 4 contains the placeholder inside longer text — the
        /// captioned form the parser also refuses to call a placeholder — and must produce none: an
        /// exact-equality backfill and a substring one give different answers here, which is the
        /// point of including it.
        /// </remarks>
        private static readonly string[] MessageContents =
        [
            "Hi Sam",
            "Hi Alex",
            ParsedMessage.MediaPlaceholderContent,
            $"see this {ParsedMessage.MediaPlaceholderContent} please",
            $"{ParsedMessage.MediaPlaceholderContent}\na caption",
            "See you tomorrow",
        ];

        /// <summary>
        /// The sequence number of the one message that is exactly a media placeholder.
        /// </summary>
        internal const int ExactPlaceholderSequenceNumber = 3;

        /// <summary>How many messages the fixture stores.</summary>
        internal static int MessageCount => MessageContents.Length;

        /// <summary>
        /// Creates a version-1 database at <paramref name="databasePath"/> and populates it.
        /// </summary>
        /// <remarks>
        /// Written through a plain connection rather than through the data layer's own, so that not
        /// even the connection string comes from the code being tested. Foreign keys are enforced,
        /// as a real version-1 workspace enforced them, which is also what proves the fixture data
        /// is internally consistent rather than merely inserted.
        /// </remarks>
        internal static void Create(string databasePath)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true,
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;

                foreach (var statement in SchemaStatements)
                {
                    command.CommandText = statement;
                    command.ExecuteNonQuery();
                }

                command.CommandText = "PRAGMA user_version = 1;";
                command.ExecuteNonQuery();
            }

            Populate(connection, transaction);

            transaction.Commit();
        }

        /// <summary>
        /// Inserts one import source, one conversation, two participants and
        /// <see cref="MessageCount"/> messages.
        /// </summary>
        private static void Populate(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            command.CommandText =
                $"""
                INSERT INTO ImportSource (
                    SourceType, DisplayName, OriginalFileName, SHA256, ImportedDateTimeUtc,
                    SourceTimeZoneID)
                VALUES (
                    '{SourceType}', '{SourceDisplayName}', '{OriginalFileName}', '{SHA256}',
                    '{ImportedDateTimeUtcText}', '{SourceTimeZoneID}');

                INSERT INTO Conversation (Title, CreatedDateTimeUtc)
                VALUES ('{ConversationTitle}', '{ImportedDateTimeUtcText}');

                INSERT INTO Participant (DisplayName) VALUES ('Alex');
                INSERT INTO Participant (DisplayName) VALUES ('Sam');

                INSERT INTO ConversationParticipant (ConversationID, ParticipantID) VALUES (1, 1);
                INSERT INTO ConversationParticipant (ConversationID, ParticipantID) VALUES (1, 2);
                """;

            command.ExecuteNonQuery();

            command.CommandText =
                """
                INSERT INTO Message (
                    ConversationID,
                    ImportSourceID,
                    SequenceNumber,
                    MessageDateTimeLocal,
                    SenderParticipantID,
                    MessageType,
                    MessageContent,
                    RawContent,
                    SourceLineStart,
                    SourceLineEnd)
                VALUES (
                    1,
                    1,
                    $sequenceNumber,
                    $messageDateTimeLocal,
                    $senderParticipantID,
                    'User',
                    $messageContent,
                    $rawContent,
                    $sourceLineStart,
                    $sourceLineEnd);
                """;

            var sequenceNumber = command.Parameters.Add("$sequenceNumber", SqliteType.Integer);
            var messageDateTimeLocal = command.Parameters.Add("$messageDateTimeLocal", SqliteType.Text);
            var senderParticipantID = command.Parameters.Add("$senderParticipantID", SqliteType.Integer);
            var messageContent = command.Parameters.Add("$messageContent", SqliteType.Text);
            var rawContent = command.Parameters.Add("$rawContent", SqliteType.Text);
            var sourceLineStart = command.Parameters.Add("$sourceLineStart", SqliteType.Integer);
            var sourceLineEnd = command.Parameters.Add("$sourceLineEnd", SqliteType.Integer);

            for (var index = 0; index < MessageContents.Length; index++)
            {
                var content = MessageContents[index];

                sequenceNumber.Value = index + 1;
                messageDateTimeLocal.Value = MessageDateTimeText;

                // Alternating, so both participants are genuinely referenced by messages.
                senderParticipantID.Value = (index % 2) + 1;

                messageContent.Value = content;
                rawContent.Value = $"2026/01/05, 14:03 - Alex: {content}";
                sourceLineStart.Value = index + 1;
                sourceLineEnd.Value = index + 1;

                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Opens a plain connection to an existing database, with foreign keys enforced and without
        /// going through the data layer.
        /// </summary>
        internal static SqliteConnection OpenPlainConnection(string databasePath)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                ForeignKeys = true,
            }.ToString();

            var connection = new SqliteConnection(connectionString);
            connection.Open();

            return connection;
        }
    }
}
