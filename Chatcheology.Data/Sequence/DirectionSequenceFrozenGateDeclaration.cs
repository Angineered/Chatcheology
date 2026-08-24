using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// The accepted Stage B2C-0 gate state an alignment run must reproduce before it may read any
    /// outcome.
    /// </summary>
    /// <remarks>
    /// Every figure here is supplied by the caller and none has a default. That is the point: these are
    /// one archive's counts, and compiling them in as production defaults would make a stale workspace,
    /// a wrong scope or a drifted grammar reconcile against the code's own memory of the last run
    /// instead of against a stated freeze. A synthetic test declares synthetic targets through the same
    /// type, which is what proves the numbers are not defaults.
    /// <para>
    /// Integer counts are compared exactly. The four reference readings are compared as the six-decimal
    /// rendering the freeze record prints, because that rendering is what was frozen; their exact
    /// integer foundations and the determinacy classifications remain the authority, and no reading here
    /// is a threshold.
    /// </para>
    /// <para>
    /// Deliberately absent: any workspace path, hash, participant, conversation or date. Those belong to
    /// the run's request and to the private harness that verifies file identity, not to the description
    /// of a frozen population.
    /// </para>
    /// </remarks>
    public sealed record DirectionSequenceFrozenGateDeclaration
    {
        /// <summary>The frozen primary numbering scope every primary aggregate must use.</summary>
        public required ScopeLevel PrimaryScope { get; init; }

        /// <summary>
        /// The pair-universe and exclusion targets, one per scope the gate measures.
        /// </summary>
        public required IReadOnlyList<DirectionSequenceFrozenScopeTarget> Scopes { get; init; }

        // -----------------------------------------------------------------------------------------
        // Message side.
        // -----------------------------------------------------------------------------------------

        /// <summary>Unresolved attachments of the conversation the run considers.</summary>
        public required int ConsideredAttachmentCount { get; init; }

        /// <summary>Distinct local dates those attachments fall on.</summary>
        public required int DistinctAttachmentDateCount { get; init; }

        /// <summary>How many of them the local participant sent.</summary>
        public required int OutgoingAttachmentCount { get; init; }

        /// <summary>How many of them the local participant did not send.</summary>
        public required int IncomingAttachmentCount { get; init; }

        /// <summary>Messages carrying more than one unresolved attachment.</summary>
        public required int MultiAttachmentMessageCount { get; init; }

        /// <summary>The most unresolved attachments any one message carries.</summary>
        public required int MaximumAttachmentsOnOneMessage { get; init; }

        // -----------------------------------------------------------------------------------------
        // Primary scope: supply, after collapse.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Supply-insufficient pairs at the primary scope, measured after equivalent-position collapse.
        /// </summary>
        /// <remarks>
        /// The after-collapse supply census, which is the state the gate classifies on, and which counts
        /// the pairs emitting no token position at all. The pair-population figure of the same name
        /// describes a narrower set — pairs that emitted positions and still could not supply the message
        /// side — and is not this number.
        /// </remarks>
        public required int SupplyInsufficientPairCountAfterCollapse { get; init; }

        /// <summary>Supply-sufficient pairs at the primary scope, after collapse.</summary>
        public required int SupplySufficientPairCountAfterCollapse { get; init; }

        /// <summary>
        /// Pairs insufficient before collapse and sufficient after it, which cannot happen.
        /// </summary>
        /// <remarks>
        /// Declared so the impossible case is asserted rather than assumed. Collapse only ever removes
        /// positions, so a non-zero value means the two stages are not counting the same thing.
        /// </remarks>
        public required int PairsBecomingSufficientAfterCollapse { get; init; }

        // -----------------------------------------------------------------------------------------
        // Primary scope: pair universe and classification.
        // -----------------------------------------------------------------------------------------

        /// <summary>Distinct degenerate pairs at the primary scope.</summary>
        public required int DegeneratePairCount { get; init; }

        /// <summary>Pairs whose scope emits no direction-labelled token position on the date.</summary>
        public required int NoTokenPositionPairCount { get; init; }

        /// <summary>Supply-sufficient, non-degenerate pairs classified for both statistics.</summary>
        public required int ClassifiedPairCount { get; init; }

        /// <summary>Message observations those classified pairs carry.</summary>
        public required int ClassifiedMessageObservationCount { get; init; }

        /// <summary>Classified pairs whose binary admission the conditioning data fixes.</summary>
        public required int BinaryDeterminatePairCount { get; init; }

        /// <summary>Classified pairs whose binary admission it does not fix.</summary>
        public required int BinaryInformativePairCount { get; init; }

        /// <summary>Classified pairs whose normalised embedding share the conditioning data fixes.</summary>
        public required int GradedDeterminatePairCount { get; init; }

        /// <summary>Classified pairs whose normalised embedding share it does not fix.</summary>
        public required int GradedInformativePairCount { get; init; }

        /// <summary>Informative under both classifications.</summary>
        public required int BinaryInformativeAndGradedInformative { get; init; }

        /// <summary>Binary-determinate and graded-informative.</summary>
        public required int BinaryDeterminateAndGradedInformative { get; init; }

        /// <summary>
        /// Binary-informative and graded-determinate, which graded determinacy's implication forbids.
        /// </summary>
        public required int BinaryInformativeAndGradedDeterminate { get; init; }

        /// <summary>Determinate under both classifications.</summary>
        public required int BinaryDeterminateAndGradedDeterminate { get; init; }

        /// <summary>Classified pairs every arrangement of whose class admits the pattern.</summary>
        public required int ExactlyOneQrBandPairCount { get; init; }

        /// <summary>Classified pairs no arrangement of whose class admits the pattern.</summary>
        public required int ExactlyZeroQrBandPairCount { get; init; }

        // -----------------------------------------------------------------------------------------
        // Reference readings, as printed.
        // -----------------------------------------------------------------------------------------

        /// <summary>Sum of exact <c>q_r</c> over classified pairs, to six decimals.</summary>
        public required string SumOfConditionalAdmissionProbability { get; init; }

        /// <summary>The same sum over binary-informative pairs, to six decimals.</summary>
        public required string SumOfConditionalAdmissionProbabilityOverInformative { get; init; }

        /// <summary>Sum of exact <c>E_r[share]</c> over classified pairs, to six decimals.</summary>
        public required string SumOfExpectedEmbeddingShare { get; init; }

        /// <summary>The same sum over graded-informative pairs, to six decimals.</summary>
        public required string SumOfExpectedEmbeddingShareOverInformative { get; init; }
    }
}
