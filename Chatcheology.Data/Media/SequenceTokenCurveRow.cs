namespace Chatcheology.Data.Media
{
    /// <summary>One token's row of the behaviour curve.</summary>
    /// <remarks>
    /// <see cref="DistinctDeviceGroupDateGroupCount"/> is the figure a daily-reset reading rests on,
    /// not <see cref="PhysicalFileCount"/>. Raw file frequency is inflated wherever two acquisition
    /// stores of one handset hold the same write; a count of distinct device-group and date groups is
    /// not.
    /// <para>
    /// A generally declining curve is <em>consistent with</em> a daily, device-local counter. Because
    /// acquisition is incomplete, a flatter curve is evidence against that simple model rather than
    /// proof of anything, and a declining one is not proof of a reset. No statistical test is applied;
    /// every figure here is a count.
    /// </para>
    /// </remarks>
    public sealed class SequenceTokenCurveRow
    {
        /// <summary>The token as four characters, leading zeroes preserved.</summary>
        public required string Token { get; init; }

        /// <summary>Its numeric value, which orders identically at fixed width.</summary>
        public required int TokenValue { get; init; }

        /// <summary>Supported physical files carrying it.</summary>
        public required int PhysicalFileCount { get; init; }

        /// <summary>Distinct assets carrying it.</summary>
        public required int DistinctMediaAssetCount { get; init; }

        /// <summary>Distinct calendar dates on which it occurs.</summary>
        public required int DistinctFileDateCount { get; init; }

        /// <summary>Distinct device-group and date groups containing it.</summary>
        public required int DistinctDeviceGroupDateGroupCount { get; init; }

        /// <summary>Distinct source and date groups containing it.</summary>
        public required int DistinctSourceDateGroupCount { get; init; }

        /// <summary>Per-group contributions, ordered by group identifier.</summary>
        public required IReadOnlyList<DeviceGroupTokenCounts> PerDeviceGroup { get; init; }
    }
}
