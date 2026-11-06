using System.Buffers.Binary;
using System.Text;

namespace Chatcheology.Mobile.Presentation
{
    /// <summary>A photo's file format, read from its first bytes (never from its name).</summary>
    public enum PhotoFormat
    {
        /// <summary>Anything else: HEIC/HEIF, AVIF, TIFF, raw formats, or not a picture.</summary>
        Other,

        /// <summary>JPEG (<c>FF D8 FF</c>).</summary>
        Jpeg,

        /// <summary>PNG (<c>89 50 4E 47 0D 0A 1A 0A</c>).</summary>
        Png,

        /// <summary>WebP (<c>RIFF....WEBP</c>).</summary>
        WebP,

        /// <summary>GIF (<c>GIF87a</c> / <c>GIF89a</c>).</summary>
        Gif,

        /// <summary>BMP (<c>BM</c>).</summary>
        Bmp,
    }

    /// <summary>How a memory photo is turned into the copy that is shared.</summary>
    public enum PhotoShareMethod
    {
        /// <summary>Not shared: its location cannot be removed on this phone. Missing is better than wrong.</summary>
        Refuse,

        /// <summary>Copied, then every GPS tag is removed from the copy with Android's <c>ExifInterface</c>.</summary>
        RemoveLocation,

        /// <summary>Copied as it is: the format has no EXIF block, so there is no location tag to remove.</summary>
        PlainCopy,
    }

    /// <summary>
    /// CH-59 (decision D-34, option A): a memory photo is shared as a temporary copy with its location removed;
    /// the archive file is only ever read. These are the rules, kept free of Android so they are host-tested.
    /// </summary>
    /// <remarks>
    /// <para>The format comes from the file's first bytes, not its extension, so a HEIC saved as ".jpg" is
    /// still refused. Per format (<see cref="MethodFor"/>):</para>
    /// <list type="bullet">
    /// <item>JPEG: <see cref="PhotoShareMethod.RemoveLocation"/> on every supported Android (8.0 and later).</item>
    /// <item>PNG and WebP: <see cref="PhotoShareMethod.RemoveLocation"/> on Android 11 (API 30) and later, where
    /// <c>ExifInterface</c> can write them. Below that, <see cref="PhotoShareMethod.PlainCopy"/>, but the copy is
    /// refused when it carries an EXIF block (PNG <c>eXIf</c>, WebP <c>EXIF</c>), which could hold a location.</item>
    /// <item>GIF and BMP: <see cref="PhotoShareMethod.PlainCopy"/>; neither format has EXIF.</item>
    /// <item>Everything else (HEIC/HEIF, AVIF, TIFF, DNG, unknown): <see cref="PhotoShareMethod.Refuse"/>. HEIC can
    /// hold a location and <c>ExifInterface</c> cannot write it.</item>
    /// </list>
    /// <para>Whatever the method, the finished copy is checked again before it is shared
    /// (<see cref="MayStillCarryLocation"/>): any XMP location text ("GPSLatitude", "GPSLongitude") refuses it,
    /// as does compressed PNG text (which cannot be read for it). Android also re-reads the copy's GPS tags.</para>
    /// </remarks>
    public static class PhotoShare
    {
        /// <summary>The first API level whose <c>ExifInterface</c> writes PNG and WebP (Android 11).</summary>
        public const int FirstPngWebPWriteApiLevel = 30;

        /// <summary>The folder of the app's cache that holds shared copies; the only cache folder the FileProvider serves.</summary>
        public const string CopiesFolderName = "shared-photos";

        /// <summary>The message when a photo is refused.</summary>
        public const string CannotShareText = "This photo can't be shared without its location.";

        /// <summary>
        /// Every GPS tag <c>ExifInterface</c> knows (the GPS IFD, EXIF 2.32), by its <c>ExifInterface</c> name. All are
        /// cleared on the copy; an empty GPS IFD is then left out when the file is saved.
        /// </summary>
        public static IReadOnlyList<string> LocationTags { get; } =
        [
            "GPSVersionID",
            "GPSLatitudeRef",
            "GPSLatitude",
            "GPSLongitudeRef",
            "GPSLongitude",
            "GPSAltitudeRef",
            "GPSAltitude",
            "GPSTimeStamp",
            "GPSSatellites",
            "GPSStatus",
            "GPSMeasureMode",
            "GPSDOP",
            "GPSSpeedRef",
            "GPSSpeed",
            "GPSTrackRef",
            "GPSTrack",
            "GPSImgDirectionRef",
            "GPSImgDirection",
            "GPSMapDatum",
            "GPSDestLatitudeRef",
            "GPSDestLatitude",
            "GPSDestLongitudeRef",
            "GPSDestLongitude",
            "GPSDestBearingRef",
            "GPSDestBearing",
            "GPSDestDistanceRef",
            "GPSDestDistance",
            "GPSProcessingMethod",
            "GPSAreaInformation",
            "GPSDateStamp",
            "GPSDifferential",
            "GPSHPositioningError",
        ];

        /// <summary>The bytes <see cref="Sniff"/> needs.</summary>
        public const int HeaderLength = 12;

        private static readonly byte[][] LocationText =
        [
            Encoding.ASCII.GetBytes("GPSLatitude"),
            Encoding.ASCII.GetBytes("GPSLongitude"),
        ];

        /// <summary>The format of a file from its first <see cref="HeaderLength"/> bytes (fewer reads as <see cref="PhotoFormat.Other"/> unless they suffice).</summary>
        public static PhotoFormat Sniff(ReadOnlySpan<byte> header)
        {
            if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            {
                return PhotoFormat.Jpeg;
            }

            if (header.Length >= 8 && header[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            {
                return PhotoFormat.Png;
            }

            if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
            {
                return PhotoFormat.WebP;
            }

            if (header.Length >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8)))
            {
                return PhotoFormat.Gif;
            }

            if (header.Length >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M')
            {
                return PhotoFormat.Bmp;
            }

            return PhotoFormat.Other;
        }

        /// <summary>How a photo of <paramref name="format"/> is shared on a phone at <paramref name="apiLevel"/>.</summary>
        public static PhotoShareMethod MethodFor(PhotoFormat format, int apiLevel) => format switch
        {
            PhotoFormat.Jpeg => PhotoShareMethod.RemoveLocation,
            PhotoFormat.Png or PhotoFormat.WebP => apiLevel >= FirstPngWebPWriteApiLevel ? PhotoShareMethod.RemoveLocation : PhotoShareMethod.PlainCopy,
            PhotoFormat.Gif or PhotoFormat.Bmp => PhotoShareMethod.PlainCopy,
            _ => PhotoShareMethod.Refuse,
        };

        /// <summary>
        /// True when <paramref name="fullPath"/> is a file inside <paramref name="folder"/> (after resolving
        /// <c>..</c>); the folder itself and anything outside it are refused. Ordinal: the phone's file system is
        /// case-sensitive.
        /// </summary>
        public static bool IsInside(string? fullPath, string? folder)
        {
            if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(folder) || !Path.IsPathFullyQualified(fullPath) || !Path.IsPathFullyQualified(folder))
            {
                return false;
            }

            var full = Path.GetFullPath(fullPath);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;

            return full.Length > root.Length && full.StartsWith(root, StringComparison.Ordinal);
        }

        /// <summary>
        /// The shared copy's file name: "photo-" and <paramref name="unique"/>, with the source's extension in lower
        /// case (so the MIME type from <see cref="MediaMimeTypes"/> matches). Never the archive's own file name.
        /// </summary>
        public static string CopyFileName(string sourcePath, string unique) =>
            "photo-" + unique + Path.GetExtension(sourcePath).ToLowerInvariant();

        /// <summary>
        /// Checks the finished copy before it is shared: true (refuse) when it may still hold a location that the
        /// method did not remove. Reads <paramref name="copy"/> from its start to its end.
        /// </summary>
        /// <param name="copy">The copy, readable and seekable.</param>
        /// <param name="format">Its sniffed format.</param>
        /// <param name="method">How it was made.</param>
        public static bool MayStillCarryLocation(Stream copy, PhotoFormat format, PhotoShareMethod method)
        {
            ArgumentNullException.ThrowIfNull(copy);

            if (method == PhotoShareMethod.Refuse)
            {
                return true;
            }

            copy.Position = 0;

            if (format == PhotoFormat.Png && PngBlocksLocationCheck(copy, method))
            {
                return true;
            }

            copy.Position = 0;

            if (format == PhotoFormat.WebP && method == PhotoShareMethod.PlainCopy && WebPHasExif(copy))
            {
                return true;
            }

            copy.Position = 0;

            return ContainsLocationText(copy);
        }

        /// <summary>True when the stream holds "GPSLatitude" or "GPSLongitude" as text (XMP, a PNG text chunk, a GIF comment).</summary>
        public static bool ContainsLocationText(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            var overlap = LocationText.Max(t => t.Length) - 1;
            var buffer = new byte[64 * 1024];
            var kept = 0;

            while (true)
            {
                var read = stream.Read(buffer, kept, buffer.Length - kept);

                if (read <= 0)
                {
                    return false;
                }

                var span = buffer.AsSpan(0, kept + read);

                foreach (var text in LocationText)
                {
                    if (span.IndexOf(text) >= 0)
                    {
                        return true;
                    }
                }

                kept = Math.Min(overlap, span.Length);
                span[^kept..].CopyTo(buffer);
            }
        }

        /// <summary>
        /// PNG chunks that defeat the check: an <c>eXIf</c> chunk on a plain copy (not written by ExifInterface, so its
        /// GPS tags are still there), any <c>zTXt</c> chunk, or an <c>iTXt</c> chunk with its compression flag set
        /// (compressed text cannot be searched for XMP location). A damaged chunk list also refuses.
        /// </summary>
        private static bool PngBlocksLocationCheck(Stream stream, PhotoShareMethod method)
        {
            Span<byte> head = stackalloc byte[8];

            if (!ReadExactly(stream, head))
            {
                return true;
            }

            while (true)
            {
                if (!ReadExactly(stream, head))
                {
                    // No IEND: damaged.
                    return true;
                }

                var length = BinaryPrimitives.ReadUInt32BigEndian(head);
                var type = Encoding.ASCII.GetString(head[4..8]);

                if (length > int.MaxValue || stream.Position + length + 4 > stream.Length)
                {
                    return true;
                }

                if (type == "IEND")
                {
                    return false;
                }

                if ((type == "eXIf" && method == PhotoShareMethod.PlainCopy) || type == "zTXt")
                {
                    return true;
                }

                if (type == "iTXt" && ITxtIsCompressed(stream, (int)length))
                {
                    return true;
                }

                stream.Position += length + 4;
            }
        }

        /// <summary>An iTXt chunk's compression flag: the byte after its keyword's terminating zero. Leaves the position at the chunk's data.</summary>
        private static bool ITxtIsCompressed(Stream stream, int length)
        {
            var start = stream.Position;
            var probe = new byte[Math.Min(length, 81)];
            var ok = ReadExactly(stream, probe);
            stream.Position = start;

            if (!ok)
            {
                return true;
            }

            var zero = Array.IndexOf(probe, (byte)0);

            return zero < 0 || zero + 1 >= probe.Length || probe[zero + 1] != 0;
        }

        /// <summary>True when a WebP file has an <c>EXIF</c> chunk (or a damaged chunk list).</summary>
        private static bool WebPHasExif(Stream stream)
        {
            Span<byte> head = stackalloc byte[12];

            if (!ReadExactly(stream, head))
            {
                return true;
            }

            Span<byte> chunk = stackalloc byte[8];

            while (stream.Position < stream.Length)
            {
                if (!ReadExactly(stream, chunk))
                {
                    return true;
                }

                var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..8]);

                if (chunk[..4].SequenceEqual("EXIF"u8))
                {
                    return true;
                }

                var padded = (long)size + (size & 1);

                if (stream.Position + padded > stream.Length)
                {
                    return true;
                }

                stream.Position += padded;
            }

            return false;
        }

        private static bool ReadExactly(Stream stream, Span<byte> buffer)
        {
            try
            {
                stream.ReadExactly(buffer);
                return true;
            }
            catch (EndOfStreamException)
            {
                return false;
            }
        }
    }
}
