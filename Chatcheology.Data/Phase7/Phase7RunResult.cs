namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// What one completed Phase 7 evidence run recorded.
    /// </summary>
    /// <remarks>
    /// Returned only by a run that published. A run that failed does not return one of these — it
    /// throws, and the <c>Phase7Run</c> row it leaves behind is the record of the attempt.
    /// <para>
    /// Nothing here is evidence. The four row counts are coverage figures, not findings, and no field
    /// is a rank, a score or a summary of what the evidence says.
    /// </para>
    /// </remarks>
    public sealed class Phase7RunResult
    {
        /// <summary>The run's identifier — the surrogate key assigned in write order.</summary>
        /// <remarks>
        /// Also the only authority on which completed run is the later one. A clock cannot corrupt a
        /// key assigned in write order, which is why "current" is defined by the maximum identifier
        /// and never by a timestamp — the same rule the frozen design pins for resolution-event
        /// replay.
        /// </remarks>
        public required long RunID { get; init; }

        /// <summary>The run's lifecycle status, always <see cref="Phase7RunStatus.Completed"/>.</summary>
        public required Phase7RunStatus Status { get; init; }

        /// <summary>When the run actually started, recorded by its first write.</summary>
        public required DateTime StartedAtUtc { get; init; }

        /// <summary>
        /// When the run published, written in the same statement that set it completed.
        /// </summary>
        public required DateTime? CompletedAtUtc { get; init; }

        /// <summary>
        /// The workspace file's byte identity immediately before this run's first write.
        /// </summary>
        /// <remarks>
        /// Provenance and debugging, and nothing more. It is <strong>not</strong> an evidence
        /// fingerprint and <strong>not</strong> an evidence-change oracle: once Phase 7 writes to a
        /// workspace this value legitimately differs between runs because of Phase 7's own
        /// accumulating history, and a difference between two runs' values is never evidence that the
        /// underlying Phase 6 evidence changed.
        /// <para>
        /// It binds the run's <em>starting</em> byte state, never the state the evidence was computed
        /// from. Those can legitimately differ: a competing writer may commit in the single method call
        /// between the run row's commit and the writer reservation being taken, after which every read
        /// observes that one newer committed state consistently.
        /// </para>
        /// </remarks>
        public required string PreRunWorkspaceFileSha256 { get; init; }

        /// <summary>The identity of the ranking dimension set this evidence was computed for.</summary>
        /// <remarks>
        /// Dimension-set identity only. It records nothing about what the run was asked to cover.
        /// </remarks>
        public required string RankingDimensionSetVersion { get; init; }

        /// <summary>
        /// The conversation the run was asked to evaluate, echoed verbatim from the request.
        /// </summary>
        /// <remarks>
        /// <strong>Provenance only.</strong> It records what the run was asked to do; it is neither
        /// evidence nor a ranking input, and nothing reads it back.
        /// <para>
        /// Also derivable from the run's own snapshot rows, by joining them through
        /// <c>Attachment</c> to <c>Message.ConversationID</c>, so conversation scope survives even a
        /// run whose result object is long gone.
        /// </para>
        /// </remarks>
        public required long ConversationID { get; init; }

        /// <summary>
        /// The local participant the run was asked to read direction against, echoed verbatim from the
        /// request, or <see langword="null"/> when none was supplied.
        /// </summary>
        /// <remarks>
        /// <strong>Provenance only, and not recoverable from the database.</strong> Direction evidence
        /// depends entirely on this value — two runs with different values would legitimately produce
        /// different <c>DirectionCompatibilityAnnotation</c> rows for the same candidates — and nothing
        /// in the workspace records which was used. It must therefore be preserved outside the schema,
        /// in the run record a real run produces, exactly as Phase 6 preserved its own request identity
        /// rather than re-deriving it.
        /// <para>
        /// Neither <see cref="RankingDimensionSetVersion"/> nor
        /// <see cref="PreRunWorkspaceFileSha256"/> records request scope. Two runs agreeing on either
        /// or both are <strong>not</strong> thereby comparable, and no reader may treat them as
        /// evaluations of the same population; the run record and the run's own snapshot rows are the
        /// only authorities on what a run covered.
        /// </para>
        /// </remarks>
        public required long? LocalParticipantID { get; init; }

        /// <summary>How many attachments the run evaluated — one snapshot row each.</summary>
        public required int SnapshotRowCount { get; init; }

        /// <summary>How many exact-date candidate rows the run published.</summary>
        public required int CandidateRowCount { get; init; }

        /// <summary>How many per-occurrence sequence-evidence rows the run published.</summary>
        public required int OccurrenceRowCount { get; init; }

        /// <summary>How many adjacent-date context rows the run published.</summary>
        public required int AdjacentDateContextRowCount { get; init; }
    }
}
