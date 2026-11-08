using System.Text.Json;
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

        /// <summary>Games (by key) only in the old file, with or without events.</summary>
        public int GamesOnlyOld { get; internal set; }

        /// <summary>Games (by key) only in the new file, with or without events.</summary>
        public int GamesOnlyNew { get; internal set; }

        /// <summary>Games in both whose title differs.</summary>
        public int GamesDifferentTitle { get; internal set; }

        /// <summary>Games in both whose icon differs.</summary>
        public int GamesDifferentIcon { get; internal set; }

        /// <summary>Events in both with the same level, per level.</summary>
        public Dictionary<string, int> SameLevel { get; } = MemoriesDatabase.DayLevels.ToDictionary(level => level, _ => 0, StringComparer.Ordinal);

        /// <summary>Events in both with a different level, per (old level, new level).</summary>
        public Dictionary<(string Old, string New), int> DifferentLevel { get; } = [];

        /// <summary>Events only in the old file, per level.</summary>
        public Dictionary<string, int> OnlyOld { get; } = MemoriesDatabase.DayLevels.ToDictionary(level => level, _ => 0, StringComparer.Ordinal);

        /// <summary>Events only in the new file, per level.</summary>
        public Dictionary<string, int> OnlyNew { get; } = MemoriesDatabase.DayLevels.ToDictionary(level => level, _ => 0, StringComparer.Ordinal);

        /// <summary>Events in both with the same level whose trophies (role, set, id, type, UTC time) differ.</summary>
        public int SameLevelDifferentTrophies { get; internal set; }

        /// <summary>Events in both with the same level whose matching pairs (id, aTime, bTime), pairs not located or split differ.</summary>
        public int SameLevelDifferentPairs { get; internal set; }

        /// <summary>Events in both with the same level whose periods (label, start, end, counts) differ.</summary>
        public int SameLevelDifferentPeriods { get; internal set; }

        /// <summary>Events in both with the same level.</summary>
        public int SameLevelTotal => SameLevel.Values.Sum();

        /// <summary>Events in both with a different level.</summary>
        public int DifferentLevelTotal => DifferentLevel.Values.Sum();

        /// <summary>Events only in the old file.</summary>
        public int OnlyOldTotal => OnlyOld.Values.Sum();

        /// <summary>Events only in the new file.</summary>
        public int OnlyNewTotal => OnlyNew.Values.Sum();

        /// <summary>True only when the two files hold the same games and the same events in every compared respect.</summary>
        public bool Same =>
            OldGames == NewGames && GamesOnlyOld == 0 && GamesOnlyNew == 0 && GamesDifferentTitle == 0 && GamesDifferentIcon == 0
            && DifferentLevelTotal == 0 && OnlyOldTotal == 0 && OnlyNewTotal == 0
            && SameLevelDifferentTrophies == 0 && SameLevelDifferentPairs == 0 && SameLevelDifferentPeriods == 0;
    }

    /// <summary>
    /// Compares two shared-activity files, both already read and checked by
    /// <see cref="PsnSharedActivityReader"/> with the same prefixes. Games are matched by key, events by id.
    /// </summary>
    public static class PsnSharedActivityComparer
    {
        private sealed record GameFacts(string Title, string? Icon);

        private sealed record PairFacts(long Id, string ATime, string BTime);

        /// <summary>Compares the old file with the new one.</summary>
        /// <param name="oldActivity">The old file as the reader returned it.</param>
        /// <param name="oldJson">The old file's bytes (for games without events and the pairs themselves).</param>
        /// <param name="newActivity">The new file as the reader returned it.</param>
        /// <param name="newJson">The new file's bytes.</param>
        public static PsnCompareResult Compare(
            PsnActivity oldActivity, ReadOnlyMemory<byte> oldJson, PsnActivity newActivity, ReadOnlyMemory<byte> newJson)
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

            var (oldGames, oldPairs) = ReadRaw(oldJson);
            var (newGames, newPairs) = ReadRaw(newJson);

            foreach (var (key, old) in oldGames)
            {
                if (!newGames.TryGetValue(key, out var built))
                {
                    result.GamesOnlyOld++;
                    continue;
                }

                if (!string.Equals(old.Title, built.Title, StringComparison.Ordinal))
                {
                    result.GamesDifferentTitle++;
                }

                if (!string.Equals(old.Icon, built.Icon, StringComparison.Ordinal))
                {
                    result.GamesDifferentIcon++;
                }
            }

            result.GamesOnlyNew = newGames.Keys.Count(key => !oldGames.ContainsKey(key));

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

                if (!string.Equals(old.DayLevel, built.DayLevel, StringComparison.Ordinal))
                {
                    var pair = (old.DayLevel, built.DayLevel);
                    result.DifferentLevel[pair] = result.DifferentLevel.GetValueOrDefault(pair) + 1;
                    continue;
                }

                result.SameLevel[old.DayLevel]++;

                if (!SameTrophies(old, built))
                {
                    result.SameLevelDifferentTrophies++;
                }

                if (old.PairCount != built.PairCount
                    || old.PairsNotLocated != built.PairsNotLocated
                    || old.PairsSplit != built.PairsSplit
                    || !oldPairs[old.SourceKey].SequenceEqual(newPairs[built.SourceKey]))
                {
                    result.SameLevelDifferentPairs++;
                }

                if (!SamePeriods(old, built))
                {
                    result.SameLevelDifferentPeriods++;
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
            static IEnumerable<(string, string, string, string, DateTime)> Keys(PsnRecord record) =>
                record.Items
                    .Select(item => (item.Role, item.SetId, item.ItemId, item.ItemType, item.TimeUtc))
                    .Order();

            return a.Items.Count == b.Items.Count && Keys(a).SequenceEqual(Keys(b));
        }

        private static bool SamePeriods(PsnRecord a, PsnRecord b) =>
            a.Periods.Count == b.Periods.Count
            && a.Periods.Zip(b.Periods).All(pair =>
                pair.First.Label == pair.Second.Label
                && pair.First.StartUtc == pair.Second.StartUtc
                && pair.First.EndUtc == pair.Second.EndUtc
                && pair.First.LocalItemCount == pair.Second.LocalItemCount
                && pair.First.OtherItemCount == pair.Second.OtherItemCount);

        /// <summary>
        /// Reads the games (key, title, icon) and each event's matching pairs, sorted, from a file the reader
        /// has already accepted, so the shape is known to be valid.
        /// </summary>
        private static (Dictionary<string, GameFacts> Games, Dictionary<string, List<PairFacts>> Pairs) ReadRaw(ReadOnlyMemory<byte> json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var games = new Dictionary<string, GameFacts>(StringComparer.Ordinal);

            foreach (var game in Get(root, "games")!.Value.EnumerateArray())
            {
                var icon = Get(game, "icon");
                var iconText = icon is { ValueKind: JsonValueKind.String } && !string.IsNullOrWhiteSpace(icon.Value.GetString())
                    ? icon.Value.GetString()
                    : null;
                games.Add(Get(game, "key")!.Value.GetString()!, new GameFacts(Get(game, "game")!.Value.GetString()!, iconText));
            }

            var pairs = new Dictionary<string, List<PairFacts>>(StringComparer.Ordinal);

            foreach (var e in Get(root, "events")!.Value.EnumerateArray())
            {
                var list = Get(e, "matchingTrophies")!.Value.EnumerateArray()
                    .Select(pair => new PairFacts(
                        Get(pair, "id")!.Value.GetInt64(),
                        Get(pair, "aTime")!.Value.GetString()!,
                        Get(pair, "bTime")!.Value.GetString()!))
                    .OrderBy(pair => pair.Id).ThenBy(pair => pair.ATime, StringComparer.Ordinal).ThenBy(pair => pair.BTime, StringComparer.Ordinal)
                    .ToList();
                pairs.Add(Get(e, "id")!.Value.GetString()!, list);
            }

            return (games, pairs);
        }

        /// <summary>Case-insensitive lookup, as the reader matches field names.</summary>
        private static JsonElement? Get(JsonElement element, string name)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }

            return null;
        }
    }
}
