namespace Chatcheology.Data.Media
{
    /// <summary>Every observed token's behaviour, in ascending token order.</summary>
    /// <remarks>
    /// Published in full. The token set is small and carries no private information on its own, and a
    /// capped table would hide the curve shape this measurement exists to show.
    /// <para>
    /// The ascent figures are a mechanical description of the curve's monotonicity, not a test. A
    /// count of adjacent increases and the largest of them say how far the curve departs from
    /// declining without asserting what that departure means.
    /// </para>
    /// </remarks>
    public sealed class SequenceTokenCurve
    {
        /// <summary>One row per observed token, ascending.</summary>
        public required IReadOnlyList<SequenceTokenCurveRow> Rows { get; init; }

        /// <summary>Distinct tokens observed.</summary>
        public required int DistinctTokenCount { get; init; }

        /// <summary>The lowest observed token, or null when there is none.</summary>
        public required string? MinimumToken { get; init; }

        /// <summary>The highest observed token, or null when there is none.</summary>
        public required string? MaximumToken { get; init; }

        /// <summary>
        /// Whether the observed set is the complete inclusive range between those bounds.
        /// </summary>
        public required bool ObservedSetIsContiguous { get; init; }

        /// <summary>Device-group and date groups holding supported evidence.</summary>
        public required int TotalDeviceGroupDateGroups { get; init; }

        /// <summary>Groups containing the lowest possible token, <c>0000</c>.</summary>
        public required int GroupsContainingLowestToken { get; init; }

        /// <summary>Groups containing <c>0001</c>.</summary>
        public required int GroupsContainingSecondToken { get; init; }

        /// <summary>
        /// Adjacent pairs where the device-group-and-date count rises with the token.
        /// </summary>
        public required int AdjacentAscentCount { get; init; }

        /// <summary>The largest single such rise.</summary>
        public required int LargestAscent { get; init; }

        /// <summary>Tokens whose device-group-and-date count exceeds <c>0000</c>'s.</summary>
        public required int TokensExceedingLowestToken { get; init; }
    }
}
