using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Synthetic;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// Noted dates on the phone (CH-47 step 2): gifted and own dates on the On this day card, the year count,
    /// the jump, 29 February, the switch, muting, damaged settings, and Forget. Invented dates only.
    /// </summary>
    public sealed class NotedDatesOnThisDayTests : IDisposable
    {
        private readonly TemporaryFolder _root = new();
        private readonly ArchiveLibrary _library;
        private readonly MemorySettings _store = new();
        private readonly ArchiveSettings _settings;

        public NotedDatesOnThisDayTests()
        {
            _library = new ArchiveLibrary(_root.Path);
            _settings = new ArchiveSettings(_store);
        }

        public void Dispose() => _root.Dispose();

        private static DateOnly D(int year, int month, int day) => new(year, month, day);

        private static ArchiveNotedDate Gifted(long id, string title, int month, int day, int? year = null, string? note = null) =>
            new() { NotedDateID = id, Title = title, Month = month, Day = day, Year = year, Note = note };

        private static OwnNotedDate Own(string id, string title, int month, int day, int? year = null, string? note = null) =>
            new(id, title, month, day, year, note);

        private InstalledArchive Install(ArchiveBuilder builder)
        {
            var staging = _library.CreateStagingFolder();
            builder.Write(staging);

            var outcome = _library.Install(staging);
            Assert.True(outcome.Installed);

            return _library.ListArchives(_settings).Single(archive => archive.Folder == outcome.InstalledFolder);
        }

        [Fact]
        public void Gifted_and_own_dates_on_the_same_day_show_together_by_title_before_the_years()
        {
            _settings.SetOwnNotedDates("hash", [Own("a", "Zebra day", 6, 1, 2020), Own("b", "Apple day", 6, 1)]);
            DateOnly[] days = [D(2019, 6, 1)];

            var card = OnThisDay.For(_settings, "hash", days, D(2026, 6, 1), [Gifted(7, "Middle day", 6, 1, 2022), Gifted(8, "Other day", 6, 2)]);

            Assert.Equal(["Apple day", "Middle day", "Zebra day"], card.Noted.Select(n => n.Title).ToList());
            Assert.Equal(["Apple day", "Middle day · 4 years", "Zebra day · 6 years"], card.Noted.Select(n => n.Text).ToList());
            Assert.Equal([2019], card.Years.Select(y => y.Year).ToList());
            Assert.True(card.ShowsIcon);
        }

        [Fact]
        public void A_noted_date_with_no_messages_that_day_still_shows_the_card()
        {
            var card = OnThisDay.For(_settings, "hash", [D(2020, 1, 1)], D(2026, 2, 14), [Gifted(1, "Our day", 2, 14, 2019, "A note")]);

            Assert.Empty(card.Years);
            Assert.True(card.ShowsIcon);
            Assert.True(card.OpensByItself);
            Assert.Equal("A note", card.Noted.Single().Note);
            Assert.Null(card.Noted.Single().JumpDay);

            // Nothing noted and no years: no card.
            Assert.False(OnThisDay.For(_settings, "hash", [D(2020, 1, 1)], D(2026, 2, 15), [Gifted(1, "Our day", 2, 14, 2019)]).ShowsIcon);
        }

        [Fact]
        public void The_year_count_is_shown_from_one_year_and_not_at_nought_or_without_a_year()
        {
            var today = D(2026, 5, 5);

            Assert.Equal("Day · 7 years", NoteOn(today, 2019).Text);
            Assert.Equal("Day · 1 year", NoteOn(today, 2025).Text);
            Assert.Equal("Day", NoteOn(today, 2026).Text);
            Assert.Null(NoteOn(today, 2026).Years);
            Assert.Equal("Day", NoteOn(today, null).Text);
        }

        private NotedToday NoteOn(DateOnly today, int? year) =>
            OnThisDay.For(_settings, "hash", [], today, [Gifted(1, "Day", today.Month, today.Day, year)]).Noted.Single();

        [Fact]
        public void The_leap_day_shows_on_the_28th_in_a_year_without_one_and_counts_years()
        {
            DateOnly[] days = [D(2020, 2, 29)];
            ArchiveNotedDate[] gifted = [Gifted(1, "Leap", 2, 29, 2020), Gifted(2, "Plain 28th", 2, 28)];

            var in2027 = OnThisDay.For(_settings, "hash", days, D(2027, 2, 28), gifted).Noted;
            Assert.Equal(["Leap · 7 years", "Plain 28th"], in2027.Select(n => n.Text).ToList());
            Assert.Equal(D(2020, 2, 29), in2027[0].JumpDay);

            // In a leap year the 28th is only the 28th, and the 29th only the 29th.
            Assert.Equal(["Plain 28th"], OnThisDay.For(_settings, "hash", days, D(2028, 2, 28), gifted).Noted.Select(n => n.Title).ToList());
            Assert.Equal(["Leap · 8 years"], OnThisDay.For(_settings, "hash", days, D(2028, 2, 29), gifted).Noted.Select(n => n.Text).ToList());

            // Other days are never moved.
            Assert.Empty(OnThisDay.For(_settings, "hash", days, D(2027, 3, 1), gifted).Noted);
        }

        [Fact]
        public void It_jumps_only_when_the_noted_year_has_messages_that_day()
        {
            DateOnly[] days = [D(2019, 6, 1), D(2021, 6, 1)];
            ArchiveNotedDate[] gifted =
            [
                Gifted(1, "Has messages", 6, 1, 2019),
                Gifted(2, "Year without messages", 6, 1, 2020),
                Gifted(3, "No year", 6, 1),
                Gifted(4, "This year", 6, 1, 2026),
            ];

            var noted = OnThisDay.For(_settings, "hash", days, D(2026, 6, 1), gifted).Noted.ToDictionary(n => n.Title, n => n.JumpDay);

            Assert.Equal(D(2019, 6, 1), noted["Has messages"]);
            Assert.Null(noted["Year without messages"]);
            Assert.Null(noted["No year"]);
            Assert.Null(noted["This year"]);
        }

        [Fact]
        public void Turned_off_hides_the_noted_dates_too()
        {
            ArchiveNotedDate[] gifted = [Gifted(1, "Day", 6, 1, 2019)];
            _settings.SetOnThisDayEnabled("hash", false);

            var card = OnThisDay.For(_settings, "hash", [D(2019, 6, 1)], D(2026, 6, 1), gifted);

            Assert.False(card.ShowsIcon);
            Assert.False(card.OpensByItself);

            _settings.SetOnThisDayEnabled("hash", true);
            Assert.True(OnThisDay.For(_settings, "hash", [D(2019, 6, 1)], D(2026, 6, 1), gifted).ShowsIcon);
        }

        [Fact]
        public void Muting_is_stored_and_read_and_does_not_hide_the_date()
        {
            Assert.Empty(_settings.GetMutedNotedDates("hash"));

            _settings.SetNotedDateMuted("hash", 7, true);
            _settings.SetNotedDateMuted("hash", 3, true);
            Assert.Equal([3L, 7L], _settings.GetMutedNotedDates("hash").Order().ToList());
            Assert.Empty(_settings.GetMutedNotedDates("other"));

            ArchiveNotedDate[] gifted = [Gifted(7, "Muted", 6, 1), Gifted(8, "Not muted", 6, 1)];
            Assert.Equal([false, true], NotedDates.List(_settings, "hash", gifted).Select(l => l.Notify).ToList());
            Assert.Equal(2, OnThisDay.For(_settings, "hash", [], D(2026, 6, 1), gifted).Noted.Count);

            _settings.SetNotedDateMuted("hash", 7, false);
            _settings.SetNotedDateMuted("hash", 3, false);
            Assert.Empty(_settings.GetMutedNotedDates("hash"));
            Assert.False(_store.Values.ContainsKey("archive.hash.mutedNotedDates"));
        }

        [Fact]
        public void Own_dates_round_trip_and_a_damaged_value_reads_as_empty()
        {
            _settings.SetOwnNotedDates("hash", [Own("x", "Trip", 9, 3, 2018, "Note"), Own("y", "Another", 1, 31)]);

            Assert.Equal(
                [Own("y", "Another", 1, 31), Own("x", "Trip", 9, 3, 2018, "Note")],
                _settings.GetOwnNotedDates("hash"));

            foreach (var damaged in new[] { "not json", "{\"a\":1}", "[1,2]", "[null]", "[{\"Id\":\"a\",\"Title\":\"T\",\"Month\":13,\"Day\":1}]", "[{\"Id\":\"a\"}]", "[" })
            {
                _store.Set("archive.hash.ownNotedDates", damaged);
                Assert.Empty(_settings.GetOwnNotedDates("hash"));
            }

            // One bad entry does not take the good ones with it; a duplicate id is dropped.
            _store.Set(
                "archive.hash.ownNotedDates",
                "[{\"Id\":\"a\",\"Title\":\"Good\",\"Month\":2,\"Day\":29},{\"Id\":\"b\",\"Title\":\"\",\"Month\":1,\"Day\":1},{\"Id\":\"a\",\"Title\":\"Dup\",\"Month\":1,\"Day\":2}]");
            Assert.Equal([Own("a", "Good", 2, 29)], _settings.GetOwnNotedDates("hash"));

            _store.Set("archive.hash.mutedNotedDates", "x,,3,4:5");
            Assert.Equal([3L], _settings.GetMutedNotedDates("hash").ToList());
        }

        [Fact]
        public void Forget_clears_own_dates_and_mutes()
        {
            _settings.SetOwnNotedDates("hash", [Own("x", "Trip", 9, 3)]);
            _settings.SetNotedDateMuted("hash", 4, true);
            _settings.SetStarredMessages("hash", [1]);

            _settings.Forget("hash");

            Assert.Empty(_settings.GetOwnNotedDates("hash"));
            Assert.Empty(_settings.GetMutedNotedDates("hash"));
            Assert.Equal([1], _settings.GetStarredMessages("hash"));
        }

        [Fact]
        public void The_session_reads_gifted_dates_from_the_archive_and_keeps_own_ones_outside_it()
        {
            var builder = new ArchiveBuilder();
            builder.Add("Long ago", ArchiveBuilder.Other, new DateTime(2019, 7, 4, 10, 0, 0));
            builder.NotedDate(5, "Invented anniversary", 7, 4, 2019, "A note");
            var archive = Install(builder);
            var folder = archive.Folder;
            var before = ArchiveContents.Build(folder);
            var today = D(2026, 7, 4);

            using (var session = ArchiveSession.Open(archive, _settings))
            {
                Assert.Empty(session.SaveOwnNotedDate(null, "  My own  ", 7, 4, 2020, "  ", today));
                session.SetNotedDateMuted(5, true);
                session.SetNotedDateMuted(99, true);

                var listings = session.NotedDateListings;
                Assert.Equal(["Invented anniversary", "My own"], listings.Select(l => l.Title).ToList());
                Assert.Equal([true, false], listings.Select(l => l.IsGifted).ToList());
                Assert.False(listings[0].Notify);
                Assert.Equal("4 July 2019", listings[0].DateText);
                Assert.Null(listings[1].Note);

                var card = session.OnThisDayFor(session.ReadDays(), today);
                Assert.Equal(["Invented anniversary · 7 years", "My own · 6 years"], card.Noted.Select(n => n.Text).ToList());
                Assert.Equal(D(2019, 7, 4), card.Noted[0].JumpDay);
                Assert.Null(card.Noted[1].JumpDay);

                // Edit keeps the id; delete removes only own dates.
                var id = listings[1].Id;
                Assert.Empty(session.SaveOwnNotedDate(id, "Renamed", 7, 5, null, "Kept", today));
                Assert.Equal(["Renamed"], session.NotedDateListings.Where(l => !l.IsGifted).Select(l => l.Title).ToList());
                Assert.Equal(id, session.NotedDateListings.Single(l => !l.IsGifted).Id);

                session.DeleteOwnNotedDate("5");
                Assert.Equal(2, session.NotedDateListings.Count);
                session.DeleteOwnNotedDate(id);
                Assert.Equal(["Invented anniversary"], session.NotedDateListings.Select(l => l.Title).ToList());
            }

            // Nothing was written into the archive's folder.
            Assert.Equal(before, ArchiveContents.Build(folder));
        }

        [Fact]
        public void A_bad_own_date_is_refused_with_the_shared_rules_and_nothing_is_stored()
        {
            var archive = Install(new ArchiveBuilder());
            var today = D(2026, 7, 4);

            using var session = ArchiveSession.Open(archive, _settings);

            Assert.Equal([NotedDateField.Title], session.SaveOwnNotedDate(null, " ", 7, 4, null, null, today).Keys);
            Assert.Equal([NotedDateField.Date], session.SaveOwnNotedDate(null, "T", 2, 29, 2021, null, today).Keys);
            Assert.Equal([NotedDateField.Year], session.SaveOwnNotedDate(null, "T", 7, 4, 2027, null, today).Keys);
            Assert.Empty(session.SaveOwnNotedDate(null, "Leap", 2, 29, 2024, null, today));
            Assert.Empty(session.SaveOwnNotedDate(null, "Leap, no year", 2, 29, null, null, today));
            Assert.Equal(2, session.NotedDateListings.Count);
        }

        [Fact]
        public void Saving_a_new_date_twice_under_the_same_id_does_not_duplicate_it()
        {
            var archive = Install(new ArchiveBuilder());
            var today = D(2026, 7, 4);

            using var session = ArchiveSession.Open(archive, _settings);

            Assert.Empty(session.SaveOwnNotedDate("fixed", "Once", 7, 4, null, null, today));
            Assert.Empty(session.SaveOwnNotedDate("fixed", "Once", 7, 4, null, null, today));

            Assert.Equal(["fixed"], session.NotedDateListings.Select(l => l.Id).ToList());
        }

        [Fact]
        public void The_synthetic_archive_carries_noted_dates_and_validates()
        {
            using var folder = new TemporaryFolder();
            var options = new SyntheticArchiveOptions(Messages: 400, Days: 200, Images: 4, Missing: 0, Unmatched: 0, Start: new DateOnly(2016, 1, 6), NotedDates: true);
            var counts = SyntheticArchive.Write(folder.Path, options);

            Assert.Equal(3, counts.NotedDates);

            var validation = ArchiveValidator.Validate(folder.Path);
            Assert.True(validation.Passed, string.Join(", ", validation.FirstProblems));

            using var reader = ArchiveReader.Open(folder.Path);
            var days = reader.ReadDays();
            var card = OnThisDay.For(_settings, "hash", days, D(2027, 3, 14), reader.ReadNotedDates());

            Assert.Equal("Our synthetic anniversary · 11 years", card.Noted.Single().Text);
        }
    }
}
