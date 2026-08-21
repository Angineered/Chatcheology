namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// Everything the Stage B2C-0 direction-sequence gate measured: population, coverage, scope,
    /// integrity, supply, burstiness and exact discriminating capacity.
    /// </summary>
    /// <remarks>
    /// A power-and-validity gate, not an analysis. It measures whether the archive can carry a later
    /// direction-order alignment test at all, and it does so <em>before</em> any outcome is visible:
    /// nothing here is an observed alignment, an embedding of an actual token sequence, an order loss,
    /// an adjacent-date comparison or a reversal. Those belong to the later stage, and computing one
    /// now would reveal the answer before the population rules protecting it are frozen.
    /// <para>
    /// No verdict either. Whether the informative populations are large enough to justify going on is a
    /// decision taken at the gate review from the figures below, together with three other frozen
    /// choices — the primary scope per date, the two primary population rules, and the cross-source
    /// disagreement tolerance. Compiling any of them in would let the code appear to decide something
    /// it must not.
    /// </para>
    /// <para>
    /// Nothing here resolves an attachment, ranks a candidate, scores a confidence, consumes a
    /// candidate identity or a Stage B2B assignment, changes a schema or writes anything at all.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceGateCensus
    {
        /// <summary>The conversation analysed.</summary>
        public required long ConversationID { get; init; }

        /// <summary>The participant named as the local, exporting user.</summary>
        public required long LocalParticipantID { get; init; }

        /// <summary>C0 — the message side.</summary>
        public required DirectionSequenceMessagePopulation MessagePopulation { get; init; }

        /// <summary>C0 — the token side per acquisition source, in identifier order.</summary>
        public required IReadOnlyList<DirectionSequenceSourcePopulation> Sources { get; init; }

        /// <summary>C0 — the token side per device group, in identifier order.</summary>
        public required IReadOnlyList<DirectionSequenceDeviceGroupPopulation>
            DeviceGroups { get; init; }

        /// <summary>C0 — cross-source overlap and the direction-disagreement rate.</summary>
        public required DirectionSequenceCrossSourceOverlap CrossSourceOverlap { get; init; }

        /// <summary>
        /// Whether the caller declared the preserved Stage A coverage figures, and the gate's own
        /// recount was therefore reconciled against them rather than merely reported.
        /// </summary>
        public required bool StageATokenCoverageReconciled { get; init; }

        /// <summary>
        /// One census per numbering scope, in scope order, never merged into a single total.
        /// </summary>
        public required IReadOnlyList<DirectionSequenceScopeCensus> Scopes { get; init; }
    }
}
