namespace Chatcheology.Data.Media
{
    /// <summary>What one device group contributed to one token's row of the curve.</summary>
    /// <remarks>
    /// Split per group rather than pooled, because two acquisition stores of one handset overlap: a
    /// pooled file count counts the same handset-side write two or three times, and a curve read off
    /// that would be describing the acquisition rather than the numbering.
    /// </remarks>
    public sealed class DeviceGroupTokenCounts
    {
        /// <summary>The group, as the caller's opaque identifier.</summary>
        public required long DeviceGroupID { get; init; }

        /// <summary>Supported physical files in this group carrying the token.</summary>
        public required int PhysicalFileCount { get; init; }

        /// <summary>Distinct calendar dates in this group on which it occurs.</summary>
        public required int DistinctFileDateCount { get; init; }
    }
}
