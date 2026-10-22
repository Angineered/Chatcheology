using Android.Graphics;
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

        /// <summary>Reads the picture's size, then decodes it at the sample size <paramref name="sample"/> gives for it.</summary>
        private static Bitmap? Decode(string path, Func<int, int, int> sample)
        {
            try
            {
                var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(path, bounds);

                if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
                {
                    return null;
                }

                return BitmapFactory.DecodeFile(path, new BitmapFactory.Options { InSampleSize = sample(bounds.OutWidth, bounds.OutHeight) });
            }
            catch (Java.Lang.Throwable)
            {
                // Out of memory or an unreadable file: shown as "Can't preview".
                return null;
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
