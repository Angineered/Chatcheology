namespace Chatcheology.Data.Matching
{
    /// <summary>
    /// What one media source contributed to the census.
    /// </summary>
    /// <remarks>
    /// Sources differ in what they are able to say. A recovered source with no <c>Sent</c> structure
    /// anywhere records no folder direction at all, so a candidate resting only on its copies is
    /// direction-unknown by construction rather than by accident. Splitting the contribution per
    /// source is what makes that legible instead of surprising.
    /// <para>
    /// <see cref="ExactCandidateRelationsContributed"/> overlaps between sources: the same payload
    /// dated the same day can survive on two devices, and both are credited. These figures
    /// therefore do not sum to the workspace total, which is a property of duplicated evidence
    /// rather than an error.
    /// </para>
    /// </remarks>
    public sealed class MediaSourceDateContribution
    {
        /// <summary>The source these figures describe.</summary>
        public required long MediaSourceID { get; init; }

        /// <summary>How many physical files the source holds.</summary>
        public required int MediaFileCount { get; init; }

        /// <summary>How many of those files carry a naming-derived date.</summary>
        public required int MediaFileWithFileDateCount { get; init; }

        /// <summary>
        /// How many distinct candidate-eligible assets the source holds a dated copy of.
        /// </summary>
        public required int DistinctNonZeroAssetsWithFileDate { get; init; }

        /// <summary>
        /// Exact-date attachment/asset relationships for which at least one supporting copy came
        /// from this source.
        /// </summary>
        public required int ExactCandidateRelationsContributed { get; init; }
    }
}
