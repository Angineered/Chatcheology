using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Memories;

namespace Chatcheology.Mobile.Conversation
{
    /// <summary>
    /// A noted date the phone's own user added (CH-47): kept in the app's settings for the archive, never in
    /// the archive. Fields follow <see cref="NotedDateRules"/>.
    /// </summary>
    /// <param name="Id">A stable identifier made on the phone.</param>
    /// <param name="Title">The title.</param>
    /// <param name="Month">The month.</param>
    /// <param name="Day">The day.</param>
    /// <param name="Year">The year it started, or none.</param>
    /// <param name="Note">A short note, or none.</param>
    public sealed record OwnNotedDate(string Id, string Title, int Month, int Day, int? Year, string? Note)
    {
        /// <summary>Its external gallery links (CH-48), in order, each keeping <see cref="ExternalLinkRules"/>.</summary>
        public IReadOnlyList<GalleryLink> Links { get; init; } = [];

        /// <summary>Equal when every field and every link, in order, are equal.</summary>
        public bool Equals(OwnNotedDate? other) =>
            other is not null
            && Id == other.Id && Title == other.Title && Month == other.Month && Day == other.Day && Year == other.Year
            && Note == other.Note && Links.SequenceEqual(other.Links);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Id, Title, Month, Day, Year, Note, Links.Count);
    }

    /// <summary>One noted date as Settings lists it.</summary>
    /// <param name="Id">The identifier: the archive's number for a gifted date, the phone's for an own one.</param>
    /// <param name="Title">The title.</param>
    /// <param name="Month">The month.</param>
    /// <param name="Day">The day.</param>
    /// <param name="Year">The year it started, or none.</param>
    /// <param name="Note">A short note, or none.</param>
    /// <param name="IsGifted">Whether it came with the archive (read-only here; it can only be muted).</param>
    /// <param name="Notify">Whether it notifies (step 3 uses this); an own date always does.</param>
    public sealed record NotedDateListing(string Id, string Title, int Month, int Day, int? Year, string? Note, bool IsGifted, bool Notify)
    {
        /// <summary>
        /// Its external gallery links (CH-48), in order: a gifted date's from the archive, an own date's from the
        /// phone's settings.
        /// </summary>
        public IReadOnlyList<GalleryLink> Galleries { get; init; } = [];

        /// <summary>"Link · photos.google.com", "3 links", or empty.</summary>
        public string GalleryText => GalleryLinks.Summary(Galleries);

        /// <summary>Equal when every field and every link, in order, are equal.</summary>
        public bool Equals(NotedDateListing? other) =>
            other is not null
            && Id == other.Id && Title == other.Title && Month == other.Month && Day == other.Day && Year == other.Year
            && Note == other.Note && IsGifted == other.IsGifted && Notify == other.Notify && Galleries.SequenceEqual(other.Galleries);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Id, Title, Month, Day, Year, Note, IsGifted, Notify);

        /// <summary>"14 February", plus the year when one is set.</summary>
        public string DateText => NotedDates.DateText(Month, Day, Year);
    }

    /// <summary>A noted date that falls on today, for the card.</summary>
    /// <param name="Title">The title.</param>
    /// <param name="Note">The note, or none.</param>
    /// <param name="Years">How many years since it started, when a year is set and at least 1; otherwise none.</param>
    /// <param name="JumpDay">The day to jump to: the noted year's day, when that year has messages on it; otherwise none.</param>
    public sealed record NotedToday(string Title, string? Note, int? Years, DateOnly? JumpDay)
    {
        /// <summary>Its external gallery links (CH-48), in order.</summary>
        public IReadOnlyList<GalleryLink> Galleries { get; init; } = [];

        /// <summary>"Link · photos.google.com", "3 links", or empty.</summary>
        public string GalleryText => GalleryLinks.Summary(Galleries);

        /// <summary>The row's text: "Our anniversary · 8 years", or the title alone.</summary>
        public string Text => Years is { } years
            ? $"{Title} · {years.ToString(CultureInfo.CurrentCulture)} {(years == 1 ? "year" : "years")}"
            : Title;
    }

    /// <summary>Which noted dates fall on a day (CH-47).</summary>
    public static class NotedDates
    {
        /// <summary>
        /// Whether a noted date on <paramref name="month"/>/<paramref name="day"/> shows on <paramref name="today"/>.
        /// 29 February follows CH-39: in a year without one it shows on 28 February; on 29 February itself only
        /// 29 February matches.
        /// </summary>
        public static bool Matches(int month, int day, DateOnly today) =>
            (month == today.Month && day == today.Day)
            || (month == 2 && day == 29 && today is { Month: 2, Day: 28 } && !DateTime.IsLeapYear(today.Year));

        /// <summary>
        /// Every noted date of the archive for Settings: the gifted ones (with their mute switch) and the
        /// phone's own, each group by title.
        /// </summary>
        /// <param name="settings">The app's settings (own dates and mutes).</param>
        /// <param name="contentsSha256">The archive's identity.</param>
        /// <param name="gifted">The archive's noted dates.</param>
        /// <param name="giftedLinks">The gifted dates' gallery links by noted-date identifier (CH-48), or none.</param>
        public static IReadOnlyList<NotedDateListing> List(
            Settings.ArchiveSettings settings,
            string contentsSha256,
            IEnumerable<ArchiveNotedDate> gifted,
            IReadOnlyDictionary<long, IReadOnlyList<GalleryLink>>? giftedLinks = null)
        {
            var muted = settings.GetMutedNotedDates(contentsSha256);

            return gifted
                .Select(date => new NotedDateListing(
                    date.NotedDateID.ToString(CultureInfo.InvariantCulture), date.Title, date.Month, date.Day, date.Year, date.Note,
                    IsGifted: true, Notify: !muted.Contains(date.NotedDateID))
                {
                    Galleries = giftedLinks?.GetValueOrDefault(date.NotedDateID) ?? [],
                })
                .OrderBy(date => date.Title, StringComparer.CurrentCultureIgnoreCase)
                .Concat(settings.GetOwnNotedDates(contentsSha256)
                    .Select(date => new NotedDateListing(date.Id, date.Title, date.Month, date.Day, date.Year, date.Note, IsGifted: false, Notify: true)
                    {
                        Galleries = date.Links,
                    }))
                .ToList();
        }

        /// <summary>
        /// Today's noted dates, by title: gifted and own together. <paramref name="days"/> are the days with
        /// messages, for the jump.
        /// </summary>
        public static IReadOnlyList<NotedToday> For(
            IEnumerable<NotedDateListing> dates, IReadOnlySet<DateOnly> days, DateOnly today) =>
            dates
                .Where(date => Matches(date.Month, date.Day, today))
                .Select(date => new NotedToday(date.Title, date.Note, YearsSince(date.Year, today), JumpDay(date, days, today))
                {
                    Galleries = date.Galleries,
                })
                .OrderBy(date => date.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        /// <summary>Whole years from the noted year to today's year, or none when no year is set or it is not an earlier year.</summary>
        public static int? YearsSince(int? year, DateOnly today) => year is { } y && today.Year - y >= 1 ? today.Year - y : null;

        /// <summary>"14 February 2019", or "14 February" without a year.</summary>
        public static string DateText(int month, int day, int? year)
        {
            var text = string.Create(CultureInfo.CurrentCulture, $"{day} {CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(month)}");

            return year is { } y ? text + " " + y.ToString(CultureInfo.CurrentCulture) : text;
        }

        private static DateOnly? JumpDay(NotedDateListing date, IReadOnlySet<DateOnly> days, DateOnly today)
        {
            if (date.Year is not { } year || year >= today.Year || !IsDate(year, date.Month, date.Day))
            {
                return null;
            }

            var day = new DateOnly(year, date.Month, date.Day);

            return days.Contains(day) ? day : null;
        }

        private static bool IsDate(int year, int month, int day) =>
            year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month);
    }
}
