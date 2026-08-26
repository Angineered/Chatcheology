namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// One attachment's candidates arranged into the tiers of a partial order.
    /// </summary>
    /// <remarks>
    /// <strong>A rendering of a partial order, never a sorted list and never a score.</strong> The
    /// tier index carries no magnitude: it says that some candidate in an earlier tier is evidentially
    /// preferred to this one under the frozen rule, and nothing more. It does not say by how much, it
    /// says nothing at all about two candidates within one tier, and it cannot be turned into a number
    /// that ranks them.
    /// <para>
    /// Candidates within a tier are ordered by <c>MediaAssetID</c> ascending, for display stability
    /// only, carrying zero evidential meaning.
    /// </para>
    /// <para>
    /// An empty set is a legitimate and common answer — an attachment whose only evidence is
    /// adjacent-date context has no candidates at all, and remains unresolved.
    /// </para>
    /// </remarks>
    public sealed class Phase7RankedCandidateSet
    {
        /// <summary>The tiers, best-supported first.</summary>
        public required IReadOnlyList<IReadOnlyList<Phase7CandidateSupport>> Tiers { get; init; }

        /// <summary>How many candidates the set holds across all tiers.</summary>
        public int CandidateCount => Tiers.Sum(tier => tier.Count);
    }
}
