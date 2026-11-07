using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Chatcheology.Mobile.OwnMemories
{
    /// <summary>
    /// The small zip subset of the own memories file (CH-54 step 3), written and read by this code alone, so nothing of a
    /// zip it did not write is ever trusted.
    /// </summary>
    /// <remarks>
    /// <para><strong>Written:</strong> entries one after the other with their sizes and CRC in the local header (no data
    /// descriptor), stored (photos) or deflated (the JSON), every date 1 January 1980 (no time of saving), no extra fields,
    /// no comments, no zip64; then the central directory and its end record. Written front to back, so the target needs
    /// no seeking (a system Save dialog's stream).</para>
    /// <para><strong>Read</strong> front to back from any stream, never trusting a header for a limit: the bytes read, each
    /// entry's unpacked bytes and the sum of them are counted while reading and refused past their limit (a zip bomb). An
    /// entry must be stored or deflated, unencrypted, with no data descriptor and no zip64 field; its unpacked size and CRC
    /// must match its header; names must be unique; the central directory must repeat every local header exactly, and the
    /// end record must close the stream. Anything else refuses the whole file.</para>
    /// </remarks>
    internal static class OwnMemoryZip
    {
        private const uint LocalSignature = 0x04034B50;
        private const uint CentralSignature = 0x02014B50;
        private const uint EndSignature = 0x06054B50;
        private const uint Zip64EndSignature = 0x06064B50;
        private const uint Zip64LocatorSignature = 0x07064B50;
        private const ushort Version = 20;
        private const ushort DosDate1980 = 0x0021;
        private const ushort Stored = 0;
        private const ushort Deflated = 8;

        /// <summary>The longest entry name read (step 3 review S2); this app writes 76 at most.</summary>
        internal const int MaxNameBytes = 255;

        /// <summary>The general-purpose flags a read entry may have: the deflate option bits (1, 2) and UTF-8 names (11).</summary>
        private const ushort AllowedFlags = 0x0806;

        /// <summary>Encryption (0) and strong encryption (6).</summary>
        private const ushort EncryptionFlags = 0x0041;

        /// <summary>A data descriptor follows the data (3).</summary>
        private const ushort DescriptorFlag = 0x0008;

        /// <summary>One entry as its local header gave it; the central directory must repeat it.</summary>
        private sealed record Entry(byte[] Name, ushort Flags, ushort Method, uint Crc, uint PackedSize, uint Size, long Offset);

        /// <summary>
        /// Reads <paramref name="source"/> to its end. <paramref name="capFor"/> says, for an entry's name, the most bytes it
        /// may unpack to, or null to skip it unread (an entry the file has no use for, counted). Each accepted entry's bytes
        /// go to <paramref name="accept"/> as soon as they are read and checked.
        /// </summary>
        /// <returns>How many entries were skipped.</returns>
        /// <exception cref="OwnMemoryFileException">The file is refused; see its problem.</exception>
        /// <remarks>
        /// An accepted entry that is deflated while <paramref name="mayDeflate"/> says it must be stored refuses the file as
        /// not supported (step 3 review S1: unpacking work then grows with the file's size, not with the caps). A name longer
        /// than <see cref="MaxNameBytes"/> refuses the file as damaged (S2: names are kept for the central directory check).
        /// </remarks>
        internal static int Read(
            Stream source, OwnMemoryFileLimits limits, Func<string, long?> capFor, Func<string, bool> mayDeflate, Action<string, byte[]> accept)
        {
            var input = new LimitedStream(source, limits.MaxFileBytes);
            var entries = new List<Entry>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var skipped = 0;
            long unpacked = 0;

            try
            {
                var signature = input.ReadUInt32();

                while (signature == LocalSignature)
                {
                    if (entries.Count >= limits.MaxEntries)
                    {
                        throw Refuse(OwnMemoryFileProblem.TooLarge);
                    }

                    var offset = input.Position - 4;
                    var header = input.ReadBytes(26);
                    var needed = BinaryPrimitives.ReadUInt16LittleEndian(header);
                    var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
                    var method = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
                    var crc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(10));
                    var packed = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14));
                    var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(18));
                    var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22));

                    if (nameLength > MaxNameBytes)
                    {
                        throw Refuse(OwnMemoryFileProblem.Damaged);
                    }

                    var name = input.ReadBytes(nameLength);
                    var extra = input.ReadBytes(BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(24)));

                    CheckSupported(needed, flags, method, packed, size, extra);

                    if (method == Stored && packed != size)
                    {
                        throw Refuse(OwnMemoryFileProblem.Damaged);
                    }

                    // Latin-1 keeps every byte as one character, so two different names never compare equal.
                    var text = Encoding.Latin1.GetString(name);

                    if (!names.Add(text))
                    {
                        throw Refuse(OwnMemoryFileProblem.Damaged);
                    }

                    entries.Add(new Entry(name, flags, method, crc, packed, size, offset));

                    if (capFor(text) is not { } cap)
                    {
                        input.Skip(packed);
                        skipped++;
                    }
                    else
                    {
                        if (method == Deflated && !mayDeflate(text))
                        {
                            throw Refuse(OwnMemoryFileProblem.NotSupported);
                        }

                        if (size > cap)
                        {
                            throw Refuse(OwnMemoryFileProblem.TooLarge);
                        }

                        var data = Unpack(input, method, packed, cap, limits.MaxTotalBytes, ref unpacked);

                        if (data.LongLength != size || Crc(data) != crc)
                        {
                            throw Refuse(OwnMemoryFileProblem.Damaged);
                        }

                        accept(text, data);
                    }

                    signature = input.ReadUInt32();
                }

                if (entries.Count == 0)
                {
                    throw Refuse(signature == EndSignature ? OwnMemoryFileProblem.NotAMemoriesFile : OwnMemoryFileProblem.Damaged);
                }

                var centralStart = input.Position - 4;

                foreach (var entry in entries)
                {
                    ReadCentral(input, signature, entry);
                    signature = input.ReadUInt32();
                }

                ReadEnd(input, signature, entries.Count, centralStart);
                return skipped;
            }
            catch (EndOfStreamException)
            {
                throw Refuse(OwnMemoryFileProblem.Damaged);
            }
            catch (InvalidDataException)
            {
                throw Refuse(OwnMemoryFileProblem.Damaged);
            }
        }

        private static void CheckSupported(ushort needed, ushort flags, ushort method, uint packed, uint size, byte[] extra)
        {
            if ((flags & EncryptionFlags) != 0 || (flags & DescriptorFlag) != 0 || (flags & ~(AllowedFlags | EncryptionFlags | DescriptorFlag)) != 0
                || method is not (Stored or Deflated)
                || needed > Version
                || packed == uint.MaxValue || size == uint.MaxValue
                || HasZip64Field(extra))
            {
                throw Refuse(OwnMemoryFileProblem.NotSupported);
            }
        }

        /// <summary>Whether an extra field block holds a zip64 field (id 1); a block that does not parse is damage.</summary>
        private static bool HasZip64Field(byte[] extra)
        {
            var position = 0;

            while (position < extra.Length)
            {
                if (position + 4 > extra.Length)
                {
                    throw Refuse(OwnMemoryFileProblem.Damaged);
                }

                var id = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(position));
                var length = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(position + 2));

                if (id == 1)
                {
                    return true;
                }

                position += 4 + length;

                if (position > extra.Length)
                {
                    throw Refuse(OwnMemoryFileProblem.Damaged);
                }
            }

            return false;
        }

        /// <summary>Unpacks one entry, counting what comes out against its own cap and the whole file's.</summary>
        private static byte[] Unpack(LimitedStream input, ushort method, uint packed, long cap, long totalCap, ref long unpacked)
        {
            Stream bounded = new BoundedStream(input, packed);
            TrickleStream? trickle = null;

            if (method == Deflated)
            {
                // Only the JSON may be deflated (S1), so its packed bytes are small: they are read first (past the cap plus
                // room for deflate's own overhead the file is too large), then fed to the inflater one byte at a time, so
                // what it leaves unread after the end of its stream is known exactly (review N1: junk after the data).
                if (packed > cap + (64 * 1024))
                {
                    throw Refuse(OwnMemoryFileProblem.TooLarge);
                }

                var packedBytes = new byte[packed];
                bounded.ReadExactly(packedBytes);
                bounded = trickle = new TrickleStream(packedBytes);
            }

            using var output = new MemoryStream();
            var buffer = new byte[81920];

            using (var data = method == Deflated ? new DeflateStream(bounded, CompressionMode.Decompress, leaveOpen: true) : bounded)
            {
                int read;

                while ((read = data.Read(buffer, 0, buffer.Length)) > 0)
                {
                    unpacked += read;

                    if (output.Length + read > cap || unpacked > totalCap)
                    {
                        throw Refuse(OwnMemoryFileProblem.TooLarge);
                    }

                    output.Write(buffer, 0, read);
                }
            }

            // Packed bytes the deflate stream did not use are junk after its data: the file is damaged.
            if (trickle is not null && trickle.Left > 0)
            {
                throw Refuse(OwnMemoryFileProblem.Damaged);
            }

            return output.ToArray();
        }

        /// <summary>A packed entry in memory, handed out one byte per read so the inflater never reads past its own end.</summary>
        private sealed class TrickleStream(byte[] bytes) : Stream
        {
            private int _position;

            internal int Left => bytes.Length - _position;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (count == 0 || _position >= bytes.Length)
                {
                    return 0;
                }

                buffer[offset] = bytes[_position++];
                return 1;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private static void ReadCentral(LimitedStream input, uint signature, Entry entry)
        {
            if (signature is Zip64EndSignature or Zip64LocatorSignature)
            {
                throw Refuse(OwnMemoryFileProblem.NotSupported);
            }

            if (signature != CentralSignature)
            {
                throw Refuse(OwnMemoryFileProblem.Damaged);
            }

            var header = input.ReadBytes(42);
            var needed = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
            var method = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            var packed = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(24));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            var disk = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(38));

            if (nameLength != entry.Name.Length)
            {
                throw Refuse(OwnMemoryFileProblem.Damaged);
            }

            var name = input.ReadBytes(nameLength);
            var extra = input.ReadBytes(extraLength);
            input.Skip(commentLength);

            CheckSupported(needed, flags, method, packed, size, extra);

            if (offset == uint.MaxValue || disk == ushort.MaxValue)
            {
                throw Refuse(OwnMemoryFileProblem.NotSupported);
            }

            if (flags != entry.Flags || method != entry.Method || crc != entry.Crc || packed != entry.PackedSize || size != entry.Size
                || disk != 0 || offset != entry.Offset || !name.AsSpan().SequenceEqual(entry.Name))
            {
                throw Refuse(OwnMemoryFileProblem.Damaged);
            }
        }

        private static void ReadEnd(LimitedStream input, uint signature, int count, long centralStart)
        {
            if (signature is Zip64EndSignature or Zip64LocatorSignature)
            {
                throw Refuse(OwnMemoryFileProblem.NotSupported);
            }

            if (signature != EndSignature)
            {
                throw Refuse(OwnMemoryFileProblem.Damaged);
            }

            var centralSize = input.Position - 4 - centralStart;
            var end = input.ReadBytes(18);
            var disk = BinaryPrimitives.ReadUInt16LittleEndian(end);
            var centralDisk = BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(2));
            var onDisk = BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(4));
            var total = BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(6));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(end.AsSpan(8));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(end.AsSpan(12));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(end.AsSpan(16));

            if (total == ushort.MaxValue || onDisk == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
            {
                throw Refuse(OwnMemoryFileProblem.NotSupported);
            }

            if (disk != 0 || centralDisk != 0 || onDisk != count || total != count || size != centralSize || offset != centralStart)
            {
                throw Refuse(OwnMemoryFileProblem.Damaged);
            }

            input.Skip(commentLength);

            if (input.ReadByte() != -1)
            {
                throw Refuse(OwnMemoryFileProblem.Damaged);
            }
        }

        /// <summary>The CRC-32 a zip entry carries.</summary>
        internal static uint Crc(ReadOnlySpan<byte> bytes) => PhotoRebuild.Crc32(bytes);

        private static OwnMemoryFileException Refuse(OwnMemoryFileProblem problem) => new(problem);

        /// <summary>Writes the zip subset front to back.</summary>
        internal sealed class Writer
        {
            private readonly Stream _target;
            private readonly List<Entry> _entries = [];
            private long _position;

            internal Writer(Stream target)
            {
                _target = target;
            }

            /// <summary>Adds an entry stored as it is (photos: they do not shrink).</summary>
            internal void AddStored(string name, byte[] data) => Add(name, data, Stored, data);

            /// <summary>Adds a deflated entry (the JSON).</summary>
            internal void AddDeflated(string name, byte[] data)
            {
                using var packed = new MemoryStream();

                using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true))
                {
                    deflate.Write(data);
                }

                Add(name, data, Deflated, packed.ToArray());
            }

            /// <summary>Writes the central directory and the end record.</summary>
            internal void Finish()
            {
                var centralStart = _position;

                foreach (var entry in _entries)
                {
                    var header = new byte[46];
                    BinaryPrimitives.WriteUInt32LittleEndian(header, CentralSignature);
                    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), Version);
                    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), Version);
                    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), entry.Method);
                    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), DosDate1980);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), entry.Crc);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), entry.PackedSize);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), entry.Size);
                    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), (ushort)entry.Name.Length);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(42), checked((uint)entry.Offset));
                    Write(header);
                    Write(entry.Name);
                }

                var end = new byte[22];
                BinaryPrimitives.WriteUInt32LittleEndian(end, EndSignature);
                BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), checked((ushort)_entries.Count));
                BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), checked((ushort)_entries.Count));
                BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), checked((uint)(_position - centralStart)));
                BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), checked((uint)centralStart));
                Write(end);
                _target.Flush();
            }

            private void Add(string name, byte[] data, ushort method, byte[] packed)
            {
                var nameBytes = Encoding.ASCII.GetBytes(name);
                var entry = new Entry(nameBytes, 0, method, Crc(data), checked((uint)packed.Length), checked((uint)data.Length), _position);

                // Never past what the format can say without zip64 (the limits keep far below it).
                if (_position + 30 + nameBytes.Length + packed.LongLength >= uint.MaxValue || _entries.Count >= ushort.MaxValue - 1)
                {
                    throw new OwnMemoryFileException(OwnMemoryFileProblem.TooLarge);
                }

                var header = new byte[30];
                BinaryPrimitives.WriteUInt32LittleEndian(header, LocalSignature);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), Version);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), method);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), DosDate1980);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), entry.Crc);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), entry.PackedSize);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), entry.Size);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), (ushort)nameBytes.Length);
                Write(header);
                Write(nameBytes);
                Write(packed);
                _entries.Add(entry);
            }

            private void Write(byte[] bytes)
            {
                _target.Write(bytes);
                _position += bytes.LongLength;
            }
        }

        /// <summary>The source, counted: past <c>max</c> bytes the file is too large; reads front to back only.</summary>
        private sealed class LimitedStream(Stream inner, long max) : Stream
        {
            private readonly byte[] _one = new byte[1];
            private long _read;

            public override long Position
            {
                get => _read;
                set => throw new NotSupportedException();
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = inner.Read(buffer, offset, count);
                _read += read;

                if (_read > max)
                {
                    throw Refuse(OwnMemoryFileProblem.TooLarge);
                }

                return read;
            }

            public override int ReadByte() => Read(_one, 0, 1) == 1 ? _one[0] : -1;

            internal byte[] ReadBytes(int count)
            {
                var bytes = new byte[count];
                ReadExactly(bytes);
                return bytes;
            }

            internal uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));

            /// <summary>Reads past <paramref name="count"/> bytes; the stream ending first is damage.</summary>
            internal void Skip(long count) => new BoundedStream(this, count).Drain();

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>Exactly <c>length</c> bytes of the source: no more are read, and fewer is damage.</summary>
        private sealed class BoundedStream(Stream inner, long length) : Stream
        {
            private long _left = length;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_left == 0)
                {
                    return 0;
                }

                var read = inner.Read(buffer, offset, (int)Math.Min(count, _left));

                if (read <= 0)
                {
                    throw new EndOfStreamException();
                }

                _left -= read;
                return read;
            }

            internal void Drain()
            {
                var buffer = new byte[81920];

                while (Read(buffer, 0, buffer.Length) > 0)
                {
                }
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
