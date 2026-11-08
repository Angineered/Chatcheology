using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Notifications;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// Noted-date notifications (CH-47 step 3): the next moment, its content per archive, exclusions, the
    /// notification time setting and the reading of installed archives. Invented dates only.
    /// </summary>
    public sealed class NotedDateScheduleTests : IDisposable
    {
        private static readonly TimeOnly Nine = new(9, 0);

        private readonly TemporaryFolder _root = new();

        public void Dispose() => _root.Dispose();

        private static DateTime At(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
            new(year, month, day, hour, minute, second, DateTimeKind.Unspecified);

        private static NotedDateListing Date(string id, string title, int month, int day, int? year = null, bool notify = true, bool gifted = false) =>
            new(id, title, month, day, year, null, gifted, notify);

        private static ArchiveNotedDates Archive(string id, params NotedDateListing[] dates) =>
            new(id, "hash-" + id, OnThisDayEnabled: true, dates);

        private static ArchiveNotedDates Posted(ArchiveNotedDates archive, DateOnly day) => archive with { PostedThrough = day };

        [Fact]
        public void Before_the_time_today_is_next_and_once_posted_the_next_year_is()
        {
            var archive = Archive("a", Date("1", "Invented day", 6, 1, 2020));
            var today = new DateOnly(2026, 6, 1);

            var before = NotedDateSchedule.Next([archive], Nine, At(2026, 6, 1, 8, 59, 59))!;
            Assert.Equal(today, before.Day);
            Assert.Equal(At(2026, 6, 1, 9, 0), before.LocalMoment);
            Assert.Equal("A noted date today", before.Due.Single().Text);

            // Posted today: next year, at the time.
            var next = NotedDateSchedule.Next([Posted(archive, today)], Nine, At(2026, 6, 1, 9, 0))!;
            Assert.Equal(new DateOnly(2027, 6, 1), next.Day);
            Assert.Equal(At(2027, 6, 1, 9, 0), next.LocalMoment);
            Assert.Equal(new DateOnly(2027, 6, 1), NotedDateSchedule.Next([Posted(archive, today)], Nine, At(2026, 6, 1, 15, 0))!.Day);
        }

        [Fact]
        public void Late_today_and_not_yet_posted_is_scheduled_at_once()
        {
            var archive = Archive("a", Date("1", "Invented day", 6, 1));
            var today = new DateOnly(2026, 6, 1);

            // A boot at 09:01, a phone switched on at 09:30, an app start while the alarm was deferred: a minute from now.
            foreach (var now in new[] { At(2026, 6, 1, 9, 0), At(2026, 6, 1, 9, 1), At(2026, 6, 1, 9, 30, 15), At(2026, 6, 1, 15, 0) })
            {
                var late = NotedDateSchedule.Next([archive], Nine, now)!;
                Assert.Equal(today, late.Day);
                Assert.Equal(now + NotedDateSchedule.CatchUpDelay, late.LocalMoment);
                Assert.Equal("a", late.Due.Single().InstallId);
            }

            // Posted on an earlier day only: still today.
            Assert.Equal(today, NotedDateSchedule.Next([Posted(archive, new DateOnly(2025, 6, 1))], Nine, At(2026, 6, 1, 9, 30))!.Day);

            // A recorded day after today (the clock was set back) does not silence today.
            Assert.Equal(today, NotedDateSchedule.Next([Posted(archive, new DateOnly(2026, 6, 2))], Nine, At(2026, 6, 1, 9, 30))!.Day);

            // Only the archives not yet posted today are in it.
            var other = Archive("b", Date("2", "Other", 6, 1));
            var mixed = NotedDateSchedule.Next([Posted(archive, today), other], Nine, At(2026, 6, 1, 10, 0))!;
            Assert.Equal(At(2026, 6, 1, 10, 1), mixed.LocalMoment);
            Assert.Equal(["b"], mixed.Due.Select(d => d.InstallId).ToList());

            // Nothing due today: the next day with one, at the time.
            Assert.Equal(At(2027, 6, 1, 9, 0), NotedDateSchedule.Next([Posted(archive, today), Posted(other, today)], Nine, At(2026, 6, 1, 10, 0))!.LocalMoment);
        }

        [Fact]
        public void Across_midnight_and_the_year_end()
        {
            var archive = Archive("a", Date("1", "New year", 1, 1), Date("2", "Second", 1, 2));
            ArchiveNotedDates[] archives = [archive];

            // Late on 31 December: 1 January of the new year.
            var next = NotedDateSchedule.Next(archives, Nine, At(2026, 12, 31, 23, 59))!;
            Assert.Equal(At(2027, 1, 1, 9, 0), next.LocalMoment);
            Assert.Equal("A noted date today", next.Due.Single().Text);

            // Just after midnight with a time of 00:05: the same day.
            Assert.Equal(At(2027, 1, 1, 0, 5), NotedDateSchedule.Next(archives, new TimeOnly(0, 5), At(2027, 1, 1, 0, 0, 1))!.LocalMoment);

            // After 1 January's time, posted: 2 January.
            Assert.Equal(At(2027, 1, 2, 9, 0), NotedDateSchedule.Next([Posted(archive, new DateOnly(2027, 1, 1))], Nine, At(2027, 1, 1, 10, 0))!.LocalMoment);

            // After 2 January's, posted: a year on.
            Assert.Equal(new DateOnly(2028, 1, 1), NotedDateSchedule.Next([Posted(archive, new DateOnly(2027, 1, 2))], Nine, At(2027, 1, 2, 9, 30))!.Day);
        }

        [Fact]
        public void Twenty_ninth_of_February_notifies_on_the_28th_in_a_year_without_one()
        {
            var archive = Archive("a", Date("1", "Leap day", 2, 29, 2020));
            ArchiveNotedDates[] archives = [archive];

            var common = NotedDateSchedule.Next(archives, Nine, At(2026, 1, 10))!;
            Assert.Equal(new DateOnly(2026, 2, 28), common.Day);
            Assert.Equal("A noted date today", common.Due.Single().Text);

            // In a leap year, 29 February itself and not the 28th.
            Assert.Equal(new DateOnly(2028, 2, 29), NotedDateSchedule.Next(archives, Nine, At(2028, 1, 10))!.Day);
            Assert.Empty(NotedDateSchedule.DueOn(archives, new DateOnly(2028, 2, 28)));

            // After the 28th of a common year, posted, the next is the following year's.
            Assert.Equal(new DateOnly(2027, 2, 28), NotedDateSchedule.Next([Posted(archive, new DateOnly(2026, 2, 28))], Nine, At(2026, 2, 28, 10, 0))!.Day);

            // A 28 February date stays on the 28th in a leap year.
            ArchiveNotedDates[] twentyEighth = [Archive("a", Date("1", "Plain", 2, 28))];
            Assert.Equal(new DateOnly(2028, 2, 28), NotedDateSchedule.Next(twentyEighth, Nine, At(2028, 2, 1))!.Day);
        }

        [Fact]
        public void Changing_the_time_moves_the_moment_on_the_same_day_or_the_next()
        {
            var first = Archive("a", Date("1", "Daily test", 3, 10));
            var second = Archive("b", Date("2", "Other", 3, 11));
            var now = At(2026, 3, 10, 9, 30);
            ArchiveNotedDates[] posted = [Posted(first, new DateOnly(2026, 3, 10)), second];
            ArchiveNotedDates[] notPosted = [first, second];

            // Today already posted: the next day, whatever the time.
            Assert.Equal(At(2026, 3, 11, 9, 0), NotedDateSchedule.Next(posted, Nine, now)!.LocalMoment);
            Assert.Equal(At(2026, 3, 11, 21, 0), NotedDateSchedule.Next(posted, new TimeOnly(21, 0), now)!.LocalMoment);
            Assert.Equal(At(2026, 3, 11, 7, 0), NotedDateSchedule.Next(posted, new TimeOnly(7, 0), now)!.LocalMoment);

            // Not yet posted: a later time today is today at that time; an earlier one is at once.
            Assert.Equal(At(2026, 3, 10, 9, 32), NotedDateSchedule.Next(notPosted, new TimeOnly(9, 32), now)!.LocalMoment);
            Assert.Equal(At(2026, 3, 10, 21, 0), NotedDateSchedule.Next(notPosted, new TimeOnly(21, 0), now)!.LocalMoment);
            Assert.Equal(At(2026, 3, 10, 9, 31), NotedDateSchedule.Next(notPosted, new TimeOnly(7, 0), now)!.LocalMoment);
        }

        [Fact]
        public void The_posted_day_is_per_archive_and_forgotten_with_the_archive()
        {
            var store = new MemorySettings();
            var settings = new ArchiveSettings(store);

            Assert.Null(settings.GetNotedDatesPostedThrough("one"));
            settings.SetNotedDatesPostedThrough("one", new DateOnly(2026, 6, 1));
            Assert.Equal("2026-06-01", store.Values["archive.one.notedDatesPosted"]);
            Assert.Equal(new DateOnly(2026, 6, 1), settings.GetNotedDatesPostedThrough("one"));

            // Another archive is not affected: one notification per archive per day.
            Assert.Null(settings.GetNotedDatesPostedThrough("two"));
            ArchiveNotedDates[] archives =
            [
                Archive("one", Date("1", "First", 6, 1)) with { PostedThrough = settings.GetNotedDatesPostedThrough("one") },
                Archive("two", Date("2", "Second", 6, 1)) with { PostedThrough = settings.GetNotedDatesPostedThrough("two") },
            ];
            Assert.Equal(["two"], NotedDateSchedule.DueOn(archives, new DateOnly(2026, 6, 1)).Select(d => d.InstallId).ToList());
            Assert.Equal(["one", "two"], NotedDateSchedule.DueOn(archives, new DateOnly(2027, 6, 1)).Select(d => d.InstallId).ToList());

            store.Values["archive.one.notedDatesPosted"] = "not a day";
            Assert.Null(settings.GetNotedDatesPostedThrough("one"));

            settings.SetNotedDatesPostedThrough("one", new DateOnly(2026, 6, 1));
            settings.Forget("one");
            Assert.Null(settings.GetNotedDatesPostedThrough("one"));

            // The per-app settings do not keep one.
            Assert.DoesNotContain(store.Values.Keys, key => key.StartsWith("app.", StringComparison.Ordinal));
        }

        [Fact]
        public void Muted_gifted_dates_and_archives_with_On_this_day_off_are_left_out()
        {
            var muted = Archive("a", Date("1", "Muted gift", 5, 5, gifted: true, notify: false), Date("2", "Own", 5, 6));
            var off = Archive("b", Date("3", "Off archive", 5, 5)) with { OnThisDayEnabled = false };
            ArchiveNotedDates[] archives = [muted, off];

            Assert.Empty(NotedDateSchedule.DueOn(archives, new DateOnly(2026, 5, 5)));

            var next = NotedDateSchedule.Next(archives, Nine, At(2026, 5, 1))!;
            Assert.Equal(new DateOnly(2026, 5, 6), next.Day);
            Assert.Equal("a", next.Due.Single().InstallId);

            // Late on 5 May: nothing due today, so no catch-up; 6 May at the time.
            Assert.Equal(At(2026, 5, 6, 9, 0), NotedDateSchedule.Next(archives, Nine, At(2026, 5, 5, 12, 0))!.LocalMoment);

            // Nothing that can notify: no moment at all.
            Assert.Null(NotedDateSchedule.Next([Archive("a", Date("1", "Muted gift", 5, 5, gifted: true, notify: false)), off], Nine, At(2026, 5, 1)));
            Assert.Null(NotedDateSchedule.Next([], Nine, At(2026, 5, 1)));
            Assert.Null(NotedDateSchedule.Next([Archive("empty")], Nine, At(2026, 5, 1)));
        }

        [Fact]
        public void One_notification_per_archive_saying_a_noted_date_or_a_count_never_a_title()
        {
            ArchiveNotedDates[] archives =
            [
                Archive("one", Date("1", "Zeta", 8, 8), Date("2", "Alpha", 8, 8, 2025), Date("3", "Later", 8, 9)),
                Archive("two", Date("4", "Single", 8, 8, 2026)),
                Archive("three", Date("5", "Not today", 8, 10)),
            ];

            var due = NotedDateSchedule.Next(archives, Nine, At(2026, 8, 1))!.Due;

            Assert.Equal(["one", "two"], due.Select(d => d.InstallId).ToList());
            Assert.Equal([2, 1], due.Select(d => d.Count).ToList());
            Assert.Equal(["2 noted dates today", "A noted date today"], due.Select(d => d.Text).ToList());
            Assert.Equal("hash-two", due[1].ContentsSha256);

            // No title, year count or name ever reaches the text.
            foreach (var title in new[] { "Zeta", "Alpha", "Single", "year", "Conversation" })
            {
                Assert.DoesNotContain(due, d => d.Text.Contains(title, StringComparison.OrdinalIgnoreCase));
            }

            Assert.Equal("A noted date today", NotedDateSchedule.DueOn(archives, new DateOnly(2026, 8, 9)).Single().Text);
            Assert.Equal("3 noted dates today", NotedDateSchedule.TextFor(3));
        }

        [Fact]
        public void Local_times_become_instants_in_the_zone_including_across_a_clock_change()
        {
            var fixedZone = TimeZoneInfo.CreateCustomTimeZone("Invented+2", TimeSpan.FromHours(2), "Invented+2", "Invented+2");
            Assert.Equal(new DateTime(2026, 6, 1, 7, 0, 0, DateTimeKind.Utc), NotedDateSchedule.ToUtc(At(2026, 6, 1, 9, 0), fixedZone));
            Assert.Equal(DateTimeKind.Utc, NotedDateSchedule.ToUtc(At(2026, 6, 1, 9, 0), fixedZone).Kind);

            // An invented zone, +1 in winter and +2 in summer, the clocks moving at 02:00 on the last Sundays of
            // March and October.
            var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2000, 1, 1),
                new DateTime(2099, 12, 31),
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday));
            var zone = TimeZoneInfo.CreateCustomTimeZone("Invented", TimeSpan.FromHours(1), "Invented", "Invented", "Invented summer", [rule]);

            // The day before and after a change: 09:00 local each day, whatever the offset.
            Assert.Equal(new DateTime(2026, 3, 28, 8, 0, 0, DateTimeKind.Utc), NotedDateSchedule.ToUtc(At(2026, 3, 28, 9, 0), zone));
            Assert.Equal(new DateTime(2026, 3, 29, 7, 0, 0, DateTimeKind.Utc), NotedDateSchedule.ToUtc(At(2026, 3, 29, 9, 0), zone));

            // 02:30 does not exist on 29 March 2026: the first minute after the gap (03:00 summer time).
            Assert.Equal(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc), NotedDateSchedule.ToUtc(At(2026, 3, 29, 2, 30), zone));

            // 02:30 happens twice on 25 October 2026: the first (summer time).
            Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), NotedDateSchedule.ToUtc(At(2026, 10, 25, 2, 30), zone));
        }

        [Fact]
        public void The_notification_time_defaults_to_nine_and_a_damaged_value_reads_as_nine()
        {
            var store = new MemorySettings();
            var settings = new NotificationSettings(store);

            Assert.Equal(new TimeOnly(9, 0), settings.Time);
            Assert.False(settings.PermissionAsked);

            settings.Time = new TimeOnly(7, 45);
            Assert.Equal(new TimeOnly(7, 45), settings.Time);
            Assert.Equal("07:45", store.Values["app.notedDates.time"]);

            settings.Time = new TimeOnly(9, 0);
            Assert.False(store.Values.ContainsKey("app.notedDates.time"));

            store.Values["app.notedDates.time"] = "25:99";
            Assert.Equal(new TimeOnly(9, 0), settings.Time);

            settings.PermissionAsked = true;
            Assert.True(settings.PermissionAsked);

            // Per app: removing an archive does not touch it.
            store.Values["app.notedDates.time"] = "06:30";
            new ArchiveSettings(store).Forget("hash");
            Assert.Equal(new TimeOnly(6, 30), settings.Time);
            Assert.True(settings.PermissionAsked);
        }

        [Fact]
        public void Notification_ids_are_stable_per_archive_and_not_zero()
        {
            Assert.Equal(NotedDateSchedule.NotificationId("0123456789abcdef0123456789abcdef"), NotedDateSchedule.NotificationId("0123456789abcdef0123456789abcdef"));
            Assert.NotEqual(NotedDateSchedule.NotificationId("aaaa"), NotedDateSchedule.NotificationId("aaab"));
            Assert.True(NotedDateSchedule.NotificationId("aaaa") > 0);

            // FNV-1a of "a" (0xE40C292C), kept positive.
            Assert.Equal(0x640C292C, NotedDateSchedule.NotificationId("a"));
        }

        [Fact]
        public void Installed_archives_are_read_with_gifted_dates_mutes_and_the_switch_and_forget_clears_them()
        {
            var library = new ArchiveLibrary(_root.Path);
            var store = new MemorySettings();
            var settings = new ArchiveSettings(store);

            var withMessages = new ArchiveBuilder();
            withMessages.Add("Hello", ArchiveBuilder.Other, new DateTime(2020, 4, 4, 10, 0, 0));
            withMessages.NotedDate(1, "Gifted one", 4, 4, 2019);
            withMessages.NotedDate(2, "Gifted muted", 4, 4);

            // No messages: no card, but its noted dates still notify.
            var empty = new ArchiveBuilder();
            empty.NotedDate(3, "In an empty archive", 4, 4);

            var first = Install(library, withMessages);
            var second = Install(library, empty);
            var firstHash = ArchiveLibrary.ContentsSha256Of(first)!;
            var secondHash = ArchiveLibrary.ContentsSha256Of(second)!;

            settings.SetNotedDateMuted(firstHash, 2, true);

            // A leftover own noted date (removed by CH-54 step 2) is never read and never notifies.
            store.Values["archive." + firstHash + ".ownNotedDates"] = "[{\"Id\":\"own\",\"Title\":\"Own one\",\"Month\":4,\"Day\":4}]";

            var archives = NotedDateSchedule.Read(library, settings);

            Assert.Equal(2, archives.Count);
            var read = archives.Single(a => a.ContentsSha256 == firstHash);
            Assert.Equal(Path.GetFileName(first), read.InstallId);
            Assert.Equal(2, read.Dates.Count);
            Assert.Equal(["Gifted one"], read.Notifying.Select(d => d.Title).Order().ToList());

            var due = NotedDateSchedule.DueOn(archives, new DateOnly(2026, 4, 4));
            Assert.Equal(2, due.Count);
            Assert.Equal("A noted date today", due.Single(d => d.ContentsSha256 == firstHash).Text);
            Assert.Equal("A noted date today", due.Single(d => d.ContentsSha256 == secondHash).Text);

            // The day posted is read per archive.
            settings.SetNotedDatesPostedThrough(firstHash, new DateOnly(2026, 4, 4));
            Assert.Equal([secondHash], NotedDateSchedule.DueOn(NotedDateSchedule.Read(library, settings), new DateOnly(2026, 4, 4)).Select(d => d.ContentsSha256).ToList());
            Assert.Equal("archives=2 notedDates=3 yearly=0 notifying=2 scheduled=yes", NotedDateSchedule.DescribeCounts(archives, NotedDateSchedule.Next(archives, Nine, At(2026, 4, 1))));

            // On this day off for the empty archive: it is left out.
            settings.SetOnThisDayEnabled(secondHash, false);
            Assert.Equal([firstHash], NotedDateSchedule.DueOn(NotedDateSchedule.Read(library, settings), new DateOnly(2027, 4, 4)).Select(d => d.ContentsSha256).ToList());

            // Removed: gone from the schedule, and a leftover own dates key and the mutes are forgotten.
            library.Remove(library.ListArchives(settings).Single(a => a.ContentsSha256 == firstHash), settings);
            Assert.False(store.Values.ContainsKey("archive." + firstHash + ".ownNotedDates"));
            Assert.Empty(settings.GetMutedNotedDates(firstHash));
            Assert.Null(settings.GetNotedDatesPostedThrough(firstHash));
            var after = NotedDateSchedule.Read(library, settings);
            Assert.Equal([secondHash], after.Select(a => a.ContentsSha256).ToList());
            Assert.Null(NotedDateSchedule.Next(after, Nine, At(2026, 4, 1)));
        }

        [Fact]
        public void An_archive_that_cannot_be_read_is_left_out()
        {
            var library = new ArchiveLibrary(_root.Path);
            var settings = new ArchiveSettings(new MemorySettings());
            var builder = new ArchiveBuilder();
            builder.NotedDate(1, "Readable", 9, 9);
            Install(library, builder);

            // An installed folder with a contents list but no database.
            var broken = Path.Combine(library.ArchivesFolder, "broken");
            Directory.CreateDirectory(broken);
            File.WriteAllText(Path.Combine(broken, "CONTENTS.sha256"), "synthetic");

            Assert.Equal(["Readable"], NotedDateSchedule.Read(library, settings).SelectMany(a => a.Dates).Select(d => d.Title).ToList());
        }

        private static string Install(ArchiveLibrary library, ArchiveBuilder builder)
        {
            var staging = library.CreateStagingFolder();
            builder.Write(staging);

            var outcome = library.Install(staging);
            Assert.True(outcome.Installed);

            return outcome.InstalledFolder!;
        }
    }
}
