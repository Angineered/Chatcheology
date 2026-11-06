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
    /// <item>JPEG: <see cref="PhotoShareMethod.RemoveLocation"/> on every supported Android (8.0 and later). The
    /// copy is rebuilt from the main image only (<see cref="WriteMainJpeg"/>): APP0 JFIF, APP1 Exif, APP2 ICC
    /// profile, APP14 Adobe (colour transform) and the image segments up to the main image's EOI. XMP, MPF, IPTC,
    /// comments, other APPn blocks and everything after EOI (a second image, a gain map, a motion photo's video) are left out; then
    /// <c>ExifInterface</c> clears the GPS tags of the one Exif block left.</item>
    /// <item>PNG and WebP: <see cref="PhotoShareMethod.RemoveLocation"/> on Android 11 (API 30) and later, where
    /// <c>ExifInterface</c> can write them. Below that, <see cref="PhotoShareMethod.PlainCopy"/>, but the copy is
    /// refused when it carries an EXIF block (PNG <c>eXIf</c>, WebP <c>EXIF</c>), which could hold a location.</item>
    /// <item>GIF and BMP: <see cref="PhotoShareMethod.PlainCopy"/>; neither format has EXIF.</item>
    /// <item>Everything else (HEIC/HEIF, AVIF, TIFF, DNG, unknown): <see cref="PhotoShareMethod.Refuse"/>. HEIC can
    /// hold a location and <c>ExifInterface</c> cannot write it.</item>
    /// </list>
    /// <para>Whatever the method, the finished copy is checked again before it is shared
    /// (<see cref="MayStillCarryLocation"/>): any EXIF/TIFF block anywhere in the file with a non-empty GPS
    /// directory (<see cref="HasGpsBlock"/>), any XMP location text ("GPSLatitude", "GPSLongitude") and compressed
    /// PNG text (which cannot be read for it) all refuse it. Android also re-reads the copy's GPS tags.</para>
    /// </remarks>
    public static class PhotoShare
    {
        /// <summary>The first API level whose <c>ExifInterface</c> writes PNG and WebP (Android 11).</summary>
        public const int FirstPngWebPWriteApiLevel = 30;

        /// <summary>
        /// The length of an Adobe APP14 block's data ("Adobe", version, two flag words, colour transform): the only
        /// APP14 block a shared JPEG keeps, and only at exactly this length.
        /// </summary>
        public const int AdobeSegmentLength = 12;

        /// <summary>The folder of the app's cache that holds shared copies; the only cache folder the FileProvider serves.</summary>
        public const string CopiesFolderName = "shared-photos";

        /// <summary>
        /// How long a shared copy is kept: long enough for the receiving app to read it a little later (a messenger
        /// may upload in the background), then it is deleted at the next app start or share.
        /// </summary>
        public static readonly TimeSpan CopyLifetime = TimeSpan.FromHours(24);

        /// <summary>True when a copy last written at <paramref name="writtenUtc"/> is older than <see cref="CopyLifetime"/> at <paramref name="nowUtc"/>.</summary>
        public static bool IsExpired(DateTime writtenUtc, DateTime nowUtc) => nowUtc - writtenUtc > CopyLifetime;

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

            // Independent of how the copy was made: every EXIF/TIFF block in the file, wherever it sits.
            var bytes = new byte[copy.Length];
            copy.Position = 0;
            copy.ReadExactly(bytes);

            if (HasGpsBlock(bytes))
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

        /// <summary>
        /// True when any EXIF/TIFF block anywhere in <paramref name="data"/> has a GPS directory pointer (tag
        /// 0x8825) whose directory is not empty, or points outside the data. Every TIFF header ("MM\0*" or
        /// "II*\0") in the file is tried, so a JPEG APP1 Exif in a trailing image or video, a PNG <c>eXIf</c> chunk
        /// and a WebP <c>EXIF</c> chunk (with or without its "Exif\0\0" prefix) are all found. Only a GPS pointer
        /// tag counts, so random bytes that look like a TIFF header do not refuse a photo.
        /// </summary>
        public static bool HasGpsBlock(ReadOnlySpan<byte> data)
        {
            for (var i = 0; i + 8 <= data.Length; i++)
            {
                var bigEndian = data[i] == (byte)'M' && data[i + 1] == (byte)'M' && data[i + 2] == 0 && data[i + 3] == 42;
                var littleEndian = data[i] == (byte)'I' && data[i + 1] == (byte)'I' && data[i + 2] == 42 && data[i + 3] == 0;

                if ((bigEndian || littleEndian) && TiffHasGps(data[i..], bigEndian))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Walks a TIFF block's directory chain (IFD0, IFD1, at most 8) for a GPS pointer with a non-empty directory.</summary>
        private static bool TiffHasGps(ReadOnlySpan<byte> tiff, bool bigEndian)
        {
            var offset = U32(tiff[4..], bigEndian);

            for (var directory = 0; directory < 8; directory++)
            {
                if (offset < 8 || offset + 2 > (uint)tiff.Length)
                {
                    return false;
                }

                var at = (int)offset;
                var count = U16(tiff[at..], bigEndian);

                for (var entry = 0; entry < count; entry++)
                {
                    var start = at + 2 + (entry * 12);

                    if (start + 12 > tiff.Length)
                    {
                        return false;
                    }

                    if (U16(tiff[start..], bigEndian) != 0x8825)
                    {
                        continue;
                    }

                    var gps = U32(tiff[(start + 8)..], bigEndian);

                    // A GPS pointer that leads nowhere readable is a doubt, and a doubt refuses.
                    if (gps < 8 || gps + 2 > (uint)tiff.Length)
                    {
                        return true;
                    }

                    if (U16(tiff[(int)gps..], bigEndian) > 0)
                    {
                        return true;
                    }
                }

                var next = at + 2 + (count * 12);

                if (next + 4 > tiff.Length)
                {
                    return false;
                }

                offset = U32(tiff[next..], bigEndian);

                if (offset == 0)
                {
                    return false;
                }
            }

            return false;
        }

        private static ushort U16(ReadOnlySpan<byte> at, bool bigEndian) =>
            bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(at) : BinaryPrimitives.ReadUInt16LittleEndian(at);

        private static uint U32(ReadOnlySpan<byte> at, bool bigEndian) =>
            bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(at) : BinaryPrimitives.ReadUInt32LittleEndian(at);

        /// <summary>
        /// Writes a JPEG's main image only to <paramref name="target"/>: SOI; APP0 "JFIF", APP1 "Exif", APP2
        /// "ICC_PROFILE" and APP14 "Adobe" blocks; every other non-APPn segment (DQT, SOFn, DHT, DRI, SOS with its
        /// scan data, ...); and the main image's EOI. Everything else is left out: APP1 XMP, APP2 MPF, APP13 IPTC, other APPn, comments,
        /// and every byte after EOI (a second image, a gain map, a motion photo's video). False, with
        /// <paramref name="target"/> to be thrown away, when the data is not a complete JPEG.
        /// </summary>
        public static bool WriteMainJpeg(ReadOnlySpan<byte> source, Stream target) => WriteMain(source, target, orientationOnly: false);

        /// <summary>
        /// As <see cref="WriteMainJpeg"/>, but no Exif block is copied either (CH-54, D-37: an own memory photo is
        /// stored without its location, with no Android code). In place of the first APP1 "Exif" block, a new
        /// minimal one is written holding only its orientation (tag 274) when that is 2 to 8, so the photo still
        /// shows the right way up; every other Exif tag (the GPS directory among them) is left out. Other segments
        /// come from an allow-list (review S1): DQT, DHT, DAC, SOFn, DRI, SOS and its scan, RSTn, APP2 ICC, the
        /// exact 12-byte APP14 Adobe block, and APP0 JFIF cut to its 14-byte header with no thumbnail. JPGn, DNL,
        /// DHP, EXP, TEM and reserved markers are left out. A DRI that is not exactly 4 bytes refuses the photo.
        /// <see cref="WriteMainJpeg"/> (CH-59 share) is unchanged.
        /// </summary>
        public static bool WriteMainJpegWithoutLocation(ReadOnlySpan<byte> source, Stream target) =>
            WriteMain(source, target, orientationOnly: true);

        /// <summary>
        /// The EXIF orientation (tag 274) of IFD0 in a TIFF block (as inside an "Exif\0\0" APP1), or null when there
        /// is none or it cannot be read.
        /// </summary>
        public static int? ReadOrientation(ReadOnlySpan<byte> tiff)
        {
            if (tiff.Length < 8)
            {
                return null;
            }

            var bigEndian = tiff[0] == (byte)'M' && tiff[1] == (byte)'M' && tiff[2] == 0 && tiff[3] == 42;
            var littleEndian = tiff[0] == (byte)'I' && tiff[1] == (byte)'I' && tiff[2] == 42 && tiff[3] == 0;

            if (!bigEndian && !littleEndian)
            {
                return null;
            }

            var offset = U32(tiff[4..], bigEndian);

            if (offset < 8 || offset + 2 > (uint)tiff.Length)
            {
                return null;
            }

            var at = (int)offset;
            var count = U16(tiff[at..], bigEndian);

            for (var entry = 0; entry < count; entry++)
            {
                var start = at + 2 + (entry * 12);

                if (start + 12 > tiff.Length)
                {
                    return null;
                }

                // Orientation: type SHORT (3), count 1, the value in the first two bytes of the value field.
                if (U16(tiff[start..], bigEndian) == ImageOrientation.ExifTag
                    && U16(tiff[(start + 2)..], bigEndian) == 3
                    && U32(tiff[(start + 4)..], bigEndian) == 1)
                {
                    return U16(tiff[(start + 8)..], bigEndian);
                }
            }

            return null;
        }

        /// <summary>An APP1 "Exif" block's data holding only an orientation: a big-endian TIFF with one IFD0 entry.</summary>
        private static byte[] OrientationOnlyExif(int orientation)
        {
            var data = new byte[6 + 26];
            "Exif\0\0"u8.CopyTo(data);
            var t = data.AsSpan(6);
            "MM"u8.CopyTo(t);
            BinaryPrimitives.WriteUInt16BigEndian(t[2..], 42);
            BinaryPrimitives.WriteUInt32BigEndian(t[4..], 8);
            BinaryPrimitives.WriteUInt16BigEndian(t[8..], 1);
            BinaryPrimitives.WriteUInt16BigEndian(t[10..], ImageOrientation.ExifTag);
            BinaryPrimitives.WriteUInt16BigEndian(t[12..], 3);
            BinaryPrimitives.WriteUInt32BigEndian(t[14..], 1);
            BinaryPrimitives.WriteUInt16BigEndian(t[18..], (ushort)orientation);
            BinaryPrimitives.WriteUInt32BigEndian(t[22..], 0);
            return data;
        }

        /// <summary>A DRI segment's only length: the length field and the restart interval.</summary>
        private const int DriLength = 4;

        /// <summary>The JFIF header's length after its marker and length: "JFIF\0", version, units, density, thumbnail size.</summary>
        private const int JfifHeaderLength = 14;

        /// <summary>
        /// The segments a decoder needs, kept when a photo is stored without location: DQT, DHT, DAC, the SOFn frame
        /// headers (not JPG, DHT or DAC, which share the range), DRI and SOS.
        /// </summary>
        private static bool IsImageSegment(byte marker) => marker switch
        {
            0xDB or 0xC4 or 0xCC or 0xDD or 0xDA => true,
            >= 0xC0 and <= 0xCF => marker is not (0xC4 or 0xC8 or 0xCC),
            _ => false,
        };

        private static bool WriteMain(ReadOnlySpan<byte> source, Stream target, bool orientationOnly)
        {
            ArgumentNullException.ThrowIfNull(target);

            var exifSeen = false;

            if (source.Length < 4 || source[0] != 0xFF || source[1] != 0xD8)
            {
                return false;
            }

            target.Write([0xFF, 0xD8]);
            var position = 2;

            while (true)
            {
                if (position >= source.Length || source[position] != 0xFF)
                {
                    return false;
                }

                // Fill bytes before a marker.
                while (position < source.Length && source[position] == 0xFF)
                {
                    position++;
                }

                if (position >= source.Length)
                {
                    return false;
                }

                var marker = source[position++];

                if (marker == 0xD9)
                {
                    target.Write([0xFF, 0xD9]);
                    return true;
                }

                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    // Stand-alone markers carry no length. Without location, only restart markers are kept (TEM is not).
                    if (!orientationOnly || marker != 0x01)
                    {
                        target.Write([0xFF, marker]);
                    }

                    continue;
                }

                if (marker == 0xD8 || position + 2 > source.Length)
                {
                    return false;
                }

                var length = BinaryPrimitives.ReadUInt16BigEndian(source[position..]);

                if (length < 2 || position + length > source.Length)
                {
                    return false;
                }

                var segment = source.Slice(position, length);
                var data = segment[2..];

                var keep = marker switch
                {
                    0xE0 => data.StartsWith("JFIF\0"u8),
                    0xE1 => data.StartsWith("Exif\0\0"u8),
                    0xE2 => data.StartsWith("ICC_PROFILE\0"u8),

                    // Adobe's colour transform (no location): without it a CMYK or YCCK JPEG shows wrong colours.
                    // Kept only in its exact 12-byte form; anything longer could carry other data and is dropped.
                    0xEE => data.Length == AdobeSegmentLength && data.StartsWith("Adobe"u8),
                    >= 0xE3 and <= 0xEF => false,
                    0xFE => false,
                    _ => true,
                };

                if (orientationOnly && marker is < 0xE0 or > 0xEF and not 0xFE)
                {
                    // CH-54 (review S1): without location, only the segments a decoder needs, from an allow-list:
                    // DQT, DHT, DAC, SOFn, DRI and SOS. Every other marker (JPGn, DNL, DHP, EXP, reserved) is left out.
                    keep = IsImageSegment(marker);

                    // DRI only in its exact 4-byte form (step 1a re-review S5): extra bytes could carry other data.
                    // Refused, not normalised, so the platform step re-encodes the photo instead.
                    if (marker == 0xDD && length != DriLength)
                    {
                        return false;
                    }
                }

                if (keep && orientationOnly && marker == 0xE0)
                {
                    // The JFIF header only (14 bytes), with no thumbnail: whatever follows it is left out.
                    keep = false;

                    if (data.Length >= JfifHeaderLength)
                    {
                        var jfif = new byte[4 + JfifHeaderLength];
                        jfif[0] = 0xFF;
                        jfif[1] = 0xE0;
                        BinaryPrimitives.WriteUInt16BigEndian(jfif.AsSpan(2), JfifHeaderLength + 2);
                        data[..JfifHeaderLength].CopyTo(jfif.AsSpan(4));
                        jfif[^2] = 0;
                        jfif[^1] = 0;
                        target.Write(jfif);
                    }
                }

                if (keep && orientationOnly && marker == 0xE1)
                {
                    // Only the first Exif block's orientation, as a new block; the block itself is never copied.
                    keep = false;

                    if (!exifSeen && ReadOrientation(data[6..]) is { } orientation and >= 2 and <= 8)
                    {
                        var exif = OrientationOnlyExif(orientation);
                        var header = new byte[] { 0xFF, 0xE1, 0, 0 };
                        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)(exif.Length + 2));
                        target.Write(header);
                        target.Write(exif);
                    }

                    exifSeen = true;
                }

                if (keep)
                {
                    target.Write([0xFF, marker]);
                    target.Write(segment);
                }

                position += length;

                if (marker != 0xDA)
                {
                    continue;
                }

                // Scan data: up to the next marker that is not a stuffed 0x00, a restart marker or a fill byte.
                var start = position;

                while (true)
                {
                    if (position + 1 >= source.Length)
                    {
                        return false;
                    }

                    if (source[position] == 0xFF)
                    {
                        var next = source[position + 1];

                        if (next == 0x00 || (next >= 0xD0 && next <= 0xD7))
                        {
                            position += 2;
                            continue;
                        }

                        if (next != 0xFF)
                        {
                            break;
                        }
                    }

                    position++;
                }

                // Up to the fill bytes; the marker loop above skips them.
                target.Write(source[start..position]);
            }
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
