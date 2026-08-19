namespace Chatcheology.Data.Media
{
    /// <summary>How many files and distinct assets fall into one suffix syntax class.</summary>
    /// <remarks>
    /// Assets are counted as well as files because one deduplicated payload can carry several
    /// names in several classes at once. The asset counts therefore overlap between classes and
    /// must not be summed as though they partitioned the archive.
    /// </remarks>
    public sealed class SuffixClassCounts
    {
        /// <summary>The class these counts describe.</summary>
        public required SuffixSyntaxClass SuffixClass { get; init; }

        /// <summary>Physical files whose suffix falls in this class.</summary>
        public required int FileCount { get; init; }

        /// <summary>Distinct assets with at least one such file.</summary>
        public required int AssetCount { get; init; }
    }
}
