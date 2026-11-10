using Chatcheology.Archive;

namespace Chatcheology.Mobile.Memories
{
    /// <summary>
    /// One row of a game memory's Trophies list (CH-67): one trophy, or a together trophy (the same trophy, same set,
    /// earned by both participants within the memory's shown trophies) as one row with both times.
    /// </summary>
    /// <param name="First">The trophy, or the earlier of the two; its name, detail and icon are the row's.</param>
    /// <param name="Second">The later of the two for a together trophy; otherwise <see langword="null"/>.</param>
    public sealed record GameTrophyLine(MemoryItemLine First, MemoryItemLine? Second)
    {
        /// <summary>Whether the row is a together trophy with both times.</summary>
        public bool IsTogether => Second is not null;
    }

    /// <summary>
    /// Works out together trophies for a game memory's details (CH-67), from the trophies already in the archive.
    /// </summary>
    /// <remarks>
    /// Two shown trophies are one together row only when they have the same set id and the same item id (ordinal,
    /// as stored), belong to two different participants, and are the only two shown trophies with that set and id.
    /// Trophies of different sets are never paired, even with the same item id. Anything else (one participant's
    /// duplicate, three or more with the same set and id) stays as separate rows: missing is better than wrong.
    /// The row is placed where the earlier one is; the order of everything else is kept.
    /// </remarks>
    public static class TogetherTrophies
    {
        /// <summary>
        /// The rows for <paramref name="ordered"/> (the shown trophies, already in time order): each trophy once,
        /// a together pair as one row at the earlier trophy's place.
        /// </summary>
        public static IReadOnlyList<(ArchiveEntryItem First, ArchiveEntryItem? Second)> Pair(IReadOnlyList<ArchiveEntryItem> ordered)
        {
            ArgumentNullException.ThrowIfNull(ordered);

            var groups = new Dictionary<(string SetId, string ItemId), List<int>>();

            for (var i = 0; i < ordered.Count; i++)
            {
                var key = (ordered[i].SetId, ordered[i].ItemId);

                if (!groups.TryGetValue(key, out var list))
                {
                    groups[key] = list = [];
                }

                list.Add(i);
            }

            // For each pair: the earlier index points at the later one; the later one is left out.
            var partner = new Dictionary<int, int>();
            var absorbed = new HashSet<int>();

            foreach (var list in groups.Values)
            {
                if (list.Count == 2 && ordered[list[0]].ParticipantID != ordered[list[1]].ParticipantID)
                {
                    partner[list[0]] = list[1];
                    absorbed.Add(list[1]);
                }
            }

            var rows = new List<(ArchiveEntryItem, ArchiveEntryItem?)>(ordered.Count - absorbed.Count);

            for (var i = 0; i < ordered.Count; i++)
            {
                if (absorbed.Contains(i))
                {
                    continue;
                }

                rows.Add((ordered[i], partner.TryGetValue(i, out var later) ? ordered[later] : null));
            }

            return rows;
        }
    }
}
