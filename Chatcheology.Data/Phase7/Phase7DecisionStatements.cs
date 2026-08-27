namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// The read statements every Stage 7.2 write service needs in order to validate a decision, held
    /// in one place because all three services ask the same questions of the same rows.
    /// </summary>
    /// <remarks>
    /// <para><strong>Three of these are restated Stage 7.1 statements, deliberately rather than
    /// reused.</strong> A Stage 7.2 write validates its evidence identities <em>inside</em> the write
    /// transaction, and <c>Microsoft.Data.Sqlite</c> requires every command on a connection with an
    /// open transaction to carry that transaction — which the closed Stage 7.1
    /// <see cref="Phase7SupportReader"/> methods, built for caller-owned read connections, do not do.
    /// Rather than widen a closed signature for a convenience, the statement text is restated here
    /// character for character and a structural test asserts each restated text is present in
    /// <see cref="Phase7SupportReader.Statements"/>, so the two cannot drift silently.</para>
    /// <para>
    /// This is the device the accepted Stage 7.1 pre-flight scope check already uses for the frozen
    /// matching-service membership queries: restate the same query rather than expose a helper, and
    /// hold the two to being identical.
    /// </para>
    /// <para>
    /// Every statement here is a <c>SELECT</c>. Nothing in this type writes.
    /// </para>
    /// </remarks>
    internal static class Phase7DecisionStatements
    {
        /// <summary>
        /// Every statement this type holds, for the structural test that asserts none of them writes.
        /// </summary>
        internal static IReadOnlyList<string> Statements =>
        [
            LatestCompletedRunContainingStatement,
            SnapshotExistsStatement,
            RunStatusStatement,
            AttachmentProjectionStatement,
            MediaAssetExistsStatement,
        ];

        /// <summary>
        /// The subset restated from Stage 7.1, for the test that asserts each is character for
        /// character one of <see cref="Phase7SupportReader.Statements"/>.
        /// </summary>
        internal static IReadOnlyList<string> RestatedStageSevenOneStatements =>
        [
            LatestCompletedRunContainingStatement,
            SnapshotExistsStatement,
            RunStatusStatement,
        ];

        /// <summary>
        /// The attachment's current completed evidence run, restated from Stage 7.1's correction C1.
        /// </summary>
        /// <remarks>
        /// <strong>Attachment-scoped, never workspace-global.</strong> Two runs may cover two different
        /// conversations, and the later one is not this attachment's evidence run unless it evaluated
        /// it. Derived from the run's own snapshot rows, which the frozen design makes the authority on
        /// what a run covered.
        /// <para>
        /// A null result means no completed run has ever evaluated this attachment, which is a distinct
        /// and honest answer rather than an error.
        /// </para>
        /// </remarks>
        internal const string LatestCompletedRunContainingStatement =
            """
            SELECT MAX(s.RunID)
            FROM AttachmentEvidenceSnapshot AS s
            INNER JOIN Phase7Run AS r ON r.RunID = s.RunID
            WHERE s.AttachmentID = $attachmentID
              AND r.Status = $completed;
            """;

        /// <summary>
        /// Whether one run actually evaluated one attachment, restated from Stage 7.1.
        /// </summary>
        /// <remarks>
        /// The frozen authority on run scope. A review names a snapshot, so the snapshot has to exist.
        /// </remarks>
        internal const string SnapshotExistsStatement =
            """
            SELECT COUNT(*)
            FROM AttachmentEvidenceSnapshot
            WHERE RunID = $runID AND AttachmentID = $attachmentID;
            """;

        /// <summary>One run's lifecycle status, restated from Stage 7.1.</summary>
        internal const string RunStatusStatement =
            "SELECT Status FROM Phase7Run WHERE RunID = $runID;";

        /// <summary>
        /// One attachment's current resolution projection.
        /// </summary>
        /// <remarks>
        /// No row means the attachment does not exist, which is a caller mistake rather than an
        /// unresolved attachment — the two must stay distinguishable, so the absence is reported and
        /// never read as unresolved.
        /// </remarks>
        internal const string AttachmentProjectionStatement =
            """
            SELECT ResolutionStatus, ResolvedMediaAssetID
            FROM Attachment
            WHERE AttachmentID = $attachmentID;
            """;

        /// <summary>
        /// Whether a confirmation's target asset exists at all.
        /// </summary>
        /// <remarks>
        /// The foreign key is the real guarantee; this check exists so the caller gets a diagnostic
        /// naming the asset rather than a constraint violation. It is the <em>only</em> membership
        /// requirement placed on a confirmation target: nothing here asks whether the asset is a
        /// candidate of any run, because requiring that would make Stage 7.1 evidence a necessary
        /// condition for identity.
        /// </remarks>
        internal const string MediaAssetExistsStatement =
            "SELECT COUNT(*) FROM MediaAsset WHERE MediaAssetID = $mediaAssetID;";
    }
}
