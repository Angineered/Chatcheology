namespace Chatcheology.Data.Memories.Psn
{
    /// <summary>Aggregate counts over a read PSN file. Counts and dates only.</summary>
    public sealed record PsnActivitySummary(
        int GameCount,
        int RecordCount,
        int ItemCount,
        int PeriodCount,
        IReadOnlyDictionary<string, int> PeriodsPerLabel,
        int TimelineCards,
        int RecordsWithNoSharedPeriod,
        int PairCount,
        int PairsNotLocated,
        int PairsSplit,
        IReadOnlyDictionary<int, int> RecordsPerYear,
        DateOnly? FirstDate,
        DateOnly? LastDate)
    {
        /// <summary>Builds the summary.</summary>
        public static PsnActivitySummary From(PsnActivity activity)
        {
            ArgumentNullException.ThrowIfNull(activity);

            var records = activity.Records;
            var periods = records.SelectMany(record => record.Periods).ToList();

            var perLabel = PeriodLabels.All.ToDictionary(
                label => label,
                label => periods.Count(period => period.Label == label),
                StringComparer.Ordinal);

            var perYear = new SortedDictionary<int, int>();

            foreach (var record in records)
            {
                var year = record.StartLocal.Year;
                perYear[year] = perYear.GetValueOrDefault(year) + 1;
            }

            return new PsnActivitySummary(
                activity.GameCount,
                records.Count,
                records.Sum(record => record.Items.Count),
                periods.Count,
                perLabel,
                periods.Count(period => period.IsShared),
                records.Count(record => !record.Periods.Any(period => period.IsShared)),
                records.Sum(record => record.PairCount),
                records.Sum(record => record.PairsNotLocated),
                records.Sum(record => record.PairsSplit),
                perYear,
                records.Count == 0 ? null : DateOnly.FromDateTime(records.Min(record => record.StartLocal)),
                records.Count == 0 ? null : DateOnly.FromDateTime(records.Max(record => record.EndLocal)));
        }
    }
}
