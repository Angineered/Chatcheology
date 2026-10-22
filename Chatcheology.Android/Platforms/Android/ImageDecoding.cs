using Android.Graphics;
using Android.Util;

namespace Chatcheology.Android
{
    /// <summary>
    /// Decodes pictures on the Android side at the size they are shown, never at full size: the file's
    /// dimensions are read first, then it is decoded with the largest power-of-two reduction that still
    /// covers the target, and scaled down to the target if that is still larger.
    /// </summary>
    internal static class ImageDecoding
    {
        private static readonly BitmapCache Cache = new((int)Math.Min(Java.Lang.Runtime.GetRuntime()!.MaxMemory() / 8, 64L * 1024 * 1024));

        /// <summary>
        /// The picture at <paramref name="path"/> decoded to cover <paramref name="targetWidth"/> by
        /// <paramref name="targetHeight"/> pixels, from the cache when it is there; <see langword="null"/>
        /// when it cannot be decoded. Call it off the UI thread.
        /// </summary>
        internal static Bitmap? Decode(string path, int targetWidth, int targetHeight, bool cache = true)
        {
            var key = $"{targetWidth}x{targetHeight}:{path}";

            if (cache && Cache.Get(key) is Bitmap cached)
            {
                return cached;
            }

            try
            {
                var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(path, bounds);

                if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
                {
                    return null;
                }

                var sample = 1;

                while (bounds.OutWidth / (sample * 2) >= targetWidth && bounds.OutHeight / (sample * 2) >= targetHeight)
                {
                    sample *= 2;
                }

                var bitmap = BitmapFactory.DecodeFile(path, new BitmapFactory.Options { InSampleSize = sample });

                if (bitmap is null)
                {
                    return null;
                }

                // Still more than half again too large: scale to just cover the target.
                var scale = Math.Max((double)targetWidth / bitmap.Width, (double)targetHeight / bitmap.Height);

                if (scale < 0.67)
                {
                    var scaled = Bitmap.CreateScaledBitmap(
                        bitmap, Math.Max(1, (int)Math.Round(bitmap.Width * scale)), Math.Max(1, (int)Math.Round(bitmap.Height * scale)), filter: true);

                    if (!ReferenceEquals(scaled, bitmap))
                    {
                        bitmap.Recycle();
                        bitmap = scaled;
                    }
                }

                if (cache)
                {
                    Cache.Put(key, bitmap);
                }

                return bitmap;
            }
            catch (Java.Lang.Throwable)
            {
                // Out of memory or an unreadable file: shown as "Can't preview".
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
