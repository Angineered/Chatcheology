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
    /// <param name="OnThisDayEnabled">Whether On this day is on for the archive; off, it never notifies.</param>
    /// <param name="Dates">Its gifted and own noted dates, each with whether it notifies (a muted gifted date does not).</param>
    /// <param name="PostedThrough">The last day this archive's notification was posted, if any: it is not posted twice that day.</param>
    public sealed record ArchiveNotedDates(
        string ArchiveId, string ContentsSha256, bool OnThisDayEnabled, IReadOnlyList<NotedDateListing> Dates, DateOnly? PostedThrough = null)
    {
        /// <summary>The dates that can notify: none when On this day is off, otherwise every date not muted.</summary>
        public IEnumerable<NotedDateListing> Notifying => OnThisDayEnabled ? Dates.Where(date => date.Notify) : [];
    }

    /// <summary>The one notification an archive gets on a day.</summary>
    /// <param name="ArchiveId">The archive's folder name.</param>
    /// <param name="ContentsSha256">The archive's identity.</param>
    /// <param name="Count">How many of its noted dates fall on the day.</param>
    /// <param name="Text">What the notification says: see <see cref="NotedDateSchedule.TextFor"/>.</param>
    public sealed record DueNotification(string ArchiveId, string ContentsSha256, int Count, string Text);

    /// <summary>The next moment a noted date notifies, and what is posted then.</summary>
    /// <param name="Day">The day.</param>
    /// <param name="LocalMoment">
    /// When, in the phone's local wall-clock time: the day at the notification time, or, for today when that
    /// time has already passed without today being posted, a moment from now (<see cref="NotedDateSchedule.CatchUpDelay"/>).
    /// </param>
    /// <param name="Due">One notification per archive with noted dates that day, in archive order.</param>
    public sealed record NextNotification(DateOnly Day, DateTime LocalMoment, IReadOnlyList<DueNotification> Due);

    /// <summary>
    /// When noted dates notify (CH-47 step 3), without any Android type: from every installed archive's noted
    /// dates, mutes and On this day switch, and the app's notification time.
    /// </summary>
    /// <remarks>
    /// <para>Only noted dates notify; message years never do. One notification per archive per day, at the
    /// app's time (default 09:00, local). A muted gifted date and every date of an archive with On this day
    /// off are left out. An archive with no messages still notifies for its noted dates. An archive already
    /// posted today (<see cref="ArchiveNotedDates.PostedThrough"/>) is not posted again that day.</para>
    /// <para><strong>Late today.</strong> When the time has passed and an archive due today has not been posted
    /// (the phone was off or restarted, the alarm was deferred, a date for today was added), today is
    /// scheduled at once: <see cref="CatchUpDelay"/> from now, through the normal alarm.</para>
    /// <para>29 February follows <see cref="NotedDates.Matches"/>: in a year without one it notifies on
    /// 28 February.</para>
    /// <para>Times are local wall-clock times; turning one into an instant is <see cref="ToUtc"/>'s job, so the
    /// day arithmetic never crosses a daylight-saving change.</para>
    /// <para><strong>Privacy.</strong> The notification never carries a title or the conversation's name, so
    /// nothing personal is on a lock screen whatever its setting; the titles are on the card after the tap.</para>
    /// </remarks>
    public static class NotedDateSchedule
    {
        /// <summary>
        /// How many days ahead are searched: every noted date falls at least once a year (29 February on
        /// 28 February otherwise), so a year and a day is enough.
        /// </summary>
        public const int DaysAhead = 367;

        /// <summary>How long after now a late notification for today is scheduled.</summary>
        public static readonly TimeSpan CatchUpDelay = TimeSpan.FromMinutes(1);

        /// <summary>
        /// What a notification for <paramref name="count"/> noted dates says: "A noted date today" or
        /// "N noted dates today". Never a title (CH-47 review, 2026-10-28): the one place to change if titles
        /// are ever shown after unlock.
        /// </summary>
        public static string TextFor(int count) =>
            count == 1 ? "A noted date today" : $"{count.ToString(CultureInfo.CurrentCulture)} noted dates today";

        /// <summary>
        /// The notifications for <paramref name="day"/>: one per archive with noted dates that day and not yet
        /// posted that day.
        /// </summary>
        public static IReadOnlyList<DueNotification> DueOn(IEnumerable<ArchiveNotedDates> archives, DateOnly day)
        {
            ArgumentNullException.ThrowIfNull(archives);

            var due = new List<DueNotification>();

            foreach (var archive in archives)
            {
                if (archive.PostedThrough == day)
                {
                    continue;
                }

                var count = archive.Notifying.Count(date => NotedDates.Matches(date.Month, date.Day, day));

                if (count > 0)
                {
                    due.Add(new DueNotification(archive.ArchiveId, archive.ContentsSha256, count, TextFor(count)));
                }
            }

            return due;
        }

        /// <summary>
        /// The next moment a noted date notifies after <paramref name="localNow"/>, or <see langword="null"/> when
        /// nothing can notify. Today at the time when that is still ahead; today at once (a
        /// <see cref="CatchUpDelay"/> from now) when the time has passed and an archive due today is not yet
        /// posted; otherwise the first later day with one.
        /// </summary>
        /// <param name="archives">Every installed archive's noted dates.</param>
        /// <param name="time">The app's notification time.</param>
        /// <param name="localNow">The phone's local time now.</param>
        public static NextNotification? Next(IEnumerable<ArchiveNotedDates> archives, TimeOnly time, DateTime localNow)
        {
            ArgumentNullException.ThrowIfNull(archives);

            var list = archives.ToList();

            if (!list.Any(archive => archive.Notifying.Any()))
            {
                return null;
            }

            var now = DateTime.SpecifyKind(localNow, DateTimeKind.Unspecified);
            var day = DateOnly.FromDateTime(now);

            if (day.ToDateTime(time) <= now)
            {
                var late = DueOn(list, day);

                if (late.Count > 0)
                {
                    return new NextNotification(day, now + CatchUpDelay, late);
                }

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
                        settings.GetOnThisDayEnabled(hash),
                        NotedDates.List(settings, hash, reader.ReadNotedDates()),
                        settings.GetNotedDatesPostedThrough(hash)));
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
