using System.Text;
using Chatcheology.Core.Importing;
using Chatcheology.Core.Models;

namespace Chatcheology.Data.OlderExports
{
    /// <summary>
    /// One older WhatsApp export, read from its folder: the parsed chat text, the text file's name
    /// and SHA-256, and the names of the files beside it.
    /// </summary>
    /// <remarks>
    /// An export is a folder holding exactly one <c>.txt</c> file (the chat) plus whatever media
    /// files came with it. The folder is the unit: the chat file is found in it, never named by the
    /// caller, and media is looked for in the same folder. Nothing is renamed, moved or written.
    /// <para>
    /// The chat file is read once into memory, and both the SHA-256 and the parse come from those
    /// same bytes, so the recorded hash is always the hash of exactly what was parsed. It must be
    /// valid UTF-8 (a byte order mark is allowed); invalid bytes stop the read rather than being
    /// replaced.
    /// </para>
    /// <para>
    /// Exception messages carry counts and reasons only — no path, file name or content.
    /// </para>
    /// </remarks>
    public sealed class OlderExportSource
    {
        /// <summary>The chat file's extension, compared without regard to case.</summary>
        public const string ChatFileExtension = ".txt";

        private OlderExportSource()
        {
        }

        /// <summary>The export folder, fully qualified.</summary>
        public required string FolderPath { get; init; }

        /// <summary>The chat file's name only, as it is on disk.</summary>
        public required string ChatFileName { get; init; }

        /// <summary>The chat file's SHA-256, upper-case hexadecimal.</summary>
        public required string SHA256 { get; init; }

        /// <summary>The detected header layout, or null when the file holds no message.</summary>
        public required WhatsAppHeaderShape? Shape { get; init; }

        /// <summary>The parsed messages in source order.</summary>
        public required IReadOnlyList<ParsedMessage> Messages { get; init; }

        /// <summary>
        /// The names of the other files directly in the folder (the chat file excluded), compared
        /// ordinally, so a lookup is exact and case-sensitive whatever the file system does.
        /// </summary>
        public required IReadOnlySet<string> MediaFileNames { get; init; }

        /// <summary>
        /// Reads the export in <paramref name="folderPath"/>.
        /// </summary>
        /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
        /// <exception cref="InvalidOperationException">
        /// The folder does not hold exactly one <c>.txt</c> file. The message gives the count.
        /// </exception>
        /// <exception cref="FormatException">
        /// The chat file is not valid UTF-8, or the parser rejected it (with a line number).
        /// </exception>
        public static OlderExportSource Load(string folderPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

            var fullFolderPath = Path.GetFullPath(folderPath);

            if (!Directory.Exists(fullFolderPath))
            {
                throw new DirectoryNotFoundException(
                    "The export folder does not exist. Nothing has been read.");
            }

            var fileNames = ListFileNames(fullFolderPath);

            var chatFileNames = fileNames
                .Where(name => string.Equals(
                    Path.GetExtension(name), ChatFileExtension, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (chatFileNames.Count != 1)
            {
                throw new InvalidOperationException(
                    $"The export folder holds {chatFileNames.Count} .txt files; exactly one (the " +
                    $"chat) is required. Nothing has been read or renamed.");
            }

            var chatFileName = chatFileNames[0];
            var bytes = WithoutPathInErrors(
                () => File.ReadAllBytes(Path.Combine(fullFolderPath, chatFileName)));
            var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

            ParsedChatExport parsed;

            try
            {
                using var reader = new StreamReader(
                    new MemoryStream(bytes, writable: false),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                    detectEncodingFromByteOrderMarks: true);

                parsed = new WhatsAppAndroidChatParser().ParseExport(reader);
            }
            catch (DecoderFallbackException)
            {
                // The framework's message quotes the offending bytes, so it is not passed on.
                throw new FormatException(
                    "The chat file is not valid UTF-8, so it is not read. Nothing has been imported.");
            }

            var mediaFileNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var name in fileNames)
            {
                if (!string.Equals(name, chatFileName, StringComparison.Ordinal))
                {
                    mediaFileNames.Add(name);
                }
            }

            return new OlderExportSource
            {
                FolderPath = fullFolderPath,
                ChatFileName = chatFileName,
                SHA256 = sha256,
                Shape = parsed.Shape,
                Messages = parsed.Messages,
                MediaFileNames = mediaFileNames,
            };
        }

        /// <summary>
        /// Runs a file-system read, replacing an I/O failure with one whose message carries no
        /// path (the framework's own messages quote it).
        /// </summary>
        private static T WithoutPathInErrors<T>(Func<T> read)
        {
            try
            {
                return read();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"The export folder could not be read ({exception.GetType().Name}). Nothing has " +
                    $"been imported.");
            }
        }

        /// <summary>
        /// The names of the files directly in <paramref name="folderPath"/>, as the file system
        /// spells them. Subfolders are not searched.
        /// </summary>
        internal static List<string> ListFileNames(string folderPath) =>
            WithoutPathInErrors(() => Directory.EnumerateFiles(folderPath)
                .Select(path => Path.GetFileName(path))
                .ToList());
    }
}
