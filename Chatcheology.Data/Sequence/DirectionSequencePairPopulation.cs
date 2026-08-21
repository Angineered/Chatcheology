namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C0 — how many <c>(scope key, local date)</c> pairs one scope holds, and where each of them
    /// stopped.
    /// </summary>
    /// <remarks>
    /// The pair universe is every scope key paired with every date on which either that scope key holds
    /// a supported token position or the conversation holds an unresolved attachment. Nothing is
    /// dropped from it silently: a pair that cannot carry the question is counted under the reason.
    /// <para>
    /// Direction conflict is the one exclusion that acts on a whole pair. A logical position whose
    /// copies disagree about direction removes its pair from every primary population, because
    /// preferring a source would invent evidence and dropping only the conflicting position would
    /// rewrite the very order the stage exists to measure.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequencePairPopulation
    {
        /// <summary>Scope keys — sources or device groups — this scope has.</summary>
        public required int ScopeKeyCount { get; init; }

        /// <summary>Pairs in the universe.</summary>
        public required int PairCount { get; init; }

        /// <summary>Pairs whose date carries at least one message symbol.</summary>
        public required int PairsWithMessageSymbols { get; init; }

        /// <summary>Pairs whose scope emits at least one direction-labelled token position.</summary>
        public required int PairsWithTokenPositions { get; init; }

        /// <summary>Logical positions at this scope whose copies disagree about direction.</summary>
        public required int ConflictingLogicalPositionCount { get; init; }

        /// <summary>Pairs excluded because they hold such a position.</summary>
        public required int ExcludedByDirectionConflictPairCount { get; init; }

        /// <summary>
        /// Message observations lost with those pairs, which is what the exclusion actually costs.
        /// </summary>
        public required int MessageObservationsLostToDirectionConflict { get; init; }

        /// <summary>Pairs whose token side cannot supply the message side's direction counts.</summary>
        public required int SupplyInsufficientPairCount { get; init; }

        /// <summary>Pairs left with nothing for order to decide.</summary>
        public required DirectionSequenceDegenerateCounts Degenerate { get; init; }

        /// <summary>Supply-sufficient, non-degenerate pairs, classified for both statistics.</summary>
        public required int ClassifiedPairCount { get; init; }

        /// <summary>Message observations those classified pairs carry.</summary>
        public required int MessageObservationsClassified { get; init; }
    }
}
