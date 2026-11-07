using System.Buffers.Binary;
using Chatcheology.Mobile.Presentation;

namespace Chatcheology.Mobile.OwnMemories
{
    /// <summary>
    /// Rebuilds a photo from an allow-list of the parts that make up its picture (CH-54, D-37; review M1): everything
    /// else, metadata of any kind, unknown parts and bytes after the end, is left out. No detection decides what is
    /// kept, so a location in a form no check knows (a raw Exif profile in PNG text, a place name in XMP, coordinates in
    /// a GIF comment) is never stored. Pure .NET; the picture's own bytes are copied unchanged.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>JPEG: <see cref="PhotoShare.WriteMainJpegWithoutLocation"/>.</item>
    /// <item>PNG: <c>IHDR</c> first, then only <c>PLTE, IDAT, tRNS, gAMA, cHRM, sRGB, iCCP, sBIT, bKGD, pHYs, cICP</c> and
    /// the APNG chunks <c>acTL, fcTL, fdAT</c>, up to <c>IEND</c>. Each kept chunk is copied whole, its CRC included,
    /// except <c>iCCP</c>, whose free-text profile name is replaced by the fixed <see cref="IccProfileName"/> (CRC
    /// recomputed; step 1a re-review S5).</item>
    /// <item>WebP: only <c>VP8 , VP8L, VP8X, ALPH, ANIM, ANMF, ICCP</c>; inside an <c>ANMF</c> frame only <c>ALPH, VP8 , VP8L</c>.
    /// The <c>VP8X</c> EXIF and XMP flags are cleared and the RIFF size is rewritten. A <c>VP8X</c> chunk that is not
    /// exactly 10 bytes is refused (S5).</item>
    /// <item>GIF: the header, screen descriptor, colour tables, images and graphic control extensions; the
    /// <c>NETSCAPE2.0</c> / <c>ANIMEXTS1.0</c> loop extension only in its exact 3-byte form. Comment, plain-text and
    /// every other application extension are left out, and everything after the trailer. A graphic control extension
    /// that is not exactly one 4-byte block is refused (S5).</item>
    /// <item>BMP: cut to the size its file header gives; refused when the headers do not agree. The bytes between the
    /// headers (with their colour masks and palette) and the pixels are zeroed, except a V5 header's embedded colour
    /// profile (S5).</item>
    /// </list>
    /// <para>Anything damaged or not as the format says is refused (null): missing is better than wrong.</para>
    /// </remarks>
    public static class PhotoRebuild
    {
        private static readonly HashSet<string> PngChunks =
        [
            "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "bKGD", "pHYs", "cICP",
            "acTL", "fcTL", "fdAT",
        ];

        private static readonly HashSet<string> WebPChunks = ["VP8 ", "VP8L", "VP8X", "ALPH", "ANIM", "ANMF", "ICCP"];

        private static readonly HashSet<string> WebPFrameChunks = ["ALPH", "VP8 ", "VP8L"];

        /// <summary>The VP8X flags for EXIF (0x08) and XMP (0x04) metadata.</summary>
        private const byte WebPMetadataFlags = 0x08 | 0x04;

        private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        /// <summary>The profile name every stored PNG's <c>iCCP</c> chunk gets in place of its own (S5).</summary>
        public const string IccProfileName = "ICC profile";

        /// <summary>The only size of a WebP <c>VP8X</c> chunk's data.</summary>
        private const int Vp8xLength = 10;

        /// <summary>The only size of a GIF graphic control extension's one data block.</summary>
        private const int GraphicControlLength = 4;

        /// <summary>The <c>BI_BITFIELDS</c> compression, whose 3 masks follow a 40-byte header.</summary>
        private const uint BitFields = 3;

        /// <summary>The <c>BI_ALPHABITFIELDS</c> compression, whose 4 masks follow a 40-byte header.</summary>
        private const uint AlphaBitFields = 6;

        /// <summary>The V5 colour space type of an embedded profile, "MBED".</summary>
        private const uint ProfileEmbedded = 0x4D424544;

        private static readonly Lazy<uint[]> CrcTable = new(BuildCrcTable);

        /// <summary>The photo rebuilt without anything but its picture, or null when it is damaged or not of <paramref name="format"/>.</summary>
        public static byte[]? Rebuild(ReadOnlySpan<byte> source, PhotoFormat format)
        {
            using var target = new MemoryStream(source.Length);

            var ok = format switch
            {
                PhotoFormat.Jpeg => PhotoShare.WriteMainJpegWithoutLocation(source, target),
                PhotoFormat.Png => WritePng(source, target),
                PhotoFormat.WebP => WriteWebP(source, target),
                PhotoFormat.Gif => WriteGif(source, target),
                PhotoFormat.Bmp => WriteBmp(source, target),
                _ => false,
            };

            return ok ? target.ToArray() : null;
        }

        /// <summary>PNG by chunk allow-list; false when damaged.</summary>
        public static bool WritePng(ReadOnlySpan<byte> source, Stream target)
        {
            ArgumentNullException.ThrowIfNull(target);

            if (source.Length < 8 || !source[..8].SequenceEqual(PngSignature))
            {
                return false;
            }

            target.Write(PngSignature);
            var position = 8;
            var first = true;

            while (true)
            {
                if (position + 12 > source.Length)
                {
                    return false;
                }

                var length = BinaryPrimitives.ReadUInt32BigEndian(source[position..]);

                if (length > int.MaxValue || position + 12L + length > source.Length)
                {
                    return false;
                }

                var typeBytes = source.Slice(position + 4, 4);

                if (!IsAsciiLetters(typeBytes))
                {
                    return false;
                }

                var type = System.Text.Encoding.ASCII.GetString(typeBytes);
                var whole = source.Slice(position, 12 + (int)length);

                if (first && type != "IHDR")
                {
                    return false;
                }

                first = false;

                if (type == "iCCP")
                {
                    if (IccpWithFixedName(source.Slice(position + 8, (int)length)) is not { } chunk)
                    {
                        return false;
                    }

                    target.Write(chunk);
                }
                else if (PngChunks.Contains(type))
                {
                    target.Write(whole);
                }

                position += whole.Length;

                if (type == "IEND")
                {
                    // Everything after the end is left out.
                    return true;
                }
            }
        }

        /// <summary>
        /// An <c>iCCP</c> chunk (length, type, data, CRC) whose profile name is <see cref="IccProfileName"/>, the
        /// compression method and the compressed profile copied as they are; null when the data has no name ending.
        /// </summary>
        private static byte[]? IccpWithFixedName(ReadOnlySpan<byte> data)
        {
            // The name: 1 to 79 bytes, then a zero byte, then the compression method.
            var end = data.IndexOf((byte)0);

            if (end < 1 || end > 79 || end + 2 > data.Length)
            {
                return null;
            }

            var name = System.Text.Encoding.ASCII.GetBytes(IccProfileName);
            var rest = data[end..];
            var chunk = new byte[4 + 4 + name.Length + rest.Length + 4];
            BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)(name.Length + rest.Length));
            "iCCP"u8.CopyTo(chunk.AsSpan(4));
            name.CopyTo(chunk.AsSpan(8));
            rest.CopyTo(chunk.AsSpan(8 + name.Length));
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), Crc32(chunk.AsSpan(4, chunk.Length - 8)));
            return chunk;
        }

        /// <summary>The PNG CRC (ISO 3309) of <paramref name="bytes"/>: a chunk's type and data.</summary>
        public static uint Crc32(ReadOnlySpan<byte> bytes)
        {
            var table = CrcTable.Value;
            var crc = 0xFFFFFFFFu;

            foreach (var b in bytes)
            {
                crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];

            for (var n = 0u; n < 256; n++)
            {
                var c = n;

                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }

        /// <summary>WebP by chunk allow-list, metadata flags cleared and the RIFF size rewritten; false when damaged.</summary>
        public static bool WriteWebP(ReadOnlySpan<byte> source, Stream target)
        {
            ArgumentNullException.ThrowIfNull(target);

            if (source.Length < 12 || !source[..4].SequenceEqual("RIFF"u8) || !source[8..12].SequenceEqual("WEBP"u8))
            {
                return false;
            }

            var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(source[4..]);

            if (riffSize < 4 || 8L + riffSize > source.Length)
            {
                return false;
            }

            // Only the RIFF body; trailing bytes are left out.
            if (KeepChunks(source[12..(int)(8 + riffSize)], WebPChunks, topLevel: true) is not { } body || body.Length == 0)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[12];
            "RIFF"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(4 + body.Length));
            "WEBP"u8.CopyTo(header[8..]);
            target.Write(header);
            target.Write(body);
            return true;
        }

        /// <summary>The allowed chunks of a RIFF body, rewritten; null when the chunk list is damaged.</summary>
        private static byte[]? KeepChunks(ReadOnlySpan<byte> body, HashSet<string> allowed, bool topLevel)
        {
            using var output = new MemoryStream(body.Length);
            var position = 0;
            var image = false;

            while (position < body.Length)
            {
                if (position + 8 > body.Length)
                {
                    return null;
                }

                var fourcc = System.Text.Encoding.ASCII.GetString(body.Slice(position, 4));
                var size = BinaryPrimitives.ReadUInt32LittleEndian(body[(position + 4)..]);
                var padded = (long)size + (size & 1);

                if (position + 8L + size > body.Length)
                {
                    return null;
                }

                var data = body.Slice(position + 8, (int)size);

                if (allowed.Contains(fourcc))
                {
                    byte[] kept;

                    if (topLevel && fourcc == "VP8X")
                    {
                        // Exactly 10 bytes (S5): anything longer could carry other data.
                        if (data.Length != Vp8xLength)
                        {
                            return null;
                        }

                        kept = data.ToArray();
                        kept[0] &= unchecked((byte)~WebPMetadataFlags);
                    }
                    else if (topLevel && fourcc == "ANMF")
                    {
                        // A frame: 16 bytes of position, size and timing, then its own chunks.
                        if (data.Length < 16 || KeepChunks(data[16..], WebPFrameChunks, topLevel: false) is not { } frame)
                        {
                            return null;
                        }

                        kept = [.. data[..16], .. frame];
                    }
                    else
                    {
                        kept = data.ToArray();
                    }

                    image |= fourcc is "VP8 " or "VP8L" or "ANMF";
                    WriteChunk(output, body.Slice(position, 4), kept);
                }

                // The last chunk may lack its pad byte.
                position = (int)Math.Min(position + 8L + padded, body.Length);
            }

            return image ? output.ToArray() : null;
        }

        private static void WriteChunk(Stream output, ReadOnlySpan<byte> fourcc, ReadOnlySpan<byte> data)
        {
            Span<byte> head = stackalloc byte[8];
            fourcc.CopyTo(head);
            BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)data.Length);
            output.Write(head);
            output.Write(data);

            if (data.Length % 2 == 1)
            {
                output.WriteByte(0);
            }
        }

        /// <summary>GIF without comment, plain-text and application extensions (but the loop count); false when damaged.</summary>
        public static bool WriteGif(ReadOnlySpan<byte> source, Stream target)
        {
            ArgumentNullException.ThrowIfNull(target);

            if (source.Length < 13 || !(source[..6].SequenceEqual("GIF87a"u8) || source[..6].SequenceEqual("GIF89a"u8)))
            {
                return false;
            }

            // Header and logical screen descriptor, then the global colour table if there is one.
            var position = 13 + ColourTableLength(source[10]);

            if (position > source.Length)
            {
                return false;
            }

            target.Write(source[..position]);

            while (true)
            {
                if (position >= source.Length)
                {
                    return false;
                }

                switch (source[position])
                {
                    case 0x3B:
                        // The trailer; everything after it is left out.
                        target.WriteByte(0x3B);
                        return true;

                    case 0x2C:
                    {
                        // Image descriptor (10 bytes), local colour table, LZW code size, data sub-blocks.
                        if (position + 11 > source.Length)
                        {
                            return false;
                        }

                        var start = position;
                        position += 10 + ColourTableLength(source[position + 9]) + 1;

                        if (position > source.Length || SkipSubBlocks(source, position) is not { } end)
                        {
                            return false;
                        }

                        target.Write(source[start..end]);
                        position = end;
                        break;
                    }

                    case 0x21:
                    {
                        if (position + 2 > source.Length || SkipSubBlocks(source, position + 2) is not { } end)
                        {
                            return false;
                        }

                        var label = source[position + 1];
                        var blocks = source[(position + 2)..end];

                        if (label == 0xF9 && !IsGraphicControl(blocks))
                        {
                            // Not its exact form (S5): extra bytes could carry other data. Refused, not normalised.
                            return false;
                        }

                        if (label == 0xF9 || (label == 0xFF && IsLoopExtension(blocks)))
                        {
                            target.Write(source[position..end]);
                        }

                        position = end;
                        break;
                    }

                    default:
                        return false;
                }
            }
        }

        /// <summary>
        /// The NETSCAPE2.0 or ANIMEXTS1.0 loop extension in its exact form: an 11-byte identifier block, one 3-byte
        /// block (1, loop count), then the terminator.
        /// </summary>
        private static bool IsLoopExtension(ReadOnlySpan<byte> blocks) =>
            blocks.Length == 1 + 11 + 1 + 3 + 1
            && blocks[0] == 11
            && (blocks.Slice(1, 11).SequenceEqual("NETSCAPE2.0"u8) || blocks.Slice(1, 11).SequenceEqual("ANIMEXTS1.0"u8))
            && blocks[12] == 3
            && blocks[13] == 1
            && blocks[16] == 0;

        /// <summary>A graphic control extension's blocks in their exact form: one 4-byte block, then the terminator.</summary>
        private static bool IsGraphicControl(ReadOnlySpan<byte> blocks) =>
            blocks.Length == 1 + GraphicControlLength + 1 && blocks[0] == GraphicControlLength && blocks[^1] == 0;

        private static int ColourTableLength(byte packed) => (packed & 0x80) == 0 ? 0 : 3 * (1 << ((packed & 0x07) + 1));

        /// <summary>The position after a chain of sub-blocks ending with a zero-length block, or null when it runs out.</summary>
        private static int? SkipSubBlocks(ReadOnlySpan<byte> source, int position)
        {
            while (position < source.Length)
            {
                var size = source[position];
                position += 1 + size;

                if (size == 0)
                {
                    return position;
                }
            }

            return null;
        }

        /// <summary>BMP cut to its file header's size; false when the headers do not agree.</summary>
        public static bool WriteBmp(ReadOnlySpan<byte> source, Stream target)
        {
            ArgumentNullException.ThrowIfNull(target);

            if (source.Length < 26 || source[0] != (byte)'B' || source[1] != (byte)'M')
            {
                return false;
            }

            var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(source[2..]);
            var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(source[10..]);
            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(source[14..]);

            if (headerSize is not (12 or 40 or 52 or 56 or 108 or 124)
                || fileSize > source.Length
                || pixelOffset < 14 + headerSize
                || pixelOffset >= fileSize)
            {
                return false;
            }

            var copy = source[..(int)fileSize].ToArray();
            ZeroBmpGap(copy, (int)headerSize, (int)pixelOffset);
            target.Write(copy);
            return true;
        }

        /// <summary>
        /// Zeroes the bytes after the headers, colour masks and palette and before the pixels (S5), keeping a V5
        /// header's embedded colour profile where it lies in that gap.
        /// </summary>
        private static void ZeroBmpGap(byte[] bmp, int headerSize, int pixelOffset)
        {
            const int info = 14;
            long tables;

            if (headerSize == 12)
            {
                // OS/2 core header: 3-byte palette entries, one per colour of up to 8 bits.
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(bmp.AsSpan(info + 10));
                tables = bits is >= 1 and <= 8 ? 3L * (1 << bits) : 0;
            }
            else
            {
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(bmp.AsSpan(info + 14));
                var compression = BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(info + 16));
                var used = BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(info + 32));
                var colours = used != 0 ? used : bits is >= 1 and <= 8 ? 1u << bits : 0u;
                var masks = headerSize == 40 ? compression switch { BitFields => 12, AlphaBitFields => 16, _ => 0 } : 0;
                tables = masks + (4L * colours);
            }

            var gapStart = Math.Min(info + headerSize + tables, pixelOffset);
            long keepStart = 0;
            long keepEnd = 0;

            if (headerSize == 124 && BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(info + 56)) == ProfileEmbedded)
            {
                keepStart = info + (long)BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(info + 112));
                keepEnd = keepStart + BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(info + 116));
            }

            for (var i = gapStart; i < pixelOffset; i++)
            {
                if (i < keepStart || i >= keepEnd)
                {
                    bmp[i] = 0;
                }
            }
        }

        private static bool IsAsciiLetters(ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes)
            {
                if (!char.IsAsciiLetter((char)b))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
