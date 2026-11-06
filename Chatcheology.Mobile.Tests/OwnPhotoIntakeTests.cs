using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Chatcheology.Archive;
using Chatcheology.Mobile.OwnMemories;
using Chatcheology.Mobile.Presentation;
using Chatcheology.Mobile.Synthetic;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>CH-54 step 1a: a photo is stored streamed, capped, named by its hash and without its location (D-37).</summary>
    public sealed class OwnPhotoIntakeTests : IDisposable
    {
        private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        private readonly TemporaryFolder _root = new();
        private readonly string _folder;

        public OwnPhotoIntakeTests()
        {
            _folder = OwnMemoriesDatabase.PhotosFolderIn(_root.Path);
        }

        public void Dispose() => _root.Dispose();

        [Fact]
        public async Task A_JPEG_with_a_location_is_stored_without_it()
        {
            var source = SyntheticJpeg.WithLocation();
            Assert.True(PhotoShare.HasGpsBlock(source));

            var result = await Add(new OwnPhotoIntake(_folder), source);

            Assert.Equal(OwnPhotoStatus.Stored, result.Status);
            var stored = File.ReadAllBytes(StoredPath(result));
            Assert.False(PhotoShare.HasGpsBlock(stored));
            Assert.False(PhotoShare.MayStillCarryLocation(new MemoryStream(stored), PhotoFormat.Jpeg, PhotoShareMethod.RemoveLocation));
            Assert.Equal(PhotoFormat.Jpeg, PhotoShare.Sniff(stored));
            Assert.Equal([0xFF, 0xD9], stored[^2..]);

            // The picture itself is the main image of the source, unchanged.
            Assert.Equal(MainImage(SyntheticJpeg.Plain()).Length - JfifLength(), stored.Length);
            Assert.Equal(Hash(stored) + ".jpg", result.FileName);
            Assert.Single(Directory.GetFiles(_folder));
        }

        [Fact]
        public async Task A_JPEG_keeps_its_orientation_and_nothing_else_of_its_Exif_block()
        {
            var source = WithExif(OrientationAndGpsTiff(orientation: 6));
            Assert.True(PhotoShare.HasGpsBlock(source));

            var result = await Add(new OwnPhotoIntake(_folder), source);

            Assert.Equal(OwnPhotoStatus.Stored, result.Status);
            var stored = File.ReadAllBytes(StoredPath(result));
            Assert.False(PhotoShare.HasGpsBlock(stored));
            Assert.Equal(6, PhotoShare.ReadOrientation(ExifOf(stored)));
        }

        [Fact]
        public async Task A_JPEG_without_an_orientation_is_stored_with_no_Exif_block()
        {
            var result = await Add(new OwnPhotoIntake(_folder), WithExif(OrientationAndGpsTiff(orientation: 1)));

            var stored = File.ReadAllBytes(StoredPath(result));
            Assert.Empty(ExifOf(stored));
            Assert.False(PhotoShare.HasGpsBlock(stored));
        }

        [Fact]
        public async Task Only_the_main_image_of_a_two_image_file_is_kept()
        {
            var result = await Add(new OwnPhotoIntake(_folder), SyntheticJpeg.TwoImagesLocationInSecond());

            var stored = File.ReadAllBytes(StoredPath(result));
            Assert.False(PhotoShare.HasGpsBlock(stored));
            Assert.Equal(1, Count(stored, [0xFF, 0xD8, 0xFF]));
        }

        [Fact]
        public async Task A_file_is_named_by_the_hash_of_its_stored_bytes_and_identical_bytes_are_one_file()
        {
            var gif = Gif(100);
            var intake = new OwnPhotoIntake(_folder);

            var first = await Add(intake, gif);
            var second = await Add(intake, gif);

            Assert.Equal(OwnPhotoStatus.Stored, first.Status);
            Assert.Equal(OwnPhotoStatus.AlreadyStored, second.Status);
            Assert.True(second.IsStored);
            Assert.Equal(Hash(gif) + ".gif", first.FileName);
            Assert.Equal(first.FileName, second.FileName);
            Assert.Equal(gif, File.ReadAllBytes(StoredPath(first)));
            Assert.Single(Directory.GetFiles(_folder));

            // Two JPEGs that differ only in their location are the same stored photo.
            var gpsOnly = await Add(intake, SyntheticJpeg.WithLocation());
            var located = await Add(intake, WithExif(OrientationAndGpsTiff(orientation: 1)));
            Assert.Equal(OwnPhotoStatus.Stored, gpsOnly.Status);
            Assert.Equal(gpsOnly.FileName, located.FileName);
            Assert.Equal(OwnPhotoStatus.AlreadyStored, located.Status);
        }

        [Fact]
        public async Task The_size_limit_is_20_MB_and_is_checked_while_streaming()
        {
            Assert.Equal(20 * 1024 * 1024, OwnPhotoIntake.MaxPhotoBytes);

            var intake = new OwnPhotoIntake(_folder, maxPhotoBytes: 1000);

            Assert.Equal(OwnPhotoStatus.Stored, (await Add(intake, Gif(1000))).Status);
            Assert.Equal(OwnPhotoStatus.TooLarge, (await Add(intake, Gif(1001))).Status);

            // A stream with no end and no length: refused after the limit, not read to the end.
            var endless = new EndlessStream();
            var refused = await intake.AddAsync(endless);
            Assert.Equal(OwnPhotoStatus.TooLarge, refused.Status);
            Assert.Null(refused.FileName);
            Assert.True(endless.Served < 1000 + 81920 * 2);

            Assert.Single(Directory.GetFiles(_folder));
        }

        [Fact]
        public async Task A_failed_read_or_a_cancel_leaves_no_partial_file()
        {
            var intake = new OwnPhotoIntake(_folder);

            await Assert.ThrowsAsync<IOException>(() => intake.AddAsync(new FailingStream(Gif(200_000), failAfter: 100_000)));
            Assert.Empty(Directory.GetFiles(_folder));

            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => intake.AddAsync(new MemoryStream(Gif(500)), cancel.Token));
            Assert.Empty(Directory.GetFiles(_folder));
        }

        [Fact]
        public async Task A_damaged_JPEG_or_an_unknown_format_is_refused_with_no_file_left()
        {
            var intake = new OwnPhotoIntake(_folder);
            var jpeg = SyntheticJpeg.Plain();

            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, jpeg[..(jpeg.Length / 2)])).Status);
            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, Heic())).Status);
            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, "not a picture"u8.ToArray())).Status);
            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, [])).Status);
            Assert.Empty(Directory.GetFiles(_folder));
        }

        [Fact]
        public async Task A_PNG_or_WebP_without_EXIF_is_kept_as_it_is_and_one_with_a_location_needs_the_platform()
        {
            var intake = new OwnPhotoIntake(_folder);
            var clean = Png(("IHDR", new byte[13]), ("IDAT", new byte[20]));
            var located = Png(("IHDR", new byte[13]), ("eXIf", SyntheticJpeg.GpsTiff()), ("IDAT", new byte[20]));
            var xmp = Png(("IHDR", new byte[13]), ("iTXt", Encoding.ASCII.GetBytes("XML:com.adobe.xmp\0\0\0\0\0<exif:GPSLatitude>1</exif:GPSLatitude>")), ("IDAT", new byte[20]));
            var webp = WebP(("VP8 ", new byte[10]), ("EXIF", [.. "Exif\0\0"u8, .. SyntheticJpeg.GpsTiff()]));

            var kept = await Add(intake, clean);
            Assert.Equal(OwnPhotoStatus.Stored, kept.Status);
            Assert.Equal(Hash(clean) + ".png", kept.FileName);
            Assert.Equal(clean, File.ReadAllBytes(StoredPath(kept)));

            Assert.Equal(OwnPhotoStatus.HasLocation, (await Add(intake, located)).Status);
            Assert.Equal(OwnPhotoStatus.HasLocation, (await Add(intake, xmp)).Status);
            Assert.Equal(OwnPhotoStatus.HasLocation, (await Add(intake, webp)).Status);
            Assert.Single(Directory.GetFiles(_folder));

            // With the platform's step (ExifInterface on the phone; a fake here), the cleaned copy is stored.
            var cleaned = Png(("IHDR", new byte[13]), ("IDAT", new byte[21]));
            var normaliser = new FakeNormaliser((_, format) => format == PhotoFormat.Png ? (PhotoFormat.Png, cleaned) : (null, null));
            var stored = await Add(new OwnPhotoIntake(_folder, normaliser), located);
            Assert.Equal(OwnPhotoStatus.Stored, stored.Status);
            Assert.Equal(Hash(cleaned) + ".png", stored.FileName);
            Assert.Equal(1, normaliser.Calls);
        }

        [Fact]
        public async Task A_HEIC_photo_is_converted_by_the_platform_and_its_JPEG_is_rebuilt_again()
        {
            // The platform hands back a JPEG that still has a location: it is rebuilt without it.
            var normaliser = new FakeNormaliser((_, _) => (PhotoFormat.Jpeg, SyntheticJpeg.WithLocation()));

            var result = await Add(new OwnPhotoIntake(_folder, normaliser), Heic());

            Assert.Equal(OwnPhotoStatus.Stored, result.Status);
            Assert.EndsWith(".jpg", result.FileName, StringComparison.Ordinal);
            Assert.False(PhotoShare.HasGpsBlock(File.ReadAllBytes(StoredPath(result))));
            Assert.Single(Directory.GetFiles(_folder));
        }

        [Fact]
        public async Task Whatever_the_platform_writes_is_checked_again_and_any_doubt_refuses()
        {
            var located = Png(("IHDR", new byte[13]), ("eXIf", SyntheticJpeg.GpsTiff()), ("IDAT", new byte[20]));
            var clean = Png(("IHDR", new byte[13]), ("IDAT", new byte[20]));

            // Its PNG still has the GPS block.
            Assert.Equal(
                OwnPhotoStatus.HasLocation,
                (await Add(new OwnPhotoIntake(_folder, new FakeNormaliser((_, _) => (PhotoFormat.Png, located))), Heic())).Status);

            // It says JPEG but writes a PNG.
            Assert.Equal(
                OwnPhotoStatus.NotSupported,
                (await Add(new OwnPhotoIntake(_folder, new FakeNormaliser((_, _) => (PhotoFormat.Jpeg, clean))), Heic())).Status);

            // It cannot.
            Assert.Equal(
                OwnPhotoStatus.NotSupported,
                (await Add(new OwnPhotoIntake(_folder, new FakeNormaliser((_, _) => (null, null))), Heic())).Status);
            Assert.Equal(
                OwnPhotoStatus.HasLocation,
                (await Add(new OwnPhotoIntake(_folder, new FakeNormaliser((_, _) => (null, null))), located)).Status);

            // Its copy is over the limit.
            Assert.Equal(
                OwnPhotoStatus.TooLarge,
                (await Add(new OwnPhotoIntake(_folder, new FakeNormaliser((_, _) => (PhotoFormat.Gif, Gif(2000))), maxPhotoBytes: 1500), Heic())).Status);

            Assert.Empty(Directory.GetFiles(_folder));
        }

        [Fact]
        public void Only_partial_files_of_the_intake_are_recognised_as_partial()
        {
            Assert.True(OwnPhotoIntake.IsIncomingFileName(".incoming-" + Guid.NewGuid().ToString("N") + ".tmp"));
            Assert.False(OwnPhotoIntake.IsIncomingFileName(".incoming-x.tmp"));
            Assert.False(OwnPhotoIntake.IsIncomingFileName("photo.tmp"));
            Assert.False(OwnPhotoIntake.IsIncomingFileName(null));
        }

        [Fact]
        public async Task A_stored_photo_can_be_used_by_a_memory()
        {
            var store = new OwnMemoryStore(_root.Path);
            var photo = await Add(new OwnPhotoIntake(store.PhotosFolder), SyntheticJpeg.WithLocation());

            var memory = store.Add(new OwnMemoryDraft
            {
                Title = "With a photo",
                Start = new DateOnly(2015, 8, 1),
                Precision = MemoryDatePrecision.Day,
                Photos = [new OwnPhotoDraft(photo.FileName!, "Cover")],
            });

            Assert.Equal(photo.FileName, Assert.Single(memory.Photos).FileName);
            Assert.True(File.Exists(memory.Photos[0].Path));
        }

        private static Task<OwnPhotoResult> Add(OwnPhotoIntake intake, byte[] bytes) => intake.AddAsync(new MemoryStream(bytes));

        private string StoredPath(OwnPhotoResult result)
        {
            Assert.NotNull(result.FileName);
            return Path.Combine(_folder, result.FileName);
        }

        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        private static byte[] MainImage(byte[] source)
        {
            using var target = new MemoryStream();
            Assert.True(PhotoShare.WriteMainJpeg(source, target));
            return target.ToArray();
        }

        /// <summary>The plain picture's JFIF block, which <see cref="SyntheticJpeg.WithLocation"/> replaces.</summary>
        private static int JfifLength()
        {
            var plain = SyntheticJpeg.Plain();
            return 2 + ((plain[4] << 8) | plain[5]);
        }

        /// <summary>The plain picture with its JFIF block replaced by an APP1 Exif block holding <paramref name="tiff"/>.</summary>
        private static byte[] WithExif(byte[] tiff)
        {
            var plain = SyntheticJpeg.Plain();
            var length = 2 + 6 + tiff.Length;
            return [0xFF, 0xD8, 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. "Exif\0\0"u8, .. tiff, .. plain[(2 + JfifLength())..]];
        }

        /// <summary>The TIFF of the first APP1 Exif block, or empty when there is none.</summary>
        private static byte[] ExifOf(byte[] jpeg)
        {
            var position = 2;

            while (position + 4 <= jpeg.Length && jpeg[position] == 0xFF && jpeg[position + 1] is >= 0xE0 and <= 0xEF or 0xDB or 0xC0 or 0xC4)
            {
                var length = (jpeg[position + 2] << 8) | jpeg[position + 3];

                if (jpeg[position + 1] == 0xE1 && jpeg.AsSpan(position + 4).StartsWith("Exif\0\0"u8))
                {
                    return jpeg[(position + 10)..(position + 2 + length)];
                }

                position += 2 + length;
            }

            return [];
        }

        /// <summary>A big-endian TIFF whose IFD0 holds an orientation and a GPS pointer to a one-entry GPS directory.</summary>
        private static byte[] OrientationAndGpsTiff(ushort orientation)
        {
            var tiff = new byte[8 + 2 + 24 + 4 + 2 + 12 + 4];
            var t = tiff.AsSpan();
            "MM"u8.CopyTo(t);
            BinaryPrimitives.WriteUInt16BigEndian(t[2..], 42);
            BinaryPrimitives.WriteUInt32BigEndian(t[4..], 8);
            BinaryPrimitives.WriteUInt16BigEndian(t[8..], 2);
            BinaryPrimitives.WriteUInt16BigEndian(t[10..], 0x0112);
            BinaryPrimitives.WriteUInt16BigEndian(t[12..], 3);
            BinaryPrimitives.WriteUInt32BigEndian(t[14..], 1);
            BinaryPrimitives.WriteUInt16BigEndian(t[18..], orientation);
            BinaryPrimitives.WriteUInt16BigEndian(t[22..], 0x8825);
            BinaryPrimitives.WriteUInt16BigEndian(t[24..], 4);
            BinaryPrimitives.WriteUInt32BigEndian(t[26..], 1);
            BinaryPrimitives.WriteUInt32BigEndian(t[30..], 38);
            BinaryPrimitives.WriteUInt32BigEndian(t[34..], 0);
            BinaryPrimitives.WriteUInt16BigEndian(t[38..], 1);
            BinaryPrimitives.WriteUInt16BigEndian(t[40..], 0x0001);
            BinaryPrimitives.WriteUInt16BigEndian(t[42..], 2);
            BinaryPrimitives.WriteUInt32BigEndian(t[44..], 2);
            BinaryPrimitives.WriteUInt32BigEndian(t[48..], (uint)'N' << 24);
            return tiff;
        }

        private static int Count(byte[] data, byte[] pattern)
        {
            var count = 0;

            for (var i = 0; i + pattern.Length <= data.Length; i++)
            {
                if (data.AsSpan(i).StartsWith(pattern))
                {
                    count++;
                }
            }

            return count;
        }

        private static byte[] Gif(int length)
        {
            var bytes = new byte[length];
            "GIF89a"u8.CopyTo(bytes);

            for (var i = 6; i < length; i++)
            {
                bytes[i] = (byte)(i * 7);
            }

            return bytes;
        }

        /// <summary>An ISO box header as HEIC files start, then filler: not a format the pure layer stores.</summary>
        private static byte[] Heic() => [0, 0, 0, 0x18, .. "ftypheic"u8, .. new byte[200]];

        private static byte[] Png(params (string Type, byte[] Data)[] chunks)
        {
            var bytes = new List<byte>(PngSignature);

            foreach (var (type, data) in chunks.Append(("IEND", [])))
            {
                var head = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(head, (uint)data.Length);
                bytes.AddRange(head);
                bytes.AddRange(Encoding.ASCII.GetBytes(type));
                bytes.AddRange(data);
                bytes.AddRange(new byte[4]);
            }

            return [.. bytes];
        }

        private static byte[] WebP(params (string Type, byte[] Data)[] chunks)
        {
            var body = new List<byte>(Encoding.ASCII.GetBytes("WEBP"));

            foreach (var (type, data) in chunks)
            {
                body.AddRange(Encoding.ASCII.GetBytes(type));
                var size = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
                body.AddRange(size);
                body.AddRange(data);

                if (data.Length % 2 == 1)
                {
                    body.Add(0);
                }
            }

            var riff = new byte[8];
            "RIFF"u8.CopyTo(riff);
            BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), (uint)body.Count);
            return [.. riff, .. body];
        }

        /// <summary>A stand-in for the Android step: writes what the test says.</summary>
        private sealed class FakeNormaliser(Func<string, PhotoFormat, (PhotoFormat? Format, byte[]? Bytes)> write) : IPhotoNormaliser
        {
            public int Calls { get; private set; }

            public PhotoFormat? WriteWithoutLocation(string sourcePath, PhotoFormat format, string targetPath)
            {
                Calls++;
                Assert.True(File.Exists(sourcePath));
                Assert.False(File.Exists(targetPath));

                var (written, bytes) = write(sourcePath, format);

                if (bytes is not null)
                {
                    File.WriteAllBytes(targetPath, bytes);
                }

                return written;
            }
        }

        /// <summary>A stream of GIF-looking bytes with no end and no length.</summary>
        private sealed class EndlessStream : Stream
        {
            public long Served { get; private set; }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                buffer.AsSpan(offset, count).Fill(0x47);
                Served += count;
                return count;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>A stream that fails part-way, as a picker stream can when the source goes away.</summary>
        private sealed class FailingStream(byte[] data, int failAfter) : Stream
        {
            private int _position;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= failAfter)
                {
                    throw new IOException("The source went away.");
                }

                var n = Math.Min(count, Math.Min(data.Length, failAfter) - _position);
                data.AsSpan(_position, n).CopyTo(buffer.AsSpan(offset));
                _position += n;
                return n;
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
