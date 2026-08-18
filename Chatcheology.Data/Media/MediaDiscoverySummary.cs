namespace Chatcheology.Data.Media
{
    /// <summary>
    /// What one recursive walk of a media root found, counted rather than listed.
    /// </summary>
    /// <remarks>
    /// Every member is an aggregate. There is no file name, no relative path and no date belonging
    /// to any individual file anywhere in this type, so a summary of a real personal archive can be
    /// reported, logged or shown without disclosing its contents. The one textual member,
    /// <see cref="UnknownExtensionCounts"/>, holds bare extensions.
    /// <para>
    /// The same summary describes a read-only preflight and a committed inventory, because both run
    /// the same walk. A preflight that reported different numbers from the inventory it precedes
    /// would be worth very little as a check on it.
    /// </para>
    /// </remarks>
    public sealed class MediaDiscoverySummary
    {
        /// <summary>How many physical files were found.</summary>
        public required int FileCount { get; init; }

        /// <summary>Their total length in bytes.</summary>
        public required long TotalSizeBytes { get; init; }

        /// <summary>How many classified as <see cref="MediaType.Image"/>.</summary>
        public required int ImageCount { get; init; }

        /// <summary>How many classified as <see cref="MediaType.Video"/>.</summary>
        public required int VideoCount { get; init; }

        /// <summary>How many classified as <see cref="MediaType.Audio"/>.</summary>
        public required int AudioCount { get; init; }

        /// <summary>How many classified as <see cref="MediaType.Document"/>.</summary>
        public required int DocumentCount { get; init; }

        /// <summary>
        /// How many classified as <see cref="MediaType.Unknown"/>: an unrecognised extension, or an
        /// empty file whose extension therefore describes nothing.
        /// </summary>
        public required int UnknownCount { get; init; }

        /// <summary>How many the path identifies as sent.</summary>
        public required int SentCount { get; init; }

        /// <summary>How many the path identifies as not sent.</summary>
        public required int NonSentCount { get; init; }

        /// <summary>
        /// How many gave no direction evidence at all.
        /// </summary>
        /// <remarks>
        /// Present so the three direction counts add up to <see cref="FileCount"/>. Without it,
        /// a source type whose layout carries no direction meaning would appear as an archive in
        /// which nothing was ever sent, rather than one in which nothing is known.
        /// </remarks>
        public required int DirectionUnknownCount { get; init; }

        /// <summary>How many file names encoded a valid calendar date.</summary>
        public required int FileDateCount { get; init; }

        /// <summary>
        /// How many files are zero bytes.
        /// </summary>
        /// <remarks>
        /// Worth its own count. Empty files all share one SHA-256, so they collapse into a single
        /// asset during deduplication and would otherwise be invisible in the totals; and a large
        /// number of them says something about how well a copy or a recovery went.
        /// </remarks>
        public required int ZeroByteFileCount { get; init; }

        /// <summary>How many carry the hidden attribute. Diagnostic only; not stored.</summary>
        public required int HiddenFileCount { get; init; }

        /// <summary>How many carry the system attribute. Diagnostic only; not stored.</summary>
        public required int SystemFileCount { get; init; }

        /// <summary>
        /// The extensions of files that classified as <see cref="MediaType.Unknown"/>, with counts,
        /// most frequent first and then by extension.
        /// </summary>
        /// <remarks>
        /// Diagnostic only, and deterministic so two runs of the same source produce the same
        /// report. It answers the one question the unknown count raises — what are they? — from
        /// evidence rather than from guesswork, so the classification table can be extended for what
        /// an archive actually holds instead of for what it might.
        /// <para>
        /// Empty files appear here under their own extension even though they were classified
        /// unknown for their size rather than their name. <see cref="ZeroByteFileCount"/> is what
        /// separates the two causes.
        /// </para>
        /// </remarks>
        public required IReadOnlyList<MediaExtensionCount> UnknownExtensionCounts { get; init; }
    }
}
