namespace Chatcheology.Data.Media
{
    /// <summary>
    /// Starting values, contiguity and gaps for the groups of one scope in one size band.
    /// </summary>
    /// <remarks>
    /// Banded by distinct-token group size on purpose. A group holding one or two tokens is contiguous
    /// by arithmetic, and those groups are numerous enough that an unbanded contiguity headline would be
    /// an artefact of counting them.
    /// <para>
    /// <b>Every figure here is descriptive.</b> A gap means only that a number inside the observed
    /// minimum-to-maximum range is absent from the recovered supported set. It does not mean a missing
    /// attachment, a missing file, a deleted message or a failed recovery. And because acquisition is
    /// incomplete, gap behaviour cannot by itself validate or refute a counter hypothesis: a high gap
    /// rate is equally consistent with a perfect daily counter observed through partial acquisition.
    /// This section describes the archive; it discriminates between no hypotheses.
    /// </para>
    /// <para>
    /// <see cref="GroupsWhereMaximumPlusOneEqualsDistinctCount"/> is the strong indicator. The distinct
    /// count can never exceed <c>max - min + 1</c>, so that equality implies both a minimum of
    /// <c>0000</c> and no internal gaps at once.
    /// </para>
    /// </remarks>
    public sealed class ContinuityByGroupSize
    {
        /// <summary>The group scope described.</summary>
        public required ScopeLevel Scope { get; init; }

        /// <summary>The size band described.</summary>
        public required GroupSizeBand Band { get; init; }

        /// <summary>Groups in this band.</summary>
        public required int GroupCount { get; init; }

        /// <summary>Groups whose lowest token is <c>0000</c>.</summary>
        public required int GroupsStartingAtLowestToken { get; init; }

        /// <summary>Groups whose lowest token is <c>0001</c>.</summary>
        public required int GroupsStartingAtSecondToken { get; init; }

        /// <summary>Groups whose lowest token is higher than <c>0001</c>.</summary>
        public required int GroupsStartingHigher { get; init; }

        /// <summary>Observed minima as exact values, ascending.</summary>
        public required IReadOnlyList<ValueCount> ObservedMinima { get; init; }

        /// <summary>Groups whose observed tokens fill their own range.</summary>
        public required int GroupsWithNoInternalMissingTokens { get; init; }

        /// <summary>Groups missing at least one value inside their own range.</summary>
        public required int GroupsWithInternalMissingTokens { get; init; }

        /// <summary>Groups missing no values.</summary>
        public required int GapCountZero { get; init; }

        /// <summary>Groups missing exactly one.</summary>
        public required int GapCountOne { get; init; }

        /// <summary>Groups missing exactly two.</summary>
        public required int GapCountTwo { get; init; }

        /// <summary>Groups missing three to five.</summary>
        public required int GapCountThreeToFive { get; init; }

        /// <summary>Groups missing six to ten.</summary>
        public required int GapCountSixToTen { get; init; }

        /// <summary>Groups missing more than ten.</summary>
        public required int GapCountMoreThanTen { get; init; }

        /// <summary>Distinct tokens observed across these groups.</summary>
        public required long TotalObservedDistinctTokens { get; init; }

        /// <summary>Values absent from inside these groups' observed ranges.</summary>
        public required long TotalUnobservedValuesInsideRanges { get; init; }

        /// <summary>The most missing values any one group has.</summary>
        public required int MaximumInternalGapCount { get; init; }

        /// <summary>Groups that are a complete run from <c>0000</c> with no gaps.</summary>
        public required int GroupsWhereMaximumPlusOneEqualsDistinctCount { get; init; }

        /// <summary>Groups contiguous within their range but not starting at <c>0000</c>.</summary>
        public required int GroupsContiguousButNotStartingAtLowestToken { get; init; }
    }
}
