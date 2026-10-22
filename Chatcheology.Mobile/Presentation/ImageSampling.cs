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

        private static void Check(int width, int height, int boxWidth, int boxHeight)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boxWidth);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boxHeight);
        }
    }
}
