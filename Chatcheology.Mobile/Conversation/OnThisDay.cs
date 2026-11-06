using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Settings;

namespace Chatcheology.Mobile.Conversation
{
    /// <summary>One earlier year with messages on today's day and month.</summary>
    /// <param name="Year">The year, as the card shows it.</param>
    /// <param name="Day">The day to jump to in that year.</param>
    public sealed record OnThisDayYear(int Year, DateOnly Day)
    {
        /// <summary>The year as the card shows it.</summary>
        public string Text => Year.ToString(CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// On this day (CH-39): the earlier years that have messages on today's day and month, by the phone's
    /// date, and whether the card is shown when the conversation opens.
    /// </summary>
    /// <remarks>
    /// <para>Days are as stamped in the export (<c>ArchiveReader.ReadDays</c>), the same days the
    /// separators show. Only years before today's count.</para>
    /// <para><strong>29 February.</strong> In a year without one, 28 February also matches the 29 February
    /// of earlier years; such a year jumps to its 28 February when it has messages then too. On 29 February
    /// itself only 29 February matches (its 28 February matched the day before).</para>
    /// <para><strong>Once a day.</strong> The card opens by itself the first time the conversation is opened
    /// on a day; showing it records the day, so after that (dismissed or not) it opens only from the top-bar
    /// icon until tomorrow. Turned off in Settings, there is neither card nor icon.</para>
    /// </remarks>
    public sealed class OnThisDay
    {
        private readonly ArchiveSettings _settings;
        private readonly string _identity;

        private OnThisDay(
            ArchiveSettings settings,
            string identity,
            DateOnly today,
            IReadOnlyList<OnThisDayYear> years,
            IReadOnlyList<NotedToday> noted,
            IReadOnlyList<YearlyToday> yearly)
        {
            _settings = settings;
            _identity = identity;
            Today = today;
            Years = years;
            Noted = noted;
            Yearly = yearly;
        }

        /// <summary>
        /// Today's yearly memories (CH-53), by title; they come first, before the noted dates and the message
        /// years, and turn the card on by themselves. Shown whatever the memory layer switches say.
        /// </summary>
        public IReadOnlyList<YearlyToday> Yearly { get; }

        /// <summary>
        /// Today's noted dates (CH-47), gifted and own, by title; they come before the message years and
        /// turn the card on even when no year has messages.
        /// </summary>
        public IReadOnlyList<NotedToday> Noted { get; }

        /// <summary>The phone's date the years were found for.</summary>
        public DateOnly Today { get; }

        /// <summary>The earlier years with messages on today's day, oldest first.</summary>
        public IReadOnlyList<OnThisDayYear> Years { get; }

        /// <summary>Whether On this day is on for the archive (Settings).</summary>
        public bool IsEnabled => _settings.GetOnThisDayEnabled(_identity);

        /// <summary>Whether the top-bar icon is shown: on, and a yearly memory, a noted date or at least one year matches.</summary>
        public bool ShowsIcon => IsEnabled && (Years.Count > 0 || Noted.Count > 0 || Yearly.Count > 0);

        /// <summary>Whether the card opens by itself now: the icon is shown and it has not been shown today.</summary>
        public bool OpensByItself => ShowsIcon && _settings.GetOnThisDayShown(_identity) != Today;

        /// <summary>The card's line: "On this day: 2016 · 2019 · 2021".</summary>
        public string Text => "On this day: " + string.Join(" · ", Years.Select(year => year.Text));

        /// <summary>
        /// The years and noted dates for the archive <paramref name="contentsSha256"/> on <paramref name="today"/>.
        /// <paramref name="gifted"/> are the archive's own noted dates (none for an archive without any); the
        /// phone's own are read from <paramref name="settings"/>.
        /// </summary>
        /// <remarks>
        /// <paramref name="giftedLinks"/> are the gifted dates' gallery links (CH-48) by identifier, or none.
        /// <paramref name="yearly"/> are the archive's yearly memories (CH-53, none before version 3), and
        /// <paramref name="memories"/> the memory index their thumbnails and gallery links come from.
        /// </remarks>
        public static OnThisDay For(
            ArchiveSettings settings,
            string contentsSha256,
            IEnumerable<DateOnly> days,
            DateOnly today,
            IEnumerable<ArchiveNotedDate>? gifted = null,
            IReadOnlyDictionary<long, IReadOnlyList<Memories.GalleryLink>>? giftedLinks = null,
            IEnumerable<YearlyMemory>? yearly = null,
            Memories.MemoryIndex? memories = null,
            Memories.MemoryIndex? ownMemories = null)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentException.ThrowIfNullOrWhiteSpace(contentsSha256);

            var dayList = days as IReadOnlyCollection<DateOnly> ?? days.ToList();
            var daySet = dayList.ToHashSet();
            var noted = NotedDates.For(
                NotedDates.List(settings, contentsSha256, gifted ?? [], giftedLinks),
                daySet,
                today);

            return new OnThisDay(
                settings, contentsSha256, today, Find(dayList, today), noted, YearlyMemories.For(yearly ?? [], daySet, today, memories, ownMemories));
        }

        /// <summary>Records that the card was shown today, so it does not open by itself again until tomorrow.</summary>
        public void MarkShown() => _settings.SetOnThisDayShown(_identity, Today);

        /// <summary>
        /// The earlier years with messages on <paramref name="today"/>'s day and month, oldest first, each with
        /// the day to jump to.
        /// </summary>
        public static IReadOnlyList<OnThisDayYear> Find(IEnumerable<DateOnly> days, DateOnly today)
        {
            ArgumentNullException.ThrowIfNull(days);

            var leapDayToo = today is { Month: 2, Day: 28 } && !DateTime.IsLeapYear(today.Year);

            return days
                .Where(day => day.Year < today.Year
                              && ((day.Month == today.Month && day.Day == today.Day) || (leapDayToo && day is { Month: 2, Day: 29 })))
                .GroupBy(day => day.Year)
                .Select(group => new OnThisDayYear(group.Key, group.Min()))
                .OrderBy(year => year.Year)
                .ToList();
        }

        /// <summary>The phone's local date from <paramref name="clock"/>.</summary>
        public static DateOnly TodayFrom(TimeProvider clock)
        {
            ArgumentNullException.ThrowIfNull(clock);

            return DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        }
    }
}
