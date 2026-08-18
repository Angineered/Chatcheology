namespace Chatcheology.Data.Media
{
    /// <summary>
    /// The path rules a media source obeys: how a root is normalised, when two roots overlap, and
    /// how a file beneath a root is named relative to it.
    /// </summary>
    /// <remarks>
    /// These are Windows path rules, applied to the text of a path. They are not filesystem
    /// identity: a directory reached through a <c>subst</c> drive, a UNC alias, an 8.3 short name
    /// or a junction is a different string and will not be recognised as the same place. Resolving
    /// physical identity would mean opening each candidate directory and comparing volume and file
    /// identifiers, which is a larger promise than the guard needs to make and a worse one to make
    /// badly. The limitation is documented rather than half-implemented.
    /// </remarks>
    internal static class MediaSourcePath
    {
        /// <summary>
        /// The directory separator a canonical relative path is stored with.
        /// </summary>
        /// <remarks>
        /// <c>/</c> rather than the platform separator, so a stored path means the same thing
        /// wherever the workspace is later read, and so the same tree inventoried twice cannot
        /// produce two spellings of one file.
        /// </remarks>
        internal const char CanonicalDirectorySeparator = '/';

        /// <summary>
        /// The comparison two Windows paths are held to.
        /// </summary>
        /// <remarks>
        /// Ordinal, so comparison does not vary by culture, and case-insensitive, because Windows
        /// paths are.
        /// </remarks>
        internal const StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;

        /// <summary>
        /// Normalises a supplied media root to the form the workspace stores and compares.
        /// </summary>
        /// <remarks>
        /// Fully qualified through <see cref="Path.GetFullPath(string)"/>, which resolves any
        /// relative segments, then stripped of a trailing separator so that <c>C:\Media</c> and
        /// <c>C:\Media\</c> are one root rather than two. A filesystem root such as <c>E:\</c>
        /// keeps its separator, because there it is part of the path rather than punctuation after
        /// it.
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// <paramref name="rootPath"/> is empty, whitespace, or not a path this platform can
        /// qualify.
        /// </exception>
        internal static string Normalise(string rootPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

            var fullPath = Path.GetFullPath(rootPath);

            if (string.Equals(fullPath, Path.GetPathRoot(fullPath), PathComparison))
            {
                return fullPath;
            }

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>
        /// Whether two normalised roots describe overlapping trees: the same directory, or one
        /// inside the other.
        /// </summary>
        /// <remarks>
        /// Overlap matters because inventorying both would record one physical file twice, under
        /// two sources, as two <c>MediaFile</c> rows. Nothing would be corrupt — the two rows would
        /// deduplicate to one asset — but every count of "how many files does this archive hold"
        /// would then be wrong, and later matching would see two pieces of evidence where the
        /// archive contains one.
        /// <para>
        /// Containment is tested against the separator, not against the raw string, so
        /// <c>C:\Media</c> and <c>C:\Media2</c> are siblings rather than an ancestor and a
        /// descendant. Sibling roots are exactly what registering several media sources is for.
        /// </para>
        /// </remarks>
        internal static bool Overlaps(string normalisedLeft, string normalisedRight) =>
            string.Equals(normalisedLeft, normalisedRight, PathComparison)
            || Contains(normalisedLeft, normalisedRight)
            || Contains(normalisedRight, normalisedLeft);

        /// <summary>
        /// Whether <paramref name="normalisedDescendant"/> lies strictly beneath
        /// <paramref name="normalisedAncestor"/>.
        /// </summary>
        private static bool Contains(string normalisedAncestor, string normalisedDescendant)
        {
            // A filesystem root keeps its trailing separator through normalisation; anything else
            // has had it stripped, and needs it back before this can be a segment-wise test.
            var prefix = normalisedAncestor.EndsWith(Path.DirectorySeparatorChar)
                ? normalisedAncestor
                : normalisedAncestor + Path.DirectorySeparatorChar;

            return normalisedDescendant.Length > prefix.Length
                && normalisedDescendant.StartsWith(prefix, PathComparison);
        }

        /// <summary>
        /// The canonical relative path of <paramref name="fullFilePath"/> beneath
        /// <paramref name="normalisedRoot"/>.
        /// </summary>
        /// <remarks>
        /// Relative, never absolute; no leading separator; no <c>.</c> or <c>..</c> segment;
        /// <c>/</c> as the separator; every other character of every segment preserved exactly as
        /// the source spelled it, including its casing.
        /// <para>
        /// The workspace records where a file sits inside its source, not where that source sits on
        /// this machine. Storing the full physical path in every one of tens of thousands of rows
        /// would put the layout of one computer into a record meant to outlive it.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The file does not lie beneath the root. Enumeration should make this impossible, so it
        /// is raised rather than worked around.
        /// </exception>
        internal static string ToCanonicalRelativePath(string normalisedRoot, string fullFilePath)
        {
            var relativePath = Path.GetRelativePath(normalisedRoot, fullFilePath);

            if (Path.IsPathRooted(relativePath)
                || relativePath.StartsWith("..", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A file enumerated beneath the media root does not resolve to a path inside " +
                    "it. The inventory is abandoned rather than recording a file it cannot place.");
            }

            return relativePath.Replace(Path.DirectorySeparatorChar, CanonicalDirectorySeparator);
        }

        /// <summary>
        /// Resolves a stored relative path back to a physical file beneath
        /// <paramref name="normalisedRoot"/>, refusing anything that would escape it.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> and the full path when it resolves safely inside the root;
        /// otherwise <see langword="false"/>.
        /// </returns>
        /// <remarks>
        /// Checked rather than trusted. The relative path is read back out of a database that this
        /// build wrote, but a workspace file is an ordinary file on the user's disk and can be
        /// edited by anything; a row saying <c>..\..\Windows\System32</c> must resolve to a refusal
        /// rather than to a read outside the source the user chose.
        /// <para>
        /// The check is made against the combined and re-qualified path, not against the text of
        /// the relative path, so it cannot be defeated by a spelling that only looks safe.
        /// </para>
        /// </remarks>
        internal static bool TryResolveUnderRoot(
            string normalisedRoot, string relativePath, out string fullPath)
        {
            fullPath = string.Empty;

            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            {
                return false;
            }

            string combined;

            try
            {
                combined = Path.GetFullPath(
                    Path.Combine(
                        normalisedRoot,
                        relativePath.Replace(
                            CanonicalDirectorySeparator, Path.DirectorySeparatorChar)));
            }
            catch (Exception exception) when (
                exception is ArgumentException or PathTooLongException or NotSupportedException)
            {
                return false;
            }

            if (!Contains(normalisedRoot, combined))
            {
                return false;
            }

            fullPath = combined;

            return true;
        }
    }
}
