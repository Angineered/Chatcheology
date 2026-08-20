using Chatcheology.Data.Matching;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// Everything one cross-direction sequence census measured.
    /// </summary>
    /// <remarks>
    /// Evidence and measurements only. No gate verdict, no threshold and no outcome state appears
    /// here: whether the result is strong enough to justify designing a later sequence-based candidate
    /// stage is a pre-registered decision belonging to the run harness, and compiling it in would let
    /// the code appear to decide something it must not.
    /// <para>
    /// Nothing here resolves an attachment, ranks a candidate, persists a decision or names an
    /// identifier. The cohort it measures is provisional throughout: date and direction evidence
    /// reduced each relation to one asset, which is not the same as that asset being the media the
    /// attachment lost.
    /// </para>
    /// </remarks>
    public sealed class CrossDirectionSequenceCensus
    {
        /// <summary>The conversation analysed.</summary>
        public required long ConversationID { get; init; }

        /// <summary>The participant named as the local, exporting user.</summary>
        public required long LocalParticipantID { get; init; }

        /// <summary>
        /// The frozen first-pass census this cohort was drawn from, returned unchanged.
        /// </summary>
        /// <remarks>
        /// Carried so the run harness can reconcile against the preserved first-pass figures without
        /// running the frozen analysis a second time, and so a mismatch is detectable before any
        /// figure below it is read.
        /// </remarks>
        public required MatchAnalysisCensus MatchingCensus { get; init; }

        /// <summary>C0.</summary>
        public required CohortStructureCensus CohortStructure { get; init; }

        /// <summary>
        /// Direction provenance of token evidence, counted once per cohort relation.
        /// </summary>
        /// <remarks>
        /// Operational coverage of the attachments. These are not independent observations, because
        /// all relations in one <c>(date, direction)</c> group rest on one asset's evidence.
        /// </remarks>
        public required TokenDirectionProvenanceCounts RelationWeightedTokenProvenance { get; init; }

        /// <summary>
        /// The same classes counted once per distinct <c>(date, direction, candidate asset)</c> group,
        /// which is coverage of the evidence rather than of the attachments.
        /// </summary>
        public required TokenDirectionProvenanceCounts GroupWeightedTokenProvenance { get; init; }

        /// <summary>C1.</summary>
        public required TokenCardinalityCensus StrictDateTokenCardinality { get; init; }

        /// <summary>C2 and C3 — the primary result.</summary>
        public required StrictOrderCensus PrimaryOrder { get; init; }

        /// <summary>C4.</summary>
        public required TokenDisplacementCensus Displacement { get; init; }

        /// <summary>C5.</summary>
        public required DirectionNamespaceDiagnostic DirectionNamespace { get; init; }

        /// <summary>
        /// C6 — the same measurement over observations whose tokens are each seen on at least one
        /// copy whose own direction evidence agrees with its message.
        /// </summary>
        /// <remarks>
        /// A sensitivity view, never a replacement: it is computed from the retained observations of
        /// <see cref="PrimaryOrder"/> and cannot move them.
        /// </remarks>
        public required StrictOrderCensus DirectionAgreeingSensitivity { get; init; }
    }
}
