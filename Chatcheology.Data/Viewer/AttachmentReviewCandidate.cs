using Chatcheology.Data.Media;
using Chatcheology.Data.Phase7;

namespace Chatcheology.Data.Viewer
{
    /// <summary>
    /// One current exact-date candidate, as the review panel needs to show it.
    /// </summary>
    /// <remarks>
    /// <para><strong>Nothing here is a recommendation.</strong> Tier, direction and order support are
    /// Phase 7's own reduction of what the evidence can compare, reported unchanged. None of them is
    /// a score, a rank in the ordinary sense, a probability or a reason to confirm anything, and no
    /// combination of them becomes one.</para>
    /// <para><strong>The row identity is load-bearing.</strong> A disproof names
    /// <see cref="AttachmentCandidateID"/> — the candidate row — and never a rebuilt
    /// attachment/run/asset tuple. A later evidence run produces new candidate rows even for the same
    /// asset, which is precisely what keeps an old run's disproof from carrying forward into a new
    /// one.</para>
    /// </remarks>
    public sealed class AttachmentReviewCandidate
    {
        /// <summary>The candidate row. The identity every disposition write carries.</summary>
        public required long AttachmentCandidateID { get; init; }

        /// <summary>The asset this candidate proposes.</summary>
        public required long MediaAssetID { get; init; }

        /// <summary>The evidence run this candidate belongs to.</summary>
        public required long RunID { get; init; }

        /// <summary>
        /// Which tier of the current partial order this candidate sits in, counting from one.
        /// </summary>
        /// <remarks>
        /// A position in a partial order, not a magnitude. Tier 1 is not "better"; it is the group
        /// nothing in the comparison placed below anything else. Read it beside
        /// <see cref="AttachmentReviewState.TierCount"/>: one tier means the comparison did not
        /// distinguish these candidates at all.
        /// </remarks>
        public required int TierIndex { get; init; }

        /// <summary>What the direction evidence supports, or that it says nothing.</summary>
        public required Phase7DirectionSupport Direction { get; init; }

        /// <summary>What the occurrence-order evidence supports, or that it says nothing.</summary>
        public required Phase7OrderSupport OrderSupport { get; init; }

        /// <summary>
        /// Whether this exact candidate row carries an independent disproof in this run.
        /// </summary>
        /// <remarks>
        /// Matched by candidate row, never by asset. Two runs proposing the same asset are two
        /// candidate rows, and a disproof of one says nothing about the other.
        /// </remarks>
        public required bool IsIndependentlyDisproven { get; init; }

        /// <summary>The asset's recorded kind, typed here rather than left as stored text.</summary>
        public required MediaType MediaType { get; init; }

        /// <summary>The asset's recorded size in bytes.</summary>
        public required long SizeBytes { get; init; }

        /// <summary>The asset's recorded duration in milliseconds, or <see langword="null"/>.</summary>
        public required long? DurationMS { get; init; }

        /// <summary>The asset's recorded pixel width, or <see langword="null"/>.</summary>
        public required int? Width { get; init; }

        /// <summary>The asset's recorded pixel height, or <see langword="null"/>.</summary>
        public required int? Height { get; init; }
    }
}
