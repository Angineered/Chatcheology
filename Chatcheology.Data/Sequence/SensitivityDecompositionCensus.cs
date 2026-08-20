namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C7 — the direction-agreeing sensitivity view, decomposed into the three effects it contains.
    /// </summary>
    /// <remarks>
    /// Filtering to occurrences with at least one direction-agreeing supporting copy moves two things at
    /// once: it removes token positions, and it can remove whole candidate assets. Pooling them into one
    /// "reduction" would report direction availability as sequence-order narrowing, so the two baselines
    /// are kept apart and the combined figure is labelled as combined.
    /// <para>
    /// The primary analysis is unaffected: it intentionally includes unknown-direction token-bearing
    /// copies, and this view never replaces it.
    /// </para>
    /// </remarks>
    public sealed class SensitivityDecompositionCensus
    {
        // Effect 1 — availability. Caused by requiring agreeing evidence, not by ordering.

        /// <summary>Baseline relations from the frozen compatible sets, over analysed groups.</summary>
        public required int FrozenCandidateRelations { get; init; }

        /// <summary>The same relations once assets without an agreeing-backed token are dropped.</summary>
        public required int AgreeingTokenEligibleCandidateRelations { get; init; }

        /// <summary>Token positions removed by the filter, aggregated over analysed groups.</summary>
        public required int TokenPositionsRemovedByFiltering { get; init; }

        /// <summary>Asset/group pairs losing every token position under the filter.</summary>
        public required int AssetsLosingEveryTokenPosition { get; init; }

        // Effect 2 — sequence order inside the sensitivity population.

        /// <summary>Feasibility under the filtered token positions.</summary>
        public required FeasibilityCounts Feasibility { get; init; }

        /// <summary>Slack under the filtered token positions.</summary>
        public required SequenceSlackDistribution Slack { get; init; }

        /// <summary>
        /// Narrowing measured inside the sensitivity population, against the
        /// agreeing-token-eligible baseline. This is the isolated sequence-order effect.
        /// </summary>
        public required CandidateNarrowingCensus SequenceOrderEffect { get; init; }

        // Effect 3 — combined, and labelled as such.

        /// <summary>
        /// COMBINED effect: frozen baseline relations in sensitivity-feasible groups, against the final
        /// sensitivity candidate relations. Attributable to direction-agreeing token availability
        /// <em>and</em> the sequence-order hypothesis together, never to ordering alone.
        /// </summary>
        public required int CombinedFrozenBaselineRelations { get; init; }

        /// <summary>The final sensitivity candidate relations for those same groups.</summary>
        public required int CombinedFinalCandidateRelations { get; init; }
    }
}
