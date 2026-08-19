namespace Chatcheology.Data.Matching
{
    /// <summary>
    /// How many distinct candidate-eligible assets share a single calendar date.
    /// </summary>
    /// <remarks>
    /// The direct measure of what date evidence can and cannot do. If a typical date carries fifty
    /// assets then date evidence alone can never narrow an attachment to one item, and that is a
    /// fact about the archive rather than a shortcoming of the analysis. This is the figure a later
    /// decision about ordering or filename evidence should be argued from.
    /// <para>
    /// Only dates carrying at least one eligible asset are counted, so a quiet week contributes
    /// nothing rather than contributing zeroes that would drag the middle of the distribution down.
    /// </para>
    /// </remarks>
    public sealed class AssetsPerDateDensity
    {
        /// <summary>How many calendar dates carry at least one candidate-eligible asset.</summary>
        public required int DatedEligibleMediaDateCount { get; init; }

        /// <summary>The fewest eligible assets any counted date carries.</summary>
        public required int Minimum { get; init; }

        /// <summary>
        /// The middle of the per-date counts, taken as the lower of the two middle values when the
        /// number of dates is even.
        /// </summary>
        /// <remarks>
        /// The lower median is chosen and stated rather than left to the reader, so two runs of this
        /// census are comparable. Averaging the two middle values would produce a fractional count
        /// of assets, which is not a thing the archive contains.
        /// </remarks>
        public required int Median { get; init; }

        /// <summary>The most eligible assets any counted date carries.</summary>
        public required int Maximum { get; init; }
    }
}
