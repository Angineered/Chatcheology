namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// One human disproof of one specific candidate row of one specific run.
    /// </summary>
    /// <remarks>
    /// <para><strong>The candidate identifier is the only identity a caller supplies.</strong> The
    /// attachment, the run and the asset are read from the candidate row itself, which already pins all
    /// three through its own foreign keys and unique constraint. There is therefore no field in which a
    /// caller could supply a mismatched relationship, and no way for the recorded disproof to disagree
    /// with the evidence it names.</para>
    /// <para><strong>There is no disposition field.</strong>
    /// <see cref="Phase7CandidateDisposition.IndependentlyDisproven"/> is the only value the schema
    /// permits, so the service writes the literal.</para>
    /// <para><strong>There is no ranking or support field.</strong> No dimension reference, no tier
    /// index, no support value, nothing. The frozen rule is that a disproof must state an independent
    /// non-ranking basis, and the narrowest way to enforce it structurally is to give the API nowhere
    /// to put a ranking fact.</para>
    /// </remarks>
    public sealed class Phase7CandidateDispositionRequest
    {
        /// <summary>
        /// An existing workspace at schema version
        /// <see cref="Workspace.WorkspaceDatabase.SchemaVersion"/>.
        /// </summary>
        public required string DatabasePath { get; init; }

        /// <summary>
        /// The exact candidate row being disproven.
        /// </summary>
        /// <remarks>
        /// A candidate row belongs to one run, so disproving it says nothing about the same asset's
        /// candidate row in a later run. That later row is a new candidate identity and has to be ruled
        /// out on its own, which is what stops a disproof from going quietly stale across snapshots.
        /// </remarks>
        public required long AttachmentCandidateID { get; init; }

        /// <summary>
        /// Why this specific candidate is disproven, on a basis of its own.
        /// </summary>
        /// <remarks>
        /// Required, non-empty and stored verbatim. Phase 6 ranking or support evidence is not
        /// sufficient content for it — agreeing with a ranking is not disproving a candidate — but no
        /// code can verify that of human prose, and nothing here pretends to. What is enforced is that
        /// the field says something inspectable, and that the API offers no ranking fact to put in it.
        /// </remarks>
        public required string Reason { get; init; }

        /// <summary>
        /// Who disproved it, or <see langword="null"/> when that is not recorded.
        /// </summary>
        public string? ActorLabel { get; init; }
    }
}
