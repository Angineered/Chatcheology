namespace Chatcheology.Data.Media
{
    /// <summary>
    /// Syntactic features of the file names that carry no <c>FileDate</c>.
    /// </summary>
    /// <remarks>
    /// Features, not causes, and deliberately overlapping: one name can satisfy several. Nothing
    /// here says why a name looks as it does. Whether it was written by WhatsApp, rewritten by a
    /// recovery tool, renamed by a person or produced by another application is not recorded
    /// anywhere in the workspace and cannot be recovered from the name.
    /// </remarks>
    public sealed class UndatedNameFeatureCounts
    {
        /// <summary>Names examined.</summary>
        public required int NameCount { get; init; }

        /// <summary>Contains eight consecutive ASCII digits anywhere.</summary>
        public required int ContainsEightDigitRun { get; init; }

        /// <summary>Contains eight consecutive ASCII digits immediately preceded by <c>-</c>.</summary>
        public required int ContainsHyphenPrefixedEightDigitRun { get; init; }

        /// <summary>Contains eight consecutive ASCII digits that form a real calendar date.</summary>
        public required int ContainsValidCalendarDateRun { get; init; }

        /// <summary>Contains the literal text <c>-WA</c>.</summary>
        public required int ContainsDashWAMarker { get; init; }

        /// <summary>Contains <c>WA</c> followed immediately by one or more ASCII digits.</summary>
        public required int ContainsWAFollowedByDigits { get; init; }

        /// <summary>
        /// Contains <c>-</c>, eight ASCII digits and <c>-WA</c>, whatever those digits mean.
        /// </summary>
        public required int ContainsFullStructure { get; init; }

        /// <summary>
        /// Contains that full structure where the eight digits are not a real calendar date.
        /// </summary>
        /// <remarks>
        /// The one feature that explains a null <c>FileDate</c> on an otherwise WhatsApp-shaped
        /// name. For files belonging to a WhatsApp media directory this must equal
        /// <see cref="ContainsFullStructure"/>, because a valid date anywhere in such a name would
        /// have produced a <c>FileDate</c> and kept the file out of this population entirely. A
        /// divergence means either this census disagrees with the committed classifier or a source
        /// is not a WhatsApp media directory.
        /// </remarks>
        public required int ContainsFullStructureWithInvalidDate { get; init; }

        /// <summary>Names satisfying none of the features above.</summary>
        public required int MatchedNoFeature { get; init; }
    }
}
