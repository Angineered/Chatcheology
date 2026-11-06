using System.Buffers.Binary;
using System.Text;
using Chatcheology.Mobile.Presentation;
using Chatcheology.Mobile.Synthetic;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>CH-59: the rules for sharing a memory photo as a copy without its location.</summary>
    public sealed class PhotoShareTests
    {
        private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        [Fact]
        public void The_format_comes_from_the_first_bytes()
        {
            Assert.Equal(PhotoFormat.Jpeg, PhotoShare.Sniff([0xFF, 0xD8, 0xFF, 0xE1, 0, 0, 0, 0, 0, 0, 0, 0]));
            Assert.Equal(PhotoFormat.Png, PhotoShare.Sniff([.. PngSignature, 0, 0, 0, 13]));
            Assert.Equal(PhotoFormat.WebP, PhotoShare.Sniff([.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8]));
            Assert.Equal(PhotoFormat.Gif, PhotoShare.Sniff([.. "GIF89a"u8, 0, 0, 0, 0, 0, 0]));
            Assert.Equal(PhotoFormat.Gif, PhotoShare.Sniff([.. "GIF87a"u8]));
            Assert.Equal(PhotoFormat.Bmp, PhotoShare.Sniff([.. "BM"u8, 0, 0, 0, 0]));

            // HEIC (an ISO box "ftypheic"), TIFF, empty, short and text are all Other.
            Assert.Equal(PhotoFormat.Other, PhotoShare.Sniff([0, 0, 0, 0x18, .. "ftypheic"u8]));
            Assert.Equal(PhotoFormat.Other, PhotoShare.Sniff([.. "II*\0"u8, 8, 0, 0, 0]));
            Assert.Equal(PhotoFormat.Other, PhotoShare.Sniff([]));
            Assert.Equal(PhotoFormat.Other, PhotoShare.Sniff([0xFF, 0xD8]));
            Assert.Equal(PhotoFormat.Other, PhotoShare.Sniff("not a picture"u8));
        }

        [Theory]
        [InlineData(PhotoFormat.Jpeg, 26, PhotoShareMethod.RemoveLocation)]
        [InlineData(PhotoFormat.Jpeg, 36, PhotoShareMethod.RemoveLocation)]
        [InlineData(PhotoFormat.Png, 29, PhotoShareMethod.PlainCopy)]
        [InlineData(PhotoFormat.Png, 30, PhotoShareMethod.RemoveLocation)]
        [InlineData(PhotoFormat.WebP, 29, PhotoShareMethod.PlainCopy)]
        [InlineData(PhotoFormat.WebP, 30, PhotoShareMethod.RemoveLocation)]
        [InlineData(PhotoFormat.Gif, 26, PhotoShareMethod.PlainCopy)]
        [InlineData(PhotoFormat.Gif, 36, PhotoShareMethod.PlainCopy)]
        [InlineData(PhotoFormat.Bmp, 33, PhotoShareMethod.PlainCopy)]
        [InlineData(PhotoFormat.Other, 26, PhotoShareMethod.Refuse)]
        [InlineData(PhotoFormat.Other, 36, PhotoShareMethod.Refuse)]
        public void Each_format_has_one_method_per_Android_version(PhotoFormat format, int apiLevel, PhotoShareMethod method)
        {
            Assert.Equal(method, PhotoShare.MethodFor(format, apiLevel));
        }

        [Fact]
        public void The_refusal_text_says_why()
        {
            Assert.Equal("This photo can't be shared without its location.", PhotoShare.CannotShareText);
        }

        [Fact]
        public void Every_GPS_tag_is_cleared_and_nothing_else()
        {
            // Every tag of the EXIF 2.32 GPS directory (0x0000-0x001F), by ExifInterface's name.
            string[] all =
            [
                "GPSVersionID", "GPSLatitudeRef", "GPSLatitude", "GPSLongitudeRef", "GPSLongitude", "GPSAltitudeRef",
                "GPSAltitude", "GPSTimeStamp", "GPSSatellites", "GPSStatus", "GPSMeasureMode", "GPSDOP", "GPSSpeedRef",
                "GPSSpeed", "GPSTrackRef", "GPSTrack", "GPSImgDirectionRef", "GPSImgDirection", "GPSMapDatum",
                "GPSDestLatitudeRef", "GPSDestLatitude", "GPSDestLongitudeRef", "GPSDestLongitude", "GPSDestBearingRef",
                "GPSDestBearing", "GPSDestDistanceRef", "GPSDestDistance", "GPSProcessingMethod", "GPSAreaInformation",
                "GPSDateStamp", "GPSDifferential", "GPSHPositioningError",
            ];

            Assert.Equal(32, all.Length);
            Assert.Equal(all.Order(StringComparer.Ordinal), PhotoShare.LocationTags.Order(StringComparer.Ordinal));
        }

        [Fact]
        public void Copies_are_kept_for_a_day()
        {
            var now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

            Assert.False(PhotoShare.IsExpired(now, now));
            Assert.False(PhotoShare.IsExpired(now.AddHours(-23), now));
            Assert.False(PhotoShare.IsExpired(now.AddHours(-24), now));
            Assert.True(PhotoShare.IsExpired(now.AddHours(-24).AddSeconds(-1), now));
            Assert.True(PhotoShare.IsExpired(now.AddDays(-3), now));
        }

        [Fact]
        public void A_GPS_block_is_found_wherever_it_is()
        {
            var tiff = SyntheticJpeg.GpsTiff();

            Assert.True(PhotoShare.HasGpsBlock(SyntheticJpeg.WithLocation()));
            Assert.True(PhotoShare.HasGpsBlock(tiff));
            Assert.True(PhotoShare.HasGpsBlock([.. new byte[1000], .. tiff, .. new byte[1000]]));
            Assert.True(PhotoShare.HasGpsBlock(LittleEndian(gpsCount: 3)));
            Assert.False(PhotoShare.HasGpsBlock(SyntheticJpeg.Plain()));
            Assert.False(PhotoShare.HasGpsBlock(new byte[5000]));
        }

        [Fact]
        public void An_empty_GPS_directory_is_no_location_but_a_broken_pointer_is_a_doubt()
        {
            Assert.False(PhotoShare.HasGpsBlock(LittleEndian(gpsCount: 0)));
            Assert.True(PhotoShare.HasGpsBlock(LittleEndian(gpsCount: 1, gpsOffset: 5000)));
        }

        [Fact]
        public void A_two_image_JPEG_is_shared_as_its_main_image_only()
        {
            var both = SyntheticJpeg.TwoImagesLocationInSecond();
            Assert.True(PhotoShare.HasGpsBlock(both));
            Assert.True(PhotoShare.MayStillCarryLocation(new MemoryStream(both), PhotoFormat.Jpeg, PhotoShareMethod.RemoveLocation));

            var main = MainImage(both);

            Assert.Equal(SyntheticJpeg.Plain(), main);
            Assert.False(PhotoShare.HasGpsBlock(main));
            Assert.False(PhotoShare.MayStillCarryLocation(new MemoryStream(main), PhotoFormat.Jpeg, PhotoShareMethod.RemoveLocation));
        }

        [Fact]
        public void A_motion_photo_shares_its_still_image()
        {
            // A JPEG, then an MP4-shaped tail whose metadata holds an Exif/GPS block.
            byte[] video = [0, 0, 0, 0x18, .. "ftypmp42"u8, .. new byte[100], .. "Exif\0\0"u8, .. SyntheticJpeg.GpsTiff(), .. new byte[300]];
            byte[] motion = [.. SyntheticJpeg.Plain(), .. video];

            Assert.True(PhotoShare.HasGpsBlock(motion));

            var still = MainImage(motion);

            Assert.Equal(SyntheticJpeg.Plain(), still);
            Assert.False(PhotoShare.HasGpsBlock(still));
        }

        [Fact]
        public void The_rebuilt_JPEG_keeps_JFIF_Exif_ICC_and_the_image_and_drops_every_other_block()
        {
            var plain = SyntheticJpeg.Plain();
            var app0Length = (plain[4] << 8) | plain[5];
            var jfif = plain[2..(4 + app0Length)];
            var image = plain[(4 + app0Length)..];

            byte[] source =
            [
                0xFF, 0xD8,
                .. jfif,
                .. Segment(0xE1, [.. "Exif\0\0"u8, .. NoGpsTiff()]),
                .. Segment(0xE1, Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0<exif:GPSLatitude>1</exif:GPSLatitude>")),
                .. Segment(0xE2, [.. "ICC_PROFILE\0"u8, 1, 1, .. new byte[20]]),
                .. Segment(0xE2, [.. "MPF\0"u8, .. SyntheticJpeg.GpsTiff()]),
                .. Segment(0xED, Encoding.ASCII.GetBytes("Photoshop 3.0\0invented IPTC")),
                .. Segment(0xEB, Encoding.ASCII.GetBytes("JP\0invented")),
                .. Segment(0xFE, Encoding.ASCII.GetBytes("an invented comment")),
                .. image,
                .. "trailing bytes"u8,
            ];

            var rebuilt = MainImage(source);

            byte[] expected =
            [
                0xFF, 0xD8,
                .. jfif,
                .. Segment(0xE1, [.. "Exif\0\0"u8, .. NoGpsTiff()]),
                .. Segment(0xE2, [.. "ICC_PROFILE\0"u8, 1, 1, .. new byte[20]]),
                .. image,
            ];

            Assert.Equal(expected, rebuilt);
            Assert.False(PhotoShare.MayStillCarryLocation(new MemoryStream(rebuilt), PhotoFormat.Jpeg, PhotoShareMethod.RemoveLocation));
        }

        [Fact]
        public void An_Adobe_APP14_block_is_kept_so_a_CMYK_JPEG_keeps_its_colours()
        {
            // APP14 "Adobe": version, flags0, flags1, colour transform (2 = YCCK).
            byte[] adobe = [.. "Adobe"u8, 0x00, 0x64, 0x00, 0x00, 0x00, 0x00, 0x02];
            byte[] image = [.. Segment(0xDB, new byte[65]), .. Segment(0xDA, new byte[10]), 0x12, 0x34, 0xFF, 0xD9];

            byte[] source =
            [
                0xFF, 0xD8,
                .. Segment(0xEE, adobe),
                .. Segment(0xEE, Encoding.ASCII.GetBytes("Other\0invented")),
                .. image,
            ];

            var rebuilt = MainImage(source);

            Assert.Equal([0xFF, 0xD8, .. Segment(0xEE, adobe), .. image], rebuilt);
        }

        [Fact]
        public void A_scan_with_stuffed_bytes_restart_markers_and_fill_bytes_is_kept_whole()
        {
            byte[] scan = [0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56, 0xFF, 0xD7, 0x78];
            byte[] source = [0xFF, 0xD8, .. Segment(0xDB, new byte[65]), .. Segment(0xDA, new byte[10]), .. scan, 0xFF, 0xFF, 0xD9, .. "after"u8];

            var rebuilt = MainImage(source);

            Assert.Equal([0xFF, 0xD8, .. Segment(0xDB, new byte[65]), .. Segment(0xDA, new byte[10]), .. scan, 0xFF, 0xFF, 0xD9], rebuilt);
        }

        [Theory]
        [InlineData(new byte[] { 0x89, 0x50 })]
        [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x43 })]
        [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x02, 0x11, 0x22 })]
        [InlineData(new byte[] { 0xFF, 0xD8, 0x00, 0x00 })]
        public void A_cut_or_broken_JPEG_is_not_rebuilt(byte[] source)
        {
            Assert.False(PhotoShare.WriteMainJpeg(source, new MemoryStream()));
        }

        [Fact]
        public void A_WebP_EXIF_chunk_with_GPS_refuses_with_or_without_the_Exif_prefix()
        {
            var prefixed = WebP(("VP8 ", new byte[10]), ("EXIF", [.. "Exif\0\0"u8, .. SyntheticJpeg.GpsTiff()]));
            var bare = WebP(("VP8 ", new byte[10]), ("EXIF", SyntheticJpeg.GpsTiff()));
            var clean = WebP(("VP8 ", new byte[10]), ("EXIF", [.. "Exif\0\0"u8, .. NoGpsTiff()]));

            Assert.True(PhotoShare.MayStillCarryLocation(new MemoryStream(prefixed), PhotoFormat.WebP, PhotoShareMethod.RemoveLocation));
            Assert.True(PhotoShare.MayStillCarryLocation(new MemoryStream(bare), PhotoFormat.WebP, PhotoShareMethod.RemoveLocation));
            Assert.False(PhotoShare.MayStillCarryLocation(new MemoryStream(clean), PhotoFormat.WebP, PhotoShareMethod.RemoveLocation));
        }

        [Fact]
        public void A_PNG_eXIf_chunk_with_GPS_refuses_even_after_ExifInterface()
        {
            var png = Png(("IHDR", new byte[13]), ("eXIf", SyntheticJpeg.GpsTiff()), ("IDAT", new byte[20]));

            Assert.True(PhotoShare.MayStillCarryLocation(new MemoryStream(png), PhotoFormat.Png, PhotoShareMethod.RemoveLocation));
        }

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

        /// <summary>A little-endian TIFF block whose IFD0 has a GPS pointer to a directory of <paramref name="gpsCount"/> entries.</summary>
        private static byte[] LittleEndian(ushort gpsCount, uint gpsOffset = 26)
        {
            var tiff = new byte[26 + 2 + (gpsCount * 12) + 4];
            "II"u8.CopyTo(tiff);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x8825);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(18), gpsOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(26), gpsCount);

            for (var i = 0; i < gpsCount; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(28 + (i * 12)), (ushort)i);
            }

            return tiff;
        }

        [Theory]
        [InlineData("archives/abc/media/p.jpg", true)]
        [InlineData("archives/abc/deep/er/p.jpg", true)]
        [InlineData("archives", false)]
        [InlineData("archives/", false)]
        [InlineData("archives-old/abc/p.jpg", false)]
        [InlineData("staging/abc/p.jpg", false)]
        [InlineData("archives/../staging/p.jpg", false)]
        [InlineData("archives/abc/../../settings.json", false)]
        [InlineData("shared-photos/photo-1.jpg", false)]
        public void A_path_outside_the_archives_folder_is_refused(string relative, bool inside)
        {
            // The app's files folder, as an absolute path of the host.
            var files = Path.Combine(Path.GetTempPath(), "chatcheology-share-test", "files");
            var path = files + Path.DirectorySeparatorChar + relative.Replace('/', Path.DirectorySeparatorChar);

            Assert.Equal(inside, PhotoShare.IsInside(path, Path.Combine(files, "archives")));
        }

        [Fact]
        public void Relative_missing_and_other_paths_are_refused()
        {
            var archives = Path.Combine(Path.GetTempPath(), "chatcheology-share-test", "files", "archives");

            Assert.False(PhotoShare.IsInside(Path.Combine("archives", "abc", "p.jpg"), archives));
            Assert.False(PhotoShare.IsInside(null, archives));
            Assert.False(PhotoShare.IsInside(string.Empty, archives));
            Assert.False(PhotoShare.IsInside(Path.Combine(Path.GetTempPath(), "DCIM", "p.jpg"), archives));
            Assert.False(PhotoShare.IsInside(Path.Combine(archives, "abc", "p.jpg"), null));
            Assert.False(PhotoShare.IsInside(Path.Combine(archives, "abc", "p.jpg"), string.Empty));
            Assert.False(PhotoShare.IsInside(Path.Combine(archives, "abc", "p.jpg"), "archives"));

            // Ordinal: the phone's file system is case-sensitive, so another case is another folder.
            Assert.False(PhotoShare.IsInside(Path.Combine(archives.ToUpperInvariant(), "abc", "p.jpg"), archives));
        }

        [Fact]
        public void The_copy_never_carries_the_archive_file_name()
        {
            Assert.Equal("photo-0123456789ab.jpg", PhotoShare.CopyFileName("/x/archives/a/media/IMG-2021-holiday.JPG", "0123456789ab"));
            Assert.Equal("photo-u.png", PhotoShare.CopyFileName("/x/a.png", "u"));
            Assert.Equal("image/jpeg", MediaMimeTypes.FromFileName(PhotoShare.CopyFileName("/x/a.JPEG", "u")));
        }

        [Fact]
        public void A_copy_without_location_text_passes()
        {
            var jpeg = Jpeg(Encoding.ASCII.GetBytes("Exif\0\0 no location here"));

            Assert.False(PhotoShare.MayStillCarryLocation(new MemoryStream(jpeg), PhotoFormat.Jpeg, PhotoShareMethod.RemoveLocation));
        }

        [Theory]
        [InlineData("<x:xmpmeta><rdf:Description exif:GPSLatitude=\"51,30.0N\"/></x:xmpmeta>")]
        [InlineData("<exif:GPSLongitude>0,7.5W</exif:GPSLongitude>")]
        public void XMP_location_left_in_a_JPEG_refuses_it(string xmp)
        {
            var jpeg = Jpeg(Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0" + xmp));

            Assert.True(PhotoShare.MayStillCarryLocation(new MemoryStream(jpeg), PhotoFormat.Jpeg, PhotoShareMethod.RemoveLocation));
        }

        [Fact]
        public void Location_text_across_a_read_boundary_is_found()
        {
            // "GPSLatitude" starting a few bytes before the 64 KiB buffer edge.
            var bytes = new byte[200_000];
            Encoding.ASCII.GetBytes("GPSLatitude").CopyTo(bytes, (64 * 1024) - 4);

            Assert.True(PhotoShare.ContainsLocationText(new MemoryStream(bytes)));
            Assert.False(PhotoShare.ContainsLocationText(new MemoryStream(new byte[200_000])));
        }

        [Fact]
        public void A_refused_method_is_never_passed()
        {
            Assert.True(PhotoShare.MayStillCarryLocation(new MemoryStream(Jpeg([])), PhotoFormat.Jpeg, PhotoShareMethod.Refuse));
        }

        [Fact]
        public void A_plain_PNG_with_no_EXIF_passes()
        {
            var png = Png(("IHDR", new byte[13]), ("tEXt", Encoding.ASCII.GetBytes("Software\0Synthetic")), ("IDAT", new byte[20]));

            Assert.False(PhotoShare.MayStillCarryLocation(new MemoryStream(png), PhotoFormat.Png, PhotoShareMethod.PlainCopy));
        }

        [Fact]
        public void A_PNG_eXIf_chunk_refuses_a_plain_copy_but_not_a_rewritten_one()
        {
            var png = Png(("IHDR", new byte[13]), ("eXIf", Encoding.ASCII.GetBytes("MM\0*\0\0\0\b")), ("IDAT", new byte[20]));

            Assert.True(PhotoShare.MayStillCarryLocation(new MemoryStream(png), PhotoFormat.Png, PhotoShareMethod.PlainCopy));

            // On Android 11+ ExifInterface rewrote it without GPS tags; the EXIF block itself may stay.
            Assert.False(PhotoShare.MayStillCarryLocation(new MemoryStream(png), PhotoFormat.Png, PhotoShareMethod.RemoveLocation));
        }

        [Fact]
        public void Compressed_PNG_text_cannot_be_searched_and_refuses()
        {
            var ztxt = Png(("IHDR", new byte[13]), ("zTXt", Encoding.ASCII.GetBytes("XML:com.adobe.xmp\0\0xxxx")), ("IDAT", new byte[4]));
            var compressedItxt = Png(("IHDR", new byte[13]), ("iTXt", Encoding.ASCII.GetBytes("XML:com.adobe.xmp\0\u0001\0\0\0xxxx")), ("IDAT", new byte[4]));
            var plainItxt = Png(("IHDR", new byte[13]), ("iTXt", Encoding.ASCII.GetBytes("XML:com.adobe.xmp\0\0\0\0\0<x:xmpmeta/>")), ("IDAT", new byte[4]));
            var plainItxtWithGps = Png(("IHDR", new byte[13]), ("iTXt", Encoding.ASCII.GetBytes("XML:com.adobe.xmp\0\0\0\0\0<exif:GPSLatitude>1</exif:GPSLatitude>")), ("IDAT", new byte[4]));

            Assert.True(PhotoShare.MayStillCarryLocation(new MemoryStream(ztxt), PhotoFormat.Png, PhotoShareMethod.RemoveLocation));
            Assert.True(PhotoShare.MayStillCarryLocation(new MemoryStream(compressedItxt), PhotoFormat.Png, PhotoShareMethod.PlainCopy));
            Assert.False(PhotoShare.MayStillCarryLocation(new MemoryStream(plainItxt), PhotoFormat.Png, PhotoShareMethod.PlainCopy));
            Assert.True(PhotoShare.MayStillCarryLocation(new MemoryStream(plainItxtWithGps), PhotoFormat.Png, PhotoShareMethod.RemoveLocation));
        }

        [Fact]
        public void A_damaged_PNG_refuses()
        {
            var png = Png(("IHDR", new byte[13]), ("IDAT", new byte[20]));
            var cut = png[..^10];

            Assert.True(PhotoShare.MayStillCarryLocation(new MemoryStream(cut), PhotoFormat.Png, PhotoShareMethod.PlainCopy));
        }

        [Fact]
        public void A_WebP_EXIF_chunk_refuses_a_plain_copy_only()
        {
            var plain = WebP(("VP8 ", new byte[10]));
            var withExif = WebP(("VP8X", new byte[10]), ("VP8 ", new byte[11]), ("EXIF", Encoding.ASCII.GetBytes("MM\0*\0\0\0\b")));

            Assert.False(PhotoShare.MayStillCarryLocation(new MemoryStream(plain), PhotoFormat.WebP, PhotoShareMethod.PlainCopy));
            Assert.True(PhotoShare.MayStillCarryLocation(new MemoryStream(withExif), PhotoFormat.WebP, PhotoShareMethod.PlainCopy));
            Assert.False(PhotoShare.MayStillCarryLocation(new MemoryStream(withExif), PhotoFormat.WebP, PhotoShareMethod.RemoveLocation));
        }

        [Fact]
        public void A_GIF_with_XMP_location_refuses()
        {
            var gif = Encoding.ASCII.GetBytes("GIF89a....XMP DataXMP<exif:GPSLatitude>1</exif:GPSLatitude>;");
            var clean = Encoding.ASCII.GetBytes("GIF89a....;");

            Assert.True(PhotoShare.MayStillCarryLocation(new MemoryStream(gif), PhotoFormat.Gif, PhotoShareMethod.PlainCopy));
            Assert.False(PhotoShare.MayStillCarryLocation(new MemoryStream(clean), PhotoFormat.Gif, PhotoShareMethod.PlainCopy));
        }

        [Fact]
        public void The_synthetic_memory_JPEG_carries_an_invented_location_to_remove()
        {
            var jpeg = SyntheticJpeg.WithLocation();

            Assert.Equal(PhotoFormat.Jpeg, PhotoShare.Sniff(jpeg));
            Assert.Equal(PhotoShareMethod.RemoveLocation, PhotoShare.MethodFor(PhotoShare.Sniff(jpeg), 33));

            // APP1 "Exif" first, big-endian TIFF, IFD0's GPS pointer, then the GPS directory's tags.
            Assert.Equal([0xFF, 0xD8, 0xFF, 0xE1], jpeg[..4]);
            Assert.True(jpeg.AsSpan(6, 6).SequenceEqual("Exif\0\0"u8));
            var tiff = jpeg.AsSpan(12);
            Assert.True(tiff[..2].SequenceEqual("MM"u8));
            Assert.Equal(0x8825, BinaryPrimitives.ReadUInt16BigEndian(tiff[10..]));
            var gps = (int)BinaryPrimitives.ReadUInt32BigEndian(tiff[18..]);
            var count = BinaryPrimitives.ReadUInt16BigEndian(tiff[gps..]);
            var tags = new List<int>();

            for (var i = 0; i < count; i++)
            {
                tags.Add(BinaryPrimitives.ReadUInt16BigEndian(tiff[(gps + 2 + (i * 12))..]));
            }

            Assert.Equal([0, 1, 2, 3, 4, 5, 6], tags);

            // The picture after the EXIF block is the stored JPEG without its JFIF header.
            var afterExif = 4 + ((jpeg[4] << 8) | jpeg[5]);
            Assert.Equal([0xFF, 0xDB], jpeg[afterExif..(afterExif + 2)]);
            Assert.Equal([0xFF, 0xD9], jpeg[^2..]);
        }

        private static byte[] Jpeg(byte[] app1)
        {
            var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1 };
            var length = (ushort)(app1.Length + 2);
            bytes.Add((byte)(length >> 8));
            bytes.Add((byte)length);
            bytes.AddRange(app1);
            bytes.AddRange([0xFF, 0xDA, 0, 2, 1, 2, 3, 0xFF, 0xD9]);
            return [.. bytes];
        }

        private static byte[] Png(params (string Type, byte[] Data)[] chunks)
        {
            var bytes = new List<byte>(PngSignature);

            foreach (var (type, data) in chunks.Append(("IEND", [])))
            {
                var head = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(head, (uint)data.Length);
                bytes.AddRange(head);
                var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
                bytes.AddRange(typed);
                var crc = new byte[4];
                // The CRC is not read by the check; zeros do.
                bytes.AddRange(crc);
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

            var riffSize = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(riffSize, (uint)body.Count);

            return [.. Encoding.ASCII.GetBytes("RIFF"), .. riffSize, .. body];
        }
    }
}
