namespace Chatcheology.Data.Media
{
    /// <summary>What one WA sequence scope census is asked to describe.</summary>
    /// <remarks>
    /// The device grouping is <b>required</b> and has no default. A silent one-group-per-source default
    /// would quietly reinstate the assumption this census exists to test — that an acquisition store is a
    /// numbering authority — so a caller who genuinely wants that must say so by supplying it.
    /// <para>
    /// <see cref="DeviceGroups"/> is a list rather than a dictionary on purpose. A dictionary cannot hold
    /// the same <c>MediaSourceID</c> twice, so a duplicate would be lost at construction and could never
    /// be refused. A list makes the malformed input representable, and therefore rejectable.
    /// </para>
    /// </remarks>
    public sealed class WaSequenceScopeCensusRequest
    {
        /// <summary>
        /// An existing workspace at the current schema version, opened read-only. Never created, never
        /// migrated.
        /// </summary>
        public required string DatabasePath { get; init; }

        /// <summary>
        /// Which device group each source belongs to. Must name every source in the workspace exactly
        /// once and name nothing else.
        /// </summary>
        public required IReadOnlyList<DeviceGroupAssignment> DeviceGroups { get; init; }
    }
}
