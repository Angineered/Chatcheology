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
    /// schema outright, versions 1 and 2 are each migrated by one explicit step, version
    /// <see cref="SchemaVersion"/> is accepted, and anything else is rejected.
    /// </para>
    /// <para>
    /// Nothing here acquires <see cref="WorkspaceExclusiveLock"/>. Creating or migrating a workspace
    /// is the same operation it has always been, and every existing caller keeps its behaviour; the
    /// exclusive lock is owned by whichever caller needs a whole protocol protected, which is what
    /// the real Phase 7 migration protocol does around this method rather than inside it.
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
        public const int SchemaVersion = 3;

        /// <summary>
        /// The <c>user_version</c> of a SQLite database that has never had a workspace schema
        /// applied to it. This is also the value of a brand-new empty file.
        /// </summary>
        internal const int UninitialisedSchemaVersion = 0;

        /// <summary>
        /// The oldest historical schema version this build can migrate forward.
        /// </summary>
        internal const int VersionOneSchemaVersion = 1;

        /// <summary>
        /// The schema version immediately before the current one, and the version every workspace
        /// written by the previous build carries.
        /// </summary>
        internal const int VersionTwoSchemaVersion = 2;

        /// <summary>
        /// Every application table a version-2 workspace has, and the only ones it has.
        /// </summary>
        /// <remarks>
        /// One canonical list, shared by the pre-migration gate that refuses to migrate anything but
        /// exactly this set and by the reconciliation that proves a migration left all ten untouched.
        /// Two copies of it would be free to drift into disagreeing about what a version-2 workspace
        /// is, which is precisely the bookkeeping error this single definition exists to prevent.
        /// <para>
        /// The order is the version-1-then-version-2 creation order, not alphabetical, so it reads
        /// against the schema statements it describes.
        /// </para>
        /// </remarks>
        internal static readonly string[] VersionTwoTableNames =
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

        /// <summary>
        /// The three version-3 tables that hold human decisions rather than computed evidence.
        /// </summary>
        /// <remarks>
        /// One canonical list, for the same reason <see cref="VersionTwoTableNames"/> is one: the
        /// Stage 7.2 services write them, the Stage 7.1 publication tripwire detects a change a run
        /// observes in their row count or primary-key aggregates, and the structural tests prove the
        /// Stage 7.1 writer names none of them. Separate copies of the list would be free to drift into
        /// disagreeing about which tables carry decisions.
        /// <para>
        /// Each of those three does a different job and none of them does all of it. The tripwire is
        /// four aggregates and can collide, so it establishes neither the exact primary-key set nor
        /// anything about non-key columns; the structural no-DML-path tests are what cover the columns
        /// it cannot see; and whole-row preservation is established under test by
        /// <c>WorkspaceCheckpoint.RequireIdenticalTables</c>, which needs a checkpoint a run must never
        /// create.
        /// </para>
        /// <para>
        /// Every one of them declares its own <c>&lt;TableName&gt;ID INTEGER PRIMARY KEY</c>, which is
        /// the convention the publication tripwire relies on to name each table's key column.
        /// </para>
        /// <para>
        /// The order is the version-3 creation order, matching the schema statements below.
        /// </para>
        /// </remarks>
        internal static readonly string[] VersionThreeDecisionTableNames =
        [
            "AttachmentReviewEvent",
            "AttachmentCandidateDispositionEvent",
            "AttachmentResolutionEvent",
        ];

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
        /// The tables and indexes version 3 adds, one statement per element, in creation order.
        /// </summary>
        /// <remarks>
        /// These are the Phase 7 evidence and decision tables. They are purely additive: no version-1
        /// or version-2 table, column or constraint is altered by any statement here, and the only
        /// relationship to the existing schema is a child foreign key pointing at it.
        /// <para>
        /// Every table starts and stays empty as far as this file is concerned. There is no backfill
        /// and no derivation: computing evidence, ranking candidates, recording a review and
        /// resolving an attachment are all later milestones with their own code, and a migration that
        /// invented a single row of any of it would be asserting a matching decision the project has
        /// not earned.
        /// </para>
        /// <para>
        /// The vocabularies are spelled out as <c>CHECK</c> constraints rather than left to
        /// application code, for the reason version 2 already applies to
        /// <c>Attachment.ResolutionStatus</c>: a value outside the agreed set is not a value this
        /// project has a meaning for, and the database is the one place that cannot be bypassed.
        /// </para>
        /// <para>
        /// Three explicit indexes are declared, and only three. The event tables carry no
        /// <c>UNIQUE</c> constraint to index their attachment or candidate column as a side effect,
        /// and every later milestone reads them that way; everywhere else the primary-key and unique
        /// constraints already index what a lookup would use, so declaring more would be indexing
        /// against query code that does not exist yet.
        /// </para>
        /// </remarks>
        private static readonly string[] VersionThreeSchemaStatements =
        [
            // One Phase 7 evidence run. Status is a lifecycle, not an outcome: a run that has not
            // finished, or that failed, must never be mistaken for the current evidence, so the
            // paired CHECK makes a completed run inseparable from the moment it completed.
            //
            // PreRunWorkspaceFileSha256 is the byte identity of the workspace file immediately
            // before this run's first write. It is deliberately not an evidence fingerprint: once
            // Phase 7 writes to the workspace, this value legitimately differs between runs because
            // of Phase 7's own accumulating history, and a difference between two runs' values is
            // never evidence that the underlying Phase 6 evidence changed.
            """
            CREATE TABLE Phase7Run (
                RunID                      INTEGER PRIMARY KEY,
                StartedAtUtc               TEXT NOT NULL,
                CompletedAtUtc             TEXT NULL,
                Status                     TEXT NOT NULL,
                PreRunWorkspaceFileSha256  TEXT NOT NULL COLLATE NOCASE,
                RankingDimensionSetVersion TEXT NOT NULL,

                CHECK (Status IN ('Running', 'Completed', 'Failed')),
                CHECK (Status <> 'Completed' OR CompletedAtUtc IS NOT NULL),
                CHECK (length(PreRunWorkspaceFileSha256) = 64),
                CHECK (length(RankingDimensionSetVersion) > 0)
            );
            """,

            // One row per attachment per run, including every attachment that produced no candidate
            // at all. That is the whole point of this table: a zero count is a recorded finding, and
            // the absence of a row means the attachment was not evaluated in that run. Those are
            // different facts and must not look alike.
            //
            // IsWithinDirectionImpossibleGroup and IsForcedPositionGroupShape are group-shape facts
            // about the attachment's message, not about any candidate, which is why they live here
            // rather than on a candidate row. RemainingCompatiblePositionCountUnderHypothesis is
            // explanatory context only: nothing ranks by it, because a smaller surviving count is
            // not a claim that any individual candidate is more likely to be correct.
            """
            CREATE TABLE AttachmentEvidenceSnapshot (
                AttachmentEvidenceSnapshotID                    INTEGER PRIMARY KEY,
                RunID                                           INTEGER NOT NULL,
                AttachmentID                                    INTEGER NOT NULL,
                ExactDateCandidateCount                         INTEGER NOT NULL,
                AdjacentDateContextCount                        INTEGER NOT NULL,
                HasAnyCompatibleAnnotatedCandidate              INTEGER NOT NULL,
                HasAnyContradictoryOnlyAnnotatedCandidate       INTEGER NOT NULL,
                IsWithinDirectionImpossibleGroup                INTEGER NOT NULL,
                IsForcedPositionGroupShape                      INTEGER NOT NULL,
                RemainingCompatiblePositionCountUnderHypothesis  INTEGER NULL,

                UNIQUE (RunID, AttachmentID),

                CHECK (ExactDateCandidateCount >= 0),
                CHECK (AdjacentDateContextCount >= 0),
                CHECK (HasAnyCompatibleAnnotatedCandidate IN (0, 1)),
                CHECK (HasAnyContradictoryOnlyAnnotatedCandidate IN (0, 1)),
                CHECK (IsWithinDirectionImpossibleGroup IN (0, 1)),
                CHECK (IsForcedPositionGroupShape IN (0, 1)),
                CHECK (
                    RemainingCompatiblePositionCountUnderHypothesis IS NULL
                    OR RemainingCompatiblePositionCountUnderHypothesis >= 0
                ),

                FOREIGN KEY (RunID) REFERENCES Phase7Run (RunID),
                FOREIGN KEY (AttachmentID) REFERENCES Attachment (AttachmentID)
            );
            """,

            // One exact-date candidate relation: this attachment, this deduplicated payload, in this
            // run. Exact date only. Adjacent-date evidence is a weaker, separately modelled context
            // and is never promoted into this table, however empty this table is for an attachment.
            //
            // DirectionCompatibilityAnnotation is the one candidate-level aggregate here, because it
            // already had defined many-to-one semantics before Phase 7 existed: the matching code
            // computes exactly this verdict from a relationship's supporting copies. Everything else
            // Phase 6 established about sequence behaviour is a property of one physical occurrence
            // and lives in the occurrence table, not here.
            //
            // ContradictoryOnly is a recorded annotation and never an exclusion. A duplicate payload
            // can survive in another chat's or another device's history while the copy that belonged
            // to this conversation is gone, so a candidate annotated that way stays a candidate.
            """
            CREATE TABLE AttachmentCandidate (
                AttachmentCandidateID            INTEGER PRIMARY KEY,
                RunID                            INTEGER NOT NULL,
                AttachmentID                     INTEGER NOT NULL,
                MediaAssetID                     INTEGER NOT NULL,
                DirectionCompatibilityAnnotation TEXT NOT NULL,

                UNIQUE (RunID, AttachmentID, MediaAssetID),

                CHECK (DirectionCompatibilityAnnotation IN
                    ('Unknown', 'Compatible', 'Mixed', 'ContradictoryOnly')),

                FOREIGN KEY (RunID) REFERENCES Phase7Run (RunID),
                FOREIGN KEY (AttachmentID) REFERENCES Attachment (AttachmentID),
                FOREIGN KEY (MediaAssetID) REFERENCES MediaAsset (MediaAssetID)
            );
            """,

            // Sequence evidence at the scope Phase 6 actually established it: one physical file, not
            // one deduplicated payload. Two supporting copies of the same payload on the same date
            // legitimately carry different tokens, so a single asset-level token or order verdict
            // would be a property no file ever had.
            //
            // MediaFileID is a plain reference. No file name and no path is stored here: the token is
            // the small integer the approved grammar already reduces a name to, which is a derived
            // fact rather than recovered private data.
            //
            // The grammar pairings are biconditional on purpose. If the grammar parsed, both derived
            // facts are computable and must be present; if it did not, neither exists and the order
            // reading is not assessable. A half-populated occurrence row would otherwise reach a
            // later ranking computation looking like a computed negative.
            //
            // The collision rule is stated with IS NOT rather than an equality that NULL would make
            // unknown, so the constraint says what it means on its own instead of depending on the
            // grammar constraints above it to rule the NULL case out.
            """
            CREATE TABLE AttachmentCandidateOccurrenceEvidence (
                AttachmentCandidateOccurrenceEvidenceID INTEGER PRIMARY KEY,
                AttachmentCandidateID                   INTEGER NOT NULL,
                MediaFileID                             INTEGER NOT NULL,
                TokenGrammarSupported                   INTEGER NOT NULL,
                SequenceToken                           INTEGER NULL,
                DateTokenKeyUniqueToPayload             INTEGER NULL,
                OrderConsistencyAnnotation              TEXT NOT NULL,

                UNIQUE (AttachmentCandidateID, MediaFileID),

                CHECK (TokenGrammarSupported IN (0, 1)),
                CHECK (SequenceToken IS NULL OR SequenceToken BETWEEN 0 AND 9999),
                CHECK (DateTokenKeyUniqueToPayload IS NULL OR DateTokenKeyUniqueToPayload IN (0, 1)),
                CHECK (OrderConsistencyAnnotation IN (
                    'NotAssessable',
                    'NoCompatibleArrangement',
                    'CompatibleWithAmbiguity',
                    'UniquelyConstrained')),

                CHECK (SequenceToken IS NULL OR TokenGrammarSupported = 1),
                CHECK (TokenGrammarSupported = 0 OR SequenceToken IS NOT NULL),
                CHECK (DateTokenKeyUniqueToPayload IS NULL OR TokenGrammarSupported = 1),
                CHECK (TokenGrammarSupported = 0 OR DateTokenKeyUniqueToPayload IS NOT NULL),

                CHECK (TokenGrammarSupported = 1 OR OrderConsistencyAnnotation = 'NotAssessable'),
                CHECK (
                    DateTokenKeyUniqueToPayload IS NOT 0
                    OR OrderConsistencyAnnotation = 'NotAssessable'
                ),

                FOREIGN KEY (AttachmentCandidateID)
                    REFERENCES AttachmentCandidate (AttachmentCandidateID),

                FOREIGN KEY (MediaFileID) REFERENCES MediaFile (MediaFileID)
            );
            """,

            // Adjacent-date context, kept deliberately inert. It carries no evidence column and no
            // ranking column at all, so there is nothing here a later ranking computation could read
            // even by accident. It exists so that an attachment whose exact-date set is empty still
            // has its weaker context recorded and visible rather than silently absent.
            """
            CREATE TABLE AttachmentAdjacentDateContext (
                AttachmentAdjacentDateContextID INTEGER PRIMARY KEY,
                RunID                           INTEGER NOT NULL,
                AttachmentID                    INTEGER NOT NULL,
                MediaAssetID                    INTEGER NOT NULL,
                AdjacentOffsetDays              INTEGER NOT NULL,

                UNIQUE (RunID, AttachmentID, MediaAssetID, AdjacentOffsetDays),

                CHECK (AdjacentOffsetDays IN (-1, 1)),

                FOREIGN KEY (RunID) REFERENCES Phase7Run (RunID),
                FOREIGN KEY (AttachmentID) REFERENCES Attachment (AttachmentID),
                FOREIGN KEY (MediaAssetID) REFERENCES MediaAsset (MediaAssetID)
            );
            """,

            // A human's review of one attachment, against one specific evidence run. SourceRunID is
            // mandatory because a review is always a review of a particular snapshot, never of "the
            // evidence" in the abstract.
            //
            // The one recordable outcome is that the evidence was reviewed and found insufficient.
            // Whether every candidate in a run has been independently disproven is derived by asking
            // that question of that run, never stored here, because a later run's candidate set would
            // make a stored answer quietly wrong.
            """
            CREATE TABLE AttachmentReviewEvent (
                AttachmentReviewEventID INTEGER PRIMARY KEY,
                AttachmentID            INTEGER NOT NULL,
                SourceRunID             INTEGER NOT NULL,
                ReviewOutcome           TEXT NOT NULL,
                ReviewNotes             TEXT NULL,
                ActorLabel              TEXT NULL,
                OccurredAtUtc           TEXT NOT NULL,

                CHECK (ReviewOutcome = 'ReviewedInsufficientEvidence'),
                CHECK (ReviewNotes IS NULL OR length(ReviewNotes) > 0),
                CHECK (ActorLabel IS NULL OR length(ActorLabel) > 0),

                FOREIGN KEY (AttachmentID) REFERENCES Attachment (AttachmentID),
                FOREIGN KEY (SourceRunID) REFERENCES Phase7Run (RunID)
            );
            """,

            """
            CREATE INDEX IX_AttachmentReviewEvent_AttachmentID
                ON AttachmentReviewEvent (AttachmentID);
            """,

            // One candidate ruled out, on a stated basis of its own. The reference is to the
            // candidate row of one run rather than to an attachment-and-asset pair, so a candidate
            // disproven under one run's evidence is not silently treated as disproven under a later
            // run's: that row is a different candidate record and has to be ruled out on its own.
            //
            // Reason is required and non-empty. Phase 6 ranking or support evidence is not sufficient
            // content for it — agreeing with a ranking is not disproving a candidate.
            """
            CREATE TABLE AttachmentCandidateDispositionEvent (
                AttachmentCandidateDispositionEventID INTEGER PRIMARY KEY,
                AttachmentCandidateID                 INTEGER NOT NULL,
                Disposition                           TEXT NOT NULL,
                Reason                                TEXT NOT NULL,
                ActorLabel                            TEXT NULL,
                OccurredAtUtc                         TEXT NOT NULL,

                CHECK (Disposition = 'IndependentlyDisproven'),
                CHECK (length(Reason) > 0),
                CHECK (ActorLabel IS NULL OR length(ActorLabel) > 0),

                FOREIGN KEY (AttachmentCandidateID)
                    REFERENCES AttachmentCandidate (AttachmentCandidateID)
            );
            """,

            """
            CREATE INDEX IX_AttachmentCandidateDispositionEvent_AttachmentCandidateID
                ON AttachmentCandidateDispositionEvent (AttachmentCandidateID);
            """,

            // The append-only history of every resolution decision. Nothing here is ever updated or
            // deleted: undoing a decision is a new event that says so, which is why there is no
            // reversal timestamp to write into an existing row.
            //
            // A confirmation must carry an independently justified basis and a description of it. The
            // vocabulary is closed at two values on purpose, and neither of them can express "the
            // evidence was convincing": no combination of Phase 6 evidence identifies an individual
            // attachment, so there is deliberately no way to record one as though it did.
            //
            // A revocation carries its own reason instead. Why a resolution is no longer believed is
            // a different question from what established it, and one field holding both would blur
            // that for every later reader.
            //
            // SupersedesEventID is null exactly when the event is a Confirm: a fresh confirmation has
            // nothing to supersede, and a replacement or revocation must say what it acts against.
            // The composite foreign key makes the superseded event's attachment the same attachment,
            // the way Message's composite key makes a sender belong to the message's own
            // conversation, and it needs the UNIQUE below as its parent key. SQLite's MATCH SIMPLE
            // semantics leave it unenforced when SupersedesEventID is null, which is precisely the
            // Confirm case.
            //
            // That the superseded event is the attachment's *current effective* one is a fact about
            // current state rather than about this row, so no CHECK or foreign key can express it. It
            // is enforced by the service that writes these events, together with the projection onto
            // Attachment, in one transaction.
            """
            CREATE TABLE AttachmentResolutionEvent (
                AttachmentResolutionEventID INTEGER PRIMARY KEY,
                AttachmentID                INTEGER NOT NULL,
                EventType                   TEXT NOT NULL,
                MediaAssetID                INTEGER NULL,
                IndependentBasisType        TEXT NULL,
                IndependentBasisDescription TEXT NULL,
                RevocationReason            TEXT NULL,
                SupersedesEventID           INTEGER NULL,
                SourceRunID                 INTEGER NULL,
                ActorLabel                  TEXT NULL,
                OccurredAtUtc               TEXT NOT NULL,

                UNIQUE (AttachmentResolutionEventID, AttachmentID),

                CHECK (EventType IN ('Confirm', 'Revoke', 'ConfirmReplacement')),
                CHECK (IndependentBasisType IS NULL OR IndependentBasisType IN
                    ('HumanContentOrContextInspection', 'ExternalProvenanceOrGroundTruth')),
                CHECK (IndependentBasisDescription IS NULL
                    OR length(IndependentBasisDescription) > 0),
                CHECK (RevocationReason IS NULL OR length(RevocationReason) > 0),
                CHECK (ActorLabel IS NULL OR length(ActorLabel) > 0),

                CHECK (
                    (EventType IN ('Confirm', 'ConfirmReplacement')
                        AND MediaAssetID IS NOT NULL
                        AND IndependentBasisType IS NOT NULL
                        AND IndependentBasisDescription IS NOT NULL
                        AND RevocationReason IS NULL)
                    OR
                    (EventType = 'Revoke'
                        AND MediaAssetID IS NULL
                        AND IndependentBasisType IS NULL
                        AND IndependentBasisDescription IS NULL
                        AND RevocationReason IS NOT NULL)
                ),

                CHECK (EventType <> 'Confirm' OR SupersedesEventID IS NULL),
                CHECK (EventType = 'Confirm' OR SupersedesEventID IS NOT NULL),

                FOREIGN KEY (AttachmentID) REFERENCES Attachment (AttachmentID),
                FOREIGN KEY (MediaAssetID) REFERENCES MediaAsset (MediaAssetID),
                FOREIGN KEY (SourceRunID) REFERENCES Phase7Run (RunID),

                FOREIGN KEY (SupersedesEventID, AttachmentID)
                    REFERENCES AttachmentResolutionEvent (AttachmentResolutionEventID, AttachmentID)
            );
            """,

            """
            CREATE INDEX IX_AttachmentResolutionEvent_AttachmentID
                ON AttachmentResolutionEvent (AttachmentID);
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
                    MigrateVersionOneToVersionThree(connection);
                    break;

                case VersionTwoSchemaVersion:
                    MigrateVersionTwoToVersionThree(connection);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"The workspace database reports schema version {existingVersion}, which " +
                        $"this build does not support. This build creates and accepts version " +
                        $"{SchemaVersion} and can migrate versions {VersionOneSchemaVersion} and " +
                        $"{VersionTwoSchemaVersion}.");
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

                ApplyStatements(command, VersionOneSchemaStatements);
                ApplyStatements(command, VersionTwoSchemaStatements);
                ApplyStatements(command, VersionThreeSchemaStatements);

                SetSchemaVersion(command);
            }

            transaction.Commit();
        }

        /// <summary>
        /// Migrates a version-1 workspace to version <see cref="SchemaVersion"/> in one transaction.
        /// </summary>
        /// <remarks>
        /// The version-1 tables, columns and rows are left exactly as they are. Nothing is rewritten
        /// and nothing is dropped: the migration adds the version-2 tables, derives attachments from
        /// messages that are already persisted, and adds the version-3 tables.
        /// <para>
        /// One transaction covering both version steps, rather than two migrations run back to back.
        /// A version-1 workspace has always reached the current version in one atomic jump, and that
        /// contract does not change because "current" now means one version further on.
        /// </para>
        /// <para>
        /// Foreign keys stay enforced throughout. The new tables are created in dependency order and
        /// the only rows inserted reference messages that already exist, so there is no point at
        /// which the constraints would have to be relaxed.
        /// </para>
        /// </remarks>
        private static void MigrateVersionOneToVersionThree(SqliteConnection connection)
        {
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;

                ApplyStatements(command, VersionTwoSchemaStatements);

                command.CommandText = BackfillPlaceholderAttachmentsStatement;
                command.Parameters.AddWithValue(
                    "$mediaPlaceholderContent", ParsedMessage.MediaPlaceholderContent);
                command.ExecuteNonQuery();

                // The statements that follow carry no parameters, and a leftover one would be an
                // unused-parameter error rather than a silent no-op.
                command.Parameters.Clear();

                ApplyStatements(command, VersionThreeSchemaStatements);

                SetSchemaVersion(command);
            }

            transaction.Commit();
        }

        /// <summary>
        /// Migrates a version-2 workspace to version <see cref="SchemaVersion"/> in one transaction.
        /// </summary>
        /// <remarks>
        /// Purely additive. The version-2 tables, columns, constraints and rows are left exactly as
        /// they are; the migration only creates the Phase 7 tables and their indexes. There is no
        /// backfill, because no Phase 7 row can be derived from anything the workspace already holds
        /// without asserting a matching decision that has not been justified.
        /// <para>
        /// The table-set gate runs first, outside the transaction, so a workspace this build refuses
        /// to migrate is never written to at all rather than written to and rolled back.
        /// </para>
        /// </remarks>
        private static void MigrateVersionTwoToVersionThree(SqliteConnection connection)
        {
            RequireExactVersionTwoTableSet(connection);

            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;

                ApplyStatements(command, VersionThreeSchemaStatements);

                SetSchemaVersion(command);
            }

            transaction.Commit();
        }

        /// <summary>
        /// Refuses to migrate anything but a workspace holding exactly the version-2 application
        /// tables.
        /// </summary>
        /// <remarks>
        /// Phase 7 is the first phase that intentionally writes to a workspace holding evidence, so
        /// the migration establishes what it is migrating before it changes anything. An unexpected
        /// table means this is not the workspace the schema statements were written against, and a
        /// missing one means it is not a complete version-2 workspace; neither is a safe starting
        /// point, and neither is worth guessing about.
        /// <para>
        /// Deliberately archive-agnostic. It asserts the shape every version-2 workspace has, not the
        /// contents of any particular one: a synthetic workspace holding three messages and no
        /// attachments is a perfectly legitimate version-2 workspace and is accepted here. Verifying
        /// that a specific real archive still carries its expected counts belongs to the validation
        /// harness that knows which archive it is looking at, not to generic migration code.
        /// </para>
        /// <para>
        /// Baked into the migration path rather than offered as a guard a caller must remember to
        /// call, so that every migration — synthetic or real — is checked the same way.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The database does not hold exactly the version-2 application tables. Nothing has been
        /// written and the workspace is unchanged.
        /// </exception>
        private static void RequireExactVersionTwoTableSet(SqliteConnection connection)
        {
            var actual = ReadApplicationTableNames(connection);

            var expected = new SortedSet<string>(VersionTwoTableNames, StringComparer.Ordinal);

            var unexpected = actual.Except(expected, StringComparer.Ordinal).ToList();
            var missing = expected.Except(actual, StringComparer.Ordinal).ToList();

            if (unexpected.Count == 0 && missing.Count == 0)
            {
                return;
            }

            var detail = string.Join(
                " ",
                new[]
                {
                    missing.Count > 0 ? $"Missing: {string.Join(", ", missing)}." : null,
                    unexpected.Count > 0 ? $"Unexpected: {string.Join(", ", unexpected)}." : null,
                }.OfType<string>());

            throw new InvalidOperationException(
                $"The workspace database reports schema version {VersionTwoSchemaVersion} but does " +
                $"not hold exactly the {VersionTwoTableNames.Length} tables a version-" +
                $"{VersionTwoSchemaVersion} workspace has, so this build will not migrate it to " +
                $"version {SchemaVersion}. {detail} Nothing has been written, no version-" +
                $"{SchemaVersion} table has been created, and the workspace is still version " +
                $"{VersionTwoSchemaVersion}.");
        }

        /// <summary>
        /// Reads the names of the database's application tables, excluding SQLite's own internal
        /// ones.
        /// </summary>
        /// <remarks>
        /// The <c>sqlite_</c> prefix is reserved for SQLite's own objects, so excluding it by prefix
        /// is how an application table is distinguished from bookkeeping this project did not create.
        /// </remarks>
        internal static SortedSet<string> ReadApplicationTableNames(SqliteConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT name
                FROM sqlite_master
                WHERE type = 'table'
                  AND name NOT LIKE 'sqlite_%'
                ORDER BY name;
                """;

            var names = new SortedSet<string>(StringComparer.Ordinal);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }

        /// <summary>
        /// Executes one schema statement collection against an open command.
        /// </summary>
        private static void ApplyStatements(SqliteCommand command, string[] statements)
        {
            foreach (var statement in statements)
            {
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }
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
