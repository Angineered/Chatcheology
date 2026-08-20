namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C6 — groups the hypothesis cannot satisfy, and by how much.
    /// </summary>
    /// <remarks>
    /// <c>T &lt; M</c> means the recovered supported sequence evidence cannot provide one distinct
    /// increasing token position per unresolved media message in that group. It does not prove the
    /// sequence model wrong, the messages invalid, media deleted, recovery incomplete or the direction
    /// evidence mistaken. Any of those may contribute, and this census cannot separate them.
    /// </remarks>
    public sealed class ImpossibleGroupCensus
    {
        public required int NoCompatibleCandidateAssetGroups { get; init; }

        public required int NoSupportedTokenPositionGroups { get; init; }

        public required int TooFewTokenPositionGroups { get; init; }

        /// <summary>How far short the too-few groups fall, banded.</summary>
        public required ShortfallBandCounts Shortfall { get; init; }

        public required int MessagesInImpossibleGroups { get; init; }

        /// <summary>Baseline compatible relations sitting in impossible groups.</summary>
        public required int BaselineRelationsInImpossibleGroups { get; init; }
    }
}
