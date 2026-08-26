using System.Globalization;
using System.Runtime.ExceptionServices;
using Chatcheology.Data.Matching;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// Runs one Phase 7 evidence snapshot: re-runs the frozen Phase 6 analysis over a conversation and
    /// publishes, atomically, one snapshot row per in-scope attachment together with its exact-date
    /// candidates, their per-occurrence sequence evidence and its adjacent-date context.
    /// </summary>
    /// <remarks>
    /// <para><strong>What this service cannot do.</strong> It never writes
    /// <c>Attachment.ResolutionStatus</c> or <c>Attachment.ResolvedMediaAssetID</c>, never writes any
    /// pre-existing row, never touches the three Stage 7.2 event tables, and contains no resolution,
    /// confirmation, revocation, review or disposition path of any kind. The most persuasive evidence
    /// shape the model can produce — one candidate, direction-compatible, uniquely constrained —
    /// leaves that attachment exactly as unresolved as the emptiest one.</para>
    /// <para><strong>It also cannot migrate a workspace.</strong> It calls
    /// <see cref="WorkspaceSchemaGuard"/> on a read-only connection <em>before</em> any read-write
    /// connection exists, and never calls <c>WorkspaceDatabase.Initialise</c>. Run against a
    /// schema-version-2 workspace it refuses with the guard's message and writes nothing at all —
    /// not even a run row.</para>
    /// <para><strong>The consistency guarantee.</strong> Every read this run performs happens while a
    /// non-deferred transaction holds SQLite's <c>RESERVED</c> writer reservation, so no conforming
    /// SQLite client can commit any change to the database for the duration; and the transaction that
    /// guaranteed the reads is the one that publishes the result. The evidence therefore describes one
    /// committed database state, covering every input — file names, extensions, dates, direction
    /// flags, hashes, asset links, senders — because it is a property of the file's lock state rather
    /// than of which columns anyone remembered to cross-check.</para>
    /// <para>
    /// What is <em>not</em> guaranteed, stated rather than papered over: a process that ignores
    /// SQLite's locking protocol and writes raw bytes into the database file is outside the model
    /// altogether. Such a process corrupts the database rather than merely racing it, and no check
    /// this service could perform would make that safe.
    /// </para>
    /// <para>
    /// The <see cref="WorkspaceExclusiveLock"/> is still taken, and is not redundant: it excludes a
    /// second <em>cooperating</em> Chatcheology run with a clear diagnostic before any SQLite lock is
    /// attempted, whereas the reservation excludes <em>any</em> conforming SQLite writer with a
    /// <c>SQLITE_BUSY</c>. The two are complementary and neither replaces the other.
    /// </para>
    /// </remarks>
    public sealed class Phase7EvidenceSnapshotService
    {
        /// <summary>
        /// The identity of the ranking dimension set this build's evidence is computed for.
        /// </summary>
        /// <remarks>
        /// It names the frozen dimension set — exactly two dimensions — and the closure sections that
        /// license the order dimension's existence at all. It changes only if the dimension set
        /// changes, which would be a reviewed design change rather than an implementation choice.
        /// <para>
        /// Deliberately <strong>not</strong> overloaded with anything else. It records neither the
        /// conversation nor the local participant, and a future edit that smuggled either into it
        /// would leave a later reader unable to tell which part is which.
        /// </para>
        /// </remarks>
        public const string RankingDimensionSetVersion =
            "PH7-v3.1/Direction+OrderSupport/closure-5+7.2";

        /// <summary>
        /// The key under which a failure to record a failed run is attached to the primary exception.
        /// </summary>
        internal const string MarkFailedFailureKey = "Phase7.MarkFailedFailure";

        /// <summary>
        /// The key under which the resulting run state is attached to the primary exception.
        /// </summary>
        internal const string MarkFailedRunStateKey = "Phase7.RunState";

        /// <summary>
        /// Runs one evidence snapshot against the workspace the request names.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The request has no database path or no usable conversation identifier. Nothing has been
        /// opened.
        /// </exception>
        /// <exception cref="FileNotFoundException">
        /// There is no workspace at the requested path. <strong>No file is created.</strong>
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The workspace is claimed by another cooperating run, is not at the current schema version,
        /// has a SQLite companion file beside it, is not one the frozen analysis can read, or failed a
        /// publication check. Whether a <c>Phase7Run</c> row exists depends on whether the failure
        /// happened before or after T1 committed, and the message says which.
        /// </exception>
        /// <exception cref="Microsoft.Data.Sqlite.SqliteException">
        /// Another conforming SQLite writer held the writer reservation, or held a read lock at
        /// publication time (<c>SQLITE_BUSY</c>). The run is marked <c>Failed</c> and nothing is
        /// published.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> was signalled. If T1 had committed the run is marked
        /// <c>Failed</c> first, and the cancellation reaches the caller as itself.
        /// </exception>
        public Phase7RunResult Run(
            Phase7RunRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            using var session = OpenSession(request);

            var runID = session.BeginRun();

            return RunAfterBegin(session, runID, cancellationToken);
        }

        /// <summary>
        /// Steps 1 to 7: validate, claim the workspace, prove it is one this run may read, hash it,
        /// and open the read-write connection — without writing anything.
        /// </summary>
        /// <remarks>
        /// The order is load-bearing at three points. The missing-file check comes before any
        /// connection is built, because the workspace connection string opens
        /// <c>ReadWriteCreate</c> and would otherwise create the file — the same reasoning and the
        /// same order <c>WorkspaceImporter.Import</c> uses. The companion-file check comes before the
        /// hash, because hashing a file beside a live <c>-journal</c> would prove nothing and because
        /// opening a database with a hot journal is the one case where an open really does write. And
        /// the schema guard runs on a read-only connection that is then disposed, so a workspace this
        /// run must refuse never has a read-write handle on it at all.
        /// <para>
        /// The request's scope is validated here too, on the same read-only connection, so that a
        /// request naming a conversation or a participant that is not there is refused with
        /// <strong>nothing written</strong> — the same way a missing file, a held cooperative lock, a
        /// stray companion file and a schema-version-2 workspace are all refused. Without it those two
        /// caller mistakes would surface in the first matching pass instead, which is after T1 has
        /// committed, and would leave a durable <c>Failed</c> run row behind for what is only a typo.
        /// </para>
        /// </remarks>
        internal static RunSession OpenSession(Phase7RunRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.DatabasePath);

            if (!File.Exists(request.DatabasePath))
            {
                throw new FileNotFoundException(
                    "There is no workspace database at the supplied path. A Phase 7 evidence run " +
                    "snapshots a workspace that already exists; it never creates one, and no file " +
                    "has been created here.",
                    request.DatabasePath);
            }

            var workspaceLock = WorkspaceExclusiveLock.Acquire(request.DatabasePath);

            try
            {
                Phase7PreExistingBaseline baseline;

                using (var readOnly =
                    WorkspaceDatabase.OpenReadOnlyConnection(request.DatabasePath))
                {
                    WorkspaceSchemaGuard.RequireCurrentSchemaVersion(
                        readOnly, "a Phase 7 evidence run");

                    RequireRequestScope(readOnly, request);

                    baseline = Phase7RunReconciliation.ReadBaseline(readOnly);
                }

                WorkspaceCheckpoint.RequireNoCompanionFiles(request.DatabasePath);

                // Every connection this run opened has been disposed, which is what
                // ComputeFileSha256's documented contract requires of its caller. The value is the
                // file's exact byte content immediately before the run's first write, which is T1.
                var preRunHash = WorkspaceCheckpoint.ComputeFileSha256(request.DatabasePath);

                return new RunSession(request, workspaceLock, preRunHash, baseline);
            }
            catch
            {
                workspaceLock.Dispose();

                throw;
            }
        }

        /// <summary>
        /// Refuses a request naming a conversation, or a local participant, that the workspace does not
        /// hold — before anything has been written.
        /// </summary>
        /// <remarks>
        /// A pre-flight refusal, not a second opinion. The frozen
        /// <c>WorkspaceMatchingService.RequireConversation</c> and
        /// <c>RequireLocalParticipantInConversation</c> remain the authority and still run in the first
        /// matching pass; this only moves the moment of refusal to before T1, so a caller's typo cannot
        /// leave a <c>Failed</c> run row in a workspace it never legitimately touched. If the two ever
        /// disagreed, the frozen check would still refuse the run — this one can only refuse earlier,
        /// never permit something the frozen check would reject.
        /// <para>
        /// The membership rule is the frozen one, restated as the same two counting queries rather than
        /// by widening a frozen signature to expose a helper: a conversation must exist, and a supplied
        /// participant must belong to <em>that</em> conversation, because a participant of another
        /// conversation would produce direction for every message — all of it wrong, and all of it
        /// looking exactly like the right answer.
        /// </para>
        /// <para>
        /// A null <c>LocalParticipantID</c> is valid and is not checked: it means direction is simply
        /// unavailable, which is the honest result rather than a degraded one.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The conversation does not exist, or the supplied participant does not belong to it. Nothing
        /// has been written, no run row exists, and the workspace is unchanged.
        /// </exception>
        private static void RequireRequestScope(
            SqliteConnection connection, Phase7RunRequest request)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = ConversationExistsStatement;
                command.Parameters.AddWithValue("$conversationID", request.ConversationID);

                if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                {
                    throw new InvalidOperationException(
                        $"The workspace has no conversation " +
                        $"{request.ConversationID.ToString(CultureInfo.InvariantCulture)}. A Phase 7 " +
                        $"evidence run of a conversation that is not there would report an empty " +
                        $"snapshot, which reads as a conversation with no attachments rather than as a " +
                        $"conversation that does not exist. Nothing has been written, no run has been " +
                        $"recorded, and the workspace is unchanged.");
                }
            }

            if (request.LocalParticipantID is not { } localParticipantID)
            {
                return;
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = ParticipantInConversationStatement;
                command.Parameters.AddWithValue("$conversationID", request.ConversationID);
                command.Parameters.AddWithValue("$participantID", localParticipantID);

                if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1)
                {
                    return;
                }
            }

            throw new InvalidOperationException(
                $"Participant {request.LocalParticipantID?.ToString(CultureInfo.InvariantCulture)} " +
                $"does not belong to conversation " +
                $"{request.ConversationID.ToString(CultureInfo.InvariantCulture)}, so it cannot be " +
                $"that conversation's local user. Every direction annotation this run would persist " +
                $"rests on that claim, and a wrong local participant would make every message read as " +
                $"incoming. Supply a participant of this conversation, or none at all. Nothing has " +
                $"been written, no run has been recorded, and the workspace is unchanged.");
        }

        private const string ConversationExistsStatement =
            "SELECT COUNT(*) FROM Conversation WHERE ConversationID = $conversationID;";

        private const string ParticipantInConversationStatement =
            """
            SELECT COUNT(*)
            FROM ConversationParticipant
            WHERE ConversationID = $conversationID
              AND ParticipantID = $participantID;
            """;

        /// <summary>
        /// Steps 9 to 13: take the consistency guard, compute the evidence under it, publish, and on
        /// any failure record the run as failed without publishing anything.
        /// </summary>
        /// <param name="whileGuardHeld">
        /// Run once, immediately after the guard is acquired and before any read begins. The internal
        /// seam the consistency-guard tests need in order to act at the one moment that matters; the
        /// public <see cref="Run"/> never supplies it, so no production path can.
        /// </param>
        /// <param name="afterInsertsBeforeCommit">
        /// Run once, after every row is written and every check has passed but before the publication
        /// commit. The same kind of internal observation seam, for the one assertion that has to be made
        /// from inside that window.
        /// </param>
        internal static Phase7RunResult RunAfterBegin(
            RunSession session,
            long runID,
            CancellationToken cancellationToken,
            Action? whileGuardHeld = null,
            Action? afterInsertsBeforeCommit = null)
        {
            ArgumentNullException.ThrowIfNull(session);

            try
            {
                // T2 begins here, as the statement immediately following T1's commit. It takes
                // SQLite's RESERVED writer reservation and executes no DML until every read below is
                // finished.
                session.Writer.BeginPublicationGuard();

                whileGuardHeld?.Invoke();

                return session.ComputeAndPublish(
                    runID, cancellationToken, afterInsertsBeforeCommit);
            }
            catch (Exception primary)
            {
                // The guard is rolled back before anything else, so "no evidence row survives" holds
                // by construction and T3 can get the exclusive upgrade it needs.
                session.Writer.RollbackGuard();

                try
                {
                    session.Writer.MarkFailed(runID);
                }
                catch (Exception secondary)
                {
                    // A bookkeeping failure must never replace the primary diagnosis, and it must
                    // never be swallowed either: the run is now stuck in Running, which is safe
                    // (Running is never current) but is a fact the caller has to be able to see.
                    primary.Data[MarkFailedFailureKey] = secondary.ToString();

                    primary.Data[MarkFailedRunStateKey] =
                        $"Run {runID.ToString(CultureInfo.InvariantCulture)} could not be marked " +
                        $"Failed, so it remains Running and is therefore not current. No evidence " +
                        $"row was published.";
                }

                // Captured and rethrown rather than wrapped, so the primary exception reaches the
                // caller as itself with its original stack — in particular an
                // OperationCanceledException must stay one, which an AggregateException would break.
                ExceptionDispatchInfo.Capture(primary).Throw();

                throw;
            }
        }

        /// <summary>
        /// One run's held resources: the cooperative claim on the workspace, the read-write
        /// connection and its transactions, the pre-run hash, and the pre-existing baseline.
        /// </summary>
        /// <remarks>
        /// Internal, and the only reason it exists as a separate object rather than as locals inside
        /// <see cref="Run"/> is deterministic testing. The T1-failure, cancellation-after-T1,
        /// guard-held and mark-failed tests each need to run code at one exact point in the lifecycle,
        /// and a test that staged those with sleeps or a production mutation hook would be either
        /// flaky or a hole in the product. The public <see cref="Run"/> composes the same steps and
        /// remains the only path a caller sees.
        /// </remarks>
        internal sealed class RunSession : IDisposable
        {
            private readonly WorkspaceExclusiveLock _workspaceLock;

            internal RunSession(
                Phase7RunRequest request,
                WorkspaceExclusiveLock workspaceLock,
                string preRunWorkspaceFileSha256,
                Phase7PreExistingBaseline baseline)
            {
                Request = request;
                _workspaceLock = workspaceLock;
                PreRunWorkspaceFileSha256 = preRunWorkspaceFileSha256;
                Baseline = baseline;

                try
                {
                    Writer = new Phase7EvidenceWriter(request.DatabasePath);
                }
                catch
                {
                    workspaceLock.Dispose();

                    throw;
                }
            }

            internal Phase7RunRequest Request { get; }

            /// <summary>The workspace's byte identity immediately before this run's first write.</summary>
            internal string PreRunWorkspaceFileSha256 { get; }

            internal Phase7PreExistingBaseline Baseline { get; }

            internal Phase7EvidenceWriter Writer { get; }

            /// <summary>When T1 recorded the run as starting.</summary>
            internal DateTime StartedAtUtc { get; private set; }

            /// <summary>T1: records the run as <c>Running</c> and commits.</summary>
            internal long BeginRun()
            {
                StartedAtUtc = DateTime.UtcNow;

                return Writer.BeginRun(
                    StartedAtUtc, PreRunWorkspaceFileSha256, RankingDimensionSetVersion);
            }

            /// <summary>
            /// Steps 10 and 11: the two read passes and the occurrence pass under the DML-free guard,
            /// then the publication.
            /// </summary>
            internal Phase7RunResult ComputeAndPublish(
                long runID,
                CancellationToken cancellationToken,
                Action? afterInsertsBeforeCommit = null)
            {
                var payload = Compute(cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                var completedAtUtc = DateTime.UtcNow;

                Writer.Publish(
                    runID, Request, payload, Baseline, completedAtUtc, afterInsertsBeforeCommit);

                return new Phase7RunResult
                {
                    RunID = runID,
                    Status = Phase7RunStatus.Completed,
                    StartedAtUtc = StartedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    PreRunWorkspaceFileSha256 = PreRunWorkspaceFileSha256,
                    RankingDimensionSetVersion = RankingDimensionSetVersion,
                    ConversationID = Request.ConversationID,
                    LocalParticipantID = Request.LocalParticipantID,
                    SnapshotRowCount = payload.SnapshotRows.Count,
                    CandidateRowCount = payload.CandidateRows.Count,
                    OccurrenceRowCount = payload.OccurrenceRows.Count,
                    AdjacentDateContextRowCount = payload.AdjacentContextRows.Count,
                };
            }

            /// <summary>
            /// The read/compute phase, entirely inside the DML-free guard.
            /// </summary>
            /// <remarks>
            /// Every connection opened here is opened and disposed by the method that uses it, so by
            /// the time this returns no read connection remains and the guard may safely start
            /// modifying pages. Two matching passes are needed because a group's admissible window
            /// depends on how many attachments share the group, which the first pass is what
            /// establishes.
            /// </remarks>
            private Phase7EvidencePayload Compute(CancellationToken cancellationToken)
            {
                var analysisRequest =
                    new MatchAnalysisRequest(Request.ConversationID, Request.LocalParticipantID);

                var matchingService = new WorkspaceMatchingService();

                var collector = new Phase7GroupShapeCollector();

                matchingService.Analyse(
                    Request.DatabasePath, analysisRequest, collector.Add, cancellationToken);

                var occurrenceIndex =
                    Phase7OccurrenceIndex.Read(Request.DatabasePath, cancellationToken);

                var passOne = collector.Build(occurrenceIndex);

                var builder = new Phase7EvidenceBuilder(passOne, occurrenceIndex);

                matchingService.Analyse(
                    Request.DatabasePath, analysisRequest, builder.Add, cancellationToken);

                return builder.Build();
            }

            public void Dispose()
            {
                Writer.Dispose();
                _workspaceLock.Dispose();
            }
        }
    }
}
