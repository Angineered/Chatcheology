using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// One numbering scope's frozen pair-universe and exclusion targets.
    /// </summary>
    /// <remarks>
    /// Declared for every scope the gate measures, not only the primary one. The non-primary scope's
    /// <see cref="SupplyAssessedPairCount"/> is what makes a wrong-scope run fail: the classified
    /// population and the reference readings are scope-invariant, so a run that silently numbered by
    /// source instead of by device group would reconcile everything except this.
    /// </remarks>
    public sealed record DirectionSequenceFrozenScopeTarget
    {
        /// <summary>The scope described.</summary>
        public required ScopeLevel Scope { get; init; }

        /// <summary>Pairs whose date carries at least one message symbol.</summary>
        public required int PairsWithMessageSymbols { get; init; }

        /// <summary>Pairs excluded because they hold a direction-conflicting logical position.</summary>
        public required int ExcludedByDirectionConflictPairCount { get; init; }

        /// <summary>
        /// Pairs supply was assessed over, which is those carrying message symbols and not excluded.
        /// </summary>
        public required int SupplyAssessedPairCount { get; init; }
    }
}
