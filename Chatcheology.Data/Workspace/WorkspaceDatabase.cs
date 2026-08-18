using System.Globalization;
using Chatcheology.Core.Models;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Workspace
{
    /// <summary>
    /// Creates, migrates and opens the workspace SQLite database and owns its schema definition.
    /// </summary>
    /// <remarks>
    /// The workspace database holds reconstruction state. It is deliberately created and opened
    /// through this one type so that every connection is built the same way and foreign-key
    /// enforcement can never be accidentally left off.
    /// <para>
    /// The schema version lives in SQLite's own <c>PRAGMA user_version</c> header field rather than
    /// in a table of our own, so a workspace file describes itself without needing to be queried
    /// through application code. There is no migration engine: version 0 is created as the current
    /// schema outright, version 1 is migrated by one explicit step, version
    /// <see cref="SchemaVersion"/> is accepted, and anything else is rejected.
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
        public const int SchemaVersion = 2;

        /// <summary>
        /// The <c>user_version</c> of a SQLite database that has never had a workspace schema
        /// applied to it. This is also the value of a brand-new empty file.
        /// </summary>
        internal const int UninitialisedSchemaVersion = 0;

        /// <summary>
        /// The one historical schema version this build can migrate forward.
        /// </summary>
        internal const int VersionOneSchemaVersion = 1;

        /// <summary>
        /// The stored <c>Attachment.ResolutionStatus</c> of an attachment that is not linked to a
        /// media asset.
        /// </summary>
        /// <remarks>
        /// Schema vocabulary lives with the schema. The importer writes the same value the migration
        /// backfills, so the two paths cannot drift into storing different text for one state.
        /// </remarks>
        internal const string UnresolvedAttachmentStatus = "Unresolved";

        /// <summary>
        /// The <c>Attachment.Ordinal</c> of the single attachment an exact media placeholder implies.
        /// </summary>
        /// <remarks>
        /// The supported export format marks one omitted media item per placeholder message, so
        /// every attachment created from a placeholder is the first and only one. The column exists
        /// so the schema does not permanently assume one attachment per message; nothing in this
        /// phase has source evidence for a second ordinal.
        /// </remarks>
        internal const int MediaPlaceholderAttachmentOrdinal = 1;

        /// <summary>
        /// How every workspace metadata timestamp is stored: the round-trip format, which on a
        /// <see cref="DateTimeKind.Utc"/> value ends in <c>Z</c> and reads back as UTC.
        /// </summary>
        /// <remarks>
        /// One constant rather than one per writer. An import source's timestamp and a media
        /// source's timestamp mean the same kind of thing — when a workspace operation happened —
        /// and two copies of the format string could drift into storing that one meaning two ways.
        /// <para>
        /// Deliberately not used for a source message's wall-clock timestamp, which is a different
        /// kind of value and is formatted separately where it is written.
        /// </para>
        /// </remarks>
        internal const string UtcTimestampFormat = "O";

        /// <summary>
        /// The version-1 tables, one statement per element, in creation order.
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
        /// <para>
        /// Kept verbatim from version 1. A workspace created at version 1 and then migrated must
        /// end up the same database as one created fresh at version 2, and the surest way to
        /// guarantee that is for both to apply the version-2 statements to this same starting point.
        /// </para>
        /// </remarks>
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
        /// The tables version 2 adds, one statement per element, in creation order.
        /// </summary>
        /// <remarks>
        /// SQLite resolves foreign keys lazily, so a referenced table need not already exist when a
        /// child table is created. The order is still the dependency order, for the same readability
        /// reason as version 1, and it means foreign-key enforcement never has to be switched off to
        /// apply this schema.
        /// <para>
        /// No performance indexes are declared. The indexes implied by the primary keys and unique
        /// constraints are what the schema's own guarantees need, and the access patterns that would
        /// justify more do not exist yet. <c>UNIQUE (MessageID, Ordinal)</c> already indexes
        /// <c>Attachment.MessageID</c> as its leftmost column.
        /// </para>
        /// </remarks>
        private static readonly string[] VersionTwoSchemaStatements =
        [
            // One user-selected physical media root. A workspace may legitimately record an absolute
            // local path here, because it is reconstruction state describing this machine rather
            // than the portable archive that gets shared. Nothing in this phase reads, validates or
            // assumes a path.
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

            // One unique media payload, identified by content. SHA-256 is the identity; there is no
            // perceptual or fuzzy hashing, so two files are one asset only when their bytes match.
            //
            // The hash column carries NOCASE so the same hash written once in upper case and once
            // in lower case cannot become two assets. NOCASE folds ASCII only, which is exactly and
            // only what hexadecimal needs. The length is constrained here because a value that is
            // not 64 characters cannot be a SHA-256 at all; validating the characters themselves is
            // left to the hashing phase's code, which also canonicalises a hash to the project's
            // upper-case convention, rather than being spelled out as an unreadable SQL expression.
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

            // One physical file discovered beneath one MediaSource. RelativePath is relative to
            // MediaSource.RootPath, and the pair is unique because one path under one root is one
            // file. The canonical form of that path — relative, no leading separator, no traversal
            // segments, "/" as the separator, source spelling otherwise preserved — belongs to the
            // inventory code when that code exists; as a SQL CHECK it would be unreadable and still
            // not a real guarantee.
            //
            // SHA256 is nullable because discovery legitimately precedes hashing. MediaType has no
            // CHECK because the vocabulary is the inventory phase's to define from the file types
            // actually found, rather than something to guess at now.
            //
            // FileDate has one narrow meaning: a calendar date reliably extracted from the file's
            // source naming convention, stored as yyyy-MM-dd, for later matching evidence. It is
            // never a filesystem timestamp, an EXIF capture time or a message timestamp, and it is
            // NULL when no reliable naming-derived date exists.
            //
            // IsSent is tri-state on purpose: 1 for a known Sent path, 0 for a known non-Sent path,
            // NULL for unknown. A two-state column would force "unknown" to be recorded as "not
            // sent", which is evidence the file never provided.
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

            // Which physical files carry which unique payload: many MediaFile rows to one
            // MediaAsset. UNIQUE (MediaFileID) states the deduplication rule as a constraint — one
            // physical file has one content identity. A separate table rather than a nullable column
            // on MediaFile, so the link can later carry facts of its own without altering the
            // physical-file record.
            //
            // Not every MediaFile has a row here: an inventoried file that has not been hashed yet
            // has no known asset.
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

            // One expected media item belonging to a message, made explicit instead of being
            // re-derived from message text every time it is needed.
            //
            // ResolutionStatus records one thing only: whether this attachment is linked to a
            // MediaAsset. It is deliberately not a matching or confidence state — candidates,
            // evidence and decisions are later tables, and admitting their vocabulary here would
            // invite reading a guess as a fact. The paired CHECK makes the two halves of the
            // statement inseparable: Unresolved has no asset, Resolved has one.
            //
            // ExpectedMediaType is nullable because the supported export format does not reveal
            // whether an omitted item was an image, a video, audio or a document, and nothing here
            // infers it from surrounding text, dates or files on disk.
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
        /// Creates one unresolved attachment for every already-persisted message whose content is
        /// exactly the media placeholder.
        /// </summary>
        /// <remarks>
        /// The comparison is exact equality against a parameter, under SQLite's default BINARY text
        /// semantics stated explicitly, so a message that merely contains the placeholder inside
        /// longer text — a placeholder carrying a caption, for instance — creates no attachment.
        /// No <c>LIKE</c>, no substring matching and no re-parsing of <c>RawContent</c>.
        /// <para>
        /// This mirrors <see cref="ParsedMessage.IsMediaPlaceholder"/>, which is ordinal equality
        /// against the same constant. The parser has already removed the U+200E and U+200F
        /// direction marks from the content it persisted, so a placeholder that carried one in the
        /// export is still matched here without the comparison itself being loosened.
        /// </para>
        /// </remarks>
        private static readonly string BackfillPlaceholderAttachmentsStatement =
            $"""
            INSERT INTO Attachment (
                MessageID,
                Ordinal,
                ExpectedMediaType,
                ResolutionStatus,
                ResolvedMediaAssetID)
            SELECT
                MessageID,
                {MediaPlaceholderAttachmentOrdinal},
                NULL,
                '{UnresolvedAttachmentStatus}',
                NULL
            FROM Message
            WHERE MessageContent = $mediaPlaceholderContent COLLATE BINARY;
            """;

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
        public static string BuildConnectionString(string databasePath) =>
            BuildConnectionString(databasePath, SqliteOpenMode.ReadWriteCreate);

        /// <summary>
        /// Builds the connection string for a file-backed workspace database opened in
        /// <paramref name="mode"/>.
        /// </summary>
        /// <remarks>
        /// The one place a workspace connection string is assembled, so that a read-only open and a
        /// read-write open differ in exactly one property and cannot drift apart in any other.
        /// <para>
        /// <c>ReadWriteCreate</c> lets SQLite create the database file itself. It does not create
        /// missing parent directories, and no directory structure is invented on the caller's
        /// behalf. <c>ReadOnly</c> cannot create anything at all.
        /// </para>
        /// </remarks>
        private static string BuildConnectionString(string databasePath, SqliteOpenMode mode)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = mode,
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
        /// Opens an existing workspace read-only, for inspection that must not be able to change
        /// what it is inspecting.
        /// </summary>
        /// <param name="databasePath">
        /// The path of a workspace database that already exists. Nothing is created here under any
        /// circumstances.
        /// </param>
        /// <returns>An open read-only connection. The caller owns it and must dispose it.</returns>
        /// <remarks>
        /// The guarantee is enforced by SQLite itself through <c>Mode=ReadOnly</c> rather than by
        /// the caller promising not to write, so a stray <c>INSERT</c> or <c>PRAGMA user_version</c>
        /// write fails instead of succeeding quietly.
        /// <para>
        /// The missing-file check is deliberate and comes first. Requesting <c>ReadOnly</c> already
        /// prevents SQLite from creating a file, but the failure it produces is a low-level "unable
        /// to open database file" that reads as a corrupt or locked workspace rather than as a path
        /// that holds nothing. Callers verifying that an expected workspace is present deserve to
        /// be told which of those two it is.
        /// </para>
        /// <para>
        /// Read-only is not the same as unobserved: Microsoft.Data.Sqlite pools connections by
        /// connection string, so a disposed read-only connection can still hold the file open until
        /// the pool is cleared. Code that goes on to read, hash or copy the database file itself
        /// must call <see cref="SqliteConnection.ClearAllPools"/> first.
        /// </para>
        /// </remarks>
        /// <exception cref="FileNotFoundException">
        /// There is no file at <paramref name="databasePath"/>. No file is created.
        /// </exception>
        public static SqliteConnection OpenReadOnlyConnection(string databasePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

            if (!File.Exists(databasePath))
            {
                throw new FileNotFoundException(
                    "There is no workspace database at the supplied path. A read-only open " +
                    "inspects a workspace that already exists; it never creates one, and no file " +
                    "has been created here.",
                    databasePath);
            }

            var connection =
                new SqliteConnection(BuildConnectionString(databasePath, SqliteOpenMode.ReadOnly));

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
        /// <see cref="SchemaVersion"/> for a current workspace, 1 for a workspace this build can
        /// migrate, 0 for a database that has never had a workspace schema applied, or any other
        /// value written by a build that is not this one.
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
        /// <see cref="SchemaVersion"/>, creating or migrating it as its current version requires.
        /// </summary>
        /// <remarks>
        /// A database with no workspace schema is created as the complete current schema outright
        /// rather than created at version 1 and then migrated, and a version-1 workspace is migrated
        /// by one explicit step. Both paths apply the same version-2 statements, so they converge on
        /// the same schema by construction rather than by two definitions being kept in agreement by
        /// hand.
        /// <para>
        /// Each path is a single transaction covering the table definitions, any backfill and the
        /// <c>user_version</c> write. SQLite treats all three as transactional work — schema changes
        /// and the header field alike — so a failure part way through rolls the whole attempt back.
        /// There is no state in which a partially created schema claims to be version
        /// <see cref="SchemaVersion"/>, and none in which a half-finished migration leaves a
        /// version-1 workspace altered.
        /// </para>
        /// <para>
        /// An already-current workspace is accepted and left completely untouched, so calling this
        /// repeatedly cannot destroy existing data.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The database reports a schema version this build does not support.
        /// </exception>
        /// <exception cref="SqliteException">
        /// The schema could not be created or migrated — for example because the database already
        /// contains an object whose name collides with a workspace table, which is a malformed
        /// workspace rather than a supported starting point. The database is left as it was found.
        /// </exception>
        public static void Initialise(string databasePath)
        {
            using var connection = OpenConnection(databasePath);

            var existingVersion = ReadSchemaVersion(connection);

            if (existingVersion == SchemaVersion)
            {
                return;
            }

            switch (existingVersion)
            {
                case UninitialisedSchemaVersion:
                    CreateCurrentSchema(connection);
                    break;

                case VersionOneSchemaVersion:
                    MigrateVersionOneToVersionTwo(connection);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"The workspace database reports schema version {existingVersion}, which " +
                        $"this build does not support. This build creates and accepts version " +
                        $"{SchemaVersion} and can migrate version {VersionOneSchemaVersion}.");
            }
        }

        /// <summary>
        /// Creates the complete current schema in a database that has none.
        /// </summary>
        private static void CreateCurrentSchema(SqliteConnection connection)
        {
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;

                foreach (var statement in VersionOneSchemaStatements)
                {
                    command.CommandText = statement;
                    command.ExecuteNonQuery();
                }

                foreach (var statement in VersionTwoSchemaStatements)
                {
                    command.CommandText = statement;
                    command.ExecuteNonQuery();
                }

                SetSchemaVersion(command);
            }

            transaction.Commit();
        }

        /// <summary>
        /// Migrates a version-1 workspace to version 2 in one transaction.
        /// </summary>
        /// <remarks>
        /// The version-1 tables, columns and rows are left exactly as they are. Nothing is rewritten
        /// and nothing is dropped: the migration adds the version-2 tables and derives attachments
        /// from messages that are already persisted.
        /// <para>
        /// Foreign keys stay enforced throughout. The new tables are created in dependency order and
        /// the only rows inserted reference messages that already exist, so there is no point at
        /// which the constraints would have to be relaxed.
        /// </para>
        /// </remarks>
        private static void MigrateVersionOneToVersionTwo(SqliteConnection connection)
        {
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;

                foreach (var statement in VersionTwoSchemaStatements)
                {
                    command.CommandText = statement;
                    command.ExecuteNonQuery();
                }

                command.CommandText = BackfillPlaceholderAttachmentsStatement;
                command.Parameters.AddWithValue(
                    "$mediaPlaceholderContent", ParsedMessage.MediaPlaceholderContent);
                command.ExecuteNonQuery();

                // The pragma that follows carries no parameters, and a leftover one would be an
                // unused-parameter error rather than a silent no-op.
                command.Parameters.Clear();

                SetSchemaVersion(command);
            }

            transaction.Commit();
        }

        /// <summary>
        /// Writes <c>PRAGMA user_version</c>, the last step of creation and of migration.
        /// </summary>
        /// <remarks>
        /// A pragma value cannot be parameterised. The interpolated value is a private integer
        /// constant, not caller input.
        /// </remarks>
        private static void SetSchemaVersion(SqliteCommand command)
        {
            command.CommandText = $"PRAGMA user_version = {SchemaVersion};";
            command.ExecuteNonQuery();
        }
    }
}
