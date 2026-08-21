namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C0 — whether direction-capable sources classify the same logical <c>(date, token)</c> position
    /// the same way, and how often they do not.
    /// </summary>
    /// <remarks>
    /// A folder-state consistency check, and only that. Agreement does not prove that
    /// <c>IsSent = 0</c> means incoming: shared files may simply have been copied with the same folder
    /// structure. Material disagreement, on the other hand, would show that the gate's direction label
    /// is itself unstable, which is why the rate is a first-class output.
    /// <para>
    /// Agreement between two sources is also not replication. The archive's sources hold substantial
    /// copies of one media library, so nothing here may be read as independent confirmation.
    /// </para>
    /// <para>
    /// A non-zero <see cref="ConflictingPositionCount"/> is not by itself a stop condition. The rate is
    /// measured and returned so that a tolerance can be chosen and frozen at the gate review, before
    /// any alignment outcome is visible. No tolerance is encoded here.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceCrossSourceOverlap
    {
        /// <summary>Distinct <c>(date, token)</c> positions across the whole archive.</summary>
        public required int DistinctLogicalPositionCount { get; init; }

        /// <summary>Positions observed by more than one acquisition source.</summary>
        public required int SharedLogicalPositionCount { get; init; }

        /// <summary>Positions observed by exactly one.</summary>
        public required int SingleSourceLogicalPositionCount { get; init; }

        /// <summary>
        /// Shared positions where at least two sources record direction and all of them agree.
        /// </summary>
        public required int AgreeingPositionCount { get; init; }

        /// <summary>
        /// Shared positions where the recorded directions disagree, whether between two sources or
        /// within the copies of one.
        /// </summary>
        public required int ConflictingPositionCount { get; init; }

        /// <summary>Shared positions where exactly one source records direction at all.</summary>
        public required int OneSideDirectionKnownPositionCount { get; init; }

        /// <summary>Shared positions where no source records direction.</summary>
        public required int NoDirectionKnownPositionCount { get; init; }

        /// <summary>
        /// Conflicting positions whose disagreeing observations all sit inside one device group, and so
        /// exclude a device-group pair as well as a source pair.
        /// </summary>
        public required int ConflictingPositionsWithinOneDeviceGroup { get; init; }

        /// <summary>
        /// Conflicting positions whose disagreeing observations span device groups, which excludes no
        /// device-group pair because neither group's own copies disagree.
        /// </summary>
        /// <remarks>
        /// Reported separately rather than folded in, so a disagreement that the chosen scope happens
        /// not to act on is still visible at the freeze review.
        /// </remarks>
        public required int ConflictingPositionsSpanningDeviceGroups { get; init; }

        /// <summary>
        /// Conflicting positions as a share of shared positions, or zero where none are shared.
        /// </summary>
        /// <remarks>
        /// Descriptive. The threshold this rate will be judged against is chosen at the gate review
        /// from C0 information only, and is deliberately absent from the code.
        /// </remarks>
        public double ConflictingShareOfSharedPositions =>
            SharedLogicalPositionCount == 0
                ? 0d
                : (double)ConflictingPositionCount / SharedLogicalPositionCount;
    }
}
