using Chatcheology.Data.Media;

namespace Chatcheology.Data.Viewer
{
    /// <summary>
    /// One physical copy the workspace records for an already-resolved media asset.
    /// </summary>
    /// <remarks>
    /// <para><strong>Presentation, never evidence.</strong> This describes where a copy of an asset
    /// is recorded to sit on this machine, so the viewer can show a picture or hand a file to the
    /// operating system. Which asset an attachment corresponds to was settled by Phase 7 before
    /// anything here was read, and nothing here can revisit it.</para>
    /// <para><strong>No identity or matching facts.</strong> <c>SHA256</c>, <c>FileDate</c> and
    /// <c>IsSent</c> are all stored on the same rows and none of them is here: the first is the
    /// deduplication identity rather than a descriptive fact, and the other two are matching
    /// evidence inputs, which a presentation descriptor has no business carrying where a later
    /// reader could mistake them for a reason to prefer one copy.</para>
    /// <para><strong>No duplication of the resolved asset.</strong> Duration and dimensions belong
    /// to <c>Phase7ResolvedAssetDescriptor</c>, which is the authority on what the asset is. This
    /// describes only where a copy of it lives.</para>
    /// </remarks>
    public sealed class MediaAssetLocation
    {
        /// <summary>The resolved asset this is a copy of.</summary>
        public required long MediaAssetID { get; init; }

        /// <summary>The physical file record.</summary>
        public required long MediaFileID { get; init; }

        /// <summary>The media source the file was discovered beneath.</summary>
        public required long MediaSourceID { get; init; }

        /// <summary>That source's stored display name, for telling the operator where a copy is.</summary>
        public required string MediaSourceDisplayName { get; init; }

        /// <summary>The source's recorded root path.</summary>
        public required string RootPath { get; init; }

        /// <summary>The file's recorded path beneath that root, in the stored canonical form.</summary>
        public required string RelativePath { get; init; }

        /// <summary>The file's recorded name.</summary>
        public required string FileName { get; init; }

        /// <summary>The file's recorded extension, or <see langword="null"/> when it records none.</summary>
        public required string? Extension { get; init; }

        /// <summary>
        /// The asset's recorded media type, read from <c>MediaAsset</c> rather than from the
        /// physical file row.
        /// </summary>
        /// <remarks>
        /// The asset is the authority on what the content is; a copy's own recorded type describes
        /// the same payload and could in principle have been written by an earlier inventory pass.
        /// Typed here rather than left as stored text, because the spelling belongs to this
        /// assembly and a caller outside it comparing <c>"Image"</c> is exactly the drift the
        /// stored-text owner exists to prevent.
        /// </remarks>
        public required MediaType MediaType { get; init; }

        /// <summary>The file's recorded size in bytes.</summary>
        public required long SizeBytes { get; init; }

        /// <summary>
        /// <see cref="RootPath"/> and <see cref="RelativePath"/> combined into a path this machine
        /// can open.
        /// </summary>
        /// <remarks>
        /// Composed inside the data layer because the stored relative form is a workspace
        /// convention — relative, no leading separator, <c>/</c> as the separator — and a
        /// presentation layer that knew that convention would be a second place it is spelled out.
        /// <para>
        /// Composition is not a claim that the file is there. Whether a copy is currently reachable
        /// is a question about this machine at this moment, and the caller asks it.
        /// </para>
        /// </remarks>
        public required string FullPath { get; init; }
    }
}
