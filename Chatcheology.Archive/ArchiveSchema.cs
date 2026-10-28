namespace Chatcheology.Archive
{
    /// <summary>
    /// The <c>archive.db</c> tables of each format version, one statement per element, in creation
    /// order.
    /// </summary>
    /// <remarks>
    /// <para>Only the result of a reconstruction is here. There is no evidence, run, candidate,
    /// disproof or review table, no media source or root path, no phone path and no raw export line;
    /// those stay in the workspace.</para>
    /// <para>The constraints restate the format's rules so a viewer can rely on them: a Restored
    /// attachment has a media file, a name and a basis; any other state has none of them. Foreign keys
    /// are deferred to the end of the writing transaction, because a low-quality copy may name a full
    /// copy that is written later.</para>
    /// <para>Indexes: <c>UNIQUE (SequenceNumber)</c> is the sequence index, and
    /// <c>UNIQUE (MessageID, Ordinal)</c> serves lookups by message, so neither needs a second one.
    /// Day jumps get their own index on <c>DateTimeLocal</c>.</para>
    /// <para>Version 2 differs from version 1 in two ways only: <c>Message</c> gains <c>Source</c>,
    /// and the six memory tables are added. Every version 1 table and meaning is otherwise the
    /// same.</para>
    /// </remarks>
    internal static class ArchiveSchema
    {
        /// <summary>The statements that create an archive of <paramref name="formatVersion"/>, in order.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Not a version this build knows.</exception>
        internal static IReadOnlyList<string> StatementsOf(int formatVersion) => formatVersion switch
        {
            1 => Statements(MessageV1),
            2 => [.. Statements(MessageV2), .. MemoryStatements],
            _ => throw new ArgumentOutOfRangeException(
                nameof(formatVersion), formatVersion, "Not a format version this build knows."),
        };

        /// <summary>Version 1's <c>Message</c> table.</summary>
        private const string MessageV1 =
            """
            CREATE TABLE Message (
                MessageID           INTEGER PRIMARY KEY,
                SequenceNumber      INTEGER NOT NULL,
                DateTimeLocal       TEXT NOT NULL,
                Kind                TEXT NOT NULL,
                SenderParticipantID INTEGER NULL,
                Text                TEXT NOT NULL,
                IsForwarded         INTEGER NULL,
                ForwardScore        INTEGER NULL,

                UNIQUE (SequenceNumber),

                CHECK (SequenceNumber > 0),
                CHECK (length(DateTimeLocal) = 19),
                CHECK (Kind IN ('User', 'System')),
                CHECK (
                    (Kind = 'User' AND SenderParticipantID IS NOT NULL)
                    OR (Kind = 'System' AND SenderParticipantID IS NULL)
                ),
                CHECK (IsForwarded IS NULL OR IsForwarded IN (0, 1)),

                FOREIGN KEY (SenderParticipantID) REFERENCES Participant (ParticipantID)
                    DEFERRABLE INITIALLY DEFERRED
            );
            """;

        /// <summary>
        /// Version 2's <c>Message</c> table: version 1's, plus <c>Source</c>, the file the message came
        /// from (the main workspace, or an older chat export). Recorded, never displayed (CH-33 D4). An
        /// older export has no forwarded facts, so its messages have none.
        /// </summary>
        private const string MessageV2 =
            """
            CREATE TABLE Message (
                MessageID           INTEGER PRIMARY KEY,
                SequenceNumber      INTEGER NOT NULL,
                DateTimeLocal       TEXT NOT NULL,
                Kind                TEXT NOT NULL,
                SenderParticipantID INTEGER NULL,
                Text                TEXT NOT NULL,
                IsForwarded         INTEGER NULL,
                ForwardScore        INTEGER NULL,
                Source              TEXT NOT NULL,

                UNIQUE (SequenceNumber),

                CHECK (SequenceNumber > 0),
                CHECK (length(DateTimeLocal) = 19),
                CHECK (Kind IN ('User', 'System')),
                CHECK (
                    (Kind = 'User' AND SenderParticipantID IS NOT NULL)
                    OR (Kind = 'System' AND SenderParticipantID IS NULL)
                ),
                CHECK (IsForwarded IS NULL OR IsForwarded IN (0, 1)),
                CHECK (Source IN ('Workspace', 'Export')),
                CHECK (Source = 'Workspace' OR (IsForwarded IS NULL AND ForwardScore IS NULL)),

                FOREIGN KEY (SenderParticipantID) REFERENCES Participant (ParticipantID)
                    DEFERRABLE INITIALLY DEFERRED
            );
            """;

        private const string LocalGlob =
            "'[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9]'";

        private const string UtcGlob =
            "'[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9]Z'";

        private static string[] Statements(string message) =>
        [
            """
            CREATE TABLE ArchiveInfo (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """,

            """
            CREATE TABLE Participant (
                ParticipantID INTEGER PRIMARY KEY,
                DisplayName   TEXT NOT NULL,
                IsLocal       INTEGER NOT NULL,

                CHECK (IsLocal IN (0, 1))
            );
            """,

            message,

            """
            CREATE INDEX IX_Message_DateTimeLocal ON Message (DateTimeLocal);
            """,

            """
            CREATE TABLE Media (
                Sha256     TEXT PRIMARY KEY,
                MediaType  TEXT NOT NULL,
                SizeBytes  INTEGER NOT NULL,
                DurationMS INTEGER NULL,
                Width      INTEGER NULL,
                Height     INTEGER NULL,

                CHECK (length(Sha256) = 64 AND Sha256 = lower(Sha256)),
                CHECK (SizeBytes >= 0),
                CHECK (DurationMS IS NULL OR DurationMS >= 0),
                CHECK (Width IS NULL OR Width > 0),
                CHECK (Height IS NULL OR Height > 0)
            );
            """,

            """
            CREATE TABLE Attachment (
                AttachmentID                 INTEGER PRIMARY KEY,
                MessageID                    INTEGER NOT NULL,
                Ordinal                      INTEGER NOT NULL,
                State                        TEXT NOT NULL,
                MediaSha256                  TEXT NULL,
                OriginalFileName             TEXT NULL,
                IsForwarded                  INTEGER NULL,
                ForwardScore                 INTEGER NULL,
                LowQualityCopyOfAttachmentID INTEGER NULL,

                UNIQUE (MessageID, Ordinal),

                CHECK (Ordinal > 0),
                CHECK (State IN ('Restored', 'Missing', 'Unmatched')),
                CHECK (
                    (State = 'Restored' AND MediaSha256 IS NOT NULL AND OriginalFileName IS NOT NULL)
                    OR (State <> 'Restored' AND MediaSha256 IS NULL AND OriginalFileName IS NULL)
                ),
                CHECK (IsForwarded IS NULL OR IsForwarded IN (0, 1)),
                CHECK (LowQualityCopyOfAttachmentID IS NULL
                    OR LowQualityCopyOfAttachmentID <> AttachmentID),

                FOREIGN KEY (MessageID) REFERENCES Message (MessageID)
                    DEFERRABLE INITIALLY DEFERRED,
                FOREIGN KEY (MediaSha256) REFERENCES Media (Sha256)
                    DEFERRABLE INITIALLY DEFERRED,
                FOREIGN KEY (LowQualityCopyOfAttachmentID) REFERENCES Attachment (AttachmentID)
                    DEFERRABLE INITIALLY DEFERRED
            );
            """,

            """
            CREATE TABLE AttachmentBasis (
                AttachmentID INTEGER PRIMARY KEY,
                BasisType    TEXT NOT NULL,
                BasisText    TEXT NOT NULL,
                Actor        TEXT NULL,
                ConfirmedUtc TEXT NOT NULL,

                CHECK (length(BasisType) > 0),
                CHECK (length(BasisText) > 0),
                CHECK (length(ConfirmedUtc) = 20),

                FOREIGN KEY (AttachmentID) REFERENCES Attachment (AttachmentID)
                    DEFERRABLE INITIALLY DEFERRED
            );
            """,
        ];

        /// <summary>
        /// The memory tables of version 2 (CH-42, CH-43): the memories file's tables with the same meanings
        /// and checks, less what the archive does not carry (image and icon source URLs, definitions no
        /// item uses, anything naming a source account). An item names the archive participant instead of a role.
        /// An entry image is a <c>Media</c> payload, stored as <c>media/&lt;sha256&gt;&lt;ImageExtension&gt;</c>.
        /// </summary>
        private static readonly string[] MemoryStatements =
        [
            """
            CREATE TABLE Collection (
                CollectionID      INTEGER PRIMARY KEY,
                Kind              TEXT NOT NULL,
                Name              TEXT NOT NULL,
                SourceDescription TEXT NULL,
                ShownByDefault    INTEGER NOT NULL,
                PeriodGapMinutes  INTEGER NULL,

                UNIQUE (Kind, Name),

                CHECK (Kind IN ('Imported', 'Personal')),
                CHECK (length(trim(Name)) > 0),
                CHECK (ShownByDefault IN (0, 1)),
                CHECK ((Kind = 'Imported' AND PeriodGapMinutes IS NOT NULL AND PeriodGapMinutes > 0)
                    OR (Kind = 'Personal' AND PeriodGapMinutes IS NULL))
            );
            """,

            $"""
            CREATE TABLE Entry (
                EntryID        INTEGER PRIMARY KEY,
                CollectionID   INTEGER NOT NULL,
                SourceKey      TEXT NULL,
                Title          TEXT NOT NULL,
                StartLocal     TEXT NOT NULL,
                EndLocal       TEXT NULL,
                StartUtc       TEXT NULL,
                EndUtc         TEXT NULL,
                DatePrecision  TEXT NOT NULL,
                Summary        TEXT NULL,
                ImageSha256    TEXT NULL,
                ImageExtension TEXT NULL,
                Source         TEXT NULL,
                DayLevel       TEXT NULL,

                UNIQUE (CollectionID, SourceKey),

                CHECK (length(trim(Title)) > 0),
                CHECK (SourceKey IS NULL OR length(SourceKey) > 0),
                CHECK (DatePrecision IN ('Second', 'Day', 'Month')),
                CHECK (StartLocal GLOB {LocalGlob}),
                CHECK (EndLocal IS NULL OR (EndLocal GLOB {LocalGlob} AND EndLocal >= StartLocal)),
                CHECK (StartUtc IS NULL OR StartUtc GLOB {UtcGlob}),
                CHECK (EndUtc IS NULL OR (EndUtc GLOB {UtcGlob} AND StartUtc IS NOT NULL AND EndUtc >= StartUtc)),
                CHECK (DayLevel IS NULL OR DayLevel IN ('strong', 'supporting', 'same-day')),
                CHECK ((ImageSha256 IS NULL) = (ImageExtension IS NULL)),
                CHECK (ImageSha256 IS NULL OR (length(ImageSha256) = 64 AND ImageSha256 = lower(ImageSha256))),
                CHECK (ImageExtension IS NULL
                    OR (substr(ImageExtension, 1, 1) = '.' AND length(ImageExtension) BETWEEN 2 AND 11
                        AND ImageExtension = lower(ImageExtension))),

                FOREIGN KEY (CollectionID) REFERENCES Collection (CollectionID)
                    DEFERRABLE INITIALLY DEFERRED,
                FOREIGN KEY (ImageSha256) REFERENCES Media (Sha256)
                    DEFERRABLE INITIALLY DEFERRED
            );
            """,

            """
            CREATE INDEX IX_Entry_StartLocal ON Entry (StartLocal);
            """,

            """
            CREATE INDEX IX_Entry_Collection ON Entry (CollectionID, StartLocal);
            """,

            $"""
            CREATE TABLE EntryPeriod (
                EntryID        INTEGER NOT NULL,
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
                CHECK (Label IN ('strong', 'supporting', 'same-period', 'one-account')),
                CHECK (LocalItemCount >= 0 AND OtherItemCount >= 0 AND LocalItemCount + OtherItemCount > 0),
                CHECK ((Label = 'one-account') = (LocalItemCount = 0 OR OtherItemCount = 0)),

                FOREIGN KEY (EntryID) REFERENCES Entry (EntryID)
                    DEFERRABLE INITIALLY DEFERRED
            );
            """,

            $"""
            CREATE TABLE EntryItem (
                EntryItemID   INTEGER PRIMARY KEY,
                EntryID       INTEGER NOT NULL,
                PeriodIndex   INTEGER NOT NULL,
                ParticipantID INTEGER NOT NULL,
                SetId         TEXT NOT NULL,
                ItemId        TEXT NOT NULL,
                ItemType      TEXT NOT NULL,
                TimeUtc       TEXT NOT NULL,
                TimeLocal     TEXT NOT NULL,

                CHECK (length(SetId) > 0 AND length(ItemId) > 0 AND length(ItemType) > 0),
                CHECK (TimeUtc GLOB {UtcGlob}),
                CHECK (TimeLocal GLOB {LocalGlob}),

                FOREIGN KEY (EntryID, PeriodIndex) REFERENCES EntryPeriod (EntryID, PeriodIndex)
                    DEFERRABLE INITIALLY DEFERRED,
                FOREIGN KEY (ParticipantID) REFERENCES Participant (ParticipantID)
                    DEFERRABLE INITIALLY DEFERRED
            );
            """,

            """
            CREATE INDEX IX_EntryItem_Entry ON EntryItem (EntryID, PeriodIndex);
            """,

            """
            CREATE TABLE EntryLink (
                EntryLinkID             INTEGER PRIMARY KEY,
                FromEntryID             INTEGER NOT NULL,
                ToEntryID               INTEGER NULL,
                ToMessageSequenceNumber INTEGER NULL,

                CHECK ((ToEntryID IS NULL) <> (ToMessageSequenceNumber IS NULL)),
                CHECK (ToMessageSequenceNumber IS NULL OR ToMessageSequenceNumber > 0),
                CHECK (ToEntryID IS NULL OR ToEntryID <> FromEntryID),

                FOREIGN KEY (FromEntryID) REFERENCES Entry (EntryID)
                    DEFERRABLE INITIALLY DEFERRED,
                FOREIGN KEY (ToEntryID) REFERENCES Entry (EntryID)
                    DEFERRABLE INITIALLY DEFERRED,
                FOREIGN KEY (ToMessageSequenceNumber) REFERENCES Message (SequenceNumber)
                    DEFERRABLE INITIALLY DEFERRED
            );
            """,

            """
            CREATE INDEX IX_EntryLink_From ON EntryLink (FromEntryID);
            """,

            // The memories file's definitions (CH-43) that some item uses, joined to items on
            // (SetId, ItemId). The icon is a Media payload like an entry image; no icon source address.
            """
            CREATE TABLE ItemDefinition (
                SetId         TEXT NOT NULL,
                ItemId        TEXT NOT NULL,
                ItemType      TEXT NOT NULL,
                Name          TEXT NULL,
                Detail        TEXT NULL,
                Hidden        INTEGER NULL,
                GroupId       TEXT NULL,
                IconSha256    TEXT NULL,
                IconExtension TEXT NULL,

                PRIMARY KEY (SetId, ItemId),

                CHECK (length(SetId) > 0 AND length(ItemId) > 0 AND length(ItemType) > 0),
                CHECK (Hidden IS NULL OR Hidden IN (0, 1)),
                CHECK ((IconSha256 IS NULL) = (IconExtension IS NULL)),
                CHECK (IconSha256 IS NULL OR (length(IconSha256) = 64 AND IconSha256 = lower(IconSha256))),
                CHECK (IconExtension IS NULL
                    OR (substr(IconExtension, 1, 1) = '.' AND length(IconExtension) BETWEEN 2 AND 11
                        AND IconExtension = lower(IconExtension))),

                FOREIGN KEY (IconSha256) REFERENCES Media (Sha256)
                    DEFERRABLE INITIALLY DEFERRED
            );
            """,
        ];
    }
}
