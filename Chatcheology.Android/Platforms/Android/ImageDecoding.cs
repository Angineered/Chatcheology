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
            var key = CoverKey(path, targetWidth, targetHeight);

            if (Cache.Get(key) is Bitmap cached)
            {
                return cached;
            }

            var bitmap = Decode(path, (width, height) => ImageSampling.CoverSample(width, height, targetWidth, targetHeight));

            if (bitmap is null)
            {
                return null;
            }

            // Only the part the box shows is kept (CH-66 scroll follow-up), so more thumbnails fit the cache;
            // still more than half again too large, it is also scaled to just cover the target.
            var crop = ImageSampling.CoverCropOf(bitmap.Width, bitmap.Height, targetWidth, targetHeight);

            if (!crop.IsWhole(bitmap.Width, bitmap.Height))
            {
                bitmap = Crop(bitmap, crop);
            }

            if (bitmap is not null)
            {
                Cache.Put(key, bitmap);
            }

            return bitmap;
        }

        /// <summary>
        /// The thumbnail <see cref="DecodeToCover"/> made for these arguments when it is still in the cache, else
        /// <see langword="null"/>. Reads memory only, so it may be called on the UI thread: a cell scrolled back to
        /// is drawn at once instead of after a trip through a worker.
        /// </summary>
        internal static Bitmap? CachedCover(string path, int targetWidth, int targetHeight) =>
            Cache.Get(CoverKey(path, targetWidth, targetHeight)) as Bitmap;

        private static string CoverKey(string path, int targetWidth, int targetHeight) => $"{targetWidth}x{targetHeight}:{path}";

        /// <summary>
        /// A saved thumbnail (CH-66) of the picture at <paramref name="path"/>, read from <paramref name="savedPath"/> and
        /// kept in the memory cache as <see cref="DecodeToCover"/> would keep the picture's own, so
        /// <see cref="CachedCover"/> finds it. <see langword="null"/> when the saved file does not decode. Off the UI thread.
        /// </summary>
        internal static Bitmap? DecodeSavedCover(string savedPath, string path, int targetWidth, int targetHeight)
        {
            var key = CoverKey(path, targetWidth, targetHeight);

            if (Cache.Get(key) is Bitmap cached)
            {
                return cached;
            }

            Bitmap? bitmap;

            try
            {
                bitmap = BitmapFactory.DecodeFile(savedPath);
            }
            catch (Java.Lang.Throwable)
            {
                return null;
            }

            if (bitmap is not null)
            {
                Cache.Put(key, bitmap);
            }

            return bitmap;
        }

        /// <summary>
        /// Writes <paramref name="bitmap"/> (a cover thumbnail) to <paramref name="stream"/> as a JPEG of at most
        /// <paramref name="targetWidth"/> by <paramref name="targetHeight"/>, for the saved thumbnails (CH-66). The bitmap is
        /// shared and left as it is. Whether it was written.
        /// </summary>
        internal static bool WriteCoverJpeg(Bitmap bitmap, int targetWidth, int targetHeight, System.IO.Stream stream)
        {
            Bitmap? scaled = null;

            try
            {
                // JPEG has no transparency: a see-through picture is never saved, so it keeps looking as it does.
                if (bitmap.HasAlpha)
                {
                    return false;
                }

                // A thumbnail kept up to half again larger in memory is saved at the cell's size: smaller files.
                var scale = Math.Max((double)targetWidth / bitmap.Width, (double)targetHeight / bitmap.Height);

                if (scale < 1)
                {
                    scaled = Bitmap.CreateScaledBitmap(
                        bitmap, Math.Max(1, (int)Math.Round(bitmap.Width * scale)), Math.Max(1, (int)Math.Round(bitmap.Height * scale)), filter: true);
                }

                return (scaled ?? bitmap).Compress(Bitmap.CompressFormat.Jpeg!, 85, stream);
            }
            catch (Java.Lang.Throwable)
            {
                return false;
            }
            finally
            {
                if (scaled is not null && !ReferenceEquals(scaled, bitmap))
                {
                    scaled.Recycle();
                }
            }
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
                using var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
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
                using var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(path, bounds);

                if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
                {
                    return null;
                }

                orientation = ReadOrientation(path);

                // The reduction is chosen for the shape the picture is shown at, not the one it is stored at.
                var (width, height) = orientation.UprightSize(bounds.OutWidth, bounds.OutHeight);

                using var options = new BitmapFactory.Options { InSampleSize = sample(width, height) };
                bitmap = BitmapFactory.DecodeFile(path, options);
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

        /// <summary>
        /// The part of the bitmap <paramref name="crop"/> names, at its scale; the original is released. When that
        /// fails the bitmap is kept whole: the view's centre crop shows the same part.
        /// </summary>
        private static Bitmap Crop(Bitmap bitmap, CoverCrop crop)
        {
            try
            {
                using var matrix = new Matrix();
                matrix.SetScale((float)crop.Scale, (float)crop.Scale);

                var part = Bitmap.CreateBitmap(bitmap, crop.X, crop.Y, crop.Width, crop.Height, matrix, filter: true);

                if (!ReferenceEquals(part, bitmap))
                {
                    bitmap.Recycle();
                }

                return part;
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
