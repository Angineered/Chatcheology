namespace Chatcheology.Data.OlderExports
{
    /// <summary>
    /// Why lines of an inner export are not found exactly in an outer export, as counts only.
    /// </summary>
    /// <remarks>
    /// Produced by <see cref="OlderExportOverlap.Analyse"/>. The five categories are exclusive and
    /// add up to <see cref="NotFoundCount"/>. No text, name or timestamp is carried.
    /// </remarks>
    public sealed class OlderExportOverlapBreakdown
    {
        /// <summary>All logical messages of the inner export.</summary>
        public required int InnerCount { get; init; }

        /// <summary>Inner messages with no exact counterpart in the outer export.</summary>
        public required int NotFoundCount { get; init; }

        /// <summary>
        /// The inner line is <c>(file attached)</c> and the outer has <c>&lt;Media omitted&gt;</c>
        /// at the same timestamp and sender.
        /// </summary>
        public required int FileAttachedWhereOuterHasMediaOmittedCount { get; init; }

        /// <summary>
        /// The outer has the identical line, but fewer times than the inner repeats it.
        /// </summary>
        public required int RepeatedMoreOftenThanInOuterCount { get; init; }

        /// <summary>
        /// The outer has a line at the same timestamp and sender whose content differs only in
        /// leading or trailing whitespace (per line or overall).
        /// </summary>
        public required int WhitespaceOnlyDifferenceCount { get; init; }

        /// <summary>
        /// The outer has lines at the same timestamp and sender, but their content differs in
        /// some other way.
        /// </summary>
        public required int OtherContentDifferenceCount { get; init; }

        /// <summary>The outer has no line at all with the same timestamp and sender.</summary>
        public required int NoOuterLineAtTimestampAndSenderCount { get; init; }

        /// <summary>
        /// Outer messages whose timestamp lies within the inner export's period, first to last
        /// inclusive.
        /// </summary>
        public required int OuterInInnerPeriodCount { get; init; }

        /// <summary>Of those, how many have no exact counterpart in the inner export.</summary>
        public required int OuterInInnerPeriodNotFoundInInnerCount { get; init; }
    }
}
