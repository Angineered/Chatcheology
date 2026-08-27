namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// A replacement: this attachment's media is a <em>different</em> asset from the one currently
    /// recorded, on an independently stated basis.
    /// </summary>
    /// <remarks>
    /// <para>Valid only while the attachment is <see cref="Phase7ResolutionStatus.Resolved"/>.</para>
    /// <para><strong>A replacement must actually replace.</strong>
    /// <see cref="MediaAssetID"/> must differ from the attachment's current
    /// <c>ResolvedMediaAssetID</c>. Each of the three transitions has exactly one job — confirm sets
    /// the asset, replacement changes it, revocation clears it — so a replacement that left the asset
    /// where it was would give this operation a second meaning it does not have. A same-asset
    /// replacement is refused, and <strong>no fourth "reaffirm" operation exists</strong> to serve that
    /// case.</para>
    /// <para>
    /// The comparison is against the asset held <em>now</em>, not against the attachment's history.
    /// Replacing X with Y and later replacing Y back with X is two valid replacements: each changed the
    /// asset from what it then was.
    /// </para>
    /// </remarks>
    public sealed class Phase7ConfirmReplacementRequest
    {
        /// <summary>
        /// An existing workspace at schema version
        /// <see cref="Workspace.WorkspaceDatabase.SchemaVersion"/>.
        /// </summary>
        public required string DatabasePath { get; init; }

        /// <summary>The attachment being re-resolved. Must currently be resolved.</summary>
        public required long AttachmentID { get; init; }

        /// <summary>
        /// The replacement asset. Must exist, and must differ from the current one.
        /// </summary>
        public required long MediaAssetID { get; init; }

        /// <summary>
        /// The attachment's <strong>current effective</strong> resolution event.
        /// </summary>
        /// <remarks>
        /// Not merely an event that exists somewhere in this attachment's history: the one the replay
        /// of that history currently ends on. That is a fact about current state rather than about this
        /// row, so no schema constraint can express it and the service checks it inside the write
        /// transaction. The schema does enforce two neighbouring halves of the rule: that the value is
        /// present at all, and that the event it names belongs to this same attachment.
        /// </remarks>
        public required long SupersedesEventID { get; init; }

        /// <summary>Which kind of independent basis established the new correspondence.</summary>
        public required Phase7IndependentBasisType IndependentBasisType { get; init; }

        /// <summary>
        /// The human-written statement of that basis. Required, non-empty, stored verbatim.
        /// </summary>
        /// <remarks>
        /// A replacement needs its own basis. Inheriting the superseded event's would record that the
        /// old reasoning established the new asset, which it did not.
        /// </remarks>
        public required string IndependentBasisDescription { get; init; }

        /// <summary>
        /// Which Stage 7.1 snapshot the person was looking at, or <see langword="null"/>. Provenance
        /// only, on the same terms as <see cref="Phase7ConfirmRequest.SourceRunID"/>.
        /// </summary>
        public long? SourceRunID { get; init; }

        /// <summary>Who replaced it, or <see langword="null"/> when that is not recorded.</summary>
        public string? ActorLabel { get; init; }
    }
}
