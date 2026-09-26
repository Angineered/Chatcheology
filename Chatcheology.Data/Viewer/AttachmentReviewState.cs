using Chatcheology.Data.Phase7;

namespace Chatcheology.Data.Viewer
{
    /// <summary>
    /// Everything the review panel shows about one attachment, read in one pass.
    /// </summary>
    /// <remarks>
    /// <para><strong>Assembled from Phase 7, not recomputed.</strong> The reconstruction state, the
    /// current run, the support and tiers, the disproof state and the resolution history are all
    /// existing Phase 7 answers, carried through unchanged. The only thing read outside those APIs is
    /// the candidate rows' own identity and their assets' descriptive facts, which no Phase 7 reader
    /// exposes together.</para>
    /// <para><strong>Every current candidate is here.</strong> Nothing is filtered by tier, by
    /// support availability or by disproof. A candidate already independently disproven stays in the
    /// list, marked; hiding it would let a count of what remains look like an answer.</para>
    /// </remarks>
    public sealed class AttachmentReviewState
    {
        /// <summary>The attachment under review.</summary>
        public required long AttachmentID { get; init; }

        /// <summary>Its position within its message.</summary>
        public required int Ordinal { get; init; }

        /// <summary>Phase 7's reconstruction answer for this attachment, unchanged.</summary>
        public required Phase7ReconstructionState Reconstruction { get; init; }

        /// <summary>
        /// The attachment's own current completed evidence run, or <see langword="null"/> when no
        /// completed run has evaluated it.
        /// </summary>
        /// <remarks>
        /// Attachment-scoped, never the workspace's latest run. Read through
        /// <c>Phase7SupportReader.ReadLatestCompletedRunContaining</c> rather than from
        /// <see cref="Phase7ReconstructionState.EvidenceRunID"/>, which is deliberately null for a
        /// resolved attachment — a resolved attachment answers from its projection, and the review
        /// panel still needs its evidence context.
        /// </remarks>
        public required long? CurrentEvidenceRunID { get; init; }

        /// <summary>
        /// Every current exact-date candidate, ordered by asset identifier.
        /// </summary>
        /// <remarks>
        /// Empty when there is no current run. Adjacent-date context is not here and never will be:
        /// it is contextual evidence, and putting it in a chooser would promote it into a candidate
        /// set the run did not record.
        /// <para>
        /// The order is the stored asset identifier's — a surrogate key, chosen because it is stable
        /// and says nothing. It is not a preference, and the UI must not present it as one.
        /// </para>
        /// </remarks>
        public required IReadOnlyList<AttachmentReviewCandidate> Candidates { get; init; }

        /// <summary>
        /// How many tiers the frozen partial order produced over <see cref="Candidates"/>.
        /// </summary>
        /// <remarks>
        /// Zero when there is no candidate. <strong>One means the comparison did not distinguish
        /// them</strong> — the same reading a single candidate produces — and the UI should say so
        /// rather than showing a group heading that implies a decision was reached.
        /// </remarks>
        public required int TierCount { get; init; }

        /// <summary>
        /// Whether a review event names this attachment's current evidence run.
        /// </summary>
        /// <remarks>
        /// Phase 7's own scoping: a review of a superseded snapshot remains valid history but is not
        /// a statement about the evidence as it now stands.
        /// </remarks>
        public required bool HasCurrentReviewedInsufficientEvidence { get; init; }

        /// <summary>
        /// The attachment's resolution history, in Phase 7's own event order.
        /// </summary>
        /// <remarks>
        /// Ordered by <c>AttachmentResolutionEventID</c> inside the Phase 7 reader. Nothing here
        /// re-sorts it, and nothing sorts it by time: two events written in the same second are still
        /// ordered by the sequence they were recorded in.
        /// </remarks>
        public required IReadOnlyList<Phase7ResolutionHistoryEntry> ResolutionHistory { get; init; }

        /// <summary>
        /// The event a replacement or revocation must supersede, or <see langword="null"/> when the
        /// attachment has no effective resolution.
        /// </summary>
        /// <remarks>
        /// From Phase 7's replay, which is the authority on which event is currently in force. The
        /// operator never sees or types it.
        /// </remarks>
        public required long? CurrentEffectiveEventID { get; init; }

        /// <summary>Whether Phase 7 currently records an asset for this attachment.</summary>
        public bool IsResolved =>
            Reconstruction.Status == Phase7ResolutionStatus.Resolved;
    }
}
