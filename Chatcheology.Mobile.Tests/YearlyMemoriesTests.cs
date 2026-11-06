using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Memories;
using Chatcheology.Mobile.Notifications;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Synthetic;
using Chatcheology.Mobile.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// Yearly memories on the phone (CH-53 step 2): the date rule, the card's order and rows, the layer and On this
    /// day switches, rows that break the rule, the notification schedule, mutes and their own key, Forget, and
    /// version 2 archives unchanged. Invented titles and dates only.
    /// </summary>
    public sealed class YearlyMemoriesTests : IDisposable
    {
        private static readonly TimeOnly Nine = new(9, 0);

        private readonly TemporaryFolder _root = new();
        private readonly MemorySettings _store = new();
        private readonly ArchiveSettings _settings;

        public YearlyMemoriesTests() => _settings = new ArchiveSettings(_store);

        public void Dispose() => _root.Dispose();

        private static DateOnly D(int year, int month, int day) => new(year, month, day);

        private static DateTime At(int year, int month, int day, int hour = 0, int minute = 0) => new(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);

        private static YearlyMemory Y(long id, string title, int year, int month, int day) => new(id, title, D(year, month, day));

        private static ArchiveYearlyEntry Row(long id, string title, DateTime start, string precision = "Day", string? kind = ArchiveMemoryVocabulary.KindPersonal, DateTime? end = null) =>
            new() { EntryID = id, Title = title, StartLocal = start, DatePrecision = precision, CollectionKind = kind, EndLocal = end };

        private OnThisDay Card(DateOnly today, IEnumerable<YearlyMemory> yearly, IEnumerable<DateOnly>? days = null, IEnumerable<ArchiveNotedDate>? gifted = null) =>
            OnThisDay.For(_settings, "hash", days ?? [], today, gifted, null, yearly);

        private ArchiveLibrary NewLibrary() => new(Path.Combine(_root.Path, "library-" + Guid.NewGuid().ToString("N")));

        private static string Install(ArchiveLibrary library, MemoryArchiveBuilder builder)
        {
            var staging = library.CreateStagingFolder();
            builder.Write(staging);

            var outcome = library.Install(staging);
            Assert.True(outcome.Installed, string.Join(", ", outcome.Validation.FirstProblems));

            return outcome.InstalledFolder!;
        }

        private ArchiveSession Open(ArchiveLibrary library, string folder) =>
            ArchiveSession.Open(library.ListArchives(_settings).Single(archive => archive.Folder == folder), _settings);

        /// <summary>A message on 14 Feb 2019 and 1 Mar 2021, and three yearly memories: 14 Feb 2019, 14 Feb 2020 (no messages that day), 29 Feb 2020.</summary>
        private static MemoryArchiveBuilder Sample()
        {
            var builder = new MemoryArchiveBuilder();
            builder.Message(new DateTime(2019, 2, 14, 10, 0, 0));
            builder.Message(new DateTime(2021, 3, 1, 10, 0, 0));
            builder.Yearly(31, "Zebra anniversary", D(2019, 2, 14), image: true);
            builder.Yearly(32, "Apple anniversary", D(2020, 2, 14), false, "An invented caption");
            builder.Yearly(33, "Invented leap day", D(2020, 2, 29));
            builder.Gallery(ExternalLinkOwnerKind.Entry, 31, "https://example.org/invented-gallery");

            // An ordinary personal memory on the same day is not yearly.
            builder.Memory(34, "Not yearly", new DateTime(2019, 2, 14));

            return builder;
        }

        [Fact]
        public void Not_before_its_own_date_without_a_count_in_its_own_year_and_counted_after()
        {
            YearlyMemory[] yearly = [Y(1, "Future day", 2027, 6, 1)];

            // Before its own date: nothing, in earlier years and earlier in its own year.
            Assert.Empty(Card(D(2026, 6, 1), yearly).Yearly);
            Assert.False(Card(D(2026, 6, 1), yearly).ShowsIcon);
            Assert.Empty(Card(D(2027, 5, 31), yearly).Yearly);

            // Its own date: the title alone.
            var own = Card(D(2027, 6, 1), yearly).Yearly.Single();
            Assert.Null(own.Years);
            Assert.Equal("Future day", own.Text);

            // Later years: counted.
            Assert.Equal("Future day · 1 year", Card(D(2028, 6, 1), yearly).Yearly.Single().Text);
            Assert.Equal("Future day · 7 years", Card(D(2034, 6, 1), yearly).Yearly.Single().Text);

            // Other days never.
            Assert.Empty(Card(D(2028, 6, 2), yearly).Yearly);

            Assert.False(YearlyMemories.ShowsOn(D(2027, 6, 1), D(2026, 6, 1)));
            Assert.True(YearlyMemories.ShowsOn(D(2027, 6, 1), D(2027, 6, 1)));
            Assert.Null(YearlyMemories.YearsOn(D(2027, 6, 1), D(2027, 6, 1)));
            Assert.Equal(2, YearlyMemories.YearsOn(D(2027, 6, 1), D(2029, 6, 1)));
        }

        [Fact]
        public void The_leap_day_shows_on_the_28th_in_common_years_and_on_the_29th_in_leap_years()
        {
            YearlyMemory[] yearly = [Y(1, "Leap", 2020, 2, 29), Y(2, "Plain 28th", 2021, 2, 28)];

            Assert.Equal(["Leap · 7 years", "Plain 28th · 6 years"], Card(D(2027, 2, 28), yearly).Yearly.Select(y => y.Text).ToList());
            Assert.Equal(["Plain 28th · 7 years"], Card(D(2028, 2, 28), yearly).Yearly.Select(y => y.Text).ToList());
            Assert.Equal(["Leap · 8 years"], Card(D(2028, 2, 29), yearly).Yearly.Select(y => y.Text).ToList());
            Assert.Empty(Card(D(2027, 3, 1), yearly).Yearly);

            // Its own day, then not on 28 Feb 2020 (before it), nor on 28 Feb of a leap year.
            Assert.Equal("Leap", Card(D(2020, 2, 29), yearly).Yearly.Single(y => y.EntryID == 1).Text);
            Assert.Empty(Card(D(2020, 2, 28), yearly).Yearly);

            // The 28th in its first common year after the leap day counts one year.
            Assert.Equal("Leap · 1 year", Card(D(2021, 2, 28), yearly).Yearly.Single(y => y.EntryID == 1).Text);
        }

        [Fact]
        public void Yearly_memories_come_first_by_title_then_noted_dates_then_the_years()
        {
            _settings.SetOwnNotedDates("hash", [new OwnNotedDate("o", "Own noted", 6, 1, null, null)]);
            YearlyMemory[] yearly = [Y(3, "Zulu memory", 2018, 6, 1), Y(1, "alpha memory", 2019, 6, 1), Y(2, "Mike memory", 2020, 6, 2)];
            ArchiveNotedDate[] gifted = [new() { NotedDateID = 1, Title = "A gifted date", Month = 6, Day = 1, Year = 2017, Note = null }];

            var card = Card(D(2026, 6, 1), yearly, [D(2019, 6, 1), D(2021, 6, 1)], gifted);

            Assert.Equal(["alpha memory · 7 years", "Zulu memory · 8 years"], card.Yearly.Select(y => y.Text).ToList());
            Assert.Equal(["A gifted date", "Own noted"], card.Noted.Select(n => n.Title).ToList());
            Assert.Equal([2019, 2021], card.Years.Select(y => y.Year).ToList());

            // Show in chat only when the memory's own day has messages.
            Assert.Equal(D(2019, 6, 1), card.Yearly[0].ShowInChatDay);
            Assert.Null(card.Yearly[1].ShowInChatDay);

            // A yearly memory alone turns the card and icon on.
            var alone = OnThisDay.For(_settings, "another archive", [], D(2026, 6, 1), yearly: [Y(1, "Alone", 2019, 6, 1)]);
            Assert.Empty(alone.Noted);
            Assert.Empty(alone.Years);
            Assert.True(alone.ShowsIcon);
            Assert.True(alone.OpensByItself);
        }

        [Fact]
        public void The_session_shows_yearly_memories_with_cover_and_gallery_even_with_the_layer_off_and_On_this_day_off_hides_them()
        {
            var library = NewLibrary();
            var builder = Sample();
            builder.PersonalShownByDefault = false;
            var folder = Install(library, builder);

            using var session = Open(library, folder);

            // The layer is off: no memory rows in the chat, but the card still has them.
            Assert.Empty(session.Memories.Layout(session.MemoryView).Placements);

            var card = session.OnThisDayFor(session.ReadDays(), D(2026, 2, 14));
            Assert.Equal(["Apple anniversary · 6 years", "Zebra anniversary · 7 years"], card.Yearly.Select(y => y.Text).ToList());
            Assert.True(card.ShowsIcon);

            var apple = card.Yearly[0];
            var zebra = card.Yearly[1];

            // The cover thumbnail is the first present photo, as the memory card draws (an extra photo when there is no cover).
            Assert.NotNull(zebra.ImagePath);
            Assert.True(File.Exists(zebra.ImagePath));
            Assert.NotNull(apple.ImagePath);
            Assert.Equal(session.MemoryDetails(new MemoryKey(32, null))!.ImagePath, apple.ImagePath);
            Assert.Equal("Link · example.org", zebra.GalleryText);
            Assert.Empty(apple.GalleryText);

            // Show in chat only when that day has messages; the details open whatever the layer says.
            Assert.Equal(D(2019, 2, 14), zebra.ShowInChatDay);
            Assert.Null(apple.ShowInChatDay);
            Assert.Equal("Zebra anniversary", session.MemoryDetails(new MemoryKey(zebra.EntryID, null))!.Title);

            // Switching the layer on or off changes nothing on the card.
            session.SetLayerShown(MemoryArchiveBuilder.Personal, true);
            Assert.Equal(2, session.OnThisDayFor(session.ReadDays(), D(2026, 2, 14)).Yearly.Count);

            // On this day off: neither card nor icon.
            session.OnThisDayEnabled = false;
            Assert.False(session.OnThisDayFor(session.ReadDays(), D(2026, 2, 14)).ShowsIcon);
        }

        [Fact]
        public void Rows_that_break_the_rule_are_left_out()
        {
            ArchiveYearlyEntry[] rows =
            [
                Row(1, "Good", new DateTime(2019, 6, 1)),
                Row(2, "Month", new DateTime(2019, 6, 1), precision: "Month"),
                Row(3, "Second", new DateTime(2019, 6, 1, 10, 0, 0), precision: "Second"),
                Row(4, "Imported", new DateTime(2019, 6, 1), kind: ArchiveMemoryVocabulary.KindImported),
                Row(5, "No collection", new DateTime(2019, 6, 1), kind: null),
                Row(6, "With an end", new DateTime(2019, 6, 1), end: new DateTime(2019, 6, 3)),
            ];

            Assert.Equal(["Good"], YearlyMemories.From(rows).Select(y => y.Title).ToList());
        }

        [Fact]
        public void A_tampered_installed_archive_shows_and_notifies_only_the_rows_that_keep_the_rule()
        {
            var library = NewLibrary();
            var builder = Sample();
            builder.Memory(40, "Month memory", new DateTime(2019, 2, 1), precision: "Month");
            builder.Memory(41, "Ranged memory", new DateTime(2019, 2, 14), new DateTime(2019, 2, 16));
            builder.Game(42, "Invented game", "strong", false, new PeriodSpec(
                "strong",
                new TrophySpec(MemoryArchiveBuilder.Local, new DateTime(2019, 2, 14, 10, 0, 0), "1"),
                new TrophySpec(MemoryArchiveBuilder.Other, new DateTime(2019, 2, 14, 10, 0, 30), "2")));
            var folder = Install(library, builder);

            // Changed after install (the validator would refuse it at import): Every year on rows that break the rule.
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(folder, ArchiveFormat.DatabaseFileName)};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE Entry SET EveryYear = 1 WHERE EntryID IN (40, 41, 42);";
                Assert.Equal(3, command.ExecuteNonQuery());
            }

            using (var reader = ArchiveReader.Open(folder))
            {
                Assert.Equal([31L, 32L, 33L, 40L, 41L, 42L], reader.ReadYearlyEntries().Select(e => e.EntryID).ToList());
            }

            using (var session = Open(library, folder))
            {
                Assert.Equal([31L, 32L], session.OnThisDayFor(session.ReadDays(), D(2026, 2, 14)).Yearly.Select(y => y.EntryID).Order().ToList());
                Assert.Equal([32L, 33L, 31L], session.YearlyMemoryListings.Select(y => y.EntryID).ToList());
            }

            var archive = NotedDateSchedule.Read(library, _settings).Single();
            Assert.Equal([31L, 32L, 33L], archive.Yearly.Select(y => y.EntryID).Order().ToList());
            Assert.Equal("2 noted dates today", NotedDateSchedule.DueOn([archive], D(2026, 2, 14)).Single().Text);
        }

        [Fact]
        public void The_schedule_counts_yearly_memories_and_noted_dates_together_once_per_archive_per_day()
        {
            static YearlyMemoryListing L(long id, int year, int month, int day, bool notify = true) => new(id, "T" + id, D(year, month, day), notify);

            var both = new ArchiveNotedDates("a", "hash-a", true, [new NotedDateListing("1", "Noted", 3, 3, null, null, true, true)])
            {
                Yearly = [L(10, 2019, 3, 3), L(11, 2020, 3, 3), L(12, 2030, 3, 3), L(13, 2019, 3, 3, notify: false)],
            };
            var other = new ArchiveNotedDates("b", "hash-b", true, []) { Yearly = [L(20, 2019, 3, 3)] };
            ArchiveNotedDates[] archives = [both, other];

            // 3 March 2026: one noted date and two yearly memories in "a" (one dated 2030, one muted: left out); one in "b".
            var due = NotedDateSchedule.DueOn(archives, D(2026, 3, 3));
            Assert.Equal(["a", "b"], due.Select(d => d.ArchiveId).ToList());
            Assert.Equal([3, 1], due.Select(d => d.Count).ToList());
            Assert.Equal(["3 noted dates today", "A noted date today"], due.Select(d => d.Text).ToList());
            Assert.DoesNotContain(due, d => d.Text.Contains("T1", StringComparison.Ordinal) || d.Text.Contains("T2", StringComparison.Ordinal));

            // In 2030, the memory dated that year counts too.
            Assert.Equal(4, NotedDateSchedule.DueOn(archives, D(2030, 3, 3)).Single(d => d.ArchiveId == "a").Count);

            // Posted today: not again that day.
            Assert.Equal(["b"], NotedDateSchedule.DueOn([both with { PostedThrough = D(2026, 3, 3) }, other], D(2026, 3, 3)).Select(d => d.ArchiveId).ToList());

            // Only a yearly memory: a moment is found; only one dated in the future: none before its day.
            Assert.Equal(At(2026, 3, 3, 9, 0), NotedDateSchedule.Next([other], Nine, At(2026, 1, 1))!.LocalMoment);
            var future = new ArchiveNotedDates("c", "hash-c", true, []) { Yearly = [L(30, 2026, 5, 5)] };
            Assert.Equal(D(2026, 5, 5), NotedDateSchedule.Next([future], Nine, At(2025, 6, 1))!.Day);
            Assert.Empty(NotedDateSchedule.DueOn([future], D(2025, 5, 5)));

            // Muted or with On this day off: nothing can notify.
            Assert.Null(NotedDateSchedule.Next([new ArchiveNotedDates("d", "hash-d", true, []) { Yearly = [L(1, 2019, 3, 3, notify: false)] }], Nine, At(2026, 1, 1)));
            Assert.Null(NotedDateSchedule.Next([other with { OnThisDayEnabled = false }], Nine, At(2026, 1, 1)));
            Assert.False((other with { OnThisDayEnabled = false }).CanNotify);

            Assert.Equal(
                "archives=2 notedDates=1 yearly=5 notifying=5 scheduled=yes",
                NotedDateSchedule.DescribeCounts(archives, NotedDateSchedule.Next(archives, Nine, At(2026, 1, 1))));
        }

        [Fact]
        public void A_yearly_memory_dated_years_ahead_is_scheduled_for_its_own_date()
        {
            static ArchiveNotedDates Only(params YearlyMemoryListing[] yearly) => new("f", "hash-f", true, []) { Yearly = yearly };

            // Five years ahead, nothing else: its own date at the time, not nothing.
            var far = Only(new YearlyMemoryListing(1, "Far", D(2031, 11, 20), true));
            Assert.True(far.CanNotify);
            var next = NotedDateSchedule.Next([far], Nine, At(2026, 11, 3, 10, 0))!;
            Assert.Equal(D(2031, 11, 20), next.Day);
            Assert.Equal(At(2031, 11, 20, 9, 0), next.LocalMoment);
            Assert.Equal("A noted date today", next.Due.Single().Text);

            // A leap day years ahead: its own 29 February.
            Assert.Equal(D(2032, 2, 29), NotedDateSchedule.Next([Only(new YearlyMemoryListing(2, "Leap", D(2032, 2, 29), true))], Nine, At(2026, 11, 3))!.Day);

            // Two far memories: the nearer one; a nearer noted date still comes first.
            Assert.Equal(D(2029, 1, 1), NotedDateSchedule.Next([Only(new YearlyMemoryListing(3, "B", D(2033, 5, 5), true), new YearlyMemoryListing(4, "A", D(2029, 1, 1), true))], Nine, At(2026, 11, 3))!.Day);
            var withNoted = far with { Dates = [new NotedDateListing("n", "Noted", 1, 10, null, null, false, true)] };
            Assert.Equal(D(2027, 1, 10), NotedDateSchedule.Next([withNoted], Nine, At(2026, 11, 3))!.Day);

            // Muted, or On this day off: still nothing.
            Assert.Null(NotedDateSchedule.Next([Only(new YearlyMemoryListing(1, "Far", D(2031, 11, 20), false))], Nine, At(2026, 11, 3)));
            Assert.Null(NotedDateSchedule.Next([far with { OnThisDayEnabled = false }], Nine, At(2026, 11, 3)));

            // The emulator archive's future memory, alone: scheduled for its own date.
            var synthetic = Only(new YearlyMemoryListing(503, "Future", SyntheticArchive.FutureYearlyDay, true));
            Assert.Equal(SyntheticArchive.FutureYearlyDay, NotedDateSchedule.Next([synthetic], Nine, At(2026, 11, 3))!.Day);
        }

        [Fact]
        public void The_schedule_reads_yearly_memories_and_their_mutes_from_installed_archives()
        {
            var library = NewLibrary();
            var folder = Install(library, Sample());
            var hash = ArchiveLibrary.ContentsSha256Of(folder)!;

            var read = NotedDateSchedule.Read(library, _settings).Single();
            Assert.Equal(["Apple anniversary", "Invented leap day", "Zebra anniversary"], read.Yearly.Select(y => y.Title).ToList());
            Assert.Empty(read.Dates);
            Assert.Equal("2 noted dates today", NotedDateSchedule.DueOn([read], D(2026, 2, 14)).Single().Text);

            // 28 Feb 2027: the leap day memory.
            Assert.Equal("A noted date today", NotedDateSchedule.DueOn([read], D(2027, 2, 28)).Single().Text);

            // Muting one leaves the other.
            using (var session = Open(library, folder))
            {
                session.SetYearlyMemoryMuted(31, true);

                // Not a yearly memory of this archive: ignored.
                session.SetYearlyMemoryMuted(34, true);
                session.SetYearlyMemoryMuted(999, true);
                Assert.Equal([31L], _settings.GetMutedYearlyMemories(hash).ToList());
                Assert.Equal([true, true, false], session.YearlyMemoryListings.Select(y => y.Notify).ToList());

                // Muting does not take it off the card.
                Assert.Equal(2, session.OnThisDayFor(session.ReadDays(), D(2026, 2, 14)).Yearly.Count);
            }

            Assert.Equal("A noted date today", NotedDateSchedule.DueOn(NotedDateSchedule.Read(library, _settings), D(2026, 2, 14)).Single().Text);

            // On this day off for the archive: it does not notify.
            _settings.SetOnThisDayEnabled(hash, false);
            Assert.Null(NotedDateSchedule.Next(NotedDateSchedule.Read(library, _settings), Nine, At(2026, 1, 1)));
        }

        [Fact]
        public void Yearly_mutes_have_their_own_key_never_mixed_with_noted_date_mutes_and_Forget_clears_them()
        {
            _settings.SetYearlyMemoryMuted("hash", 5, true);
            _settings.SetYearlyMemoryMuted("hash", 2, true);

            Assert.Equal("2,5", _store.Values["archive.hash.mutedYearlyMemories"]);
            Assert.Empty(_settings.GetMutedNotedDates("hash"));
            Assert.False(_store.Values.ContainsKey("archive.hash.mutedNotedDates"));

            _settings.SetNotedDateMuted("hash", 5, true);
            Assert.Equal("5", _store.Values["archive.hash.mutedNotedDates"]);
            Assert.Equal([2L, 5L], _settings.GetMutedYearlyMemories("hash").Order().ToList());

            // A noted date muted with the same number does not mute the yearly memory, and the other way round.
            _settings.SetYearlyMemoryMuted("hash", 5, false);
            Assert.Equal([5L], _settings.GetMutedNotedDates("hash").ToList());
            Assert.Equal([2L], _settings.GetMutedYearlyMemories("hash").ToList());
            Assert.Equal(
                [true, false],
                YearlyMemories.List(_settings, "hash", [Y(5, "Five", 2019, 1, 1), Y(2, "Two", 2019, 1, 1)]).Select(y => y.Notify).ToList());

            // Another archive is not affected; a damaged value reads what it can.
            Assert.Empty(_settings.GetMutedYearlyMemories("other"));
            _store.Values["archive.hash.mutedYearlyMemories"] = "x,,7,4:5";
            Assert.Equal([7L], _settings.GetMutedYearlyMemories("hash").ToList());

            _settings.SetStarredMessages("hash", [1]);
            _settings.Forget("hash");
            Assert.Empty(_settings.GetMutedYearlyMemories("hash"));
            Assert.Empty(_settings.GetMutedNotedDates("hash"));
            Assert.Equal([1], _settings.GetStarredMessages("hash"));

            _settings.SetYearlyMemoryMuted("hash", 3, true);
            _settings.SetYearlyMemoryMuted("hash", 3, false);
            Assert.False(_store.Values.ContainsKey("archive.hash.mutedYearlyMemories"));
        }

        [Fact]
        public void Removing_the_archive_forgets_its_yearly_mutes()
        {
            var library = NewLibrary();
            var folder = Install(library, Sample());
            var hash = ArchiveLibrary.ContentsSha256Of(folder)!;
            _settings.SetYearlyMemoryMuted(hash, 31, true);

            library.Remove(library.ListArchives(_settings).Single(), _settings);

            Assert.Empty(_settings.GetMutedYearlyMemories(hash));
            Assert.Empty(NotedDateSchedule.Read(library, _settings));
        }

        [Fact]
        public void A_version_two_archive_with_gifted_noted_dates_behaves_as_before()
        {
            var library = NewLibrary();
            var builder = new MemoryArchiveBuilder();
            builder.Message(new DateTime(2019, 7, 4, 10, 0, 0));
            builder.Memory(20, "Ordinary memory", new DateTime(2019, 7, 4));
            builder.NotedDate(5, "Invented anniversary", 7, 4, 2019);
            builder.Gallery(ExternalLinkOwnerKind.NotedDate, 5, "https://example.org/invented");
            var folder = Install(library, builder);

            using (var reader = ArchiveReader.Open(folder))
            {
                Assert.Equal(ArchiveFormat.NotedDatesFormatVersion, reader.Info.FormatVersion);
            }

            using (var session = Open(library, folder))
            {
                Assert.Empty(session.YearlyMemoryListings);

                var card = session.OnThisDayFor(session.ReadDays(), D(2026, 7, 4));
                Assert.Empty(card.Yearly);
                Assert.Equal("Invented anniversary · 7 years", card.Noted.Single().Text);
                Assert.Equal("Link · example.org", card.Noted.Single().GalleryText);
                Assert.Equal(D(2019, 7, 4), card.Noted.Single().JumpDay);
                Assert.Equal([2019], card.Years.Select(y => y.Year).ToList());

                // Muting the gifted date uses the noted-date key only.
                session.SetNotedDateMuted(5, true);
                session.SetYearlyMemoryMuted(5, true);
                session.SetYearlyMemoryMuted(20, true);
                var hash = ArchiveLibrary.ContentsSha256Of(folder)!;
                Assert.Equal([5L], _settings.GetMutedNotedDates(hash).ToList());
                Assert.Empty(_settings.GetMutedYearlyMemories(hash));
                session.SetNotedDateMuted(5, false);
            }

            var read = NotedDateSchedule.Read(library, _settings).Single();
            Assert.Empty(read.Yearly);
            Assert.Equal("A noted date today", NotedDateSchedule.DueOn([read], D(2026, 7, 4)).Single().Text);
            Assert.Equal("archives=1 notedDates=1 yearly=0 notifying=1 scheduled=yes", NotedDateSchedule.DescribeCounts([read], NotedDateSchedule.Next([read], Nine, At(2026, 1, 1))));
        }

        [Fact]
        public void The_synthetic_emulator_archive_shows_its_yearly_memories_on_their_days()
        {
            using var folder = new TemporaryFolder();
            SyntheticArchive.Write(folder.Path, new SyntheticArchiveOptions(Messages: 400, Days: 200, Images: 4, Missing: 0, Unmatched: 0, Start: new DateOnly(2016, 1, 6), YearlyMemories: true));

            using var reader = ArchiveReader.Open(folder.Path);
            var yearly = YearlyMemories.From(reader.ReadYearlyEntries());
            var days = reader.ReadDays();

            Assert.Equal(["Our synthetic anniversary · 11 years"], OnThisDay.For(_settings, "hash", days, D(2027, 3, 14), yearly: yearly).Yearly.Select(y => y.Text).ToList());
            Assert.Equal(["Invented leap day · 7 years"], OnThisDay.For(_settings, "hash", days, D(2027, 2, 28), yearly: yearly).Yearly.Select(y => y.Text).ToList());

            // The memory dated in the future shows nothing before its day, then its title alone.
            var future = SyntheticArchive.FutureYearlyDay;
            Assert.Empty(OnThisDay.For(_settings, "hash", days, future.AddYears(-1), yearly: yearly).Yearly);
            Assert.Equal(["Invented future day"], OnThisDay.For(_settings, "hash", days, future, yearly: yearly).Yearly.Select(y => y.Text).ToList());
        }
    }
}
