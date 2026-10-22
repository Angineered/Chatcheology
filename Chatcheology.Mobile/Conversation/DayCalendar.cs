using System.Globalization;

namespace Chatcheology.Mobile.Conversation
{
    /// <summary>One cell of a month grid: a day of the month, or a blank before the first.</summary>
    /// <param name="Day">The day, or <see langword="null"/> for a blank cell.</param>
    /// <param name="Text">The day number, or empty for a blank.</param>
    /// <param name="HasMessages">Whether the day has messages, so it can be picked.</param>
    public sealed record CalendarCell(DateOnly? Day, string Text, bool HasMessages);

    /// <summary>One month of the date picker, laid out as weeks.</summary>
    /// <param name="Index">Its position among the months with messages.</param>
    /// <param name="Year">The year.</param>
    /// <param name="Month">The month, 1 to 12.</param>
    /// <param name="Title">The month and year as the picker heads it.</param>
    /// <param name="WeekdayNames">The column headings, starting on the first day of the week.</param>
    /// <param name="Cells">Blanks before the first day, then every day of the month, row by row of seven.</param>
    public sealed record CalendarMonth(
        int Index, int Year, int Month, string Title, IReadOnlyList<string> WeekdayNames, IReadOnlyList<CalendarCell> Cells);

    /// <summary>
    /// The date picker's calendar: only days that have messages can be picked, and only months that
    /// have messages are shown (design "Date picker: only days that have messages").
    /// </summary>
    /// <remarks>Built from <c>ArchiveReader.ReadDays</c>. Days only: nothing about the messages themselves.</remarks>
    public sealed class DayCalendar
    {
        private readonly HashSet<DateOnly> _days;
        private readonly List<(int Year, int Month)> _months;
        private readonly DayOfWeek _firstDayOfWeek;
        private readonly CultureInfo _culture;

        /// <summary>A calendar of <paramref name="days"/>.</summary>
        /// <param name="days">The days that have messages, in any order.</param>
        /// <param name="firstDayOfWeek">The first column; the current culture's when not given.</param>
        /// <param name="culture">For month and weekday names; the current culture when not given.</param>
        public DayCalendar(IEnumerable<DateOnly> days, DayOfWeek? firstDayOfWeek = null, CultureInfo? culture = null)
        {
            ArgumentNullException.ThrowIfNull(days);

            _culture = culture ?? CultureInfo.CurrentCulture;
            _firstDayOfWeek = firstDayOfWeek ?? _culture.DateTimeFormat.FirstDayOfWeek;
            _days = [.. days];
            _months = _days.Select(day => (day.Year, day.Month)).Distinct().Order().ToList();
        }

        /// <summary>How many months have messages.</summary>
        public int MonthCount => _months.Count;

        /// <summary>Whether no day has messages: the picker is not offered.</summary>
        public bool IsEmpty => _months.Count == 0;

        /// <summary>Whether <paramref name="day"/> has messages.</summary>
        public bool HasMessages(DateOnly day) => _days.Contains(day);

        /// <summary>
        /// The index of the month with messages that holds <paramref name="day"/>; otherwise the nearest
        /// such month (the later one on a tie). -1 when the calendar is empty.
        /// </summary>
        public int IndexOfMonthNear(DateOnly day)
        {
            if (IsEmpty)
            {
                return -1;
            }

            var target = MonthNumber(day.Year, day.Month);
            var best = 0;
            var bestDistance = int.MaxValue;

            for (var i = 0; i < _months.Count; i++)
            {
                var distance = Math.Abs(MonthNumber(_months[i].Year, _months[i].Month) - target);

                if (distance <= bestDistance)
                {
                    best = i;
                    bestDistance = distance;
                }

                if (distance == 0)
                {
                    break;
                }
            }

            return best;
        }

        /// <summary>
        /// The month with messages nearest to the same month a year earlier (<paramref name="years"/> = -1)
        /// or later (+1), among the months of earlier (or later) years than the month at
        /// <paramref name="index"/>; -1 when no year that way has messages.
        /// </summary>
        public int YearStep(int index, int years)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _months.Count);

            if (years == 0)
            {
                return index;
            }

            var target = MonthNumber(_months[index].Year, _months[index].Month) + 12 * years;
            var best = -1;
            var bestDistance = int.MaxValue;

            for (var i = 0; i < _months.Count; i++)
            {
                if ((years < 0 && _months[i].Year >= _months[index].Year) || (years > 0 && _months[i].Year <= _months[index].Year))
                {
                    continue;
                }

                var distance = Math.Abs(MonthNumber(_months[i].Year, _months[i].Month) - target);

                if (distance < bestDistance)
                {
                    best = i;
                    bestDistance = distance;
                }
            }

            return best;
        }

        /// <summary>The month at <paramref name="index"/> among the months with messages, as a grid.</summary>
        public CalendarMonth Month(int index)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _months.Count);

            var (year, month) = _months[index];
            var first = new DateOnly(year, month, 1);
            var blanks = ((int)first.DayOfWeek - (int)_firstDayOfWeek + 7) % 7;
            var cells = new List<CalendarCell>(blanks + 31);

            for (var i = 0; i < blanks; i++)
            {
                cells.Add(new CalendarCell(null, string.Empty, false));
            }

            for (var day = first; day.Month == month; day = day.AddDays(1))
            {
                cells.Add(new CalendarCell(day, day.Day.ToString(_culture), _days.Contains(day)));
            }

            var names = Enumerable.Range(0, 7)
                .Select(offset => _culture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)(((int)_firstDayOfWeek + offset) % 7)))
                .ToList();

            return new CalendarMonth(index, year, month, first.ToString("MMMM yyyy", _culture), names, cells);
        }

        private static int MonthNumber(int year, int month) => year * 12 + month - 1;
    }
}
