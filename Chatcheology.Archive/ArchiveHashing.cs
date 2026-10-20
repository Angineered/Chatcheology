using System.Security.Cryptography;

namespace Chatcheology.Archive
{
    /// <summary>
    /// SHA-256 of files, and copying a file while hashing the bytes that pass. Sources are only ever
    /// opened for reading.
    /// </summary>
    public static class ArchiveHashing
    {
        private const int BufferSize = 1 << 20;

        /// <summary>A hash as lower-case hex, the archive's spelling.</summary>
        public static string Hex(byte[] hash)
        {
            ArgumentNullException.ThrowIfNull(hash);

            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>The SHA-256 of a file, lower-case hex.</summary>
        public static string Sha256OfFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);

            return Hex(SHA256.HashData(stream));
        }

        /// <summary>
        /// Copies <paramref name="source"/> to a new file at <paramref name="destination"/>, hashing
        /// the bytes as they pass, so the hash describes exactly what was written. Never overwrites.
        /// </summary>
        /// <returns>The SHA-256 (lower-case hex) and the number of bytes copied.</returns>
        /// <remarks>
        /// A failure to read the source surfaces as the ordinary I/O exception: that copy is unusable
        /// and the caller may try another. A failure to create or write the destination (a full disk,
        /// a blocked name) surfaces as <see cref="ArchiveDestinationWriteException"/>, because no other
        /// copy would fare better and the caller must stop rather than fall through.
        /// </remarks>
        /// <exception cref="ArchiveDestinationWriteException">The destination could not be written.</exception>
        public static (string Sha256, long Bytes) CopyWithHash(string source, string destination)
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
            using var output = Destination(
                () => new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[BufferSize];
            long total = 0;
            int read;

            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                Destination(() => output.Write(buffer, 0, read));
                total += read;
            }

            Destination(() => output.Flush(flushToDisk: true));

            return (Hex(hash.GetHashAndReset()), total);
        }

        private static void Destination(Action write) =>
            Destination(() =>
            {
                write();
                return 0;
            });

        private static T Destination<T>(Func<T> write)
        {
            try
            {
                return write();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ArchiveDestinationWriteException(exception);
            }
        }
    }

    /// <summary>
    /// A copy's destination could not be created or written. The message is fixed; the cause, which
    /// may name a path, is kept as the inner exception only.
    /// </summary>
    public sealed class ArchiveDestinationWriteException(Exception innerException)
        : IOException("A file could not be written into the archive folder.", innerException);

    /// <summary>Path containment, compared the way the file system on Windows compares names.</summary>
    public static class ArchivePaths
    {
        /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or beneath it.</summary>
        public static bool IsWithin(string path, string folder)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

            return full.Equals(root, StringComparison.OrdinalIgnoreCase)
                   || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
