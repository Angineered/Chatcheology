namespace Chatcheology.Data.Media
{
    /// <summary>
    /// How the supported-evidence date set relates to the dated non-empty-payload date set.
    /// </summary>
    /// <remarks>
    /// Two sets, measured rather than argued, and <b>no containment relationship between them is
    /// assumed</b>.
    /// <para>
    /// <c>S</c> is the dates carried by supported sequence-bearing files. <c>E</c> is the dates carried by
    /// any dated file whose asset holds a payload — mechanically the figure the Phase 6 first pass
    /// reported as its dated eligible media date count, which is why a real run pins it.
    /// </para>
    /// <para>
    /// Both differences must be fully accounted for, and that is enforced. Every dated file is either
    /// supported or unsupported, so a date in <c>S</c> but not <c>E</c> can only be carried by empty
    /// payloads, and a date in <c>E</c> but not <c>S</c> can only be carried by unsupported dated files. A
    /// residual contradicts that partition, so it is a reconciliation failure rather than an unexplained
    /// figure, and no census is returned.
    /// </para>
    /// <para>
    /// Counts only. No literal date appears here or anywhere else this census writes.
    /// </para>
    /// </remarks>
    public sealed class DatePopulationReconciliation
    {
        /// <summary>Dates carried by supported sequence-bearing files.</summary>
        public required int SupportedDateCount { get; init; }

        /// <summary>Dates carried by any dated file whose asset holds a payload.</summary>
        public required int DatedNonZeroByteDateCount { get; init; }

        /// <summary>Dates in both sets.</summary>
        public required int IntersectionCount { get; init; }

        /// <summary>Dates in the supported set only.</summary>
        public required int SupportedOnlyCount { get; init; }

        /// <summary>Dates in the dated non-empty set only.</summary>
        public required int DatedNonZeroByteOnlyCount { get; init; }

        /// <summary>Supported-only dates every supported file of which is an empty payload.</summary>
        public required int SupportedOnlyAccountedByZeroByteAsset { get; init; }

        /// <summary>Dated-non-empty-only dates carried solely by unsupported dated files.</summary>
        public required int DatedNonZeroByteOnlyAccountedByUnsupportedFiles { get; init; }
    }
}
