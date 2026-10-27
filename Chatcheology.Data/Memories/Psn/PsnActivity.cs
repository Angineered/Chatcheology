namespace Chatcheology.Data.Memories.Psn
{
    /// <summary>One earned trophy, mapped to a chat role, with the period it falls in.</summary>
    /// <param name="Role"><see cref="MemoryRoles.Local"/> or <see cref="MemoryRoles.Other"/>.</param>
    /// <param name="SetId">The account's trophy set ID for the game.</param>
    /// <param name="ItemId">The trophy ID as the source writes it.</param>
    /// <param name="ItemType">The trophy type as the source writes it.</param>
    /// <param name="TimeUtc">The source's <c>utc</c> value, whole seconds, kind UTC.</param>
    /// <param name="TimeLocal">The UTC time converted to the file's time zone (checked against <c>time</c>).</param>
    /// <param name="PeriodIndex">The period it belongs to, 0-based within the record.</param>
    public sealed record PsnItem(
        string Role,
        string SetId,
        string ItemId,
        string ItemType,
        DateTime TimeUtc,
        DateTime TimeLocal,
        int PeriodIndex);

    /// <summary>One activity period of a record: items chained while the gap is at most the threshold.</summary>
    public sealed record PsnPeriod(
        int Index,
        DateTime StartUtc,
        DateTime EndUtc,
        DateTime StartLocal,
        DateTime EndLocal,
        string Label,
        int LocalItemCount,
        int OtherItemCount)
    {
        /// <summary>True when both roles earned trophies in it: a timeline card.</summary>
        public bool IsShared => LocalItemCount > 0 && OtherItemCount > 0;
    }

    /// <summary>One source event (game-day record) ready to store as an Entry.</summary>
    /// <param name="SourceKey">The event <c>id</c>.</param>
    /// <param name="Title">The game's title.</param>
    /// <param name="DayLevel">The event <c>quality</c>, copied as-is.</param>
    /// <param name="ImageSourceUrl">The game's <c>icon</c>, or null.</param>
    /// <param name="Items">Both roles' items, sorted by UTC.</param>
    /// <param name="Periods">The periods, in time order.</param>
    /// <param name="PairCount">The event's <c>matchingTrophies</c> entries.</param>
    /// <param name="PairsNotLocated">Pairs whose items could not be found by role, item id and local time.</param>
    /// <param name="PairsSplit">Pairs whose two items fall in different periods.</param>
    public sealed record PsnRecord(
        string SourceKey,
        string Title,
        string DayLevel,
        string? ImageSourceUrl,
        IReadOnlyList<PsnItem> Items,
        IReadOnlyList<PsnPeriod> Periods,
        int PairCount,
        int PairsNotLocated,
        int PairsSplit)
    {
        /// <summary>First item, UTC.</summary>
        public DateTime StartUtc => Items[0].TimeUtc;

        /// <summary>Last item, UTC.</summary>
        public DateTime EndUtc => Items[^1].TimeUtc;

        /// <summary>Earliest local reading (equals the first item's except across a clock change).</summary>
        public DateTime StartLocal => Items.Min(item => item.TimeLocal);

        /// <summary>Latest local reading.</summary>
        public DateTime EndLocal => Items.Max(item => item.TimeLocal);
    }

    /// <summary>A whole PSN shared-activity file after reading, checking and splitting into periods.</summary>
    public sealed record PsnActivity(int GameCount, int PeriodGapMinutes, IReadOnlyList<PsnRecord> Records);
}
