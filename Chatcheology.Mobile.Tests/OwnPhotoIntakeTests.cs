using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Chatcheology.Archive;
using Chatcheology.Mobile.OwnMemories;
using Chatcheology.Mobile.Presentation;
using Chatcheology.Mobile.Synthetic;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// CH-54 step 1a: a photo is stored streamed, capped, named by its hash and rebuilt from an allow-list, so it has
    /// no location (D-37). Synthetic data only; the coordinates and place names are invented.
    /// </summary>
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

        // ---- JPEG ----

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
        public async Task A_JPEG_keeps_only_the_segments_of_its_allow_list_and_the_JFIF_header_without_a_thumbnail()
        {
            var plain = SyntheticJpeg.Plain();
            var jfif = plain.AsSpan(6, JfifLength() - 4).ToArray();
            var thumbnail = (byte[])[.. jfif[..12], 1, 1, 9, 9, 9, .. "GpsLatitude=12.5"u8];
            var drone = Encoding.ASCII.GetBytes("drone-dji:GpsLatitude=\"+12.582\"");
            byte[] source =
            [
                0xFF, 0xD8,
                .. Segment(0xE0, thumbnail),
                .. Segment(0xF0, drone),
                .. Segment(0xEE, [.. "Adobe"u8, 0, 100, 0, 0, 0, 0, 1]),
                .. Segment(0xE2, [.. "ICC_PROFILE\0"u8, 1, 1, .. new byte[40]]),
                .. plain[(2 + JfifLength())..],
            ];

            var result = await Add(new OwnPhotoIntake(_folder), source);

            Assert.Equal(OwnPhotoStatus.Stored, result.Status);
            var stored = File.ReadAllBytes(StoredPath(result));
            Assert.Equal(0, Count(stored, "GpsLatitude"u8.ToArray()));
            Assert.Equal(0, Count(stored, [0xFF, 0xF0]));
            Assert.Equal([0xFF, 0xD8, 0xFF, 0xE0, 0, 16, .. jfif[..12], 0, 0], stored[..20]);

            // CH-59's Adobe (exact 12 bytes) and ICC blocks are kept.
            Assert.Equal(1, Count(stored, "Adobe"u8.ToArray()));
            Assert.Equal(1, Count(stored, "ICC_PROFILE"u8.ToArray()));

            // The CH-59 share rebuild of an archive photo is not changed: it still copies the JPGn segment.
            Assert.Equal(1, Count(MainImage(source), [0xFF, 0xF0]));
        }

        [Fact]
        public async Task A_rebuilt_JPEG_over_the_limit_is_too_large()
        {
            // A 22-byte Exif TIFF (no next-directory word): the stored one is written in full and is 4 bytes longer.
            var tiff = new byte[22];
            "MM"u8.CopyTo(tiff);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2), 42);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10), 0x0112);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12), 3);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14), 1);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(18), 6);
            var source = WithExif(tiff);

            var result = await Add(new OwnPhotoIntake(_folder, maxPhotoBytes: source.Length), source);

            Assert.Equal(OwnPhotoStatus.TooLarge, result.Status);
            Assert.Empty(Directory.GetFiles(_folder));
        }

        // ---- The review's M1 cases: a location the detection does not know is still never stored ----

        [Fact]
        public async Task A_raw_Exif_profile_in_PNG_text_is_left_out()
        {
            var hex = Convert.ToHexStringLower([.. "Exif\0\0"u8, .. SyntheticJpeg.GpsTiff()]);
            var text = (byte[])[.. "Raw profile type exif\0"u8, .. Encoding.ASCII.GetBytes("\nexif\n" + (hex.Length / 2).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(8) + "\n" + hex + "\n")];
            var source = Png(("IHDR", Ihdr()), ("tEXt", text), ("IDAT", Idat()));

            var stored = await StoredBytes(source, OwnPhotoStatus.Stored);

            Assert.Equal(Png(("IHDR", Ihdr()), ("IDAT", Idat())), stored);
            Assert.Equal(0, Count(stored, Encoding.ASCII.GetBytes(hex[..40])));
        }

        [Fact]
        public async Task A_compressed_Exif_chunk_in_PNG_is_left_out()
        {
            var zxif = (byte[])[.. "zxIf"u8, 0, .. Deflate([.. "Exif\0\0"u8, .. SyntheticJpeg.GpsTiff()])];
            var source = Png(("IHDR", Ihdr()), ("zxIf", zxif), ("IDAT", Idat()));

            var stored = await StoredBytes(source, OwnPhotoStatus.Stored);

            Assert.Equal(Png(("IHDR", Ihdr()), ("IDAT", Idat())), stored);
        }

        [Fact]
        public async Task A_place_name_in_WebP_XMP_is_left_out()
        {
            var xmp = Encoding.UTF8.GetBytes("<x:xmpmeta><photoshop:City>Invented Harbour</photoshop:City></x:xmpmeta>");
            var source = WebP(("VP8X", Vp8x(0x04)), ("VP8 ", Vp8()), ("XMP ", xmp));

            var stored = await StoredBytes(source, OwnPhotoStatus.Stored);

            Assert.Equal(WebP(("VP8X", Vp8x(0x00)), ("VP8 ", Vp8())), stored);
            Assert.Equal(0, Count(stored, "Invented Harbour"u8.ToArray()));
        }

        [Fact]
        public async Task Coordinates_in_a_GIF_comment_or_UTF16_XMP_are_left_out()
        {
            var comment = Extension(0xFE, "Taken at 12.5823 N, 65.7225 E"u8.ToArray());
            var xmp = Extension(0xFF, [.. "XMP DataXMP"u8], Encoding.Unicode.GetBytes("<exif:GPSLatitude>12,34.9N</exif:GPSLatitude>"));
            var source = Gif(comment, xmp, Image());

            var stored = await StoredBytes(source, OwnPhotoStatus.Stored);

            Assert.Equal(Gif(Image()), stored);
        }

        [Fact]
        public async Task Bytes_after_the_end_of_a_PNG_GIF_WebP_or_BMP_are_left_out()
        {
            var tail = "GPS 12.5823 65.7225"u8.ToArray();
            var png = Png(("IHDR", Ihdr()), ("IDAT", Idat()));
            var gif = Gif(Image());
            var webp = WebP(("VP8 ", Vp8()));
            var bmp = Bmp();

            Assert.Equal(png, await StoredBytes([.. png, .. tail], OwnPhotoStatus.Stored));
            Assert.Equal(gif, await StoredBytes([.. gif, .. tail], OwnPhotoStatus.Stored));
            Assert.Equal(webp, await StoredBytes([.. webp, .. tail], OwnPhotoStatus.Stored));
            Assert.Equal(bmp, await StoredBytes([.. bmp, .. tail], OwnPhotoStatus.Stored));
        }

        // ---- Each format keeps its allowed parts, byte for byte ----

        [Fact]
        public async Task A_PNG_keeps_its_image_and_colour_chunks_unchanged()
        {
            (string, byte[])[] allowed =
            [
                ("IHDR", Ihdr()), ("acTL", new byte[8]), ("PLTE", new byte[6]), ("tRNS", [0, 255]), ("gAMA", [0, 0, 177, 143]),
                ("cHRM", new byte[32]), ("sRGB", [0]), ("iCCP", [.. "icc\0"u8, 0, 1, 2, 3]), ("sBIT", [8, 8, 8]),
                ("bKGD", [0, 0]), ("pHYs", new byte[9]), ("cICP", [1, 13, 0, 1]), ("fcTL", new byte[26]), ("IDAT", Idat()),
                ("fcTL", new byte[26]), ("fdAT", [0, 0, 0, 2, .. Idat()]),
            ];
            (string, byte[])[] dropped =
            [
                ("tEXt", "Comment\0hello"u8.ToArray()), ("iTXt", "XML:com.adobe.xmp\0\0\0\0\0<x/>"u8.ToArray()),
                ("zTXt", "Comment\0\0x"u8.ToArray()), ("eXIf", SyntheticJpeg.GpsTiff()), ("tIME", new byte[7]), ("prVt", new byte[5]),
            ];
            var source = Png([.. allowed[..2], .. dropped[..3], .. allowed[2..], .. dropped[3..]]);

            var stored = await StoredBytes(source, OwnPhotoStatus.Stored);

            // The iCCP chunk keeps its compression method and profile, under the fixed name, with its CRC recomputed (S5).
            var expected = allowed.Select(chunk => chunk.Item1 == "iCCP" ? ("iCCP", (byte[])[.. "ICC profile\0"u8, 0, 1, 2, 3]) : chunk).ToArray();
            Assert.Equal(WithCrc(Png(expected), "iCCP"), stored);
        }

        // ---- Step 1a re-review S5: exact forms only, and no free text in kept parts ----

        [Fact]
        public void The_PNG_CRC_is_the_standard_one()
        {
            // The CRC of an empty IEND chunk, as every PNG ends.
            Assert.Equal(0xAE426082u, PhotoRebuild.Crc32("IEND"u8));
        }

        [Fact]
        public async Task A_PNG_colour_profile_keeps_its_profile_under_a_fixed_name_and_a_nameless_one_is_refused()
        {
            var profile = Deflate(new byte[64]);
            var named = Png(("IHDR", Ihdr()), ("iCCP", [.. "Taken at 12.58 N 65.72 E\0"u8, 0, .. profile]), ("IDAT", Idat()));

            var stored = await StoredBytes(named, OwnPhotoStatus.Stored);

            Assert.Equal(0, Count(stored, "12.58"u8.ToArray()));
            Assert.Equal(
                WithCrc(Png(("IHDR", Ihdr()), ("iCCP", [.. "ICC profile\0"u8, 0, .. profile]), ("IDAT", Idat())), "iCCP"),
                stored);

            var intake = new OwnPhotoIntake(_folder);
            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, Png(("IHDR", Ihdr()), ("iCCP", "no name ending"u8.ToArray()), ("IDAT", Idat())))).Status);
            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, Png(("IHDR", Ihdr()), ("iCCP", [0, 0, 1]), ("IDAT", Idat())))).Status);
            Assert.Empty(Directory.GetFiles(_folder));
        }

        [Fact]
        public async Task A_GIF_graphic_control_extension_not_in_its_exact_form_is_refused()
        {
            var intake = new OwnPhotoIntake(_folder);
            var longBlock = Extension(0xF9, [0, 10, 0, 0, .. "12.58N"u8]);
            var extraBlock = Extension(0xF9, [0, 10, 0, 0], "12.58N"u8.ToArray());
            var shortBlock = Extension(0xF9, [0, 10, 0]);

            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, Gif(longBlock, Image()))).Status);
            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, Gif(extraBlock, Image()))).Status);
            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, Gif(shortBlock, Image()))).Status);
            Assert.Empty(Directory.GetFiles(_folder));

            var exact = Gif(Extension(0xF9, [0, 10, 0, 0]), Image());
            Assert.Equal(exact, await StoredBytes(exact, OwnPhotoStatus.Stored));
        }

        [Fact]
        public async Task A_WebP_VP8X_chunk_of_any_size_but_10_bytes_is_refused()
        {
            var intake = new OwnPhotoIntake(_folder);

            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, WebP(("VP8X", [.. Vp8x(0), .. "12.58N"u8]), ("VP8 ", Vp8())))).Status);
            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, WebP(("VP8X", Vp8x(0)[..9]), ("VP8 ", Vp8())))).Status);
            Assert.Empty(Directory.GetFiles(_folder));
        }

        [Fact]
        public async Task A_JPEG_restart_interval_not_of_exactly_4_bytes_is_refused_and_one_of_4_is_kept()
        {
            var plain = SyntheticJpeg.Plain();
            byte[] With(byte[] dri) => [.. plain[..(2 + JfifLength())], .. Segment(0xDD, dri), .. plain[(2 + JfifLength())..]];
            var intake = new OwnPhotoIntake(_folder);

            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, With([0, 4, .. "12.58N"u8]))).Status);
            Assert.Empty(Directory.GetFiles(_folder));

            var kept = await Add(intake, With([0, 4]));
            Assert.Equal(OwnPhotoStatus.Stored, kept.Status);
            Assert.Equal(1, Count(File.ReadAllBytes(StoredPath(kept)), [0xFF, 0xDD, 0, 4, 0, 4]));

            // The CH-59 share rebuild is not changed: it still copies a longer DRI.
            Assert.Equal(1, Count(MainImage(With([0, 4, .. "12.58N"u8])), "12.58N"u8.ToArray()));
        }

        [Fact]
        public async Task A_BMP_gap_between_headers_and_pixels_is_zeroed_and_its_palette_and_pixels_kept()
        {
            // 8 bits per pixel, two palette colours, then 6 gap bytes of text before the pixels.
            var bmp = Bmp(bitsPerPixel: 8, coloursUsed: 2, gap: "12.58N"u8.ToArray());
            var stored = await StoredBytes(bmp, OwnPhotoStatus.Stored);

            var expected = (byte[])bmp.Clone();
            Array.Clear(expected, 54 + 8, 6);
            Assert.Equal(expected, stored);
            Assert.Equal(bmp[54..62], stored[54..62]);
            Assert.Equal(0, Count(stored, "12.58N"u8.ToArray()));
        }

        [Fact]
        public async Task A_BMP_with_BI_BITFIELDS_after_a_40_byte_header_keeps_its_12_mask_bytes_and_zeroes_only_the_gap()
        {
            // 1b review S4: zeroed masks would garble a 16/32-bit picture.
            var masks = (byte[])[0x00, 0xF8, 0, 0, 0xE0, 0x07, 0, 0, 0x1F, 0, 0, 0];
            var bmp = BmpWithHeader(40, bitsPerPixel: 16, compression: 3, tables: masks, gap: "12.58N"u8.ToArray());

            var stored = await StoredBytes(bmp, OwnPhotoStatus.Stored);

            Assert.Equal(masks, stored[54..66]);
            Assert.Equal(new byte[6], stored[66..72]);
            Assert.Equal(bmp[72..], stored[72..]);
            Assert.Equal(bmp[..54], stored[..54]);
        }

        [Fact]
        public async Task A_BMP_with_BI_ALPHABITFIELDS_after_a_40_byte_header_keeps_its_16_mask_bytes()
        {
            var masks = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
            var bmp = BmpWithHeader(40, bitsPerPixel: 32, compression: 6, tables: masks, gap: "12.58N"u8.ToArray());

            var stored = await StoredBytes(bmp, OwnPhotoStatus.Stored);

            Assert.Equal(masks, stored[54..70]);
            Assert.Equal(new byte[6], stored[70..76]);
        }

        [Fact]
        public async Task An_OS2_BMP_keeps_its_3_byte_palette_and_zeroes_the_gap_after_it()
        {
            // 12-byte core header, 1 bit per pixel: two 3-byte palette entries.
            var palette = (byte[])[0x10, 0x20, 0x30, 0x40, 0x50, 0x60];
            var bmp = BmpWithHeader(12, bitsPerPixel: 1, compression: 0, tables: palette, gap: "12.58N"u8.ToArray());

            var stored = await StoredBytes(bmp, OwnPhotoStatus.Stored);

            Assert.Equal(bmp[..26], stored[..26]);
            Assert.Equal(palette, stored[26..32]);
            Assert.Equal(new byte[6], stored[32..38]);
            Assert.Equal(bmp[38..], stored[38..]);
        }

        [Fact]
        public async Task A_V4_BMP_keeps_its_header_masks_and_palette_and_zeroes_the_gap()
        {
            // V4 (108 bytes): the masks are inside the header, so only the palette follows it.
            var palette = Enumerable.Range(0, 8).Select(i => (byte)(0x80 + i)).ToArray();
            var bmp = BmpWithHeader(108, bitsPerPixel: 1, compression: 0, tables: palette, gap: "12.58N"u8.ToArray());

            var stored = await StoredBytes(bmp, OwnPhotoStatus.Stored);

            Assert.Equal(bmp[..122], stored[..122]);
            Assert.Equal(palette, stored[122..130]);
            Assert.Equal(new byte[6], stored[130..136]);
            Assert.Equal(bmp[136..], stored[136..]);
        }

        [Fact]
        public async Task A_BMP_V5_embedded_profile_in_the_gap_is_kept_and_the_rest_of_the_gap_zeroed()
        {
            var profile = "ICC-PROFILE-BYTES"u8.ToArray();
            var bmp = BmpV5WithProfile(profile, before: "12.58N"u8.ToArray());

            var stored = await StoredBytes(bmp, OwnPhotoStatus.Stored);

            Assert.Equal(1, Count(stored, profile));
            Assert.Equal(0, Count(stored, "12.58N"u8.ToArray()));
            Assert.Equal(bmp.Length, stored.Length);
        }

        [Fact]
        public async Task A_WebP_keeps_its_image_chunks_and_loses_its_metadata_and_their_flags()
        {
            var frame = (byte[])[.. new byte[16], .. RiffChunk("ALPH", [1, 2, 3]), .. RiffChunk("VP8 ", Vp8()), .. RiffChunk("XMP ", "<x/>"u8.ToArray())];
            var keptFrame = (byte[])[.. new byte[16], .. RiffChunk("ALPH", [1, 2, 3]), .. RiffChunk("VP8 ", Vp8())];
            var source = WebP(
                ("VP8X", Vp8x(0x20 | 0x10 | 0x08 | 0x04 | 0x02)),
                ("ICCP", [9, 9, 9]),
                ("ANIM", new byte[6]),
                ("ANMF", frame),
                ("EXIF", [.. "Exif\0\0"u8, .. SyntheticJpeg.GpsTiff()]),
                ("XMP ", "<x/>"u8.ToArray()),
                ("ZZZZ", [7]));

            var stored = await StoredBytes(source, OwnPhotoStatus.Stored);

            Assert.Equal(
                WebP(("VP8X", Vp8x(0x20 | 0x10 | 0x02)), ("ICCP", [9, 9, 9]), ("ANIM", new byte[6]), ("ANMF", keptFrame)),
                stored);
            Assert.Equal((uint)(stored.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(stored.AsSpan(4)));
        }

        [Fact]
        public async Task A_GIF_keeps_its_images_graphic_control_and_loop_count_only()
        {
            var control = Extension(0xF9, [0, 10, 0, 0]);
            var loop = Extension(0xFF, [.. "NETSCAPE2.0"u8], [1, 0, 0]);
            var longLoop = Extension(0xFF, [.. "NETSCAPE2.0"u8], [1, 0, 0, .. "hidden"u8]);
            var plainText = Extension(0x01, new byte[12], "12.58 N"u8.ToArray());
            var other = Extension(0xFF, [.. "ICCRGBG1012"u8], new byte[20]);
            var comment = Extension(0xFE, "note"u8.ToArray());

            var source = Gif(loop, comment, control, Image(localTable: true), plainText, other, longLoop, control, Image());

            var stored = await StoredBytes(source, OwnPhotoStatus.Stored);

            Assert.Equal(Gif(loop, control, Image(localTable: true), control, Image()), stored);
        }

        [Fact]
        public async Task A_BMP_is_cut_to_its_size_and_one_whose_headers_disagree_is_refused()
        {
            var bmp = Bmp();
            var intake = new OwnPhotoIntake(_folder);

            Assert.Equal(bmp, await StoredBytes([.. bmp, 1, 2, 3], OwnPhotoStatus.Stored));

            var tooBig = (byte[])bmp.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(tooBig.AsSpan(2), (uint)bmp.Length + 10);
            var badOffset = (byte[])bmp.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(badOffset.AsSpan(10), 20);

            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, tooBig)).Status);
            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, badOffset)).Status);
            Assert.Empty(Directory.GetFiles(_folder));
        }

        // ---- Naming, size, failure ----

        [Fact]
        public async Task A_file_is_named_by_the_hash_of_its_stored_bytes_and_identical_bytes_are_one_file()
        {
            var gif = Gif(Image());
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

            var exact = Gif(Image(dataLength: 900));
            var over = Gif(Image(dataLength: 901));
            var intake = new OwnPhotoIntake(_folder, maxPhotoBytes: exact.Length);

            Assert.Equal(OwnPhotoStatus.Stored, (await Add(intake, exact)).Status);
            Assert.Equal(OwnPhotoStatus.TooLarge, (await Add(intake, over)).Status);

            // A stream with no end and no length: refused after the limit, not read to the end.
            var endless = new EndlessStream();
            var refused = await intake.AddAsync(endless);
            Assert.Equal(OwnPhotoStatus.TooLarge, refused.Status);
            Assert.Null(refused.FileName);
            Assert.True(endless.Served < exact.Length + 81920 * 2);

            Assert.Single(Directory.GetFiles(_folder));
        }

        [Fact]
        public async Task A_failed_read_or_a_cancel_before_or_during_the_copy_leaves_no_partial_file()
        {
            var intake = new OwnPhotoIntake(_folder);
            var big = Gif(Image(dataLength: 200_000));

            await Assert.ThrowsAsync<IOException>(() => intake.AddAsync(new FailingStream(big, failAfter: 100_000)));
            Assert.Empty(Directory.GetFiles(_folder));

            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => intake.AddAsync(new MemoryStream(big), cancel.Token));
                Assert.Empty(Directory.GetFiles(_folder));
            }

            using (var cancel = new CancellationTokenSource())
            {
                // Cancelled after the first block has been written to the partial file.
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => intake.AddAsync(new CancellingStream(big, cancel), cancel.Token));
                Assert.Empty(Directory.GetFiles(_folder));
            }
        }

        [Fact]
        public async Task A_damaged_photo_or_an_unknown_format_is_refused_as_not_supported_with_no_file_left()
        {
            var intake = new OwnPhotoIntake(_folder);
            var jpeg = SyntheticJpeg.Plain();
            var png = Png(("IHDR", Ihdr()), ("IDAT", Idat()));
            var webp = WebP(("VP8 ", Vp8()));

            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, jpeg[..(jpeg.Length / 2)])).Status);
            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, png[..^6])).Status);
            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, Png(("IDAT", Idat()), ("IHDR", Ihdr())))).Status);
            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, webp[..^3])).Status);
            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, WebP(("XMP ", "<x/>"u8.ToArray())))).Status);
            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, Gif(Image())[..^2])).Status);
            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, Heic())).Status);
            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, "not a picture"u8.ToArray())).Status);
            Assert.Equal(OwnPhotoStatus.NotSupported, (await Add(intake, [])).Status);
            Assert.Empty(Directory.GetFiles(_folder));
        }

        [Fact]
        public async Task The_CH59_check_stays_as_a_second_line()
        {
            // An ICC profile is kept (colour), so location text inside one is only caught by the re-check.
            byte[] source =
            [
                0xFF, 0xD8,
                .. Segment(0xE2, [.. "ICC_PROFILE\0"u8, 1, 1, .. "<exif:GPSLatitude>1</exif:GPSLatitude>"u8]),
                .. SyntheticJpeg.Plain()[(2 + JfifLength())..],
            ];

            Assert.Equal(OwnPhotoStatus.HasLocation, (await Add(new OwnPhotoIntake(_folder), source)).Status);
            Assert.Empty(Directory.GetFiles(_folder));
        }

        // ---- The platform's step ----

        [Fact]
        public async Task The_platform_is_asked_only_when_the_rebuild_cannot_read_the_photo()
        {
            var normaliser = new FakeNormaliser((_, _) => (null, null));
            var located = Png(("IHDR", Ihdr()), ("eXIf", SyntheticJpeg.GpsTiff()), ("IDAT", Idat()));

            var result = await Add(new OwnPhotoIntake(_folder, normaliser), located);

            Assert.Equal(OwnPhotoStatus.Stored, result.Status);
            Assert.Equal(0, normaliser.Calls);
        }

        [Fact]
        public async Task A_HEIC_photo_is_converted_by_the_platform_and_its_output_is_rebuilt_again()
        {
            // The platform hands back a JPEG that still has a location: it is rebuilt without it.
            var jpeg = await Add(new OwnPhotoIntake(_folder, new FakeNormaliser((_, _) => (PhotoFormat.Jpeg, SyntheticJpeg.WithLocation()))), Heic());

            Assert.Equal(OwnPhotoStatus.Stored, jpeg.Status);
            Assert.EndsWith(".jpg", jpeg.FileName, StringComparison.Ordinal);
            Assert.False(PhotoShare.HasGpsBlock(File.ReadAllBytes(StoredPath(jpeg))));

            // A PNG with a GPS-free eXIf chunk (as ExifInterface leaves one): the chunk is left out all the same.
            var withExif = Png(("IHDR", Ihdr()), ("eXIf", NoGpsTiff()), ("IDAT", Idat()));
            var png = await Add(new OwnPhotoIntake(_folder, new FakeNormaliser((_, _) => (PhotoFormat.Png, withExif))), Heic());

            Assert.Equal(OwnPhotoStatus.Stored, png.Status);
            Assert.Equal(Png(("IHDR", Ihdr()), ("IDAT", Idat())), File.ReadAllBytes(StoredPath(png)));
            Assert.Equal(2, Directory.GetFiles(_folder).Length);
        }

        [Fact]
        public async Task Whatever_the_platform_writes_is_checked_and_any_doubt_refuses()
        {
            var clean = Png(("IHDR", Ihdr()), ("IDAT", Idat()));

            // It says JPEG but writes a PNG.
            Assert.Equal(
                OwnPhotoStatus.NotSupported,
                (await Add(new OwnPhotoIntake(_folder, new FakeNormaliser((_, _) => (PhotoFormat.Jpeg, clean))), Heic())).Status);

            // It writes something the rebuild cannot read.
            Assert.Equal(
                OwnPhotoStatus.NotSupported,
                (await Add(new OwnPhotoIntake(_folder, new FakeNormaliser((_, _) => (PhotoFormat.Png, clean[..^4]))), Heic())).Status);

            // It cannot.
            Assert.Equal(
                OwnPhotoStatus.NotSupported,
                (await Add(new OwnPhotoIntake(_folder, new FakeNormaliser((_, _) => (null, null))), Heic())).Status);

            // Its copy is over the limit.
            Assert.Equal(
                OwnPhotoStatus.TooLarge,
                (await Add(new OwnPhotoIntake(_folder, new FakeNormaliser((_, _) => (PhotoFormat.Gif, Gif(Image(dataLength: 2000)))), maxPhotoBytes: 1500), Heic())).Status);

            Assert.Empty(Directory.GetFiles(_folder));
        }

        // ---- Partial files ----

        [Fact]
        public void Only_partial_files_of_the_intake_are_recognised_as_partial()
        {
            Assert.True(OwnPhotoIntake.IsIncomingFileName(".incoming-" + Guid.NewGuid().ToString("N") + ".tmp"));
            Assert.False(OwnPhotoIntake.IsIncomingFileName(".incoming-x.tmp"));
            Assert.False(OwnPhotoIntake.IsIncomingFileName("photo.tmp"));
            Assert.False(OwnPhotoIntake.IsIncomingFileName(null));
        }

        [Fact]
        public async Task Partial_files_left_by_a_killed_add_are_swept_at_first_use_when_old()
        {
            Directory.CreateDirectory(_folder);
            var old = Path.Combine(_folder, ".incoming-" + Guid.NewGuid().ToString("N") + ".tmp");
            var recent = Path.Combine(_folder, ".incoming-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(old, SyntheticJpeg.WithLocation());
            File.WriteAllBytes(recent, SyntheticJpeg.WithLocation());
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-11));

            await Add(new OwnPhotoIntake(_folder), Gif(Image()));

            Assert.False(File.Exists(old));
            Assert.True(File.Exists(recent));
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

        // ---- Helpers ----

        private static Task<OwnPhotoResult> Add(OwnPhotoIntake intake, byte[] bytes) => intake.AddAsync(new MemoryStream(bytes));

        /// <summary>Adds the photo in a fresh folder and returns the stored bytes (also checks the name is their hash).</summary>
        private async Task<byte[]> StoredBytes(byte[] source, OwnPhotoStatus expected)
        {
            var folder = Path.Combine(_root.Path, "case-" + Guid.NewGuid().ToString("N"));
            var result = await new OwnPhotoIntake(folder).AddAsync(new MemoryStream(source));

            Assert.Equal(expected, result.Status);
            var bytes = File.ReadAllBytes(Path.Combine(folder, result.FileName!));
            Assert.StartsWith(Hash(bytes) + ".", result.FileName, StringComparison.Ordinal);
            Assert.Single(Directory.GetFiles(folder));
            return bytes;
        }

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

        private static byte[] Segment(byte marker, byte[] data)
        {
            var length = data.Length + 2;
            return [0xFF, marker, (byte)(length >> 8), (byte)length, .. data];
        }

        /// <summary>The plain picture's JFIF block (marker, length and data), which <see cref="SyntheticJpeg.WithLocation"/> replaces.</summary>
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

        /// <summary>A big-endian TIFF block with one IFD0 entry (orientation) and no GPS pointer.</summary>
        private static byte[] NoGpsTiff()
        {
            var tiff = new byte[26];
            "MM"u8.CopyTo(tiff);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2), 42);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10), 0x0112);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12), 3);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14), 1);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(18), 0x00010000);
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

        private static byte[] Deflate(byte[] data)
        {
            using var output = new MemoryStream();

            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
            {
                zlib.Write(data);
            }

            return output.ToArray();
        }

        /// <summary>An ISO box header as HEIC files start, then filler: not a format the pure layer stores.</summary>
        private static byte[] Heic() => [0, 0, 0, 0x18, .. "ftypheic"u8, .. new byte[200]];

        private static byte[] Ihdr() => [0, 0, 0, 2, 0, 0, 0, 2, 8, 2, 0, 0, 0];

        private static byte[] Idat() => [0x78, 0x9C, 1, 2, 3, 4, 5, 6, 7];

        /// <summary>A PNG of the chunks given, then IEND. CRCs are zeros: the rebuild copies them as they are.</summary>
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

        /// <summary><paramref name="png"/> with the CRC of its first <paramref name="type"/> chunk computed (the helper writes zeros).</summary>
        private static byte[] WithCrc(byte[] png, string type)
        {
            var bytes = (byte[])png.Clone();
            var position = 8;

            while (position + 12 <= bytes.Length)
            {
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position));

                if (Encoding.ASCII.GetString(bytes, position + 4, 4) == type)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(
                        bytes.AsSpan(position + 8 + length), PhotoRebuild.Crc32(bytes.AsSpan(position + 4, 4 + length)));
                    return bytes;
                }

                position += 12 + length;
            }

            throw new InvalidOperationException("No such chunk.");
        }

        private static byte[] Vp8() => [0x10, 0x02, 0, 0x9D, 0x01, 0x2A, 2, 0, 2, 0, 1];

        private static byte[] Vp8x(byte flags) => [flags, 0, 0, 0, 1, 0, 0, 1, 0, 0];

        private static byte[] RiffChunk(string type, byte[] data)
        {
            var size = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
            return [.. Encoding.ASCII.GetBytes(type), .. size, .. data, .. data.Length % 2 == 1 ? (byte[])[0] : []];
        }

        private static byte[] WebP(params (string Type, byte[] Data)[] chunks)
        {
            var body = new List<byte>(Encoding.ASCII.GetBytes("WEBP"));

            foreach (var (type, data) in chunks)
            {
                body.AddRange(RiffChunk(type, data));
            }

            var riff = new byte[8];
            "RIFF"u8.CopyTo(riff);
            BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), (uint)body.Count);
            return [.. riff, .. body];
        }

        /// <summary>A GIF89a of 2 x 2 with a two-colour global table, the blocks given, then the trailer.</summary>
        private static byte[] Gif(params byte[][] blocks) =>
            [.. "GIF89a"u8, 2, 0, 2, 0, 0x80, 0, 0, 0, 0, 0, 255, 255, 255, .. blocks.SelectMany(b => b), 0x3B];

        /// <summary>An image: descriptor, optional local colour table, LZW code size, data sub-blocks, terminator.</summary>
        private static byte[] Image(bool localTable = false, int dataLength = 4)
        {
            var bytes = new List<byte> { 0x2C, 0, 0, 0, 0, 2, 0, 2, 0, (byte)(localTable ? 0x80 : 0) };

            if (localTable)
            {
                bytes.AddRange([10, 20, 30, 40, 50, 60]);
            }

            bytes.Add(2);
            var data = Enumerable.Range(0, dataLength).Select(i => (byte)(i * 13)).ToArray();

            foreach (var chunk in data.Chunk(255))
            {
                bytes.Add((byte)chunk.Length);
                bytes.AddRange(chunk);
            }

            bytes.Add(0);
            return [.. bytes];
        }

        /// <summary>An extension: introducer, label, a first block, optional data sub-blocks, terminator.</summary>
        private static byte[] Extension(byte label, byte[] first, byte[]? data = null)
        {
            var bytes = new List<byte> { 0x21, label, (byte)first.Length };
            bytes.AddRange(first);

            foreach (var chunk in (data ?? []).Chunk(255))
            {
                bytes.Add((byte)chunk.Length);
                bytes.AddRange(chunk);
            }

            bytes.Add(0);
            return [.. bytes];
        }

        /// <summary>A 2 x 2, 24-bit BMP (BITMAPINFOHEADER).</summary>
        private static byte[] Bmp()
        {
            var bytes = new byte[54 + 16];
            "BM"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), (uint)bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), 54);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 2);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 24);

            for (var i = 54; i < bytes.Length; i++)
            {
                bytes[i] = (byte)(i * 3);
            }

            return bytes;
        }

        /// <summary>A 2 x 2 BMP (BITMAPINFOHEADER) with a palette of <paramref name="coloursUsed"/> entries, then <paramref name="gap"/>, then the pixels.</summary>
        private static byte[] Bmp(ushort bitsPerPixel, uint coloursUsed, byte[] gap)
        {
            var palette = (int)(4 * coloursUsed);
            var pixelOffset = 54 + palette + gap.Length;
            var bytes = new byte[pixelOffset + 8];
            "BM"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), (uint)bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), (uint)pixelOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 2);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), bitsPerPixel);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(46), coloursUsed);

            for (var i = 54; i < 54 + palette; i++)
            {
                bytes[i] = (byte)(0x40 + i);
            }

            gap.CopyTo(bytes, 54 + palette);

            for (var i = pixelOffset; i < bytes.Length; i++)
            {
                bytes[i] = 1;
            }

            return bytes;
        }

        /// <summary>
        /// A 2 x 2 BMP with a <paramref name="headerSize"/>-byte info header, then <paramref name="tables"/> (masks and
        /// palette), then <paramref name="gap"/>, then 8 pixel bytes. The colour count is left 0 (the default for the bits).
        /// </summary>
        private static byte[] BmpWithHeader(int headerSize, ushort bitsPerPixel, uint compression, byte[] tables, byte[] gap)
        {
            const int info = 14;
            var pixelOffset = info + headerSize + tables.Length + gap.Length;
            var bytes = new byte[pixelOffset + 8];
            "BM"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), (uint)bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), (uint)pixelOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(info), (uint)headerSize);

            if (headerSize == 12)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(info + 4), 2);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(info + 6), 2);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(info + 8), 1);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(info + 10), bitsPerPixel);
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(info + 4), 2);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(info + 8), 2);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(info + 12), 1);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(info + 14), bitsPerPixel);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(info + 16), compression);

                // Header bytes after the common fields (V4 masks, colour space) are not zero, so a wrong gap start shows.
                for (var i = info + 40; i < info + headerSize; i++)
                {
                    bytes[i] = 0x5A;
                }
            }

            tables.CopyTo(bytes, info + headerSize);
            gap.CopyTo(bytes, info + headerSize + tables.Length);

            for (var i = pixelOffset; i < bytes.Length; i++)
            {
                bytes[i] = 3;
            }

            return bytes;
        }

        /// <summary>A 2 x 2, 24-bit BMP with a V5 header and an embedded profile in the gap after <paramref name="before"/>.</summary>
        private static byte[] BmpV5WithProfile(byte[] profile, byte[] before)
        {
            const int info = 14;
            var profileOffset = 124 + before.Length;
            var pixelOffset = info + profileOffset + profile.Length;
            var bytes = new byte[pixelOffset + 16];
            "BM"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), (uint)bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), (uint)pixelOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(info), 124);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(info + 4), 2);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(info + 8), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(info + 12), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(info + 14), 24);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(info + 56), 0x4D424544);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(info + 112), (uint)profileOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(info + 116), (uint)profile.Length);
            before.CopyTo(bytes, info + 124);
            profile.CopyTo(bytes, info + profileOffset);

            for (var i = pixelOffset; i < bytes.Length; i++)
            {
                bytes[i] = 2;
            }

            return bytes;
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

        /// <summary>A stream of bytes with no end and no length.</summary>
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

        /// <summary>A stream that cancels its reader after serving the first block, as a user's Cancel during a long copy.</summary>
        private sealed class CancellingStream(byte[] data, CancellationTokenSource cancel) : Stream
        {
            private int _position;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position > 0)
                {
                    cancel.Cancel();
                }

                var n = Math.Min(count, data.Length - _position);
                data.AsSpan(_position, n).CopyTo(buffer.AsSpan(offset));
                _position += n;
                return n;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                var scratch = new byte[buffer.Length];
                var n = Read(scratch, 0, scratch.Length);
                scratch.AsSpan(0, n).CopyTo(buffer.Span);
                cancellationToken.ThrowIfCancellationRequested();
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
