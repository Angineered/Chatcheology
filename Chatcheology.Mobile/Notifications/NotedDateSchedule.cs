using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.OwnMemories;
using Chatcheology.Mobile.Settings;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Mobile.Notifications
{
    /// <summary>One installed archive's noted dates and yearly memories, as the notification schedule sees them.</summary>
    /// <param name="ArchiveId">The archive's folder name under <c>archives/</c> (what a tap opens).</param>
    /// <param name="ContentsSha256">The archive's identity.</param>
    /// <param name="OnThisDayEnabled">Whether On this day is on for the archive; off, it never notifies.</param>
    /// <param name="Dates">Its gifted and own noted dates, each with whether it notifies (a muted gifted date does not).</param>
    /// <param name="PostedThrough">The last day this archive's notification was posted, if any: it is not posted twice that day.</param>
    public sealed record ArchiveNotedDates(
        string ArchiveId, string ContentsSha256, bool OnThisDayEnabled, IReadOnlyList<NotedDateListing> Dates, DateOnly? PostedThrough = null)
    {
        /// <summary>Its yearly memories (CH-53, version 3), each with whether it notifies (a muted one does not).</summary>
        public IReadOnlyList<YearlyMemoryListing> Yearly { get; init; } = [];

        /// <summary>The dates that can notify: none when On this day is off, otherwise every date not muted.</summary>
        public IEnumerable<NotedDateListing> Notifying => OnThisDayEnabled ? Dates.Where(date => date.Notify) : [];

        /// <summary>The yearly memories that can notify: none when On this day is off, otherwise every one not muted.</summary>
        public IEnumerable<YearlyMemoryListing> NotifyingYearly => OnThisDayEnabled ? Yearly.Where(memory => memory.Notify) : [];

        /// <summary>Whether anything of this archive can notify on some day.</summary>
        public bool CanNotify => Notifying.Any() || NotifyingYearly.Any();

        /// <summary>
        /// How many of its noted dates and yearly memories notify on <paramref name="day"/>: a yearly memory only on
        /// or after its own date (<see cref="YearlyMemories.ShowsOn"/>).
        /// </summary>
        public int CountOn(DateOnly day) =>
            Notifying.Count(date => NotedDates.Matches(date.Month, date.Day, day))
            + NotifyingYearly.Count(memory => YearlyMemories.ShowsOn(memory.Start, day));
    }

    /// <summary>
    /// The phone owner's own yearly memories (CH-54) as the notification schedule sees them: counted once per phone,
    /// whatever archives are installed, in their own daily notification.
    /// </summary>
    /// <param name="RemindersOn">The My memories reminders switch (per phone); off, they never notify.</param>
    /// <param name="Yearly">The own yearly memories, each with whether it notifies (a muted one does not).</param>
    /// <param name="PostedThrough">The last day the own notification was posted, if any: it is not posted twice that day.</param>
    /// <param name="TapArchiveId">What a tap opens: the last-opened installed archive's folder name, or none (the start screen).</param>
    public sealed record OwnMemoryReminders(
        bool RemindersOn, IReadOnlyList<OwnYearlyMemoryListing> Yearly, DateOnly? PostedThrough = null, string? TapArchiveId = null)
    {
        /// <summary>The own yearly memories that can notify: none with reminders off, otherwise every one not muted.</summary>
        public IEnumerable<OwnYearlyMemoryListing> NotifyingYearly => RemindersOn ? Yearly.Where(memory => memory.Notify) : [];

        /// <summary>Whether an own memory can notify on some day.</summary>
        public bool CanNotify => NotifyingYearly.Any();

        /// <summary>How many own yearly memories notify on <paramref name="day"/>: only on or after their own date.</summary>
        public int CountOn(DateOnly day) => NotifyingYearly.Count(memory => YearlyMemories.ShowsOn(memory.Start, day));
    }

    /// <summary>The one own memories notification of a day (CH-54).</summary>
    /// <param name="Count">How many own yearly memories fall on the day.</param>
    /// <param name="Text">What it says: see <see cref="NotedDateSchedule.TextForOwn"/>; never a title.</param>
    /// <param name="TapArchiveId">The archive a tap opens at today's On this day card, or none: the start screen.</param>
    public sealed record DueOwnNotification(int Count, string Text, string? TapArchiveId);

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
    public sealed record NextNotification(DateOnly Day, DateTime LocalMoment, IReadOnlyList<DueNotification> Due)
    {
        /// <summary>The own memories notification posted at the same moment (CH-54), or none.</summary>
        public DueOwnNotification? Own { get; init; }
    }

    /// <summary>
    /// When noted dates notify (CH-47 step 3), without any Android type: from every installed archive's noted
    /// dates, mutes and On this day switch, and the app's notification time.
    /// </summary>
    /// <remarks>
    /// <para>Only noted dates and yearly memories (CH-53) notify; message years never do. A yearly memory
    /// notifies on its day only on or after its own date and counts like a noted date in the same generic text. One notification per archive per day, at the
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
        /// How many days ahead are searched day by day: every noted date, and every yearly memory already past its
        /// own date, falls at least once a year (29 February on 28 February otherwise), so a year and a day is
        /// enough. A yearly memory dated later than that is found by jumping to its own date (<see cref="Next"/>).
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

                var count = archive.CountOn(day);

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
        /// posted; otherwise the first later day with one, however far: a yearly memory dated years ahead (CH-53)
        /// is scheduled for its own date.
        /// </summary>
        /// <param name="archives">Every installed archive's noted dates.</param>
        /// <param name="time">The app's notification time.</param>
        /// <param name="localNow">The phone's local time now.</param>
        public static NextNotification? Next(IEnumerable<ArchiveNotedDates> archives, TimeOnly time, DateTime localNow) =>
            Next(archives, null, time, localNow);

        /// <summary>
        /// As <see cref="Next(IEnumerable{ArchiveNotedDates}, TimeOnly, DateTime)"/>, with the phone owner's own yearly
        /// memories (CH-54) too: the next moment is the earlier of the archives' and the own one, and a moment that is
        /// both carries both. A due, unposted own day after the time is scheduled <see cref="CatchUpDelay"/> from now,
        /// as an archive's is.
        /// </summary>
        /// <param name="archives">Every installed archive's noted dates.</param>
        /// <param name="own">The own yearly memories, or none.</param>
        /// <param name="time">The app's notification time.</param>
        /// <param name="localNow">The phone's local time now.</param>
        public static NextNotification? Next(IEnumerable<ArchiveNotedDates> archives, OwnMemoryReminders? own, TimeOnly time, DateTime localNow)
        {
            ArgumentNullException.ThrowIfNull(archives);

            var list = archives.ToList();

            if (!list.Any(archive => archive.CanNotify) && own?.CanNotify != true)
            {
                return null;
            }

            var now = DateTime.SpecifyKind(localNow, DateTimeKind.Unspecified);
            var day = DateOnly.FromDateTime(now);

            if (day.ToDateTime(time) <= now)
            {
                var late = DueOn(list, day);
                var lateOwn = OwnDueOn(own, day);

                if (late.Count > 0 || lateOwn is not null)
                {
                    return new NextNotification(day, now + CatchUpDelay, late) { Own = lateOwn };
                }

                day = day.AddDays(1);
            }

            while (true)
            {
                for (var i = 0; i < DaysAhead && day < DateOnly.MaxValue; i++, day = day.AddDays(1))
                {
                    var due = DueOn(list, day);
                    var dueOwn = OwnDueOn(own, day);

                    if (due.Count > 0 || dueOwn is not null)
                    {
                        return new NextNotification(day, day.ToDateTime(time), due) { Own = dueOwn };
                    }
                }

                // Nothing within a year and a day: only a yearly memory dated later still can notify (CH-53), first
                // on its own date. Jump there and search on; every jump moves forward, so this ends.
                if (day >= DateOnly.MaxValue
                    || list.SelectMany(archive => archive.NotifyingYearly)
                        .Select(memory => memory.Start)
                        .Concat((own?.NotifyingYearly ?? []).Select(memory => memory.Start))
                        .Select(start => (DateOnly?)start)
                        .Where(start => start >= day)
                        .Min() is not { } later)
                {
                    return null;
                }

                day = later;
            }
        }

        /// <summary>
        /// What the own memories notification (CH-54) says for <paramref name="count"/>: "A memory today" or
        /// "N memories today". Never a title.
        /// </summary>
        public static string TextForOwn(int count) =>
            count == 1 ? "A memory today" : $"{count.ToString(CultureInfo.CurrentCulture)} memories today";

        /// <summary>
        /// The own memories notification for <paramref name="day"/>, or none: none when nothing of <paramref name="own"/>
        /// falls on it or it was already posted that day.
        /// </summary>
        public static DueOwnNotification? OwnDueOn(OwnMemoryReminders? own, DateOnly day)
        {
            if (own is null || own.PostedThrough == day)
            {
                return null;
            }

            var count = own.CountOn(day);

            return count > 0 ? new DueOwnNotification(count, TextForOwn(count), own.TapArchiveId) : null;
        }

        /// <summary>
        /// What a tap on the own memories notification opens (CH-54): the installed archive opened last, or none (the
        /// start screen) when no archive is installed or none was ever opened. A tie goes to the first folder name.
        /// </summary>
        /// <param name="archives">Each installed archive's folder name and when it was last opened, if ever.</param>
        public static string? OwnTapTarget(IEnumerable<(string ArchiveId, DateTime? LastOpenedUtc)> archives)
        {
            ArgumentNullException.ThrowIfNull(archives);

            return archives
                .Where(archive => archive.LastOpenedUtc is not null)
                .OrderByDescending(archive => archive.LastOpenedUtc)
                .ThenBy(archive => archive.ArchiveId, StringComparer.Ordinal)
                .Select(archive => archive.ArchiveId)
                .FirstOrDefault();
        }

        /// <summary>
        /// The own yearly memories from <paramref name="store"/> with the reminders switch, mutes and posted day kept
        /// there, and the tap target among <paramref name="archives"/> (from <see cref="Read"/>, so only archives that can
        /// be opened). None when the store cannot be read (nothing then notifies for it). The store's busy timeout lets
        /// this read wait for a write the UI is committing. Call it off the UI thread.
        /// </summary>
        public static OwnMemoryReminders? ReadOwn(OwnMemoryStore store, IEnumerable<ArchiveNotedDates> archives, ArchiveSettings settings)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(archives);
            ArgumentNullException.ThrowIfNull(settings);

            var tap = OwnTapTarget(archives.Select(archive => (archive.ArchiveId, settings.GetLastOpenedUtc(archive.ContentsSha256))));

            try
            {
                return new OwnMemoryReminders(
                    store.Settings.RemindersOn,
                    OwnMemoryIndex.YearlyListings(store),
                    store.Settings.PostedDay,
                    tap);
            }
            catch (Exception exception) when (exception is OwnMemoriesStoreException or SqliteException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>The own memories notification's number: one per phone, the same on every run, never an archive's.</summary>
        public static int OwnNotificationId { get; } = NotificationId("own-memories");

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

                    // Yearly memories (CH-53) through the cheap reader method, never a whole memory index.
                    archives.Add(new ArchiveNotedDates(
                        Path.GetFileName(folder),
                        hash,
                        settings.GetOnThisDayEnabled(hash),
                        NotedDates.List(settings, hash, reader.ReadNotedDates()),
                        settings.GetNotedDatesPostedThrough(hash))
                    {
                        Yearly = YearlyMemories.List(settings, hash, YearlyMemories.From(reader.ReadYearlyEntries())),
                    });
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

        /// <summary>Counts for the log with the own memories (CH-54): <c>own=N ownNotifying=N</c> added; never a title, a name or a date.</summary>
        public static string DescribeCounts(IReadOnlyCollection<ArchiveNotedDates> archives, OwnMemoryReminders? own, NextNotification? next) =>
            DescribeCounts(archives, next) + string.Create(
                CultureInfo.InvariantCulture,
                $" own={own?.Yearly.Count ?? 0} ownNotifying={own?.NotifyingYearly.Count() ?? 0}");

        /// <summary>Counts for the log: never a title, a name or a date.</summary>
        public static string DescribeCounts(IReadOnlyCollection<ArchiveNotedDates> archives, NextNotification? next) =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"archives={archives.Count} notedDates={archives.Sum(archive => archive.Dates.Count)} yearly={archives.Sum(archive => archive.Yearly.Count)} notifying={archives.Sum(archive => archive.Notifying.Count() + archive.NotifyingYearly.Count())} scheduled={(next is null ? "no" : "yes")}");
    }
}
