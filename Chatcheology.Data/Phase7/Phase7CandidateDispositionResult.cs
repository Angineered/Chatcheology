namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// The disposition event that was appended.
    /// </summary>
    /// <remarks>
    /// <see cref="AttachmentID"/>, <see cref="RunID"/> and <see cref="MediaAssetID"/> are echoed back
    /// as <strong>provenance read from the candidate row</strong>, never as values the caller supplied.
    /// They are here so a caller can see which evidence identity it actually acted on without issuing a
    /// second query.
    /// </remarks>
    public sealed class Phase7CandidateDispositionResult
    {
        /// <summary>The identifier the appended event was assigned. Service-generated.</summary>
        public required long AttachmentCandidateDispositionEventID { get; init; }

        /// <summary>The candidate row disproven, echoed from the request.</summary>
        public required long AttachmentCandidateID { get; init; }

        /// <summary>The attachment that candidate belongs to, read from the candidate row.</summary>
        public required long AttachmentID { get; init; }

        /// <summary>The run that candidate belongs to, read from the candidate row.</summary>
        public required long RunID { get; init; }

        /// <summary>The asset that candidate names, read from the candidate row.</summary>
        public required long MediaAssetID { get; init; }

        /// <summary>The stored disposition. Service-generated, and single-valued by design.</summary>
        public required Phase7CandidateDisposition Disposition { get; init; }

        /// <summary>
        /// When the service recorded the event. Service-generated, descriptive metadata only.
        /// </summary>
        public required DateTime OccurredAtUtc { get; init; }

        /// <summary>The actor label as stored, or <see langword="null"/>.</summary>
        public required string? ActorLabel { get; init; }
    }
}
