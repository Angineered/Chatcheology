namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C4 — the exact weighted assignment count, kept as a secondary diagnostic.
    /// </summary>
    /// <remarks>
    /// The weighted count equals the unweighted <c>C(T, M)</c> exactly when every token position holds
    /// one asset, which Stage B1's archive-wide key figures suggest is nearly always true. So the two
    /// comparisons below isolate the entire contribution of multi-asset token positions, which is the
    /// reason this section exists at all.
    /// <para>
    /// No assignment is ever materialised: the count comes from a dynamic programme over token
    /// positions with <see cref="System.Numerics.BigInteger"/> state.
    /// </para>
    /// </remarks>
    public sealed class AssignmentCountCensus
    {
        public required int FeasibleGroupCount { get; init; }

        /// <summary>Feasible groups whose weighted count equals <c>C(T, M)</c>.</summary>
        public required int GroupsWhereWeightedEqualsUnweighted { get; init; }

        /// <summary>Feasible groups whose weighted count exceeds it.</summary>
        public required int GroupsWhereWeightedExceedsUnweighted { get; init; }

        /// <summary>Analysed token positions holding more than one compatible asset.</summary>
        public required int TokenPositionsWithSeveralAssets { get; init; }

        /// <summary>Groups holding at least one such position.</summary>
        public required int GroupsWithASeveralAssetPosition { get; init; }

        /// <summary>Feasible groups banded by their exact assignment count.</summary>
        public required AssignmentCountBandCounts AssignmentCounts { get; init; }

        /// <summary>Decimal digits in the largest single group's assignment count.</summary>
        public required int MaximumDecimalDigitCount { get; init; }

        /// <summary>Lower median decimal digit count among feasible groups.</summary>
        public required int MedianDecimalDigitCount { get; init; }
    }
}
