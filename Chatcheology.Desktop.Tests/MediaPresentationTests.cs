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
