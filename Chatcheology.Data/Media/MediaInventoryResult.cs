namespace Chatcheology.Data.Media
{
    /// <summary>
    /// What one successful inventory recorded.
    /// </summary>
    public sealed class MediaInventoryResult
    {
        /// <summary>The generated <c>MediaSource.MediaSourceID</c>.</summary>
        /// <remarks>
        /// Returned because SQLite generates it and the caller cannot otherwise observe which
        /// source its own call created.
        /// </remarks>
        public required long MediaSourceID { get; init; }

        /// <summary>
        /// What the walk found, counted. One <c>MediaFile</c> row exists for each file counted here.
        /// </summary>
        /// <remarks>
        /// The same type a read-only preflight returns, deliberately. An inventory and the preflight
        /// that precedes it run one walk between them, so their numbers are comparable by
        /// construction rather than by two counting routines agreeing.
        /// </remarks>
        public required MediaDiscoverySummary Summary { get; init; }
    }
}
