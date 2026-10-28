namespace Chatcheology.Archive
{
    /// <summary>The stored spellings of the memory tables' fixed vocabularies (format version 2).</summary>
    /// <remarks>
    /// The same words as the memories file (CH-42), so the archive and the desktop never disagree about
    /// what a label means. Labels are copied, never computed, by anything that reads the archive.
    /// </remarks>
    public static class ArchiveMemoryVocabulary
    {
        /// <summary>A collection imported from another source (PSN first).</summary>
        public const string KindImported = "Imported";

        /// <summary>A collection made by hand.</summary>
        public const string KindPersonal = "Personal";

        /// <summary>Entry date precisions.</summary>
        public static IReadOnlyList<string> DatePrecisions { get; } = ["Second", "Day", "Month"];

        /// <summary>Day levels, copied from the source record.</summary>
        public static IReadOnlyList<string> DayLevels { get; } = ["strong", "supporting", "same-day"];

        /// <summary>Period labels, derived once at import from the source's own pairs.</summary>
        public static IReadOnlyList<string> PeriodLabels { get; } = ["strong", "supporting", "same-period", "one-account"];

        /// <summary>The period label of a period in which only one participant has items.</summary>
        public const string OneAccount = "one-account";
    }

    /// <summary>
    /// The cross-row rules of the memory tables that a constraint cannot state, as fixed <c>SELECT</c>s
    /// shared by the writer (refuses) and the validator (counts).
    /// </summary>
    internal static class ArchiveMemoryRules
    {
        /// <summary>Links whose start is not an entry of a personal collection.</summary>
        internal const string LinksNotFromPersonal =
            """
            SELECT COUNT(*) FROM EntryLink AS l
            LEFT JOIN Entry AS e ON e.EntryID = l.FromEntryID
            LEFT JOIN Collection AS c ON c.CollectionID = e.CollectionID
            WHERE c.Kind IS NOT 'Personal';
            """;

        /// <summary>Periods whose counts differ from their items (<c>$local</c> is the local participant).</summary>
        internal const string PeriodCountsDiffer =
            """
            SELECT COUNT(*) FROM EntryPeriod AS p
            WHERE p.LocalItemCount <> (SELECT COUNT(*) FROM EntryItem AS i
                                       WHERE i.EntryID = p.EntryID AND i.PeriodIndex = p.PeriodIndex
                                         AND i.ParticipantID = $local)
               OR p.OtherItemCount <> (SELECT COUNT(*) FROM EntryItem AS i
                                       WHERE i.EntryID = p.EntryID AND i.PeriodIndex = p.PeriodIndex
                                         AND i.ParticipantID <> $local);
            """;

        /// <summary>Media rows that no restored attachment, entry image or item icon names (version 2).</summary>
        internal const string MediaUnusedVersionTwo =
            """
            SELECT COUNT(*) FROM Media WHERE Sha256 NOT IN (
                SELECT MediaSha256 FROM Attachment WHERE State = 'Restored' AND MediaSha256 IS NOT NULL)
              AND Sha256 NOT IN (SELECT ImageSha256 FROM Entry WHERE ImageSha256 IS NOT NULL)
              AND Sha256 NOT IN (SELECT IconSha256 FROM ItemDefinition WHERE IconSha256 IS NOT NULL);
            """;

        /// <summary>Item definitions no item of the archive uses (the archive carries used definitions only).</summary>
        internal const string DefinitionsUnused =
            """
            SELECT COUNT(*) FROM ItemDefinition AS d
            WHERE NOT EXISTS (SELECT 1 FROM EntryItem AS i WHERE i.SetId = d.SetId AND i.ItemId = d.ItemId);
            """;

        /// <summary>Entries, periods and items per collection, ordered by collection.</summary>
        internal const string CollectionCounts =
            """
            SELECT c.CollectionID,
                   (SELECT COUNT(*) FROM Entry AS e WHERE e.CollectionID = c.CollectionID),
                   (SELECT COUNT(*) FROM EntryPeriod AS p JOIN Entry AS e ON e.EntryID = p.EntryID
                    WHERE e.CollectionID = c.CollectionID),
                   (SELECT COUNT(*) FROM EntryItem AS i JOIN Entry AS e ON e.EntryID = i.EntryID
                    WHERE e.CollectionID = c.CollectionID)
            FROM Collection AS c
            ORDER BY c.CollectionID;
            """;
    }

    /// <summary>One collection (a layer that can be switched on and off).</summary>
    public sealed class ArchiveCollection
    {
        /// <summary>The collection, with the identifier the memories file gave it.</summary>
        public required long CollectionID { get; init; }

        /// <summary><see cref="ArchiveMemoryVocabulary.KindImported"/> or <see cref="ArchiveMemoryVocabulary.KindPersonal"/>.</summary>
        public required string Kind { get; init; }

        /// <summary>The collection's name.</summary>
        public required string Name { get; init; }

        /// <summary>What the collection was made from, or <see langword="null"/>.</summary>
        public required string? SourceDescription { get; init; }

        /// <summary>Whether the layer is shown until the viewer switches it.</summary>
        public required bool ShownByDefault { get; init; }

        /// <summary>The gap (minutes) the import split periods at; <see langword="null"/> for a personal collection.</summary>
        public required int? PeriodGapMinutes { get; init; }
    }

    /// <summary>One dated card of a collection.</summary>
    /// <remarks>
    /// Times are as the memories file stored them: local wall clock with no zone, UTC marked as UTC.
    /// <see cref="DayLevel"/> is the source record's own level, never recomputed.
    /// </remarks>
    public sealed class ArchiveEntry
    {
        /// <summary>The entry, with the identifier the memories file gave it.</summary>
        public required long EntryID { get; init; }

        /// <summary>Its collection.</summary>
        public required long CollectionID { get; init; }

        /// <summary>The source's own identifier, or <see langword="null"/> for a personal entry.</summary>
        public required string? SourceKey { get; init; }

        /// <summary>The title.</summary>
        public required string Title { get; init; }

        /// <summary>The start, local wall clock.</summary>
        public required DateTime StartLocal { get; init; }

        /// <summary>The end, local wall clock, or <see langword="null"/>.</summary>
        public required DateTime? EndLocal { get; init; }

        /// <summary>The start in UTC, or <see langword="null"/>.</summary>
        public required DateTime? StartUtc { get; init; }

        /// <summary>The end in UTC, or <see langword="null"/>.</summary>
        public required DateTime? EndUtc { get; init; }

        /// <summary><c>Second</c>, <c>Day</c> or <c>Month</c>.</summary>
        public required string DatePrecision { get; init; }

        /// <summary>A short summary, or <see langword="null"/>.</summary>
        public required string? Summary { get; init; }

        /// <summary>The image payload's SHA-256, or <see langword="null"/>.</summary>
        public required string? ImageSha256 { get; init; }

        /// <summary>The image file's extension in <c>media/</c> (such as <c>.jpg</c>), or <see langword="null"/>.</summary>
        public required string? ImageExtension { get; init; }

        /// <summary>The source line (such as "PSN trophies"), or <see langword="null"/>.</summary>
        public required string? Source { get; init; }

        /// <summary>The source record's day level, or <see langword="null"/>.</summary>
        public required string? DayLevel { get; init; }

        /// <summary>The periods, with their items. Empty for a personal entry.</summary>
        public IReadOnlyList<ArchiveEntryPeriod> Periods { get; init; } = [];

        /// <summary>The image file's path inside the archive folder (<c>/</c> separated), or <see langword="null"/>.</summary>
        public string? ImageRelativePath =>
            ImageSha256 is { } sha && ImageExtension is { } extension
                ? ArchiveFormat.MediaFolderName + "/" + sha + extension
                : null;
    }

    /// <summary>One period of an entry: a run of items chained by the collection's gap.</summary>
    public sealed class ArchiveEntryPeriod
    {
        /// <summary>The entry.</summary>
        public required long EntryID { get; init; }

        /// <summary>Its position within the entry, from 0.</summary>
        public required int PeriodIndex { get; init; }

        /// <summary>The first item's time, UTC.</summary>
        public required DateTime StartUtc { get; init; }

        /// <summary>The last item's time, UTC.</summary>
        public required DateTime EndUtc { get; init; }

        /// <summary>The first item's time, local wall clock.</summary>
        public required DateTime StartLocal { get; init; }

        /// <summary>The last item's time, local wall clock.</summary>
        public required DateTime EndLocal { get; init; }

        /// <summary>The period label (see <see cref="ArchiveMemoryVocabulary.PeriodLabels"/>).</summary>
        public required string Label { get; init; }

        /// <summary>Items of the local participant in the period.</summary>
        public required int LocalItemCount { get; init; }

        /// <summary>Items of the other participant in the period.</summary>
        public required int OtherItemCount { get; init; }

        /// <summary>The period's items, in time order.</summary>
        public IReadOnlyList<ArchiveEntryItem> Items { get; init; } = [];
    }

    /// <summary>One detail row of a period (for PSN: one earned trophy).</summary>
    public sealed class ArchiveEntryItem
    {
        /// <summary>The item.</summary>
        public required long EntryItemID { get; init; }

        /// <summary>The entry.</summary>
        public required long EntryID { get; init; }

        /// <summary>The period within the entry.</summary>
        public required int PeriodIndex { get; init; }

        /// <summary>The archive participant the item belongs to.</summary>
        public required long ParticipantID { get; init; }

        /// <summary>The source's set identifier (for PSN, the trophy set).</summary>
        public required string SetId { get; init; }

        /// <summary>The source's item identifier within the set.</summary>
        public required string ItemId { get; init; }

        /// <summary>The item's type (for PSN, the trophy grade).</summary>
        public required string ItemType { get; init; }

        /// <summary>The exact time, UTC.</summary>
        public required DateTime TimeUtc { get; init; }

        /// <summary>The time, local wall clock.</summary>
        public required DateTime TimeLocal { get; init; }
    }

    /// <summary>A hand-made link from a personal entry to another entry or to a message.</summary>
    public sealed class ArchiveEntryLink
    {
        /// <summary>The link.</summary>
        public required long EntryLinkID { get; init; }

        /// <summary>The personal entry it starts at.</summary>
        public required long FromEntryID { get; init; }

        /// <summary>The entry it points to, or <see langword="null"/>.</summary>
        public required long? ToEntryID { get; init; }

        /// <summary>The archive sequence number of the message it points to, or <see langword="null"/>.</summary>
        public required int? ToMessageSequenceNumber { get; init; }
    }

    /// <summary>
    /// What an item is (for PSN: one trophy's name, detail, grade and icon), joined to items on
    /// (<see cref="SetId"/>, <see cref="ItemId"/>). Only definitions some archived item uses are carried.
    /// </summary>
    /// <remarks>
    /// Copied from the memories file's definitions (CH-43). The icon is a <c>Media</c> payload stored as
    /// <c>media/&lt;sha256&gt;&lt;IconExtension&gt;</c>, like an entry image; its source address is not
    /// carried. A hidden trophy keeps its name: the definitions carry it.
    /// </remarks>
    public sealed class ArchiveItemDefinition
    {
        /// <summary>The source's set identifier (for PSN, the trophy set).</summary>
        public required string SetId { get; init; }

        /// <summary>The source's item identifier within the set.</summary>
        public required string ItemId { get; init; }

        /// <summary>The item's type (for PSN, the trophy grade).</summary>
        public required string ItemType { get; init; }

        /// <summary>The item's name, or <see langword="null"/> when the source gave none.</summary>
        public required string? Name { get; init; }

        /// <summary>The item's detail (for PSN, the trophy's description), or <see langword="null"/>.</summary>
        public required string? Detail { get; init; }

        /// <summary>Whether the source marks the item hidden, or <see langword="null"/> when not known.</summary>
        public required bool? Hidden { get; init; }

        /// <summary>The source's group within the set, or <see langword="null"/>.</summary>
        public required string? GroupId { get; init; }

        /// <summary>The icon payload's SHA-256, or <see langword="null"/>.</summary>
        public required string? IconSha256 { get; init; }

        /// <summary>The icon file's extension in <c>media/</c> (such as <c>.png</c>), or <see langword="null"/>.</summary>
        public required string? IconExtension { get; init; }

        /// <summary>The icon file's path inside the archive folder (<c>/</c> separated), or <see langword="null"/>.</summary>
        public string? IconRelativePath =>
            IconSha256 is { } sha && IconExtension is { } extension
                ? ArchiveFormat.MediaFolderName + "/" + sha + extension
                : null;
    }
}
