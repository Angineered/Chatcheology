using System.Security.Cryptography;
using Chatcheology.Mobile.Presentation;

namespace Chatcheology.Mobile.OwnMemories
{
    /// <summary>
    /// The platform's part of storing a photo without its location (CH-54, D-37): the steps that need Android, such
    /// as removing the location of a PNG or WebP with <c>ExifInterface</c>, or turning a HEIC photo into a JPEG.
    /// Implemented by the Android app (step 2); <see cref="OwnPhotoIntake"/> checks whatever it writes.
    /// </summary>
    public interface IPhotoNormaliser
    {
        /// <summary>
        /// Writes a copy of the photo at <paramref name="sourcePath"/> to <paramref name="targetPath"/> with no
        /// location. Never logs either path.
        /// </summary>
        /// <param name="sourcePath">The photo as it came in.</param>
        /// <param name="format">Its format, read from its first bytes (<see cref="PhotoFormat.Other"/> for HEIC and anything unknown).</param>
        /// <param name="targetPath">Where to write the copy; not there yet.</param>
        /// <returns>The format written, or <see langword="null"/> when it cannot (the photo is then refused).</returns>
        PhotoFormat? WriteWithoutLocation(string sourcePath, PhotoFormat format, string targetPath);
    }

    /// <summary>What happened to a photo handed to <see cref="OwnPhotoIntake"/>.</summary>
    public enum OwnPhotoStatus
    {
        /// <summary>Stored as a new file.</summary>
        Stored,

        /// <summary>A file with exactly these bytes was already stored: that one is used.</summary>
        AlreadyStored,

        /// <summary>Refused: larger than the limit.</summary>
        TooLarge,

        /// <summary>Refused: not a photo this phone can store (an unknown or damaged format, or one the platform could not convert).</summary>
        NotSupported,

        /// <summary>Refused: its location could not be removed for certain. Missing is better than wrong.</summary>
        HasLocation,
    }

    /// <summary>The outcome of adding one photo.</summary>
    /// <param name="Status">What happened.</param>
    /// <param name="FileName">The stored file's name (<c>&lt;sha256&gt;.&lt;ext&gt;</c>) when stored or already stored; otherwise null.</param>
    public sealed record OwnPhotoResult(OwnPhotoStatus Status, string? FileName)
    {
        /// <summary>Whether the photo can be used: <see cref="FileName"/> names a stored file.</summary>
        public bool IsStored => FileName is not null;
    }

    /// <summary>
    /// Puts a photo into the own photos folder (CH-54): streamed in from the picker (no cache copy), at most
    /// <see cref="MaxPhotoBytes"/> (D-29, checked while streaming), stored without its location (D-37, option A)
    /// as <c>&lt;sha256&gt;.&lt;ext&gt;</c> of the stored bytes, so identical photos are one file.
    /// </summary>
    /// <remarks>
    /// <para>Per format, read from the first bytes (<see cref="PhotoShare.Sniff"/>):</para>
    /// <list type="bullet">
    /// <item>JPEG: rebuilt from the main image only, with no Exif block but its orientation
    /// (<see cref="PhotoShare.WriteMainJpegWithoutLocation"/>). No platform step.</item>
    /// <item>GIF and BMP: kept as they are; neither has EXIF.</item>
    /// <item>PNG and WebP: kept as they are when they carry no EXIF chunk, no compressed text and no XMP location
    /// text. Otherwise the platform removes the location (<see cref="IPhotoNormaliser"/>).</item>
    /// <item>Anything else (HEIC among them): the platform converts it (to JPEG, say). Without a platform step it
    /// is refused.</item>
    /// </list>
    /// <para>Whatever the path, the finished file is checked again with the CH-59 checks
    /// (<see cref="PhotoShare.MayStillCarryLocation"/>); a JPEG from the platform is rebuilt again first. Any doubt
    /// refuses the photo. A refused or failed photo leaves no file behind: every partial file is deleted.</para>
    /// <para>Partial files are named <c>.incoming-&lt;guid&gt;.tmp</c> in the photos folder, never in the cache.</para>
    /// </remarks>
    public sealed class OwnPhotoIntake
    {
        /// <summary>The largest photo kept (D-29): 20 MB.</summary>
        public const long MaxPhotoBytes = 20 * 1024 * 1024;

        private const string IncomingPrefix = ".incoming-";
        private const string IncomingSuffix = ".tmp";

        private readonly string _folder;
        private readonly IPhotoNormaliser? _normaliser;
        private readonly long _maxBytes;

        /// <summary>Stores into <paramref name="photosFolder"/>.</summary>
        /// <param name="photosFolder">The own photos folder (<see cref="OwnMemoryStore.PhotosFolder"/>).</param>
        /// <param name="normaliser">The platform's step, or null on a host with none.</param>
        /// <param name="maxPhotoBytes">The size limit (tests may lower it).</param>
        public OwnPhotoIntake(string photosFolder, IPhotoNormaliser? normaliser = null, long maxPhotoBytes = MaxPhotoBytes)
        {
            ArgumentException.ThrowIfNullOrEmpty(photosFolder);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxPhotoBytes, 1);

            _folder = photosFolder;
            _normaliser = normaliser;
            _maxBytes = maxPhotoBytes;
        }

        /// <summary>Whether <paramref name="name"/> is a partial file's name.</summary>
        public static bool IsIncomingFileName(string? name) =>
            name is not null
            && name.StartsWith(IncomingPrefix, StringComparison.Ordinal)
            && name.EndsWith(IncomingSuffix, StringComparison.Ordinal)
            && Guid.TryParseExact(name[IncomingPrefix.Length..^IncomingSuffix.Length], "N", out _);

        /// <summary>The stored extension of a format; null for <see cref="PhotoFormat.Other"/>.</summary>
        public static string? ExtensionFor(PhotoFormat format) => format switch
        {
            PhotoFormat.Jpeg => "jpg",
            PhotoFormat.Png => "png",
            PhotoFormat.WebP => "webp",
            PhotoFormat.Gif => "gif",
            PhotoFormat.Bmp => "bmp",
            _ => null,
        };

        /// <summary>Reads <paramref name="source"/> to its end and stores it, or refuses it.</summary>
        /// <exception cref="IOException">Reading or writing failed; no file is left behind.</exception>
        /// <exception cref="OperationCanceledException">Cancelled; no file is left behind.</exception>
        public async Task<OwnPhotoResult> AddAsync(Stream source, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);

            Directory.CreateDirectory(_folder);
            var partials = new List<string>();

            try
            {
                var incoming = NewPartial(partials);
                byte[] incomingHash;

                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    await using (var target = new FileStream(incoming, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        var buffer = new byte[81920];
                        long total = 0;

                        while (true)
                        {
                            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

                            if (read <= 0)
                            {
                                break;
                            }

                            total += read;

                            if (total > _maxBytes)
                            {
                                return new OwnPhotoResult(OwnPhotoStatus.TooLarge, null);
                            }

                            hash.AppendData(buffer, 0, read);
                            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        }
                    }

                    incomingHash = hash.GetHashAndReset();
                }

                cancellationToken.ThrowIfCancellationRequested();
                return Finish(incoming, incomingHash, partials);
            }
            finally
            {
                foreach (var partial in partials)
                {
                    TryDelete(partial);
                }
            }
        }

        /// <summary>Turns the incoming file into the stored one, or refuses it. Every file it makes is in <paramref name="partials"/>.</summary>
        private OwnPhotoResult Finish(string incoming, byte[] incomingHash, List<string> partials)
        {
            var format = SniffFile(incoming);
            string stored;
            byte[] storedHash;
            PhotoShareMethod method;

            switch (format)
            {
                case PhotoFormat.Jpeg:
                    if (RebuildJpeg(incoming, partials) is not { } rebuilt)
                    {
                        return new OwnPhotoResult(OwnPhotoStatus.NotSupported, null);
                    }

                    (stored, storedHash) = rebuilt;
                    method = PhotoShareMethod.RemoveLocation;
                    break;

                case PhotoFormat.Gif or PhotoFormat.Bmp:
                    (stored, storedHash, method) = (incoming, incomingHash, PhotoShareMethod.PlainCopy);
                    break;

                case PhotoFormat.Png or PhotoFormat.WebP when !MayCarryLocation(incoming, format, PhotoShareMethod.PlainCopy):
                    (stored, storedHash, method) = (incoming, incomingHash, PhotoShareMethod.PlainCopy);
                    break;

                default:
                    if (_normaliser is null)
                    {
                        return new OwnPhotoResult(format == PhotoFormat.Other ? OwnPhotoStatus.NotSupported : OwnPhotoStatus.HasLocation, null);
                    }

                    var normalised = NewPartial(partials);

                    if (_normaliser.WriteWithoutLocation(incoming, format, normalised) is not { } written || !File.Exists(normalised))
                    {
                        return new OwnPhotoResult(format == PhotoFormat.Other ? OwnPhotoStatus.NotSupported : OwnPhotoStatus.HasLocation, null);
                    }

                    if (new FileInfo(normalised).Length > _maxBytes)
                    {
                        return new OwnPhotoResult(OwnPhotoStatus.TooLarge, null);
                    }

                    // Trust the bytes, not the platform's word for them.
                    format = SniffFile(normalised);

                    if (format != written || ExtensionFor(format) is null)
                    {
                        return new OwnPhotoResult(OwnPhotoStatus.NotSupported, null);
                    }

                    if (format == PhotoFormat.Jpeg)
                    {
                        if (RebuildJpeg(normalised, partials) is not { } again)
                        {
                            return new OwnPhotoResult(OwnPhotoStatus.NotSupported, null);
                        }

                        (stored, storedHash) = again;
                        method = PhotoShareMethod.RemoveLocation;
                    }
                    else
                    {
                        stored = normalised;
                        storedHash = HashFile(normalised);
                        method = format is PhotoFormat.Gif or PhotoFormat.Bmp ? PhotoShareMethod.PlainCopy : PhotoShareMethod.RemoveLocation;
                    }

                    break;
            }

            // The CH-59 checks on the very bytes that are kept: any doubt refuses.
            if (MayCarryLocation(stored, format, method))
            {
                return new OwnPhotoResult(OwnPhotoStatus.HasLocation, null);
            }

            var name = Convert.ToHexStringLower(storedHash) + "." + ExtensionFor(format);
            var target = Path.Combine(_folder, name);

            if (File.Exists(target))
            {
                return new OwnPhotoResult(OwnPhotoStatus.AlreadyStored, name);
            }

            try
            {
                File.Move(stored, target, overwrite: false);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Stored by another add in the meantime: the same bytes.
                return new OwnPhotoResult(OwnPhotoStatus.AlreadyStored, name);
            }

            partials.Remove(stored);
            return new OwnPhotoResult(OwnPhotoStatus.Stored, name);
        }

        /// <summary>Rebuilds a JPEG without its location into a new partial file; null when it is not a complete JPEG or too large.</summary>
        private (string Path, byte[] Hash)? RebuildJpeg(string path, List<string> partials)
        {
            var source = File.ReadAllBytes(path);
            using var rebuilt = new MemoryStream(source.Length);

            if (!PhotoShare.WriteMainJpegWithoutLocation(source, rebuilt) || rebuilt.Length > _maxBytes)
            {
                return null;
            }

            var output = NewPartial(partials);
            var bytes = rebuilt.ToArray();

            using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
            }

            return (output, SHA256.HashData(bytes));
        }

        private string NewPartial(List<string> partials)
        {
            var path = Path.Combine(_folder, IncomingPrefix + Guid.NewGuid().ToString("N") + IncomingSuffix);
            partials.Add(path);
            return path;
        }

        private static PhotoFormat SniffFile(string path)
        {
            Span<byte> header = stackalloc byte[PhotoShare.HeaderLength];
            using var file = File.OpenRead(path);
            var read = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return PhotoShare.Sniff(header[..read]);
        }

        private static bool MayCarryLocation(string path, PhotoFormat format, PhotoShareMethod method)
        {
            using var file = File.OpenRead(path);
            return PhotoShare.MayStillCarryLocation(file, format, method);
        }

        private static byte[] HashFile(string path)
        {
            using var file = File.OpenRead(path);
            return SHA256.HashData(file);
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left for Delete all, which removes partial files too.
            }
        }
    }
}
