using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Memories;
using Chatcheology.Mobile.Search;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// Memory layers in the conversation (CH-42 step 3): placement, ordering, filters, days outside the
    /// chat, details wording, search, and that with every layer off the conversation is exactly as before.
    /// </summary>
    /// <remarks>Invented games, trophies and names only.</remarks>
    public sealed class MemoryLayerTests : IDisposable
    {
        private const long Local = MemoryArchiveBuilder.Local;
        private const long Other = MemoryArchiveBuilder.Other;

        private readonly TemporaryFolder _root = new();
        private readonly MemorySettings _store = new();

        public void Dispose() => _root.Dispose();

        private static DateTime At(int day, int hour, int minute, int second = 0) => new(2025, 3, day, hour, minute, second);

        /// <summary>
        /// Five messages on 10 and 12 March 2025, and memories before, between, among and after them:
        /// <list type="bullet">
        /// <item>10: "Invented Racer" on 10 March, day level supporting: a strong period at message 2's
        /// second, a one-account period, a same-period period after message 3.</item>
        /// <item>11: "Invented Puzzler" on 11 March (no messages): two one-account periods, one per
        /// account (a "different times" line).</item>
        /// <item>12: "Invented Racer" on 1 March, before the chat: one strong period.</item>
        /// <item>13: "Invented Racer" on 12 March: one supporting period between messages 4 and 5.</item>
        /// <item>14: "Invented Solo" on 12 March: one account only (no row at all).</item>
        /// <item>20: "Beach week", personal, 20 to 25 March (after the chat), with an image and links.</item>
        /// </list>
        /// </summary>
        private static MemoryArchiveBuilder Sample(Action<MemoryArchiveBuilder>? more = null)
        {
            var builder = new MemoryArchiveBuilder();

            builder.Message(At(10, 20, 0));
            builder.Message(At(10, 20, 10));
            builder.Message(At(10, 20, 20));
            builder.Message(At(12, 9, 0));
            builder.Message(At(12, 9, 5));

            builder.Game(
                10, "Invented Racer", "supporting", true,
                new PeriodSpec("strong", new TrophySpec(Local, At(10, 20, 10), "1"), new TrophySpec(Other, At(10, 20, 10, 30), "2")),
                new PeriodSpec("one-account", new TrophySpec(Other, At(10, 20, 40), "3")),
                new PeriodSpec("same-period", new TrophySpec(Local, At(10, 21, 0), "4"), new TrophySpec(Other, At(10, 21, 30), "5", "silver")));

            builder.Game(
                11, "Invented Puzzler", "same-day", false,
                new PeriodSpec("one-account", new TrophySpec(Local, At(11, 10, 0), "6")),
                new PeriodSpec("one-account", new TrophySpec(Other, At(11, 15, 0), "7")));

            builder.Game(
                12, "Invented Racer", "strong", false,
                new PeriodSpec("strong", new TrophySpec(Local, At(1, 18, 0), "8"), new TrophySpec(Other, At(1, 18, 0, 20), "9")));

            builder.Game(
                13, "Invented Racer", "strong", false,
                new PeriodSpec("supporting", new TrophySpec(Local, At(12, 9, 2), "10"), new TrophySpec(Other, At(12, 9, 3), "11")));

            builder.Game(
                14, "Invented Solo", "same-day", false,
                new PeriodSpec("one-account", new TrophySpec(Local, At(12, 9, 4), "12")));

            builder.Memory(20, "Beach week", At(20, 0, 0), At(25, 0, 0), summary: "Invented days by the sea.", image: true);
            builder.Link(20, toMessage: 2);
            builder.Link(20, toEntry: 10);

            builder.Definition("setA", "1", "Invented Start", hidden: true, icon: true, detail: "Begin the invented race.");

            more?.Invoke(builder);

            return builder;
        }

        private ArchiveSession Open(MemoryArchiveBuilder builder, Action<ArchiveSettings, string>? configure = null, OwnMemories.OwnMemoryStore? own = null)
        {
            var library = new ArchiveLibrary(Path.Combine(_root.Path, "library-" + Guid.NewGuid().ToString("N")));
            var settings = new ArchiveSettings(_store);

            var staging = library.CreateStagingFolder();
            builder.Write(staging);

            var outcome = library.Install(staging);
            Assert.True(outcome.Installed, string.Join(", ", outcome.Validation.FirstProblems));

            var archive = library.ListArchives(settings).Single();
            configure?.Invoke(settings, archive.ContentsSha256!);

            return ArchiveSession.Open(archive, settings, ownStore: own);
        }

        private static ArchiveSession AllOn(ArchiveSession session)
        {
            session.SetLayerShown(MemoryArchiveBuilder.Gaming, true);
            session.SetLayerShown(MemoryArchiveBuilder.Personal, true);

            return session;
        }

        private static List<string> Describe(IEnumerable<ConversationRow> rows) =>
            rows.Select(row => row switch
            {
                DateSeparatorRow separator => "D " + separator.Day.ToString("MM-dd", CultureInfo.InvariantCulture),
                MessageRow message => "M " + message.SequenceNumber.ToString(CultureInfo.InvariantCulture),
                PeriodCardRow card => "P " + card.Key.EntryID.ToString(CultureInfo.InvariantCulture) + ":" + card.Key.PeriodIndex!.Value.ToString(CultureInfo.InvariantCulture),
                DifferentTimesRow line => "X " + line.Key.EntryID.ToString(CultureInfo.InvariantCulture),
                EntryCardRow card => "E " + card.Key.EntryID.ToString(CultureInfo.InvariantCulture),
                _ => "?",
            }).ToList();

        // ---------------------------------------------------------------------------------------
        // Layers off: exactly today's conversation.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void WithEveryLayerOff_TheConversationIsExactlyAsWithoutMemories()
        {
            using var session = Open(Sample());
            session.SetLayerShown(MemoryArchiveBuilder.Personal, false);

            var timeline = session.CreateTimeline();
            timeline.OpenAtLatest();

            var plain = new ConversationTimeline(session.Reader, session.Me, session.LowQuality, session.Stars);
            plain.OpenAtLatest();

            Assert.Equal(Describe(plain.Rows), Describe(timeline.Rows));
            Assert.Equal(["D 03-10", "M 1", "M 2", "M 3", "D 03-12", "M 4", "M 5"], Describe(timeline.Rows));
            Assert.Equal(session.ReadDays(), session.ReadCalendarDays());
            Assert.Equal(timeline.OpenAtDay(new DateOnly(2025, 3, 11)), plain.OpenAtDay(new DateOnly(2025, 3, 11)));
            Assert.Empty(session.FindMemories("Racer"));
        }

        [Fact]
        public void WithEveryLayerOff_IncludingMyMemories_TheConversationIsExactlyAsWithoutMemories()
        {
            // CH-54: the phone owner's own memories, one before, one among and one after the messages, then every layer
            // off, the My memories switch included.
            var own = new OwnMemories.OwnMemoryStore(Path.Combine(_root.Path, "files-" + Guid.NewGuid().ToString("N")));
            own.Add(OwnDraft("Invented Racer night", new DateOnly(2025, 3, 1)));
            own.Add(OwnDraft("Invented Racer rematch", new DateOnly(2025, 3, 11)));
            own.Add(OwnDraft("Invented Racer final", new DateOnly(2025, 4, 2)));

            using var session = Open(Sample(), own: own);
            var before = session.CreateTimeline();
            before.OpenAtLatest();
            Assert.Equal(3, before.Rows.OfType<EntryCardRow>().Count(row => row.Key.IsOwn));

            session.SetLayerShown(MemoryArchiveBuilder.Personal, false);
            session.OwnLayerShown = false;

            var timeline = session.CreateTimeline();
            timeline.OpenAtLatest();

            var plain = new ConversationTimeline(session.Reader, session.Me, session.LowQuality, session.Stars);
            plain.OpenAtLatest();

            Assert.Equal(Describe(plain.Rows), Describe(timeline.Rows));
            Assert.Equal(["D 03-10", "M 1", "M 2", "M 3", "D 03-12", "M 4", "M 5"], Describe(timeline.Rows));
            Assert.Equal(session.ReadDays(), session.ReadCalendarDays());
            Assert.Equal(timeline.OpenAtDay(new DateOnly(2025, 3, 11)), plain.OpenAtDay(new DateOnly(2025, 3, 11)));
            Assert.Equal(timeline.OpenAtDay(new DateOnly(2025, 3, 1)), plain.OpenAtDay(new DateOnly(2025, 3, 1)));
            Assert.Empty(session.FindMemories("Racer"));
        }

        private static OwnMemories.OwnMemoryDraft OwnDraft(string title, DateOnly day) => new()
        {
            Title = title,
            Start = day,
            Precision = MemoryDatePrecision.Day,
        };

        [Fact]
        public void TheDefaults_AreTheCollections_SharedGamingOff_PersonalOn()
        {
            using var session = Open(Sample());

            Assert.Equal([false, true], session.MemoryLayers.Select(l => l.IsShown));
            Assert.Equal(MemoryFilters.All, session.MemoryLayers[0].Filters.Select(f => f.Filter));
            Assert.All(session.MemoryLayers[0].Filters, f => Assert.True(f.IsOn));
            Assert.Empty(session.MemoryLayers[1].Filters);

            var timeline = session.CreateTimeline();
            timeline.OpenAtLatest();

            Assert.Equal(["D 03-10", "M 1", "M 2", "M 3", "D 03-12", "M 4", "M 5", "D 03-20", "E 20"], Describe(timeline.Rows));
        }

        // ---------------------------------------------------------------------------------------
        // Placement.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Rows_ArePlacedAtTheirTimes_AfterMessagesOfTheSameSecond_WithDaysOutsideTheChat()
        {
            using var session = AllOn(Open(Sample()));

            var timeline = session.CreateTimeline();
            timeline.OpenAtLatest();

            Assert.Equal(
                [
                    "D 03-01", "P 12:0",
                    "D 03-10", "M 1", "M 2", "P 10:0", "M 3", "P 10:2",
                    "D 03-11", "X 11",
                    "D 03-12", "M 4", "P 13:0", "M 5",
                    "D 03-20", "E 20",
                ],
                Describe(timeline.Rows));

            // Messages keep their numbers and order.
            Assert.Equal([1, 2, 3, 4, 5], timeline.Rows.OfType<MessageRow>().Select(m => m.SequenceNumber));
        }

        [Fact]
        public void APeriodCard_ShowsTitleDateCountsTimesLabelAndSource_WithoutDurations()
        {
            using var session = AllOn(Open(Sample()));

            var timeline = session.CreateTimeline();
            timeline.OpenAtLatest();

            var strong = timeline.Rows.OfType<PeriodCardRow>().Single(c => c.Key == new MemoryKey(10, 0));
            Assert.Equal("Invented Racer", strong.Title);
            Assert.Equal(DateSeparatorRow.DayText(new DateOnly(2025, 3, 10)), strong.DateText);
            Assert.Equal(["Alex: 1 trophy, at 20:10", "Sam: 1 trophy, at 20:10"], strong.ParticipantLines);
            Assert.Equal("This period: strong", strong.LabelText);
            Assert.Equal("PSN trophies", strong.SourceText);
            Assert.True(strong.HasImage);
            Assert.True(File.Exists(strong.ImagePath));

            var samePeriod = timeline.Rows.OfType<PeriodCardRow>().Single(c => c.Key == new MemoryKey(10, 2));
            Assert.Equal("This period: same period", samePeriod.LabelText);
            Assert.Equal(["Alex: 1 trophy, at 21:00", "Sam: 1 trophy, at 21:30"], samePeriod.ParticipantLines);

            Assert.Equal("This period: supporting", timeline.Rows.OfType<PeriodCardRow>().Single(c => c.Key.EntryID == 13).LabelText);

            var line = Assert.Single(timeline.Rows.OfType<DifferentTimesRow>());
            Assert.Equal("Both earned trophies in Invented Puzzler today, at different times", line.Text);
            Assert.Equal(At(11, 10, 0), line.TimeLocal);

            var every = string.Join(" ", timeline.Rows.OfType<PeriodCardRow>().SelectMany(c => c.ParticipantLines.Append(c.LabelText)));
            Assert.DoesNotContain("together", every, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("minute", every, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hour", every, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void OneAccountPeriods_AreNeverCards()
        {
            using var session = AllOn(Open(Sample()));

            var timeline = session.CreateTimeline();
            timeline.OpenAtLatest();

            Assert.DoesNotContain(timeline.Rows.OfType<PeriodCardRow>(), c => c.Key == new MemoryKey(10, 1));
            Assert.DoesNotContain(timeline.Rows.OfType<MemoryRow>(), r => r.Key.EntryID == 14);
            Assert.DoesNotContain(timeline.Rows.OfType<PeriodCardRow>(), c => c.Key.EntryID == 11);
        }

        [Fact]
        public void APersonalCard_ShowsItsRangeAtItsStart_WithImageAndSummary()
        {
            using var session = Open(Sample());

            var timeline = session.CreateTimeline();
            timeline.OpenAtLatest();

            var card = timeline.Rows.OfType<EntryCardRow>().Single();
            Assert.Equal("Beach week", card.Title);
            Assert.Equal(
                DateSeparatorRow.DayText(new DateOnly(2025, 3, 20)) + " to " + DateSeparatorRow.DayText(new DateOnly(2025, 3, 25)),
                card.DateText);
            Assert.Equal("Invented days by the sea.", card.Summary);
            Assert.True(card.HasImage);
            Assert.Equal(new DateOnly(2025, 3, 20), card.Day);
            Assert.Single(timeline.Rows.OfType<MemoryRow>());
        }

        // ---------------------------------------------------------------------------------------
        // Filters.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Filters_HideTheirLabelsAndTheDifferentTimesLines_Only()
        {
            using var session = AllOn(Open(Sample()));

            session.SetMemoryFilter(MemoryArchiveBuilder.Gaming, MemoryFilters.Strong, false);
            session.SetMemoryFilter(MemoryArchiveBuilder.Gaming, MemoryFilters.DifferentTimes, false);

            var timeline = session.CreateTimeline();
            timeline.OpenAtLatest();

            Assert.Equal(
                ["D 03-10", "M 1", "M 2", "M 3", "P 10:2", "D 03-12", "M 4", "P 13:0", "M 5", "D 03-20", "E 20"],
                Describe(timeline.Rows));

            session.SetMemoryFilter(MemoryArchiveBuilder.Gaming, MemoryFilters.Strong, true);
            session.SetMemoryFilter(MemoryArchiveBuilder.Gaming, MemoryFilters.Supporting, false);
            session.SetMemoryFilter(MemoryArchiveBuilder.Gaming, MemoryFilters.SamePeriod, false);

            timeline = session.CreateTimeline();
            timeline.OpenAtLatest();

            Assert.Equal(["P 12:0", "P 10:0", "E 20"], Describe(timeline.Rows.OfType<MemoryRow>()));
            Assert.Equal([true, false, false, false], session.MemoryLayers[0].Filters.Select(f => f.IsOn));
        }

        // ---------------------------------------------------------------------------------------
        // Paging, separators and the date picker with rows outside the chat.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Paging_BothWays_GivesTheSameRows_WithEveryMemoryOnce_AndMessageNumbersUnchanged()
        {
            // 400 messages, 20 a day from 1 April 2025 (pages of 150 join inside days), and memory rows
            // before the chat, on a day of messages, on a day without messages, and after the chat.
            var builder = new MemoryArchiveBuilder { GamingShownByDefault = true };
            var start = new DateTime(2025, 4, 1, 8, 0, 0);

            for (var i = 0; i < 400; i++)
            {
                // Every fifth day is left without messages.
                var day = i / 20;
                builder.Message(start.AddDays(day + day / 4).AddMinutes(i % 20 * 10));
            }

            builder.Game(30, "Invented Early", "strong", false,
                new PeriodSpec("strong", new TrophySpec(Local, new DateTime(2025, 3, 20, 10, 0, 0), "1"), new TrophySpec(Other, new DateTime(2025, 3, 20, 10, 0, 5), "2")));
            builder.Game(31, "Invented Middle", "strong", false,
                new PeriodSpec("strong", new TrophySpec(Local, start.AddDays(3).AddMinutes(25), "3"), new TrophySpec(Other, start.AddDays(3).AddMinutes(26), "4")));
            builder.Game(32, "Invented Gap", "supporting", false,
                new PeriodSpec("supporting", new TrophySpec(Local, start.AddDays(4).AddHours(5), "5"), new TrophySpec(Other, start.AddDays(4).AddHours(6), "6")));
            builder.Memory(33, "Invented Trip", new DateTime(2025, 9, 1));
            builder.Memory(34, "Invented Earlier Trip", new DateTime(2025, 2, 1));

            using var session = Open(builder);

            var backward = session.CreateTimeline();
            backward.OpenAtLatest();

            while (backward.PrepareEarlier() is { } earlier)
            {
                backward.Prepend(earlier);
            }

            var forward = session.CreateTimeline();
            forward.OpenAt(1);

            while (forward.PrepareLater() is { } later)
            {
                forward.Append(later);
            }

            var rows = Describe(backward.Rows);

            Assert.Equal(Describe(forward.Rows), rows);
            Assert.Equal(Enumerable.Range(1, 400), backward.Rows.OfType<MessageRow>().Select(m => m.SequenceNumber));
            Assert.Equal(["E 34", "P 30:0", "P 31:0", "P 32:0", "E 33"], Describe(backward.Rows.OfType<MemoryRow>()));

            // Each day once, in order, and every row after its day's separator.
            var separators = backward.Rows.OfType<DateSeparatorRow>().Select(s => s.Day).ToList();
            Assert.Equal(separators.Distinct().Order(), separators);

            DateOnly? current = null;

            foreach (var row in backward.Rows)
            {
                if (row is DateSeparatorRow separator)
                {
                    current = separator.Day;
                }
                else
                {
                    Assert.Equal(current, row.Day);
                }
            }

            Assert.Equal(new DateOnly(2025, 2, 1), separators[0]);
            Assert.Equal(new DateOnly(2025, 9, 1), separators[^1]);
            Assert.Contains(new DateOnly(2025, 4, 5), separators);
        }

        [Fact]
        public void TheDatePicker_OffersDaysWithOnlyMemoryRows_AndJumpsToThem()
        {
            using var session = AllOn(Open(Sample()));

            var days = session.ReadCalendarDays();

            Assert.Equal(
                [new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 10), new DateOnly(2025, 3, 11), new DateOnly(2025, 3, 12), new DateOnly(2025, 3, 20)],
                days);

            // On this day stays message-only.
            Assert.Equal([new DateOnly(2025, 3, 10), new DateOnly(2025, 3, 12)], session.ReadDays());

            var calendar = new DayCalendar(days);
            Assert.True(calendar.HasMessages(new DateOnly(2025, 3, 11)));

            var timeline = session.CreateTimeline();

            var before = timeline.OpenAtDay(new DateOnly(2025, 3, 1));
            Assert.Equal(new MemoryKey(12, 0), Assert.IsType<PeriodCardRow>(timeline.Rows[before]).Key);
            Assert.IsType<DateSeparatorRow>(timeline.Rows[before - 1]);

            var gap = timeline.OpenAtDay(new DateOnly(2025, 3, 11));
            Assert.IsType<DifferentTimesRow>(timeline.Rows[gap]);

            var after = timeline.OpenAtDay(new DateOnly(2025, 3, 15));
            Assert.Equal(new MemoryKey(20, null), Assert.IsType<EntryCardRow>(timeline.Rows[after]).Key);

            var messages = timeline.OpenAtDay(new DateOnly(2025, 3, 12));
            Assert.Equal(4, Assert.IsType<MessageRow>(timeline.Rows[messages]).SequenceNumber);

            Assert.Equal(-1, timeline.OpenAtDay(new DateOnly(2025, 4, 1)));
        }

        [Fact]
        public void OpenAtMemory_FindsAVisibleRow_AndRefusesAHiddenOne()
        {
            using var session = AllOn(Open(Sample()));

            var timeline = session.CreateTimeline();
            var index = timeline.OpenAtMemory(new MemoryKey(20, null));
            Assert.Equal(new MemoryKey(20, null), Assert.IsType<EntryCardRow>(timeline.Rows[index]).Key);

            Assert.True(timeline.OpenAtMemory(new MemoryKey(12, 0)) >= 0);
            Assert.Equal(-1, timeline.OpenAtMemory(new MemoryKey(10, 1)));

            session.SetLayerShown(MemoryArchiveBuilder.Personal, false);
            Assert.Equal(-1, session.CreateTimeline().OpenAtMemory(new MemoryKey(20, null)));
        }

        // ---------------------------------------------------------------------------------------
        // Details.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Details_ListThePeriodsTrophies_WithDefinitionNamesOrTheFallback_ThenDayEvidenceAndOtherPeriods()
        {
            using var session = AllOn(Open(Sample()));

            var details = session.MemoryDetails(new MemoryKey(10, 0))!;

            Assert.Equal("Invented Racer", details.Title);
            Assert.Equal(["Alex: 1 trophy, at 20:10", "Sam: 1 trophy, at 20:10", "This period: strong"], details.Lines);

            Assert.Equal(2, details.Items.Count);
            var named = details.Items[0];
            Assert.Equal("20:10:00", named.TimeText);
            Assert.Equal("Alex", named.ParticipantName);
            Assert.Equal("Invented Start", named.Name);
            Assert.True(named.IsHidden);
            Assert.Equal("Begin the invented race.", named.Detail);
            Assert.True(named.HasIcon);
            Assert.True(File.Exists(named.IconPath));

            var fallback = details.Items[1];
            Assert.Equal("20:10:30", fallback.TimeText);
            Assert.Equal("Sam", fallback.ParticipantName);
            Assert.Equal("Bronze trophy #2", fallback.Name);
            Assert.False(fallback.HasIcon);

            // The day's level is named differently from the period label, and never combined with it.
            Assert.Equal("Day evidence: supporting", details.DayEvidenceText);
            Assert.Equal(["at 20:40 · one account: Sam 1 trophy", "21:00 to 21:30 · same period: Alex 1 trophy, Sam 1 trophy"], details.OtherPeriods);
            Assert.Equal("PSN trophies", details.SourceText);
            Assert.Null(details.ShareText);

            Assert.Equal("Silver trophy #5", session.MemoryDetails(new MemoryKey(10, 2))!.Items[1].Name);
        }

        [Fact]
        public void Details_OfADifferentTimesLine_ShowEveryTrophyAndItsDayEvidence()
        {
            using var session = AllOn(Open(Sample()));

            var details = session.MemoryDetails(new MemoryKey(11, null))!;

            Assert.Equal(["Both earned trophies in Invented Puzzler today, at different times"], details.Lines);
            Assert.Equal(["10:00:00", "15:00:00"], details.Items.Select(i => i.TimeText));
            Assert.Equal(["Bronze trophy #6", "Bronze trophy #7"], details.Items.Select(i => i.Name));
            Assert.Equal("Day evidence: same-day", details.DayEvidenceText);
            Assert.Equal(["at 10:00 · one account: Alex 1 trophy", "at 15:00 · one account: Sam 1 trophy"], details.OtherPeriods);
        }

        [Fact]
        public void Details_OfAPersonalMemory_ShowItsLinks()
        {
            using var session = Open(Sample());

            var details = session.MemoryDetails(new MemoryKey(20, null))!;

            Assert.Equal("Invented days by the sea.", details.Summary);
            Assert.Empty(details.DayEvidenceText);
            Assert.Empty(details.Items);
            Assert.True(details.HasImage);
            Assert.Equal(2, details.Links.Count);
            Assert.Equal(2, details.Links[0].SequenceNumber);
            Assert.Equal("A message on " + DateSeparatorRow.DayText(new DateOnly(2025, 3, 10)) + ", 20:10", details.Links[0].Text);
            Assert.Equal(10, details.Links[1].EntryID);
            Assert.StartsWith("Invented Racer, ", details.Links[1].Text, StringComparison.Ordinal);

            // A link to an entry whose layer is off has no row to open.
            Assert.Null(session.Memories.FirstRowOf(10, session.MemoryView));
            session.SetLayerShown(MemoryArchiveBuilder.Gaming, true);
            Assert.Equal(new MemoryKey(10, 0), session.Memories.FirstRowOf(10, session.MemoryView));
        }

        // ---------------------------------------------------------------------------------------
        // Search.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void EntryTitles_AreSearchable_OnlyWhileTheirLayerIsOn()
        {
            using var session = Open(Sample());

            Assert.Empty(session.FindMemories("racer"));
            Assert.Equal([new MemoryKey(20, null)], session.FindMemories("BEACH").Select(h => h.Key));

            session.SetLayerShown(MemoryArchiveBuilder.Gaming, true);

            var hits = session.FindMemories("racer");
            Assert.Equal([new MemoryKey(13, 0), new MemoryKey(10, 0), new MemoryKey(12, 0)], hits.Select(h => h.Key));
            Assert.All(hits, h => Assert.Equal("Shared gaming", h.CollectionName));
            Assert.Equal(new MatchRange(9, 5), hits[0].Highlights.Single());

            // A filtered-out row is not opened: the entry's first visible row is, or the entry is not found.
            session.SetMemoryFilter(MemoryArchiveBuilder.Gaming, MemoryFilters.Strong, false);
            Assert.Equal([new MemoryKey(13, 0), new MemoryKey(10, 2)], session.FindMemories("racer").Select(h => h.Key));

            session.SetLayerShown(MemoryArchiveBuilder.Personal, false);
            Assert.Empty(session.FindMemories("beach"));
        }

        // ---------------------------------------------------------------------------------------
        // CH-33: an older export's "(file attached)" first line.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AFileAttachedFirstLine_IsHiddenLikeTheMediaPlaceholder_CaptionShown()
        {
            var builder = new MemoryArchiveBuilder();
            builder.Message(At(10, 9, 0), "IMG-0001.jpg (file attached)\nan invented caption", Other, ArchiveAttachmentState.Unmatched);
            builder.Message(At(10, 9, 1), "IMG-0002.jpg (file attached)", Other, ArchiveAttachmentState.Unmatched);
            builder.Message(At(10, 9, 2), "IMG-0003.jpg (file attached)", Other);
            builder.Message(At(10, 9, 3), "plain text", Other, ArchiveAttachmentState.Unmatched);

            using var session = Open(builder);
            var timeline = session.CreateTimeline();
            timeline.OpenAtLatest();

            var messages = timeline.Rows.OfType<MessageRow>().ToList();

            Assert.Equal("an invented caption", messages[0].Text);
            Assert.Equal("an invented caption", messages[0].ShareText);
            Assert.False(messages[1].HasText);
            Assert.Null(messages[1].ShareText);

            // Without an attachment the line is the sender's own text, kept.
            Assert.Equal("IMG-0003.jpg (file attached)", messages[2].Text);
            Assert.Equal("plain text", messages[3].Text);

            var search = ConversationSearch.Build(session.Reader);
            Assert.Equal(0, search.Find("IMG-0001").TotalMatches);
            Assert.Equal(0, search.Find("IMG-0002").TotalMatches);
            Assert.Equal(1, search.Find("caption").TotalMatches);
        }

        private static ArchiveMessage Attached(string text, params string?[] names) => new()
        {
            MessageID = 1,
            SequenceNumber = 1,
            DateTimeLocal = At(10, 9, 0),
            Kind = ArchiveMessageKind.User,
            SenderParticipantID = Other,
            Text = text,
            IsForwarded = null,
            ForwardScore = null,
            Attachments = names.Select((name, index) => new ArchiveAttachment
            {
                AttachmentID = 10 + index,
                MessageID = 1,
                Ordinal = index + 1,
                State = name is null ? ArchiveAttachmentState.Unmatched : ArchiveAttachmentState.Restored,
                MediaSha256 = name is null ? null : new string('a', 64),
                OriginalFileName = name,
                IsForwarded = null,
                ForwardScore = null,
                LowQualityCopyOfAttachmentID = null,
                Basis = null,
            }).ToList(),
            Source = ArchiveMessageSource.Export,
        };

        [Theory]
        [InlineData(" (file attached)\ncaption")]
        [InlineData("   (file attached)\ncaption")]
        [InlineData("a/b (file attached)\ncaption")]
        [InlineData("a\\b (file attached)\ncaption")]
        public void AFileAttachedLine_ThatIsNotAFileName_StaysVisible(string text)
        {
            Assert.Equal(text, MessageRow.PresentedText(Attached(text, [null])));
        }

        [Fact]
        public void AFileAttachedLine_NamingAnotherFileThanTheAttachment_StaysVisible()
        {
            const string Text = "IMG-0009.jpg (file attached)\ncaption";

            Assert.Equal(Text, MessageRow.PresentedText(Attached(Text, "IMG-0001.jpg")));
            Assert.Equal("caption", MessageRow.PresentedText(Attached("IMG-0001.jpg (file attached)\ncaption", "IMG-0001.jpg")));
            Assert.Equal("caption", MessageRow.PresentedText(Attached("IMG-0002.jpg (file attached)\ncaption", [null])));
        }

        // ---------------------------------------------------------------------------------------
        // Settings.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void LayerSwitchesAndFilters_ArePerArchive_AndForgottenWithIt()
        {
            var settings = new ArchiveSettings(_store);
            const string A = "a1";
            const string B = "b2";

            Assert.False(settings.GetLayerShown(A, 1, shownByDefault: false));
            Assert.True(settings.GetLayerShown(A, 2, shownByDefault: true));

            settings.SetLayerShown(A, 1, true);
            settings.SetLayerShown(A, 2, false);
            settings.SetMemoryFilter(A, 1, MemoryFilters.SamePeriod, false);

            Assert.True(settings.GetLayerShown(A, 1, shownByDefault: false));
            Assert.False(settings.GetLayerShown(A, 2, shownByDefault: true));
            Assert.False(settings.GetMemoryFilter(A, 1, MemoryFilters.SamePeriod));
            Assert.True(settings.GetMemoryFilter(A, 1, MemoryFilters.Strong));
            Assert.False(settings.GetLayerShown(B, 1, shownByDefault: false));
            Assert.True(settings.GetMemoryFilter(B, 1, MemoryFilters.SamePeriod));

            settings.SetMemoryFilter(A, 1, MemoryFilters.SamePeriod, true);
            Assert.True(settings.GetMemoryFilter(A, 1, MemoryFilters.SamePeriod));

            settings.Forget(A);
            Assert.False(settings.GetLayerShown(A, 1, shownByDefault: false));
            Assert.Empty(_store.Values);
        }

        [Fact]
        public void AnArchiveWithoutCollections_HasNoMemories()
        {
            var builder = new ArchiveBuilder();
            builder.Add("hello");

            var library = new ArchiveLibrary(Path.Combine(_root.Path, "v1"));
            var settings = new ArchiveSettings(_store);
            var staging = library.CreateStagingFolder();
            builder.Write(staging);
            library.Install(staging);

            using var session = ArchiveSession.Open(library.ListArchives(settings).Single(), settings);

            Assert.False(session.Memories.HasMemories);
            Assert.Empty(session.MemoryLayers);
            Assert.Equal(session.ReadDays(), session.ReadCalendarDays());
        }
    }
}
