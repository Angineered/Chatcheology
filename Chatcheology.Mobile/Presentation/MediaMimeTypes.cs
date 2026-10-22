namespace Chatcheology.Mobile.Presentation
{
    /// <summary>
    /// The MIME type a restored media file is handed to another app with, from its file extension.
    /// </summary>
    /// <remarks>
    /// <para>The archive's <c>MediaType</c> is a category (Image, Video, Audio, Document, Unknown), not a
    /// MIME type, so the type comes from the extension the media file is stored under. The table covers
    /// every extension the desktop's classification knows (<c>MediaClassification</c> in Data), so any
    /// file the desktop classified gets a specific type.</para>
    /// <para>Anything else is <see cref="Fallback"/>, which lets Android offer the apps that open any
    /// file rather than guessing.</para>
    /// </remarks>
    public static class MediaMimeTypes
    {
        /// <summary>The type of a file whose extension is not in the table.</summary>
        public const string Fallback = "application/octet-stream";

        private static readonly Dictionary<string, string> ByExtension = new(StringComparer.Ordinal)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".bmp"] = "image/bmp",
            [".heic"] = "image/heic",
            [".heif"] = "image/heif",
            [".svg"] = "image/svg+xml",
            [".eps"] = "application/postscript",

            [".mp4"] = "video/mp4",
            [".3gp"] = "video/3gpp",
            [".mkv"] = "video/x-matroska",
            [".mov"] = "video/quicktime",
            [".webm"] = "video/webm",

            // WhatsApp voice notes are Opus in an Ogg container.
            [".opus"] = "audio/ogg",
            [".ogg"] = "audio/ogg",
            [".mp3"] = "audio/mpeg",
            [".m4a"] = "audio/mp4",
            [".aac"] = "audio/aac",
            [".wav"] = "audio/x-wav",
            [".amr"] = "audio/amr",
            [".flac"] = "audio/flac",

            [".pdf"] = "application/pdf",
            [".doc"] = "application/msword",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xls"] = "application/vnd.ms-excel",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".ppt"] = "application/vnd.ms-powerpoint",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".txt"] = "text/plain",
            [".csv"] = "text/csv",
            [".rtf"] = "application/rtf",
            [".zip"] = "application/zip",
            [".vcf"] = "text/x-vcard",
            [".7z"] = "application/x-7z-compressed",
            [".rar"] = "application/vnd.rar",
            [".json"] = "application/json",
            [".md"] = "text/markdown",
            [".sql"] = "application/sql",
        };

        /// <summary>Every extension in the table, lower-case with its dot.</summary>
        public static IReadOnlyCollection<string> KnownExtensions => ByExtension.Keys;

        /// <summary>The MIME type for <paramref name="fileName"/>'s extension, or <see cref="Fallback"/>.</summary>
        /// <remarks>The extension is compared lower-cased with the invariant culture.</remarks>
        public static string FromFileName(string? fileName)
        {
            var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();

            return ByExtension.TryGetValue(extension, out var type) ? type : Fallback;
        }
    }
}
