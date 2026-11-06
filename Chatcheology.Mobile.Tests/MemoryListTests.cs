using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Memories;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// The Memories list on the phone (CH-56): what is listed with every layer and filter ignored, order and
    /// headings, row text, covers, groups (the Saved tabs, CH-59), title search, Show in chat, and the session's
    /// cached build.
    /// Invented titles, games, names and dates only.
    /// </summary>
    public sealed class MemoryListTests : IDisposable
    {
        private const long Local = MemoryArchiveBuilder.Local;
        private const long Other = MemoryArchiveBuilder.Other;

        private static readonly IReadOnlySet<long> NoYearly = new HashSet<long>();

        private readonly TemporaryFolder _root = new();
        private readonly MemorySettings _store = new();
        private readonly ArchiveSettings _settings;

        public MemoryListTests() => _settings = new ArchiveSettings(_store);

        public void Dispose() => _root.Dispose();

        private static DateTime At(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
            new(year, month, day, hour, minute, second);

        // ---------------------------------------------------------------------------------------
        // Builders: an index from a source (no files), and installed archives for session tests.
        // ---------------------------------------------------------------------------------------

        private static ArchiveCollection Collection(long id, string kind, string name, bool shown = false) => new()
        {
            CollectionID = id,
            Kind = kind,
            Name = name,
            SourceDescription = null,
            ShownByDefault = shown,
            PeriodGapMinutes = kind == ArchiveMemoryVocabulary.KindImported ? 60 : null,
        };

        private static ArchiveCollection Personal(long id, string name = "Holidays") => Collection(id, ArchiveMemoryVocabulary.KindPersonal, name, true);

        private static ArchiveCollection Imported(long id, string name = "Shared gaming") => Collection(id, ArchiveMemoryVocabulary.KindImported, name);

        private static ArchiveEntry EntryRow(long id, long collection, string title, DateTime start, DateTime? end = null, string precision = "Day") => new()
        {
            EntryID = id,
            CollectionID = collection,
            SourceKey = null,
            Title = title,
            StartLocal = start,
            EndLocal = end,
            StartUtc = null,
            EndUtc = null,
            DatePrecision = precision,
            Summary = null,
            ImageSha256 = null,
            ImageExtension = null,
            Source = null,
            DayLevel = null,
        };

        private static MemorySourceEntry Memory(
            long id, long collection, string title, DateTime start, DateTime? end = null, string precision = "Day", string? image = null, params MemoryPhoto[] photos) =>
            new(EntryRow(id, collection, title, start, end, precision), [], image, []) { ExtraPhotos = photos };

        private static MemorySourceEntry Game(long id, string title, params (string Label, (long Participant, DateTime Time)[] Items)[] periods) => GameIn(1, id, title, periods);

        private static MemorySourceEntry GameIn(long collection, long id, string title, params (string Label, (long Participant, DateTime Time)[] Items)[] periods)
        {
            var item = id * 100;
            var list = periods.Select((period, index) => new ArchiveEntryPeriod
            {
                EntryID = id,
                PeriodIndex = index,
                StartUtc = DateTime.SpecifyKind(period.Items.Min(i => i.Time), DateTimeKind.Utc),
                EndUtc = DateTime.SpecifyKind(period.Items.Max(i => i.Time), DateTimeKind.Utc),
                StartLocal = period.Items.Min(i => i.Time),
                EndLocal = period.Items.Max(i => i.Time),
                Label = period.Label,
                LocalItemCount = period.Items.Count(i => i.Participant == Local),
                OtherItemCount = period.Items.Count(i => i.Participant != Local),
                Items = period.Items.OrderBy(i => i.Time).Select(i => new ArchiveEntryItem
                {
                    EntryItemID = ++item,
                    EntryID = id,
                    PeriodIndex = index,
                    ParticipantID = i.Participant,
                    SetId = "setA",
                    ItemId = item.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ItemType = "bronze",
                    TimeUtc = DateTime.SpecifyKind(i.Time, DateTimeKind.Utc),
                    TimeLocal = i.Time,
                }).ToList(),
            }).ToList();

            var all = list.SelectMany(p => p.Items).Select(i => i.TimeLocal).ToList();
            var entry = EntryRow(id, collection, title, all.Min(), all.Max(), "Second");

            return new MemorySourceEntry(entry, list, null, []);
        }

        private static (string, (long, DateTime)[]) Period(string label, params (long, DateTime)[] items) => (label, items);

        private static MemoryIndex Index(IReadOnlyList<ArchiveCollection> collections, IReadOnlyList<MemorySourceEntry> entries, IReadOnlyList<DateTime>? messages = null) =>
            MemoryIndex.Build(new MemorySource
            {
                Collections = collections,
                Entries = entries,
                ParticipantNames = new Dictionary<long, string> { [Local] = "Alex", [Other] = "Sam" },
                Definitions = [],
                IconPath = _ => null,
                MessageTimes = messages ?? [],
            });

        private static List<MemoryListItem> Items(MemoryList list, string? group = null, string? query = null) =>
            group is null ? [.. list.Items] : list.Rows(group, query).OfType<MemoryListItem>().ToList();

        private static List<string> Describe(IEnumerable<MemoryListRow> rows) =>
            rows.Select(row => row switch
            {
                MemoryListYearRow year => "Y " + year.Text,
                MemoryListItem item => item.Key.PeriodIndex is { } period ? $"P {item.Key.EntryID}:{period}" : $"E {item.Key.EntryID}",
                _ => "?",
            }).ToList();

        /// <summary>Messages on 10 and 12 March 2025; games, a "different times" record and a one-account record; a personal memory.</summary>
        private static MemoryArchiveBuilder Sample(Action<MemoryArchiveBuilder>? more = null)
        {
            var builder = new MemoryArchiveBuilder();

            builder.Message(At(2025, 3, 10, 20, 0));
            builder.Message(At(2025, 3, 10, 20, 10));
            builder.Message(At(2025, 3, 12, 9, 0));

            builder.Game(
                10, "Invented Racer", "supporting", true,
                new PeriodSpec("strong", new TrophySpec(Local, At(2025, 3, 10, 20, 10), "1"), new TrophySpec(Other, At(2025, 3, 10, 20, 10, 30), "2")),
                new PeriodSpec("one-account", new TrophySpec(Other, At(2025, 3, 10, 20, 40), "3")),
                new PeriodSpec("same-period", new TrophySpec(Local, At(2025, 3, 10, 21, 0), "4"), new TrophySpec(Other, At(2025, 3, 10, 21, 30), "5")));

            builder.Game(
                11, "Invented Puzzler", "same-day", false,
                new PeriodSpec("one-account", new TrophySpec(Local, At(2025, 3, 11, 10, 0), "6")),
                new PeriodSpec("one-account", new TrophySpec(Other, At(2025, 3, 11, 15, 0), "7")));

            builder.Game(
                14, "Invented Solo", "same-day", false,
                new PeriodSpec("one-account", new TrophySpec(Local, At(2025, 3, 12, 9, 4), "12")));

            builder.Memory(20, "Beach week", At(2025, 3, 20), At(2025, 3, 25), image: true);

            more?.Invoke(builder);

            return builder;
        }

        private ArchiveSession Open(MemoryArchiveBuilder builder)
        {
            var library = new ArchiveLibrary(Path.Combine(_root.Path, "library-" + Guid.NewGuid().ToString("N")));
            var staging = library.CreateStagingFolder();
            builder.Write(staging);

            var outcome = library.Install(staging);
            Assert.True(outcome.Installed, string.Join(", ", outcome.Validation.FirstProblems));

            return ArchiveSession.Open(library.ListArchives(_settings).Single(), _settings);
        }

        // ---------------------------------------------------------------------------------------
        // 1, 2, 3: what is listed.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public async Task EveryCollection_IsListed_WithEveryLayerAndFilterOff()
        {
            using var session = Open(Sample());

            // Shared gaming is hidden by default; switch the personal layer and every filter off too.
            session.SetLayerShown(MemoryArchiveBuilder.Personal, false);

            foreach (var filter in MemoryFilters.All)
            {
                session.SetMemoryFilter(MemoryArchiveBuilder.Gaming, filter, false);
            }

            Assert.True(session.MemoryView.IsEmpty);

            var list = await session.GetMemoryListAsync();

            Assert.Equal(["E 20", "E 11", "P 10:2", "P 10:0"], Describe(list.Items));
            Assert.Equal(["Y 2025", "E 20"], Describe(list.Rows(MemoryList.PersonalGroup, null)));
            Assert.Equal(["Y 2025", "E 11", "P 10:2", "P 10:0"], Describe(list.Rows(MemoryList.CollectionGroup(MemoryArchiveBuilder.Gaming), null)));

            // Nothing was written to Settings by building the list.
            Assert.True(session.MemoryView.IsEmpty);
        }

        [Fact]
        public void OneAccountPeriods_AreNeverListed()
        {
            var index = Index(
                [Imported(1)],
                [
                    Game(10, "Mixed", Period("strong", (Local, At(2025, 3, 10, 20, 0)), (Other, At(2025, 3, 10, 20, 5))), Period("one-account", (Other, At(2025, 3, 10, 22, 0)))),
                    Game(11, "Solo", Period("one-account", (Local, At(2025, 3, 11, 9, 0)))),
                    Game(12, "Solo again", Period("one-account", (Local, At(2025, 3, 12, 9, 0))), Period("one-account", (Local, At(2025, 3, 12, 15, 0)))),
                ]);

            var list = MemoryList.Build(index, NoYearly);

            Assert.Equal([new MemoryKey(10, 0)], Items(list).Select(i => i.Key));
        }

        [Fact]
        public void ADifferentTimesRecord_IsOneRowAtItsFirstItem()
        {
            var index = Index(
                [Imported(1)],
                [Game(11, "Invented Puzzler", Period("one-account", (Other, At(2025, 3, 11, 15, 0))), Period("one-account", (Local, At(2025, 3, 11, 10, 0))))]);

            var item = Assert.Single(Items(MemoryList.Build(index, NoYearly)));

            Assert.Equal(new MemoryKey(11, null), item.Key);
            Assert.Equal(At(2025, 3, 11, 10, 0), item.SortTime);
            Assert.Equal(MemoryListItemKind.DifferentTimes, item.Kind);
            Assert.Equal(DateSeparatorRow.DayText(new DateOnly(2025, 3, 11)), item.DateText);
            Assert.Equal("at different times", item.DetailText);
            Assert.Equal(item.DateText + " · at different times", item.MutedLine);
            Assert.False(item.HasLine);
            Assert.Null(item.ImagePath);
        }

        // ---------------------------------------------------------------------------------------
        // 4, 5: yearly memories and noted dates.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public async Task AYearlyMemory_IsListedOnceAtItsOwnDate()
        {
            var builder = new MemoryArchiveBuilder();
            builder.Message(At(2019, 2, 14, 10, 0));
            builder.Message(At(2024, 2, 14, 10, 0));
            builder.Yearly(31, "Invented anniversary", new DateOnly(2019, 2, 14));
            builder.Memory(34, "Not yearly", At(2019, 2, 14));

            using var session = Open(builder);
            var items = Items(await session.GetMemoryListAsync());

            var yearly = Assert.Single(items, i => i.Key.EntryID == 31);
            Assert.True(yearly.IsYearly);
            Assert.Equal(At(2019, 2, 14), yearly.SortTime);
            Assert.Equal("Holidays · Every year", yearly.DetailText);

            var plain = Assert.Single(items, i => i.Key.EntryID == 34);
            Assert.False(plain.IsYearly);
            Assert.Equal("Holidays", plain.DetailText);

            Assert.Equal(2, items.Count);
        }

        [Fact]
        public void AnEntryNotInTheYearlySet_ListsWithoutEveryYear()
        {
            // A rule-breaking row is left out of the yearly set by YearlyMemories.From; it shows as a plain memory.
            var index = Index([Personal(2)], [Memory(31, 2, "Invented anniversary", At(2019, 2, 14))]);

            var item = Assert.Single(Items(MemoryList.Build(index, NoYearly)));

            Assert.False(item.IsYearly);
            Assert.Equal("Holidays", item.DetailText);
        }

        [Fact]
        public async Task NotedDates_AreNotListed()
        {
            // Version 2 with gifted noted dates, and the phone's own noted date added.
            var builder = Sample(b => b.NotedDate(1, "Invented gifted day", 3, 10, 2020));

            using var session = Open(builder);
            Assert.Empty(session.SaveOwnNotedDate(null, "Invented own day", 3, 12, 2021, null, new DateOnly(2026, 11, 3)));
            Assert.NotEmpty(session.NotedDateListings);

            var list = await session.GetMemoryListAsync();

            Assert.Equal([20L, 11L, 10L, 10L], Items(list).Select(i => i.Key.EntryID));
            Assert.DoesNotContain(Items(list), i => i.Title.Contains("day", StringComparison.Ordinal));
        }

        // ---------------------------------------------------------------------------------------
        // 6: order and headings.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void NewestFirst_WithStableTies_HeadingsOnlyForYearsWithRows()
        {
            var index = Index(
                [Personal(2), Imported(1)],
                [
                    Memory(5, 2, "Tie B", At(2021, 6, 1)),
                    Memory(4, 2, "Tie A", At(2021, 6, 1)),
                    Memory(6, 2, "March month", At(2021, 3, 1), precision: "Month"),
                    Memory(7, 2, "March day", At(2021, 3, 15)),
                    Memory(8, 2, "Exact", At(2021, 3, 1, 0, 0, 1), precision: "Second"),
                    Memory(9, 2, "Old", At(2018, 1, 2)),
                    Game(10, "Two periods", Period("strong", (Local, At(2021, 6, 1)), (Other, At(2021, 6, 1))), Period("supporting", (Local, At(2021, 6, 1)), (Other, At(2021, 6, 1)))),
                ]);

            var list = MemoryList.Build(index, NoYearly);

            Assert.Equal(["E 4", "E 5", "P 10:0", "P 10:1", "E 7", "E 8", "E 6", "E 9"], Describe(list.Items));
            Assert.Equal(
                ["Y 2021", "E 4", "E 5", "E 7", "E 8", "E 6", "Y 2018", "E 9"],
                Describe(list.Rows(MemoryList.PersonalGroup, string.Empty)));
            Assert.Equal(["Y 2021", "P 10:0", "P 10:1"], Describe(list.Rows(MemoryList.CollectionGroup(1), string.Empty)));

            // A Month memory sorts at its 1st, below that month's dated rows, and says only the month.
            var month = Items(list).Single(i => i.Key.EntryID == 6);
            Assert.Equal(At(2021, 3, 1), month.SortTime);
            Assert.Equal(MemoryWording.EntryDate("Month", At(2021, 3, 1), null), month.DateText);

            // A Second-precision entry sorts at its exact time.
            Assert.Equal(At(2021, 3, 1, 0, 0, 1), Items(list).Single(i => i.Key.EntryID == 8).SortTime);
        }

        // ---------------------------------------------------------------------------------------
        // 7: text.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void EntryDates_AreTheCardsDates()
        {
            var index = Index(
                [Personal(2)],
                [
                    Memory(1, 2, "Day", At(2018, 1, 2)),
                    Memory(2, 2, "Month", At(2018, 1, 1), precision: "Month"),
                    Memory(3, 2, "Second", At(2018, 1, 3, 14, 5, 7), precision: "Second"),
                    Memory(4, 2, "Range", At(2019, 6, 1), At(2019, 6, 14)),
                ]);

            var items = Items(MemoryList.Build(index, NoYearly)).ToDictionary(i => i.Key.EntryID);

            Assert.Equal(MemoryWording.EntryDate("Day", At(2018, 1, 2), null), items[1].DateText);
            Assert.Equal(MemoryWording.EntryDate("Month", At(2018, 1, 1), null), items[2].DateText);
            Assert.Equal(MemoryWording.EntryDate("Second", At(2018, 1, 3, 14, 5, 7), null), items[3].DateText);
            Assert.Equal(MemoryWording.EntryDate("Day", At(2019, 6, 1), At(2019, 6, 14)), items[4].DateText);
            Assert.Equal(DateSeparatorRow.DayText(new DateOnly(2019, 6, 1)) + " to " + DateSeparatorRow.DayText(new DateOnly(2019, 6, 14)), items[4].DateText);

            // A range is one row, at its start.
            Assert.Equal(4, items.Count);
            Assert.Equal(At(2019, 6, 1), items[4].SortTime);
            Assert.Equal(items[4].DateText, items[4].Line);
            Assert.Equal("Holidays", items[4].MutedLine);
        }

        [Fact]
        public void PeriodRows_ShowTheDayObservedTimesAndLabel()
        {
            var index = Index(
                [Imported(1)],
                [
                    Game(1, "One trophy each minute", Period("strong", (Local, At(2021, 3, 14, 20, 18)), (Other, At(2021, 3, 14, 20, 18, 40)))),
                    Game(2, "Evening", Period("supporting", (Local, At(2021, 3, 15, 20, 18)), (Other, At(2021, 3, 15, 22, 15)))),
                    Game(3, "Late", Period("same-period", (Local, At(2021, 3, 16, 23, 40)), (Other, At(2021, 3, 17, 0, 35)))),
                    Game(4, "Long", Period("strong", (Local, At(2021, 3, 1, 23, 40)), (Other, At(2021, 3, 3, 0, 35)))),
                    Game(5, "Same minute, later day", Period("strong", (Local, At(2021, 3, 1, 20, 18)), (Other, At(2021, 3, 3, 20, 18)))),
                ]);

            var items = Items(MemoryList.Build(index, NoYearly)).ToDictionary(i => i.Key.EntryID);

            Assert.Equal("at 20:18 · strong", items[1].DetailText);
            Assert.Equal(DateSeparatorRow.DayText(new DateOnly(2021, 3, 14)), items[1].DateText);
            Assert.Equal(items[1].DateText + " · at 20:18 · strong", items[1].Line);
            Assert.False(items[1].HasMutedLine);
            Assert.Equal("20:18 to 22:15 · supporting", items[2].DetailText);
            Assert.Equal("23:40 to 00:35 next day · same period", items[3].DetailText);
            Assert.Equal("23:40 to " + new DateOnly(2021, 3, 3).ToString("d MMMM", System.Globalization.CultureInfo.CurrentCulture) + ", 00:35 · strong", items[4].DetailText);
            Assert.DoesNotContain("at 20:18", items[5].DetailText, StringComparison.Ordinal);
            Assert.StartsWith("20:18 to ", items[5].DetailText, StringComparison.Ordinal);

            // Never a duration.
            Assert.All(items.Values, i => Assert.DoesNotContain(" h ", i.DetailText, StringComparison.Ordinal));
            Assert.All(items.Values, i => Assert.DoesNotContain("min", i.DetailText, StringComparison.Ordinal));
        }

        [Fact]
        public void SpanText_ComparesDatesFirst()
        {
            Assert.Equal("at 20:18", MemoryWording.SpanText(At(2021, 3, 1, 20, 18), At(2021, 3, 1, 20, 18, 59)));
            Assert.Equal("20:18 to 22:15", MemoryWording.SpanText(At(2021, 3, 1, 20, 18), At(2021, 3, 1, 22, 15)));
            Assert.Equal("23:40 to 00:35 next day", MemoryWording.SpanText(At(2021, 3, 1, 23, 40), At(2021, 3, 2, 0, 35)));
            Assert.Equal("20:18 to 20:18 next day", MemoryWording.SpanText(At(2021, 3, 1, 20, 18), At(2021, 3, 2, 20, 18)));
            Assert.Equal(
                "20:18 to " + new DateOnly(2021, 3, 3).ToString("d MMMM", System.Globalization.CultureInfo.CurrentCulture) + ", 20:18",
                MemoryWording.SpanText(At(2021, 3, 1, 20, 18), At(2021, 3, 3, 20, 18)));
            Assert.Equal(
                "23:00 to " + DateSeparatorRow.DayText(new DateOnly(2022, 1, 2)) + ", 01:00",
                MemoryWording.SpanText(At(2021, 12, 31, 23, 0), At(2022, 1, 2, 1, 0)));
        }

        [Fact]
        public void AnImportedEntryWithoutPeriods_HasTheMemoryLayoutWithoutAnImage()
        {
            var index = Index(
                [Imported(1, "Invented imports")],
                [Memory(1, 1, "Imported note", At(2020, 5, 6), image: "cover.png")]);

            var list = MemoryList.Build(index, NoYearly);
            var item = Assert.Single(Items(list));

            Assert.Equal(MemoryListItemKind.Entry, item.Kind);
            Assert.Equal(MemoryWording.EntryDate("Day", At(2020, 5, 6), null), item.DateText);
            Assert.Equal("Invented imports", item.DetailText);
            Assert.Null(item.ImagePath);
            Assert.Equal(MemoryList.CollectionGroup(1), item.Group);
            Assert.Single(Items(list, MemoryList.CollectionGroup(1)));
            Assert.Empty(Items(list, MemoryList.PersonalGroup));
        }

        // ---------------------------------------------------------------------------------------
        // 8: covers.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TheCover_IsTheFirstPresentPhoto_AndImportedRowsHaveNone()
        {
            var index = Index(
                [Personal(2), Imported(1)],
                [
                    Memory(1, 2, "Cover there", At(2020, 1, 1), image: "cover.png", photos: [new MemoryPhoto("extra.png", null)]),
                    Memory(2, 2, "Cover gone", At(2020, 1, 2), image: null, photos: [new MemoryPhoto(null, "gone"), new MemoryPhoto("second.png", null)]),
                    Memory(3, 2, "Nothing there", At(2020, 1, 3), image: null, photos: [new MemoryPhoto(null, null)]),
                    Game(4, "Game", Period("strong", (Local, At(2020, 1, 4)), (Other, At(2020, 1, 4)))),
                ]);

            var items = Items(MemoryList.Build(index, NoYearly)).ToDictionary(i => i.Key.EntryID);

            Assert.Equal("cover.png", items[1].ImagePath);
            Assert.Equal("second.png", items[2].ImagePath);
            Assert.Null(items[3].ImagePath);
            Assert.False(items[3].HasImage);
            Assert.Null(items[4].ImagePath);

            foreach (var item in items.Values)
            {
                Assert.Equal(item.Kind == MemoryListItemKind.Entry ? index.CoverOf(item.Key.EntryID) : null, item.ImagePath);
            }
        }

        // ---------------------------------------------------------------------------------------
        // 9, 13: groups (the Saved tabs) and empty lists.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Groups_PersonalTogether_EachImportedOnItsOwn()
        {
            var index = Index(
                [Imported(1, "Shared gaming"), Personal(2, "Holidays"), Personal(3, "Firsts"), Imported(4, "Invented other source"), Imported(5, "Only one account")],
                [
                    Memory(1, 2, "Beach", At(2020, 1, 1)),
                    Memory(2, 3, "First walk", At(2020, 1, 2)),
                    Game(3, "Racer", Period("strong", (Local, At(2020, 1, 3)), (Other, At(2020, 1, 3)))),
                    GameIn(4, 4, "Puzzle", Period("supporting", (Local, At(2020, 1, 4)), (Other, At(2020, 1, 4)))),
                    GameIn(5, 5, "Solo", Period("one-account", (Local, At(2020, 1, 5)))),
                ]);

            var list = MemoryList.Build(index, NoYearly);

            // Memories folds the personal collections together.
            Assert.Equal([2L, 1L], Items(list, MemoryList.PersonalGroup).Select(i => i.Key.EntryID));
            Assert.Equal([3L], Items(list, "collection:1").Select(i => i.Key.EntryID));
            Assert.Equal([4L], Items(list, "collection:4").Select(i => i.Key.EntryID));
            Assert.Empty(list.Rows("collection:5", null));
            Assert.Empty(list.Rows("collection:99", null));

            Assert.Equal(4, list.Count);
            Assert.Equal(2, list.PersonalCount);
            Assert.Equal(2, list.ImportedCount);
            Assert.Equal(2, list.CountIn(MemoryList.PersonalGroup));
            Assert.Equal(0, list.CountIn("collection:5"));
        }

        [Fact]
        public void AnEmptyGroup_HasItsOwnEmptyLine()
        {
            var index = Index(
                [Imported(1), Personal(2), Imported(3, "Only one account")],
                [
                    Game(3, "Racer", Period("strong", (Local, At(2020, 1, 3)), (Other, At(2020, 1, 3)))),
                    GameIn(3, 5, "Solo", Period("one-account", (Local, At(2020, 1, 5)))),
                ]);

            var list = MemoryList.Build(index, NoYearly);

            Assert.Equal("No memories yet. Memories you add will show here.", list.EmptyText(MemoryList.PersonalGroup, list.Rows(MemoryList.PersonalGroup, null)));
            Assert.Equal("Nothing shared to list in this collection.", list.EmptyText("collection:3", list.Rows("collection:3", null)));
            Assert.Equal(string.Empty, list.EmptyText("collection:1", list.Rows("collection:1", null)));
        }

        [Fact]
        public void NoCollections_IsEmpty()
        {
            foreach (var index in new[] { MemoryIndex.Empty, Index([], []) })
            {
                Assert.False(index.HasMemories);

                var list = MemoryList.Build(index, NoYearly);

                Assert.True(list.IsEmpty);
                Assert.Empty(list.Items);
                Assert.Empty(list.Rows(MemoryList.PersonalGroup, null));
                Assert.Equal(MemoryList.NoPersonalText, list.EmptyText(MemoryList.PersonalGroup, list.Rows(MemoryList.PersonalGroup, null)));
            }

            // Collections whose only records are one-account: nothing listed.
            var oneAccount = MemoryList.Build(Index([Imported(1)], [Game(5, "Solo", Period("one-account", (Local, At(2020, 1, 5))))]), NoYearly);

            Assert.True(oneAccount.IsEmpty);
            Assert.Equal(MemoryList.NothingSharedText, oneAccount.EmptyText("collection:1", oneAccount.Rows("collection:1", null)));
        }

        // ---------------------------------------------------------------------------------------
        // 10: search.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Search_MatchesTitlesOnly_InsideTheGroup()
        {
            var index = Index(
                [Imported(1, "Café games"), Personal(2, "Holidays")],
                [
                    Memory(1, 2, "Café by the sea", At(2021, 1, 1)),
                    Memory(2, 2, "Mountain walk", At(2019, 1, 1)),
                    Game(3, "Invented CAFE Racer", Period("strong", (Local, At(2020, 1, 3)), (Other, At(2020, 1, 3)))),
                ]);

            var list = MemoryList.Build(index, NoYearly);

            // Case and accents ignored; trimmed.
            Assert.Equal(["Y 2021", "E 1"], Describe(list.Rows(MemoryList.PersonalGroup, "  cafe ")));
            Assert.Equal(["Y 2020", "P 3:0"], Describe(list.Rows("collection:1", "  cafe ")));

            // Inside the group; headings follow the result.
            Assert.Equal(["Y 2021", "E 1"], Describe(list.Rows(MemoryList.PersonalGroup, "café")));

            // Blank is everything.
            Assert.Equal(2, Items(list, MemoryList.PersonalGroup, "   ").Count);
            Assert.Equal(2, Items(list, MemoryList.PersonalGroup, null).Count);

            // Collection names are not searched.
            Assert.Empty(list.Rows(MemoryList.PersonalGroup, "Holidays"));

            // No match: the no-match line, without the typed text.
            var none = list.Rows(MemoryList.PersonalGroup, "racer");
            Assert.Empty(none);
            Assert.Equal(MemoryList.NoMatchText, list.EmptyText(MemoryList.PersonalGroup, none));
            Assert.Equal(string.Empty, list.EmptyText(MemoryList.PersonalGroup, list.Rows(MemoryList.PersonalGroup, null)));
        }

        // ---------------------------------------------------------------------------------------
        // 10b: group by title on a collection tab (CH-59 step 3).
        // ---------------------------------------------------------------------------------------

        private const string Kart = "Invented Kart Rally";

        private static List<string> DescribeGrouped(IEnumerable<MemoryListRow> rows) =>
            rows.Select(row => row switch
            {
                MemoryListTitleRow title => $"T {title.Title} {(title.IsExpanded ? "open" : "closed")}",
                MemoryListItem item => item.Key.PeriodIndex is { } period ? $"P {item.Key.EntryID}:{period}" : $"E {item.Key.EntryID}",
                _ => "?",
            }).ToList();

        private static HashSet<string> Open(params string[] titles) => new(titles, StringComparer.Ordinal);

        /// <summary>
        /// Game-day records: Kart Rally on three days (one "different times"), the same title in lower case, another
        /// game on two days, a "different times" game, and Kart Rally in another collection.
        /// </summary>
        private static MemoryList GroupedSample() => MemoryList.Build(
            Index(
                [Imported(1), Imported(2, "Invented other source")],
                [
                    Game(10, Kart,
                        Period("strong", (Local, At(2021, 3, 5, 20, 0)), (Other, At(2021, 3, 5, 20, 5))),
                        Period("one-account", (Local, At(2021, 3, 5, 21, 0))),
                        Period("supporting", (Local, At(2021, 3, 5, 23, 0)), (Other, At(2021, 3, 5, 23, 5)))),
                    Game(11, Kart, Period("strong", (Local, At(2024, 1, 9, 20, 0)), (Other, At(2024, 1, 9, 20, 1)))),
                    Game(14, Kart, Period("one-account", (Other, At(2022, 5, 3, 15, 0))), Period("one-account", (Local, At(2022, 5, 3, 9, 0)))),
                    Game(15, "invented kart rally", Period("strong", (Local, At(2020, 7, 1, 10, 0)), (Other, At(2020, 7, 1, 10, 1)))),
                    Game(12, "Invented Tower Puzzle",
                        Period("strong", (Local, At(2022, 6, 1, 10, 0)), (Other, At(2022, 6, 1, 10, 1)))),
                    Game(16, "Invented Tower Puzzle",
                        Period("strong", (Local, At(2022, 8, 1, 10, 0)), (Other, At(2022, 8, 1, 10, 1)))),
                    Game(13, "Invented Garden Builder", Period("one-account", (Other, At(2023, 2, 2, 15, 0))), Period("one-account", (Local, At(2023, 2, 2, 9, 0)))),
                    GameIn(2, 17, Kart, Period("strong", (Local, At(2023, 9, 1, 10, 0)), (Other, At(2023, 9, 1, 10, 1)))),
                ]),
            NoYearly);

        [Fact]
        public void GroupByTitle_SameExactTitleIsOneHeading_OtherTitlesAndCaseStayApart_NewestFirst_StartingClosed()
        {
            var list = GroupedSample();

            var rows = MemoryTitleGroups.Rows(list, "collection:1", null, Open());

            // One heading per exact title in this collection, ordered by its latest row; a title differing only in
            // case stays apart.
            Assert.Equal(
                [$"T {Kart} closed", "T Invented Garden Builder closed", "T Invented Tower Puzzle closed", "T invented kart rally closed"],
                DescribeGrouped(rows));

            // The other collection's Kart Rally is its own tab's heading, not merged here.
            Assert.Equal([$"T {Kart} open", "P 17:0"], DescribeGrouped(MemoryTitleGroups.Rows(list, "collection:2", null, Open(Kart))));

            // Grouping changes no row: every Date row is under exactly one heading.
            var all = MemoryTitleGroups.Rows(list, "collection:1", null, Open(rows.Cast<MemoryListTitleRow>().Select(t => t.Title).ToArray()))
                .OfType<MemoryListItem>().ToList();
            var dated = Items(list, "collection:1");
            Assert.Equal(dated.Count, all.Count);
            Assert.Equal(dated.ToHashSet(), all.ToHashSet());

            // The Date list itself is unchanged.
            Assert.Equal(
                ["Y 2024", "P 11:0", "Y 2023", "E 13", "Y 2022", "P 16:0", "P 12:0", "E 14", "Y 2021", "P 10:2", "P 10:0", "Y 2020", "P 15:0"],
                Describe(list.Rows("collection:1", null)));
        }

        [Fact]
        public void GroupByTitle_AnOpenHeading_ListsAllItsEntriesRowsNewestFirstUnderIt()
        {
            var list = GroupedSample();

            var rows = MemoryTitleGroups.Rows(list, "collection:1", null, Open(Kart, "Invented Garden Builder"));

            Assert.Equal(
                [$"T {Kart} open", "P 11:0", "E 14", "P 10:2", "P 10:0", "T Invented Garden Builder open", "E 13", "T Invented Tower Puzzle closed", "T invented kart rally closed"],
                DescribeGrouped(rows));

            // Open state is by exact title: the lower-case title is not opened by the other.
            Assert.DoesNotContain("P 15:0", DescribeGrouped(rows));
        }

        [Fact]
        public void GroupByTitle_CountsSharedPeriodsOfAllEntries_AndShowsFirstToLastMonth()
        {
            var titles = MemoryTitleGroups.Rows(GroupedSample(), "collection:1", null, Open()).Cast<MemoryListTitleRow>().ToDictionary(t => t.Title, StringComparer.Ordinal);

            // Kart Rally: 2 + 1 shared periods over two entries; the one-account period is not listed and the
            // "different times" record (May 2022) sits under it uncounted; range over all its rows.
            Assert.Equal(3, titles[Kart].Sessions);
            Assert.Equal("3 sessions · Mar 2021 - Jan 2024", titles[Kart].Summary);

            // Singular, one month.
            Assert.Equal("1 session · Jul 2020", titles["invented kart rally"].Summary);

            // Two game days in different months.
            Assert.Equal("2 sessions · Jun 2022 - Aug 2022", titles["Invented Tower Puzzle"].Summary);

            // Only a "different times" row: no session.
            Assert.Equal(0, titles["Invented Garden Builder"].Sessions);
            Assert.Equal("0 sessions · Feb 2023", titles["Invented Garden Builder"].Summary);
        }

        [Fact]
        public void GroupByTitle_SummaryWording()
        {
            Assert.Equal("12 sessions · Mar 2021 - Jan 2024", MemoryTitleGroups.SummaryText(12, At(2021, 3, 1), At(2024, 1, 31)));
            Assert.Equal("1 session · Mar 2021", MemoryTitleGroups.SummaryText(1, At(2021, 3, 1), At(2021, 3, 31, 23, 59)));

            // The same month of another year is a range; the earlier comes first.
            Assert.Equal("Mar 2021 - Mar 2022", MemoryTitleGroups.RangeText(At(2022, 3, 1), At(2021, 3, 1)));

            var heading = new MemoryListTitleRow(Kart, 3, At(2021, 3, 1), At(2021, 5, 1), false);
            Assert.Equal("Invented Kart Rally, 3 sessions · Mar 2021 - May 2021, collapsed", heading.ScreenReaderText);
            Assert.Equal("Invented Kart Rally, 3 sessions · Mar 2021 - May 2021, expanded", (heading with { IsExpanded = true }).ScreenReaderText);
            Assert.NotEqual(heading.Mark, (heading with { IsExpanded = true }).Mark);

            Assert.Equal("Group by date, selected", MemoryTitleGroups.SwitchScreenReaderName(MemoryListGrouping.Date, selected: true));
            Assert.Equal("Group by title", MemoryTitleGroups.SwitchScreenReaderName(MemoryListGrouping.Title, selected: false));
        }

        [Fact]
        public void GroupByTitle_SearchLeavesOnlyMatchingHeadings_InsideTheGroup()
        {
            var list = GroupedSample();

            // Case and accents ignored, trimmed, as the Date search: both Kart titles match, still two headings.
            Assert.Equal(
                [$"T {Kart} closed", "T invented kart rally closed"],
                DescribeGrouped(MemoryTitleGroups.Rows(list, "collection:1", "  KART ", Open())));
            Assert.Equal(
                ["T Invented Tower Puzzle open", "P 16:0", "P 12:0"],
                DescribeGrouped(MemoryTitleGroups.Rows(list, "collection:1", "tower", Open("Invented Tower Puzzle", Kart))));

            Assert.Empty(MemoryTitleGroups.Rows(list, "collection:1", "nothing like it", Open()));
            Assert.Empty(MemoryTitleGroups.Rows(list, "collection:99", null, Open()));
            Assert.Equal(4, MemoryTitleGroups.Groups(list, "collection:1", "   ").Count);
        }

        // ---------------------------------------------------------------------------------------
        // 11: Show in chat.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ShowInChat_RowWhenVisible_DayWhenHiddenWithMessages_OtherwiseNone()
        {
            DateTime[] messages = [At(2025, 3, 10, 9, 0), At(2025, 3, 12, 9, 0), At(2025, 3, 20, 9, 0)];

            var index = Index(
                [Imported(1), Personal(2)],
                [
                    Game(10, "Racer", Period("strong", (Local, At(2025, 3, 10, 20, 0)), (Other, At(2025, 3, 10, 20, 5)))),
                    Game(11, "Puzzler", Period("one-account", (Local, At(2025, 3, 12, 8, 0))), Period("one-account", (Other, At(2025, 3, 12, 15, 0)))),
                    Game(12, "Midnight", Period("strong", (Local, At(2025, 3, 10, 23, 50)), (Other, At(2025, 3, 11, 0, 20)))),
                    Memory(20, 2, "Beach week", At(2025, 3, 20), At(2025, 3, 25)),
                    Memory(21, 2, "March", At(2025, 3, 1), precision: "Month"),
                    Memory(22, 2, "No messages that day", At(2025, 3, 5)),
                    Memory(23, 2, "Before the first message", At(2025, 2, 20)),
                ],
                messages);

            var list = MemoryList.Build(index, NoYearly);
            var allOn = new MemoryView([1, 2]);
            var allOff = MemoryView.None;
            var strongOff = new MemoryView([1, 2], [(1, MemoryFilters.Strong)]);

            // Visible now: the row.
            Assert.Equal(new MemoryKey(10, 0), list.ShowInChat(new MemoryKey(10, 0), allOn, true)!.Row);
            Assert.Null(list.ShowInChat(new MemoryKey(10, 0), allOn, true)!.Day);
            Assert.Equal(new MemoryKey(21, null), list.ShowInChat(new MemoryKey(21, null), allOn, true)!.Row);

            // Layer off, messages that day: the day.
            Assert.Equal(new DateOnly(2025, 3, 10), list.ShowInChat(new MemoryKey(10, 0), allOff, true)!.Day);
            Assert.Null(list.ShowInChat(new MemoryKey(10, 0), allOff, true)!.Row);

            // Filter off: the day.
            Assert.Equal(new DateOnly(2025, 3, 10), list.ShowInChat(new MemoryKey(10, 0), strongOff, true)!.Day);

            // A different-times row: its first item's day.
            Assert.Equal(new DateOnly(2025, 3, 12), list.ShowInChat(new MemoryKey(11, null), allOff, true)!.Day);

            // A period crossing midnight: its start day.
            Assert.Equal(new DateOnly(2025, 3, 10), list.ShowInChat(new MemoryKey(12, 0), allOff, true)!.Day);

            // A range memory: its start day.
            Assert.Equal(new DateOnly(2025, 3, 20), list.ShowInChat(new MemoryKey(20, null), allOff, true)!.Day);

            // No messages that day, between messages or before the first: none.
            Assert.Null(list.ShowInChat(new MemoryKey(22, null), allOff, true));
            Assert.Null(list.ShowInChat(new MemoryKey(23, null), allOff, true));

            // A Month memory with its layer off: none.
            Assert.Null(list.ShowInChat(new MemoryKey(21, null), allOff, true));

            // An archive without messages: none.
            Assert.Null(list.ShowInChat(new MemoryKey(10, 0), allOn, false));
            Assert.Null(list.ShowInChat(new MemoryKey(10, 0), allOff, false));

            // A key that is not listed: none.
            Assert.Null(list.ShowInChat(new MemoryKey(99, null), allOn, true));
        }

        [Fact]
        public void ShowInChat_AMonthMemoryOnADayWithMessages_StillNone()
        {
            var index = Index(
                [Personal(2)],
                [Memory(21, 2, "March", At(2025, 3, 1), precision: "Month")],
                [At(2025, 3, 1, 9, 0)]);

            var list = MemoryList.Build(index, NoYearly);

            Assert.Null(list.ShowInChat(new MemoryKey(21, null), MemoryView.None, true));
            Assert.NotNull(list.ShowInChat(new MemoryKey(21, null), new MemoryView([2]), true)!.Row);
        }

        [Fact]
        public async Task ShowInChat_ThroughTheSession_FollowsSettings()
        {
            using var session = Open(Sample());
            var list = await session.GetMemoryListAsync();

            // Shared gaming is off by default: the day; switched on: the row.
            Assert.Equal(new DateOnly(2025, 3, 10), session.MemoryShowInChat(list, new MemoryKey(10, 0))!.Day);

            session.SetLayerShown(MemoryArchiveBuilder.Gaming, true);
            Assert.Equal(new MemoryKey(10, 0), session.MemoryShowInChat(list, new MemoryKey(10, 0))!.Row);

            // The Beach week (20 March, no messages): visible, so the row; with its layer off, none.
            Assert.Equal(new MemoryKey(20, null), session.MemoryShowInChat(list, new MemoryKey(20, null))!.Row);
            session.SetLayerShown(MemoryArchiveBuilder.Personal, false);
            Assert.Null(session.MemoryShowInChat(list, new MemoryKey(20, null)));
        }

        // ---------------------------------------------------------------------------------------
        // 12, 14, 15, 16.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public async Task EveryListedKey_HasDetails()
        {
            using var session = Open(Sample(b => b.Yearly(31, "Invented anniversary", new DateOnly(2024, 6, 1))));
            var list = await session.GetMemoryListAsync();

            Assert.NotEmpty(Items(list));

            foreach (var item in Items(list))
            {
                var details = session.MemoryDetails(item.Key);
                Assert.NotNull(details);
                Assert.Equal(item.Key, details.Key);
                Assert.Equal(item.Title, details.Title);
            }
        }

        [Fact]
        public void TenThousandPeriods_AreListedInOrder()
        {
            var start = At(2000, 1, 1, 12, 0);
            var entries = Enumerable.Range(1, 5_000)
                .Select(i => Game(
                    i,
                    "Invented game " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Period("strong", (Local, start.AddHours(i * 3)), (Other, start.AddHours(i * 3).AddMinutes(5))),
                    Period("supporting", (Local, start.AddHours(i * 3 + 1)), (Other, start.AddHours(i * 3 + 1).AddMinutes(5)))))
                .ToList();

            var list = MemoryList.Build(Index([Imported(1)], entries), NoYearly);
            var items = Items(list);

            Assert.Equal(10_000, items.Count);
            Assert.Equal(10_000, list.Count);
            Assert.Equal(new MemoryKey(5_000, 1), items[0].Key);
            Assert.Equal(new MemoryKey(1, 0), items[^1].Key);
            Assert.True(items.Zip(items.Skip(1)).All(pair => pair.First.SortTime >= pair.Second.SortTime));

            var rows = list.Rows(MemoryList.CollectionGroup(1), null);
            Assert.Equal(items.Select(i => i.SortTime.Year).Distinct().Count(), rows.OfType<MemoryListYearRow>().Count());
        }

        [Fact]
        public async Task GetMemoryListAsync_IsBuiltOnce_AndRefusedAfterDispose()
        {
            var session = Open(Sample());

            var first = session.GetMemoryListAsync();
            Assert.Same(first, session.GetMemoryListAsync());
            Assert.Same(await first, await session.GetMemoryListAsync());

            session.Dispose();

            Assert.Throws<ObjectDisposedException>(() => { _ = session.GetMemoryListAsync(); });
        }

        [Fact]
        public async Task BuildingTheList_LeavesTheConversationUnchanged()
        {
            using var session = Open(Sample());
            session.SetLayerShown(MemoryArchiveBuilder.Personal, false);

            var before = session.CreateTimeline();
            before.OpenAtLatest();
            var daysBefore = session.ReadCalendarDays();

            _ = await session.GetMemoryListAsync();

            var after = session.CreateTimeline();
            after.OpenAtLatest();

            Assert.Equal(TestArchives.Describe(before.Rows), TestArchives.Describe(after.Rows));
            Assert.DoesNotContain("?", TestArchives.Describe(after.Rows));
            Assert.Equal(daysBefore, session.ReadCalendarDays());
            Assert.Equal(session.ReadDays(), session.ReadCalendarDays());
            Assert.Empty(session.FindMemories("Invented"));
        }

        [Fact]
        public async Task DaysWithMessages_AreExactlyTheReadDays()
        {
            // Messages on several days (one late at night); a personal memory on every day from 8 to 14 March.
            var builder = new MemoryArchiveBuilder();
            builder.Message(At(2025, 3, 9, 23, 59, 59));
            builder.Message(At(2025, 3, 10, 0, 0));
            builder.Message(At(2025, 3, 10, 20, 0));
            builder.Message(At(2025, 3, 12, 9, 0));
            builder.Message(At(2025, 3, 14, 7, 30));

            for (var day = 8; day <= 14; day++)
            {
                builder.Memory(100 + day, "Invented day " + day.ToString(System.Globalization.CultureInfo.InvariantCulture), At(2025, 3, day));
            }

            using var session = Open(builder);
            session.SetLayerShown(MemoryArchiveBuilder.Personal, false);

            var list = await session.GetMemoryListAsync();
            var readDays = session.ReadDays().ToHashSet();

            Assert.Equal(4, readDays.Count);

            for (var day = 8; day <= 14; day++)
            {
                var date = new DateOnly(2025, 3, day);
                var target = session.MemoryShowInChat(list, new MemoryKey(100 + day, null));

                Assert.Equal(readDays.Contains(date), target is not null);
                Assert.Equal(readDays.Contains(date) ? date : null, target?.Day);
            }
        }

        [Fact]
        public async Task AnArchiveWithoutCollections_HasNoMemories_AndAnEmptyList()
        {
            // No version 1 builder exists in the test support; an archive written without collections reads the same way.
            var builder = new ArchiveBuilder();
            builder.Add("Invented message");

            var library = new ArchiveLibrary(Path.Combine(_root.Path, "library-" + Guid.NewGuid().ToString("N")));
            var staging = library.CreateStagingFolder();
            builder.Write(staging);
            Assert.True(library.Install(staging).Installed);

            using var session = ArchiveSession.Open(library.ListArchives(_settings).Single(), _settings);
            var list = await session.GetMemoryListAsync();

            Assert.False(session.Memories.HasMemories);
            Assert.True(list.IsEmpty);
            Assert.Equal(MemoryList.NoPersonalText, list.EmptyText(MemoryList.PersonalGroup, list.Rows(MemoryList.PersonalGroup, null)));
        }
    }
}