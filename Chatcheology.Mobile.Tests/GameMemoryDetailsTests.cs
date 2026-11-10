using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Memories;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// A game memory's details on the phone (CH-67): the game model (date line with play window, evidence block and
    /// its meaning, trophies with together rows), and that personal, yearly and own memories get none of it.
    /// Invented games, names, sets and dates only.
    /// </summary>
    public sealed class GameMemoryDetailsTests
    {
        private const long Local = 1;
        private const long Other = 2;
        private const long Gaming = 1;
        private const long Personal = 2;

        private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

        /// <summary>Saturday 7 November 2026.</summary>
        private static DateTime At(int hour, int minute, int second = 0, int day = 7) => new(2026, 11, day, hour, minute, second);

        private sealed record Trophy(long Participant, DateTime Time, string ItemId, string SetId = "NPWR00001_00", string Type = "bronze");

        private static ArchiveCollection Collection(long id, string kind, string name) => new()
        {
            CollectionID = id,
            Kind = kind,
            Name = name,
            SourceDescription = null,
            ShownByDefault = true,
            PeriodGapMinutes = kind == ArchiveMemoryVocabulary.KindImported ? 60 : null,
        };

        private static ArchiveEntry EntryRow(long id, long collection, string title, DateTime start, DateTime? end, string precision, string? dayLevel, string? summary = null, string? source = null, bool everyYear = false) => new()
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
            Summary = summary,
            ImageSha256 = null,
            ImageExtension = null,
            Source = source,
            DayLevel = dayLevel,
            EveryYear = everyYear,
        };

        private static MemorySourceEntry Game(long id, string title, string? dayLevel, params (string Label, Trophy[] Items)[] periods)
        {
            var itemID = id * 100;
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
                Items = period.Items.Select(i => new ArchiveEntryItem
                {
                    EntryItemID = ++itemID,
                    EntryID = id,
                    PeriodIndex = index,
                    ParticipantID = i.Participant,
                    SetId = i.SetId,
                    ItemId = i.ItemId,
                    ItemType = i.Type,
                    TimeUtc = DateTime.SpecifyKind(i.Time, DateTimeKind.Utc),
                    TimeLocal = i.Time,
                }).ToList(),
            }).ToList();

            var all = list.SelectMany(p => p.Items).Select(i => i.TimeLocal).ToList();

            return new MemorySourceEntry(
                EntryRow(id, Gaming, title, all.Min(), all.Max(), "Second", dayLevel, summary: "Invented summary", source: "PSN trophies"),
                list,
                "game.png",
                []);
        }

        private static (string, Trophy[]) Period(string label, params Trophy[] items) => (label, items);

        private static ArchiveItemDefinition Definition(string itemId, string name, string setId = "NPWR00001_00", bool hidden = false) => new()
        {
            SetId = setId,
            ItemId = itemId,
            ItemType = "gold",
            Name = name,
            Detail = "Invented detail",
            Hidden = hidden,
            GroupId = null,
            IconSha256 = null,
            IconExtension = null,
        };

        private static MemoryIndex Index(MemoryOwner owner = MemoryOwner.Archive, IReadOnlyList<ArchiveItemDefinition>? definitions = null, params MemorySourceEntry[] entries) =>
            MemoryIndex.Build(new MemorySource
            {
                Collections =
                [
                    Collection(Gaming, ArchiveMemoryVocabulary.KindImported, "Shared gaming"),
                    Collection(Personal, ArchiveMemoryVocabulary.KindPersonal, "Holidays"),
                ],
                Entries = entries,
                ParticipantNames = new Dictionary<long, string> { [Local] = "Alex", [Other] = "Sam" },
                Definitions = definitions ?? [],
                IconPath = d => "icon-" + d.ItemId + ".png",
                MessageTimes = [],
                Owner = owner,
            });

        private static GameDetails GameOf(MemoryIndex index, long entry, int? period) =>
            Assert.IsType<GameDetails>(index.Details(new MemoryKey(entry, period))!.Game);

        private static List<string> Rows(GameDetails game) =>
            game.Trophies.Select(t => t.First.Name + " | " + MemoryWording.GameItemMeta(t)).ToList();

        private static void InEnglish(Action test)
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = English;

            try
            {
                test();
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        // ---------------------------------------------------------------------------------------
        // The game model.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void GameMemory_HasDateWithPlayWindow_Block_Picture_AndTrophiesInTimeOrder_WithoutTypes() => InEnglish(() =>
        {
            var index = Index(
                definitions: [Definition("1", "First Steps"), Definition("2", "Night Owl")],
                entries: Game(
                    10, "Invented Racer", "strong",
                    Period(
                        "strong",
                        new Trophy(Other, At(22, 15, 9), "3", Type: "platinum"),
                        new Trophy(Local, At(19, 40, 5), "1"),
                        new Trophy(Other, At(20, 0, 0), "2", Type: "silver"))));

            var details = index.Details(new MemoryKey(10, 0))!;
            var game = Assert.IsType<GameDetails>(details.Game);

            Assert.True(details.IsGame);
            Assert.Equal("Invented Racer", details.Title);
            Assert.Equal("Sat 7 Nov 2026, 19:40-22:15", game.DateText);
            Assert.Equal("\U0001F7E9", game.EvidenceBlock);
            Assert.Equal("Strong: you both earned the same trophy within a minute of each other this day.", game.EvidenceMeaning);
            Assert.Equal("Day evidence: strong. Tap for what it means.", game.EvidenceDescription);
            Assert.Equal(["game.png"], details.Photos.Select(p => p.Path));

            Assert.Equal(
                [
                    "First Steps | 19:40:05 · Alex",
                    "Night Owl | 20:00:00 · Sam",
                    "Platinum trophy #3 | 22:15:09 · Sam",
                ],
                Rows(game));

            Assert.Equal(["icon-1.png", "icon-2.png", null], game.Trophies.Select(t => t.First.IconPath));

            // No trophy type in any line the page shows, except the fallback name of an item without a definition.
            foreach (var trophy in game.Trophies)
            {
                foreach (var type in new[] { "bronze", "silver", "gold", "platinum" })
                {
                    Assert.DoesNotContain(type, MemoryWording.GameItemMeta(trophy), StringComparison.OrdinalIgnoreCase);
                }
            }

            Assert.DoesNotContain("gold", game.Trophies[0].First.Name, StringComparison.OrdinalIgnoreCase);
        });

        [Fact]
        public void GameMemory_KeepsTheGeneralFields_ForTheDesktop() => InEnglish(() =>
        {
            var index = Index(entries: Game(10, "Invented Racer", "supporting", Period("strong", new Trophy(Local, At(19, 40), "1"), new Trophy(Other, At(19, 41), "1"))));

            var details = index.Details(new MemoryKey(10, 0))!;

            // The desktop reads these; the phone's game page does not show them.
            Assert.Equal("7 November 2026", details.DateText);
            Assert.Equal("Day evidence: supporting", details.DayEvidenceText);
            Assert.Equal("PSN trophies", details.SourceText);
            Assert.Equal(2, details.Items.Count);
            Assert.Single(details.Game!.Trophies);
        });

        [Fact]
        public void DifferentTimesRecord_IsAGameMemory_OverEveryTrophy() => InEnglish(() =>
        {
            var index = Index(entries: Game(
                11, "Invented Puzzler", "same-day",
                Period("one-account", new Trophy(Local, At(10, 0), "6")),
                Period("one-account", new Trophy(Other, At(15, 5), "7"))));

            var game = GameOf(index, 11, null);

            Assert.Equal("Sat 7 Nov 2026, 10:00-15:05", game.DateText);
            Assert.Equal("\U0001F7E7", game.EvidenceBlock);
            Assert.Equal(["Bronze trophy #6 | 10:00:00 · Alex", "Bronze trophy #7 | 15:05:00 · Sam"], Rows(game));
        });

        [Fact]
        public void PersonalYearlyAndOwnMemories_HaveNoGameLayout()
        {
            var personal = new MemorySourceEntry(EntryRow(20, Personal, "Beach week", At(0, 0), null, "Day", null, summary: "Sand"), [], null, []);
            var yearly = new MemorySourceEntry(EntryRow(21, Personal, "Anniversary", At(0, 0), null, "Day", null, everyYear: true), [], null, []);

            var archive = Index(entries: [personal, yearly]);
            var own = Index(MemoryOwner.Own, entries: [personal]);

            foreach (var details in new[]
            {
                archive.Details(new MemoryKey(20, null))!,
                archive.Details(new MemoryKey(21, null))!,
                own.Details(new MemoryKey(20, null, MemoryOwner.Own))!,
            })
            {
                Assert.Null(details.Game);
                Assert.False(details.IsGame);
            }

            Assert.Equal("Sand", archive.Details(new MemoryKey(20, null))!.Summary);
        }

        // ---------------------------------------------------------------------------------------
        // Play window wording.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void PlayWindow_IsTheDayAndFirstToLastTime_NeverADuration() => InEnglish(() =>
        {
            Assert.Equal("Sat 7 Nov 2026, 19:40-22:15", MemoryWording.GameDateLine(At(19, 40, 5), At(22, 15, 59)));
            Assert.Equal("Sat 7 Nov 2026, 19:40", MemoryWording.GameDateLine(At(19, 40, 5), At(19, 40, 50)));
            Assert.Equal("Sat 7 Nov 2026, 23:40-00:35 next day", MemoryWording.GameDateLine(At(23, 40), At(0, 35, day: 8)));
            Assert.Equal("Sat 7 Nov 2026, 23:40 to 10 Nov, 00:35", MemoryWording.GameDateLine(At(23, 40), At(0, 35, day: 10)));
            Assert.Equal("Thu 31 Dec 2026, 23:40 to 2 Jan 2027, 00:35", MemoryWording.GameDateLine(new DateTime(2026, 12, 31, 23, 40, 0), new DateTime(2027, 1, 2, 0, 35, 0)));
            Assert.Equal("Sat 7 Nov 2026", MemoryWording.GameDay(At(12, 0)));
        });

        [Fact]
        public void PlayWindow_UsesOnlyTheTrophiesShownOnThePage() => InEnglish(() =>
        {
            var index = Index(entries: Game(
                10, "Invented Racer", "strong",
                Period("strong", new Trophy(Local, At(19, 40), "1"), new Trophy(Other, At(19, 41), "2")),
                Period("one-account", new Trophy(Other, At(21, 0), "3")),
                Period("same-period", new Trophy(Local, At(23, 0), "4"), new Trophy(Other, At(23, 30), "5"))));

            Assert.Equal("Sat 7 Nov 2026, 19:40-19:41", GameOf(index, 10, 0).DateText);
            Assert.Equal("Sat 7 Nov 2026, 23:00-23:30", GameOf(index, 10, 2).DateText);
        });

        // ---------------------------------------------------------------------------------------
        // Evidence block.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("strong", "\U0001F7E9")]
        [InlineData("supporting", "\U0001F7E8")]
        [InlineData("same-day", "\U0001F7E7")]
        public void EvidenceLevel_MapsToItsBlock_WithAOneLineMeaning(string level, string block)
        {
            Assert.Equal(block, MemoryWording.DayEvidenceBlock(level));

            var meaning = MemoryWording.DayEvidenceMeaning(level);
            Assert.False(string.IsNullOrWhiteSpace(meaning));
            Assert.DoesNotContain('\n', meaning!);
            Assert.NotNull(MemoryWording.DayEvidenceBlockDescription(level));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Strong")]
        [InlineData("same-period")]
        [InlineData("unknown")]
        public void UnknownOrMissingEvidenceLevel_ShowsNoBlock(string? level)
        {
            Assert.Null(MemoryWording.DayEvidenceBlock(level));
            Assert.Null(MemoryWording.DayEvidenceMeaning(level));
            Assert.Null(MemoryWording.DayEvidenceBlockDescription(level));
        }

        [Fact]
        public void GameWithoutDayLevel_HasNoBlock()
        {
            var game = GameOf(Index(entries: Game(10, "Invented Racer", null, Period("strong", new Trophy(Local, At(19, 40), "1")))), 10, 0);

            Assert.Null(game.EvidenceBlock);
            Assert.Null(game.EvidenceMeaning);
            Assert.Null(game.EvidenceDescription);
        }

        // ---------------------------------------------------------------------------------------
        // Together trophies.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void SameSetAndId_ByBoth_IsOneRow_AtTheEarlierTime() => InEnglish(() =>
        {
            var index = Index(
                definitions: [Definition("5", "Team Work", hidden: true)],
                entries: Game(
                    10, "Invented Racer", "strong",
                    Period(
                        "strong",
                        new Trophy(Local, At(19, 40, 0), "1"),
                        new Trophy(Other, At(19, 41, 12), "5"),
                        new Trophy(Local, At(19, 50, 0), "2"),
                        new Trophy(Local, At(19, 41, 5), "5"))));

            var game = GameOf(index, 10, 0);

            Assert.Equal(
                [
                    "Bronze trophy #1 | 19:40:00 · Alex",
                    "Team Work | 19:41:05 Alex · 19:41:12 Sam · hidden trophy",
                    "Bronze trophy #2 | 19:50:00 · Alex",
                ],
                Rows(game));

            var together = game.Trophies[1];
            Assert.True(together.IsTogether);
            Assert.Equal("Alex", together.First.ParticipantName);
            Assert.Equal("Sam", together.Second!.ParticipantName);
            Assert.Equal("icon-5.png", together.First.IconPath);
        });

        [Fact]
        public void SameIdInDifferentSets_IsNeverPaired() => InEnglish(() =>
        {
            var index = Index(entries: Game(
                10, "Invented Duo", "same-day",
                Period(
                    "same-period",
                    new Trophy(Local, At(19, 40), "3", SetId: "NPWR00001_00"),
                    new Trophy(Other, At(19, 41), "3", SetId: "NPWR00002_00"))));

            var game = GameOf(index, 10, 0);

            Assert.Equal(2, game.Trophies.Count);
            Assert.All(game.Trophies, t => Assert.False(t.IsTogether));
        });

        [Fact]
        public void OneParticipantsDuplicate_IsNotPaired()
        {
            var index = Index(entries: Game(
                10, "Invented Racer", "strong",
                Period("strong", new Trophy(Local, At(19, 40), "1"), new Trophy(Local, At(19, 45), "1"), new Trophy(Other, At(20, 0), "2"))));

            var game = GameOf(index, 10, 0);

            Assert.Equal(3, game.Trophies.Count);
            Assert.All(game.Trophies, t => Assert.False(t.IsTogether));
        }

        [Fact]
        public void ThreeWithTheSameSetAndId_StaySeparate()
        {
            var index = Index(entries: Game(
                10, "Invented Racer", "strong",
                Period("strong", new Trophy(Local, At(19, 40), "1"), new Trophy(Other, At(19, 41), "1"), new Trophy(Local, At(19, 42), "1"))));

            var game = GameOf(index, 10, 0);

            Assert.Equal(3, game.Trophies.Count);
            Assert.All(game.Trophies, t => Assert.False(t.IsTogether));
        }

        [Fact]
        public void PairsOnlyWithinTheShownTrophies()
        {
            // The same trophy earned by both, but in two different periods: a period's page shows one of them only.
            var index = Index(entries: Game(
                10, "Invented Racer", "supporting",
                Period("strong", new Trophy(Local, At(19, 40), "1"), new Trophy(Other, At(19, 40, 30), "2")),
                Period("same-period", new Trophy(Local, At(23, 0), "3"), new Trophy(Other, At(23, 5), "1"))));

            Assert.All(GameOf(index, 10, 0).Trophies, t => Assert.False(t.IsTogether));
            Assert.All(GameOf(index, 10, 1).Trophies, t => Assert.False(t.IsTogether));
        }

        [Fact]
        public void DifferentTimesRecord_PairsAcrossItsPeriods() => InEnglish(() =>
        {
            var index = Index(entries: Game(
                11, "Invented Puzzler", "supporting",
                Period("one-account", new Trophy(Other, At(10, 0), "6")),
                Period("one-account", new Trophy(Local, At(15, 0), "6"))));

            var game = GameOf(index, 11, null);

            Assert.Equal(["Bronze trophy #6 | 10:00:00 Sam · 15:00:00 Alex"], Rows(game));
        });

        [Fact]
        public void Pair_KeepsOrder_AndPlacesEachPairAtItsEarlierTrophy()
        {
            static ArchiveEntryItem Item(long id, long participant, string set, string item) => new()
            {
                EntryItemID = id,
                EntryID = 1,
                PeriodIndex = 0,
                ParticipantID = participant,
                SetId = set,
                ItemId = item,
                ItemType = "bronze",
                TimeUtc = DateTime.UnixEpoch.AddMinutes(id),
                TimeLocal = DateTime.UnixEpoch.AddMinutes(id),
            };

            var items = new[]
            {
                Item(1, Local, "S", "a"),
                Item(2, Other, "S", "b"),
                Item(3, Other, "S", "a"),
                Item(4, Local, "S", "c"),
                Item(5, Local, "S", "b"),
                Item(6, Other, "s", "c"),
            };

            var rows = TogetherTrophies.Pair(items);

            Assert.Equal(
                ["1+3", "2+5", "4", "6"],
                rows.Select(r => r.First.EntryItemID.ToString(CultureInfo.InvariantCulture)
                                 + (r.Second is { } s ? "+" + s.EntryItemID.ToString(CultureInfo.InvariantCulture) : string.Empty)));
        }
    }
}
