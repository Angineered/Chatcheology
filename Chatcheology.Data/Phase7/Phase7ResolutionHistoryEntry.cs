namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// One row of an attachment's append-only resolution history.
    /// </summary>
    /// <remarks>
    /// Read exactly as stored. Nothing here is interpreted on the way out: the vocabularies are parsed
    /// into their enums and an unrecognised stored value throws rather than being softened, and every
    /// descriptive field is passed through as written.
    /// <para>
    /// Only five of these fields take part in replay — the identifier, the attachment, the event type,
    /// the asset and the supersedes reference. The rest are provenance and audit content that replay
    /// deliberately does not consume, which is also the honest limit of what reconciliation can detect
    /// about them.
    /// </para>
    /// </remarks>
    public sealed class Phase7ResolutionHistoryEntry
    {
        /// <summary>
        /// The event's identifier, and <strong>the sole replay-ordering authority</strong>.
        /// </summary>
        /// <remarks>
        /// A plain <c>INTEGER PRIMARY KEY</c>, which SQLite assigns as one greater than the current
        /// maximum whenever a value is not supplied — the only way this table is ever written — and the
        /// table never deletes a row. It is therefore monotonically increasing under normal use, which
        /// is exactly what replay needs and what a clock cannot provide.
        /// </remarks>
        public required long AttachmentResolutionEventID { get; init; }

        /// <summary>The attachment this event belongs to.</summary>
        public required long AttachmentID { get; init; }

        /// <summary>Which of the three transitions this event records.</summary>
        public required Phase7ResolutionEventType EventType { get; init; }

        /// <summary>
        /// The asset confirmed, or <see langword="null"/> on a revocation.
        /// </summary>
        public required long? MediaAssetID { get; init; }

        /// <summary>
        /// The kind of independent basis, or <see langword="null"/> on a revocation.
        /// </summary>
        public required Phase7IndependentBasisType? IndependentBasisType { get; init; }

        /// <summary>
        /// The human-written basis statement, or <see langword="null"/> on a revocation.
        /// </summary>
        public required string? IndependentBasisDescription { get; init; }

        /// <summary>
        /// The human-written revocation reason, or <see langword="null"/> on a confirmation.
        /// </summary>
        public required string? RevocationReason { get; init; }

        /// <summary>
        /// The event this one superseded, or <see langword="null"/> for a first confirmation.
        /// </summary>
        public required long? SupersedesEventID { get; init; }

        /// <summary>The snapshot cited as provenance, or <see langword="null"/>.</summary>
        public required long? SourceRunID { get; init; }

        /// <summary>Who acted, or <see langword="null"/>.</summary>
        public required string? ActorLabel { get; init; }

        /// <summary>
        /// When the event was recorded.
        /// </summary>
        /// <remarks>
        /// Descriptive metadata. <strong>Replay never orders by this</strong>, so a skewed clock cannot
        /// corrupt the reconstruction of resolution state.
        /// </remarks>
        public required DateTime OccurredAtUtc { get; init; }
    }
}
