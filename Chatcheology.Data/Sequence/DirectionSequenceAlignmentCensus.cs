namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// Everything Stage B2C-1 measured: what the observed token order carried, against exact references
    /// that were frozen before it was looked at.
    /// </summary>
    /// <remarks>
    /// <b>What this is.</b> A test of whether the archive shows more message-direction to
    /// supported-token-direction order alignment than an exact reference expects, where that reference
    /// holds each date and scope's direction composition and its observed direction burstiness fixed. It
    /// is conditional throughout on the supported token grammar, on direction-label coverage, on the
    /// frozen device-group scope, and on the stage-local working assumption that a file not beneath a
    /// complete <c>Sent</c> segment is incoming.
    /// <para>
    /// <b>What this is not.</b> It does not identify which token belongs to which message, and a unique
    /// embedding is not a match. Nothing here resolves an attachment, ranks or consumes a candidate,
    /// scores a confidence, creates an anchor, changes a schema or writes anything at all. It cannot say
    /// that the monotone hypothesis was validated or refuted: a positive result may be archive-local
    /// support under the stated assumptions, and a weak or negative one cannot be attributed to message
    /// order alone, because grammar coverage, direction coverage, missing recovered media, download
    /// timing and the scope assumption all reach the same figures.
    /// </para>
    /// <para>
    /// <b>Populations.</b> Every result names the population it was computed over and how many pairs that
    /// population holds. The populations were frozen before any outcome existed and a returned census has
    /// reconciled each of them; none is a filter applied after seeing a result.
    /// </para>
    /// <para>
    /// <b>C8 is intentionally omitted.</b> No pooled archive-level multi-source sequence is constructed
    /// here. Pooling acquisition slices of one overlapping library adds no independent evidence and could
    /// manufacture an ordering signal; any such design would need its own reviewed boundary.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceAlignmentCensus
    {
        /// <summary>The conversation analysed.</summary>
        public required long ConversationID { get; init; }

        /// <summary>The participant named as the local, exporting user.</summary>
        public required long LocalParticipantID { get; init; }

        /// <summary>
        /// The frozen Stage B2C-0 state this run reproduced before any outcome below was calculated.
        /// </summary>
        public required DirectionSequenceFrozenGateReconciliation Reconciliation { get; init; }

        /// <summary>C2 — supply and loss, over every pair supply was assessed for.</summary>
        public required DirectionSequenceSupplyLossCensus SupplyLoss { get; init; }

        /// <summary>C3 — ordered alignment and order loss, over the order-testable pairs.</summary>
        public required DirectionSequenceOrderLossCensus OrderLoss { get; init; }

        /// <summary>C4 — whether and in how many ways the whole pattern embeds.</summary>
        public required DirectionSequenceMultiplicityCensus Multiplicity { get; init; }

        /// <summary>Which failed embeddings a coverage limitation can explain.</summary>
        public required DirectionSequenceFailureAttributionCensus FailureAttribution { get; init; }

        /// <summary>C4 — the graded co-primary comparison, on the frozen graded population.</summary>
        public required DirectionSequenceGradedFigures Graded { get; init; }

        /// <summary>
        /// The observed-share path checked against the frozen expected readings where they must agree
        /// exactly.
        /// </summary>
        public required DirectionSequenceGradedDeterminateCheck GradedDeterminateCheck { get; init; }

        /// <summary>C5 — the binary co-primary comparison, on the frozen binary population.</summary>
        public required DirectionSequenceBinaryFigures Binary { get; init; }

        /// <summary>C5b — the binary adjacent-date controls, one per neighbour, never averaged.</summary>
        public required IReadOnlyList<DirectionSequenceBinaryAdjacentControl>
            BinaryAdjacentControls { get; init; }

        /// <summary>C5b — the graded adjacent-date controls, on their own intersections.</summary>
        public required IReadOnlyList<DirectionSequenceGradedAdjacentControl>
            GradedAdjacentControls { get; init; }

        /// <summary>C5c — the tertiary reversal diagnostic, which drives no conclusion.</summary>
        public required DirectionSequenceReversalCensus Reversal { get; init; }

        /// <summary>C6 — the primary results split by bands that existed before the run.</summary>
        public required IReadOnlyList<DirectionSequenceDescriptiveSplit>
            DescriptiveSplits { get; init; }

        /// <summary>C7 — the same outcomes at non-primary scopes, as labelled sensitivity only.</summary>
        public required IReadOnlyList<DirectionSequenceScopeSensitivityCensus>
            ScopeSensitivity { get; init; }
    }
}
