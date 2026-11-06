using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Search;

namespace Chatcheology.Mobile.Memories
{
    /// <summary>One row of the Memories list: a year heading or a memory.</summary>
    public abstract record MemoryListRow;

    /// <summary>The heading before a year's first row.</summary>
    /// <param name="Year">The year.</param>
    public sealed record MemoryListYearRow(int Year) : MemoryListRow
    {
        /// <summary>"2021".</summary>
        public string Text => Year.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One memory of the list: a row the chat would draw with every layer and filter on.</summary>
    /// <param name="Key">The row: what a tap opens in the details.</param>
    /// <param name="Title">The entry's title.</param>
    /// <param name="DateText">The date: "2 January 2018", "January 2018", a range, or a period's day.</param>
    /// <param name="DetailText">
    /// The rest: a period's observed times and label ("20:18 to 22:15 · strong"), "at different times", or
    /// the collection's name (with " · Every year" for a yearly memory).
    /// </param>
    /// <param name="ImagePath">A personal memory's cover thumbnail when its file is there; otherwise <see langword="null"/>.</param>
    /// <param name="IsYearly">Whether it is a yearly memory (CH-53): listed once, at its own date.</param>
    /// <param name="SortTime">The time the chat places the row at.</param>
    public sealed record MemoryListItem(
        MemoryKey Key, string Title, string DateText, string DetailText, string? ImagePath, bool IsYearly, DateTime SortTime) : MemoryListRow
    {
        /// <summary>The Saved tab the row is listed under: <c>personal</c> or <c>collection:&lt;id&gt;</c>.</summary>
        public string Group { get; init; } = MemoryList.PersonalGroup;

        /// <summary>The kind of row, which decides the two text lines.</summary>
        public MemoryListItemKind Kind { get; init; }

        /// <summary>Whether there is a thumbnail to draw.</summary>
        public bool HasImage => ImagePath is not null;

        /// <summary>
        /// The row's plain second line: the date for a memory, "date · times · label" for a period, empty for a
        /// "different times" row (whose line is muted). Made once, when the list is built.
        /// </summary>
        public string Line { get; init; } = string.Empty;

        /// <summary>
        /// The row's muted line: the collection name for a memory, "date · at different times" for a
        /// "different times" row, empty for a period. Made once, when the list is built.
        /// </summary>
        public string MutedLine { get; init; } = string.Empty;

        /// <summary>Whether <see cref="Line"/> has text.</summary>
        public bool HasLine => Line.Length > 0;

        /// <summary>Whether <see cref="MutedLine"/> has text.</summary>
        public bool HasMutedLine => MutedLine.Length > 0;
    }

    /// <summary>What a Memories list row is, as the chat draws it.</summary>
    public enum MemoryListItemKind
    {
        /// <summary>A memory with no periods (a personal one, or an imported one without periods): one card at its start.</summary>
        Entry,

        /// <summary>A shared period's card.</summary>
        Period,

        /// <summary>A record with no shared period, both accounts in it: the "different times" line.</summary>
        DifferentTimes,
    }

    /// <summary>Where Show in chat goes from a memory opened from the list: its row, or its day.</summary>
    public sealed class MemoryShowTarget
    {
        private MemoryShowTarget(MemoryKey? row, DateOnly? day)
        {
            Row = row;
            Day = day;
        }

        /// <summary>The row to open the chat at (it is visible now), or <see langword="null"/>.</summary>
        public MemoryKey? Row { get; }

        /// <summary>The day to open the chat at (the row is hidden, the day has messages), or <see langword="null"/>.</summary>
        public DateOnly? Day { get; }

        /// <summary>Open the chat at a visible row.</summary>
        public static MemoryShowTarget AtRow(MemoryKey key) => new(key, null);

        /// <summary>Open the chat at a day that has messages.</summary>
        public static MemoryShowTarget AtDay(DateOnly day) => new(null, day);
    }

    /// <summary>
    /// The Memories list (CH-56): every memory row the chat would draw with every layer and filter on, newest
    /// first, with year headings and a title search, one group per Saved tab (CH-59): the personal collections
    /// together, and each imported collection. Built once from the in-memory index; reads nothing.
    /// </summary>
    /// <remarks>
    /// <para><strong>What is listed.</strong> Exactly the rows the chat's rules give (<c>MemoryEntry.Rows</c>) with an all-on
    /// view: a personal memory (or an imported one without periods) once at its start, a yearly memory once at
    /// its own date, one row per shared period, one "different times" row at a record's first item. One-account
    /// periods and one-account records are never listed; noted dates are not memories and are not listed.</para>
    /// <para><strong>Order.</strong> Placement time descending, then entry, then period ascending.</para>
    /// <para><strong>Wording.</strong> Observed times only, never a duration (CH-42).</para>
    /// </remarks>
    public sealed class MemoryList
    {
        /// <summary>The group of every personal collection together: the Memories tab.</summary>
        public const string PersonalGroup = "personal";

        /// <summary>The Memories tab's empty line when no personal memory is listed.</summary>
        public const string NoPersonalText = "No memories yet. Memories you add will show here.";

        /// <summary>An imported collection's empty line when nothing of it is listed.</summary>
        public const string NothingSharedText = "Nothing shared to list in this collection.";

        /// <summary>The empty line when the search matches nothing in the group.</summary>
        public const string NoMatchText = "No titles match.";

        /// <summary>The empty line when the list could not be built (the archive closed under the page).</summary>
        public const string NotReadText = "The memories could not be read.";

        private const string MonthPrecision = "Month";

        private const string DifferentTimesText = "at different times";

        private readonly MemoryIndex _index;
        private readonly List<MemoryListItem> _items;
        private readonly Dictionary<MemoryKey, MemoryListItem> _byKey;

        private MemoryList(MemoryIndex index, List<MemoryListItem> items)
        {
            _index = index;
            _items = items;
            _byKey = items.ToDictionary(item => item.Key);
        }

        /// <summary>Whether nothing is listed.</summary>
        public bool IsEmpty => _items.Count == 0;

        /// <summary>Every listed memory, all groups together, in the list's order (newest first).</summary>
        public IReadOnlyList<MemoryListItem> Items => _items;

        /// <summary>How many memory rows are listed.</summary>
        public int Count => _items.Count;

        /// <summary>How many rows belong to personal collections.</summary>
        public int PersonalCount => CountIn(PersonalGroup);

        /// <summary>How many rows belong to imported collections.</summary>
        public int ImportedCount => _items.Count - PersonalCount;

        /// <summary>The group of an imported collection: its own Saved tab.</summary>
        public static string CollectionGroup(long collectionID) =>
            "collection:" + collectionID.ToString(CultureInfo.InvariantCulture);

        /// <summary>How many memory rows <paramref name="group"/> lists, whatever the search.</summary>
        public int CountIn(string group) => _items.Count(item => item.Group == group);

        /// <summary>
        /// Builds the list from <paramref name="index"/>. <paramref name="yearlyEntryIDs"/> are the archive's
        /// yearly memories that keep the rule (<see cref="YearlyMemories.From"/>); only those are marked.
        /// </summary>
        public static MemoryList Build(MemoryIndex index, IReadOnlySet<long> yearlyEntryIDs, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(index);
            ArgumentNullException.ThrowIfNull(yearlyEntryIDs);

            var items = new List<MemoryListItem>();

            // The days with messages are found here, in the background, not on the first Show in chat (UI thread).
            _ = index.HasMessagesOn(default);

            foreach (var (entry, key, time) in index.AllRows())
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(ItemOf(index, entry, key, time, yearlyEntryIDs));
            }

            items.Sort((a, b) =>
            {
                var compare = b.SortTime.CompareTo(a.SortTime);
                compare = compare != 0 ? compare : a.Key.EntryID.CompareTo(b.Key.EntryID);

                return compare != 0 ? compare : (a.Key.PeriodIndex ?? -1).CompareTo(b.Key.PeriodIndex ?? -1);
            });

            return new MemoryList(index, items);
        }

        /// <summary>
        /// The rows of <paramref name="group"/> whose titles contain <paramref name="query"/> (trimmed; case and
        /// accents ignored, as message search; blank is everything), newest first, each year's rows after its
        /// heading. An unknown group lists nothing.
        /// </summary>
        public IReadOnlyList<MemoryListRow> Rows(string group, string? query)
        {
            ArgumentNullException.ThrowIfNull(group);

            var needle = (query ?? string.Empty).Trim();
            var rows = new List<MemoryListRow>();
            int? year = null;

            foreach (var item in _items)
            {
                if (item.Group != group)
                {
                    continue;
                }

                if (needle.Length > 0 && ConversationSearch.FindAll(item.Title, needle).Count == 0)
                {
                    continue;
                }

                if (item.SortTime.Year != year)
                {
                    year = item.SortTime.Year;
                    rows.Add(new MemoryListYearRow(item.SortTime.Year));
                }

                rows.Add(item);
            }

            return rows;
        }

        /// <summary>
        /// The empty line for <paramref name="rows"/> of <paramref name="group"/>, or empty when there are rows: the
        /// group's own line when it lists nothing at all (<see cref="NoPersonalText"/> for the personal group,
        /// <see cref="NothingSharedText"/> for a collection), otherwise <see cref="NoMatchText"/>.
        /// </summary>
        public string EmptyText(string group, IReadOnlyList<MemoryListRow> rows)
        {
            ArgumentNullException.ThrowIfNull(group);
            ArgumentNullException.ThrowIfNull(rows);

            if (rows.Count > 0)
            {
                return string.Empty;
            }

            if (CountIn(group) > 0)
            {
                return NoMatchText;
            }

            return group == PersonalGroup ? NoPersonalText : NothingSharedText;
        }

        /// <summary>
        /// Where Show in chat goes for a listed row, or <see langword="null"/> for no button: the row when it is
        /// visible in <paramref name="view"/> now; otherwise its day when that day has messages (not for a Month
        /// memory, whose 1st is not its day); never in an archive without messages.
        /// </summary>
        public MemoryShowTarget? ShowInChat(MemoryKey key, MemoryView view, bool archiveHasMessages)
        {
            ArgumentNullException.ThrowIfNull(view);

            if (!archiveHasMessages || !_byKey.TryGetValue(key, out var item) || _index.Entry(key.EntryID) is not { } entry)
            {
                return null;
            }

            // Visible now: the same rule Layout applies, for this one entry.
            if (view.IsShown(entry.Collection.CollectionID) && entry.Rows(view).Any(row => row.Key == key))
            {
                return MemoryShowTarget.AtRow(key);
            }

            if (item.Kind == MemoryListItemKind.Entry && entry.Entry.DatePrecision == MonthPrecision)
            {
                return null;
            }

            var day = DateOnly.FromDateTime(item.SortTime);

            return _index.HasMessagesOn(day) ? MemoryShowTarget.AtDay(day) : null;
        }

        private static MemoryListItem ItemOf(MemoryIndex index, MemoryEntry entry, MemoryKey key, DateTime time, IReadOnlySet<long> yearlyEntryIDs)
        {
            var collection = entry.Collection;
            var imported = collection.Kind == ArchiveMemoryVocabulary.KindImported;
            var group = imported ? CollectionGroup(collection.CollectionID) : PersonalGroup;
            var title = entry.Entry.Title;

            if (key.PeriodIndex is { } periodIndex)
            {
                var period = entry.Period(periodIndex);
                var periodDay = DateSeparatorRow.DayText(DateOnly.FromDateTime(period.StartLocal));
                var span = MemoryWording.SpanText(period.StartLocal, period.EndLocal) + " · " + MemoryWording.PeriodLabel(period.Label);

                return new MemoryListItem(
                    key,
                    title,
                    periodDay,
                    span,
                    null,
                    false,
                    time)
                {
                    Group = group,
                    Kind = MemoryListItemKind.Period,
                    Line = periodDay + " · " + span,
                };
            }

            if (entry.Periods.Count > 0)
            {
                var day = DateSeparatorRow.DayText(DateOnly.FromDateTime(time));

                return new MemoryListItem(
                    key,
                    title,
                    day,
                    DifferentTimesText,
                    null,
                    false,
                    time)
                {
                    Group = group,
                    Kind = MemoryListItemKind.DifferentTimes,
                    MutedLine = day + " · " + DifferentTimesText,
                };
            }

            var yearly = !imported && yearlyEntryIDs.Contains(entry.Entry.EntryID);
            var date = MemoryWording.EntryDate(entry.Entry.DatePrecision, entry.Entry.StartLocal, entry.Entry.EndLocal);
            var detail = yearly ? collection.Name + " · Every year" : collection.Name;

            return new MemoryListItem(
                key,
                title,
                date,
                detail,
                imported ? null : index.CoverOf(entry.Entry.EntryID),
                yearly,
                time)
            {
                Group = group,
                Kind = MemoryListItemKind.Entry,
                Line = date,
                MutedLine = detail,
            };
        }
    }
}
