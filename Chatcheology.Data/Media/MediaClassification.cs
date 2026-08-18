using System.Globalization;

namespace Chatcheology.Data.Media
{
    /// <summary>
    /// Derives what can be read from a media file's name and size alone: its extension, its broad
    /// <see cref="MediaType"/>, whether its path says it was sent, and any date its name encodes.
    /// </summary>
    /// <remarks>
    /// Every method here is a pure function of its arguments. Nothing reads the filesystem, the
    /// clock, the current culture or the database, so a given name always classifies the same way
    /// and each rule can be tested on its own rather than only through an inventory run.
    /// <para>
    /// These are deliberately conservative readings of weak evidence. Each one answers "what does
    /// this name definitely say?" and returns nothing when the answer is "not enough", because a
    /// null that later matching can see is worth more than a guess it cannot distinguish from a
    /// fact.
    /// </para>
    /// </remarks>
    public static class MediaClassification
    {
        /// <summary>
        /// The extension a file has when it has none, as written in a diagnostic histogram.
        /// </summary>
        /// <remarks>
        /// A display value for reports only. The database stores a real null.
        /// </remarks>
        public const string NoExtensionLabel = "<none>";

        /// <summary>
        /// The path segment that marks outgoing media in a WhatsApp media directory.
        /// </summary>
        internal const string SentDirectorySegment = "Sent";

        /// <summary>
        /// How a naming-derived date is stored: a calendar date with no time and no zone.
        /// </summary>
        internal const string FileDateFormat = "yyyy'-'MM'-'dd";

        /// <summary>
        /// Extension to content kind. Lower-case invariant keys, matching what
        /// <see cref="NormaliseExtension"/> produces.
        /// </summary>
        /// <remarks>
        /// An explicit table rather than a pattern or a content sniff. Extension is weak evidence,
        /// but it is evidence the file itself carries, and reading it the same way every time is
        /// what makes the resulting counts comparable between runs.
        /// <para>
        /// Ordinal comparison, because these keys are already normalised and a culture-sensitive
        /// lookup would make classification depend on the machine it ran on.
        /// </para>
        /// <para>
        /// <see cref="MediaType.Document"/> is deliberately the broad category: alongside office
        /// formats it holds archives and structured or plain-text files, which is the same
        /// reading that already puts <c>.zip</c>, <c>.csv</c>, <c>.txt</c> and <c>.vcf</c> here.
        /// </para>
        /// <para>
        /// The table is extended from evidence rather than from anticipation. An extension is added
        /// once real archives are seen to contain it and it clearly belongs to one of these
        /// categories; anything genuinely not media — an installer, a database dump's companion, a
        /// marker file — stays <see cref="MediaType.Unknown"/>, which is an honest answer rather
        /// than a gap.
        /// </para>
        /// </remarks>
        private static readonly Dictionary<string, MediaType> MediaTypesByExtension =
            new(StringComparer.Ordinal)
            {
                [".jpg"] = MediaType.Image,
                [".jpeg"] = MediaType.Image,
                [".png"] = MediaType.Image,
                [".gif"] = MediaType.Image,
                [".webp"] = MediaType.Image,
                [".bmp"] = MediaType.Image,
                [".heic"] = MediaType.Image,
                [".heif"] = MediaType.Image,
                [".svg"] = MediaType.Image,
                [".eps"] = MediaType.Image,

                [".mp4"] = MediaType.Video,
                [".3gp"] = MediaType.Video,
                [".mkv"] = MediaType.Video,
                [".mov"] = MediaType.Video,
                [".webm"] = MediaType.Video,

                [".opus"] = MediaType.Audio,
                [".ogg"] = MediaType.Audio,
                [".mp3"] = MediaType.Audio,
                [".m4a"] = MediaType.Audio,
                [".aac"] = MediaType.Audio,
                [".wav"] = MediaType.Audio,
                [".amr"] = MediaType.Audio,
                [".flac"] = MediaType.Audio,

                [".pdf"] = MediaType.Document,
                [".doc"] = MediaType.Document,
                [".docx"] = MediaType.Document,
                [".xls"] = MediaType.Document,
                [".xlsx"] = MediaType.Document,
                [".ppt"] = MediaType.Document,
                [".pptx"] = MediaType.Document,
                [".txt"] = MediaType.Document,
                [".csv"] = MediaType.Document,
                [".rtf"] = MediaType.Document,
                [".zip"] = MediaType.Document,
                [".vcf"] = MediaType.Document,
                [".7z"] = MediaType.Document,
                [".rar"] = MediaType.Document,
                [".json"] = MediaType.Document,
                [".md"] = MediaType.Document,
                [".sql"] = MediaType.Document,
            };

        /// <summary>
        /// The extension of <paramref name="fileName"/> as the workspace stores it: lower-case
        /// invariant, leading <c>.</c> included, or null when the file has no extension.
        /// </summary>
        /// <remarks>
        /// Lower-cased with <see cref="CultureInfo.InvariantCulture"/> rather than the current
        /// culture, so a Turkish locale cannot turn <c>.JPG</c> into something that misses the
        /// classification table.
        /// <para>
        /// Note one inherited behaviour worth knowing rather than working around: a dot-prefixed
        /// name with no other dot, such as <c>.nomedia</c>, is treated by
        /// <see cref="Path.GetExtension(string)"/> as being entirely extension. Such a file is
        /// therefore stored with extension <c>.nomedia</c> and classifies as
        /// <see cref="MediaType.Unknown"/>, which is the honest answer for a marker file. The
        /// source file is never renamed either way.
        /// </para>
        /// </remarks>
        public static string? NormaliseExtension(string fileName)
        {
            ArgumentNullException.ThrowIfNull(fileName);

            var extension = Path.GetExtension(fileName);

            return extension.Length == 0
                ? null
                : extension.ToLowerInvariant();
        }

        /// <summary>
        /// The content kind <paramref name="extension"/> alone implies, ignoring file size.
        /// </summary>
        /// <remarks>
        /// Prefer <see cref="Classify"/>, which also applies the empty-file rule. This overload
        /// exists so the extension table can be tested as the table it is.
        /// </remarks>
        public static MediaType ClassifyByExtension(string? extension) =>
            extension is not null && MediaTypesByExtension.TryGetValue(extension, out var mediaType)
                ? mediaType
                : MediaType.Unknown;

        /// <summary>
        /// The content kind a file of this <paramref name="extension"/> and
        /// <paramref name="sizeBytes"/> carries.
        /// </summary>
        /// <remarks>
        /// An empty file is <see cref="MediaType.Unknown"/> whatever its name says. A zero-byte
        /// <c>.jpg</c> is not image content and a zero-byte <c>.mp4</c> is not video content, so
        /// extension is not trustworthy evidence when there is no payload for it to describe.
        /// <para>
        /// This is not only a truthfulness point, it is what keeps deduplication quiet. Every empty
        /// file has the same SHA-256, so without this rule the first empty file seen would fix that
        /// one asset's type and every empty file with a different extension would then be reported
        /// as a classification conflict. With it, empty files simply deduplicate to a single
        /// <see cref="MediaType.Unknown"/> asset, which is what they are.
        /// </para>
        /// <para>
        /// Whether an empty asset should ever be attached to a message is a matching question, and
        /// matching does not exist yet.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="sizeBytes"/> is negative, which no file has.
        /// </exception>
        public static MediaType Classify(string? extension, long sizeBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);

            return sizeBytes == 0
                ? MediaType.Unknown
                : ClassifyByExtension(extension);
        }

        /// <summary>
        /// Whether a directory in <paramref name="canonicalRelativePath"/> is <c>Sent</c>.
        /// </summary>
        /// <param name="canonicalRelativePath">
        /// A canonical relative path as the workspace stores it: <c>/</c>-separated, with no leading
        /// separator and no traversal segments.
        /// </param>
        /// <remarks>
        /// Whole directory segments are compared, never substrings, so <c>Sentimental</c> and
        /// <c>Unsent</c> are not matches. The comparison ignores case because Windows directory
        /// names do.
        /// <para>
        /// The file's own name is excluded from the comparison. Direction is a fact about which
        /// folder WhatsApp filed something in, and a file that happens to be named <c>Sent</c> is
        /// not evidence that it was.
        /// </para>
        /// <para>
        /// A fact about one path and nothing more. Whether it may be read as direction is
        /// <see cref="DeriveIsSent"/>'s question, because that depends on the source as a whole.
        /// </para>
        /// </remarks>
        public static bool HasSentDirectorySegment(string canonicalRelativePath)
        {
            ArgumentNullException.ThrowIfNull(canonicalRelativePath);

            var lastSeparator = canonicalRelativePath.LastIndexOf(
                MediaSourcePath.CanonicalDirectorySeparator);

            if (lastSeparator < 0)
            {
                // A file directly in the root has no directory to read direction from.
                return false;
            }

            foreach (var directory in canonicalRelativePath[..lastSeparator]
                         .Split(MediaSourcePath.CanonicalDirectorySeparator))
            {
                if (directory.Equals(SentDirectorySegment, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a source of <paramref name="sourceType"/> has folder conventions this build can
        /// read direction from at all.
        /// </summary>
        /// <remarks>
        /// A necessary condition, not a sufficient one: a source of a known layout still has to
        /// contain a <c>Sent</c> folder before anything about direction can be concluded from it.
        /// </remarks>
        public static bool ReadsDirectionFromPaths(string sourceType)
        {
            ArgumentNullException.ThrowIfNull(sourceType);

            return IsWhatsAppMediaDirectory(sourceType);
        }

        /// <summary>
        /// What <paramref name="canonicalRelativePath"/> says about direction, given the source it
        /// belongs to.
        /// </summary>
        /// <param name="sourceHasSentDirectory">
        /// Whether any file in the same source lies under a <c>Sent</c> directory.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when this file lies under a <c>Sent</c> directory,
        /// <see langword="false"/> when the source demonstrably separates sent media and this file
        /// is not among it, and <see langword="null"/> when the source provides no direction
        /// evidence at all.
        /// </returns>
        /// <remarks>
        /// Direction is judged per source, not per file, and that is the whole point of the
        /// parameter. In a tree that really does file outgoing media under <c>Sent</c>, a file
        /// outside those folders is genuinely evidence of "not sent". In a tree with no <c>Sent</c>
        /// folder anywhere — a recovered or partially copied source, for instance — the same file
        /// is evidence of nothing, and answering <see langword="false"/> would turn a missing
        /// folder into a claim about every file beneath the root.
        /// <para>
        /// Deliberately conservative, because the two mistakes are not equally recoverable. A null
        /// says "unknown" and leaves later matching free to find out; a false says "not sent" and is
        /// indistinguishable afterwards from evidence the source actually gave.
        /// </para>
        /// <para>
        /// Nothing else is consulted. Direction is never inferred from a file name, a date, a
        /// counter or a neighbouring file — only from the folder the source itself filed it in.
        /// </para>
        /// </remarks>
        public static bool? DeriveIsSent(
            string sourceType, string canonicalRelativePath, bool sourceHasSentDirectory)
        {
            ArgumentNullException.ThrowIfNull(sourceType);
            ArgumentNullException.ThrowIfNull(canonicalRelativePath);

            if (!ReadsDirectionFromPaths(sourceType) || !sourceHasSentDirectory)
            {
                return null;
            }

            return HasSentDirectorySegment(canonicalRelativePath);
        }

        /// <summary>
        /// The calendar date <paramref name="fileName"/> encodes, for a source of
        /// <paramref name="sourceType"/>, or null when it encodes none.
        /// </summary>
        /// <remarks>
        /// Only the structural pattern <c>-YYYYMMDD-WA</c> is recognised, and only when the eight
        /// digits form a real calendar date. <c>IMG-20260724-WA0004.jpg</c> yields 24 July 2026;
        /// <c>IMG-20261332-WA0004.jpg</c> yields null, because there is no such date.
        /// <para>
        /// The surrounding <c>-</c> and <c>-WA</c> are required rather than decorative. Without
        /// them any eight consecutive digits in an unrelated file name — a phone number, a counter,
        /// a camera serial — would be read as a date, and a date invented that way is indistinguishable
        /// afterwards from one the export really recorded.
        /// </para>
        /// <para>
        /// This is a naming convention, not a timestamp. It is never the file's creation time,
        /// modification time, EXIF capture time or any message's time, and nothing in this phase
        /// uses it to attach media to a message.
        /// </para>
        /// </remarks>
        public static DateOnly? DeriveFileDate(string sourceType, string fileName)
        {
            ArgumentNullException.ThrowIfNull(sourceType);
            ArgumentNullException.ThrowIfNull(fileName);

            if (!IsWhatsAppMediaDirectory(sourceType))
            {
                return null;
            }

            // Written as an explicit scan rather than a regular expression: the pattern is fixed
            // and short, and this way the date validation and the structural requirement are
            // visibly the same decision.
            const string trailingMarker = "-WA";
            const int digitCount = 8;

            var span = fileName.AsSpan();

            for (var start = 1; start + digitCount + trailingMarker.Length <= span.Length; start++)
            {
                if (span[start - 1] != '-')
                {
                    continue;
                }

                var digits = span.Slice(start, digitCount);

                if (!IsAllAsciiDigits(digits))
                {
                    continue;
                }

                if (!span.Slice(start + digitCount, trailingMarker.Length)
                        .Equals(trailingMarker, StringComparison.Ordinal))
                {
                    continue;
                }

                if (DateOnly.TryParseExact(
                        digits,
                        "yyyyMMdd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var fileDate))
                {
                    return fileDate;
                }
            }

            return null;
        }

        /// <summary>
        /// Formats a naming-derived date as the workspace stores it.
        /// </summary>
        internal static string FormatFileDate(DateOnly fileDate) =>
            fileDate.ToString(FileDateFormat, CultureInfo.InvariantCulture);

        /// <summary>
        /// The label a diagnostic histogram uses for <paramref name="extension"/>.
        /// </summary>
        internal static string ExtensionLabel(string? extension) => extension ?? NoExtensionLabel;

        private static bool IsWhatsAppMediaDirectory(string sourceType) =>
            string.Equals(
                sourceType, MediaSourceTypes.WhatsAppMediaDirectory, StringComparison.Ordinal);

        /// <remarks>
        /// <see cref="char.IsDigit(char)"/> accepts non-ASCII digits from other scripts, which
        /// would then fail to parse and make the scan's intent unclear. Only ASCII digits can
        /// appear in this convention.
        /// </remarks>
        private static bool IsAllAsciiDigits(ReadOnlySpan<char> span)
        {
            foreach (var character in span)
            {
                if (!char.IsAsciiDigit(character))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
