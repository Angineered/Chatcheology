using System.Globalization;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// The checks every Stage 7.2 write makes against the evidence identities it names, all of them
    /// inside the caller's write transaction.
    /// </summary>
    /// <remarks>
    /// <para><strong>Inside the transaction, not before it.</strong> Every check here reads through
    /// <see cref="Phase7DecisionSession.Transaction"/>, which holds SQLite's writer reservation from
    /// before the first read. A check performed outside it would be a check of a state another writer
    /// could still change, which is exactly the hole the reservation exists to close.</para>
    /// <para><strong>Currency is attachment-scoped.</strong> "The current run" means
    /// <c>MAX(RunID)</c> over completed runs that hold an evidence snapshot row <em>for this
    /// attachment</em> — never the workspace-global latest completed run, which may legitimately be a
    /// run covering a different conversation entirely.</para>
    /// <para>
    /// None of these checks identifies anything. They establish that a decision names real, current,
    /// published evidence identities; what the decision itself means is the human's statement, and no
    /// check here supplies or supplements it.
    /// </para>
    /// </remarks>
    internal static class Phase7DecisionValidation
    {
        /// <summary>
        /// One attachment's current resolution projection, refusing an attachment that is not there.
        /// </summary>
        /// <remarks>
        /// A missing attachment is a caller mistake, reported as one. It is emphatically not read as an
        /// unresolved attachment: "there is no such attachment" and "this attachment has no asset" are
        /// different facts, and collapsing them would let a typo look like a finding.
        /// </remarks>
        internal static (Phase7ResolutionStatus Status, long? ResolvedMediaAssetID) ReadProjection(
            Phase7DecisionSession session, long attachmentID)
        {
            using var command =
                session.CreateCommand(Phase7DecisionStatements.AttachmentProjectionStatement);

            command.Parameters.AddWithValue("$attachmentID", attachmentID);

            using var reader = command.ExecuteReader();

            if (!reader.Read())
            {
                throw new InvalidOperationException(
                    $"There is no attachment {Text(attachmentID)} in this workspace. A Phase 7 " +
                    $"decision is recorded against an attachment that exists, so an identifier that " +
                    $"names nothing is a caller mistake rather than an attachment without an asset. " +
                    $"Nothing has been committed.");
            }

            var status = Phase7DecisionText.ParseResolutionStatus(reader.GetString(0));

            var resolvedMediaAssetID = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);

            return (status, resolvedMediaAssetID);
        }

        /// <summary>Refuses an attachment identifier that names nothing.</summary>
        internal static void RequireAttachmentExists(
            Phase7DecisionSession session, long attachmentID) =>
            _ = ReadProjection(session, attachmentID);

        /// <summary>
        /// Refuses a run that does not exist, or that has published nothing.
        /// </summary>
        /// <remarks>
        /// A <c>Running</c> or <c>Failed</c> run has published no evidence — the frozen lifecycle says
        /// a failed run is never treated as current — so a decision recorded <em>of</em> it would be a
        /// decision about nothing.
        /// </remarks>
        internal static void RequireCompletedRun(Phase7DecisionSession session, long runID)
        {
            var stored = session.ScalarTextOrNull(
                Phase7DecisionStatements.RunStatusStatement, ("$runID", runID));

            if (stored is null)
            {
                throw new InvalidOperationException(
                    $"There is no Phase 7 run {Text(runID)} in this workspace. A decision names the " +
                    $"run it was made against, so a run that does not exist is a caller mistake " +
                    $"rather than an absence of evidence. Nothing has been committed.");
            }

            var status = Phase7EvidenceText.ParseRunStatus(stored);

            if (status == Phase7RunStatus.Completed)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Phase 7 run {Text(runID)} has status {status} and has therefore published no " +
                $"evidence. A review or a disposition is a statement about a snapshot somebody could " +
                $"actually look at, and only a Completed run is one. Nothing has been committed.");
        }

        /// <summary>
        /// Refuses a run that did not evaluate this attachment.
        /// </summary>
        /// <remarks>
        /// The evidence snapshot row is the frozen authority on what a run covered, which is the same
        /// rule <c>Phase7SupportReader.ReadAttachmentSupport</c> enforces for reads.
        /// </remarks>
        internal static void RequireRunEvaluatedAttachment(
            Phase7DecisionSession session, long runID, long attachmentID)
        {
            var rows = session.ScalarLong(
                Phase7DecisionStatements.SnapshotExistsStatement,
                ("$runID", runID),
                ("$attachmentID", attachmentID));

            if (rows == 1)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Phase 7 run {Text(runID)} holds no evidence snapshot row for attachment " +
                $"{Text(attachmentID)}, so that run did not evaluate it. This is a fact about the " +
                $"run's scope rather than about the attachment, and a decision cannot name a snapshot " +
                $"that does not exist. Nothing has been committed.");
        }

        /// <summary>
        /// The attachment's latest completed run that actually evaluated it, or null when none has.
        /// </summary>
        internal static long? ReadLatestCompletedRunContaining(
            Phase7DecisionSession session, long attachmentID) =>
            session.ScalarLongOrNull(
                Phase7DecisionStatements.LatestCompletedRunContainingStatement,
                ("$attachmentID", attachmentID),
                ("$completed", Phase7EvidenceText.Completed));

        /// <summary>
        /// Refuses a new decision recorded against a snapshot that has already been superseded for this
        /// attachment.
        /// </summary>
        /// <param name="what">
        /// What is being recorded, as a noun phrase such as <c>review</c> or <c>disposition</c>.
        /// </param>
        /// <remarks>
        /// A review or a disposition is a human judgement about the evidence as it currently stands.
        /// Recording a <em>new</em> one against a snapshot a later run has already replaced would
        /// create a row that can never be current and whose only possible effect is to be misread as
        /// current. Refusing it at the boundary makes "a stale decision that looks current" impossible
        /// to create, rather than leaving every future reader to remember to scope its query.
        /// <para>
        /// This touches no history. Every decision recorded against what was then the current run
        /// remains valid audit history for ever; only new writes are gated.
        /// </para>
        /// </remarks>
        internal static void RequireAttachmentCurrentRun(
            Phase7DecisionSession session, long attachmentID, long runID, string what)
        {
            var current = ReadLatestCompletedRunContaining(session, attachmentID);

            if (current == runID)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Phase 7 run {Text(runID)} is no longer attachment {Text(attachmentID)}'s current " +
                $"evidence run: run {Text(current)} is. A new {what} names the evidence it was made " +
                $"against, and evidence that has already been superseded for this attachment cannot " +
                $"be what a new judgement was made against. Read the current run and record the " +
                $"{what} against that; any {what} already recorded against run {Text(runID)} remains " +
                $"valid history and is untouched. Nothing has been committed.");
        }

        /// <summary>
        /// Refuses a confirmation target that is not a media asset in this workspace.
        /// </summary>
        /// <remarks>
        /// This is the <strong>only</strong> membership requirement on a confirmation target. Nothing
        /// asks whether the asset is a candidate of any run: requiring that would make Stage 7.1
        /// evidence a necessary condition for identity, which is the same closure boundary violated
        /// from the other side, and it would make an externally established target unrecordable.
        /// </remarks>
        internal static void RequireMediaAssetExists(
            Phase7DecisionSession session, long mediaAssetID)
        {
            var rows = session.ScalarLong(
                Phase7DecisionStatements.MediaAssetExistsStatement,
                ("$mediaAssetID", mediaAssetID));

            if (rows == 1)
            {
                return;
            }

            throw new InvalidOperationException(
                $"There is no media asset {Text(mediaAssetID)} in this workspace, so nothing can be " +
                $"confirmed as being it. Nothing has been committed.");
        }

        private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Text(long? value) =>
            value is { } known ? Text(known) : "none";
    }
}
