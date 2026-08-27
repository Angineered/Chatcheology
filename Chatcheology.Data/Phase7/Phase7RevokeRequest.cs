namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// A revocation: the recorded resolution is no longer believed, and the attachment goes back to
    /// having no asset.
    /// </summary>
    /// <remarks>
    /// <para>Valid only while the attachment is <see cref="Phase7ResolutionStatus.Resolved"/>.</para>
    /// <para><strong>There is no target asset and no independent basis on this request.</strong> A
    /// revocation carries its own <see cref="RevocationReason"/> instead, because "why is this no longer
    /// believed correct" is a different question from "what independently established it was correct",
    /// and one field holding both would blur that for every later reader. The schema enforces the same
    /// separation from below.</para>
    /// <para><strong>A revocation never reactivates an older resolution.</strong> It always leaves the
    /// attachment unresolved with no current effective event, whatever it revoked. Restoring an earlier
    /// asset requires a fresh confirmation from that unresolved state, with its own fresh independent
    /// basis — there is no chain-walking undo.</para>
    /// </remarks>
    public sealed class Phase7RevokeRequest
    {
        /// <summary>
        /// An existing workspace at schema version
        /// <see cref="Workspace.WorkspaceDatabase.SchemaVersion"/>.
        /// </summary>
        public required string DatabasePath { get; init; }

        /// <summary>The attachment being unresolved. Must currently be resolved.</summary>
        public required long AttachmentID { get; init; }

        /// <summary>
        /// The attachment's <strong>current effective</strong> resolution event, on the same terms as
        /// <see cref="Phase7ConfirmReplacementRequest.SupersedesEventID"/>.
        /// </summary>
        public required long SupersedesEventID { get; init; }

        /// <summary>
        /// The human-written statement of why the resolution is no longer believed. Required,
        /// non-empty, stored verbatim.
        /// </summary>
        public required string RevocationReason { get; init; }

        /// <summary>
        /// Which Stage 7.1 snapshot the person was looking at, or <see langword="null"/>. Provenance
        /// only, on the same terms as <see cref="Phase7ConfirmRequest.SourceRunID"/>.
        /// </summary>
        public long? SourceRunID { get; init; }

        /// <summary>Who revoked, or <see langword="null"/> when that is not recorded.</summary>
        public string? ActorLabel { get; init; }
    }
}
