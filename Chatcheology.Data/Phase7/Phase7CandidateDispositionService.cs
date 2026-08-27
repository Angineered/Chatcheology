using System.Globalization;
using Chatcheology.Data.Workspace;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// Appends one human disproof of one candidate row, and does nothing else.
    /// </summary>
    /// <remarks>
    /// <para><strong>What this service cannot do.</strong> It writes exactly one table,
    /// <c>AttachmentCandidateDispositionEvent</c>, through exactly one statement. It never writes the
    /// <c>Attachment</c> resolution projection, never writes an evidence or candidate row, and has no
    /// resolution path of any kind. A structural test asserts that from the statement set.</para>
    /// <para><strong>Disproving the currently resolved asset does not revoke anything.</strong> The
    /// axes are independent: recording that a candidate is disproven is a statement about that
    /// candidate, and undoing a resolution is a separate deliberate act that carries its own written
    /// reason. Wiring one to the other would be an automatic resolution change, which is exactly what
    /// the frozen design forbids — in either direction.</para>
    /// <para><strong>Repeat disproofs append.</strong> A second person independently ruling out the
    /// same candidate is a second fact, and the derived read asks only whether <em>at least one</em>
    /// disproof exists, so duplicates change no derived answer.</para>
    /// <para><strong>Every failure path reaches no data-modifying statement</strong>, for the same
    /// reason as the review service: all validation precedes the single insert, and there is no
    /// post-insert step because there is no projection to reconcile.</para>
    /// </remarks>
    public sealed class Phase7CandidateDispositionService
    {
        /// <summary>
        /// Records one <see cref="Phase7CandidateDisposition.IndependentlyDisproven"/> event against
        /// the candidate row the request names.
        /// </summary>
        /// <exception cref="ArgumentNullException">No request was supplied.</exception>
        /// <exception cref="ArgumentException">
        /// The request has no database path, no stated reason, or a whitespace-only actor label.
        /// Nothing has been opened.
        /// </exception>
        /// <exception cref="FileNotFoundException">
        /// There is no workspace at the requested path. <strong>No file is created.</strong>
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The workspace is claimed or not at the current schema version; or the candidate row does not
        /// exist, its run has not completed, or its run is no longer the attachment's current evidence
        /// run. Nothing has been committed and the workspace is unchanged.
        /// </exception>
        /// <exception cref="Microsoft.Data.Sqlite.SqliteException">
        /// Another conforming SQLite writer held the writer reservation (<c>SQLITE_BUSY</c>).
        /// </exception>
        public Phase7CandidateDispositionResult Record(Phase7CandidateDispositionRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            var reason = Phase7DecisionText.RequireStatedText(
                request.Reason, nameof(request), "The disposition reason");

            var actorLabel = Phase7DecisionText.AcceptOptionalText(
                request.ActorLabel, nameof(request), "The actor label");

            using var session = Phase7DecisionSession.Open(
                request.DatabasePath, "a Phase 7 candidate disposition");

            session.Begin();

            var candidate = ReadCandidate(session, request.AttachmentCandidateID);

            Phase7DecisionValidation.RequireCompletedRun(session, candidate.RunID);

            Phase7DecisionValidation.RequireAttachmentCurrentRun(
                session, candidate.AttachmentID, candidate.RunID, "disposition");

            var occurredAtUtc = DateTime.UtcNow;

            long eventID;

            using (var command = session.CreateCommand(InsertDispositionEventStatement))
            {
                command.Parameters.AddWithValue("$candidateID", request.AttachmentCandidateID);

                command.Parameters.AddWithValue(
                    "$disposition",
                    Phase7DecisionText.Format(
                        Phase7CandidateDisposition.IndependentlyDisproven));

                command.Parameters.AddWithValue("$reason", reason);
                command.Parameters.AddWithValue("$actorLabel", actorLabel ?? (object)DBNull.Value);

                command.Parameters.AddWithValue(
                    "$occurredAtUtc",
                    occurredAtUtc.ToString(
                        WorkspaceDatabase.UtcTimestampFormat, CultureInfo.InvariantCulture));

                eventID = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            session.Commit();

            return new Phase7CandidateDispositionResult
            {
                AttachmentCandidateDispositionEventID = eventID,
                AttachmentCandidateID = request.AttachmentCandidateID,
                AttachmentID = candidate.AttachmentID,
                RunID = candidate.RunID,
                MediaAssetID = candidate.MediaAssetID,
                Disposition = Phase7CandidateDisposition.IndependentlyDisproven,
                OccurredAtUtc = occurredAtUtc,
                ActorLabel = actorLabel,
            };
        }

        /// <summary>
        /// Reads the candidate row's own attachment, run and asset, refusing one that is not there.
        /// </summary>
        /// <remarks>
        /// This is where the relationship becomes unspoofable: the three identities come from the row,
        /// so a caller cannot claim a candidate belongs to an attachment or a run it does not.
        /// </remarks>
        private static (long AttachmentID, long RunID, long MediaAssetID) ReadCandidate(
            Phase7DecisionSession session, long candidateID)
        {
            using var command = session.CreateCommand(CandidateRowStatement);
            command.Parameters.AddWithValue("$candidateID", candidateID);

            using var reader = command.ExecuteReader();

            if (!reader.Read())
            {
                throw new InvalidOperationException(
                    $"There is no attachment candidate " +
                    $"{candidateID.ToString(CultureInfo.InvariantCulture)} in this workspace. A " +
                    $"disproof names the exact candidate row it rules out, so an identifier that " +
                    $"names nothing is a caller mistake. Nothing has been committed.");
            }

            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }

        /// <summary>
        /// Every statement this service issues, for the structural tests that assert the one
        /// data-modifying statement targets <c>AttachmentCandidateDispositionEvent</c>.
        /// </summary>
        internal static IReadOnlyList<string> Statements =>
        [
            InsertDispositionEventStatement,
            CandidateRowStatement,
            Phase7DecisionStatements.RunStatusStatement,
            Phase7DecisionStatements.LatestCompletedRunContainingStatement,
        ];

        private const string InsertDispositionEventStatement =
            """
            INSERT INTO AttachmentCandidateDispositionEvent (
                AttachmentCandidateID,
                Disposition,
                Reason,
                ActorLabel,
                OccurredAtUtc)
            VALUES ($candidateID, $disposition, $reason, $actorLabel, $occurredAtUtc)
            RETURNING AttachmentCandidateDispositionEventID;
            """;

        private const string CandidateRowStatement =
            """
            SELECT AttachmentID, RunID, MediaAssetID
            FROM AttachmentCandidate
            WHERE AttachmentCandidateID = $candidateID;
            """;
    }
}
