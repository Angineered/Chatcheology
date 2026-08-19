namespace Chatcheology.Data.Media
{
    /// <summary>
    /// Whether copies of one payload dated the same day agree about their token, across sources and
    /// across device groups.
    /// </summary>
    /// <remarks>
    /// Two populations live here and must never be added together or given the same label.
    /// <para>
    /// The <b>asset and date</b> counts describe each payload-and-date once: in how many sources and
    /// device groups it is represented, and whether all of its observations that day agree.
    /// </para>
    /// <para>
    /// The <b>pair</b> counts describe each unordered pair of represented sources once per payload and
    /// date. A payload represented in three sources on one date contributes three pair observations, so
    /// these counts are larger than the asset-and-date counts by construction.
    /// <c>AllTokensEqual</c> for a pair means the union of the supported observations contributed by
    /// those two sources holds exactly one token.
    /// </para>
    /// <para>
    /// Same-group pairs are split from cross-group pairs because agreement between two acquisition
    /// stores of one handset is the least informative comparison available — they are overlapping copies
    /// of one device's own names — and pooled it would dominate the count and read as stability.
    /// </para>
    /// </remarks>
    public sealed class SameAssetAgreement
    {
        /// <summary>Payload-and-date combinations carrying supported evidence.</summary>
        public required int AssetDateCount { get; init; }

        /// <summary>Of those, represented in exactly one source.</summary>
        public required int AssetDatesInOneSource { get; init; }

        /// <summary>Of those, represented in several sources.</summary>
        public required int AssetDatesInSeveralSources { get; init; }

        /// <summary>Of those, represented in exactly one device group.</summary>
        public required int AssetDatesInOneDeviceGroup { get; init; }

        /// <summary>Of those, represented in several device groups.</summary>
        public required int AssetDatesInSeveralDeviceGroups { get; init; }

        /// <summary>Multi-source payload-and-dates whose every observation is the same token.</summary>
        public required int MultiSourceAssetDatesAllTokensEqual { get; init; }

        /// <summary>Multi-source payload-and-dates whose observations differ.</summary>
        public required int MultiSourceAssetDatesTokensDiffer { get; init; }

        /// <summary>Multi-group payload-and-dates whose every observation is the same token.</summary>
        public required int MultiDeviceGroupAssetDatesAllTokensEqual { get; init; }

        /// <summary>Multi-group payload-and-dates whose observations differ.</summary>
        public required int MultiDeviceGroupAssetDatesTokensDiffer { get; init; }

        /// <summary>The most distinct tokens one payload carries on one date across sources.</summary>
        public required int MaximumDistinctTokensOnOneAssetDateAcrossSources { get; init; }

        /// <summary>The same across device groups.</summary>
        public required int MaximumDistinctTokensOnOneAssetDateAcrossDeviceGroups { get; init; }

        /// <summary>Unordered source pairs observed, counted once per payload and date.</summary>
        public required int SourcePairCount { get; init; }

        /// <summary>Pairs whose two sources share a device group and agree.</summary>
        public required int SameDeviceGroupPairsAllTokensEqual { get; init; }

        /// <summary>Pairs whose two sources share a device group and disagree.</summary>
        public required int SameDeviceGroupPairsTokensDiffer { get; init; }

        /// <summary>Pairs spanning device groups that agree.</summary>
        public required int CrossDeviceGroupPairsAllTokensEqual { get; init; }

        /// <summary>Pairs spanning device groups that disagree.</summary>
        public required int CrossDeviceGroupPairsTokensDiffer { get; init; }
    }
}
