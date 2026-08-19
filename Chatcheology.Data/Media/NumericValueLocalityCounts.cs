namespace Chatcheology.Data.Media
{
    /// <summary>
    /// How local the disagreement is, for each payload carrying more than one distinct token.
    /// </summary>
    /// <remarks>
    /// The sharpest available test of whether a device group behaves like one numbering authority. Every
    /// such payload lands in exactly one class, by <b>most-local-wins</b> precedence, so the four classes
    /// are mutually exclusive and sum to the population.
    /// <para>
    /// Exhaustiveness is not an assumption. If no single-date grouping of a payload's observations holds
    /// two tokens, yet the payload holds two overall, the disagreement must lie across dates — which is
    /// <see cref="AcrossDatesOnly"/>. That class is the complement of the other three.
    /// </para>
    /// <para>
    /// <see cref="WithinOneDeviceGroupAndDate"/> or <see cref="WithinOneSourceAndDate"/> carrying weight
    /// weighs against the sequence being stable even within one handset and date.
    /// <see cref="AcrossDeviceGroupsOnly"/> dominating is consistent with two handsets numbering the same
    /// payload independently. Neither reading is made here.
    /// </para>
    /// </remarks>
    public sealed class NumericValueLocalityCounts
    {
        /// <summary>Payloads carrying more than one distinct token anywhere.</summary>
        public required int AssetsWithSeveralDistinctTokens { get; init; }

        /// <summary>Class D — disagreement inside one source on one date.</summary>
        public required int WithinOneSourceAndDate { get; init; }

        /// <summary>Class C — inside one device group on one date, across sources.</summary>
        public required int WithinOneDeviceGroupAndDate { get; init; }

        /// <summary>Class B — inside one date, across device groups.</summary>
        public required int AcrossDeviceGroupsOnly { get; init; }

        /// <summary>Class A — only across different dates.</summary>
        public required int AcrossDatesOnly { get; init; }

        /// <summary>The four classes added together, which must equal the population.</summary>
        public int ClassTotal =>
            WithinOneSourceAndDate + WithinOneDeviceGroupAndDate + AcrossDeviceGroupsOnly
            + AcrossDatesOnly;
    }
}
