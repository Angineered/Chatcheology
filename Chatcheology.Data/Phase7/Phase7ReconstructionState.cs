namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// What the minimal reconstruction model can say about one attachment.
    /// </summary>
    /// <remarks>
    /// <para>Exactly one of <see cref="ResolvedAsset"/> and <see cref="PlaceholderReason"/> is
    /// non-null. A resolved attachment answers from its projection and its asset; an unresolved one
    /// answers with one explicit reason.</para>
    /// <para><strong>The headline reason never erases a fact.</strong> The precedence that picks it has
    /// to choose between conditions that can all be true at once, so every one of those conditions is
    /// also reported here on its own. A caller that wants a different headline can compute one from
    /// these fields; none of them is a score, a rank, a confidence or a judgement.</para>
    /// <para>
    /// This is a read projection over existing tables. It is not persisted, and nothing in the
    /// reconstruction path writes.
    /// </para>
    /// </remarks>
    public sealed class Phase7ReconstructionState
    {
        /// <summary>The attachment described.</summary>
        public required long AttachmentID { get; init; }

        /// <summary>Whether an asset is currently recorded.</summary>
        public required Phase7ResolutionStatus Status { get; init; }

        /// <summary>
        /// The resolved asset, or <see langword="null"/> when the attachment is unresolved.
        /// </summary>
        public required Phase7ResolvedAssetDescriptor? ResolvedAsset { get; init; }

        /// <summary>
        /// Why there is no asset, or <see langword="null"/> when there is one.
        /// </summary>
        public required Phase7UnresolvedPlaceholderReason? PlaceholderReason { get; init; }

        /// <summary>
        /// The attachment's own current evidence run, or <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// <strong>Attachment-scoped.</strong> The latest completed run that actually evaluated this
        /// attachment — never the workspace-global latest completed run, which may be a run covering an
        /// entirely different conversation. Null when no completed run ever evaluated it, and null for
        /// a resolved attachment, which answers from its projection rather than from evidence.
        /// </remarks>
        public required long? EvidenceRunID { get; init; }

        /// <summary>
        /// How many exact-date candidates that run recorded, or <see langword="null"/>.
        /// </summary>
        public required int? ExactDateCandidateCount { get; init; }

        /// <summary>
        /// How many adjacent-date context rows that run recorded, or <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// Contextual, investigative evidence. A non-zero count is never a candidate set, and nothing
        /// here promotes it into one.
        /// </remarks>
        public required int? AdjacentDateContextCount { get; init; }

        /// <summary>
        /// Whether that run flagged the attachment's group as impossible, or <see langword="null"/>.
        /// </summary>
        public required bool? IsWithinDirectionImpossibleGroup { get; init; }

        /// <summary>
        /// How many tiers the frozen partial order produced over that run's candidates, or
        /// <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// One tier means the comparison did not discriminate, which is the reading a single candidate
        /// also produces. A tier index carries no magnitude and no identity claim.
        /// </remarks>
        public required int? CandidateTierCount { get; init; }

        /// <summary>
        /// Whether a review event names the attachment's current evidence run.
        /// </summary>
        /// <remarks>
        /// A review of an older, superseded snapshot is deliberately not counted: it remains valid
        /// history, but it is not a statement about the evidence as it now stands.
        /// </remarks>
        public required bool HasCurrentReviewedInsufficientEvidence { get; init; }

        /// <summary>
        /// Whether every candidate of the current run carries an independent disproof.
        /// </summary>
        /// <remarks>
        /// Derived, never stored, and <see langword="false"/> when the current run holds no candidate at
        /// all. Disproofs recorded against an older run's candidate rows do not count towards this,
        /// because those are different candidate identities.
        /// </remarks>
        public required bool AllCurrentCandidatesIndependentlyDisproven { get; init; }
    }
}
