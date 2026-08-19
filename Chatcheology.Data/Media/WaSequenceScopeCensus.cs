namespace Chatcheology.Data.Media
{
    /// <summary>
    /// The bounded result of one WA sequence scope and behaviour census.
    /// </summary>
    /// <remarks>
    /// Counts, bands, cross-tabs and sequence tokens only. No file name, extension value, path,
    /// participant, conversation title, message content, calendar date or media hash leaves this type,
    /// and no per-group row carrying a surrogate identifier does either.
    /// <para>
    /// Nothing here is a conclusion about any attachment. The census measures what the four-digit
    /// sequence does across date, acquisition source, device group and physical copy, so that a later
    /// decision about using it as evidence can be argued from measurement rather than from a hypothesis.
    /// </para>
    /// <para>
    /// No scope is named "the namespace". The ladder is reported so that the contribution of each added
    /// component can be read as a delta, and which of them reduces ambiguity is the finding rather than
    /// the design.
    /// </para>
    /// </remarks>
    public sealed class WaSequenceScopeCensus
    {
        /// <summary>Population figures every other figure here is reconciled against.</summary>
        public required SequenceReconciliation Reconciliation { get; init; }

        /// <summary>How the two date sets relate.</summary>
        public required DatePopulationReconciliation DatePopulation { get; init; }

        /// <summary>Every source, ordered by identifier, with its assigned device group.</summary>
        public required IReadOnlyList<MediaSourceScopeSummary> MediaSources { get; init; }

        /// <summary>The four key levels of the scope ladder, in declaration order.</summary>
        public required IReadOnlyList<ScopeKeyUniqueness> KeyUniqueness { get; init; }

        /// <summary>
        /// What the colliding keys look like, for the three levels that carry a date.
        /// </summary>
        /// <remarks>
        /// The token-only level is deliberately absent. Its full summary is reported under
        /// <see cref="KeyUniqueness"/>, including the pair magnitude, but a cross-tab over keys that each
        /// span a large share of the archive's assets has no readable structure.
        /// </remarks>
        public required IReadOnlyList<CollisionCharacterisation> Collisions { get; init; }

        /// <summary>Every observed token's behaviour, ascending.</summary>
        public required SequenceTokenCurve TokenCurve { get; init; }

        /// <summary>Group sizes for the three group scopes, pooled and per partition.</summary>
        public required IReadOnlyList<ScopeGroupPopulation> GroupPopulations { get; init; }

        /// <summary>Starting values, contiguity and gaps, per scope and size band.</summary>
        public required IReadOnlyList<ContinuityByGroupSize> Continuity { get; init; }

        /// <summary>
        /// Cross-tabs relating a group's token range to its population.
        /// </summary>
        /// <remarks>
        /// Joints rather than marginals, because whether range tracks population is the question and four
        /// separate histograms cannot answer it.
        /// </remarks>
        public required IReadOnlyList<JointDistribution> RangeAndPopulationJoints { get; init; }

        /// <summary>
        /// Exact signed frequency of distinct assets minus distinct tokens, per device group and date.
        /// </summary>
        /// <remarks>
        /// Unbanded and two-sided on purpose. Negative means several tokens collapsed onto one
        /// deduplicated payload; positive means a token in that group carries more than one payload.
        /// </remarks>
        public required IReadOnlyList<ValueCount> DeviceGroupDateAssetsMinusTokens { get; init; }

        /// <summary>The same, per source and date.</summary>
        public required IReadOnlyList<ValueCount> SourceDateAssetsMinusTokens { get; init; }

        /// <summary>
        /// Device-group-and-date groups that are a complete gapless run from <c>0000</c> whose distinct
        /// asset count equals that run's length.
        /// </summary>
        public required int DeviceGroupDateGroupsWhereRangeTokensAndAssetsAllAgree { get; init; }

        /// <summary>The same, per source and date.</summary>
        public required int SourceDateGroupsWhereRangeTokensAndAssetsAllAgree { get; init; }

        /// <summary>
        /// Distinct tokens in the device-group-and-date group holding the highest observed token.
        /// </summary>
        /// <remarks>
        /// A reconciliation of the highest token against the population that produced it, not an
        /// exemplar: no date and no surrogate identifier is emitted. Where several groups hold that
        /// token, the one with the most distinct tokens is described, ties broken deterministically.
        /// </remarks>
        public required int MaximumTokenGroupDistinctTokenCount { get; init; }

        /// <summary>Its inclusive token range width.</summary>
        public required int MaximumTokenGroupInclusiveWidth { get; init; }

        /// <summary>Its distinct assets.</summary>
        public required int MaximumTokenGroupDistinctAssetCount { get; init; }

        /// <summary>Its supported physical files.</summary>
        public required int MaximumTokenGroupPhysicalFileCount { get; init; }

        /// <summary>Token reuse across dates, pooled and per partition.</summary>
        public required IReadOnlyList<TokenReuseSummary> TokenReuse { get; init; }

        /// <summary>Whether same-day copies of one payload agree, by source and by device group.</summary>
        public required SameAssetAgreement SameAssetAgreement { get; init; }

        /// <summary>How local the disagreement is, for payloads carrying several tokens.</summary>
        public required NumericValueLocalityCounts NumericValueLocality { get; init; }

        /// <summary>Duplicate-copy agreement, per source and per device group.</summary>
        public required IReadOnlyList<DuplicateCopyAgreement> DuplicateCopies { get; init; }

        /// <summary>What the empty payload contributes.</summary>
        public required ZeroByteContribution ZeroByte { get; init; }
    }
}
