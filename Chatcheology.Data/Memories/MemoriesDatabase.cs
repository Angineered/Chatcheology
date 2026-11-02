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

        /// <summary>The memories table layout this build creates. Format 1 to 5 files are still accepted.</summary>
        /// <remarks>
        /// Format 2 (CH-43) is additive: it adds <c>ItemDefinition</c> and nothing else changes. Format 3
        /// (CH-46) is additive too: it adds <c>EntryImage</c> (a personal memory's extra photos), and so are
        /// format 4 (CH-47), which adds <c>NotedDate</c>, format 5 (CH-48), which adds <c>ExternalLink</c>, and
        /// format 6 (CH-53), which adds the column <c>Entry.EveryYear</c> and its trigger.
        /// An older file is upgraded in one transaction by <see cref="UpgradeToCurrent"/> (the enrichment import
        /// and every personal write do this); every other path reads and writes all six formats.
        /// </remarks>
        internal const int MarkerFormatVersion = 6;

        /// <summary>The format this build creates and upgrades to.</summary>
        public const int CurrentFormatVersion = MarkerFormatVersion;

        /// <summary>The step 1 (CH-42) layout, without <c>ItemDefinition</c>.</summary>
        internal const int FormatVersionOne = 1;

        /// <summary>The CH-43 layout: format 1 plus <c>ItemDefinition</c>, without <c>EntryImage</c>.</summary>
        internal const int FormatVersionTwo = 2;

        /// <summary>The first format with <c>ItemDefinition</c>; fixed, whatever the current format is.</summary>
        public const int FirstFormatWithDefinitions = FormatVersionTwo;

        /// <summary>The first format with <c>EntryImage</c>; fixed, whatever the current format is.</summary>
        public const int FirstFormatWithEntryImages = 3;

        /// <summary>The first format with <c>NotedDate</c> (CH-47); fixed, whatever the current format is.</summary>
        public const int FirstFormatWithNotedDates = 4;

        /// <summary>The first format with <c>ExternalLink</c> (CH-48); fixed, whatever the current format is.</summary>
        public const int FirstFormatWithExternalLinks = 5;

        /// <summary>The first format with <c>Entry.EveryYear</c> (CH-53); fixed, whatever the current format is.</summary>
        public const int FirstFormatWithEveryYear = 6;

        /// <summary>The column format 6 adds to <c>Entry</c>.</summary>
        internal const string EveryYearColumnName = "EveryYear";

        /// <summary>
        /// The SQLite <c>user_version</c> of a memories file. Distinct from every workspace schema
        /// version, so <see cref="WorkspaceDatabase.Initialise"/> refuses the file (unsupported
        /// version) instead of treating it as empty and adding the workspace schema to it.
        /// </summary>
        internal const int UserVersion = 4201;

        /// <summary>Day levels the PSN source uses; copied as-is, never computed.</summary>
        public static IReadOnlyList<string> DayLevels { get; } = ["strong", "supporting", "same-day"];

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

        /// <summary>The tables of a format 1 file, in creation order.</summary>
        internal static readonly string[] VersionOneTableNames =
        [
            "MemoriesInfo",
            "MediaFile",
            "Collection",
            "Entry",
            "EntryPeriod",
            "EntryItem",
            "EntryLink",
        ];

        /// <summary>The table format 2 adds.</summary>
        internal const string ItemDefinitionTableName = "ItemDefinition";

        /// <summary>The table format 3 adds.</summary>
        internal const string EntryImageTableName = "EntryImage";

        /// <summary>The table format 4 adds.</summary>
        internal const string NotedDateTableName = "NotedDate";

        /// <summary>The tables of a format 2 file.</summary>
        internal static readonly string[] VersionTwoTableNames = [.. VersionOneTableNames, ItemDefinitionTableName];

        /// <summary>The tables of a format 3 file.</summary>
        internal static readonly string[] VersionThreeTableNames = [.. VersionTwoTableNames, EntryImageTableName];

        /// <summary>The table format 5 adds.</summary>
        internal const string ExternalLinkTableName = "ExternalLink";

        /// <summary>The tables of a format 4 file.</summary>
        internal static readonly string[] VersionFourTableNames = [.. VersionThreeTableNames, NotedDateTableName];

        /// <summary>The tables of a current file (format 5 and 6 have the same tables; 6 adds a column).</summary>
        internal static readonly string[] TableNames = [.. VersionFourTableNames, ExternalLinkTableName];

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

        /// <summary>
        /// The format 1 layout, exactly as step 1 created it. A new file is created with these and
        /// then upgraded by <see cref="UpgradeStatements"/>, the same path a real format 1 file takes,
        /// so a new file and an upgraded one carry the same layout.
        /// </summary>
        /// <remarks>
        /// <c>EntryItem.Name</c> and <c>EntryItem.IconSha256</c> stay in the layout but are unused:
        /// trophy names and icons live in <c>ItemDefinition</c>, joined on (SetId, ItemId).
        /// </remarks>
        private static readonly string[] VersionOneStatements =
        [
            $"""
            CREATE TABLE MemoriesInfo (
                MemoriesInfoID     INTEGER PRIMARY KEY,
                Purpose            TEXT NOT NULL,
                FormatVersion      INTEGER NOT NULL,
                CreatedDateTimeUtc TEXT NOT NULL,

                CHECK (MemoriesInfoID = 1),
                CHECK (Purpose = '{MarkerPurpose}'),
                CHECK (FormatVersion = {FormatVersionOne}),
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
                CHECK (EndUtc IS NULL OR (EndUtc GLOB {UtcGlob} AND StartUtc IS NOT NULL AND EndUtc >= StartUtc)),
                CHECK (DayLevel IS NULL OR DayLevel IN ('strong', 'supporting', 'same-day'))
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

        /// <summary>
        /// Rebuilds the marker table so its check names <paramref name="version"/>; the row's purpose and
        /// creation time are copied unchanged. No other table is touched.
        /// </summary>
        private static string[] MarkerUpgradeStatements(int version) =>
        [
            $"""
            CREATE TABLE MemoriesInfoNext (
                MemoriesInfoID     INTEGER PRIMARY KEY,
                Purpose            TEXT NOT NULL,
                FormatVersion      INTEGER NOT NULL,
                CreatedDateTimeUtc TEXT NOT NULL,

                CHECK (MemoriesInfoID = 1),
                CHECK (Purpose = '{MarkerPurpose}'),
                CHECK (FormatVersion = {version.ToString(CultureInfo.InvariantCulture)}),
                CHECK (CreatedDateTimeUtc GLOB {UtcGlob})
            );
            """,

            $"""
            INSERT INTO MemoriesInfoNext (MemoriesInfoID, Purpose, FormatVersion, CreatedDateTimeUtc)
            SELECT MemoriesInfoID, Purpose, {version.ToString(CultureInfo.InvariantCulture)}, CreatedDateTimeUtc FROM MemoriesInfo;
            """,

            "DROP TABLE MemoriesInfo;",

            "ALTER TABLE MemoriesInfoNext RENAME TO MemoriesInfo;",
        ];

        /// <summary>
        /// Format 1 to 2 (CH-43): the marker is rebuilt for format 2 and <c>ItemDefinition</c> is added. No
        /// other table is touched.
        /// </summary>
        private static readonly string[] UpgradeToTwoStatements =
        [
            .. MarkerUpgradeStatements(FormatVersionTwo),

            // One definition per (set, item), values as the source returned them. Viewers and the
            // archive join EntryItem to it on (SetId, ItemId).
            """
            CREATE TABLE ItemDefinition (
                SetId         TEXT NOT NULL,
                ItemId        TEXT NOT NULL,
                ItemType      TEXT NOT NULL,
                Name          TEXT NULL,
                Detail        TEXT NULL,
                Hidden        INTEGER NULL,
                GroupId       TEXT NULL,
                IconSourceUrl TEXT NULL,
                IconSha256    TEXT NULL REFERENCES MediaFile (Sha256),

                PRIMARY KEY (SetId, ItemId),

                CHECK (length(SetId) > 0 AND length(ItemId) > 0 AND length(ItemType) > 0),
                CHECK (Hidden IS NULL OR Hidden IN (0, 1))
            );
            """,

            "CREATE INDEX IX_ItemDefinition_IconSourceUrl ON ItemDefinition (IconSourceUrl);",
        ];

        /// <summary>
        /// Format 2 to 3 (CH-46): the marker is rebuilt for format 3 and <c>EntryImage</c> is added: a
        /// personal memory's extra photos, after its cover (<c>Entry.ImageSha256</c>). No other table is
        /// touched.
        /// </summary>
        private static readonly string[] UpgradeToThreeStatements =
        [
            .. MarkerUpgradeStatements(FirstFormatWithEntryImages),

            // Ordinal 1, 2, ... is the order shown after the cover. A photo is in a memory at most once.
            // Extension is the stored file's own (it must equal MediaFile.Extension).
            $"""
            CREATE TABLE EntryImage (
                EntryID   INTEGER NOT NULL REFERENCES Entry (EntryID),
                Ordinal   INTEGER NOT NULL,
                Sha256    TEXT NOT NULL REFERENCES MediaFile (Sha256),
                Extension TEXT NOT NULL,
                Caption   TEXT NULL,

                PRIMARY KEY (EntryID, Ordinal),
                UNIQUE (EntryID, Sha256),

                CHECK (Ordinal >= 1),
                CHECK (Sha256 GLOB {Sha256Glob}),
                CHECK (Extension IN ('jpg', 'png', 'gif', 'webp')),
                CHECK (Caption IS NULL OR length(trim(Caption)) > 0)
            );
            """,

            // Extra photos belong to a personal entry only and name their stored file's extension; CHECK
            // cannot look across tables.
            $"""
            CREATE TRIGGER EntryImage_Rules_Insert
            BEFORE INSERT ON EntryImage
            BEGIN
                SELECT RAISE(ABORT, 'Extra photos belong to a personal entry only.')
                WHERE (SELECT c.Kind FROM Entry e JOIN Collection c ON c.CollectionID = e.CollectionID
                       WHERE e.EntryID = NEW.EntryID) IS NOT '{KindPersonal}';
                SELECT RAISE(ABORT, 'A photo must name its stored file''s extension.')
                WHERE (SELECT m.Extension FROM MediaFile m WHERE m.Sha256 = NEW.Sha256) IS NOT NEW.Extension;
            END;
            """,

            $"""
            CREATE TRIGGER EntryImage_Rules_Update
            BEFORE UPDATE ON EntryImage
            BEGIN
                SELECT RAISE(ABORT, 'Extra photos belong to a personal entry only.')
                WHERE (SELECT c.Kind FROM Entry e JOIN Collection c ON c.CollectionID = e.CollectionID
                       WHERE e.EntryID = NEW.EntryID) IS NOT '{KindPersonal}';
                SELECT RAISE(ABORT, 'A photo must name its stored file''s extension.')
                WHERE (SELECT m.Extension FROM MediaFile m WHERE m.Sha256 = NEW.Sha256) IS NOT NEW.Extension;
            END;
            """,

            "CREATE INDEX IX_EntryImage_Sha256 ON EntryImage (Sha256);",
        ];

        /// <summary>
        /// Format 3 to 4 (CH-47): the marker is rebuilt for format 4 and <c>NotedDate</c> is added: days of the
        /// year that matter, shown every year. Not memories: no collection, entry or link refers to them. No
        /// other table is touched. The checks are the archive's own (<see cref="NotedDateRules.TableChecks"/>);
        /// the latest year, which a constraint cannot know, is the store's to check.
        /// </summary>
        private static readonly string[] UpgradeToFourStatements =
        [
            .. MarkerUpgradeStatements(FirstFormatWithNotedDates),

            $"""
            CREATE TABLE NotedDate (
                NotedDateID INTEGER PRIMARY KEY,
                Title       TEXT NOT NULL,
                Month       INTEGER NOT NULL,
                Day         INTEGER NOT NULL,
                Year        INTEGER NULL,
                Note        TEXT NULL,

                {NotedDateRules.TableChecks}
            );
            """,
        ];

        /// <summary>
        /// Format 4 to 5 (CH-48): the marker is rebuilt for format 5 and <c>ExternalLink</c> is added: web
        /// addresses (for photos kept elsewhere) on personal entries and noted dates, an ordered list per owner.
        /// No other table is touched. The checks are the archive's own (<see cref="ExternalLinkRules.TableChecks"/>);
        /// the owner, which a foreign key cannot name (an entry or a noted date), is checked by triggers.
        /// </summary>
        private static readonly string[] UpgradeToFiveStatements =
        [
            .. MarkerUpgradeStatements(FirstFormatWithExternalLinks),

            $"""
            CREATE TABLE ExternalLink (
                ExternalLinkID INTEGER PRIMARY KEY,
                OwnerKind      TEXT NOT NULL,
                OwnerID        INTEGER NOT NULL,
                Ordinal        INTEGER NOT NULL,
                Url            TEXT NOT NULL,
                Label          TEXT NULL,

                UNIQUE (OwnerKind, OwnerID, Ordinal),

                {ExternalLinkRules.TableChecks}
            );
            """,

            // A link belongs to an entry of a personal collection or to a noted date that exists; CHECK cannot
            // look across tables.
            $"""
            CREATE TRIGGER ExternalLink_Owner_Insert
            BEFORE INSERT ON ExternalLink
            BEGIN
                {ExternalLinkOwnerCheck}
            END;
            """,

            $"""
            CREATE TRIGGER ExternalLink_Owner_Update
            BEFORE UPDATE ON ExternalLink
            BEGIN
                {ExternalLinkOwnerCheck}
            END;
            """,
        ];

        /// <summary>
        /// Format 5 to 6 (CH-53): the marker is rebuilt for format 6 and <c>Entry.EveryYear</c> is added (0 for
        /// every existing row): a personal memory shown each year on its day. Triggers allow 1 only on an entry of
        /// a personal collection dated to the day without an end date. No other table is touched.
        /// </summary>
        private static readonly string[] UpgradeToSixStatements =
        [
            .. MarkerUpgradeStatements(FirstFormatWithEveryYear),

            $"ALTER TABLE Entry ADD COLUMN {EveryYearColumnName} INTEGER NOT NULL DEFAULT 0 CHECK ({EveryYearColumnName} IN (0, 1));",

            $"""
            CREATE TRIGGER Entry_EveryYear_Insert
            BEFORE INSERT ON Entry
            WHEN {EveryYearBroken}
            BEGIN
                SELECT RAISE(ABORT, '{EveryYearRefusal}');
            END;
            """,

            $"""
            CREATE TRIGGER Entry_EveryYear_Update
            BEFORE UPDATE OF {EveryYearColumnName}, CollectionID, DatePrecision, EndLocal ON Entry
            WHEN {EveryYearBroken}
            BEGIN
                SELECT RAISE(ABORT, '{EveryYearRefusal}');
            END;
            """,
        ];

        /// <summary>The rule's breach, on the row a trigger is about to write.</summary>
        private const string EveryYearBroken =
            $"""
            NEW.{EveryYearColumnName} = 1
                AND ((SELECT c.Kind FROM Collection c WHERE c.CollectionID = NEW.CollectionID) IS NOT '{KindPersonal}'
                     OR NEW.DatePrecision IS NOT '{PrecisionDay}'
                     OR NEW.EndLocal IS NOT NULL)
            """;

        private const string EveryYearRefusal =
            "Every year is only for a personal memory dated to the day, without an end date.";

        private const string ExternalLinkOwnerCheck =
            $"""
            SELECT RAISE(ABORT, 'An external link belongs to a personal entry or a noted date.')
                WHERE (NEW.OwnerKind = '{ExternalLinkRules.OwnerEntry}'
                       AND (SELECT c.Kind FROM Entry e JOIN Collection c ON c.CollectionID = e.CollectionID
                            WHERE e.EntryID = NEW.OwnerID) IS NOT '{KindPersonal}')
                   OR (NEW.OwnerKind = '{ExternalLinkRules.OwnerNotedDate}'
                       AND NOT EXISTS (SELECT 1 FROM NotedDate WHERE NotedDateID = NEW.OwnerID));
            """;

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
        public static void Create(string databasePath, DateTime createdDateTimeUtc) =>
            Create(databasePath, createdDateTimeUtc, MarkerFormatVersion);

        /// <summary>Creates a format 1 file exactly as step 1 did. For upgrade tests only.</summary>
        internal static void CreateFormatVersionOne(string databasePath, DateTime createdDateTimeUtc) =>
            Create(databasePath, createdDateTimeUtc, FormatVersionOne);

        /// <summary>Creates a format 2 file exactly as CH-43 did. For upgrade tests only.</summary>
        internal static void CreateFormatVersionTwo(string databasePath, DateTime createdDateTimeUtc) =>
            Create(databasePath, createdDateTimeUtc, FormatVersionTwo);

        /// <summary>Creates a format 3 file exactly as CH-46 did. For upgrade tests only.</summary>
        internal static void CreateFormatVersionThree(string databasePath, DateTime createdDateTimeUtc) =>
            Create(databasePath, createdDateTimeUtc, FirstFormatWithEntryImages);

        /// <summary>Creates a format 4 file exactly as CH-47 did. For upgrade tests only.</summary>
        internal static void CreateFormatVersionFour(string databasePath, DateTime createdDateTimeUtc) =>
            Create(databasePath, createdDateTimeUtc, FirstFormatWithNotedDates);

        /// <summary>Creates a format 5 file exactly as CH-48 did. For upgrade tests only.</summary>
        internal static void CreateFormatVersionFive(string databasePath, DateTime createdDateTimeUtc) =>
            Create(databasePath, createdDateTimeUtc, FirstFormatWithExternalLinks);

        private static void Create(string databasePath, DateTime createdDateTimeUtc, int version)
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

                foreach (var statement in VersionOneStatements)
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
                command.Parameters.AddWithValue("$formatVersion", FormatVersionOne);
                command.Parameters.AddWithValue("$created", FormatUtc(createdDateTimeUtc));
                command.ExecuteNonQuery();

                command.Parameters.Clear();

                RunUpgradeStatements(command, FormatVersionOne, version);

                command.CommandText = $"PRAGMA user_version = {UserVersion.ToString(CultureInfo.InvariantCulture)};";
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

        /// <summary>Opens an existing memories file read-only (SQLite refuses any write), after proving it is one.</summary>
        /// <exception cref="ArgumentException">The file name is not <see cref="FileName"/>.</exception>
        /// <exception cref="FileNotFoundException">There is no file. Nothing is created.</exception>
        /// <exception cref="InvalidOperationException">It is not a memories file.</exception>
        public static SqliteConnection OpenReadOnlyConnection(string databasePath)
        {
            RequireFixedFileName(databasePath);

            if (!File.Exists(databasePath))
            {
                throw new FileNotFoundException(
                    "There is no memories file at the supplied path. Opening never creates one. No file has been created.");
            }

            var connection = WorkspaceDatabase.OpenReadOnlyConnection(databasePath);

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
        /// The format version of an open memories file (1 to 6), after proving it is one. Call it
        /// outside a transaction.
        /// </summary>
        public static int ReadFormatVersion(SqliteConnection connection) => RequireMemoriesFile(connection);

        /// <summary>
        /// Upgrades an older file (format 1 to 5) to the current format inside <paramref name="transaction"/>:
        /// additive only, every existing row unchanged. A current file is left as it is. The
        /// connection must come from <see cref="OpenConnection"/>, which proved the file.
        /// </summary>
        /// <returns>True when the file was an older format and has been upgraded (on commit).</returns>
        public static bool UpgradeToCurrent(SqliteConnection connection, SqliteTransaction transaction)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(transaction);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT FormatVersion FROM MemoriesInfo WHERE MemoriesInfoID = 1;";
            var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);

            if (version == MarkerFormatVersion)
            {
                return false;
            }

            if (version is not (FormatVersionOne or FormatVersionTwo or FirstFormatWithEntryImages or FirstFormatWithNotedDates
                or FirstFormatWithExternalLinks))
            {
                throw new InvalidOperationException("The memories file's format version is not one this build upgrades. Nothing has been written.");
            }

            RunUpgradeStatements(command, version, MarkerFormatVersion);
            return true;
        }

        /// <summary>Runs the upgrade steps from format <paramref name="from"/> to <paramref name="to"/>, in order.</summary>
        private static void RunUpgradeStatements(SqliteCommand command, int from, int to)
        {
            var steps = new List<string>();

            if (from < FormatVersionTwo && to >= FormatVersionTwo)
            {
                steps.AddRange(UpgradeToTwoStatements);
            }

            if (from < FirstFormatWithEntryImages && to >= FirstFormatWithEntryImages)
            {
                steps.AddRange(UpgradeToThreeStatements);
            }

            if (from < FirstFormatWithNotedDates && to >= FirstFormatWithNotedDates)
            {
                steps.AddRange(UpgradeToFourStatements);
            }

            if (from < FirstFormatWithExternalLinks && to >= FirstFormatWithExternalLinks)
            {
                steps.AddRange(UpgradeToFiveStatements);
            }

            if (from < FirstFormatWithEveryYear && to >= FirstFormatWithEveryYear)
            {
                steps.AddRange(UpgradeToSixStatements);
            }

            foreach (var statement in steps)
            {
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Throws unless the connection is open on a memories file: every table of its format and
        /// exactly one valid marker row (format 1 to 6). A main workspace or an older-exports file
        /// is refused by name.
        /// </summary>
        /// <returns>The file's format version.</returns>
        internal static int RequireMemoriesFile(SqliteConnection connection)
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

            if (!VersionOneTableNames.All(tables.Contains))
            {
                throw new InvalidOperationException(
                    "The database does not carry the memories tables, so it is not treated as a " +
                    "memories file. Nothing has been written.");
            }

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";

            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != UserVersion)
            {
                throw new InvalidOperationException(
                    "The database does not carry the memories file's user_version, so it is not treated as " +
                    "a memories file. Nothing has been written.");
            }

            command.CommandText =
                """
                SELECT
                    (SELECT COUNT(*) FROM MemoriesInfo),
                    (SELECT COUNT(*) FROM MemoriesInfo
                     WHERE MemoriesInfoID = 1 AND Purpose = $purpose AND FormatVersion IN ($one, $two, $three, $four, $five, $current)),
                    (SELECT MAX(FormatVersion) FROM MemoriesInfo);
                """;
            command.Parameters.AddWithValue("$purpose", MarkerPurpose);
            command.Parameters.AddWithValue("$one", FormatVersionOne);
            command.Parameters.AddWithValue("$two", FormatVersionTwo);
            command.Parameters.AddWithValue("$three", FirstFormatWithEntryImages);
            command.Parameters.AddWithValue("$four", FirstFormatWithNotedDates);
            command.Parameters.AddWithValue("$five", FirstFormatWithExternalLinks);
            command.Parameters.AddWithValue("$current", MarkerFormatVersion);

            long allRows, validRows;
            int version;

            using (var reader = command.ExecuteReader())
            {
                reader.Read();
                allRows = reader.GetInt64(0);
                validRows = reader.GetInt64(1);
                version = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            }

            if (allRows != 1 || validRows != 1)
            {
                throw new InvalidOperationException(
                    "The database does not carry exactly one valid memories marker row (format version " +
                    $"{FormatVersionOne.ToString(CultureInfo.InvariantCulture)} to " +
                    $"{MarkerFormatVersion.ToString(CultureInfo.InvariantCulture)}), so it is not " +
                    "treated as a memories file. Nothing has been written.");
            }

            // The marker and the tables must agree: format 2 adds ItemDefinition, format 3 EntryImage,
            // format 4 NotedDate, format 5 ExternalLink, format 6 the column Entry.EveryYear.
            command.Parameters.Clear();
            command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('Entry') WHERE name = '{EveryYearColumnName}';";
            var hasEveryYear = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;

            if (tables.Contains(ItemDefinitionTableName) != (version >= FirstFormatWithDefinitions)
                || tables.Contains(EntryImageTableName) != (version >= FirstFormatWithEntryImages)
                || tables.Contains(NotedDateTableName) != (version >= FirstFormatWithNotedDates)
                || tables.Contains(ExternalLinkTableName) != (version >= FirstFormatWithExternalLinks)
                || hasEveryYear != (version >= FirstFormatWithEveryYear))
            {
                throw new InvalidOperationException(
                    "The memories file's format version and its tables do not agree, so it is not treated as a " +
                    "memories file. Nothing has been written.");
            }

            return version;
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
