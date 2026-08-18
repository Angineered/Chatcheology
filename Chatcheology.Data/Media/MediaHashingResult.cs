namespace Chatcheology.Data.Media
{
    /// <summary>
    /// What one hashing run did, counted rather than listed.
    /// </summary>
    /// <remarks>
    /// Aggregates and identifiers only. A run over a real personal archive can be reported in full
    /// without disclosing a single file name, path or date: where individual files must be
    /// identifiable — to investigate a failure — they are named by <c>MediaFileID</c>, which means
    /// something only to somebody who already has the workspace.
    /// <para>
    /// A run is expected to be one of many. Hashing is resumable, so the useful question a result
    /// answers is not "did it finish" but "what is now done, and what is left".
    /// </para>
    /// </remarks>
    public sealed class MediaHashingResult
    {
        /// <summary>How many files were waiting to be hashed when the run began.</summary>
        public required int PendingAtStart { get; init; }

        /// <summary>
        /// How many files were hashed and linked to an asset, and committed.
        /// </summary>
        /// <remarks>
        /// Always the sum of <see cref="NewAssets"/> and <see cref="ExistingAssetLinks"/>: every
        /// successfully hashed file either introduced a payload or joined one already known.
        /// </remarks>
        public required int SuccessfullyHashed { get; init; }

        /// <summary>
        /// How many files turned out to be hashed already when the run went to record them.
        /// </summary>
        /// <remarks>
        /// Normally zero, because only unhashed files are selected in the first place. It becomes
        /// non-zero if something else hashed the same workspace concurrently, and it exists so that
        /// case is reported rather than silently counted as fresh work.
        /// </remarks>
        public required int AlreadyHashed { get; init; }

        /// <summary>How many files introduced a payload not previously in the workspace.</summary>
        public required int NewAssets { get; init; }

        /// <summary>
        /// How many files proved to be duplicates of a payload already recorded, and were linked to
        /// the existing asset rather than creating a second one.
        /// </summary>
        public required int ExistingAssetLinks { get; init; }

        /// <summary>
        /// How many files could not be hashed: missing, changed size since inventory, unreadable,
        /// unresolvable within their source root, or contradicting an existing asset's size.
        /// </summary>
        /// <remarks>
        /// A failure leaves the file unhashed and unlinked. Nothing is written for it, and it is
        /// simply pending again next run — a file that was locked by another program at the wrong
        /// moment should not need anything more than running the operation again.
        /// </remarks>
        public required int FailedFiles { get; init; }

        /// <summary>
        /// How many files hashed to a payload already recorded under a different media type, and
        /// were therefore left alone.
        /// </summary>
        /// <remarks>
        /// Counted apart from <see cref="FailedFiles"/> because it is a different kind of problem.
        /// A failure is usually about this run; a classification conflict is a statement about the
        /// archive — the same bytes filed under two extensions that mean different things — and
        /// will recur identically until a rule is decided for it.
        /// </remarks>
        public required int ClassificationConflicts { get; init; }

        /// <summary>How many files are still waiting to be hashed now the run has ended.</summary>
        public required int RemainingUnhashed { get; init; }

        /// <summary>How many bytes were actually read and hashed during this run.</summary>
        public required long PhysicalBytesHashed { get; init; }

        /// <summary>
        /// Whether the run ended cooperatively on cancellation before all pending work was
        /// processed.
        /// </summary>
        /// <remarks>
        /// A normal, expected outcome rather than an error, which is why cancelling returns a
        /// result at all instead of throwing one away. Everything counted here is committed and
        /// stays committed; a file interrupted part-way through its own read contributes nothing
        /// and is simply pending again. Running the operation again continues from where this one
        /// stopped.
        /// </remarks>
        public required bool WasCancelled { get; init; }

        /// <summary>
        /// The <c>MediaFileID</c> of each file counted in <see cref="FailedFiles"/>.
        /// </summary>
        public required IReadOnlyList<long> FailedMediaFileIDs { get; init; }

        /// <summary>
        /// The <c>MediaFileID</c> of each file counted in <see cref="ClassificationConflicts"/>.
        /// </summary>
        public required IReadOnlyList<long> ConflictedMediaFileIDs { get; init; }
    }
}
