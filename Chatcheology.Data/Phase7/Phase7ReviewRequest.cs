namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// One human review of one attachment's evidence, in one Stage 7.1 snapshot.
    /// </summary>
    /// <remarks>
    /// <para><strong>There is no outcome field.</strong>
    /// <see cref="Phase7ReviewOutcome.ReviewedInsufficientEvidence"/> is the only outcome the schema
    /// permits, and the frozen design keeps that single-valued deliberately so that adding another is
    /// an explicit reviewed migration. A caller-supplied enum with one member would only invite a
    /// second to appear without that review, so the service writes the literal.</para>
    /// <para><strong>There is no resolution field, and no basis field.</strong> A review changes
    /// nothing about whether the attachment has an asset. The axes are independent, and this request
    /// has no way to express otherwise.</para>
    /// </remarks>
    public sealed class Phase7ReviewRequest
    {
        /// <summary>
        /// An existing workspace at schema version
        /// <see cref="Workspace.WorkspaceDatabase.SchemaVersion"/>.
        /// </summary>
        public required string DatabasePath { get; init; }

        /// <summary>The attachment being reviewed.</summary>
        public required long AttachmentID { get; init; }

        /// <summary>
        /// The completed run whose snapshot was reviewed.
        /// </summary>
        /// <remarks>
        /// Mandatory, because a review is always a review <em>of</em> a specific snapshot and never of
        /// "the evidence" in the abstract. It must be a completed run that evaluated this attachment,
        /// and it must still be this attachment's current such run.
        /// </remarks>
        public required long SourceRunID { get; init; }

        /// <summary>
        /// What the reviewer wrote, or <see langword="null"/> when nothing was written.
        /// </summary>
        /// <remarks>
        /// Optional and stored verbatim. Whitespace-only is refused, because it is neither an absence
        /// nor a note. Nothing in this project can verify what human prose says, and nothing claims to.
        /// </remarks>
        public string? ReviewNotes { get; init; }

        /// <summary>
        /// Who reviewed, or <see langword="null"/> when that is not recorded.
        /// </summary>
        public string? ActorLabel { get; init; }
    }
}
