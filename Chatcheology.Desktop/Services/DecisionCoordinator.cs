namespace Chatcheology.Desktop.Services
{
    /// <summary>
    /// What happened when a decision was attempted.
    /// </summary>
    /// <param name="Session">
    /// The reopened read-only session. Always a live session — the viewer is usable whether the
    /// decision was recorded or refused.
    /// </param>
    /// <param name="Failure">
    /// The exception the Phase 7 service raised, or <see langword="null"/> when it recorded the
    /// decision.
    /// </param>
    internal sealed record DecisionOutcome(WorkspaceSession Session, Exception? Failure)
    {
        /// <summary>Whether Phase 7 recorded the decision.</summary>
        internal bool Succeeded => Failure is null;
    }

    /// <summary>
    /// Runs one Phase 7 decision with no viewer read able to overlap it.
    /// </summary>
    /// <remarks>
    /// <para><strong>Why the read session is closed first.</strong> Phase 7 owns its own write
    /// connection and takes SQLite's writer reservation before its first validation read; its commit
    /// then needs an <c>EXCLUSIVE</c> lock, and write-ahead logging is deliberately off. A viewer
    /// read in flight at that moment contends with the upgrade. Closing the session first makes
    /// <em>no viewer read overlaps the write</em> a property of the sequence rather than a claim
    /// about timing, and it reuses the close path — dispose, then clear the connection pool — that
    /// the viewer already has.</para>
    /// <para><strong>What this is not about.</strong> Cross-connection visibility is not a hazard
    /// here: a reused connection would see committed data on its next read. The reason to close is
    /// contention, not staleness, and saying otherwise would defend a correct decision with a wrong
    /// argument.</para>
    /// <para><strong>The caller must have quiesced already.</strong> Disposing a session whose read
    /// is still running disposes the gate that read is holding.
    /// <c>MainViewModel</c> cancels the outstanding read and <em>awaits</em> it before calling here;
    /// cancelling alone is not enough, because a read may already be past its cancellation check.
    /// </para>
    /// <para><strong>Deliberately narrow.</strong> This decides nothing. It does not validate a
    /// basis or a reason — Phase 7 does, and a second copy of those rules is how the two come to
    /// disagree — it does not choose which service to call, and it does not restore any view state.
    /// It closes, invokes once, reopens, and reports.</para>
    /// </remarks>
    internal sealed class DecisionCoordinator
    {
        /// <summary>
        /// Closes <paramref name="session"/>, runs <paramref name="write"/> once, and reopens.
        /// </summary>
        /// <param name="write">
        /// Calls exactly one Phase 7 decision service by database path. It runs on the thread pool
        /// with no viewer session open.
        /// </param>
        /// <remarks>
        /// <strong>Single-shot.</strong> A refusal is never retried. A refused decision usually means
        /// the workspace moved on — the evidence run was superseded, or the effective resolution
        /// event changed — and the answer to that is to look again, not to ask again.
        /// <para>
        /// The session is reopened on both paths, so a refusal costs the operator their place and
        /// nothing else.
        /// </para>
        /// </remarks>
        /// <exception cref="Exception">
        /// The workspace could not be reopened afterwards. The decision's own outcome is lost with
        /// it, which is why this is raised rather than reported: the viewer has no session to
        /// continue with and the caller must close down to a known state.
        /// </exception>
        internal async Task<DecisionOutcome> ExecuteAsync(
            WorkspaceSession session, Action write, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(write);

            var databasePath = session.DatabasePath;

            session.Dispose();

            Exception? failure = null;

            try
            {
                await Task.Run(write, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Phase 7's refusals are written for a person to read, and every one of them says
                // what was left unchanged. They are carried back verbatim rather than restated.
                failure = exception;
            }

            return new DecisionOutcome(WorkspaceSession.Open(databasePath), failure);
        }
    }
}
