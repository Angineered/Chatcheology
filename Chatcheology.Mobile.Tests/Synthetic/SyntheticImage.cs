using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Chatcheology.Mobile.Synthetic
{
    /// <summary>
    /// Invented PNG images: diagonal bands of two colours, so each one has its own bytes and is easy
    /// to tell apart on screen. No image library; the format is written by hand.
    /// </summary>
    /// <remarks>
    /// Compiled into the tests and, as a linked file, into Debug builds of the Android app only.
    /// </remarks>
    internal static class SyntheticImage
    {
        private static readonly uint[] CrcTable = BuildCrcTable();

        /// <summary>An RGB PNG of the given size with bands of the two colours.</summary>
        internal static byte[] Png(int width, int height, (byte R, byte G, byte B) first, (byte R, byte G, byte B) second)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

            var stride = 1 + width * 3;
            var raw = new byte[stride * height];
            var band = Math.Max(8, Math.Min(width, height) / 8);

            for (var y = 0; y < height; y++)
            {
                // Each scanline starts with filter type 0 (none).
                var offset = y * stride + 1;

                for (var x = 0; x < width; x++)
                {
                    var colour = ((x + y) / band) % 2 == 0 ? first : second;
                    raw[offset + x * 3] = colour.R;
                    raw[offset + x * 3 + 1] = colour.G;
                    raw[offset + x * 3 + 2] = colour.B;
                }
            }

            using var output = new MemoryStream();
            output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
            header[8] = 8;  // bits per channel
            header[9] = 2;  // RGB
            WriteChunk(output, "IHDR", header);

            using (var compressed = new MemoryStream())
            {
                using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                {
                    zlib.Write(raw);
                }

                WriteChunk(output, "IDAT", compressed.ToArray());
            }

            WriteChunk(output, "IEND", []);

            return output.ToArray();
        }

        private static void WriteChunk(Stream output, string type, byte[] data)
        {
            var typeBytes = Encoding.ASCII.GetBytes(type);
            Span<byte> number = stackalloc byte[4];

            BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
            output.Write(number);
            output.Write(typeBytes);
            output.Write(data);

            var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
            BinaryPrimitives.WriteUInt32BigEndian(number, crc);
            output.Write(number);
        }

        private static uint Crc(uint crc, byte[] data)
        {
            foreach (var b in data)
            {
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];

            for (uint n = 0; n < 256; n++)
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
    }
}
