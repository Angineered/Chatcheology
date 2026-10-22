using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Import;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// What the Android screens take from the library: pages prepared off the UI thread, the date
    /// picker's calendar, the import progress line, About's validation line and the "me" choices.
    /// </summary>
    public sealed class ScreenSupportTests : IDisposable
    {
        private const int Count = 700;

        private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

        private readonly TemporaryFolder _root = new();

        public void Dispose() => _root.Dispose();

        // ---------------------------------------------------------------------------------------
        // Pages prepared on a worker thread, inserted on the UI thread.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData(250)]
        [InlineData(400)]
        public void Prepared_pages_both_ways_give_the_rows_of_the_whole_conversation(int anchor)
        {
            // Joins inside a day and at a day start, both ways.
            TestArchives.Write(_root.Path, Count, s => TestArchives.OnDay(s <= 240 ? 1 : s <= 260 ? 2 : s <= 410 ? 3 : 4, s));

            using var reader = ArchiveReader.Open(_root.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);
            timeline.OpenAt(anchor);

            while (timeline.PrepareEarlier() is { } earlier)
            {
                timeline.Prepend(earlier);
            }

            while (timeline.PrepareLater() is { } later)
            {
                timeline.Append(later);
            }

            var pages = reader.ReadAllPages().ToList();
            var whole = timeline.BuildRows(
                pages.SelectMany(page => page.Messages).ToList(),
                pages.SelectMany(page => page.Media).DistinctBy(pair => pair.Key).ToDictionary(),
                previousDay: null);

            Assert.Equal(TestArchives.Describe(whole), TestArchives.Describe(timeline.Rows));
        }

        [Fact]
        public void Preparing_a_page_leaves_the_loaded_rows_as_they_were()
        {
            TestArchives.Write(_root.Path, Count, s => TestArchives.OnDay(s <= 600 ? 1 : 2, s));

            using var reader = ArchiveReader.Open(_root.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);
            timeline.OpenAt(300);

            var before = TestArchives.Describe(timeline.Rows);
            var changes = 0;
            timeline.Rows.CollectionChanged += (_, _) => changes++;

            var earlier = timeline.PrepareEarlier();
            var later = timeline.PrepareLater();

            Assert.NotNull(earlier);
            Assert.NotNull(later);
            Assert.Equal(0, changes);
            Assert.Equal(before, TestArchives.Describe(timeline.Rows));
            Assert.True(timeline.HasEarlier);
            Assert.True(timeline.HasLater);

            // Applied on the list's thread: the earlier page ends on the first loaded day, so its old
            // separator goes (one change) and the page goes in (one insert); the later page is one insert.
            timeline.Prepend(earlier);
            timeline.Append(later);
            Assert.Equal(3, changes);
        }

        // ---------------------------------------------------------------------------------------
        // The date picker's calendar.
        // ---------------------------------------------------------------------------------------

        private static DayCalendar Calendar(params DateOnly[] days) => new(days, DayOfWeek.Monday, English);

        [Fact]
        public void Only_days_with_messages_can_be_picked_and_only_months_with_messages_are_shown()
        {
            var calendar = Calendar(new(2025, 1, 6), new(2025, 1, 8), new(2025, 3, 31), new(2025, 1, 6));

            Assert.Equal(2, calendar.MonthCount);

            var january = calendar.Month(0);
            Assert.Equal("January 2025", january.Title);
            Assert.Equal(["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"], january.WeekdayNames);

            // 1 January 2025 is a Wednesday: two blanks before it in a week starting on Monday.
            Assert.Equal(2 + 31, january.Cells.Count);
            Assert.All(january.Cells.Take(2), cell => Assert.Null(cell.Day));
            Assert.Equal("1", january.Cells[2].Text);
            Assert.Equal([new DateOnly(2025, 1, 6), new DateOnly(2025, 1, 8)], january.Cells.Where(c => c.HasMessages).Select(c => c.Day!.Value));

            var march = calendar.Month(1);
            Assert.Equal(3, march.Month);
            Assert.Equal(new DateOnly(2025, 3, 31), Assert.Single(march.Cells, c => c.HasMessages).Day);
        }

        [Fact]
        public void The_picker_opens_at_the_month_of_a_day_or_the_nearest_month_with_messages()
        {
            var calendar = Calendar(new(2024, 11, 2), new(2025, 1, 6), new(2025, 6, 1));

            Assert.Equal(1, calendar.IndexOfMonthNear(new DateOnly(2025, 1, 20)));
            Assert.Equal(0, calendar.IndexOfMonthNear(new DateOnly(2020, 5, 1)));
            Assert.Equal(2, calendar.IndexOfMonthNear(new DateOnly(2030, 5, 1)));

            // March is two months from January and three from June.
            Assert.Equal(1, calendar.IndexOfMonthNear(new DateOnly(2025, 3, 15)));

            Assert.Equal(-1, Calendar().IndexOfMonthNear(new DateOnly(2025, 1, 1)));
            Assert.True(Calendar().IsEmpty);
        }

        [Fact]
        public void A_year_step_goes_to_the_nearest_month_a_year_away_with_messages()
        {
            var calendar = Calendar(new(2023, 2, 1), new(2024, 1, 1), new(2024, 9, 1), new(2025, 1, 1), new(2025, 2, 1));

            var january2025 = 3;
            Assert.Equal(1, calendar.YearStep(january2025, -1));
            Assert.Equal(-1, calendar.YearStep(january2025, +1));

            // From January 2024 one year back is February 2023, the nearest earlier month.
            Assert.Equal(0, calendar.YearStep(1, -1));

            // From September 2024 one year on is February 2025 (the nearest later month to September 2025).
            Assert.Equal(4, calendar.YearStep(2, +1));
            Assert.Equal(-1, calendar.YearStep(0, -1));
        }

        [Fact]
        public void The_calendar_of_a_real_archive_has_its_days()
        {
            TestArchives.Write(_root.Path, 30, s => TestArchives.OnDay(s <= 10 ? 1 : s <= 20 ? 3 : 40, s));

            using var reader = ArchiveReader.Open(_root.Path);
            var calendar = new DayCalendar(reader.ReadDays(), DayOfWeek.Monday, English);

            Assert.True(calendar.HasMessages(new DateOnly(2025, 3, 1)));
            Assert.False(calendar.HasMessages(new DateOnly(2025, 3, 2)));
            Assert.True(calendar.HasMessages(new DateOnly(2025, 3, 3)));
            Assert.True(calendar.HasMessages(new DateOnly(2025, 4, 9)));
            Assert.Equal(2, calendar.MonthCount);
        }

        // ---------------------------------------------------------------------------------------
        // Import progress, About, "me".
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void The_progress_line_gives_sizes_and_file_counts_only()
        {
            var copying = new ImportProgress(ImportStage.Copying, 3 * 1024 * 1024, 12 * 1024 * 1024, 40, 160);

            Assert.Equal(0.25, copying.Fraction, 3);
            Assert.Equal(
                $"{ImportResult.Bytes(3 * 1024 * 1024)} of {ImportResult.Bytes(12 * 1024 * 1024)} · 40 of 160 files",
                copying.Text);

            var checking = new ImportProgress(ImportStage.Checking, 10, 10, 5, 5);
            Assert.Equal("Checking…", checking.Text);
            Assert.Equal(1, checking.Fraction);

            Assert.Equal(0, new ImportProgress(ImportStage.Copying, 0, 0, 0, 0).Fraction);
        }

        [Fact]
        public void About_describes_the_last_validation_or_says_it_is_not_recorded()
        {
            Assert.Equal("Not recorded", ArchiveSettings.DescribeValidation(null));

            var utc = new DateTime(2026, 10, 22, 7, 30, 0, DateTimeKind.Utc);
            var when = utc.ToLocalTime().ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture);

            Assert.Equal($"Passed: 6 of 6 checks, {when}", ArchiveSettings.DescribeValidation((utc, 6, 6)));
            Assert.Equal($"Did not pass: 5 of 6 checks, {when}", ArchiveSettings.DescribeValidation((utc, 5, 6)));
        }

        [Fact]
        public void An_opened_archive_offers_its_participants_as_me_and_reports_format_and_validation()
        {
            var library = new ArchiveLibrary(_root.Path);
            var settings = new ArchiveSettings(new MemorySettings());
            var staging = library.CreateStagingFolder();
            var builder = new ArchiveBuilder();
            builder.Add("Hello");
            builder.Write(staging);
            Assert.True(library.Install(staging).Installed);

            var archive = Assert.Single(library.ListArchives(settings));
            settings.SetValidation(archive.ContentsSha256!, new DateTime(2026, 10, 22, 7, 30, 0, DateTimeKind.Utc), 6, 6);

            using var session = ArchiveSession.Open(archive, settings);

            Assert.Equal(
                [new MeChoice(ArchiveBuilder.Local, "Alex (the archive's choice)"), new MeChoice(ArchiveBuilder.Other, "Sam")],
                session.MeChoices);
            Assert.Equal(ArchiveFormat.FormatVersion, session.FormatVersion);
            Assert.StartsWith("Passed: 6 of 6 checks", session.ValidationText, StringComparison.Ordinal);

            session.SetMe(ArchiveBuilder.Other);
            Assert.Equal("Alex (the archive's choice)", session.MeChoices[0].Label);
            Assert.Equal(ArchiveBuilder.Other, session.Me);
        }
    }
}
