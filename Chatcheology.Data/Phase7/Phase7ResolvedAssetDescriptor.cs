namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// What the reconstruction model reports about a resolved attachment's asset.
    /// </summary>
    /// <remarks>
    /// <para>Every field is read straight from the stored <c>MediaAsset</c> row. Nothing is recomputed,
    /// nothing is inferred from a file name, and no raw media is opened — the reconstruction boundary is
    /// a read over the workspace database and nothing else.</para>
    /// <para><strong>There is no path, no file name and no hash here.</strong> The asset table carries
    /// no path or name to expose, and <c>SHA256</c> is deliberately left out: it is the deduplication
    /// identity rather than a descriptive fact, <see cref="MediaAssetID"/> already identifies the asset
    /// for every reader inside the workspace, and whether a content hash belongs in an exportable
    /// projection is a decision a later phase makes deliberately rather than one this DTO settles by
    /// accident.</para>
    /// </remarks>
    public sealed class Phase7ResolvedAssetDescriptor
    {
        /// <summary>The asset the attachment is resolved to.</summary>
        public required long MediaAssetID { get; init; }

        /// <summary>The asset's recorded media type.</summary>
        public required string MediaType { get; init; }

        /// <summary>The asset's recorded size in bytes.</summary>
        public required long SizeBytes { get; init; }

        /// <summary>The recorded duration in milliseconds, or <see langword="null"/>.</summary>
        public required long? DurationMS { get; init; }

        /// <summary>The recorded pixel width, or <see langword="null"/>.</summary>
        public required int? Width { get; init; }

        /// <summary>The recorded pixel height, or <see langword="null"/>.</summary>
        public required int? Height { get; init; }
    }
}
