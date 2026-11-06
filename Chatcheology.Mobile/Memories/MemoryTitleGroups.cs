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
    /// One title's heading when a collection tab is grouped by title (CH-59, D-35): every memory of the collection with
    /// exactly this title (ordinal, case-sensitive, as stored), so one heading per game across its game-day records.
    /// </summary>
    /// <param name="Title">The exact title: the heading's text and its key within the collection (kept in memory only, never logged).</param>
    /// <param name="Sessions">How many shared-period rows its memories list; "different times" rows are not counted.</param>
    /// <param name="First">The earliest listed row's time.</param>
    /// <param name="Last">The latest listed row's time.</param>
    /// <param name="IsExpanded">Whether its rows are shown under it.</param>
    public sealed record MemoryListTitleRow(string Title, int Sessions, DateTime First, DateTime Last, bool IsExpanded) : MemoryListRow
    {
        /// <summary>"12 sessions · Mar 2021 - Jan 2024", "1 session · Mar 2021" (see <see cref="MemoryTitleGroups.SummaryText"/>).</summary>
        public string Summary => MemoryTitleGroups.SummaryText(Sessions, First, Last);

        /// <summary>What a screen reader hears: "Title, 12 sessions · Mar 2021 - Jan 2024, collapsed".</summary>
        public string ScreenReaderText => Title + ", " + Summary + ", " + (IsExpanded ? "expanded" : "collapsed");

        /// <summary>
        /// The open or closed mark drawn before the title: full-size triangles (not the small ones), each with the
        /// text-style selector so Android never draws them as emoji.
        /// </summary>
        public string Mark => IsExpanded ? "▼︎" : "▶︎";
    }

    /// <summary>
    /// A collection tab grouped by title (CH-59, D-35): one heading per exact title, built over the Memories list's own
    /// rows (<see cref="MemoryList.Rows"/>) without adding, removing or reordering any of them. Pure; reads nothing.
    /// </summary>
    /// <remarks>
    /// <para><strong>Grouping.</strong> An imported entry is one game-day record, so a game has many entries. They are
    /// grouped by their exact title within the one collection (ordinal, case-sensitive, nothing trimmed or
    /// normalised): one heading per game. Display only; nothing is merged or written. Titles differing in any
    /// character, case included, stay apart.</para>
    /// <para><strong>Count.</strong> Only shared-period rows (<see cref="MemoryListItemKind.Period"/>) of all its
    /// entries are sessions; a "different times" row sits under its heading but is not counted.</para>
    /// <para><strong>Dates.</strong> First to last of all its listed rows, by month: "Mar 2021 - Jan 2024", or
    /// "Mar 2021" when both fall in the same month.</para>
    /// <para><strong>Order.</strong> Headings by their latest row, newest first (ties as the list orders); rows under
    /// a heading newest first, in the list's own order.</para>
    /// <para><strong>Search.</strong> The list's title search, so only matching headings are left.</para>
    /// </remarks>
    public static class MemoryTitleGroups
    {
        /// <summary>
        /// The heading's second line: "12 sessions · Mar 2021 - Jan 2024"; "1 session" for one; "0 sessions" when
        /// the title lists only "different times" rows (or has no periods); one month alone when first and last
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
        /// <see cref="MemoryList.Rows"/>), newest first, each followed by its rows when its title is in
        /// <paramref name="expanded"/> (compared ordinally).
        /// </summary>
        public static IReadOnlyList<MemoryListRow> Rows(MemoryList list, string group, string? query, IReadOnlySet<string> expanded)
        {
            ArgumentNullException.ThrowIfNull(list);
            ArgumentNullException.ThrowIfNull(group);
            ArgumentNullException.ThrowIfNull(expanded);

            var rows = new List<MemoryListRow>();

            foreach (var heading in Groups(list, group, query))
            {
                var open = expanded.Contains(heading.Row.Title);
                rows.Add(heading.Row with { IsExpanded = open });

                if (open)
                {
                    rows.AddRange(heading.Items);
                }
            }

            return rows;
        }

        /// <summary>The rows of <paramref name="group"/> grouped by exact title, before any heading is opened.</summary>
        public static IReadOnlyList<(MemoryListTitleRow Row, IReadOnlyList<MemoryListItem> Items)> Groups(MemoryList list, string group, string? query)
        {
            ArgumentNullException.ThrowIfNull(list);
            ArgumentNullException.ThrowIfNull(group);

            var needle = (query ?? string.Empty).Trim();
            var byTitle = new Dictionary<string, List<MemoryListItem>>(StringComparer.Ordinal);
            var order = new List<string>();

            // The list is newest first, so each title's first row met is its latest, and titles are met in the
            // order of their latest rows (ties as the list sorts).
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

                if (!byTitle.TryGetValue(item.Title, out var items))
                {
                    items = [];
                    byTitle[item.Title] = items;
                    order.Add(item.Title);
                }

                items.Add(item);
            }

            return order.Select(title =>
            {
                var items = byTitle[title];
                var row = new MemoryListTitleRow(
                    title,
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
