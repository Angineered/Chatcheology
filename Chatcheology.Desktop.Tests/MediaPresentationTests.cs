using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Chatcheology.Data.Media;
using Chatcheology.Desktop.Services;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// That a preview can be decoded without taking hold of the file it came from.
    /// </summary>
    /// <remarks>
    /// The regression Phase 8.0 deferred. Its image loading was verified by hand at the time, and a
    /// defect in it — a create option that throws when the source is a stream rather than a URI —
    /// compiled, passed every test in the suite, and silently produced no thumbnails at all. The
    /// deletion test below is the one that could not have passed while a handle survived the call.
    /// </remarks>
    public class MediaPresentationTests
    {
        [Fact]
        public void AValidImage_DecodesToAFrozenBitmap()
        {
            using var directory = new TemporaryDirectory();

            var path = directory.WriteImage("preview.png");

            var thumbnail = MediaPresentation.TryLoadThumbnail(path);

            Assert.NotNull(thumbnail);
            Assert.True(thumbnail!.IsFrozen);
            Assert.True(thumbnail.PixelWidth > 0);
            Assert.True(thumbnail.PixelHeight > 0);
        }

        /// <remarks>
        /// <strong>The test that proves no handle survives.</strong> A file cannot be deleted on
        /// Windows while something holds it open, so a decode that leaked a stream — or that
        /// deferred the read to the render thread, which binding an <c>Image.Source</c> to a path
        /// would do — fails here rather than in front of the operator when their recovery drive
        /// refuses to eject.
        /// </remarks>
        [Fact]
        public void AfterDecoding_TheSourceFileCanBeDeletedImmediately()
        {
            using var directory = new TemporaryDirectory();

            var path = directory.WriteImage("preview.png");

            Assert.NotNull(MediaPresentation.TryLoadThumbnail(path));

            File.Delete(path);

            Assert.False(File.Exists(path));
        }

        [Fact]
        public void AFileThatIsNotAnImage_DecodesToNothingRatherThanThrowing()
        {
            using var directory = new TemporaryDirectory();

            var path = Path.Combine(directory.Path, "truncated.png");

            // A plausible recovered file: the right name, and bytes that are not the format.
            File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, 0x00, 0x00]);

            Assert.Null(MediaPresentation.TryLoadThumbnail(path));
        }

        [Fact]
        public void AFileThatIsNotThere_DecodesToNothingRatherThanThrowing()
        {
            using var directory = new TemporaryDirectory();

            Assert.Null(
                MediaPresentation.TryLoadThumbnail(Path.Combine(directory.Path, "absent.png")));
        }

        /// <remarks>
        /// An unclassified file is not handed to the shell. The workspace could not say what it is,
        /// and shell-executing something nobody has classified is not a thing a read-only viewer
        /// does on the operator's behalf.
        /// </remarks>
        [Theory]
        [InlineData(MediaType.Image, true)]
        [InlineData(MediaType.Video, true)]
        [InlineData(MediaType.Audio, true)]
        [InlineData(MediaType.Document, true)]
        [InlineData(MediaType.Unknown, false)]
        public void OnlyClassifiedMedia_MayBeOpenedExternally(MediaType mediaType, bool expected)
        {
            Assert.Equal(expected, MediaPresentation.CanOpenExternally(mediaType));
        }

        [Fact]
        public void OpeningAFileThatIsNotThere_ReportsRatherThanThrows()
        {
            using var directory = new TemporaryDirectory();

            var failure = MediaPresentation.TryOpenExternally(
                Path.Combine(directory.Path, "absent.bin"));

            Assert.False(string.IsNullOrWhiteSpace(failure));
        }

        // ---------------------------------------------------------------------------------------
        // EXIF orientation (CH-57). The stored picture is 64 wide by 32 high, in four solid
        // quadrants: top left red, top right green, bottom left blue, bottom right white.
        // ---------------------------------------------------------------------------------------

        private const int StoredWidth = 64;
        private const int StoredHeight = 32;

        private static readonly Color Red = Color.FromRgb(0xFF, 0x00, 0x00);
        private static readonly Color Green = Color.FromRgb(0x00, 0xFF, 0x00);
        private static readonly Color Blue = Color.FromRgb(0x00, 0x00, 0xFF);
        private static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);

        /// <summary>
        /// Each orientation comes out at its upright size, with the stored corner EXIF puts at the
        /// top left there, and the one it puts at the top right there.
        /// </summary>
        [Theory]
        [InlineData((ushort)1, "red", "green")]
        [InlineData((ushort)2, "green", "red")]
        [InlineData((ushort)3, "white", "blue")]
        [InlineData((ushort)4, "blue", "white")]
        [InlineData((ushort)5, "red", "blue")]
        [InlineData((ushort)6, "blue", "red")]
        [InlineData((ushort)7, "white", "green")]
        [InlineData((ushort)8, "green", "white")]
        public void AJpegWithAnOrientation_IsShownTheRightWayUp(ushort orientation, string topLeft, string topRight)
        {
            using var directory = new TemporaryDirectory();

            var path = directory.WriteQuadrantJpeg("photo.jpg", orientation);
            var swaps = orientation >= 5;
            var (uprightWidth, uprightHeight) = swaps ? (StoredHeight, StoredWidth) : (StoredWidth, StoredHeight);

            var image = MediaPresentation.TryLoadImage(path, uprightWidth);

            Assert.NotNull(image);
            Assert.True(image!.IsFrozen);
            Assert.Equal(uprightWidth, image.PixelWidth);
            Assert.Equal(uprightHeight, image.PixelHeight);
            AssertNear(Named(topLeft), PixelAt(image, 2, 2));
            AssertNear(Named(topRight), PixelAt(image, image.PixelWidth - 3, 2));
        }

        /// <summary>The decode width is the upright width, so a portrait thumbnail is not decoded twice too tall.</summary>
        [Fact]
        public void ARotatedThumbnail_IsDecodedAtTheThumbnailWidth()
        {
            using var directory = new TemporaryDirectory();

            var path = directory.WriteQuadrantJpeg("portrait.jpg", 6);

            var thumbnail = MediaPresentation.TryLoadThumbnail(path);

            Assert.NotNull(thumbnail);
            Assert.Equal(MediaPresentation.ThumbnailWidth, thumbnail!.PixelWidth);
            Assert.Equal(MediaPresentation.ThumbnailWidth * 2, thumbnail.PixelHeight);
        }

        [Fact]
        public void AJpegWithNoOrientation_LoadsAsStored()
        {
            using var directory = new TemporaryDirectory();

            var path = directory.WriteQuadrantJpeg("plain.jpg", orientation: null);

            var image = MediaPresentation.TryLoadImage(path, StoredWidth);

            Assert.NotNull(image);
            Assert.Equal(StoredWidth, image!.PixelWidth);
            Assert.Equal(StoredHeight, image.PixelHeight);
            AssertNear(Red, PixelAt(image, 2, 2));
            AssertNear(Green, PixelAt(image, image.PixelWidth - 3, 2));
        }

        [Fact]
        public void APng_LoadsAsStored()
        {
            using var directory = new TemporaryDirectory();

            var path = directory.WriteImage("preview.png");

            var image = MediaPresentation.TryLoadImage(path, 48);

            Assert.NotNull(image);
            Assert.Equal(48, image!.PixelWidth);
            Assert.Equal(32, image.PixelHeight);
        }

        /// <remarks>
        /// A tag that is there but cannot be used (out of range, or not a number) is read as "as
        /// stored", and the picture still shows rather than becoming "Can't preview". A JPEG whose
        /// Exif segment is outright corrupt is not tested here: WPF's own decode already refuses it
        /// (reading its colour contexts) with or without CH-57, so it returns null either way.
        /// </remarks>
        [Theory]
        [InlineData("out-of-range")]
        [InlineData("not-a-number")]
        public void AJpegWhoseOrientationCannotBeUsed_StillLoadsAsStored(string kind)
        {
            using var directory = new TemporaryDirectory();

            object tag = kind == "out-of-range" ? (ushort)9 : "sideways";
            var path = directory.WriteQuadrantImage("odd-tag.jpg", new JpegBitmapEncoder { QualityLevel = 95 }, tag);

            var image = MediaPresentation.TryLoadImage(path, StoredWidth);

            Assert.NotNull(image);
            Assert.Equal(StoredWidth, image!.PixelWidth);
            Assert.Equal(StoredHeight, image.PixelHeight);
            AssertNear(Red, PixelAt(image, 2, 2));
            AssertNear(Green, PixelAt(image, image.PixelWidth - 3, 2));
        }

        /// <summary>Formats whose frames have no metadata WPF can query load as stored.</summary>
        [Theory]
        [InlineData("picture.bmp")]
        [InlineData("picture.gif")]
        public void AFormatWithoutOrientationMetadata_LoadsAsStored(string fileName)
        {
            using var directory = new TemporaryDirectory();

            BitmapEncoder encoder = fileName.EndsWith(".bmp", StringComparison.Ordinal)
                ? new BmpBitmapEncoder()
                : new GifBitmapEncoder();
            var path = directory.WriteQuadrantImage(fileName, encoder, tag: null);

            var image = MediaPresentation.TryLoadImage(path, StoredWidth);

            Assert.NotNull(image);
            Assert.Equal(StoredWidth, image!.PixelWidth);
            Assert.Equal(StoredHeight, image.PixelHeight);
            AssertNear(Red, PixelAt(image, 2, 2));
            AssertNear(Green, PixelAt(image, image.PixelWidth - 3, 2));
        }

        private static Color Named(string name) => name switch
        {
            "red" => Red,
            "green" => Green,
            "blue" => Blue,
            "white" => White,
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        private static Color PixelAt(BitmapSource image, int x, int y)
        {
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4];
            converted.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);

            return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
        }

        /// <summary>JPEG is lossy: close enough to the solid colour, far from the others.</summary>
        private static void AssertNear(Color expected, Color actual)
        {
            static bool Near(byte a, byte b) => Math.Abs(a - b) <= 48;

            Assert.True(
                Near(expected.R, actual.R) && Near(expected.G, actual.G) && Near(expected.B, actual.B),
                $"Expected about {expected}, got {actual}.");
        }

        /// <summary>An isolated directory outside the repository, removed on dispose.</summary>
        private sealed class TemporaryDirectory : IDisposable
        {
            internal TemporaryDirectory()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "Chatcheology.Desktop.Tests",
                    Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(Path);
            }

            internal string Path { get; }

            /// <summary>Writes a small, entirely invented image.</summary>
            internal string WriteImage(string fileName)
            {
                const int Width = 48;
                const int Height = 32;

                var stride = Width * 4;
                var pixels = new byte[stride * Height];

                for (var index = 0; index < pixels.Length; index += 4)
                {
                    pixels[index] = 0x40;
                    pixels[index + 1] = 0x80;
                    pixels[index + 2] = 0xC0;
                    pixels[index + 3] = 0xFF;
                }

                var bitmap = BitmapSource.Create(
                    Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                var path = System.IO.Path.Combine(Path, fileName);

                using var stream = File.Create(path);
                encoder.Save(stream);

                return path;
            }

            /// <summary>
            /// Writes an invented 64 by 32 JPEG in four solid quadrants, with
            /// <paramref name="orientation"/> as its EXIF orientation tag, or no metadata at all.
            /// </summary>
            internal string WriteQuadrantJpeg(string fileName, ushort? orientation) =>
                WriteQuadrantImage(fileName, new JpegBitmapEncoder { QualityLevel = 95 }, orientation);

            /// <summary>
            /// Writes the invented quadrant picture with <paramref name="encoder"/>; for a JPEG,
            /// <paramref name="tag"/> (when given) is written as the EXIF orientation tag's value.
            /// </summary>
            internal string WriteQuadrantImage(string fileName, BitmapEncoder encoder, object? tag)
            {
                var stride = StoredWidth * 4;
                var pixels = new byte[stride * StoredHeight];

                for (var y = 0; y < StoredHeight; y++)
                {
                    for (var x = 0; x < StoredWidth; x++)
                    {
                        var colour = (x < StoredWidth / 2, y < StoredHeight / 2) switch
                        {
                            (true, true) => Red,
                            (false, true) => Green,
                            (true, false) => Blue,
                            _ => White,
                        };

                        var index = (y * stride) + (x * 4);
                        pixels[index] = colour.B;
                        pixels[index + 1] = colour.G;
                        pixels[index + 2] = colour.R;
                        pixels[index + 3] = 0xFF;
                    }
                }

                var bitmap = BitmapSource.Create(
                    StoredWidth, StoredHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);

                BitmapMetadata? metadata = null;

                if (tag is not null)
                {
                    metadata = new BitmapMetadata("jpg");
                    metadata.SetQuery("/app1/ifd/{ushort=274}", tag);
                }

                encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));

                var path = System.IO.Path.Combine(Path, fileName);

                using var stream = File.Create(path);
                encoder.Save(stream);

                return path;
            }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                    // A handle that outlived a test is what these tests exist to catch, and the
                    // test that catches it is the deletion one — not this cleanup.
                }
            }
        }
    }
}
