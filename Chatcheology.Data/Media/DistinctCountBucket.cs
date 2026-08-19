namespace Chatcheology.Data.Media
{
    /// <summary>How many assets carried a given number of distinct observations.</summary>
    /// <remarks>
    /// Reported alongside the three-way agreement outcome rather than instead of it. The outcome
    /// compresses an asset into one state by precedence; this distribution is the uncompressed
    /// fact, so nothing the classification hides is lost.
    /// </remarks>
    public sealed class DistinctCountBucket
    {
        /// <summary>The number of distinct observations on one asset.</summary>
        public required int DistinctCount { get; init; }

        /// <summary>Assets with exactly that many.</summary>
        public required int AssetCount { get; init; }
    }
}
