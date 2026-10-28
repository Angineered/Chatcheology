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
            new(id, "hash-" + id, "Conversation " + id, OnThisDayEnabled: true, dates);

        [Fact]
        public void Before_the_time_today_is_next_and_at_or_after_it_the_next_year_is()
        {
            ArchiveNotedDates[] archives = [Archive("a", Date("1", "Invented day", 6, 1, 2020))];

            var before = NotedDateSchedule.Next(archives, Nine, At(2026, 6, 1, 8, 59, 59))!;
            Assert.Equal(new DateOnly(2026, 6, 1), before.Day);
            Assert.Equal(At(2026, 6, 1, 9, 0), before.LocalMoment);
            Assert.Equal("Invented day · 6 years", before.Due.Single().Text);

            // Exactly at the time it has been posted (or is being); after it, next year.
            Assert.Equal(new DateOnly(2027, 6, 1), NotedDateSchedule.Next(archives, Nine, At(2026, 6, 1, 9, 0))!.Day);
            Assert.Equal(new DateOnly(2027, 6, 1), NotedDateSchedule.Next(archives, Nine, At(2026, 6, 1, 15, 0))!.Day);
            Assert.Equal("Invented day · 7 years", NotedDateSchedule.Next(archives, Nine, At(2026, 6, 1, 15, 0))!.Due.Single().Text);
        }

        [Fact]
        public void Across_midnight_and_the_year_end()
        {
            ArchiveNotedDates[] archives = [Archive("a", Date("1", "New year", 1, 1), Date("2", "Second", 1, 2))];

            // Late on 31 December: 1 January of the new year.
            var next = NotedDateSchedule.Next(archives, Nine, At(2026, 12, 31, 23, 59))!;
            Assert.Equal(At(2027, 1, 1, 9, 0), next.LocalMoment);
            Assert.Equal("New year", next.Due.Single().Text);

            // Just after midnight with a time of 00:05: the same day.
            Assert.Equal(At(2027, 1, 1, 0, 5), NotedDateSchedule.Next(archives, new TimeOnly(0, 5), At(2027, 1, 1, 0, 0, 1))!.LocalMoment);

            // After 1 January's time: 2 January.
            Assert.Equal(new DateOnly(2027, 1, 2), NotedDateSchedule.Next(archives, Nine, At(2027, 1, 1, 10, 0))!.Day);

            // After 2 January's: a year on.
            Assert.Equal(new DateOnly(2028, 1, 1), NotedDateSchedule.Next(archives, Nine, At(2027, 1, 2, 9, 30))!.Day);
        }

        [Fact]
        public void Twenty_ninth_of_February_notifies_on_the_28th_in_a_year_without_one()
        {
            ArchiveNotedDates[] archives = [Archive("a", Date("1", "Leap day", 2, 29, 2020))];

            var common = NotedDateSchedule.Next(archives, Nine, At(2026, 1, 10))!;
            Assert.Equal(new DateOnly(2026, 2, 28), common.Day);
            Assert.Equal("Leap day · 6 years", common.Due.Single().Text);

            // In a leap year, 29 February itself and not the 28th.
            Assert.Equal(new DateOnly(2028, 2, 29), NotedDateSchedule.Next(archives, Nine, At(2028, 1, 10))!.Day);
            Assert.Empty(NotedDateSchedule.DueOn(archives, new DateOnly(2028, 2, 28)));

            // After the 28th of a common year, the next is the following year's.
            Assert.Equal(new DateOnly(2027, 2, 28), NotedDateSchedule.Next(archives, Nine, At(2026, 2, 28, 10, 0))!.Day);

            // A 28 February date stays on the 28th in a leap year.
            ArchiveNotedDates[] twentyEighth = [Archive("a", Date("1", "Plain", 2, 28))];
            Assert.Equal(new DateOnly(2028, 2, 28), NotedDateSchedule.Next(twentyEighth, Nine, At(2028, 2, 1))!.Day);
        }

        [Fact]
        public void Changing_the_time_moves_the_moment_on_the_same_day_or_the_next()
        {
            ArchiveNotedDates[] archives = [Archive("a", Date("1", "Daily test", 3, 10)), Archive("b", Date("2", "Other", 3, 11))];
            var now = At(2026, 3, 10, 9, 30);

            Assert.Equal(At(2026, 3, 11, 9, 0), NotedDateSchedule.Next(archives, Nine, now)!.LocalMoment);
            Assert.Equal(At(2026, 3, 10, 9, 32), NotedDateSchedule.Next(archives, new TimeOnly(9, 32), now)!.LocalMoment);
            Assert.Equal(At(2026, 3, 10, 21, 0), NotedDateSchedule.Next(archives, new TimeOnly(21, 0), now)!.LocalMoment);
            Assert.Equal(At(2026, 3, 11, 7, 0), NotedDateSchedule.Next(archives, new TimeOnly(7, 0), now)!.LocalMoment);
        }

        [Fact]
        public void A_day_already_posted_is_not_scheduled_again_when_the_time_moves_later()
        {
            ArchiveNotedDates[] archives = [Archive("a", Date("1", "Invented day", 6, 1))];
            var now = At(2026, 6, 1, 9, 30);
            var later = new TimeOnly(10, 0);

            Assert.Equal(new DateOnly(2026, 6, 1), NotedDateSchedule.Next(archives, later, now)!.Day);
            Assert.Equal(new DateOnly(2027, 6, 1), NotedDateSchedule.Next(archives, later, now, postedThrough: new DateOnly(2026, 6, 1))!.Day);

            // An earlier day posted changes nothing; a recorded day after today (the clock was set back) is ignored.
            Assert.Equal(new DateOnly(2026, 6, 1), NotedDateSchedule.Next(archives, later, now, postedThrough: new DateOnly(2026, 5, 31))!.Day);
            Assert.Equal(new DateOnly(2026, 6, 1), NotedDateSchedule.Next(archives, later, now, postedThrough: new DateOnly(2026, 6, 2))!.Day);

            var store = new MemorySettings();
            var settings = new NotificationSettings(store);
            Assert.Null(settings.PostedThrough);
            settings.PostedThrough = new DateOnly(2026, 6, 1);
            Assert.Equal("2026-06-01", store.Values["app.notedDates.postedThrough"]);
            Assert.Equal(new DateOnly(2026, 6, 1), settings.PostedThrough);
            store.Values["app.notedDates.postedThrough"] = "not a day";
            Assert.Null(settings.PostedThrough);
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
            Assert.Equal("a", next.Due.Single().ArchiveId);

            // Nothing that can notify: no moment at all.
            Assert.Null(NotedDateSchedule.Next([Archive("a", Date("1", "Muted gift", 5, 5, gifted: true, notify: false)), off], Nine, At(2026, 5, 1)));
            Assert.Null(NotedDateSchedule.Next([], Nine, At(2026, 5, 1)));
            Assert.Null(NotedDateSchedule.Next([Archive("empty")], Nine, At(2026, 5, 1)));
        }

        [Fact]
        public void One_notification_per_archive_with_the_title_or_a_count()
        {
            ArchiveNotedDates[] archives =
            [
                Archive("one", Date("1", "Zeta", 8, 8), Date("2", "Alpha", 8, 8, 2025), Date("3", "Later", 8, 9)),
                Archive("two", Date("4", "Single", 8, 8, 2026)),
                Archive("three", Date("5", "Not today", 8, 10)),
            ];

            var due = NotedDateSchedule.Next(archives, Nine, At(2026, 8, 1))!.Due;

            Assert.Equal(["one", "two"], due.Select(d => d.ArchiveId).ToList());
            Assert.Equal([2, 1], due.Select(d => d.Count).ToList());
            Assert.Equal("2 noted dates today", due[0].Text);

            // A year that is this year counts no years: the title alone.
            Assert.Equal("Single", due[1].Text);
            Assert.Equal("Conversation two", due[1].ConversationTitle);
            Assert.Equal("hash-two", due[1].ContentsSha256);

            // The next day: only the first archive, one date with its year count (none: no year).
            Assert.Equal("Later", NotedDateSchedule.DueOn(archives, new DateOnly(2026, 8, 9)).Single().Text);
            Assert.Equal("Alpha · 1 year", NotedDateSchedule.DueOn([Archive("x", Date("2", "Alpha", 8, 8, 2025))], new DateOnly(2026, 8, 8)).Single().Text);
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
        public void Installed_archives_are_read_with_gifted_and_own_dates_mutes_and_the_switch_and_forget_clears_them()
        {
            var library = new ArchiveLibrary(_root.Path);
            var settings = new ArchiveSettings(new MemorySettings());

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
            settings.SetOwnNotedDates(firstHash, [new OwnNotedDate("own", "Own one", 4, 4, null, null)]);

            var archives = NotedDateSchedule.Read(library, settings);

            Assert.Equal(2, archives.Count);
            var read = archives.Single(a => a.ContentsSha256 == firstHash);
            Assert.Equal(Path.GetFileName(first), read.ArchiveId);
            Assert.Equal(3, read.Dates.Count);
            Assert.Equal(["Gifted one", "Own one"], read.Notifying.Select(d => d.Title).Order().ToList());

            var due = NotedDateSchedule.DueOn(archives, new DateOnly(2026, 4, 4));
            Assert.Equal(2, due.Count);
            Assert.Equal("2 noted dates today", due.Single(d => d.ContentsSha256 == firstHash).Text);
            Assert.Equal("In an empty archive", due.Single(d => d.ContentsSha256 == secondHash).Text);
            Assert.Equal("archives=2 notedDates=4 notifying=3 scheduled=yes", NotedDateSchedule.DescribeCounts(archives, NotedDateSchedule.Next(archives, Nine, At(2026, 4, 1))));

            // On this day off for the empty archive: it is left out.
            settings.SetOnThisDayEnabled(secondHash, false);
            Assert.Single(NotedDateSchedule.DueOn(NotedDateSchedule.Read(library, settings), new DateOnly(2026, 4, 4)));

            // Removed: gone from the schedule, and its own dates and mutes are forgotten.
            library.Remove(library.ListArchives(settings).Single(a => a.ContentsSha256 == firstHash), settings);
            Assert.Empty(settings.GetOwnNotedDates(firstHash));
            Assert.Empty(settings.GetMutedNotedDates(firstHash));
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
