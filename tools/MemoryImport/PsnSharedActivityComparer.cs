using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Psn;

namespace Chatcheology.Tools.MemoryImport
{
    /// <summary>What <see cref="PsnSharedActivityComparer"/> found. Counts only.</summary>
    public sealed class PsnCompareResult
    {
        /// <summary>Games in the old file.</summary>
        public int OldGames { get; internal set; }

        /// <summary>Games in the new file.</summary>
        public int NewGames { get; internal set; }

        /// <summary>Events in the old file.</summary>
        public int OldEvents { get; internal set; }

        /// <summary>Events in the new file.</summary>
        public int NewEvents { get; internal set; }

        /// <summary>Events in both with the same level, per level.</summary>
        public Dictionary<string, int> SameLevel { get; } = MemoriesDatabase.DayLevels.ToDictionary(level => level, _ => 0, StringComparer.Ordinal);

        /// <summary>Events in both with a different level, per (old level, new level).</summary>
        public Dictionary<(string Old, string New), int> DifferentLevel { get; } = [];

        /// <summary>Events only in the old file, per level.</summary>
        public Dictionary<string, int> OnlyOld { get; } = MemoriesDatabase.DayLevels.ToDictionary(level => level, _ => 0, StringComparer.Ordinal);

        /// <summary>Events only in the new file, per level.</summary>
        public Dictionary<string, int> OnlyNew { get; } = MemoriesDatabase.DayLevels.ToDictionary(level => level, _ => 0, StringComparer.Ordinal);

        /// <summary>Events in both with the same level whose trophies or matching-pair count differ.</summary>
        public int SameLevelDifferentTrophies { get; internal set; }

        /// <summary>Events in both whose game title differs.</summary>
        public int DifferentTitle { get; internal set; }

        /// <summary>Events in both whose game icon differs.</summary>
        public int DifferentIcon { get; internal set; }

        /// <summary>Events in both with the same level.</summary>
        public int SameLevelTotal => SameLevel.Values.Sum();

        /// <summary>Events in both with a different level.</summary>
        public int DifferentLevelTotal => DifferentLevel.Values.Sum();

        /// <summary>Events only in the old file.</summary>
        public int OnlyOldTotal => OnlyOld.Values.Sum();

        /// <summary>Events only in the new file.</summary>
        public int OnlyNewTotal => OnlyNew.Values.Sum();

        /// <summary>True when the two files hold the same events, levels, trophies, titles and icons.</summary>
        public bool Same =>
            DifferentLevelTotal == 0 && OnlyOldTotal == 0 && OnlyNewTotal == 0
            && SameLevelDifferentTrophies == 0 && DifferentTitle == 0 && DifferentIcon == 0;
    }

    /// <summary>
    /// Compares two shared-activity files, both read and checked by <see cref="PsnSharedActivityReader"/>
    /// with the same prefixes. Events are matched by their id.
    /// </summary>
    public static class PsnSharedActivityComparer
    {
        /// <summary>Compares <paramref name="oldActivity"/> with <paramref name="newActivity"/>.</summary>
        public static PsnCompareResult Compare(PsnActivity oldActivity, PsnActivity newActivity)
        {
            ArgumentNullException.ThrowIfNull(oldActivity);
            ArgumentNullException.ThrowIfNull(newActivity);

            var result = new PsnCompareResult
            {
                OldGames = oldActivity.GameCount,
                NewGames = newActivity.GameCount,
                OldEvents = oldActivity.Records.Count,
                NewEvents = newActivity.Records.Count,
            };

            var newByKey = newActivity.Records.ToDictionary(record => record.SourceKey, StringComparer.Ordinal);
            var oldKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var old in oldActivity.Records)
            {
                oldKeys.Add(old.SourceKey);

                if (!newByKey.TryGetValue(old.SourceKey, out var built))
                {
                    result.OnlyOld[old.DayLevel]++;
                    continue;
                }

                if (string.Equals(old.DayLevel, built.DayLevel, StringComparison.Ordinal))
                {
                    result.SameLevel[old.DayLevel]++;

                    if (!SameTrophies(old, built))
                    {
                        result.SameLevelDifferentTrophies++;
                    }
                }
                else
                {
                    var pair = (old.DayLevel, built.DayLevel);
                    result.DifferentLevel[pair] = result.DifferentLevel.GetValueOrDefault(pair) + 1;
                }

                if (!string.Equals(old.Title, built.Title, StringComparison.Ordinal))
                {
                    result.DifferentTitle++;
                }

                if (!string.Equals(old.ImageSourceUrl, built.ImageSourceUrl, StringComparison.Ordinal))
                {
                    result.DifferentIcon++;
                }
            }

            foreach (var built in newActivity.Records)
            {
                if (!oldKeys.Contains(built.SourceKey))
                {
                    result.OnlyNew[built.DayLevel]++;
                }
            }

            return result;
        }

        private static bool SameTrophies(PsnRecord a, PsnRecord b)
        {
            if (a.PairCount != b.PairCount || a.Items.Count != b.Items.Count)
            {
                return false;
            }

            static IEnumerable<(string, string, string, string, DateTime)> Keys(PsnRecord record) =>
                record.Items
                    .Select(item => (item.Role, item.SetId, item.ItemId, item.ItemType, item.TimeUtc))
                    .Order();

            return Keys(a).SequenceEqual(Keys(b));
        }
    }
}
