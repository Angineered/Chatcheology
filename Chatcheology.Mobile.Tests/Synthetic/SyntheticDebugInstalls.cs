namespace Chatcheology.Mobile.Synthetic
{
    /// <summary>
    /// The synthetic archives a Debug build of the Android app installs from its start screen, in one place so
    /// the tests check exactly what the buttons write: the current archive format (version 3, CH-53), with the
    /// invented yearly memories and, for the memories button, the invented memory layers (CH-42). Gifted noted dates
    /// (version 2) stay a test fixture (<see cref="SyntheticArchiveOptions.NotedDates"/>).
    /// </summary>
    internal static class SyntheticDebugInstalls
    {
        /// <summary>"Install synthetic archive": every card and label the screens show, and the yearly memories.</summary>
        internal static readonly SyntheticArchiveOptions Standard = new(
            Audio: 2, Documents: 1, BrokenImages: 1, LowQualityCopies: 3, Forwarded: 30, Links: 20, YearlyMemories: true);

        /// <summary>
        /// "Install synthetic archive (mid-day joins)": 130 days for 3,000 messages, about 23 a day, so 150-message
        /// pages join inside a day; with the yearly memories.
        /// </summary>
        internal static readonly SyntheticArchiveOptions MidDay = new(Days: 130, YearlyMemories: true);

        /// <summary>"Install synthetic archive with memories": the Shared gaming and Holidays layers, and the yearly memories.</summary>
        internal static readonly SyntheticArchiveOptions WithMemories = new(
            Audio: 1, Documents: 1, LowQualityCopies: 2, Forwarded: 10, Links: 10, Memories: true, YearlyMemories: true);
    }
}
