namespace Chatcheology.Data.Media
{
    /// <summary>
    /// Which device group one acquisition source belongs to.
    /// </summary>
    /// <remarks>
    /// A <c>MediaSourceID</c> is an acquisition store, not a numbering authority: two stores copied
    /// from one handset share whatever counter that handset used, and a store copied from a different
    /// handset does not. Nothing in the workspace records which is which, so the grouping is supplied
    /// by the caller and validated — never inferred from a root path, a display name, a device
    /// description or a file name.
    /// <para>
    /// <see cref="DeviceGroupID"/> is opaque. It carries no handset identity and is safe to report.
    /// </para>
    /// </remarks>
    public sealed class DeviceGroupAssignment
    {
        /// <summary>The source this assignment describes.</summary>
        public required long MediaSourceID { get; init; }

        /// <summary>The group it belongs to, as an opaque identifier chosen by the caller.</summary>
        public required long DeviceGroupID { get; init; }
    }
}
