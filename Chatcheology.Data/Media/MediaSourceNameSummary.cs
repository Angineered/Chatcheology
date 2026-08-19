namespace Chatcheology.Data.Media
{
    /// <summary>What one media source contributed to the name census.</summary>
    /// <remarks>
    /// The source type is reported rather than assumed. <c>MediaClassification.DeriveFileDate</c>
    /// reads nothing outside a WhatsApp media directory, so a source of another type would put
    /// every one of its files among the undated names for a reason that has nothing to do with how
    /// they are named.
    /// </remarks>
    public sealed class MediaSourceNameSummary
    {
        /// <summary>The source these figures describe.</summary>
        public required long MediaSourceID { get; init; }

        /// <summary>Its recorded type, verbatim.</summary>
        public required string SourceType { get; init; }

        /// <summary>Whether that type is the one the date convention is read for.</summary>
        public required bool IsWhatsAppMediaDirectory { get; init; }

        /// <summary>Physical files held by this source.</summary>
        public required int MediaFileCount { get; init; }

        /// <summary>Of those, how many carry a committed <c>FileDate</c>.</summary>
        public required int MediaFileWithFileDateCount { get; init; }

        /// <summary>Of those, how many carry none.</summary>
        public required int MediaFileWithNullFileDateCount { get; init; }

        /// <summary>Suffix classes across this source's dated files.</summary>
        public required IReadOnlyList<SuffixClassCounts> SuffixClasses { get; init; }

        /// <summary>Undated-name features for this source.</summary>
        public required UndatedNameFeatureCounts UndatedFeatures { get; init; }
    }
}
