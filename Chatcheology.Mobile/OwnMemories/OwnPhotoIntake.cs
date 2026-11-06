using System.Collections.Concurrent;
using System.Security.Cryptography;
using Chatcheology.Mobile.Presentation;

namespace Chatcheology.Mobile.OwnMemories
{
    /// <summary>
    /// The platform's part of storing a photo (CH-54, D-37): what pure .NET cannot do, such as turning a HEIC photo
    /// into a JPEG, or reading a photo whose structure the pure rebuild refuses. Implemented by the Android app (step 2).
    /// Whatever it writes is rebuilt by <see cref="PhotoRebuild"/> and checked again by <see cref="OwnPhotoIntake"/>.
    /// </summary>
    public interface IPhotoNormaliser
    {
        /// <summary>
        /// Writes a copy of the photo at <paramref name="sourcePath"/> to <paramref name="targetPath"/> as JPEG, PNG,
        /// WebP, GIF or BMP, with no location. Never logs either path.
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

        /// <summary>Refused: the rebuilt copy still looked as if it could hold a location. Missing is better than wrong.</summary>
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
    /// <para>Every photo is rebuilt from an allow-list of its picture's parts (<see cref="PhotoRebuild"/>); nothing is
    /// ever stored as it came in. A photo the rebuild refuses (HEIC, an unknown format, a damaged file) goes to the
    /// platform (<see cref="IPhotoNormaliser"/>); its output is rebuilt the same way. Without a platform step it is
    /// refused.</para>
    /// <para>The rebuilt bytes are then checked again with the CH-59 checks
    /// (<see cref="PhotoShare.MayStillCarryLocation"/>) as a second line; any doubt refuses the photo. A refused or
    /// failed photo leaves no file behind: every partial file is deleted.</para>
    /// <para>Partial files are named <c>.incoming-&lt;guid&gt;.tmp</c> in the photos folder, never in the cache. Ones left
    /// by a killed process (a full copy of the original) are deleted at first use, when older than
    /// <see cref="PartialFileAge"/>.</para>
    /// </remarks>
    public sealed class OwnPhotoIntake
    {
        /// <summary>The largest photo kept (D-29): 20 MB.</summary>
        public const long MaxPhotoBytes = 20 * 1024 * 1024;

        /// <summary>How old a partial file must be before a sweep deletes it.</summary>
        public static readonly TimeSpan PartialFileAge = TimeSpan.FromMinutes(10);

        private const string IncomingPrefix = ".incoming-";
        private const string IncomingSuffix = ".tmp";

        private static readonly ConcurrentDictionary<string, bool> Swept = new(StringComparer.Ordinal);

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

        /// <summary>
        /// Deletes the partial files in <paramref name="photosFolder"/> last written before <paramref name="nowUtc"/>
        /// minus <see cref="PartialFileAge"/>. A file that cannot be deleted is left.
        /// </summary>
        /// <returns>The number deleted.</returns>
        public static int SweepPartialFiles(string photosFolder, DateTime nowUtc)
        {
            ArgumentException.ThrowIfNullOrEmpty(photosFolder);

            if (!Directory.Exists(photosFolder))
            {
                return 0;
            }

            var deleted = 0;

            foreach (var path in Directory.EnumerateFiles(photosFolder, IncomingPrefix + "*" + IncomingSuffix))
            {
                if (IsIncomingFileName(Path.GetFileName(path))
                    && File.GetLastWriteTimeUtc(path) < nowUtc - PartialFileAge
                    && TryDelete(path))
                {
                    deleted++;
                }
            }

            return deleted;
        }

        /// <summary>Sweeps old partial files the first time a folder is used in this process.</summary>
        internal static void SweepOnce(string photosFolder)
        {
            if (Swept.TryAdd(Path.GetFullPath(photosFolder), true))
            {
                SweepPartialFiles(photosFolder, DateTime.UtcNow);
            }
        }

        /// <summary>Reads <paramref name="source"/> to its end and stores it, or refuses it.</summary>
        /// <exception cref="IOException">Reading or writing failed; no file is left behind.</exception>
        /// <exception cref="OperationCanceledException">Cancelled; no file is left behind.</exception>
        public async Task<OwnPhotoResult> AddAsync(Stream source, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);

            Directory.CreateDirectory(_folder);
            SweepOnce(_folder);
            var partials = new List<string>();

            try
            {
                var incoming = NewPartial(partials);

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

                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                return Finish(incoming, partials);
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
        private OwnPhotoResult Finish(string incoming, List<string> partials)
        {
            var source = File.ReadAllBytes(incoming);
            var format = PhotoShare.Sniff(source.AsSpan(0, Math.Min(source.Length, PhotoShare.HeaderLength)));
            var rebuilt = PhotoRebuild.Rebuild(source, format);

            if (rebuilt is null)
            {
                // Not something the pure rebuild can read: the platform's turn, if there is one.
                if (_normaliser is null)
                {
                    return new OwnPhotoResult(OwnPhotoStatus.NotSupported, null);
                }

                var normalised = NewPartial(partials);

                if (_normaliser.WriteWithoutLocation(incoming, format, normalised) is not { } written || !File.Exists(normalised))
                {
                    return new OwnPhotoResult(OwnPhotoStatus.NotSupported, null);
                }

                if (new FileInfo(normalised).Length > _maxBytes)
                {
                    return new OwnPhotoResult(OwnPhotoStatus.TooLarge, null);
                }

                // Trust the bytes, not the platform's word for them, and rebuild them the same way.
                var output = File.ReadAllBytes(normalised);
                format = PhotoShare.Sniff(output.AsSpan(0, Math.Min(output.Length, PhotoShare.HeaderLength)));

                if (format != written || (rebuilt = PhotoRebuild.Rebuild(output, format)) is null)
                {
                    return new OwnPhotoResult(OwnPhotoStatus.NotSupported, null);
                }
            }

            if (rebuilt.Length > _maxBytes)
            {
                return new OwnPhotoResult(OwnPhotoStatus.TooLarge, null);
            }

            // The CH-59 checks on the very bytes that are kept, as a second line: any doubt refuses. Nothing was
            // removed by ExifInterface, so the strict (plain copy) rules apply.
            var method = format == PhotoFormat.Jpeg ? PhotoShareMethod.RemoveLocation : PhotoShareMethod.PlainCopy;

            using (var check = new MemoryStream(rebuilt, writable: false))
            {
                if (PhotoShare.MayStillCarryLocation(check, format, method))
                {
                    return new OwnPhotoResult(OwnPhotoStatus.HasLocation, null);
                }
            }

            var name = Convert.ToHexStringLower(SHA256.HashData(rebuilt)) + "." + ExtensionFor(format);
            var target = Path.Combine(_folder, name);

            if (File.Exists(target))
            {
                return AlreadyStored(target, name);
            }

            var stored = NewPartial(partials);

            using (var file = new FileStream(stored, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(rebuilt);
            }

            try
            {
                File.Move(stored, target, overwrite: false);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Stored by another add in the meantime: the same bytes.
                return AlreadyStored(target, name);
            }

            partials.Remove(stored);
            return new OwnPhotoResult(OwnPhotoStatus.Stored, name);
        }

        /// <summary>The file is in use again: its time is renewed so the unused-photo sweep does not take it from an open form.</summary>
        private static OwnPhotoResult AlreadyStored(string path, string name)
        {
            try
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Only the sweep's age; the file is there.
            }

            return new OwnPhotoResult(OwnPhotoStatus.AlreadyStored, name);
        }

        private string NewPartial(List<string> partials)
        {
            var path = Path.Combine(_folder, IncomingPrefix + Guid.NewGuid().ToString("N") + IncomingSuffix);
            partials.Add(path);
            return path;
        }

        private static bool TryDelete(string path)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left for a later sweep or Delete all, which remove partial files too.
                return false;
            }
        }
    }
}
