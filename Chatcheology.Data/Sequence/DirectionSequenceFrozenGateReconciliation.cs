using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// Proof that a run reproduced the accepted Stage B2C-0 gate state, and the only key that opens the
    /// alignment outcome calculation.
    /// </summary>
    /// <remarks>
    /// There is no public constructor, and the only code that can produce one is
    /// <see cref="DirectionSequenceFrozenGateBarrier.Reconcile"/>, which throws rather than returning
    /// where any frozen target fails. The outcome calculator demands one of these, so "no outcome may be
    /// read before the freeze reconciles" is a property of what can be constructed rather than a rule
    /// about the order somebody calls things in.
    /// <para>
    /// It carries the accepted census because every reference sum, band and population the outcome stage
    /// compares against lives there, already computed by the code whose real-run evidence is preserved.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceFrozenGateReconciliation
    {
        internal DirectionSequenceFrozenGateReconciliation(
            DirectionSequenceGateCensus census,
            DirectionSequenceFrozenGateDeclaration declaration,
            DirectionSequenceStageB1Declaration stageB1,
            int reconciledTargetCount)
        {
            Census = census;
            Declaration = declaration;
            StageB1 = stageB1;
            ReconciledTargetCount = reconciledTargetCount;
        }

        /// <summary>The accepted gate census this run reproduced.</summary>
        public DirectionSequenceGateCensus Census { get; }

        /// <summary>The declaration it was reconciled against.</summary>
        public DirectionSequenceFrozenGateDeclaration Declaration { get; }

        /// <summary>The Stage B1 provenance the run declared.</summary>
        public DirectionSequenceStageB1Declaration StageB1 { get; }

        /// <summary>How many individual targets and identities the barrier checked.</summary>
        /// <remarks>
        /// Reported so the evidence says how much was reconciled, not merely that reconciliation
        /// "passed". A barrier that silently checked less would show up here.
        /// </remarks>
        public int ReconciledTargetCount { get; }

        /// <summary>The frozen primary numbering scope.</summary>
        public ScopeLevel PrimaryScope => Declaration.PrimaryScope;

        /// <summary>The primary scope's census.</summary>
        public DirectionSequenceScopeCensus PrimaryScopeCensus =>
            Census.Scopes.Single(scope => scope.Scope == PrimaryScope);
    }
}
