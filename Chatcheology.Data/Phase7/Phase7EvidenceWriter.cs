using System.Globalization;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// Owns the run's one read-write connection and its three transactions: the run row, the
    /// consistency guard that becomes the publication transaction, and the failure record.
    /// </summary>
    /// <remarks>
    /// <para><strong>T1 — the run row.</strong> One <c>Phase7Run</c> insert with
    /// <c>Status = 'Running'</c>, committed <em>before</em> any read or computation. That ordering is
    /// what makes a failure during evidence computation leave a diagnosable <c>Failed</c> run instead
    /// of leaving nothing at all. It is also the run's first write, which is what makes
    /// <c>PreRunWorkspaceFileSha256</c> — taken immediately before it — mean what the frozen schema
    /// says it means.</para>
    /// <para><strong>T2 — the consistency guard, and the publication transaction.</strong> Begun
    /// non-deferred immediately after T1 commits, on the same connection, with no work between them.
    /// <c>BeginTransaction(deferred: false)</c> takes SQLite's <c>RESERVED</c> writer reservation at
    /// <c>BEGIN</c>, and while it is held no conforming SQLite client can commit any change to the
    /// database — verified on this provider for all three shapes a competing writer can take: an
    /// <c>IMMEDIATE</c> begin, a deferred transaction's first write, and an autocommit statement. All
    /// three receive <c>SQLITE_BUSY</c>.</para>
    /// <para>
    /// The guard executes <strong>no DML at all</strong> while the read passes run, and that is
    /// load-bearing rather than tidy. A <c>RESERVED</c> transaction coexists safely with other
    /// connections holding <c>SHARED</c> and streaming rows — which is exactly what
    /// <c>WorkspaceMatchingService</c> needs — but once pages have been modified, a page-cache spill
    /// or the commit itself needs the <c>EXCLUSIVE</c> upgrade, which any reader's <c>SHARED</c> lock
    /// refuses. So no statement may modify a page until every read connection is closed, which is why
    /// the whole payload is built in memory first.
    /// </para>
    /// <para>
    /// The same transaction then inserts every evidence row, runs every reconciliation check, and sets
    /// <c>Status = 'Completed'</c>. Its commit is the single act of publication: either every row and
    /// <c>Completed</c> become visible together, or no row exists at all. There is no interval
    /// anywhere between "the reads were consistent" and "the evidence was written".
    /// </para>
    /// <para><strong>T3 — the failure record.</strong> One <c>Status = 'Failed'</c> update. It gets
    /// its <c>EXCLUSIVE</c> upgrade because T2's rollback has already released <c>RESERVED</c> and
    /// every read connection is disposed.</para>
    /// <para>
    /// No retry loop is added anywhere. The provider's default busy timeout is the only waiting this
    /// repository's conventions sanction, and it is left exactly as
    /// <c>WorkspaceDatabase.BuildConnectionString</c> leaves it.
    /// </para>
    /// </remarks>
    internal sealed class Phase7EvidenceWriter : IDisposable
    {
        private readonly SqliteConnection _connection;

        private SqliteTransaction? _guard;

        /// <remarks>
        /// Opened <c>ReadWriteCreate</c> on a file the caller has already proved exists. Opening an
        /// existing database read-write writes nothing — verified on this provider, file length and
        /// modification time unchanged, no companion file created — so the pre-run hash taken just
        /// before this still describes the pre-write state.
        /// </remarks>
        internal Phase7EvidenceWriter(string databasePath) =>
            _connection = WorkspaceDatabase.OpenConnection(databasePath);

        /// <summary>The run's read-write connection.</summary>
        internal SqliteConnection Connection => _connection;

        /// <summary>Whether the consistency guard is currently held.</summary>
        internal bool IsGuardHeld => _guard is not null;

        /// <summary>
        /// T1: inserts the <c>Running</c> row and commits it, returning the run's identifier.
        /// </summary>
        /// <remarks>
        /// The insert is inside its own using-scoped transaction, so an exception before the commit
        /// rolls it back and no row survives. A run that never began records nothing: a row would
        /// claim a run started when it did not, and its <c>StartedAtUtc</c> would be a fiction.
        /// <para>
        /// The parameters are taken rather than derived here so that a test can drive a deterministic
        /// T1 failure with a payload the frozen <c>CHECK</c> constraints reject — an empty
        /// <c>RankingDimensionSetVersion</c>, or a hash that is not 64 characters. The public service
        /// can never produce such a payload, which is exactly why that test has to go through this
        /// seam.
        /// </para>
        /// </remarks>
        internal long BeginRun(
            DateTime startedAtUtc, string preRunWorkspaceFileSha256, string rankingDimensionSetVersion)
        {
            using var transaction = _connection.BeginTransaction();

            long runID;

            using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = InsertRunStatement;

                command.Parameters.AddWithValue(
                    "$startedAtUtc",
                    startedAtUtc.ToString(
                        WorkspaceDatabase.UtcTimestampFormat, CultureInfo.InvariantCulture));

                command.Parameters.AddWithValue(
                    "$status", Phase7EvidenceText.Format(Phase7RunStatus.Running));

                command.Parameters.AddWithValue("$preRunHash", preRunWorkspaceFileSha256);
                command.Parameters.AddWithValue("$version", rankingDimensionSetVersion);

                runID = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            transaction.Commit();

            return runID;
        }

        /// <summary>
        /// T2: takes SQLite's writer reservation, executing no DML.
        /// </summary>
        /// <remarks>
        /// Written as the statement immediately following T1's commit, on the same connection, with no
        /// other work between them, so the one interval that cannot be closed — a commit needs
        /// <c>EXCLUSIVE</c>, so no lock of ours can span it — is a single method call wide. If a
        /// competing writer commits in that interval it commits <em>before</em> our reservation exists,
        /// so every read then observes that single newer committed state and no hybrid is possible.
        /// </remarks>
        /// <exception cref="SqliteException">
        /// Another conforming writer already owns the reservation (<c>SQLITE_BUSY</c>). This is a
        /// post-T1 failure, so the run is marked <c>Failed</c> rather than left unrecorded — it did
        /// begin, and it could not obtain its consistency guarantee.
        /// </exception>
        internal void BeginPublicationGuard()
        {
            if (_guard is not null)
            {
                throw new InvalidOperationException(
                    "The publication guard is already held. One run holds one guard, which is also " +
                    "the transaction that publishes.");
            }

            _guard = _connection.BeginTransaction(deferred: false);
        }

        /// <summary>
        /// Publishes the payload and completes the run, inside the guard, and commits.
        /// </summary>
        /// <param name="afterInsertsBeforeCommit">
        /// Run once, after every row is written and every check has passed but before the commit. The
        /// internal observation seam the consistency-guard tests need in order to prove that nothing is
        /// visible from another connection until the commit; the production path never supplies it.
        /// </param>
        /// <remarks>
        /// The first statement here is the first statement to modify a page <strong>after T1's committed
        /// run row</strong> — T1's own insert already wrote one — and nothing between T1's commit and
        /// this point issues DML of any kind. Every read connection must already be closed.
        /// </remarks>
        internal void Publish(
            long runID,
            Phase7RunRequest request,
            Phase7EvidencePayload payload,
            Phase7PreExistingBaseline baseline,
            DateTime completedAtUtc,
            Action? afterInsertsBeforeCommit = null)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(payload);
            ArgumentNullException.ThrowIfNull(baseline);

            var guard = _guard
                ?? throw new InvalidOperationException(
                    "The publication guard is not held, so this run has no consistency guarantee to " +
                    "publish under. Nothing has been written.");

            InsertSnapshots(guard, runID, payload);

            var assignedCandidateIDs = InsertCandidates(guard, runID, payload);

            InsertOccurrences(guard, payload, assignedCandidateIDs);
            InsertAdjacentContext(guard, runID, payload);

            Phase7RunReconciliation.Verify(
                _connection, guard, runID, request, payload, assignedCandidateIDs, baseline);

            CompleteRun(guard, runID, completedAtUtc);

            afterInsertsBeforeCommit?.Invoke();

            guard.Commit();

            _guard = null;
            guard.Dispose();
        }

        /// <summary>
        /// Rolls the guard back, if it is held, so no evidence row survives.
        /// </summary>
        internal void RollbackGuard()
        {
            if (_guard is not { } guard)
            {
                return;
            }

            _guard = null;

            try
            {
                guard.Rollback();
            }
            catch (InvalidOperationException)
            {
                // The provider already completed or already rolled the transaction back. There is
                // nothing to undo, and the primary failure is what the caller must see.
            }

            guard.Dispose();
        }

        /// <summary>
        /// T3: records that the run began and did not publish.
        /// </summary>
        /// <remarks>
        /// <c>CompletedAtUtc</c> is deliberately left null, which the frozen
        /// <c>CHECK (Status &lt;&gt; 'Completed' OR CompletedAtUtc IS NOT NULL)</c> permits for a
        /// failed run: a run that did not publish has no completion instant to record.
        /// </remarks>
        internal void MarkFailed(long runID)
        {
            using var transaction = _connection.BeginTransaction();

            using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = MarkRunFailedStatement;

                command.Parameters.AddWithValue(
                    "$status", Phase7EvidenceText.Format(Phase7RunStatus.Failed));

                command.Parameters.AddWithValue("$runID", runID);

                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public void Dispose()
        {
            // An uncommitted guard is rolled back rather than left to the connection's own disposal,
            // so the failure path's "no evidence row survives" holds by construction whichever way
            // the run unwinds.
            if (_guard is { } guard)
            {
                _guard = null;

                try
                {
                    guard.Rollback();
                }
                catch (InvalidOperationException)
                {
                    // Already completed or already rolled back by the provider. Nothing to undo.
                }

                guard.Dispose();
            }

            _connection.Dispose();
        }

        // ---------------------------------------------------------------------------------------
        // The inserts.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// One prepared command per table, with its parameters created once and reassigned per row.
        /// A real conversation publishes of the order of 4 x 10^5 statements in this transaction, and
        /// rebuilding a command per row would pay statement preparation that many times — the
        /// difference between seconds and minutes with the writer reservation held throughout.
        /// </remarks>
        private void InsertSnapshots(
            SqliteTransaction guard, long runID, Phase7EvidencePayload payload)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = guard;
            command.CommandText = InsertSnapshotStatement;

            var runIDParameter = command.Parameters.Add("$runID", SqliteType.Integer);
            var attachmentID = command.Parameters.Add("$attachmentID", SqliteType.Integer);
            var exact = command.Parameters.Add("$exact", SqliteType.Integer);
            var adjacent = command.Parameters.Add("$adjacent", SqliteType.Integer);
            var compatible = command.Parameters.Add("$compatible", SqliteType.Integer);
            var contradictory = command.Parameters.Add("$contradictory", SqliteType.Integer);
            var impossible = command.Parameters.Add("$impossible", SqliteType.Integer);
            var forced = command.Parameters.Add("$forced", SqliteType.Integer);
            var remaining = command.Parameters.Add("$remaining", SqliteType.Integer);

            runIDParameter.Value = runID;

            command.Prepare();

            foreach (var row in payload.SnapshotRows)
            {
                attachmentID.Value = row.AttachmentID;
                exact.Value = row.ExactDateCandidateCount;
                adjacent.Value = row.AdjacentDateContextCount;
                compatible.Value = Flag(row.HasAnyCompatibleAnnotatedCandidate);
                contradictory.Value = Flag(row.HasAnyContradictoryOnlyAnnotatedCandidate);
                impossible.Value = Flag(row.IsWithinDirectionImpossibleGroup);
                forced.Value = Flag(row.IsForcedPositionGroupShape);
                remaining.Value =
                    row.RemainingCompatiblePositionCountUnderHypothesis is { } count
                        ? (object)(long)count
                        : DBNull.Value;

                command.ExecuteNonQuery();
            }
        }

        /// <remarks>
        /// The candidate insert is the one that returns an identifier, following the importer's
        /// <c>RETURNING</c>-plus-<c>ExecuteScalar</c> convention. Each returned identifier is stored at
        /// its payload index, so occurrence rows resolve their parent from that list rather than by
        /// selecting each candidate back afterwards.
        /// </remarks>
        private List<long> InsertCandidates(
            SqliteTransaction guard, long runID, Phase7EvidencePayload payload)
        {
            var assigned = new List<long>(payload.CandidateRows.Count);

            using var command = _connection.CreateCommand();
            command.Transaction = guard;
            command.CommandText = InsertCandidateStatement;

            var runIDParameter = command.Parameters.Add("$runID", SqliteType.Integer);
            var attachmentID = command.Parameters.Add("$attachmentID", SqliteType.Integer);
            var mediaAssetID = command.Parameters.Add("$mediaAssetID", SqliteType.Integer);
            var direction = command.Parameters.Add("$direction", SqliteType.Text);

            runIDParameter.Value = runID;

            command.Prepare();

            foreach (var row in payload.CandidateRows)
            {
                attachmentID.Value = row.AttachmentID;
                mediaAssetID.Value = row.MediaAssetID;
                direction.Value = Phase7EvidenceText.Format(row.DirectionCompatibility);

                assigned.Add(
                    Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture));
            }

            return assigned;
        }

        private void InsertOccurrences(
            SqliteTransaction guard,
            Phase7EvidencePayload payload,
            IReadOnlyList<long> assignedCandidateIDs)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = guard;
            command.CommandText = InsertOccurrenceStatement;

            var candidateID = command.Parameters.Add("$candidateID", SqliteType.Integer);
            var mediaFileID = command.Parameters.Add("$mediaFileID", SqliteType.Integer);
            var grammar = command.Parameters.Add("$grammar", SqliteType.Integer);
            var token = command.Parameters.Add("$token", SqliteType.Integer);
            var unique = command.Parameters.Add("$unique", SqliteType.Integer);
            var annotation = command.Parameters.Add("$annotation", SqliteType.Text);

            command.Prepare();

            foreach (var row in payload.OccurrenceRows)
            {
                candidateID.Value = assignedCandidateIDs[row.CandidateIndex];
                mediaFileID.Value = row.MediaFileID;
                grammar.Value = Flag(row.TokenGrammarSupported);
                token.Value = row.SequenceToken is { } value ? (object)(long)value : DBNull.Value;
                unique.Value = row.DateTokenKeyUniqueToPayload is { } known
                    ? (object)Flag(known)
                    : DBNull.Value;
                annotation.Value =
                    Phase7EvidenceText.Format(row.OrderConsistencyAnnotation);

                command.ExecuteNonQuery();
            }
        }

        private void InsertAdjacentContext(
            SqliteTransaction guard, long runID, Phase7EvidencePayload payload)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = guard;
            command.CommandText = InsertAdjacentContextStatement;

            var runIDParameter = command.Parameters.Add("$runID", SqliteType.Integer);
            var attachmentID = command.Parameters.Add("$attachmentID", SqliteType.Integer);
            var mediaAssetID = command.Parameters.Add("$mediaAssetID", SqliteType.Integer);
            var offset = command.Parameters.Add("$offset", SqliteType.Integer);

            runIDParameter.Value = runID;

            command.Prepare();

            foreach (var row in payload.AdjacentContextRows)
            {
                attachmentID.Value = row.AttachmentID;
                mediaAssetID.Value = row.MediaAssetID;
                offset.Value = row.AdjacentOffsetDays;

                command.ExecuteNonQuery();
            }
        }

        private void CompleteRun(SqliteTransaction guard, long runID, DateTime completedAtUtc)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = guard;
            command.CommandText = CompleteRunStatement;

            command.Parameters.AddWithValue(
                "$status", Phase7EvidenceText.Format(Phase7RunStatus.Completed));

            command.Parameters.AddWithValue(
                "$completedAtUtc",
                completedAtUtc.ToString(
                    WorkspaceDatabase.UtcTimestampFormat, CultureInfo.InvariantCulture));

            command.Parameters.AddWithValue("$runID", runID);

            command.ExecuteNonQuery();
        }

        private static long Flag(bool value) => value ? 1L : 0L;

        // ---------------------------------------------------------------------------------------
        // Statements.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Every statement this writer issues, for the structural tests that assert each one targets a
        /// Phase 7 evidence table and names no pre-existing table, no Stage 7.2 event table and no
        /// name, path or extension column.
        /// </summary>
        internal static IReadOnlyList<string> Statements =>
        [
            InsertRunStatement,
            InsertSnapshotStatement,
            InsertCandidateStatement,
            InsertOccurrenceStatement,
            InsertAdjacentContextStatement,
            CompleteRunStatement,
            MarkRunFailedStatement,
        ];

        private const string InsertRunStatement =
            """
            INSERT INTO Phase7Run (
                StartedAtUtc,
                Status,
                PreRunWorkspaceFileSha256,
                RankingDimensionSetVersion)
            VALUES ($startedAtUtc, $status, $preRunHash, $version)
            RETURNING RunID;
            """;

        private const string InsertSnapshotStatement =
            """
            INSERT INTO AttachmentEvidenceSnapshot (
                RunID,
                AttachmentID,
                ExactDateCandidateCount,
                AdjacentDateContextCount,
                HasAnyCompatibleAnnotatedCandidate,
                HasAnyContradictoryOnlyAnnotatedCandidate,
                IsWithinDirectionImpossibleGroup,
                IsForcedPositionGroupShape,
                RemainingCompatiblePositionCountUnderHypothesis)
            VALUES (
                $runID, $attachmentID, $exact, $adjacent, $compatible, $contradictory,
                $impossible, $forced, $remaining);
            """;

        private const string InsertCandidateStatement =
            """
            INSERT INTO AttachmentCandidate (
                RunID,
                AttachmentID,
                MediaAssetID,
                DirectionCompatibilityAnnotation)
            VALUES ($runID, $attachmentID, $mediaAssetID, $direction)
            RETURNING AttachmentCandidateID;
            """;

        private const string InsertOccurrenceStatement =
            """
            INSERT INTO AttachmentCandidateOccurrenceEvidence (
                AttachmentCandidateID,
                MediaFileID,
                TokenGrammarSupported,
                SequenceToken,
                DateTokenKeyUniqueToPayload,
                OrderConsistencyAnnotation)
            VALUES ($candidateID, $mediaFileID, $grammar, $token, $unique, $annotation);
            """;

        private const string InsertAdjacentContextStatement =
            """
            INSERT INTO AttachmentAdjacentDateContext (
                RunID,
                AttachmentID,
                MediaAssetID,
                AdjacentOffsetDays)
            VALUES ($runID, $attachmentID, $mediaAssetID, $offset);
            """;

        private const string CompleteRunStatement =
            """
            UPDATE Phase7Run
            SET Status = $status, CompletedAtUtc = $completedAtUtc
            WHERE RunID = $runID;
            """;

        private const string MarkRunFailedStatement =
            """
            UPDATE Phase7Run
            SET Status = $status
            WHERE RunID = $runID;
            """;
    }
}
