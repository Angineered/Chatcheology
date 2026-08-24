namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// Runs Stage B2C-1: reproduces the accepted Stage B2C-0 gate state, and only then measures what the
    /// observed token order actually carried.
    /// </summary>
    /// <remarks>
    /// Read-only in the sense SQLite enforces, through the accepted gate service and no other path.
    /// Nothing is persisted, no schema is touched, no index is created, no attachment is resolved and no
    /// media file is opened. No candidate, asset identity, first-pass relation or Stage B2B assignment is
    /// consumed, and the request has no member able to carry one.
    /// <para>
    /// The order below is the stage's whole safety property. The gate runs first, over both its scopes;
    /// the barrier reconciles the frozen population, references and provenance and throws if any of them
    /// has moved; and only a reconciliation the barrier produced can construct the outcome calculator. So
    /// an outcome cannot be computed, logged or returned before the freeze has been reproduced.
    /// </para>
    /// <para>
    /// All or nothing. There is no persisted resume and no checkpoint: a failed or cancelled run discards
    /// its in-memory state and is re-run from the beginning, because a partial outcome retained as though
    /// it were a result would be a result chosen by when the run stopped.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceAlignmentService
    {
        /// <summary>
        /// Runs the alignment stage over one workspace.
        /// </summary>
        /// <param name="request">What to measure, with the frozen state it must reproduce.</param>
        /// <param name="progress">
        /// An optional progress channel. It carries stage, counts, elapsed time and working set, and
        /// cannot carry an outcome.
        /// </param>
        /// <param name="cancellationToken">Signalled to abandon the run.</param>
        /// <exception cref="ArgumentException">The request names no workspace.</exception>
        /// <exception cref="InvalidOperationException">
        /// The gate refused the workspace; a frozen target, identity, provenance or disagreement gate
        /// failed; a frozen population had drifted; or an internal reconciliation failed. No census is
        /// returned and no outcome is exposed.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> was signalled. No census is returned and nothing partial
        /// is retained.
        /// </exception>
        public DirectionSequenceAlignmentCensus Analyse(
            DirectionSequenceAlignmentRequest request,
            IProgress<DirectionSequenceAlignmentProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Analyse(request, cache: null, progress, cancellationToken);

        /// <summary>
        /// The same run with a supplied reference cache, so a test can prove the figures do not depend on
        /// it.
        /// </summary>
        internal DirectionSequenceAlignmentCensus Analyse(
            DirectionSequenceAlignmentRequest request,
            DirectionSequenceReferenceCache? cache,
            IProgress<DirectionSequenceAlignmentProgress>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.StageATokenCoverage);
            ArgumentNullException.ThrowIfNull(request.StageB1);
            ArgumentNullException.ThrowIfNull(request.FrozenGate);

            cancellationToken.ThrowIfCancellationRequested();

            var snapshots = new List<DirectionSequencePairSnapshot>();

            // A. The accepted gate, unchanged, over both of its scopes: the wrong-scope discriminator is
            // the source-scope figure, so skipping that scope would remove the check that this run is
            // numbering by device group at all.
            var census = new DirectionSequenceGateService().Analyse(
                request.ToGateRequest(), pairSink: null, snapshots.Add, cancellationToken);

            // B. The frozen state, or nothing. Throws rather than returning on any mismatch.
            var reconciliation = DirectionSequenceFrozenGateBarrier.Reconcile(
                census, request.FrozenGate, request.StageB1, request.DeviceGroups);

            // C and D. Only a reconciliation the barrier produced opens this.
            return new DirectionSequenceOutcomeCalculator(reconciliation, snapshots, cache)
                .Compute(progress, cancellationToken);
        }
    }
}
