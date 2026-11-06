using Android.Graphics;
using Android.Media;
using Chatcheology.Mobile.OwnMemories;
using Chatcheology.Mobile.Presentation;

namespace Chatcheology.Android
{
    /// <summary>
    /// The phone's part of storing an own memory photo (CH-54, D-37): a photo the pure rebuild refuses (HEIC, an unknown
    /// format, or an exact form it will not keep) is decoded, turned upright by its EXIF orientation, and written again as
    /// a JPEG (quality 92) that carries no metadata at all. <see cref="OwnPhotoIntake"/> then rebuilds those bytes and
    /// checks them again, so nothing is stored on this step's word alone.
    /// </summary>
    /// <remarks>
    /// <para>Android 9+ decodes with <see cref="ImageDecoder"/>, which reads HEIC and applies the EXIF orientation itself;
    /// older versions decode with <see cref="BitmapFactory"/> (no HEIC) and turn the picture by the orientation tag. A
    /// picture with transparency is laid on white first (JPEG has none). A picture larger than
    /// <see cref="MaxSide"/> pixels on its long side is reduced by a power of two, so a very large photo does not run
    /// the phone out of memory.</para>
    /// <para>Never logs a path, a name or anything about the picture.</para>
    /// </remarks>
    internal sealed class AndroidPhotoNormaliser : IPhotoNormaliser
    {
        /// <summary>The JPEG quality written.</summary>
        internal const int Quality = 92;

        /// <summary>The longest side kept, in pixels.</summary>
        internal const int MaxSide = 4096;

        /// <inheritdoc />
        public PhotoFormat? WriteWithoutLocation(string sourcePath, PhotoFormat format, string targetPath)
        {
            Bitmap? bitmap = null;

            try
            {
                bitmap = OperatingSystem.IsAndroidVersionAtLeast(28) ? DecodeWithImageDecoder(sourcePath) : DecodeWithBitmapFactory(sourcePath);

                if (bitmap is null)
                {
                    return null;
                }

                if (bitmap.HasAlpha)
                {
                    var flat = Bitmap.CreateBitmap(bitmap.Width, bitmap.Height, Bitmap.Config.Argb8888!)!;

                    using (var canvas = new Canvas(flat))
                    {
                        canvas.DrawColor(global::Android.Graphics.Color.White);
                        canvas.DrawBitmap(bitmap, 0, 0, null);
                    }

                    bitmap.Recycle();
                    bitmap = flat;
                }

                using var target = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

                return bitmap.Compress(Bitmap.CompressFormat.Jpeg!, Quality, target) ? PhotoFormat.Jpeg : null;
            }
            catch (Java.Lang.Throwable)
            {
                // Out of memory, or a picture Android cannot read: refused.
                return null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
            finally
            {
                bitmap?.Recycle();
            }
        }

        /// <summary>Android 9+: HEIC too, upright by its orientation, in software memory, reduced when very large.</summary>
        [System.Runtime.Versioning.SupportedOSPlatform("android28.0")]
        private static Bitmap? DecodeWithImageDecoder(string path)
        {
            var source = ImageDecoder.CreateSource(new Java.IO.File(path));

            return ImageDecoder.DecodeBitmap(source, new HeaderDecoded());
        }

        /// <summary>Before Android 9: what <see cref="BitmapFactory"/> reads, turned by the EXIF orientation.</summary>
        private static Bitmap? DecodeWithBitmapFactory(string path)
        {
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeFile(path, bounds);

            if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
            {
                return null;
            }

            var bitmap = BitmapFactory.DecodeFile(path, new BitmapFactory.Options { InSampleSize = SampleFor(bounds.OutWidth, bounds.OutHeight) });

            if (bitmap is null)
            {
                return null;
            }

            ImageOrientation orientation;

            using (var exif = new ExifInterface(path))
            {
                orientation = ImageOrientation.FromExif(exif.GetAttributeInt(ExifInterface.TagOrientation, 1));
            }

            if (orientation.IsAsStored)
            {
                return bitmap;
            }

            using var matrix = new Matrix();

            if (orientation.FlipHorizontal)
            {
                matrix.PostScale(-1, 1);
            }

            matrix.PostRotate(orientation.RotateDegrees);

            var turned = Bitmap.CreateBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height, matrix, filter: true);

            if (!ReferenceEquals(turned, bitmap))
            {
                bitmap.Recycle();
            }

            return turned;
        }

        /// <summary>The power-of-two reduction that brings the long side to at most <see cref="MaxSide"/>.</summary>
        private static int SampleFor(int width, int height)
        {
            var sample = 1;

            while (Math.Max(width, height) / sample > MaxSide)
            {
                sample *= 2;
            }

            return sample;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("android28.0")]
        private sealed class HeaderDecoded : Java.Lang.Object, ImageDecoder.IOnHeaderDecodedListener
        {
            public void OnHeaderDecoded(ImageDecoder decoder, ImageDecoder.ImageInfo info, ImageDecoder.Source source)
            {
                // Software memory: the bitmap is written out as JPEG, never drawn from the GPU.
                decoder.Allocator = ImageDecoderAllocator.Software;

                var size = info.Size;

                if (size is not null)
                {
                    decoder.SetTargetSampleSize(SampleFor(size.Width, size.Height));
                }
            }
        }
    }
}
