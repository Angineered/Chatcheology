using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Settings;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Mobile.Notifications
{
    /// <summary>One installed archive's noted dates, as the notification schedule sees them.</summary>
    /// <param name="ArchiveId">The archive's folder name under <c>archives/</c> (what a tap opens).</param>
    /// <param name="ContentsSha256">The archive's identity.</param>
    /// <param name="ConversationTitle">The conversation's title, shown with the notification after unlock.</param>
    /// <param name="OnThisDayEnabled">Whether On this day is on for the archive; off, it never notifies.</param>
    /// <param name="Dates">Its gifted and own noted dates, each with whether it notifies (a muted gifted date does not).</param>
    public sealed record ArchiveNotedDates(
        string ArchiveId, string ContentsSha256, string ConversationTitle, bool OnThisDayEnabled, IReadOnlyList<NotedDateListing> Dates)
    {
        /// <summary>The dates that can notify: none when On this day is off, otherwise every date not muted.</summary>
        public IEnumerable<NotedDateListing> Notifying => OnThisDayEnabled ? Dates.Where(date => date.Notify) : [];
    }

    /// <summary>The one notification an archive gets on a day.</summary>
    /// <param name="ArchiveId">The archive's folder name.</param>
    /// <param name="ContentsSha256">The archive's identity.</param>
    /// <param name="ConversationTitle">The conversation's title.</param>
    /// <param name="Count">How many of its noted dates fall on the day.</param>
    /// <param name="Text">
    /// The text: the one date as the card shows it ("Our anniversary · 8 years", or the title alone), or
    /// "N noted dates today" when several.
    /// </param>
    public sealed record DueNotification(string ArchiveId, string ContentsSha256, string ConversationTitle, int Count, string Text);

    /// <summary>The next moment a noted date notifies, and what is posted then.</summary>
    /// <param name="Day">The day.</param>
    /// <param name="LocalMoment">The day at the notification time, in the phone's local wall-clock time.</param>
    /// <param name="Due">One notification per archive with noted dates that day, in archive order.</param>
    public sealed record NextNotification(DateOnly Day, DateTime LocalMoment, IReadOnlyList<DueNotification> Due);

    /// <summary>
    /// When noted dates notify (CH-47 step 3), without any Android type: from every installed archive's noted
    /// dates, mutes and On this day switch, and the app's notification time.
    /// </summary>
    /// <remarks>
    /// <para>Only noted dates notify; message years never do. One notification per archive per day, at the
    /// app's time (default 09:00, local). A muted gifted date and every date of an archive with On this day
    /// off are left out. An archive with no messages still notifies for its noted dates.</para>
    /// <para>29 February follows <see cref="NotedDates.Matches"/>: in a year without one it notifies on
    /// 28 February.</para>
    /// <para>Times are local wall-clock times; turning one into an instant is <see cref="ToUtc"/>'s job, so the
    /// day arithmetic never crosses a daylight-saving change.</para>
    /// </remarks>
    public static class NotedDateSchedule
    {
        /// <summary>
        /// How many days ahead are searched: every noted date falls at least once a year (29 February on
        /// 28 February otherwise), so a year and a day is enough.
        /// </summary>
        public const int DaysAhead = 367;

        /// <summary>The notifications for <paramref name="day"/>: one per archive with noted dates that day.</summary>
        public static IReadOnlyList<DueNotification> DueOn(IEnumerable<ArchiveNotedDates> archives, DateOnly day)
        {
            ArgumentNullException.ThrowIfNull(archives);

            var due = new List<DueNotification>();

            foreach (var archive in archives)
            {
                var today = archive.Notifying
                    .Where(date => NotedDates.Matches(date.Month, date.Day, day))
                    .OrderBy(date => date.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                if (today.Count == 0)
                {
                    continue;
                }

                var text = today.Count == 1
                    ? new NotedToday(today[0].Title, null, NotedDates.YearsSince(today[0].Year, day), null).Text
                    : $"{today.Count.ToString(CultureInfo.CurrentCulture)} noted dates today";

                due.Add(new DueNotification(archive.ArchiveId, archive.ContentsSha256, archive.ConversationTitle, today.Count, text));
            }

            return due;
        }

        /// <summary>
        /// The first moment strictly after <paramref name="localNow"/> at which a noted date notifies, or
        /// <see langword="null"/> when nothing can notify.
        /// </summary>
        /// <param name="archives">Every installed archive's noted dates.</param>
        /// <param name="time">The app's notification time.</param>
        /// <param name="localNow">The phone's local time now.</param>
        /// <param name="postedThrough">
        /// The last day whose notifications were posted, if any: when that is today, today is not scheduled again
        /// (moving the time later on a day already notified does not notify twice).
        /// </param>
        public static NextNotification? Next(IEnumerable<ArchiveNotedDates> archives, TimeOnly time, DateTime localNow, DateOnly? postedThrough = null)
        {
            ArgumentNullException.ThrowIfNull(archives);

            var list = archives.ToList();

            if (!list.Any(archive => archive.Notifying.Any()))
            {
                return null;
            }

            var day = DateOnly.FromDateTime(localNow);

            if (day.ToDateTime(time) <= DateTime.SpecifyKind(localNow, DateTimeKind.Unspecified))
            {
                day = day.AddDays(1);
            }

            // Only today counts: a later recorded day (the clock was set back) is ignored rather than trusted.
            if (postedThrough is { } posted && posted == day && posted == DateOnly.FromDateTime(localNow))
            {
                day = day.AddDays(1);
            }

            for (var i = 0; i < DaysAhead && day < DateOnly.MaxValue; i++, day = day.AddDays(1))
            {
                var due = DueOn(list, day);

                if (due.Count > 0)
                {
                    return new NextNotification(day, day.ToDateTime(time), due);
                }
            }

            return null;
        }

        /// <summary>
        /// The instant at which the local wall-clock time <paramref name="local"/> happens in <paramref name="zone"/>.
        /// A time skipped by a clock change (it does not exist that day) moves to the first minute after the gap;
        /// a time that happens twice is the first of them.
        /// </summary>
        public static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
        {
            ArgumentNullException.ThrowIfNull(zone);

            var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

            for (var i = 0; i < 24 * 60 && zone.IsInvalidTime(wall); i++)
            {
                wall = wall.AddMinutes(1);
            }

            if (zone.IsAmbiguousTime(wall))
            {
                // The larger offset is the earlier instant.
                var offset = zone.GetAmbiguousTimeOffsets(wall).Max();

                return DateTime.SpecifyKind(wall - offset, DateTimeKind.Utc);
            }

            return TimeZoneInfo.ConvertTimeToUtc(wall, zone);
        }

        /// <summary>
        /// Every installed archive's noted dates, read from its database and the app's settings. An archive whose
        /// identity or database cannot be read is left out (a tap could not open it). Reads every archive; call it
        /// off the UI thread.
        /// </summary>
        public static IReadOnlyList<ArchiveNotedDates> Read(ArchiveLibrary library, ArchiveSettings settings)
        {
            ArgumentNullException.ThrowIfNull(library);
            ArgumentNullException.ThrowIfNull(settings);

            var archives = new List<ArchiveNotedDates>();

            foreach (var folder in library.ListInstalled())
            {
                if (ArchiveLibrary.ContentsSha256Of(folder) is not { } hash)
                {
                    continue;
                }

                try
                {
                    using var reader = ArchiveReader.Open(folder);

                    archives.Add(new ArchiveNotedDates(
                        Path.GetFileName(folder),
                        hash,
                        reader.Info.ConversationTitle,
                        settings.GetOnThisDayEnabled(hash),
                        NotedDates.List(settings, hash, reader.ReadNotedDates())));
                }
                catch (Exception exception) when (exception is ArchiveFormatException or SqliteException or IOException or UnauthorizedAccessException)
                {
                    // Left out: it cannot be opened from a notification either.
                }
            }

            return archives;
        }

        /// <summary>
        /// The notification number for an archive: the same for the same folder name on every run (FNV-1a, not
        /// the runtime's per-process string hash), so a day's notification replaces the last one and Forget can
        /// cancel it. Never 0.
        /// </summary>
        public static int NotificationId(string archiveId)
        {
            ArgumentException.ThrowIfNullOrEmpty(archiveId);

            var hash = 2166136261u;

            foreach (var c in archiveId)
            {
                hash = (hash ^ c) * 16777619u;
            }

            var id = (int)(hash & 0x7FFFFFFF);

            return id == 0 ? 1 : id;
        }

        /// <summary>Counts for the log: never a title, a name or a date.</summary>
        public static string DescribeCounts(IReadOnlyCollection<ArchiveNotedDates> archives, NextNotification? next) =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"archives={archives.Count} notedDates={archives.Sum(archive => archive.Dates.Count)} notifying={archives.Sum(archive => archive.Notifying.Count())} scheduled={(next is null ? "no" : "yes")}");
    }
}
