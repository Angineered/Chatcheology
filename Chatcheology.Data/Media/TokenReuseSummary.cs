namespace Chatcheology.Data.Media
{
    /// <summary>How often one partition's tokens recur across different calendar dates.</summary>
    /// <remarks>
    /// Heavy reuse of the same token across dates weighs against the sequence being globally unique.
    /// Reported pooled and per partition, because a pooled figure over two handsets would describe the
    /// union of two numberings rather than either of them.
    /// <para>
    /// <see cref="PartitionID"/> is null for the pooled row.
    /// </para>
    /// </remarks>
    public sealed class TokenReuseSummary
    {
        /// <summary>Whether the row is pooled, per device group, or per source.</summary>
        public required ScopeLevel Scope { get; init; }

        /// <summary>The device group or source, or null for the pooled row.</summary>
        public required long? PartitionID { get; init; }

        /// <summary>Tokens seen on exactly one date within this partition.</summary>
        public required int TokensOnOneDateOnly { get; init; }

        /// <summary>Tokens seen on several.</summary>
        public required int TokensOnSeveralDates { get; init; }

        /// <summary>Distinct dates per token within this partition.</summary>
        public required CountSummary DistinctDatesPerToken { get; init; }
    }
}
