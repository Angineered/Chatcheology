namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// An attachment's resolution state, as replaying its event history produces it.
    /// </summary>
    /// <remarks>
    /// The first two fields are what the <c>Attachment</c> row stores; the third is derived and stored
    /// nowhere. Reconciliation compares a replayed instance of this against the stored projection, and
    /// the resolution service compares the same two before it writes anything and again before it
    /// commits.
    /// </remarks>
    public sealed class Phase7ResolutionProjection
    {
        /// <summary>Whether an asset is currently recorded.</summary>
        public required Phase7ResolutionStatus Status { get; init; }

        /// <summary>The asset currently recorded, or <see langword="null"/>.</summary>
        public required long? ResolvedMediaAssetID { get; init; }

        /// <summary>
        /// The event a replacement or a revocation would have to supersede, or
        /// <see langword="null"/> when there is none.
        /// </summary>
        /// <remarks>
        /// Null after a revocation and null for an attachment with no history, which is what makes
        /// "revoking never reactivates an older resolution" fall out of the model rather than being a
        /// rule to remember: there is nothing left to supersede, and the only legal next transition is a
        /// fresh confirmation, which carries no supersedes at all.
        /// <para>
        /// Deliberately <strong>not</strong> a stored column. A pointer column would be a second
        /// authority on the same fact, free to disagree with the history it summarises.
        /// </para>
        /// </remarks>
        public required long? CurrentEffectiveEventID { get; init; }
    }
}
