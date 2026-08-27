using System.Globalization;
using Chatcheology.Data.Workspace;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// Appends one human review of one attachment's evidence, and does nothing else.
    /// </summary>
    /// <remarks>
    /// <para><strong>What this service cannot do.</strong> It writes exactly one table,
    /// <c>AttachmentReviewEvent</c>, and exactly one statement does the writing. It never writes
    /// <c>Attachment.ResolutionStatus</c> or <c>Attachment.ResolvedMediaAssetID</c>, never writes an
    /// evidence, candidate, occurrence or adjacent-context row, never writes a resolution or
    /// disposition event, and contains no confirmation, replacement or revocation path of any kind.
    /// That is a property of its statement set rather than a promise about its code, and a structural
    /// test asserts it.</para>
    /// <para><strong>A review is permitted on a resolved attachment.</strong> The four axes are
    /// independent by construction: an attachment resolved on an independent basis does not make
    /// "this snapshot's evidence was insufficient to identify it" a false statement, and nothing in the
    /// frozen design or the schema gates a review by resolution status. Inventing such a gate here
    /// would collapse two axes the design keeps apart.</para>
    /// <para><strong>Repeat reviews append.</strong> Two reviews of the same snapshot, at different
    /// times or by different people, are two genuine facts. The table carries no uniqueness constraint
    /// and the frozen design calls it append-only, so a second review is recorded rather than refused;
    /// no uniqueness rule is invented here.</para>
    /// <para><strong>Every failure path reaches no data-modifying statement.</strong> All validation
    /// happens before the single insert, and there is no post-insert step, because there is no
    /// projection to reconcile. So for this service — unlike a resolution transition — a refusal really
    /// does mean nothing was written at all.</para>
    /// <para>
    /// What the service structurally enforces is that the outcome vocabulary is a single literal, that
    /// the snapshot named exists, is completed and covers this attachment, that the snapshot is still
    /// this attachment's current one, and that nothing about resolution changes. It cannot inspect
    /// whether the notes truthfully describe a review, and it claims nothing of the sort.
    /// </para>
    /// </remarks>
    public sealed class Phase7ReviewService
    {
        /// <summary>
        /// Records one <see cref="Phase7ReviewOutcome.ReviewedInsufficientEvidence"/> event.
        /// </summary>
        /// <exception cref="ArgumentNullException">No request was supplied.</exception>
        /// <exception cref="ArgumentException">
        /// The request has no database path, or supplied whitespace-only notes or actor label. Nothing
        /// has been opened.
        /// </exception>
        /// <exception cref="FileNotFoundException">
        /// There is no workspace at the requested path. <strong>No file is created.</strong>
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The workspace is claimed by another cooperating operation or is not at the current schema
        /// version; or the attachment or run does not exist, the run has not completed, the run did not
        /// evaluate this attachment, or the run is no longer this attachment's current evidence run.
        /// Nothing has been committed and the workspace is unchanged.
        /// </exception>
        /// <exception cref="Microsoft.Data.Sqlite.SqliteException">
        /// Another conforming SQLite writer held the writer reservation (<c>SQLITE_BUSY</c>). Nothing
        /// has been committed.
        /// </exception>
        public Phase7ReviewResult Record(Phase7ReviewRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var notes = Phase7DecisionText.AcceptOptionalText(
                request.ReviewNotes, nameof(request), "The review notes");

            var actorLabel = Phase7DecisionText.AcceptOptionalText(
                request.ActorLabel, nameof(request), "The actor label");

            using var session =
                Phase7DecisionSession.Open(request.DatabasePath, "a Phase 7 review");

            session.Begin();

            Phase7DecisionValidation.RequireAttachmentExists(session, request.AttachmentID);
            Phase7DecisionValidation.RequireCompletedRun(session, request.SourceRunID);

            Phase7DecisionValidation.RequireRunEvaluatedAttachment(
                session, request.SourceRunID, request.AttachmentID);

            Phase7DecisionValidation.RequireAttachmentCurrentRun(
                session, request.AttachmentID, request.SourceRunID, "review");

            var occurredAtUtc = DateTime.UtcNow;

            long eventID;

            using (var command = session.CreateCommand(InsertReviewEventStatement))
            {
                command.Parameters.AddWithValue("$attachmentID", request.AttachmentID);
                command.Parameters.AddWithValue("$sourceRunID", request.SourceRunID);

                command.Parameters.AddWithValue(
                    "$outcome",
                    Phase7DecisionText.Format(Phase7ReviewOutcome.ReviewedInsufficientEvidence));

                command.Parameters.AddWithValue("$notes", notes ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$actorLabel", actorLabel ?? (object)DBNull.Value);

                command.Parameters.AddWithValue(
                    "$occurredAtUtc",
                    occurredAtUtc.ToString(
                        WorkspaceDatabase.UtcTimestampFormat, CultureInfo.InvariantCulture));

                eventID = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            session.Commit();

            return new Phase7ReviewResult
            {
                AttachmentReviewEventID = eventID,
                AttachmentID = request.AttachmentID,
                SourceRunID = request.SourceRunID,
                Outcome = Phase7ReviewOutcome.ReviewedInsufficientEvidence,
                OccurredAtUtc = occurredAtUtc,
                ActorLabel = actorLabel,
            };
        }

        /// <summary>
        /// Every statement this service issues, for the structural tests that assert the one
        /// data-modifying statement targets <c>AttachmentReviewEvent</c> and nothing else.
        /// </summary>
        internal static IReadOnlyList<string> Statements =>
        [
            InsertReviewEventStatement,
            Phase7DecisionStatements.AttachmentProjectionStatement,
            Phase7DecisionStatements.RunStatusStatement,
            Phase7DecisionStatements.SnapshotExistsStatement,
            Phase7DecisionStatements.LatestCompletedRunContainingStatement,
        ];

        private const string InsertReviewEventStatement =
            """
            INSERT INTO AttachmentReviewEvent (
                AttachmentID,
                SourceRunID,
                ReviewOutcome,
                ReviewNotes,
                ActorLabel,
                OccurredAtUtc)
            VALUES ($attachmentID, $sourceRunID, $outcome, $notes, $actorLabel, $occurredAtUtc)
            RETURNING AttachmentReviewEventID;
            """;
    }
}
