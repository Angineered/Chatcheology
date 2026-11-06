using System.Globalization;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Search;

namespace Chatcheology.Mobile.Memories
{
    /// <summary>How a collection tab of the Saved screen lists its memories (CH-59).</summary>
    public enum MemoryListGrouping
    {
        /// <summary>Newest first under year headings (the default, as the Memories list always was).</summary>
        Date,

        /// <summary>One heading per memory, its rows under it.</summary>
        Title,
    }

    /// <summary>
    /// One memory's heading when a collection tab is grouped by title (CH-59): the memory itself (by its entry), never
    /// a title match, so two memories with the same title are two headings.
    /// </summary>
    /// <param name="EntryID">The memory.</param>
    /// <param name="Title">The memory's title.</param>
    /// <param name="Sessions">How many shared-period rows it lists; "different times" rows are not counted.</param>
    /// <param name="First">The earliest listed row's time.</param>
    /// <param name="Last">The latest listed row's time.</param>
    /// <param name="IsExpanded">Whether its rows are shown under it.</param>
    public sealed record MemoryListTitleRow(long EntryID, string Title, int Sessions, DateTime First, DateTime Last, bool IsExpanded) : MemoryListRow
    {
        /// <summary>"12 sessions · Mar 2021 - Jan 2024", "1 session · Mar 2021" (see <see cref="MemoryTitleGroups.SummaryText"/>).</summary>
        public string Summary => MemoryTitleGroups.SummaryText(Sessions, First, Last);

        /// <summary>What a screen reader hears: "Title, 12 sessions · Mar 2021 - Jan 2024, collapsed".</summary>
        public string ScreenReaderText => Title + ", " + Summary + ", " + (IsExpanded ? "expanded" : "collapsed");

        /// <summary>The open or closed mark drawn before the title.</summary>
        public string Mark => IsExpanded ? "▾" : "▸";
    }

    /// <summary>
    /// A collection tab grouped by title (CH-59): one heading per memory, built over the Memories list's own rows
    /// (<see cref="MemoryList.Rows"/>) without adding, removing or reordering any of them. Pure; reads nothing.
    /// </summary>
    /// <remarks>
    /// <para><strong>Grouping.</strong> By <see cref="MemoryKey.EntryID"/>, never by title text: two memories with the
    /// same title stay two headings.</para>
    /// <para><strong>Count.</strong> Only shared-period rows (<see cref="MemoryListItemKind.Period"/>) are sessions; a
    /// "different times" row sits under its memory but is not counted.</para>
    /// <para><strong>Dates.</strong> First to last of the memory's listed rows, by month: "Mar 2021 - Jan 2024", or
    /// "Mar 2021" when both fall in the same month.</para>
    /// <para><strong>Order.</strong> Headings by their latest row, newest first (ties by entry); rows under a heading
    /// newest first, in the list's own order.</para>
    /// <para><strong>Search.</strong> The list's title search, so only matching headings are left.</para>
    /// </remarks>
    public static class MemoryTitleGroups
    {
        /// <summary>
        /// The heading's second line: "12 sessions · Mar 2021 - Jan 2024"; "1 session" for one; "0 sessions" when
        /// the memory lists only "different times" rows (or has no periods); one month alone when first and last
        /// fall in the same month.
        /// </summary>
        public static string SummaryText(int sessions, DateTime first, DateTime last)
        {
            var count = sessions == 1 ? "1 session" : sessions.ToString(CultureInfo.InvariantCulture) + " sessions";

            return count + " · " + RangeText(first, last);
        }

        /// <summary>The Date | Title switch's text: "Date" or "Title".</summary>
        public static string SwitchText(MemoryListGrouping grouping) => grouping == MemoryListGrouping.Title ? "Title" : "Date";

        /// <summary>What a screen reader hears for a side of the switch: "Group by title", or "Group by date, selected".</summary>
        public static string SwitchScreenReaderName(MemoryListGrouping grouping, bool selected) =>
            "Group by " + SwitchText(grouping).ToLowerInvariant() + (selected ? ", selected" : string.Empty);

        /// <summary>"Mar 2021 - Jan 2024", or "Mar 2021" when both are in the same month (earlier first).</summary>
        public static string RangeText(DateTime first, DateTime last)
        {
            if (last < first)
            {
                (first, last) = (last, first);
            }

            var from = MonthText(first);

            return first.Year == last.Year && first.Month == last.Month ? from : from + " - " + MonthText(last);
        }

        /// <summary>
        /// The headings of <paramref name="group"/> whose titles match <paramref name="query"/> (as
        /// <see cref="MemoryList.Rows"/>), newest first, each followed by its rows when its entry is in
        /// <paramref name="expanded"/>.
        /// </summary>
        public static IReadOnlyList<MemoryListRow> Rows(MemoryList list, string group, string? query, IReadOnlySet<long> expanded)
        {
            ArgumentNullException.ThrowIfNull(list);
            ArgumentNullException.ThrowIfNull(group);
            ArgumentNullException.ThrowIfNull(expanded);

            var rows = new List<MemoryListRow>();

            foreach (var heading in Groups(list, group, query))
            {
                var open = expanded.Contains(heading.Row.EntryID);
                rows.Add(heading.Row with { IsExpanded = open });

                if (open)
                {
                    rows.AddRange(heading.Items);
                }
            }

            return rows;
        }

        /// <summary>The rows of <paramref name="group"/> grouped by memory, before any heading is opened.</summary>
        public static IReadOnlyList<(MemoryListTitleRow Row, IReadOnlyList<MemoryListItem> Items)> Groups(MemoryList list, string group, string? query)
        {
            ArgumentNullException.ThrowIfNull(list);
            ArgumentNullException.ThrowIfNull(group);

            var needle = (query ?? string.Empty).Trim();
            var byEntry = new Dictionary<long, List<MemoryListItem>>();
            var order = new List<long>();

            // The list is newest first, so each memory's first row met is its latest, and memories are met in the
            // order of their latest rows (ties by entry, as the list sorts).
            foreach (var item in list.Items)
            {
                if (item.Group != group)
                {
                    continue;
                }

                if (needle.Length > 0 && ConversationSearch.FindAll(item.Title, needle).Count == 0)
                {
                    continue;
                }

                var entryID = item.Key.EntryID;

                if (!byEntry.TryGetValue(entryID, out var items))
                {
                    items = [];
                    byEntry[entryID] = items;
                    order.Add(entryID);
                }

                items.Add(item);
            }

            return order.Select(entryID =>
            {
                var items = byEntry[entryID];
                var row = new MemoryListTitleRow(
                    entryID,
                    items[0].Title,
                    items.Count(item => item.Kind == MemoryListItemKind.Period),
                    items.Min(item => item.SortTime),
                    items.Max(item => item.SortTime),
                    false);

                return (row, (IReadOnlyList<MemoryListItem>)items);
            }).ToList();
        }

        private static string MonthText(DateTime time) => time.ToString("MMM yyyy", CultureInfo.InvariantCulture);
    }
}
