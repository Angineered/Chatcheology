using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// Reads which of one run's candidates for one attachment carry an independent disproof.
    /// </summary>
    /// <remarks>
    /// <para><strong>Read-only.</strong> Every statement here is a <c>SELECT</c>, and a structural test
    /// asserts it.</para>
    /// <para><strong>Scoped to one run's own candidate identities.</strong> A disproof recorded against
    /// run 1's candidate row for some asset says nothing about run 2's candidate row for the same
    /// asset, and this reader never conflates them: it asks the question of the candidate rows
    /// belonging to the run it was given. That is what keeps a disproof from going quietly stale when
    /// evidence is refreshed.</para>
    /// </remarks>
    public sealed class Phase7CandidateDispositionReader
    {
        /// <summary>
        /// Whether every candidate of <paramref name="runID"/> for <paramref name="attachmentID"/>
        /// carries at least one independent disproof.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> only when the run holds at least one candidate for the attachment
        /// <em>and</em> every one of them is disproven.
        /// </returns>
        /// <remarks>
        /// <strong>Zero candidates is <see langword="false"/>, never vacuously true.</strong> Nothing
        /// in the frozen design licenses a vacuous reading here, and reporting that "every candidate
        /// has been individually ruled out" about an attachment nobody has ruled anything out for would
        /// be a stronger claim than the record supports — the precise class of error the project rule
        /// exists to prevent. An attachment with no candidates already has an accurate and different
        /// thing to say about itself.
        /// <para>
        /// This never becomes a resolution rule. That every remaining candidate has been disproven is
        /// not a basis for confirming anything, and the resolution service has no way to read it.
        /// </para>
        /// </remarks>
        public bool AllCandidatesIndependentlyDisproven(
            SqliteConnection connection, long runID, long attachmentID)
        {
            ArgumentNullException.ThrowIfNull(connection);

            using var command = connection.CreateCommand();
            command.CommandText = DispositionCoverageStatement;
            command.Parameters.AddWithValue("$runID", runID);
            command.Parameters.AddWithValue("$attachmentID", attachmentID);

            command.Parameters.AddWithValue(
                "$disposition",
                Phase7DecisionText.Format(Phase7CandidateDisposition.IndependentlyDisproven));

            using var reader = command.ExecuteReader();

            if (!reader.Read())
            {
                return false;
            }

            var candidateCount = reader.GetInt64(0);

            // SUM over no rows is NULL rather than zero, which is the zero-candidate case.
            var disposedCount = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);

            return candidateCount > 0 && disposedCount == candidateCount;
        }

        /// <summary>
        /// Which of one run's candidate rows for one attachment carry an independent disproof.
        /// </summary>
        /// <returns>
        /// The candidate identifiers, ascending. Empty means none of them is disproven — or that the
        /// run holds no candidate for this attachment at all, which the caller distinguishes by reading
        /// the candidate set.
        /// </returns>
        public IReadOnlyList<long> ReadIndependentlyDisprovenCandidateIDs(
            SqliteConnection connection, long runID, long attachmentID)
        {
            ArgumentNullException.ThrowIfNull(connection);

            using var command = connection.CreateCommand();
            command.CommandText = DisprovenCandidateIDsStatement;
            command.Parameters.AddWithValue("$runID", runID);
            command.Parameters.AddWithValue("$attachmentID", attachmentID);

            command.Parameters.AddWithValue(
                "$disposition",
                Phase7DecisionText.Format(Phase7CandidateDisposition.IndependentlyDisproven));

            var disproven = new List<long>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                disproven.Add(reader.GetInt64(0));
            }

            return disproven;
        }

        /// <summary>
        /// How many disposition events one candidate row carries, for tests that pin append-only
        /// behaviour.
        /// </summary>
        internal static long CountEventsForCandidate(SqliteConnection connection, long candidateID)
        {
            using var command = connection.CreateCommand();
            command.CommandText = EventCountForCandidateStatement;
            command.Parameters.AddWithValue("$candidateID", candidateID);

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Every statement this reader issues, for the structural test that asserts none of them writes.
        /// </summary>
        internal static IReadOnlyList<string> Statements =>
        [
            DispositionCoverageStatement,
            DisprovenCandidateIDsStatement,
            EventCountForCandidateStatement,
        ];

        /// <remarks>
        /// One set-based statement, never one query per candidate. The correlated <c>EXISTS</c> is
        /// driven by the deliberately created index on the disposition table's candidate column, and it
        /// asks only whether at least one disproof exists, which is why a repeated disproof changes no
        /// answer here.
        /// </remarks>
        private const string DispositionCoverageStatement =
            """
            SELECT
                COUNT(*),
                SUM(CASE WHEN EXISTS (
                        SELECT 1
                        FROM AttachmentCandidateDispositionEvent AS d
                        WHERE d.AttachmentCandidateID = c.AttachmentCandidateID
                          AND d.Disposition = $disposition
                    ) THEN 1 ELSE 0 END)
            FROM AttachmentCandidate AS c
            WHERE c.RunID = $runID AND c.AttachmentID = $attachmentID;
            """;

        private const string DisprovenCandidateIDsStatement =
            """
            SELECT DISTINCT d.AttachmentCandidateID
            FROM AttachmentCandidateDispositionEvent AS d
            INNER JOIN AttachmentCandidate AS c
                ON c.AttachmentCandidateID = d.AttachmentCandidateID
            WHERE c.RunID = $runID
              AND c.AttachmentID = $attachmentID
              AND d.Disposition = $disposition
            ORDER BY d.AttachmentCandidateID;
            """;

        private const string EventCountForCandidateStatement =
            """
            SELECT COUNT(*)
            FROM AttachmentCandidateDispositionEvent
            WHERE AttachmentCandidateID = $candidateID;
            """;
    }
}
