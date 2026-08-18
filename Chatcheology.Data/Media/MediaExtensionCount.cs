namespace Chatcheology.Data.Media
{
    /// <summary>
    /// How many files in a media root carry one extension.
    /// </summary>
    /// <remarks>
    /// An extension and a number, and nothing else. A histogram of these says what kinds of file a
    /// source holds without naming a single one of them, which is what makes it safe to print in a
    /// report about real personal media.
    /// </remarks>
    public sealed class MediaExtensionCount
    {
        /// <summary>
        /// The lower-case extension including its leading dot, or
        /// <see cref="MediaClassification.NoExtensionLabel"/> for files that have none.
        /// </summary>
        public required string Extension { get; init; }

        /// <summary>How many files carry it.</summary>
        public required int Count { get; init; }
    }
}
