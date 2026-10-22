using Android.Graphics;
using Android.Media;

namespace Chatcheology.Android
{
    /// <summary>
    /// Preview frames of restored videos (CH-40): one frame at about one second, read with Android's
    /// <see cref="MediaMetadataRetriever"/>, scaled down and kept in the app's cache folder under the media
    /// hash, so it is read from the video once. Android may clear the cache; the frame is then read again.
    /// </summary>
    /// <remarks>
    /// Nothing is ever written into the archive. A video whose frame cannot be read gets an empty marker
    /// file instead, so its card is shown straight away the next time, without trying again.
    /// </remarks>
    internal static class VideoFrames
    {
        /// <summary>The widest a stored frame is, in pixels: enough for the card on any phone.</summary>
        private const int StoredWidth = 720;

        /// <summary>Where frames are kept: a folder of the app's cache.</summary>
        private static string Folder => System.IO.Path.Combine(FileSystem.CacheDirectory, "video-frames");

        /// <summary>
        /// Whether the video with this media hash is already known to have no readable frame, so its card is
        /// shown directly. Cheap: one file check.
        /// </summary>
        internal static bool IsKnownWithoutFrame(string mediaSha256) =>
            IsHash(mediaSha256) && File.Exists(System.IO.Path.Combine(Folder, mediaSha256 + ".none"));

        /// <summary>
        /// The video's preview frame decoded to cover <paramref name="targetWidth"/> by
        /// <paramref name="targetHeight"/> pixels, from the cache folder when it is there; <see langword="null"/>
        /// when no frame can be read. Call it off the UI thread.
        /// </summary>
        internal static Bitmap? FrameToCover(string videoPath, string mediaSha256, int targetWidth, int targetHeight)
        {
            if (!IsHash(mediaSha256))
            {
                return null;
            }

            var stored = System.IO.Path.Combine(Folder, mediaSha256 + ".jpg");

            if (!File.Exists(stored))
            {
                if (IsKnownWithoutFrame(mediaSha256) || !Store(videoPath, mediaSha256, stored))
                {
                    return null;
                }
            }

            return ImageDecoding.DecodeToCover(stored, targetWidth, targetHeight);
        }

        /// <summary>Reads the frame and writes it to <paramref name="stored"/>; on failure writes the marker.</summary>
        private static bool Store(string videoPath, string mediaSha256, string stored)
        {
            Bitmap? frame = null;

            try
            {
                Directory.CreateDirectory(Folder);

                using (var retriever = new MediaMetadataRetriever())
                {
                    retriever.SetDataSource(videoPath);

                    var duration = long.TryParse(retriever.ExtractMetadata(MetadataKey.Duration), out var ms) ? ms : 0;
                    var at = duration > 1000 || duration == 0 ? 1_000_000L : duration * 500L;

                    frame = retriever.GetFrameAtTime(at, Option.ClosestSync) ?? retriever.GetFrameAtTime(0, Option.ClosestSync);
                }

                if (frame is null || frame.Width <= 0 || frame.Height <= 0)
                {
                    MarkWithoutFrame(mediaSha256);
                    return false;
                }

                if (frame.Width > StoredWidth)
                {
                    var scaled = Bitmap.CreateScaledBitmap(frame, StoredWidth, Math.Max(1, (int)Math.Round((double)frame.Height * StoredWidth / frame.Width)), filter: true);

                    if (!ReferenceEquals(scaled, frame))
                    {
                        frame.Recycle();
                        frame = scaled;
                    }
                }

                // Written beside, then moved: a frame half written by a killed app is never read.
                var partial = stored + ".part";

                using (var file = File.Create(partial))
                {
                    frame.Compress(Bitmap.CompressFormat.Jpeg!, 85, file);
                }

                File.Move(partial, stored, overwrite: true);
                return true;
            }
            catch (Exception exception) when (exception is Java.Lang.Throwable or IOException or UnauthorizedAccessException)
            {
                MarkWithoutFrame(mediaSha256);
                return false;
            }
            finally
            {
                frame?.Recycle();
            }
        }

        private static void MarkWithoutFrame(string mediaSha256)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllBytes(System.IO.Path.Combine(Folder, mediaSha256 + ".none"), []);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Only a shortcut for next time.
            }
        }

        /// <summary>Only a media hash names a cache file: 64 lower-case hex digits, nothing else.</summary>
        private static bool IsHash(string? value) =>
            value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}
