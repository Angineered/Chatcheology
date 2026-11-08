using Android.Graphics;
using Android.Media;
using Android.Util;
using Chatcheology.Mobile.Presentation;

namespace Chatcheology.Android
{
    /// <summary>
    /// Decodes pictures on the Android side at the size they are shown, never at full size: the file's
    /// dimensions are read first, then it is decoded with a power-of-two reduction
    /// (<see cref="ImageSampling"/>) and scaled down further if that is still larger than needed.
    /// </summary>
    internal static class ImageDecoding
    {
        private static readonly BitmapCache Cache = new((int)Math.Min(Java.Lang.Runtime.GetRuntime()!.MaxMemory() / 8, 64L * 1024 * 1024));

        /// <summary>
        /// The picture at <paramref name="path"/> decoded to cover <paramref name="targetWidth"/> by
        /// <paramref name="targetHeight"/> pixels (a thumbnail cropped to its box), from the cache when it
        /// is there; <see langword="null"/> when it cannot be decoded. Call it off the UI thread. Cached
        /// bitmaps are shared: the caller must not recycle them.
        /// </summary>
        internal static Bitmap? DecodeToCover(string path, int targetWidth, int targetHeight)
        {
            var key = $"{targetWidth}x{targetHeight}:{path}";

            if (Cache.Get(key) is Bitmap cached)
            {
                return cached;
            }

            var bitmap = Decode(path, (width, height) => ImageSampling.CoverSample(width, height, targetWidth, targetHeight));

            if (bitmap is null)
            {
                return null;
            }

            // Still more than half again too large: scale to just cover the target.
            var scale = Math.Max((double)targetWidth / bitmap.Width, (double)targetHeight / bitmap.Height);

            if (scale < 0.67)
            {
                bitmap = Scale(bitmap, (int)Math.Round(bitmap.Width * scale), (int)Math.Round(bitmap.Height * scale));
            }

            if (bitmap is not null)
            {
                Cache.Put(key, bitmap);
            }

            return bitmap;
        }

        /// <summary>
        /// The picture at <paramref name="path"/> for the full-screen viewer: reduced while decoding to no
        /// smaller than it is shown when it fits the screen, then fitted inside twice the screen (at most
        /// 4096 pixels a side) so zooming in stays sharp. Not cached: the caller owns the bitmap and
        /// recycles it. <see langword="null"/> when it cannot be decoded. Call it off the UI thread.
        /// </summary>
        internal static Bitmap? DecodeToFitScreen(string path, int screenWidth, int screenHeight)
        {
            var bitmap = Decode(path, (width, height) => ImageSampling.FitSample(width, height, screenWidth, screenHeight));

            if (bitmap is null)
            {
                return null;
            }

            var (width, height) = ImageSampling.FitInside(
                bitmap.Width, bitmap.Height, Math.Min(4096, screenWidth * 2), Math.Min(4096, screenHeight * 2));

            return width < bitmap.Width || height < bitmap.Height ? Scale(bitmap, width, height) : bitmap;
        }

        /// <summary>
        /// The picture's size the right way up (its EXIF orientation applied), read from its header only, never
        /// decoded; (0, 0) when it cannot be read. Call it off the UI thread.
        /// </summary>
        internal static (int Width, int Height) ReadUprightSize(string path)
        {
            try
            {
                var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(path, bounds);

                if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
                {
                    return (0, 0);
                }

                return ReadOrientation(path).UprightSize(bounds.OutWidth, bounds.OutHeight);
            }
            catch (Java.Lang.Throwable)
            {
                return (0, 0);
            }
        }

        /// <summary>
        /// Reads the picture's size and EXIF orientation, decodes it at the sample size
        /// <paramref name="sample"/> gives for its upright size, then turns it the right way up (CH-57).
        /// </summary>
        private static Bitmap? Decode(string path, Func<int, int, int> sample)
        {
            Bitmap? bitmap;
            ImageOrientation orientation;

            try
            {
                var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(path, bounds);

                if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
                {
                    return null;
                }

                orientation = ReadOrientation(path);

                // The reduction is chosen for the shape the picture is shown at, not the one it is stored at.
                var (width, height) = orientation.UprightSize(bounds.OutWidth, bounds.OutHeight);

                bitmap = BitmapFactory.DecodeFile(path, new BitmapFactory.Options { InSampleSize = sample(width, height) });
            }
            catch (Java.Lang.Throwable)
            {
                // Out of memory or an unreadable file: shown as "Can't preview".
                return null;
            }

            return bitmap is null || orientation.IsAsStored ? bitmap : Orient(bitmap, orientation);
        }

        /// <summary>The picture's EXIF orientation; as stored when there is none or it cannot be read.</summary>
        private static ImageOrientation ReadOrientation(string path)
        {
            try
            {
                using var exif = new ExifInterface(path);

                return ImageOrientation.FromExif(exif.GetAttributeInt(ExifInterface.TagOrientation, 1));
            }
            catch (Java.Lang.Throwable)
            {
                return ImageOrientation.AsStored;
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
            {
                return ImageOrientation.AsStored;
            }
        }

        /// <summary>
        /// The bitmap turned by <paramref name="orientation"/> (mirrored first, then rotated clockwise, as
        /// <see cref="ImageOrientation"/> defines); the original is released. When turning it fails the
        /// picture is shown as stored, as it was before CH-57, rather than not at all.
        /// </summary>
        private static Bitmap Orient(Bitmap bitmap, ImageOrientation orientation)
        {
            try
            {
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
            catch (Java.Lang.Throwable)
            {
                return bitmap;
            }
        }

        /// <summary>The bitmap scaled to the size given; the original is released.</summary>
        private static Bitmap? Scale(Bitmap bitmap, int width, int height)
        {
            try
            {
                var scaled = Bitmap.CreateScaledBitmap(bitmap, Math.Max(1, width), Math.Max(1, height), filter: true);

                if (!ReferenceEquals(scaled, bitmap))
                {
                    bitmap.Recycle();
                }

                return scaled;
            }
            catch (Java.Lang.Throwable)
            {
                bitmap.Recycle();
                return null;
            }
        }

        private sealed class BitmapCache(int maxBytes) : LruCache(maxBytes)
        {
            protected override int SizeOf(Java.Lang.Object? key, Java.Lang.Object? value) =>
                value is Bitmap bitmap ? bitmap.ByteCount : 1;
        }
    }
}
