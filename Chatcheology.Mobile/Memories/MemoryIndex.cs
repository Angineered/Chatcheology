using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Search;

namespace Chatcheology.Mobile.Memories
{
    /// <summary>The filters of an imported collection's layer (CH-42), as stored words. All on by default.</summary>
    public static class MemoryFilters
    {
        /// <summary>Period cards labelled strong.</summary>
        public const string Strong = "strong";

        /// <summary>Period cards labelled supporting.</summary>
        public const string Supporting = "supporting";

        /// <summary>Period cards labelled same period.</summary>
        public const string SamePeriod = "same-period";

        /// <summary>The "different times" lines.</summary>
        public const string DifferentTimes = "different-times";

        /// <summary>Every filter, in the order Settings lists them.</summary>
        public static IReadOnlyList<string> All { get; } = [Strong, Supporting, SamePeriod, DifferentTimes];

        /// <summary>How Settings names a filter.</summary>
        public static string Title(string filter) => filter switch
        {
            Strong => "Strong periods",
            Supporting => "Supporting periods",
            SamePeriod => "Same-period cards",
            DifferentTimes => "\"Different times\" lines",
            _ => filter,
        };
    }

    /// <summary>
    /// Which memory rows are visible: the layers switched on, and the filters turned off. Built from the
    /// archive's settings; with every layer off nothing is visible and the conversation is as it was.
    /// </summary>
    public sealed class MemoryView
    {
        private readonly HashSet<long> _shown;
        private readonly HashSet<(long, string)> _off;

        /// <summary>A view with <paramref name="shownCollections"/> on and <paramref name="filtersOff"/> off.</summary>
        public MemoryView(IEnumerable<long> shownCollections, IEnumerable<(long CollectionID, string Filter)>? filtersOff = null)
        {
            ArgumentNullException.ThrowIfNull(shownCollections);

            _shown = [.. shownCollections];
            _off = [.. (filtersOff ?? []).Select(f => (f.CollectionID, f.Filter))];
        }

        /// <summary>Nothing visible.</summary>
        public static MemoryView None { get; } = new([]);

        /// <summary>Whether the collection's layer is on.</summary>
        public bool IsShown(long collectionID) => _shown.Contains(collectionID);

        /// <summary>Whether the filter is on (it is, unless turned off).</summary>
        public bool IsOn(long collectionID, string filter) => !_off.Contains((collectionID, filter));

        /// <summary>Whether no layer is on.</summary>
        public bool IsEmpty => _shown.Count == 0;
    }

    /// <summary>
    /// One memory row's place in the conversation: after the message with sequence number
    /// <see cref="After"/> (0 = before the first message), at <see cref="TimeLocal"/>.
    /// </summary>
    public sealed class MemoryPlacement
    {
        private readonly Func<MemoryRow> _create;

        internal MemoryPlacement(MemoryKey key, DateTime timeLocal, int after, int kindOrder, Func<MemoryRow> create)
        {
            Key = key;
            TimeLocal = timeLocal;
            After = after;
            KindOrder = kindOrder;
            _create = create;
        }

        /// <summary>The row's entry and period.</summary>
        public MemoryKey Key { get; }

        /// <summary>The local time it is placed at.</summary>
        public DateTime TimeLocal { get; }

        /// <summary>The sequence number of the message it follows; 0 before the first.</summary>
        public int After { get; }

        internal int KindOrder { get; }

        /// <summary>A new row for the list (rows carry their own highlight, as message rows do).</summary>
        public MemoryRow CreateRow() => _create();
    }

    /// <summary>
    /// The visible memory rows of one view, in the order they are placed, for a timeline to put beside its
    /// pages. Empty when every layer is off.
    /// </summary>
    public sealed class MemoryLayout
    {
        private readonly List<MemoryPlacement> _placements;

        internal MemoryLayout(List<MemoryPlacement> placements)
        {
            _placements = placements;
        }

        /// <summary>No memory rows.</summary>
        public static MemoryLayout Empty { get; } = new([]);

        /// <summary>Every visible row, in placement order.</summary>
        public IReadOnlyList<MemoryPlacement> Placements => _placements;

        /// <summary>Whether there is no visible row.</summary>
        public bool IsEmpty => _placements.Count == 0;

        /// <summary>The rows placed after messages <paramref name="first"/> to <paramref name="last"/> (inclusive), in order.</summary>
        public IEnumerable<MemoryPlacement> Between(int first, int last)
        {
            var low = 0;
            var high = _placements.Count;

            while (low < high)
            {
                var middle = (low + high) / 2;

                if (_placements[middle].After < first)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            for (var i = low; i < _placements.Count && _placements[i].After <= last; i++)
            {
                yield return _placements[i];
            }
        }

        /// <summary>The placement of a row, or <see langword="null"/> when it is not visible.</summary>
        public MemoryPlacement? Find(MemoryKey key) => _placements.FirstOrDefault(p => p.Key == key);

        /// <summary>The days that have a visible row.</summary>
        public IEnumerable<DateOnly> Days => _placements.Select(p => DateOnly.FromDateTime(p.TimeLocal)).Distinct();
    }

    /// <summary>One memory found by search: an entry title, opening the entry's first visible row.</summary>
    /// <param name="Key">The row to open.</param>
    /// <param name="Title">The entry's title.</param>
    /// <param name="DateText">The row's day.</param>
    /// <param name="CollectionName">The layer it belongs to.</param>
    /// <param name="Highlights">Every match in the title.</param>
    public sealed record MemorySearchHit(
        MemoryKey Key, string Title, string DateText, string CollectionName, IReadOnlyList<MatchRange> Highlights);

    /// <summary>One entry as a <see cref="MemorySource"/> gives it.</summary>
    /// <param name="Entry">The entry.</param>
    /// <param name="Periods">Its periods with their items, each item naming a participant; empty for a personal entry.</param>
    /// <param name="ImagePath">The image file when it is there; otherwise <see langword="null"/>.</param>
    /// <param name="Links">Its links (a message by the sequence number of the conversation the rows are placed in).</param>
    public sealed record MemorySourceEntry(
        ArchiveEntry Entry, IReadOnlyList<ArchiveEntryPeriod> Periods, string? ImagePath, IReadOnlyList<ArchiveEntryLink> Links)
    {
        /// <summary>
        /// The extra photos after the cover, in order (CH-46), each with its file when it is there. A photo
        /// whose file is not there is not shown.
        /// </summary>
        public IReadOnlyList<MemoryPhoto> ExtraPhotos { get; init; } = [];

        /// <summary>
        /// Its external gallery links (CH-48), in order. Only a personal entry's are used, and only those that
        /// keep <see cref="ExternalLinkRules"/>; an imported entry never shows one.
        /// </summary>
        public IReadOnlyList<GalleryLink> Galleries { get; init; } = [];
    }

    /// <summary>One photo of a memory: its file, and its caption (the cover has none).</summary>
    /// <param name="Path">The file when it is there; otherwise <see langword="null"/>.</param>
    /// <param name="Caption">The caption, or <see langword="null"/>.</param>
    public sealed record MemoryPhoto(string? Path, string? Caption)
    {
        /// <summary>The caption, or empty.</summary>
        public string CaptionText => Caption ?? string.Empty;

        /// <summary>Whether there is a caption.</summary>
        public bool HasCaption => !string.IsNullOrEmpty(Caption);
    }

    /// <summary>
    /// Where the full-screen viewer is among one memory's photos (CH-46): a swipe moves to the next or
    /// previous photo of that memory only, and stops at either end.
    /// </summary>
    public sealed class MemoryPhotoBrowser
    {
        /// <summary>A browser over <paramref name="photos"/> (each with its file), at <paramref name="index"/>.</summary>
        public MemoryPhotoBrowser(IReadOnlyList<MemoryPhoto> photos, int index)
        {
            ArgumentNullException.ThrowIfNull(photos);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, photos.Count);

            if (photos.Any(p => p.Path is null))
            {
                throw new ArgumentException("Every photo the viewer moves through needs its file.", nameof(photos));
            }

            Photos = photos;
            Index = index;
        }

        /// <summary>The memory's photos, in the order a swipe moves through them.</summary>
        public IReadOnlyList<MemoryPhoto> Photos { get; }

        /// <summary>The index of the photo shown.</summary>
        public int Index { get; private set; }

        /// <summary>The photo shown.</summary>
        public MemoryPhoto Current => Photos[Index];

        /// <summary>"Photo 2 of 5", or "Photo" for a memory with one.</summary>
        public string Title => MemoryWording.PhotoTitle(Index, Photos.Count);

        /// <summary>Whether a swipe to the left has a photo to show.</summary>
        public bool HasNext => Index < Photos.Count - 1;

        /// <summary>Whether a swipe to the right has a photo to show.</summary>
        public bool HasPrevious => Index > 0;

        /// <summary>Moves to the next photo. False, and nothing changes, at the end.</summary>
        public bool Next()
        {
            if (!HasNext)
            {
                return false;
            }

            Index++;
            return true;
        }

        /// <summary>Moves to the previous photo. False, and nothing changes, at the start.</summary>
        public bool Previous()
        {
            if (!HasPrevious)
            {
                return false;
            }

            Index--;
            return true;
        }
    }

    /// <summary>
    /// What a <see cref="MemoryIndex"/> is built from, whatever holds it: an archive on the phone, or the
    /// memories file beside the workspace and the workspace conversation on the desktop (CH-42 step 5).
    /// The rules (placement, which rows show, details and wording) are the index's, so both show the same.
    /// </summary>
    public sealed class MemorySource
    {
        /// <summary>The collections, ordered by identifier.</summary>
        public required IReadOnlyList<ArchiveCollection> Collections { get; init; }

        /// <summary>Every entry of those collections, collection by collection, in the source's own order.</summary>
        public required IReadOnlyList<MemorySourceEntry> Entries { get; init; }

        /// <summary>Each participant's display name, by the identifier items carry.</summary>
        public required IReadOnlyDictionary<long, string> ParticipantNames { get; init; }

        /// <summary>The item definitions (names and icons) the items may use.</summary>
        public required IReadOnlyList<ArchiveItemDefinition> Definitions { get; init; }

        /// <summary>A definition's icon file when it is there; otherwise <see langword="null"/>.</summary>
        public required Func<ArchiveItemDefinition, string?> IconPath { get; init; }

        /// <summary>The local time of every message, in chat order.</summary>
        public required IReadOnlyList<DateTime> MessageTimes { get; init; }

        /// <summary>
        /// The sequence number of each message in <see cref="MessageTimes"/>, rising; <see langword="null"/>
        /// when they are 1 to the number of messages (an archive's).
        /// </summary>
        public IReadOnlyList<int>? MessageSequenceNumbers { get; init; }
    }

    /// <summary>
    /// The archive's memories (CH-42), read once when the archive opens: collections, entries, periods
    /// with items, item definitions, and the message times the rows are placed among.
    /// </summary>
    /// <remarks>
    /// <para>Everything is read at open, off the UI thread, so placing rows, the details and search read
    /// nothing more from the archive. An archive without memories (version 1, or no collection) reads
    /// nothing at all.</para>
    /// <para><strong>Placement.</strong> A row at local time <c>t</c> goes after every message up to and
    /// including <c>t</c>'s second and before the first later message, in chat order: the first message
    /// whose time, or any earlier message's time, is after <c>t</c>. So a row at the same second as a
    /// message comes after it, and nothing assumes the stored times rise with the sequence numbers.</para>
    /// <para><strong>What shows.</strong> An imported collection with periods: a card per period whose
    /// label is strong, supporting or same period (one-account periods are not cards), and one muted
    /// "different times" line for a record with no shared period. Anything else: one card at its start.
    /// Nothing is computed beyond that: labels and levels are the archive's.</para>
    /// </remarks>
    public sealed class MemoryIndex
    {
        private readonly List<MemoryEntry> _entries;
        private readonly Dictionary<long, MemoryEntry> _byID;
        private readonly IReadOnlyList<DateTime> _prefixMaximum;
        private readonly IReadOnlyList<DateTime> _messageTimes;
        private readonly IReadOnlyList<int>? _sequenceNumbers;
        private readonly Lazy<HashSet<DateOnly>> _messageDays;

        private MemoryIndex(
            IReadOnlyList<ArchiveCollection> collections,
            List<MemoryEntry> entries,
            IReadOnlyList<DateTime> messageTimes,
            IReadOnlyList<int>? sequenceNumbers = null)
        {
            if (sequenceNumbers is not null && sequenceNumbers.Count != messageTimes.Count)
            {
                throw new ArgumentException("Every message time needs its sequence number.", nameof(sequenceNumbers));
            }

            Collections = collections;
            _entries = entries;
            _byID = entries.ToDictionary(e => e.Entry.EntryID);
            _messageTimes = messageTimes;
            _sequenceNumbers = sequenceNumbers;

            var prefix = new DateTime[messageTimes.Count];

            for (var i = 0; i < messageTimes.Count; i++)
            {
                prefix[i] = i == 0 || messageTimes[i] > prefix[i - 1] ? messageTimes[i] : prefix[i - 1];
            }

            _prefixMaximum = prefix;
            _messageDays = new Lazy<HashSet<DateOnly>>(() => [.. messageTimes.Select(DateOnly.FromDateTime)]);
        }

        /// <summary>No memories.</summary>
        public static MemoryIndex Empty { get; } = new([], [], []);

        /// <summary>The collections, ordered by identifier.</summary>
        public IReadOnlyList<ArchiveCollection> Collections { get; }

        /// <summary>Whether the archive has any collection.</summary>
        public bool HasMemories => Collections.Count > 0;

        /// <summary>Whether filters apply to a collection: an imported one, whose entries have periods.</summary>
        public static bool HasFilters(ArchiveCollection collection)
        {
            ArgumentNullException.ThrowIfNull(collection);

            return collection.Kind == ArchiveMemoryVocabulary.KindImported;
        }

        /// <summary>Reads the archive's memories through <paramref name="reader"/>; none for a version 1 archive.</summary>
        public static MemoryIndex Build(ArchiveReader reader, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(reader);

            var collections = reader.ReadCollections();

            if (collections.Count == 0)
            {
                return Empty;
            }

            var entries = new List<MemorySourceEntry>();

            // External gallery links (CH-48), read in one go; a row that breaks a rule is left out.
            var galleries = GalleryLinks.GroupFromArchive(reader.ReadAllExternalLinks());

            foreach (var collection in collections)
            {
                foreach (var entry in reader.ReadEntriesOf(collection.CollectionID))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var periods = collection.Kind == ArchiveMemoryVocabulary.KindImported
                        ? reader.ReadPeriods(entry.EntryID)
                        : [];

                    entries.Add(new MemorySourceEntry(
                        entry,
                        periods,
                        reader.ImageFullPath(entry) is { } path && File.Exists(path) ? path : null,
                        reader.ReadLinks(entry.EntryID))
                    {
                        ExtraPhotos = reader.ReadEntryImages(entry.EntryID)
                            .Select(photo => new MemoryPhoto(
                                reader.EntryImageFullPath(photo) is var file && File.Exists(file) ? file : null,
                                photo.Caption))
                            .ToList(),
                        Galleries = collection.Kind == ArchiveMemoryVocabulary.KindPersonal
                                    && galleries.TryGetValue((ExternalLinkOwnerKind.Entry, entry.EntryID), out var links)
                            ? links
                            : [],
                    });
                }
            }

            return Build(new MemorySource
            {
                Collections = collections,
                Entries = entries,
                ParticipantNames = reader.ReadParticipants().ToDictionary(p => p.ParticipantID, p => p.DisplayName),
                Definitions = reader.ReadAllItemDefinitions(),
                IconPath = icon => reader.IconFullPath(icon) is { } file && File.Exists(file) ? file : null,
                MessageTimes = reader.ReadMessageTimes(),
            });
        }

        /// <summary>
        /// Builds the index from <paramref name="source"/>: the same rows, details and wording whatever
        /// holds the memories. Entries of a collection the source does not list are left out.
        /// </summary>
        public static MemoryIndex Build(MemorySource source)
        {
            ArgumentNullException.ThrowIfNull(source);

            if (source.Collections.Count == 0)
            {
                return Empty;
            }

            var collections = source.Collections.ToDictionary(c => c.CollectionID);
            var definitions = source.Definitions.ToDictionary(d => (d.SetId, d.ItemId));
            var entries = new List<MemoryEntry>(source.Entries.Count);

            foreach (var item in source.Entries)
            {
                if (!collections.TryGetValue(item.Entry.CollectionID, out var collection))
                {
                    continue;
                }

                entries.Add(new MemoryEntry(
                    collection,
                    item.Entry,
                    item.Periods,
                    item.ImagePath,
                    source.ParticipantNames,
                    definitions,
                    source.IconPath,
                    item.Links,
                    item.ExtraPhotos)
                {
                    // Imported (PSN) memories get no link (CH-48), whatever the source gives; each link is
                    // checked against the shared rules again.
                    Galleries = collection.Kind == ArchiveMemoryVocabulary.KindPersonal
                        ? item.Galleries
                            .Select(link => GalleryLinks.Keep(link.Url, link.Label))
                            .OfType<GalleryLink>()
                            .Take(ExternalLinkRules.MaxLinksPerOwner)
                            .ToList()
                        : [],
                });
            }

            return new MemoryIndex(source.Collections, entries, source.MessageTimes, source.MessageSequenceNumbers);
        }

        /// <summary>The collection an entry belongs to, or <see langword="null"/> when the entry is not in the index.</summary>
        public ArchiveCollection? CollectionOf(long entryID) => _byID.GetValueOrDefault(entryID)?.Collection;

        /// <summary>The visible rows of <paramref name="view"/>, placed among the messages, in order.</summary>
        public MemoryLayout Layout(MemoryView view)
        {
            ArgumentNullException.ThrowIfNull(view);

            if (view.IsEmpty || _entries.Count == 0)
            {
                return MemoryLayout.Empty;
            }

            var placements = new List<MemoryPlacement>();

            foreach (var entry in _entries.Where(e => view.IsShown(e.Collection.CollectionID)))
            {
                foreach (var (key, time, order, create) in entry.Rows(view))
                {
                    placements.Add(new MemoryPlacement(key, time, After(time), order, create));
                }
            }

            placements.Sort((a, b) =>
            {
                var compare = a.After.CompareTo(b.After);
                compare = compare != 0 ? compare : a.TimeLocal.CompareTo(b.TimeLocal);
                compare = compare != 0 ? compare : a.KindOrder.CompareTo(b.KindOrder);
                compare = compare != 0 ? compare : a.Key.EntryID.CompareTo(b.Key.EntryID);

                return compare != 0 ? compare : (a.Key.PeriodIndex ?? -1).CompareTo(b.Key.PeriodIndex ?? -1);
            });

            return new MemoryLayout(placements);
        }

        /// <summary>
        /// The entries whose title contains <paramref name="query"/> (ignoring case and accents, as message
        /// search does) and that have a visible row in <paramref name="view"/>, newest first. A hit opens
        /// the entry's first visible row.
        /// </summary>
        public IReadOnlyList<MemorySearchHit> Find(string query, MemoryView view)
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(view);

            var needle = query.Trim();

            if (needle.Length == 0 || view.IsEmpty)
            {
                return [];
            }

            var hits = new List<(DateTime Time, MemorySearchHit Hit)>();

            foreach (var entry in _entries.Where(e => view.IsShown(e.Collection.CollectionID)))
            {
                var matches = ConversationSearch.FindAll(entry.Entry.Title, needle);

                if (matches.Count == 0)
                {
                    continue;
                }

                var rows = entry.Rows(view).OrderBy(row => row.Time).ThenBy(row => row.Key.PeriodIndex ?? -1).ToList();

                if (rows.Count == 0)
                {
                    continue;
                }

                var first = rows[0];

                hits.Add((first.Time, new MemorySearchHit(
                    first.Key,
                    entry.Entry.Title,
                    DateSeparatorRow.DayText(DateOnly.FromDateTime(first.Time)),
                    entry.Collection.Name,
                    matches)));
            }

            return hits.OrderByDescending(h => h.Time).Select(h => h.Hit).ToList();
        }

        /// <summary>What a tap on a memory row opens, or <see langword="null"/> when the entry is not in the archive.</summary>
        public MemoryDetails? Details(MemoryKey key) =>
            _byID.TryGetValue(key.EntryID, out var entry) ? entry.Details(key.PeriodIndex, this) : null;

        /// <summary>The first visible row of an entry, or <see langword="null"/> (for a link to an entry).</summary>
        public MemoryKey? FirstRowOf(long entryID, MemoryView view)
        {
            ArgumentNullException.ThrowIfNull(view);

            return _byID.TryGetValue(entryID, out var entry) && view.IsShown(entry.Collection.CollectionID)
                   && entry.Rows(view).OrderBy(row => row.Time).Select(row => (MemoryKey?)row.Key).FirstOrDefault() is { } first
                ? first
                : null;
        }

        /// <summary>
        /// The image an entry's card draws (its first photo whose file is there, CH-46), or <see langword="null"/>;
        /// whatever the layer switches say (CH-53: the On this day card's thumbnail).
        /// </summary>
        public string? CoverOf(long entryID) => _byID.GetValueOrDefault(entryID)?.ShownImagePath;

        /// <summary>An entry's external gallery links (CH-48), in order; none for an imported one or an unknown entry.</summary>
        public IReadOnlyList<GalleryLink> GalleriesOf(long entryID) => _byID.GetValueOrDefault(entryID)?.Galleries ?? [];

        internal MemoryEntry? Entry(long entryID) => _byID.GetValueOrDefault(entryID);

        /// <summary>
        /// Every collection shown and no filter off, whatever Settings says (CH-56: the Memories list). Rows
        /// of this view are the rows the chat would draw with every layer and filter on.
        /// </summary>
        internal MemoryView AllOnView => new(Collections.Select(c => c.CollectionID));

        /// <summary>
        /// Every row of <see cref="AllOnView"/>, entry by entry in the index's order, unplaced: the entry and
        /// the row's key and time, by the same <see cref="MemoryEntry.Rows"/> rules as <see cref="Layout"/>.
        /// </summary>
        internal IEnumerable<(MemoryEntry Entry, MemoryKey Key, DateTime Time)> AllRows()
        {
            var view = AllOnView;

            foreach (var entry in _entries)
            {
                foreach (var (key, time, _, _) in entry.Rows(view))
                {
                    yield return (entry, key, time);
                }
            }
        }

        /// <summary>
        /// Whether any message is on <paramref name="day"/> (local date), from the message times read at open: the
        /// same days as <c>ArchiveReader.ReadDays</c>, without a read.
        /// </summary>
        internal bool HasMessagesOn(DateOnly day) => _messageDays.Value.Contains(day);

        /// <summary>The local time of a message, or <see langword="null"/> when there is no such message.</summary>
        internal DateTime? MessageTime(int sequenceNumber)
        {
            if (_sequenceNumbers is null)
            {
                return sequenceNumber >= 1 && sequenceNumber <= _messageTimes.Count ? _messageTimes[sequenceNumber - 1] : null;
            }

            var index = BinarySearch(_sequenceNumbers, sequenceNumber);

            return index >= 0 ? _messageTimes[index] : null;
        }

        private static int BinarySearch(IReadOnlyList<int> sorted, int value)
        {
            var low = 0;
            var high = sorted.Count - 1;

            while (low <= high)
            {
                var middle = (low + high) / 2;

                if (sorted[middle] == value)
                {
                    return middle;
                }

                if (sorted[middle] < value)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return -1;
        }

        /// <summary>The sequence number of the last message at or before <paramref name="time"/> in the placement sense.</summary>
        private int After(DateTime time)
        {
            var count = MessagesNotAfter(time);

            return count == 0 || _sequenceNumbers is null ? count : _sequenceNumbers[count - 1];
        }

        /// <summary>How many messages, in chat order, come before the first whose time or any earlier one's is after <paramref name="time"/>.</summary>
        private int MessagesNotAfter(DateTime time)
        {
            // The first index whose running maximum is later than the time; every message before it is not.
            var low = 0;
            var high = _prefixMaximum.Count;

            while (low < high)
            {
                var middle = (low + high) / 2;

                if (_prefixMaximum[middle] > time)
                {
                    high = middle;
                }
                else
                {
                    low = middle + 1;
                }
            }

            return low;
        }
    }

    /// <summary>One entry with what its rows and details need, read at open.</summary>
    internal sealed class MemoryEntry
    {
        private readonly IReadOnlyDictionary<long, string> _names;
        private readonly IReadOnlyDictionary<(string, string), ArchiveItemDefinition> _definitions;
        private readonly Func<ArchiveItemDefinition, string?> _iconPath;
        private readonly IReadOnlyList<ArchiveEntryLink> _links;

        internal MemoryEntry(
            ArchiveCollection collection,
            ArchiveEntry entry,
            IReadOnlyList<ArchiveEntryPeriod> periods,
            string? imagePath,
            IReadOnlyDictionary<long, string> names,
            IReadOnlyDictionary<(string, string), ArchiveItemDefinition> definitions,
            Func<ArchiveItemDefinition, string?> iconPath,
            IReadOnlyList<ArchiveEntryLink> links,
            IReadOnlyList<MemoryPhoto>? extraPhotos = null)
        {
            Collection = collection;
            Entry = entry;
            Periods = periods;
            ImagePath = imagePath;

            // Only photos whose file is there are shown or counted (CH-46); the cover keeps its own place.
            ExtraPhotos = (extraPhotos ?? []).Where(p => p.Path is not null).ToList();
            Photos = (imagePath is null ? ExtraPhotos : ExtraPhotos.Prepend(new MemoryPhoto(imagePath, null))).ToList();
            _names = names;
            _definitions = definitions;
            _iconPath = iconPath;
            _links = links;
            SourceText = entry.Source ?? string.Empty;
            FirstItemLocal = periods.SelectMany(p => p.Items).Select(i => i.TimeLocal).DefaultIfEmpty(entry.StartLocal).Min();
        }

        internal ArchiveCollection Collection { get; }

        internal ArchiveEntry Entry { get; }

        internal IReadOnlyList<ArchiveEntryPeriod> Periods { get; }

        internal string? ImagePath { get; }

        /// <summary>The extra photos whose file is there, in order.</summary>
        internal IReadOnlyList<MemoryPhoto> ExtraPhotos { get; }

        /// <summary>Every photo whose file is there: the cover first, then the extra photos.</summary>
        internal IReadOnlyList<MemoryPhoto> Photos { get; }

        /// <summary>
        /// The image the card and the single-image details draw: the first photo whose file is there (the
        /// cover, or when its file is gone the first extra photo), so no present photo is left unshown.
        /// </summary>
        internal string? ShownImagePath => Photos.Count > 0 ? Photos[0].Path : null;

        /// <summary>The photos after the one drawn: the "+N" of the card.</summary>
        internal int PhotosAfterShown => Math.Max(0, Photos.Count - 1);

        internal string SourceText { get; }

        /// <summary>The external gallery links (CH-48) of a personal entry, in order; none for an imported one.</summary>
        internal IReadOnlyList<GalleryLink> Galleries { get; init; } = [];

        internal DateTime FirstItemLocal { get; }

        internal ArchiveEntryPeriod Period(int index) => Periods.First(p => p.PeriodIndex == index);

        /// <summary>
        /// The rows this entry shows in <paramref name="view"/>: its shared periods' cards (by filter), or
        /// its "different times" line, or one card at its start.
        /// </summary>
        internal IEnumerable<(MemoryKey Key, DateTime Time, int Order, Func<MemoryRow> Create)> Rows(MemoryView view)
        {
            var id = Collection.CollectionID;

            if (Periods.Count == 0)
            {
                yield return (new MemoryKey(Entry.EntryID, null), Entry.StartLocal, 2, () => new EntryCardRow(this));
                yield break;
            }

            var shared = Periods.Where(p => p.Label != ArchiveMemoryVocabulary.OneAccount).ToList();

            if (shared.Count == 0)
            {
                // Both accounts are in the record, at different times: one muted line, if its filter is on.
                var participants = Periods.SelectMany(p => p.Items).Select(i => i.ParticipantID).Distinct().Count();

                if (participants >= 2 && view.IsOn(id, MemoryFilters.DifferentTimes))
                {
                    yield return (new MemoryKey(Entry.EntryID, null), FirstItemLocal, 1, () => new DifferentTimesRow(this));
                }

                yield break;
            }

            foreach (var period in shared)
            {
                if (view.IsOn(id, period.Label))
                {
                    var index = period.PeriodIndex;
                    yield return (new MemoryKey(Entry.EntryID, index), period.StartLocal, 0, () => new PeriodCardRow(this, index));
                }
            }
        }

        /// <summary>Each participant's count and first-to-last time in a period, by participant identifier.</summary>
        internal IReadOnlyList<string> ParticipantLines(ArchiveEntryPeriod period) =>
            period.Items
                .GroupBy(i => i.ParticipantID)
                .OrderBy(g => g.Key)
                .Select(g => MemoryWording.ParticipantLine(
                    NameOf(g.Key), g.Count(), g.Min(i => i.TimeLocal), g.Max(i => i.TimeLocal)))
                .ToList();

        internal MemoryDetails Details(int? periodIndex, MemoryIndex index)
        {
            var selected = periodIndex is { } chosen ? Periods.FirstOrDefault(p => p.PeriodIndex == chosen) : null;

            var lines = new List<string>();
            var items = new List<MemoryItemLine>();
            var others = new List<string>();

            if (selected is not null)
            {
                lines.AddRange(ParticipantLines(selected));
                lines.Add(MemoryWording.PeriodLabelLine(selected.Label));
                items.AddRange(selected.Items.OrderBy(i => i.TimeLocal).ThenBy(i => i.EntryItemID).Select(ItemLine));
                others.AddRange(Periods.Where(p => p.PeriodIndex != selected.PeriodIndex).Select(ShortLine));
            }
            else if (Periods.Count > 0)
            {
                // A "different times" line: every trophy of the record, then its periods as short lines.
                lines.Add(MemoryWording.DifferentTimesLine(Entry.Title));
                items.AddRange(Periods.SelectMany(p => p.Items).OrderBy(i => i.TimeLocal).ThenBy(i => i.EntryItemID).Select(ItemLine));
                others.AddRange(Periods.Select(ShortLine));
            }

            var links = _links
                .Select(link => link.ToMessageSequenceNumber is { } sequence
                    ? new MemoryLinkLine(
                        index.MessageTime(sequence) is { } time
                            ? "A message on " + DateSeparatorRow.DayText(DateOnly.FromDateTime(time)) + ", " + MessageRow.TimeTextOf(time)
                            : "A message",
                        sequence,
                        null)
                    : link.ToEntryID is { } target && index.Entry(target) is { } other
                        ? new MemoryLinkLine(
                            other.Entry.Title + ", " + MemoryWording.EntryDate(other.Entry.DatePrecision, other.Entry.StartLocal, other.Entry.EndLocal),
                            null,
                            target)
                        : null)
                .OfType<MemoryLinkLine>()
                .ToList();

            return new MemoryDetails
            {
                Key = new MemoryKey(Entry.EntryID, selected?.PeriodIndex),
                Title = Entry.Title,
                DateText = selected is not null
                    ? DateSeparatorRow.DayText(DateOnly.FromDateTime(selected.StartLocal))
                    : Periods.Count > 0
                        ? DateSeparatorRow.DayText(DateOnly.FromDateTime(FirstItemLocal))
                        : MemoryWording.EntryDate(Entry.DatePrecision, Entry.StartLocal, Entry.EndLocal),
                CollectionName = Collection.Name,
                ImagePath = ShownImagePath,
                Photos = Photos,
                Summary = Entry.Summary ?? string.Empty,
                SourceText = SourceText,
                Lines = lines,
                Items = items,
                DayEvidenceText = Entry.DayLevel is { } level ? MemoryWording.DayEvidenceLine(level) : string.Empty,
                OtherPeriods = others,
                Links = links,
                Galleries = Galleries,
            };
        }

        private string NameOf(long participantID) =>
            _names.TryGetValue(participantID, out var name) ? name : "Unknown participant";

        private MemoryItemLine ItemLine(ArchiveEntryItem item)
        {
            var definition = _definitions.GetValueOrDefault((item.SetId, item.ItemId));

            return new MemoryItemLine(
                MemoryWording.ExactTime(item.TimeLocal),
                NameOf(item.ParticipantID),
                definition?.Name is { Length: > 0 } name ? name : MemoryWording.FallbackItemName(item.ItemType, item.ItemId),
                definition?.Detail ?? string.Empty,
                definition is not null ? _iconPath(definition) : null,
                definition?.Hidden == true);
        }

        private string ShortLine(ArchiveEntryPeriod period)
        {
            var counts = period.Items
                .GroupBy(i => i.ParticipantID)
                .OrderBy(g => g.Key)
                .Select(g => NameOf(g.Key) + " " + MemoryWording.Trophies(g.Count()));

            return MemoryWording.TimeSpan(period.StartLocal, period.EndLocal) + " · "
                   + MemoryWording.PeriodLabel(period.Label) + ": " + string.Join(", ", counts);
        }
    }

    /// <summary>One trophy in the details.</summary>
    /// <param name="TimeText">The exact local time, to the second.</param>
    /// <param name="ParticipantName">Whose it is (the archive's display name).</param>
    /// <param name="Name">The definition's name, or "Bronze trophy #12" without one.</param>
    /// <param name="Detail">The definition's detail, or empty.</param>
    /// <param name="IconPath">The icon file when there is one; otherwise <see langword="null"/>.</param>
    /// <param name="IsHidden">Whether the source marks it hidden (its name is shown all the same).</param>
    public sealed record MemoryItemLine(
        string TimeText, string ParticipantName, string Name, string Detail, string? IconPath, bool IsHidden)
    {
        /// <summary>Whether there is a detail line.</summary>
        public bool HasDetail => Detail.Length > 0;

        /// <summary>Whether there is an icon to draw.</summary>
        public bool HasIcon => IconPath is not null;
    }

    /// <summary>A link of a personal memory: to a message, or to another entry.</summary>
    /// <param name="Text">What the link shows.</param>
    /// <param name="SequenceNumber">The message it opens, or <see langword="null"/>.</param>
    /// <param name="EntryID">The entry it opens, or <see langword="null"/>.</param>
    public sealed record MemoryLinkLine(string Text, int? SequenceNumber, long? EntryID);

    /// <summary>What the details page of a memory row shows.</summary>
    public sealed class MemoryDetails
    {
        /// <summary>The row the details belong to.</summary>
        public required MemoryKey Key { get; init; }

        /// <summary>The title.</summary>
        public required string Title { get; init; }

        /// <summary>The day (or a personal memory's date or range).</summary>
        public required string DateText { get; init; }

        /// <summary>The layer.</summary>
        public required string CollectionName { get; init; }

        /// <summary>
        /// The image drawn: the cover when its file is there, otherwise the first extra photo whose file is
        /// (CH-46); always <see cref="Photos"/>' first.
        /// </summary>
        public required string? ImagePath { get; init; }


        /// <summary>
        /// Every photo whose file is there, the cover first, then the extra photos with their captions
        /// (CH-46). Tapping one opens the full-screen viewer over these only.
        /// </summary>
        public IReadOnlyList<MemoryPhoto> Photos { get; init; } = [];

        /// <summary>Whether the details show a strip of photos (more than one) instead of the single image.</summary>
        public bool HasPhotoStrip => Photos.Count > 1;

        /// <summary>A personal memory's description, or empty.</summary>
        public required string Summary { get; init; }

        /// <summary>The source line, or empty.</summary>
        public required string SourceText { get; init; }

        /// <summary>The card's lines: each participant's count and times, and the period label.</summary>
        public required IReadOnlyList<string> Lines { get; init; }

        /// <summary>The trophies with exact local times.</summary>
        public required IReadOnlyList<MemoryItemLine> Items { get; init; }

        /// <summary>"Day evidence: …" (the source record's level), or empty for a personal memory.</summary>
        public required string DayEvidenceText { get; init; }

        /// <summary>The day's other periods as short lines.</summary>
        public required IReadOnlyList<string> OtherPeriods { get; init; }

        /// <summary>A personal memory's links.</summary>
        public required IReadOnlyList<MemoryLinkLine> Links { get; init; }

        /// <summary>
        /// A personal memory's external gallery links (CH-48), in order: each listed as its label, or its host
        /// without one, and opened only after a confirmation.
        /// </summary>
        public IReadOnlyList<GalleryLink> Galleries { get; init; } = [];

        /// <summary>Whether there is an image.</summary>
        public bool HasImage => ImagePath is not null;

        /// <summary>A viewer over <see cref="Photos"/> at <paramref name="index"/>; never over anything else.</summary>
        public MemoryPhotoBrowser BrowsePhotos(int index) => new(Photos, index);

        /// <summary>The share text: none, ever. Memories are not shared (CH-42).</summary>

        public string? ShareText => null;
    }

    /// <summary>One layer as Settings lists it: its switch and, for an imported collection, its filters.</summary>
    /// <param name="CollectionID">The collection.</param>
    /// <param name="Name">Its name.</param>
    /// <param name="IsShown">Whether the layer is on.</param>
    /// <param name="Filters">Each filter and whether it is on; empty for a personal collection.</param>
    public sealed record MemoryLayerChoice(
        long CollectionID, string Name, bool IsShown, IReadOnlyList<(string Filter, bool IsOn)> Filters);
}
