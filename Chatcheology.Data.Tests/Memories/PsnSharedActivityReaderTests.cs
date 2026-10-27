using System.Text.Json.Nodes;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Psn;
using Chatcheology.Tests.Memories;
using static Chatcheology.Tests.Memories.SyntheticPsnFile;

namespace Chatcheology.Data.Tests.Memories
{
    public class PsnSharedActivityReaderTests
    {
        private static PsnActivity Read(SyntheticPsnFile file, string local = LocalPrefix, string other = OtherPrefix, int gap = 60) =>
            PsnSharedActivityReader.Parse(file.ToBytes(), local, other, gap);

        private static SyntheticPsnFile OneEvent(List<SyntheticTrophy> local, List<SyntheticTrophy> other, List<SyntheticPair>? pairs = null, SyntheticGame? game = null)
        {
            var file = new SyntheticPsnFile();
            file.Games.Add(game ?? Quest);
            file.Events.Add(new SyntheticEvent { Game = game ?? Quest, Local = local, Other = other, Pairs = pairs ?? [] });
            return file;
        }

        [Fact]
        public void Standard_ReadsRecordsItemsAndRoles()
        {
            var activity = Read(Standard());

            Assert.Equal(2, activity.GameCount);
            Assert.Equal(2, activity.Records.Count);

            var quest = activity.Records[0];
            Assert.Equal("NPWR99999_00|NPWR99999_00|2015-03-01", quest.SourceKey);
            Assert.Equal("Example Quest", quest.Title);
            Assert.Equal("strong", quest.DayLevel);
            Assert.Equal(Quest.Icon, quest.ImageSourceUrl);
            Assert.Equal(4, quest.Items.Count);
            Assert.Equal(2, quest.Items.Count(i => i.Role == MemoryRoles.Local));
            Assert.All(quest.Items, i => Assert.Equal(DateTimeKind.Utc, i.TimeUtc.Kind));

            var racer = activity.Records[1];
            Assert.Equal("NPWR88888_00", racer.Items.Single(i => i.Role == MemoryRoles.Local).SetId);
            Assert.Equal("NPWR77777_00", racer.Items.Single(i => i.Role == MemoryRoles.Other).SetId);
        }

        [Fact]
        public void Schema_AnyOtherIsRefused()
        {
            var file = Standard();
            file.Schema = "chatcheology-psn-shared-activity-v2";

            var exception = Assert.Throws<FormatException>(() => Read(file));
            Assert.Contains("v1", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Schema_MissingIsRefused()
        {
            var file = Standard();
            file.Tamper = root => root.Remove("schema");

            Assert.Throws<FormatException>(() => Read(file));
        }

        [Fact]
        public void NotJson_IsRefused() =>
            Assert.Throws<FormatException>(() => PsnSharedActivityReader.Parse("{ nope"u8.ToArray(), LocalPrefix, OtherPrefix));

        [Fact]
        public void Prefixes_AreCaseInsensitive()
        {
            var activity = Read(Standard(), "ALPHA", "Beta");

            Assert.Equal(2, activity.Records.Count);
        }

        [Fact]
        public void Prefixes_SwappedSwapTheRoles()
        {
            var activity = Read(Standard(), OtherPrefix, LocalPrefix);

            Assert.Equal("NPWR77777_00", activity.Records[1].Items.Single(i => i.Role == MemoryRoles.Local).SetId);
        }

        [Fact]
        public void Prefix_Unknown_StopsAtTheFirstGame_WithoutNamingIt()
        {
            var exception = Assert.Throws<FormatException>(() => Read(Standard(), "gamma", OtherPrefix));

            Assert.Contains("Game at index 0", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("gamma", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Prefix_MissingFromOneEvent_StopsWithItsIndex()
        {
            var file = Standard();
            file.Tamper = root => root["events"]![1]!.AsObject().Remove("betaTrophies");

            var exception = Assert.Throws<FormatException>(() => Read(file));
            Assert.Contains("Event at index 1", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("beta", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Prefix_MissingFromOneGame_StopsWithItsIndex()
        {
            var file = Standard();
            file.Tamper = root => root["games"]![1]!.AsObject().Remove("alphaTrophySet");

            var exception = Assert.Throws<FormatException>(() => Read(file));
            Assert.Contains("Game at index 1", exception.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("alpha", "alpha")]
        [InlineData("alpha", "ALPHA")]
        [InlineData("", "beta")]
        [InlineData("al pha", "beta")]
        public void Prefix_SameTwiceOrNotPlain_IsRefused(string local, string other) =>
            Assert.Throws<ArgumentException>(() => Read(Standard(), local, other));

        [Fact]
        public void Time_UtcIsConvertedToTheFileZone()
        {
            var activity = Read(OneEvent([Trophy("1", Utc(2015, 3, 1, 22, 30, 15))], [Trophy("2", Utc(2015, 3, 1, 22, 31, 0))]));
            var item = activity.Records[0].Items[0];

            Assert.Equal(new DateTime(2015, 3, 1, 22, 30, 15, DateTimeKind.Utc), item.TimeUtc);
            Assert.Equal(new DateTime(2015, 3, 2, 0, 30, 15), item.TimeLocal);
            Assert.Equal(DateTimeKind.Unspecified, item.TimeLocal.Kind);
        }

        [Fact]
        public void Time_SourceTimeWithOffsetIsAccepted()
        {
            var trophy = new SyntheticTrophy("1", "bronze", Utc(2015, 3, 1, 10, 0, 0), "2015-03-01T12:00:00+02:00");

            var activity = Read(OneEvent([trophy], []));

            Assert.Equal(new DateTime(2015, 3, 1, 12, 0, 0), activity.Records[0].Items[0].TimeLocal);
        }

        [Theory]
        [InlineData("2015-03-01 12:00:01")]
        [InlineData("2015-03-01 10:00:00")]
        [InlineData("2015-03-01T12:00:00+01:00")]
        [InlineData("1 March 2015")]
        public void Time_MismatchStopsWithTheEventIndex(string sourceTime)
        {
            var file = Standard();
            file.Events.Add(new SyntheticEvent
            {
                Game = Quest,
                Date = "2015-03-02",
                Local = [new SyntheticTrophy("9", "gold", Utc(2015, 3, 1, 10, 0, 0), sourceTime)],
            });

            var exception = Assert.Throws<FormatException>(() => Read(file));
            Assert.Contains("Event at index 2", exception.Message, StringComparison.Ordinal);
            Assert.Contains("does not equal", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Time_UtcWithoutZoneIsRefused()
        {
            var file = Standard();
            file.Tamper = root => root["events"]![0]!["alphaTrophies"]![0]!["utc"] = "2015-03-01T18:00:00";

            Assert.Throws<FormatException>(() => Read(file));
        }

        [Fact]
        public void Timezone_UnknownIsRefused()
        {
            var file = Standard();
            file.TimeZone = "Nowhere/Invented";

            Assert.Throws<FormatException>(() => Read(file));
        }

        [Fact]
        public void Event_WithNoGameIsRefused()
        {
            var file = Standard();
            file.Games.RemoveAt(1);

            var exception = Assert.Throws<FormatException>(() => Read(file));
            Assert.Contains("Event at index 1", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Event_DuplicateIdIsRefused()
        {
            var file = Standard();
            file.Events.Add(file.Events[0]);

            Assert.Throws<FormatException>(() => Read(file));
        }

        [Fact]
        public void Event_WithoutMatchingTrophiesFieldIsRefused()
        {
            var file = Standard();
            file.Tamper = root => root["events"]![0]!.AsObject().Remove("matchingTrophies");

            Assert.Throws<FormatException>(() => Read(file));
        }

        // Gap rule

        [Fact]
        public void Gap_ExactlySixtyMinutesChains()
        {
            var activity = Read(OneEvent(
                [Trophy("1", Utc(2015, 3, 1, 10, 0, 0))],
                [Trophy("2", Utc(2015, 3, 1, 11, 0, 0))]));

            var period = Assert.Single(activity.Records[0].Periods);
            Assert.Equal(PeriodLabels.SamePeriod, period.Label);
        }

        [Fact]
        public void Gap_SixtyMinutesAndOneSecondSplits()
        {
            var activity = Read(OneEvent(
                [Trophy("1", Utc(2015, 3, 1, 10, 0, 0))],
                [Trophy("2", Utc(2015, 3, 1, 11, 0, 1))]));

            var periods = activity.Records[0].Periods;
            Assert.Equal(2, periods.Count);
            Assert.All(periods, p => Assert.Equal(PeriodLabels.OneAccount, p.Label));
        }

        [Fact]
        public void Gap_IsMeasuredToThePreviousItem_AcrossBothAccounts()
        {
            // 10:00 a, 10:50 b, 11:40 a: each gap 50 min, so one period of 1h40 despite >60 min overall.
            // 13:00 b: 80 min gap, new period. 13:30 b, 15:00 a: 90 min gap, third period.
            var activity = Read(OneEvent(
                [Trophy("1", Utc(2015, 3, 1, 10, 0, 0)), Trophy("2", Utc(2015, 3, 1, 11, 40, 0)), Trophy("3", Utc(2015, 3, 1, 15, 0, 0))],
                [Trophy("4", Utc(2015, 3, 1, 10, 50, 0)), Trophy("5", Utc(2015, 3, 1, 13, 0, 0)), Trophy("6", Utc(2015, 3, 1, 13, 30, 0))]));

            var record = activity.Records[0];
            Assert.Equal(3, record.Periods.Count);
            Assert.Equal((2, 1), (record.Periods[0].LocalItemCount, record.Periods[0].OtherItemCount));
            Assert.Equal((0, 2), (record.Periods[1].LocalItemCount, record.Periods[1].OtherItemCount));
            Assert.Equal((1, 0), (record.Periods[2].LocalItemCount, record.Periods[2].OtherItemCount));
            Assert.Equal(new DateTime(2015, 3, 1, 12, 0, 0), record.Periods[0].StartLocal);
            Assert.Equal(new DateTime(2015, 3, 1, 13, 40, 0), record.Periods[0].EndLocal);
            Assert.Equal([0, 0, 0, 1, 1, 2], record.Items.Select(i => i.PeriodIndex));
        }

        [Fact]
        public void Gap_OtherThresholdIsHonoured()
        {
            var activity = Read(OneEvent(
                [Trophy("1", Utc(2015, 3, 1, 10, 0, 0))],
                [Trophy("2", Utc(2015, 3, 1, 10, 31, 0))]), gap: 30);

            Assert.Equal(2, activity.Records[0].Periods.Count);
            Assert.Equal(30, activity.PeriodGapMinutes);
        }

        // Labels

        [Fact]
        public void Label_StrongAtExactlySixtySeconds()
        {
            var a = Trophy("1", Utc(2015, 3, 1, 10, 0, 0));
            var b = Trophy("1", Utc(2015, 3, 1, 10, 1, 0));

            var activity = Read(OneEvent([a], [b], [new SyntheticPair(a, b)]));

            Assert.Equal(PeriodLabels.Strong, Assert.Single(activity.Records[0].Periods).Label);
        }

        [Fact]
        public void Label_SupportingAtSixtyOneSeconds()
        {
            var a = Trophy("1", Utc(2015, 3, 1, 10, 0, 0));
            var b = Trophy("1", Utc(2015, 3, 1, 10, 1, 1));

            var activity = Read(OneEvent([a], [b], [new SyntheticPair(a, b)]));

            Assert.Equal(PeriodLabels.Supporting, Assert.Single(activity.Records[0].Periods).Label);
        }

        [Fact]
        public void Label_SamePeriodWithoutPairs_AndOneAccount()
        {
            var activity = Read(OneEvent(
                [Trophy("1", Utc(2015, 3, 1, 10, 0, 0)), Trophy("2", Utc(2015, 3, 1, 14, 0, 0))],
                [Trophy("3", Utc(2015, 3, 1, 10, 20, 0))]));

            var periods = activity.Records[0].Periods;
            Assert.Equal([PeriodLabels.SamePeriod, PeriodLabels.OneAccount], periods.Select(p => p.Label));
            Assert.True(periods[0].IsShared);
            Assert.False(periods[1].IsShared);
        }

        [Fact]
        public void Label_StrongDaysLaterPeriodWithoutAClosePairIsNotStrong()
        {
            var record = Read(Standard()).Records[0];

            Assert.Equal("strong", record.DayLevel);
            Assert.Equal([PeriodLabels.Strong, PeriodLabels.Supporting], record.Periods.Select(p => p.Label));
        }

        [Fact]
        public void Label_StrongDaysLaterPeriodWithNoPairAtAllIsSamePeriod()
        {
            var a1 = Trophy("1", Utc(2015, 3, 1, 10, 0, 0));
            var b1 = Trophy("1", Utc(2015, 3, 1, 10, 0, 10));

            var activity = Read(OneEvent(
                [a1, Trophy("2", Utc(2015, 3, 1, 20, 0, 0))],
                [b1, Trophy("3", Utc(2015, 3, 1, 20, 5, 0))],
                [new SyntheticPair(a1, b1)]));

            Assert.Equal([PeriodLabels.Strong, PeriodLabels.SamePeriod], activity.Records[0].Periods.Select(p => p.Label));
        }

        [Fact]
        public void Label_PairSplitAcrossPeriodsIsCounted_AndLabelsNeitherPeriod()
        {
            // Same trophy id earned 3 hours apart: a source pair, but the gap splits it.
            var a = Trophy("1", Utc(2015, 3, 1, 10, 0, 0));
            var b = Trophy("1", Utc(2015, 3, 1, 13, 0, 0));
            var a2 = Trophy("2", Utc(2015, 3, 1, 13, 10, 0));

            var record = Read(OneEvent([a, a2], [b], [new SyntheticPair(a, b)])).Records[0];

            Assert.Equal(1, record.PairsSplit);
            Assert.Equal(0, record.PairsNotLocated);
            Assert.Equal([PeriodLabels.OneAccount, PeriodLabels.SamePeriod], record.Periods.Select(p => p.Label));
        }

        [Fact]
        public void Label_PairNotLocatedIsCounted_AndNeverGuessed()
        {
            var a = Trophy("1", Utc(2015, 3, 1, 10, 0, 0));
            var b = Trophy("1", Utc(2015, 3, 1, 10, 0, 5));
            var wrongTime = a with { Time = "2015-03-01 12:00:01" };

            var record = Read(OneEvent([a], [b], [new SyntheticPair(wrongTime, b)])).Records[0];

            Assert.Equal(1, record.PairsNotLocated);
            Assert.Equal(PeriodLabels.SamePeriod, Assert.Single(record.Periods).Label);
        }

        [Fact]
        public void Label_PairWithUnknownTrophyIdIsNotLocated()
        {
            var a = Trophy("1", Utc(2015, 3, 1, 10, 0, 0));
            var b = Trophy("1", Utc(2015, 3, 1, 10, 0, 5));
            var unknown = Trophy("99", Utc(2015, 3, 1, 10, 0, 0));

            var record = Read(OneEvent([a], [b], [new SyntheticPair(unknown, b)])).Records[0];

            Assert.Equal(1, record.PairsNotLocated);
        }

        [Fact]
        public void Label_DifferentSetGameWithoutPairsIsNeverStrong()
        {
            var record = Read(Standard()).Records[1];

            Assert.Equal(0, record.PairCount);
            Assert.All(record.Periods, p => Assert.Equal(PeriodLabels.OneAccount, p.Label));

            // Same trophy ids close in time in a different-set game: still no pair, so same-period.
            var close = Read(OneEvent(
                [Trophy("1", Utc(2015, 3, 1, 10, 0, 0))],
                [Trophy("1", Utc(2015, 3, 1, 10, 0, 1))],
                game: Racer)).Records[0];

            Assert.Equal(PeriodLabels.SamePeriod, Assert.Single(close.Periods).Label);
        }

        [Fact]
        public void Pairs_NestedShapeIsReadToo()
        {
            var file = Standard();
            file.NestedPairs = true;

            var record = Read(file).Records[0];

            Assert.Equal(0, record.PairsNotLocated);
            Assert.Equal([PeriodLabels.Strong, PeriodLabels.Supporting], record.Periods.Select(p => p.Label));
        }

        [Fact]
        public void Summary_CountsCardsLabelsYearsAndDates()
        {
            var summary = PsnActivitySummary.From(Read(Standard()));

            Assert.Equal(2, summary.GameCount);
            Assert.Equal(2, summary.RecordCount);
            Assert.Equal(6, summary.ItemCount);
            Assert.Equal(4, summary.PeriodCount);
            Assert.Equal(1, summary.PeriodsPerLabel[PeriodLabels.Strong]);
            Assert.Equal(1, summary.PeriodsPerLabel[PeriodLabels.Supporting]);
            Assert.Equal(0, summary.PeriodsPerLabel[PeriodLabels.SamePeriod]);
            Assert.Equal(2, summary.PeriodsPerLabel[PeriodLabels.OneAccount]);
            Assert.Equal(2, summary.TimelineCards);
            Assert.Equal(1, summary.RecordsWithNoSharedPeriod);
            Assert.Equal(2, summary.PairCount);
            Assert.Equal(new Dictionary<int, int> { [2015] = 1, [2016] = 1 }, summary.RecordsPerYear);
            Assert.Equal(new DateOnly(2015, 3, 1), summary.FirstDate);
            Assert.Equal(new DateOnly(2016, 7, 4), summary.LastDate);
        }

        [Fact]
        public void UnusedFieldsAreIgnored()
        {
            var file = Standard();
            file.Tamper = root => root["reviewExport"] = new JsonArray("anything");

            Assert.Equal(2, Read(file).Records.Count);
        }
    }
}
