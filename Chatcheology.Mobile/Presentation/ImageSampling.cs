namespace Chatcheology.Mobile.Presentation
{
    /// <summary>
    /// How far a picture is reduced while it is decoded (a power-of-two sample size, as Android's
    /// decoder takes it), so no picture is ever decoded at full size just to be shown smaller.
    /// </summary>
    public static class ImageSampling
    {
        /// <summary>
        /// For a picture cropped to fill a box (the conversation's thumbnails): the largest power of two
        /// that still leaves both sides at least the box's.
        /// </summary>
        public static int CoverSample(int width, int height, int boxWidth, int boxHeight)
        {
            Check(width, height, boxWidth, boxHeight);

            var sample = 1;

            while (width / (sample * 2) >= boxWidth && height / (sample * 2) >= boxHeight)
            {
                sample *= 2;
            }

            return sample;
        }

        /// <summary>
        /// For a picture shown whole on a screen (the full-screen viewer): the largest power of two that
        /// still leaves the picture at least as large as it is shown when it fits the screen, so it is
        /// sharp at 1x and zooming starts from the screen's own resolution.
        /// </summary>
        public static int FitSample(int width, int height, int screenWidth, int screenHeight)
        {
            Check(width, height, screenWidth, screenHeight);

            var sample = 1;

            // At sample s the decoded picture is width/s by height/s, and fitting it to the screen scales
            // it by min(screen/size) along the sides; while that is at most 1, it is still no smaller
            // than it is shown.
            while (true)
            {
                var next = sample * 2;
                var fitted = Math.Min((double)screenWidth * next / width, (double)screenHeight * next / height);

                if (fitted > 1)
                {
                    return sample;
                }

                sample = next;
            }
        }

        /// <summary>
        /// The size that fits <paramref name="width"/> by <paramref name="height"/> inside a box of at
        /// most <paramref name="maxWidth"/> by <paramref name="maxHeight"/>, keeping its shape; the size
        /// itself when it already fits.
        /// </summary>
        public static (int Width, int Height) FitInside(int width, int height, int maxWidth, int maxHeight)
        {
            Check(width, height, maxWidth, maxHeight);

            var scale = Math.Min(1.0, Math.Min((double)maxWidth / width, (double)maxHeight / height));

            return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        }

        /// <summary>
        /// The part of a <paramref name="width"/> by <paramref name="height"/> picture a box of
        /// <paramref name="boxWidth"/> by <paramref name="boxHeight"/> shows when the picture is scaled to cover it
        /// and centred (as a centre-crop image view draws it), and the scale to keep it at: 1 unless the picture
        /// is still more than half again too large (below 0.67), as the thumbnails were scaled before.
        /// </summary>
        /// <remarks>
        /// A thumbnail kept as only this part looks the same in its box and takes less memory, so more of them fit
        /// the cache (CH-66 scroll follow-up): a panorama keeps a square instead of its whole width.
        /// </remarks>
        public static CoverCrop CoverCropOf(int width, int height, int boxWidth, int boxHeight)
        {
            Check(width, height, boxWidth, boxHeight);

            var cover = Math.Max((double)boxWidth / width, (double)boxHeight / height);
            var cropWidth = Math.Clamp((int)Math.Round(boxWidth / cover), 1, width);
            var cropHeight = Math.Clamp((int)Math.Round(boxHeight / cover), 1, height);

            return new CoverCrop((width - cropWidth) / 2, (height - cropHeight) / 2, cropWidth, cropHeight, cover < 0.67 ? cover : 1);
        }

        private static void Check(int width, int height, int boxWidth, int boxHeight)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boxWidth);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boxHeight);
        }
    }

    /// <summary>The part of a picture a cover box shows (<see cref="ImageSampling.CoverCropOf"/>), in the picture's pixels.</summary>
    /// <param name="X">Left edge.</param>
    /// <param name="Y">Top edge.</param>
    /// <param name="Width">Width of the part.</param>
    /// <param name="Height">Height of the part.</param>
    /// <param name="Scale">The scale to keep the part at; 1 keeps its pixels.</param>
    public readonly record struct CoverCrop(int X, int Y, int Width, int Height, double Scale)
    {
        /// <summary>Whether this is the whole <paramref name="width"/> by <paramref name="height"/> picture, unscaled.</summary>
        public bool IsWhole(int width, int height) => X == 0 && Y == 0 && Width == width && Height == height && Scale == 1;
    }
}
