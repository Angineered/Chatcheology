namespace Chatcheology.Data.Media
{
    /// <summary>One normalised name shape and how often it was seen.</summary>
    public sealed class ShapeSignatureCount
    {
        /// <summary>
        /// The shape, carrying no letters or digits from any original name. The pooled row for
        /// everything outside the reported top of the table is named <c>Other</c>.
        /// </summary>
        public required string Signature { get; init; }

        /// <summary>Physical files with this shape.</summary>
        public required int FileCount { get; init; }

        /// <summary>
        /// Distinct assets with at least one file of this shape. These overlap between shapes and
        /// do not sum to the archive.
        /// </summary>
        public required int AssetCount { get; init; }
    }
}
