namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// The review event that was appended.
    /// </summary>
    /// <remarks>
    /// Everything here is a fact about the row that now exists. Nothing is a summary, a judgement or a
    /// derived state, and in particular nothing here says anything about whether the attachment has an
    /// asset — a review never touches that.
    /// </remarks>
    public sealed class Phase7ReviewResult
    {
        /// <summary>The identifier the appended event was assigned. Service-generated.</summary>
        public required long AttachmentReviewEventID { get; init; }

        /// <summary>The attachment reviewed, echoed from the request.</summary>
        public required long AttachmentID { get; init; }

        /// <summary>The snapshot reviewed, echoed from the request.</summary>
        public required long SourceRunID { get; init; }

        /// <summary>The stored outcome. Service-generated, and single-valued by design.</summary>
        public required Phase7ReviewOutcome Outcome { get; init; }

        /// <summary>
        /// When the service recorded the event.
        /// </summary>
        /// <remarks>
        /// Service-generated, never caller-supplied: a caller-supplied instant would be a claim the
        /// record cannot check. It is descriptive metadata only — nothing orders or decides by it.
        /// </remarks>
        public required DateTime OccurredAtUtc { get; init; }

        /// <summary>The actor label as stored, or <see langword="null"/>.</summary>
        public required string? ActorLabel { get; init; }
    }
}
