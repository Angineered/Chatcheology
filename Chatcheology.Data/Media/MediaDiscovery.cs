namespace Chatcheology.Data.Media
{
    /// <summary>
    /// One completed walk of a media root: the files found, and what the tree as a whole turned out
    /// to say about direction.
    /// </summary>
    /// <remarks>
    /// Direction cannot be settled file by file, which is why the walk's result is a type rather
    /// than a list. Whether a file outside a <c>Sent</c> folder means "not sent" depends on whether
    /// the source separates sent media at all, and that is only known once every file has been
    /// seen. Pairing the files with that conclusion keeps the two from being used apart.
    /// </remarks>
    internal sealed class MediaDiscovery
    {
        /// <summary>Every file found, ordered by canonical relative path.</summary>
        internal required IReadOnlyList<DiscoveredMediaFile> Files { get; init; }

        /// <summary>
        /// Whether this source's direction may be read at all: a layout whose <c>Sent</c> folders
        /// are known, which demonstrably contains at least one.
        /// </summary>
        /// <remarks>
        /// False for a source type whose folder conventions this build does not know, and false for
        /// a WhatsApp tree with no <c>Sent</c> folder anywhere — a recovered or partially copied
        /// source, typically. In both cases every file's direction is recorded as unknown.
        /// </remarks>
        internal required bool DirectionEvidenceAvailable { get; init; }

        /// <summary>
        /// The direction to store for <paramref name="file"/>.
        /// </summary>
        internal bool? IsSent(DiscoveredMediaFile file) =>
            DirectionEvidenceAvailable ? file.HasSentDirectorySegment : null;
    }
}
