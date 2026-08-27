namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// A first confirmation: this attachment's media is this asset, on an independently stated basis.
    /// </summary>
    /// <remarks>
    /// <para>Valid only while the attachment is <see cref="Phase7ResolutionStatus.Unresolved"/>. There
    /// is <strong>no</strong> supersedes field, because a fresh confirmation supersedes nothing — the
    /// schema's own <c>CHECK</c> enforces the same thing from below.</para>
    /// <para><strong>The basis is required and its vocabulary is closed.</strong> No value of
    /// <see cref="Phase7IndependentBasisType"/> can express "the ranking was convincing", and there is
    /// no field on this request in which a ranking fact, a tier, a support value or a candidate
    /// reference could be recorded as a basis. A human may use Stage 7.1 evidence to decide what to
    /// inspect; what gets persisted has to be the independent statement.</para>
    /// <para><strong>Nothing gates this on evidence.</strong> No prior review is required, no
    /// disposition state is required, and the target need not be a candidate of any run.</para>
    /// </remarks>
    public sealed class Phase7ConfirmRequest
    {
        /// <summary>
        /// An existing workspace at schema version
        /// <see cref="Workspace.WorkspaceDatabase.SchemaVersion"/>.
        /// </summary>
        public required string DatabasePath { get; init; }

        /// <summary>The attachment being resolved. Must currently be unresolved.</summary>
        public required long AttachmentID { get; init; }

        /// <summary>
        /// The asset this attachment's media is.
        /// </summary>
        /// <remarks>
        /// It must exist in <c>MediaAsset</c>, and that is the only membership requirement. An asset
        /// established by <see cref="Phase7IndependentBasisType.ExternalProvenanceOrGroundTruth"/>
        /// legitimately need not appear in any run's exact-date candidate set.
        /// </remarks>
        public required long MediaAssetID { get; init; }

        /// <summary>Which kind of independent basis established the correspondence.</summary>
        public required Phase7IndependentBasisType IndependentBasisType { get; init; }

        /// <summary>
        /// The human-written statement of that basis. Required, non-empty, stored verbatim.
        /// </summary>
        public required string IndependentBasisDescription { get; init; }

        /// <summary>
        /// Which Stage 7.1 snapshot the person was looking at, or <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// <strong>Provenance only, never authority.</strong> Null is always accepted — a confirmation
        /// established entirely externally may legitimately cite no snapshot, and forcing one would
        /// make it a fiction. When supplied it must name a completed run that evaluated this
        /// attachment, so that a stated provenance is true; but it need <em>not</em> be the
        /// attachment's current run, because this records what was on screen rather than a judgement
        /// about current evidence. It never gates which asset may be confirmed.
        /// </remarks>
        public long? SourceRunID { get; init; }

        /// <summary>Who confirmed, or <see langword="null"/> when that is not recorded.</summary>
        public string? ActorLabel { get; init; }
    }
}
