using System.Globalization;
using System.Text.RegularExpressions;

namespace Chatcheology.Archive
{
    /// <summary>
    /// The fixed facts of the portable archive format: its version, its file names, and how it
    /// spells dates and media file names.
    /// </summary>
    /// <remarks>
    /// <para>An archive is one folder holding <see cref="DatabaseFileName"/>, a
    /// <see cref="MediaFolderName"/> folder with one file per distinct media payload,
    /// <see cref="ManifestFileName"/> and <see cref="ContentsFileName"/>. It holds the result of a
    /// reconstruction, never the evidence work behind it.</para>
    /// <para><strong>One owner for every spelling.</strong> The writer and the reader both take their
    /// formats from here, so the two cannot come to disagree about what is stored.</para>
    /// </remarks>
    public static partial class ArchiveFormat
    {
        /// <summary>
        /// The format version this build writes, and the highest it reads. Stored as
        /// <c>PRAGMA user_version</c> and as the <c>FormatVersion</c> info row.
        /// </summary>
        /// <remarks>
        /// A whole number, and it is the major version: a reader refuses any archive whose version is
        /// higher than the one it knows. Filling a column the format already has (such as a message's
        /// forwarded status) does not change it.
        /// </remarks>
        public const int FormatVersion = 1;

        /// <summary>The SQLite index, written once and then read-only.</summary>
        public const string DatabaseFileName = "archive.db";

        /// <summary>The folder holding one file per distinct media payload.</summary>
        public const string MediaFolderName = "media";

        /// <summary>The format version, counts, and what produced the archive.</summary>
        public const string ManifestFileName = "manifest.json";

        /// <summary>The <c>sha256sum</c> list of every other file in the folder.</summary>
        public const string ContentsFileName = "CONTENTS.sha256";

        /// <summary>How a message's local wall-clock time is stored: no zone, no fraction.</summary>
        public const string LocalDateTimeFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss";

        /// <summary>How a UTC moment is stored: whole seconds, with a trailing <c>Z</c>.</summary>
        public const string UtcDateTimeFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'";

        /// <summary>The tables of format version 1, and no others.</summary>
        public static IReadOnlyList<string> TableNames { get; } =
        [
            "ArchiveInfo",
            "Participant",
            "Message",
            "Media",
            "Attachment",
            "AttachmentBasis",
        ];

        // \z, not $: $ also matches before a trailing newline, which must never pass as a plain name.
        [GeneratedRegex("^\\.[a-z0-9]{1,10}\\z", RegexOptions.CultureInvariant)]
        private static partial Regex ExtensionPattern();

        [GeneratedRegex("^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
        private static partial Regex Sha256Pattern();

        /// <summary>
        /// The name a media payload is stored under: its SHA-256, then the lower-cased extension of
        /// the copied file when that is a plain one.
        /// </summary>
        /// <remarks>
        /// Content-addressed, so there are no name clashes and no private phone paths, and a payload
        /// several messages share is stored once. The extension is taken from the original file name
        /// the archive records, which is how a reader finds the file again without a second column.
        /// </remarks>
        /// <exception cref="ArgumentException">The hash is not 64 lower-case hex digits.</exception>
        public static string MediaFileName(string sha256, string originalFileName)
        {
            ArgumentNullException.ThrowIfNull(originalFileName);

            if (!IsSha256(sha256))
            {
                throw new ArgumentException(
                    "A media hash must be 64 lower-case hexadecimal digits.", nameof(sha256));
            }

            return sha256 + SafeExtension(Path.GetExtension(originalFileName));
        }

        /// <summary>
        /// The media file's path inside the archive folder, with <c>/</c> as the separator, as
        /// <see cref="ContentsFileName"/> lists it.
        /// </summary>
        public static string MediaRelativePath(string sha256, string originalFileName) =>
            MediaFolderName + "/" + MediaFileName(sha256, originalFileName);

        /// <summary>The extension, lower-cased, when it is a plain one; otherwise none.</summary>
        public static string SafeExtension(string? extension)
        {
            var lower = extension?.ToLowerInvariant() ?? "";

            return ExtensionPattern().IsMatch(lower) ? lower : "";
        }

        /// <summary>Whether the text is a SHA-256 in the archive's spelling: 64 lower-case hex digits.</summary>
        public static bool IsSha256(string? text) => text is not null && Sha256Pattern().IsMatch(text);

        /// <summary>A local wall-clock reading, in the stored form.</summary>
        public static string FormatLocal(DateTime value) =>
            value.ToString(LocalDateTimeFormat, CultureInfo.InvariantCulture);

        /// <summary>A UTC moment, in the stored form. Any fraction of a second is dropped.</summary>
        /// <exception cref="ArgumentException">The value is not marked as UTC.</exception>
        public static string FormatUtc(DateTime value)
        {
            if (value.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "A UTC moment must be marked as UTC; a local or unmarked time would be stored as " +
                    "the wrong moment.",
                    nameof(value));
            }

            return value.ToString(UtcDateTimeFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>Reads a stored local wall-clock reading back, with no zone attached.</summary>
        /// <exception cref="ArchiveFormatException">The text is not in the stored form.</exception>
        internal static DateTime ParseLocal(string text) =>
            DateTime.TryParseExact(
                text, LocalDateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
                ? DateTime.SpecifyKind(value, DateTimeKind.Unspecified)
                : throw new ArchiveFormatException(
                    "A message time in the archive is not in the format's local date-time form.");

        /// <summary>Reads a stored UTC moment back, marked as UTC.</summary>
        /// <exception cref="ArchiveFormatException">The text is not in the stored form.</exception>
        internal static DateTime ParseUtc(string text) =>
            DateTime.TryParseExact(
                text,
                UtcDateTimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value)
                ? value
                : throw new ArchiveFormatException("A UTC time in the archive is not in the format's UTC form.");
    }
}
