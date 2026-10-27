namespace Chatcheology.Data.Memories.Psn
{
    /// <summary>
    /// Splits one event's items into periods and labels each period from the source's own matching
    /// pairs, per the CH-42 design table. Organises timestamps only; no scoring.
    /// </summary>
    internal static class PsnPeriodBuilder
    {
        /// <summary>One source matching pair; a side is null when it could not be read.</summary>
        internal sealed record Pair((string ItemId, DateTime Local)? Local, (string ItemId, DateTime Local)? Other);

        internal static PsnRecord Build(
            string sourceKey,
            string title,
            string dayLevel,
            string? imageSourceUrl,
            string localSet,
            string otherSet,
            IReadOnlyList<(string Role, string ItemId, string Type, DateTime Utc, DateTime Local)> rawItems,
            IReadOnlyList<Pair> pairs,
            int periodGapMinutes)
        {
            var gap = TimeSpan.FromMinutes(periodGapMinutes);

            // Sorted by UTC; ties broken by role then item id so the order never depends on file order.
            var sorted = rawItems
                .OrderBy(item => item.Utc)
                .ThenBy(item => item.Role, StringComparer.Ordinal)
                .ThenBy(item => item.ItemId, StringComparer.Ordinal)
                .ToList();

            var periodOf = new int[sorted.Count];
            var period = 0;

            for (var i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Utc - sorted[i - 1].Utc > gap)
                {
                    period++;
                }

                periodOf[i] = period;
            }

            var periodCount = period + 1;

            // Locate each pair by (role, item id, local time). A side found zero or several times is
            // not located; a pair whose sides fall in different periods is split. Neither is guessed.
            var hasPair = new bool[periodCount];
            var hasClosePair = new bool[periodCount];
            var notLocated = 0;
            var split = 0;

            foreach (var pair in pairs)
            {
                var localIndex = Locate(sorted, MemoryRoles.Local, pair.Local);
                var otherIndex = Locate(sorted, MemoryRoles.Other, pair.Other);

                if (localIndex < 0 || otherIndex < 0)
                {
                    notLocated++;
                    continue;
                }

                if (periodOf[localIndex] != periodOf[otherIndex])
                {
                    split++;
                    continue;
                }

                var p = periodOf[localIndex];
                hasPair[p] = true;

                if ((sorted[localIndex].Utc - sorted[otherIndex].Utc).Duration()
                    <= TimeSpan.FromSeconds(PsnSharedActivityReader.StrongPairSeconds))
                {
                    hasClosePair[p] = true;
                }
            }

            var items = new List<PsnItem>(sorted.Count);

            for (var i = 0; i < sorted.Count; i++)
            {
                var (role, itemId, type, utc, local) = sorted[i];
                items.Add(new PsnItem(role, role == MemoryRoles.Local ? localSet : otherSet, itemId, type, utc, local, periodOf[i]));
            }

            var periods = new List<PsnPeriod>(periodCount);

            for (var p = 0; p < periodCount; p++)
            {
                var inPeriod = items.Where(item => item.PeriodIndex == p).ToList();
                var localCount = inPeriod.Count(item => item.Role == MemoryRoles.Local);
                var otherCount = inPeriod.Count - localCount;

                var label = localCount == 0 || otherCount == 0 ? PeriodLabels.OneAccount
                    : hasClosePair[p] ? PeriodLabels.Strong
                    : hasPair[p] ? PeriodLabels.Supporting
                    : PeriodLabels.SamePeriod;

                periods.Add(new PsnPeriod(
                    p,
                    inPeriod[0].TimeUtc,
                    inPeriod[^1].TimeUtc,
                    inPeriod.Min(item => item.TimeLocal),
                    inPeriod.Max(item => item.TimeLocal),
                    label,
                    localCount,
                    otherCount));
            }

            return new PsnRecord(sourceKey, title, dayLevel, imageSourceUrl, items, periods, pairs.Count, notLocated, split);
        }

        private static int Locate(
            List<(string Role, string ItemId, string Type, DateTime Utc, DateTime Local)> sorted,
            string role,
            (string ItemId, DateTime Local)? side)
        {
            if (side is not { } wanted)
            {
                return -1;
            }

            var found = -1;

            for (var i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].Role == role
                    && string.Equals(sorted[i].ItemId, wanted.ItemId, StringComparison.Ordinal)
                    && sorted[i].Local == wanted.Local)
                {
                    if (found >= 0)
                    {
                        return -1;
                    }

                    found = i;
                }
            }

            return found;
        }
    }
}
