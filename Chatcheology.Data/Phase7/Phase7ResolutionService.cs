using System.Globalization;
using Chatcheology.Data.Workspace;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// The only code in Phase 7 that changes an attachment's resolution projection.
    /// </summary>
    /// <remarks>
    /// <para><strong>Three transitions, no fourth.</strong> A confirmation sets the current asset, a
    /// replacement changes it to a different one, a revocation clears it. Each writes exactly two
    /// tables — the resolution history and the <c>Attachment</c> projection — in one transaction, so
    /// neither can exist without the other.</para>
    /// <para><strong>Every confirmation states an independent basis.</strong> The vocabulary is closed
    /// at two values and has no <c>Other</c>, and there is no field anywhere on the three request types
    /// in which a rank, a tier, a support value, a candidate reference, a score or a threshold could be
    /// recorded as a basis. Nothing here can be reached from a ranking result: this service reads no
    /// candidate row, no occurrence row and no support column at all, which is a property of its
    /// statement set rather than a promise about its code.</para>
    /// <para><strong>The current-effective-event rule is enforced in the transaction.</strong> That a
    /// superseded event is the one the attachment's history currently ends on is a fact about current
    /// state rather than about the row being written, so no schema constraint can express it. Two
    /// neighbouring halves of the rule are enforced by the schema — that a replacement or revocation
    /// names something at all, and that what it names belongs to this same attachment — and this
    /// service checks the remaining half against a state it holds the writer reservation over.</para>
    /// <para><strong>Reconciliation brackets every write.</strong> Before anything is written the
    /// attachment's history is replayed and required to match the stored projection, so a transition is
    /// never written on top of an already-inconsistent state; after the write and before the commit the
    /// same comparison is made again, so a transition can never commit a new divergence.</para>
    /// <para><strong>A failed transition commits nothing.</strong> That is a statement about committed
    /// state rather than about whether SQLite executed anything: validation failures reach no
    /// data-modifying statement at all, but a postcondition failure occurs <em>after</em> the insert and
    /// the projection update have executed inside the transaction, and those are rolled back. No claim
    /// is made — and no measurement taken — about the workspace file being physically byte-identical
    /// afterwards.</para>
    /// </remarks>
    public sealed class Phase7ResolutionService
    {
        /// <summary>
        /// Records a first confirmation, moving an unresolved attachment to resolved.
        /// </summary>
        /// <exception cref="ArgumentNullException">No request was supplied.</exception>
        /// <exception cref="ArgumentException">
        /// The request has no database path, no stated basis description, a basis type outside the
        /// closed vocabulary, or a whitespace-only actor label. Nothing has been opened.
        /// </exception>
        /// <exception cref="FileNotFoundException">There is no workspace at the requested path.</exception>
        /// <exception cref="InvalidOperationException">
        /// The workspace is claimed or not at the current schema version; the attachment does not exist
        /// or is already resolved; the target asset does not exist; a supplied source run does not
        /// exist, has not completed or did not evaluate this attachment; or the attachment's stored
        /// projection already disagrees with its history. Nothing has been committed.
        /// </exception>
        public Phase7ResolutionTransitionResult Confirm(Phase7ConfirmRequest request) =>
            Confirm(request, null);

        /// <summary>
        /// Records a replacement, moving a resolved attachment to a <strong>different</strong> asset.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// As for <see cref="Confirm(Phase7ConfirmRequest)"/>, and additionally: the attachment is not
        /// resolved; the superseded event is not the attachment's current effective event; or the target
        /// asset is the one already recorded, which would make the replacement replace nothing. Nothing
        /// has been committed.
        /// </exception>
        public Phase7ResolutionTransitionResult ConfirmReplacement(
            Phase7ConfirmReplacementRequest request) =>
            ConfirmReplacement(request, null);

        /// <summary>
        /// Records a revocation, moving a resolved attachment back to unresolved with no asset.
        /// </summary>
        /// <remarks>
        /// It never reactivates an older resolution, whatever it revoked. Restoring an earlier asset
        /// requires a fresh confirmation from the resulting unresolved state, with its own basis.
        /// </remarks>
        public Phase7ResolutionTransitionResult Revoke(Phase7RevokeRequest request) =>
            Revoke(request, null);

        /// <summary>
        /// <see cref="Confirm(Phase7ConfirmRequest)"/>, with the internal observation seam.
        /// </summary>
        /// <param name="afterWritesBeforePostcondition">
        /// Run once, after the event insert and the projection update have executed inside the
        /// transaction and before the postcondition reconciliation. The seam the after-DML rollback
        /// tests need in order to act at the one moment that matters, and to observe the uncommitted
        /// rows through the transaction that wrote them. The public overload never supplies it, so no
        /// production path can, and the delegate's parameter type is internal so it cannot appear on any
        /// public signature.
        /// </param>
        internal Phase7ResolutionTransitionResult Confirm(
            Phase7ConfirmRequest request,
            Action<Phase7DecisionSession>? afterWritesBeforePostcondition)
        {
            ArgumentNullException.ThrowIfNull(request);

            var basisType = Phase7DecisionText.Format(request.IndependentBasisType);

            var basisDescription = Phase7DecisionText.RequireStatedText(
                request.IndependentBasisDescription,
                nameof(request),
                "The independent basis description");

            var actorLabel = Phase7DecisionText.AcceptOptionalText(
                request.ActorLabel, nameof(request), "The actor label");

            return Apply(
                request.DatabasePath,
                "a Phase 7 confirmation",
                request.AttachmentID,
                request.SourceRunID,
                actorLabel,
                afterWritesBeforePostcondition,
                (session, stored, replayed) =>
                {
                    RequireStatus(
                        request.AttachmentID,
                        stored,
                        Phase7ResolutionStatus.Unresolved,
                        "A first confirmation",
                        "changing an existing resolution is a replacement, and undoing one is a " +
                        "revocation");

                    Phase7DecisionValidation.RequireMediaAssetExists(session, request.MediaAssetID);

                    return new PlannedEvent(
                        Phase7ResolutionEventType.Confirm,
                        MediaAssetID: request.MediaAssetID,
                        IndependentBasisType: basisType,
                        IndependentBasisDescription: basisDescription,
                        RevocationReason: null,
                        SupersedesEventID: null,
                        PostStatus: Phase7ResolutionStatus.Resolved,
                        PostMediaAssetID: request.MediaAssetID);
                });
        }

        /// <summary>
        /// <see cref="ConfirmReplacement(Phase7ConfirmReplacementRequest)"/>, with the internal
        /// observation seam described on <see cref="Confirm(Phase7ConfirmRequest, Action{Phase7DecisionSession})"/>.
        /// </summary>
        internal Phase7ResolutionTransitionResult ConfirmReplacement(
            Phase7ConfirmReplacementRequest request,
            Action<Phase7DecisionSession>? afterWritesBeforePostcondition)
        {
            ArgumentNullException.ThrowIfNull(request);

            var basisType = Phase7DecisionText.Format(request.IndependentBasisType);

            var basisDescription = Phase7DecisionText.RequireStatedText(
                request.IndependentBasisDescription,
                nameof(request),
                "The independent basis description");

            var actorLabel = Phase7DecisionText.AcceptOptionalText(
                request.ActorLabel, nameof(request), "The actor label");

            return Apply(
                request.DatabasePath,
                "a Phase 7 replacement confirmation",
                request.AttachmentID,
                request.SourceRunID,
                actorLabel,
                afterWritesBeforePostcondition,
                (session, stored, replayed) =>
                {
                    RequireStatus(
                        request.AttachmentID,
                        stored,
                        Phase7ResolutionStatus.Resolved,
                        "A replacement",
                        "there is nothing to replace on an attachment with no recorded asset");

                    RequireSupersedesCurrentEffective(
                        request.AttachmentID, request.SupersedesEventID, replayed, "replacement");

                    Phase7DecisionValidation.RequireMediaAssetExists(session, request.MediaAssetID);

                    RequireDifferentAsset(request.AttachmentID, request.MediaAssetID, stored);

                    return new PlannedEvent(
                        Phase7ResolutionEventType.ConfirmReplacement,
                        MediaAssetID: request.MediaAssetID,
                        IndependentBasisType: basisType,
                        IndependentBasisDescription: basisDescription,
                        RevocationReason: null,
                        SupersedesEventID: request.SupersedesEventID,
                        PostStatus: Phase7ResolutionStatus.Resolved,
                        PostMediaAssetID: request.MediaAssetID);
                });
        }

        /// <summary>
        /// <see cref="Revoke(Phase7RevokeRequest)"/>, with the internal observation seam described on
        /// <see cref="Confirm(Phase7ConfirmRequest, Action{Phase7DecisionSession})"/>.
        /// </summary>
        internal Phase7ResolutionTransitionResult Revoke(
            Phase7RevokeRequest request,
            Action<Phase7DecisionSession>? afterWritesBeforePostcondition)
        {
            ArgumentNullException.ThrowIfNull(request);

            var revocationReason = Phase7DecisionText.RequireStatedText(
                request.RevocationReason, nameof(request), "The revocation reason");

            var actorLabel = Phase7DecisionText.AcceptOptionalText(
                request.ActorLabel, nameof(request), "The actor label");

            return Apply(
                request.DatabasePath,
                "a Phase 7 revocation",
                request.AttachmentID,
                request.SourceRunID,
                actorLabel,
                afterWritesBeforePostcondition,
                (session, stored, replayed) =>
                {
                    RequireStatus(
                        request.AttachmentID,
                        stored,
                        Phase7ResolutionStatus.Resolved,
                        "A revocation",
                        "there is nothing to undo on an attachment with no recorded asset");

                    RequireSupersedesCurrentEffective(
                        request.AttachmentID, request.SupersedesEventID, replayed, "revocation");

                    return new PlannedEvent(
                        Phase7ResolutionEventType.Revoke,
                        MediaAssetID: null,
                        IndependentBasisType: null,
                        IndependentBasisDescription: null,
                        RevocationReason: revocationReason,
                        SupersedesEventID: request.SupersedesEventID,
                        PostStatus: Phase7ResolutionStatus.Unresolved,
                        PostMediaAssetID: null);
                });
        }

        /// <summary>
        /// The one implementation every transition runs through: lock, guard, transaction, precondition
        /// reconciliation, transition validation, insert, projection update, postcondition
        /// reconciliation, commit.
        /// </summary>
        /// <remarks>
        /// One place, three transitions. Three copies of this protocol would be three chances for one of
        /// them to lose a check, and the property that matters — that the projection changes only
        /// through here, always with its event, always reconciled — is only checkable if there is a
        /// single path to check.
        /// </remarks>
        private static Phase7ResolutionTransitionResult Apply(
            string databasePath,
            string operationDescription,
            long attachmentID,
            long? sourceRunID,
            string? actorLabel,
            Action<Phase7DecisionSession>? afterWritesBeforePostcondition,
            Func<Phase7DecisionSession, Phase7ResolutionProjection, Phase7ResolutionProjection,
                PlannedEvent> plan)
        {
            using var session = Phase7DecisionSession.Open(databasePath, operationDescription);

            session.Begin();

            var (status, resolvedMediaAssetID) =
                Phase7DecisionValidation.ReadProjection(session, attachmentID);

            var stored = new Phase7ResolutionProjection
            {
                Status = status,
                ResolvedMediaAssetID = resolvedMediaAssetID,
                CurrentEffectiveEventID = null,
            };

            // Precondition. Replaying the existing history both establishes the current effective
            // event this transition may supersede and proves the projection it is about to change is
            // not already inconsistent with the history that produced it.
            var replayed = Phase7ResolutionReconciliation.VerifyAttachment(
                session.Connection, session.Transaction, attachmentID, stored);

            var planned = plan(session, stored, replayed);

            if (sourceRunID is { } citedRunID)
            {
                // Provenance, validated for truthfulness and never for authority: the snapshot cited
                // has to be one that exists, published, and covered this attachment. It is not
                // required to be current, and it never constrains which asset may be confirmed.
                Phase7DecisionValidation.RequireCompletedRun(session, citedRunID);

                Phase7DecisionValidation.RequireRunEvaluatedAttachment(
                    session, citedRunID, attachmentID);
            }

            var occurredAtUtc = DateTime.UtcNow;

            var eventID = InsertEvent(
                session, attachmentID, planned, sourceRunID, actorLabel, occurredAtUtc);

            UpdateProjection(session, attachmentID, stored, planned);

            afterWritesBeforePostcondition?.Invoke(session);

            // Postcondition. The same comparison over the history this transition just extended, so a
            // transition cannot commit a divergence it created.
            var committed = new Phase7ResolutionProjection
            {
                Status = planned.PostStatus,
                ResolvedMediaAssetID = planned.PostMediaAssetID,
                CurrentEffectiveEventID = null,
            };

            _ = Phase7ResolutionReconciliation.VerifyAttachment(
                session.Connection, session.Transaction, attachmentID, committed);

            session.Commit();

            return new Phase7ResolutionTransitionResult
            {
                AttachmentResolutionEventID = eventID,
                AttachmentID = attachmentID,
                EventType = planned.EventType,
                ResolutionStatus = planned.PostStatus,
                ResolvedMediaAssetID = planned.PostMediaAssetID,
                SupersedesEventID = planned.SupersedesEventID,
                SourceRunID = sourceRunID,
                OccurredAtUtc = occurredAtUtc,
                ActorLabel = actorLabel,
            };
        }

        private static long InsertEvent(
            Phase7DecisionSession session,
            long attachmentID,
            PlannedEvent planned,
            long? sourceRunID,
            string? actorLabel,
            DateTime occurredAtUtc)
        {
            using var command = session.CreateCommand(InsertResolutionEventStatement);

            command.Parameters.AddWithValue("$attachmentID", attachmentID);

            command.Parameters.AddWithValue(
                "$eventType", Phase7DecisionText.Format(planned.EventType));

            command.Parameters.AddWithValue(
                "$mediaAssetID", planned.MediaAssetID ?? (object)DBNull.Value);

            command.Parameters.AddWithValue(
                "$basisType", planned.IndependentBasisType ?? (object)DBNull.Value);

            command.Parameters.AddWithValue(
                "$basisDescription", planned.IndependentBasisDescription ?? (object)DBNull.Value);

            command.Parameters.AddWithValue(
                "$revocationReason", planned.RevocationReason ?? (object)DBNull.Value);

            command.Parameters.AddWithValue(
                "$supersedesEventID", planned.SupersedesEventID ?? (object)DBNull.Value);

            command.Parameters.AddWithValue("$sourceRunID", sourceRunID ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$actorLabel", actorLabel ?? (object)DBNull.Value);

            command.Parameters.AddWithValue(
                "$occurredAtUtc",
                occurredAtUtc.ToString(
                    WorkspaceDatabase.UtcTimestampFormat, CultureInfo.InvariantCulture));

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Moves the projection, restating the pre-state it was validated against in the statement
        /// itself.
        /// </summary>
        /// <remarks>
        /// The guarded predicate cannot fail inside a transaction that has held the writer reservation
        /// since before the pre-state was read, which is precisely why it is worth having: it costs
        /// nothing, it says in the statement what the transition believed, and it turns a hypothetical
        /// same-transaction logic error into a refusal rather than a silent divergence. The null-safe
        /// <c>IS</c> comparison is needed because the expected asset legitimately is null on an
        /// unresolved attachment, where ordinary equality would be unknown rather than true.
        /// </remarks>
        private static void UpdateProjection(
            Phase7DecisionSession session,
            long attachmentID,
            Phase7ResolutionProjection stored,
            PlannedEvent planned)
        {
            using var command = session.CreateCommand(UpdateAttachmentProjectionStatement);

            command.Parameters.AddWithValue(
                "$status", Phase7DecisionText.Format(planned.PostStatus));

            command.Parameters.AddWithValue(
                "$resolvedMediaAssetID", planned.PostMediaAssetID ?? (object)DBNull.Value);

            command.Parameters.AddWithValue("$attachmentID", attachmentID);

            command.Parameters.AddWithValue(
                "$expectedStatus", Phase7DecisionText.Format(stored.Status));

            command.Parameters.AddWithValue(
                "$expectedMediaAssetID", stored.ResolvedMediaAssetID ?? (object)DBNull.Value);

            var changed = command.ExecuteNonQuery();

            if (changed == 1)
            {
                return;
            }

            throw new InvalidOperationException(
                $"The projection update for attachment {Text(attachmentID)} changed {Text(changed)} " +
                $"rows where exactly one was expected. The statement restates the pre-state this " +
                $"transition validated, so this means the row is not in the state it was read in — " +
                $"which cannot happen under the writer reservation and therefore indicates a defect " +
                $"rather than contention. Nothing is committed.");
        }

        private static void RequireStatus(
            long attachmentID,
            Phase7ResolutionProjection stored,
            Phase7ResolutionStatus required,
            string what,
            string because)
        {
            if (stored.Status == required)
            {
                return;
            }

            throw new InvalidOperationException(
                $"{what} is valid only while attachment {Text(attachmentID)} is {required}, and it is " +
                $"currently {stored.Status}: {because}. Nothing has been committed.");
        }

        private static void RequireSupersedesCurrentEffective(
            long attachmentID,
            long supersedesEventID,
            Phase7ResolutionProjection replayed,
            string what)
        {
            if (replayed.CurrentEffectiveEventID == supersedesEventID)
            {
                return;
            }

            throw new InvalidOperationException(
                $"A {what} must supersede attachment {Text(attachmentID)}'s current effective " +
                $"resolution event, which is {Text(replayed.CurrentEffectiveEventID)}; it names " +
                $"{Text(supersedesEventID)}. An event that merely exists somewhere in the " +
                $"attachment's history is not what the transition acts against, because acting " +
                $"against a superseded decision would silently discard everything decided after it. " +
                $"Nothing has been committed.");
        }

        private static void RequireDifferentAsset(
            long attachmentID, long target, Phase7ResolutionProjection stored)
        {
            if (target != stored.ResolvedMediaAssetID)
            {
                return;
            }

            throw new InvalidOperationException(
                $"A replacement must change the asset, and asset {Text(target)} is already what " +
                $"attachment {Text(attachmentID)} is resolved to. Each transition has exactly one " +
                $"job — a confirmation sets the asset, a replacement changes it, a revocation clears " +
                $"it — so a replacement that left the asset where it was would give one operation a " +
                $"second meaning it does not have, and there is deliberately no reaffirmation " +
                $"operation to serve that case. Nothing has been committed.");
        }

        private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Text(long? value) => value is { } known ? Text(known) : "none";

        /// <summary>
        /// What a transition decided to write, once its pre-state has been validated.
        /// </summary>
        /// <remarks>
        /// The three request types make the invalid <em>combinations</em> unrepresentable to a caller;
        /// this makes the valid ones uniform to the writer, so one insert and one projection update
        /// serve all three transitions without a branch that could diverge between them.
        /// </remarks>
        private sealed record PlannedEvent(
            Phase7ResolutionEventType EventType,
            long? MediaAssetID,
            string? IndependentBasisType,
            string? IndependentBasisDescription,
            string? RevocationReason,
            long? SupersedesEventID,
            Phase7ResolutionStatus PostStatus,
            long? PostMediaAssetID);

        /// <summary>
        /// Every statement this service issues, for the structural tests that assert its two
        /// data-modifying statements target only the resolution history and the attachment projection,
        /// and that it reads no candidate, occurrence or support column.
        /// </summary>
        internal static IReadOnlyList<string> Statements =>
        [
            InsertResolutionEventStatement,
            UpdateAttachmentProjectionStatement,
            Phase7DecisionStatements.AttachmentProjectionStatement,
            Phase7DecisionStatements.MediaAssetExistsStatement,
            Phase7DecisionStatements.RunStatusStatement,
            Phase7DecisionStatements.SnapshotExistsStatement,
        ];

        private const string InsertResolutionEventStatement =
            """
            INSERT INTO AttachmentResolutionEvent (
                AttachmentID,
                EventType,
                MediaAssetID,
                IndependentBasisType,
                IndependentBasisDescription,
                RevocationReason,
                SupersedesEventID,
                SourceRunID,
                ActorLabel,
                OccurredAtUtc)
            VALUES (
                $attachmentID, $eventType, $mediaAssetID, $basisType, $basisDescription,
                $revocationReason, $supersedesEventID, $sourceRunID, $actorLabel, $occurredAtUtc)
            RETURNING AttachmentResolutionEventID;
            """;

        private const string UpdateAttachmentProjectionStatement =
            """
            UPDATE Attachment
            SET ResolutionStatus = $status, ResolvedMediaAssetID = $resolvedMediaAssetID
            WHERE AttachmentID = $attachmentID
              AND ResolutionStatus = $expectedStatus
              AND ResolvedMediaAssetID IS $expectedMediaAssetID;
            """;
    }
}
