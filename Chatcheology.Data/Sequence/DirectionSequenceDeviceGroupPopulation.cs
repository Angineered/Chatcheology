namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C0 — one device group's token-side population, and what its direction sequence actually reduces
    /// to.
    /// </summary>
    /// <remarks>
    /// A device group is the caller's claim that its sources share a numbering authority. Nothing in
    /// the workspace records that, so nothing here infers it.
    /// <para>
    /// <see cref="ReducesToOneDirectionCapableSource"/> is the figure that keeps device-group scope
    /// honest. Where a group holds one direction-capable source beside direction-blind ones, every
    /// position known only from the blind sources is dropped for want of a label, and the group's
    /// emitted sequence is the capable source's own sequence with equivalent positions collapsed. Read
    /// without that, the scope appears to carry evidence it does not.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceDeviceGroupPopulation
    {
        /// <summary>The group described.</summary>
        public required long DeviceGroupID { get; init; }

        /// <summary>Sources the caller assigned to it.</summary>
        public required int SourceCount { get; init; }

        /// <summary>How many of those record folder direction at all.</summary>
        public required int DirectionCapableSourceCount { get; init; }

        /// <summary>How many record none, and so emit no direction symbol.</summary>
        public required int DirectionBlindSourceCount { get; init; }

        /// <summary>Physical media rows across the group.</summary>
        public required int PhysicalObservationCount { get; init; }

        /// <summary>Supported-token observations across the group.</summary>
        public required int SupportedTokenObservationCount { get; init; }

        /// <summary>Supported observations carrying usable folder direction.</summary>
        public required int DirectionCapableObservationCount { get; init; }

        /// <summary>Supported observations contributed by its direction-blind sources.</summary>
        public required int DirectionBlindSourceObservationCount { get; init; }

        /// <summary>Distinct local dates the group's supported observations cover.</summary>
        public required int DistinctSupportedDateCount { get; init; }

        /// <summary>The earliest of those dates, or null when it has none.</summary>
        public required DateOnly? EarliestSupportedDate { get; init; }

        /// <summary>The latest of those dates, or null when it has none.</summary>
        public required DateOnly? LatestSupportedDate { get; init; }

        /// <summary>Logical positions before equivalent-position collapse.</summary>
        public required int LogicalPositionsBeforeCollapse { get; init; }

        /// <summary>Distinct <c>(date, token)</c> positions after collapse within the group.</summary>
        public required int LogicalPositionsAfterCollapse { get; init; }

        /// <summary>Collapsed positions carrying a usable direction symbol.</summary>
        public required int DirectionLabelledLogicalPositionCount { get; init; }

        /// <summary>Collapsed positions dropped because no copy of them records direction.</summary>
        public required int LogicalPositionsWithoutDirectionCount { get; init; }

        /// <summary>Collapsed positions whose copies disagree about direction within the group.</summary>
        public required int ConflictingLogicalPositionCount { get; init; }

        /// <summary>
        /// Collapsed positions observed only by direction-blind sources of this group, which are
        /// dropped however many copies of them survive.
        /// </summary>
        public required int PositionsKnownOnlyFromDirectionBlindSources { get; init; }

        /// <summary>
        /// Whether the group's emitted direction sequence is really that of a single direction-capable
        /// source sitting beside blind ones.
        /// </summary>
        public bool ReducesToOneDirectionCapableSource =>
            DirectionCapableSourceCount == 1 && SourceCount > 1;
    }
}
