namespace Chatcheology.Data.Media
{
    /// <summary>
    /// One physical file found beneath a media root, with everything discovery can know about it.
    /// </summary>
    /// <remarks>
    /// Built entirely from directory metadata. No file is opened during discovery, so nothing here
    /// depends on reading content: the hash, duration, width and height a <c>MediaFile</c> row can
    /// carry are all absent, and hashing fills the first of them in a separate, resumable pass.
    /// <para>
    /// Internal because it is the shape inventory passes between its own steps. What callers see is
    /// <see cref="MediaDiscoverySummary"/>, which counts these without naming any of them.
    /// </para>
    /// </remarks>
    internal sealed class DiscoveredMediaFile
    {
        /// <summary>The canonical, <c>/</c>-separated path relative to the source root.</summary>
        internal required string RelativePath { get; init; }

        /// <summary>The file's name, exactly as the source spells it.</summary>
        internal required string FileName { get; init; }

        /// <summary>Lower-case extension including the leading dot, or null if there is none.</summary>
        internal required string? Extension { get; init; }

        /// <summary>The file's length in bytes, from directory metadata.</summary>
        internal required long SizeBytes { get; init; }

        /// <summary>The content kind implied by the extension and the size.</summary>
        internal required MediaType MediaType { get; init; }

        /// <summary>The date the file name encodes, or null when it encodes none.</summary>
        internal required DateOnly? FileDate { get; init; }

        /// <summary>Direction read from the path, or null when the layout gives none.</summary>
        internal required bool? IsSent { get; init; }

        /// <summary>
        /// Whether the file carries the hidden attribute.
        /// </summary>
        /// <remarks>
        /// Not stored in the workspace. Discovery deliberately includes hidden and system files —
        /// omitting them would silently shrink an archive the user believes was copied whole — and
        /// counting them lets a file-count that disagrees with an expected total be explained
        /// without walking the tree a second time.
        /// </remarks>
        internal required bool IsHidden { get; init; }

        /// <summary>Whether the file carries the system attribute. Not stored; counted only.</summary>
        internal required bool IsSystem { get; init; }
    }
}
