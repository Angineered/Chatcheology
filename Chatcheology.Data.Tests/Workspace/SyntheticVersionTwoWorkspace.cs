using Chatcheology.Core.Models;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Workspace.WorkspaceTestData;

namespace Chatcheology.Data.Tests.Workspace
{
    /// <summary>
    /// Builds a genuine schema-version-2 workspace database for the version-3 migration tests.
    /// </summary>
    /// <remarks>
    /// The version-2 schema is written out here rather than obtained from
    /// <see cref="Data.Workspace.WorkspaceDatabase"/>, for the same reason
    /// <see cref="SyntheticVersionOneWorkspace"/> copies version 1: a fixture produced by the code
    /// under test could only ever prove that code consistent with itself. The version-2 to version-3
    /// migration has to be shown to work on a database built the way version 2 actually built them.
    /// <para>
    /// The statements are byte-identical to the version-2 release, including the constraints and the
    /// creation order, so a migrated fixture and a real version-2 workspace present the migration
    /// with the same starting point. It is frozen: nothing about version 2 changes again.
    /// </para>
    /// <para>
    /// Populated, and not only created. A migration that is supposed to leave existing data untouched
    /// cannot be shown to do that against empty tables, so the fixture carries rows in every table a
    /// version-2 workspace can hold — including an attachment, two media sources, a deduplicated
    /// payload with two physical copies, and the nullable columns left null.
    /// </para>
    /// </remarks>
    internal static class SyntheticVersionTwoWorkspace
    {
        /// <summary>The tables a version-2 workspace has, and the only ones it has.</summary>
        internal static readonly string[] Tables =
        [
            "ImportSource",
            "Conversation",
            "Participant",
            "ConversationParticipant",
            "Message",
            "MediaSource",
            "MediaAsset",
            "MediaFile",
            "MediaAssetFile",
            "Attachment",
        ];

        private const string PayloadHash =
            "AAAA000000000000000000000000000000000000000000000000000000000001";

        private const string SecondPayloadHash =
            "BBBB000000000000000000000000000000000000000000000000000000000002";

        /// <summary>
        /// The version-1 tables, frozen, exactly as the version-1 release created them.
        /// </summary>
        private static readonly string[] VersionOneSchemaStatements =
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
        /// The tables version 2 added, frozen, exactly as the version-2 release created them.
        /// </summary>
        private static readonly string[] VersionTwoSchemaStatements =
        [
            """
            CREATE TABLE MediaSource (
                MediaSourceID       INTEGER PRIMARY KEY,
                DisplayName         TEXT NOT NULL,
                SourceType          TEXT NOT NULL,
                RootPath            TEXT NOT NULL,
                DeviceDescription   TEXT NULL,
                ImportedDateTimeUtc TEXT NOT NULL
            );
            """,

            """
            CREATE TABLE MediaAsset (
                MediaAssetID INTEGER PRIMARY KEY,
                SHA256       TEXT NOT NULL COLLATE NOCASE,
                MediaType    TEXT NOT NULL,
                SizeBytes    INTEGER NOT NULL,
                DurationMS   INTEGER NULL,
                Width        INTEGER NULL,
                Height       INTEGER NULL,

                UNIQUE (SHA256),

                CHECK (length(SHA256) = 64),
                CHECK (SizeBytes >= 0),
                CHECK (DurationMS IS NULL OR DurationMS >= 0),
                CHECK (Width IS NULL OR Width > 0),
                CHECK (Height IS NULL OR Height > 0)
            );
            """,

            """
            CREATE TABLE MediaFile (
                MediaFileID    INTEGER PRIMARY KEY,
                MediaSourceID  INTEGER NOT NULL,
                RelativePath   TEXT NOT NULL,
                FileName       TEXT NOT NULL,
                Extension      TEXT NULL,
                SizeBytes      INTEGER NOT NULL,
                SHA256         TEXT NULL COLLATE NOCASE,
                MediaType      TEXT NOT NULL,
                FileDate       TEXT NULL,
                IsSent         INTEGER NULL,
                DurationMS     INTEGER NULL,
                Width          INTEGER NULL,
                Height         INTEGER NULL,

                UNIQUE (MediaSourceID, RelativePath),

                CHECK (SizeBytes >= 0),
                CHECK (SHA256 IS NULL OR length(SHA256) = 64),
                CHECK (IsSent IS NULL OR IsSent IN (0, 1)),
                CHECK (DurationMS IS NULL OR DurationMS >= 0),
                CHECK (Width IS NULL OR Width > 0),
                CHECK (Height IS NULL OR Height > 0),

                FOREIGN KEY (MediaSourceID)
                    REFERENCES MediaSource (MediaSourceID)
            );
            """,

            """
            CREATE TABLE MediaAssetFile (
                MediaAssetFileID INTEGER PRIMARY KEY,
                MediaAssetID     INTEGER NOT NULL,
                MediaFileID      INTEGER NOT NULL,

                UNIQUE (MediaFileID),

                FOREIGN KEY (MediaAssetID)
                    REFERENCES MediaAsset (MediaAssetID),

                FOREIGN KEY (MediaFileID)
                    REFERENCES MediaFile (MediaFileID)
            );
            """,

            """
            CREATE TABLE Attachment (
                AttachmentID         INTEGER PRIMARY KEY,
                MessageID            INTEGER NOT NULL,
                Ordinal              INTEGER NOT NULL,
                ExpectedMediaType    TEXT NULL,
                ResolutionStatus     TEXT NOT NULL,
                ResolvedMediaAssetID INTEGER NULL,

                UNIQUE (MessageID, Ordinal),

                CHECK (Ordinal > 0),
                CHECK (ResolutionStatus IN ('Unresolved', 'Resolved')),
                CHECK (
                    (ResolutionStatus = 'Unresolved' AND ResolvedMediaAssetID IS NULL)
                    OR
                    (ResolutionStatus = 'Resolved' AND ResolvedMediaAssetID IS NOT NULL)
                ),

                FOREIGN KEY (MessageID)
                    REFERENCES Message (MessageID),

                FOREIGN KEY (ResolvedMediaAssetID)
                    REFERENCES MediaAsset (MediaAssetID)
            );
            """,
        ];

        /// <summary>
        /// The message contents the fixture stores, in sequence order. Sequence 3 is exactly the
        /// media placeholder and is the message the fixture's one attachment belongs to.
        /// </summary>
        private static readonly string[] MessageContents =
        [
            "Hi Sam",
            "Hi Alex",
            ParsedMessage.MediaPlaceholderContent,
            "See you tomorrow",
        ];

        /// <summary>How many messages the fixture stores.</summary>
        internal static int MessageCount => MessageContents.Length;

        /// <summary>How many attachments the fixture stores.</summary>
        internal const int AttachmentCount = 1;

        /// <summary>
        /// Creates a populated version-2 database at <paramref name="databasePath"/>.
        /// </summary>
        /// <remarks>
        /// Written through a plain connection rather than through the data layer's own, so not even
        /// the connection string comes from the code being tested. Foreign keys are enforced, as a
        /// real version-2 workspace enforced them.
        /// </remarks>
        internal static void Create(string databasePath)
        {
            using var connection = OpenPlainConnection(databasePath, create: true);

            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;

                foreach (var statement in VersionOneSchemaStatements.Concat(VersionTwoSchemaStatements))
                {
                    command.CommandText = statement;
                    command.ExecuteNonQuery();
                }

                command.CommandText = "PRAGMA user_version = 2;";
                command.ExecuteNonQuery();
            }

            Populate(connection, transaction);

            transaction.Commit();
        }

        /// <summary>
        /// Fills every version-2 table, including the deduplication relationship and one unresolved
        /// attachment.
        /// </summary>
        /// <remarks>
        /// The two media files carry the same payload hash, so one asset genuinely has two physical
        /// copies — the shape a reconciliation comparison should have to walk rather than a single
        /// row per table. Nullable columns are deliberately left null on one file and filled on the
        /// other, so a comparison that mishandled nulls would be caught.
        /// </remarks>
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

                INSERT INTO MediaSource (
                    DisplayName, SourceType, RootPath, DeviceDescription, ImportedDateTimeUtc)
                VALUES ('Handset copy', 'PhoneCopy', 'MediaRoot', 'Handset', '{ImportedDateTimeUtcText}');

                INSERT INTO MediaSource (
                    DisplayName, SourceType, RootPath, DeviceDescription, ImportedDateTimeUtc)
                VALUES ('Legacy copy', 'PhoneCopy', 'LegacyRoot', NULL, '{ImportedDateTimeUtcText}');

                INSERT INTO MediaAsset (SHA256, MediaType, SizeBytes, DurationMS, Width, Height)
                VALUES ('{PayloadHash}', 'Image', 2048, NULL, 640, 480);

                INSERT INTO MediaAsset (SHA256, MediaType, SizeBytes)
                VALUES ('{SecondPayloadHash}', 'Video', 4096);

                INSERT INTO MediaFile (
                    MediaSourceID, RelativePath, FileName, Extension, SizeBytes, SHA256, MediaType,
                    FileDate, IsSent, DurationMS, Width, Height)
                VALUES (
                    1, 'Images/IMG-20260105-WA0001.jpg', 'IMG-20260105-WA0001.jpg', '.jpg', 2048,
                    '{PayloadHash}', 'Image', '2026-01-05', 0, NULL, 640, 480);

                INSERT INTO MediaFile (
                    MediaSourceID, RelativePath, FileName, Extension, SizeBytes, SHA256, MediaType,
                    FileDate, IsSent, DurationMS, Width, Height)
                VALUES (
                    2, 'Images/Sent/IMG-20260105-WA0002.jpg', 'IMG-20260105-WA0002.jpg', '.jpg', 2048,
                    '{PayloadHash}', 'Image', '2026-01-05', 1, NULL, 640, 480);

                INSERT INTO MediaFile (
                    MediaSourceID, RelativePath, FileName, SizeBytes, MediaType)
                VALUES (1, 'Video/unhashed.mp4', 'unhashed.mp4', 4096, 'Video');

                INSERT INTO MediaAssetFile (MediaAssetID, MediaFileID) VALUES (1, 1);
                INSERT INTO MediaAssetFile (MediaAssetID, MediaFileID) VALUES (1, 2);
                """;

            command.ExecuteNonQuery();

            InsertMessages(command);

            // One unresolved attachment on the placeholder message, exactly as version 2's own
            // backfill produced. Nothing here is resolved: no version-2 workspace ever was.
            command.CommandText =
                """
                INSERT INTO Attachment (
                    MessageID, Ordinal, ExpectedMediaType, ResolutionStatus, ResolvedMediaAssetID)
                SELECT MessageID, 1, NULL, 'Unresolved', NULL
                FROM Message
                WHERE SequenceNumber = 3;
                """;

            command.ExecuteNonQuery();
        }

        private static void InsertMessages(SqliteCommand command)
        {
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
                senderParticipantID.Value = (index % 2) + 1;
                messageContent.Value = content;
                rawContent.Value = $"2026/01/05, 14:03 - Alex: {content}";
                sourceLineStart.Value = index + 1;
                sourceLineEnd.Value = index + 1;

                command.ExecuteNonQuery();
            }

            command.Parameters.Clear();
        }

        /// <summary>
        /// Opens a plain connection to the fixture database, with foreign keys enforced and without
        /// going through the data layer.
        /// </summary>
        internal static SqliteConnection OpenPlainConnection(
            string databasePath, bool create = false)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
                ForeignKeys = true,
            }.ToString();

            var connection = new SqliteConnection(connectionString);
            connection.Open();

            return connection;
        }
    }
}
