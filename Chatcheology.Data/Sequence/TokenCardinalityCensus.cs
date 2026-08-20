namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C1 — how many distinct sequence tokens each relation on a singleton both-direction date
    /// carries, and the funnel from those dates to the strict order test.
    /// </summary>
    /// <remarks>
    /// One payload commonly survives as several physical occurrences carrying different tokens —
    /// Stage B1 found 804 same-source, same-asset, same-date groups where no two copies agreed — so a
    /// relation with several tokens is ordinary, not an error. No minimum, maximum, first,
    /// source-preferred or direction-preferred token is chosen to make such a relation comparable;
    /// it simply does not enter the strict test.
    /// </remarks>
    public sealed class TokenCardinalityCensus
    {
        /// <summary>Relations, on the eligible dates, carrying no supported token.</summary>
        public required int NoSupportedToken { get; init; }

        /// <summary>Relations carrying exactly one distinct supported token.</summary>
        public required int ExactlyOneDistinctToken { get; init; }

        /// <summary>Relations carrying several distinct supported tokens.</summary>
        public required int SeveralDistinctTokens { get; init; }

        /// <summary>Distinct-token counts in bands.</summary>
        public required SequenceBandCounts DistinctTokenCounts { get; init; }

        /// <summary>Dates holding exactly one cohort relation in each direction.</summary>
        public required int SingletonBothDirectionDateCount { get; init; }

        /// <summary>Of those, dates where a side carries no supported token.</summary>
        public required int DatesExcludedNoSupportedToken { get; init; }

        /// <summary>Dates where a side carries several distinct tokens.</summary>
        public required int DatesExcludedSeveralTokens { get; init; }

        /// <summary>
        /// Dates where both sides carry one token and the two tokens are equal, which is reported
        /// rather than forced into an order.
        /// </summary>
        public required int DatesExcludedEqualToken { get; init; }

        /// <summary>Dates surviving into the strict order test, one observation each.</summary>
        public required int StrictOrderableDateCount { get; init; }
    }
}
