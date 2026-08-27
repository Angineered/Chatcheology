namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// The resolution event that was appended, and the projection it committed.
    /// </summary>
    /// <remarks>
    /// The projection fields describe the committed post-state, read back from the same transaction
    /// that wrote it and proved by replay to match its history. They are the answer to "what is true
    /// now", not a prediction.
    /// </remarks>
    public sealed class Phase7ResolutionTransitionResult
    {
        /// <summary>The identifier the appended event was assigned. Service-generated.</summary>
        public required long AttachmentResolutionEventID { get; init; }

        /// <summary>The attachment whose resolution changed.</summary>
        public required long AttachmentID { get; init; }

        /// <summary>Which of the three transitions was applied.</summary>
        public required Phase7ResolutionEventType EventType { get; init; }

        /// <summary>The attachment's committed resolution status.</summary>
        public required Phase7ResolutionStatus ResolutionStatus { get; init; }

        /// <summary>
        /// The attachment's committed asset, or <see langword="null"/> after a revocation.
        /// </summary>
        public required long? ResolvedMediaAssetID { get; init; }

        /// <summary>
        /// The event this one superseded, or <see langword="null"/> for a first confirmation.
        /// </summary>
        public required long? SupersedesEventID { get; init; }

        /// <summary>The snapshot cited as provenance, or <see langword="null"/>.</summary>
        public required long? SourceRunID { get; init; }

        /// <summary>
        /// When the service recorded the event. Service-generated, descriptive metadata only —
        /// <strong>replay never orders by it</strong>.
        /// </summary>
        public required DateTime OccurredAtUtc { get; init; }

        /// <summary>The actor label as stored, or <see langword="null"/>.</summary>
        public required string? ActorLabel { get; init; }
    }
}
