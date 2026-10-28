using System.Security.Cryptography;

namespace Chatcheology.Data.Memories.Personal
{
    /// <summary>An image file the operator chose, read once and checked: its bytes, hash and type.</summary>
    public sealed class PersonalImageFile
    {
        private PersonalImageFile(byte[] bytes, string sha256, string extension)
        {
            Bytes = bytes;
            Sha256 = sha256;
            Extension = extension;
        }

        /// <summary>The bytes as read.</summary>
        internal byte[] Bytes { get; }

        /// <summary>Lower-case SHA-256 of the bytes.</summary>
        public string Sha256 { get; }

        /// <summary>png, jpg, gif or webp, from the content (never from the file name).</summary>
        public string Extension { get; }

        /// <summary>The size in bytes.</summary>
        public long SizeBytes => Bytes.LongLength;

        /// <summary>
        /// Reads <paramref name="path"/> for reading only (shared, so nothing else is blocked) and checks it:
        /// present, not empty, at most <paramref name="maxBytes"/>, and a PNG, JPEG, GIF or WebP by its first
        /// bytes. The file is never written, moved or renamed.
        /// </summary>
        /// <param name="error">Why it was refused, or null.</param>
        /// <returns>The read file, or null when refused.</returns>
        public static PersonalImageFile? Read(string path, long maxBytes, out string? error)
        {
            if (maxBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes), "The size cap must be positive.");
            }

            byte[] bytes;

            try
            {
                var info = new FileInfo(path);

                if (!info.Exists)
                {
                    error = "The chosen image file is not there.";
                    return null;
                }

                if (info.Length > maxBytes)
                {
                    error = TooLarge(maxBytes);
                    return null;
                }

                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;

                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    buffer.Write(chunk, 0, read);

                    if (buffer.Length > maxBytes)
                    {
                        error = TooLarge(maxBytes);
                        return null;
                    }
                }

                bytes = buffer.ToArray();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error = "The chosen image file could not be read.";
                return null;
            }

            if (bytes.Length == 0)
            {
                error = "The chosen image file is empty.";
                return null;
            }

            if (MemoryImageFetcher.DetectExtension(bytes) is not { } extension)
            {
                error = "The chosen file is not a PNG, JPEG, GIF or WebP image.";
                return null;
            }

            error = null;

            return new PersonalImageFile(bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), extension);
        }

        private static string TooLarge(long maxBytes) =>
            "The chosen image is larger than " + (maxBytes / (1024 * 1024)).ToString(System.Globalization.CultureInfo.CurrentCulture) +
            " MB.";
    }
}
