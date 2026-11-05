namespace Chatcheology.Mobile.Presentation
{
    /// <summary>
    /// How a stored picture is turned to show it the right way up, from its EXIF orientation tag
    /// (tag 274, values 1-8) (CH-57).
    /// </summary>
    /// <remarks>
    /// <para><strong>Order:</strong> the picture is first mirrored left-to-right when
    /// <see cref="FlipHorizontal"/> is set, then rotated clockwise by <see cref="RotateDegrees"/>.
    /// Both apps apply it in that order.</para>
    /// <para>A missing tag, 0, or anything outside 1-8 means "as stored" (value 1): an unreadable tag
    /// never changes how a picture is shown today.</para>
    /// </remarks>
    /// <param name="RotateDegrees">Clockwise turn after any flip: 0, 90, 180 or 270.</param>
    /// <param name="FlipHorizontal">Whether the stored picture is mirrored left-to-right first.</param>
    public readonly record struct ImageOrientation(int RotateDegrees, bool FlipHorizontal)
    {
        /// <summary>The EXIF orientation tag number.</summary>
        public const int ExifTag = 274;

        /// <summary>Shown as stored.</summary>
        public static ImageOrientation AsStored => default;

        /// <summary>Whether the upright picture's width is the stored height (values 5-8).</summary>
        public bool SwapsSides => RotateDegrees is 90 or 270;

        /// <summary>Whether nothing needs doing.</summary>
        public bool IsAsStored => RotateDegrees == 0 && !FlipHorizontal;

        /// <summary>The turn for EXIF orientation <paramref name="tag"/>.</summary>
        public static ImageOrientation FromExif(int? tag) => tag switch
        {
            2 => new(0, true),      // mirrored left-to-right
            3 => new(180, false),   // upside down
            4 => new(180, true),    // mirrored top-to-bottom
            5 => new(270, true),    // transpose (mirrored along the top-left diagonal)
            6 => new(90, false),    // turn 90 clockwise to show
            7 => new(90, true),     // transverse (mirrored along the top-right diagonal)
            8 => new(270, false),   // turn 90 anticlockwise to show
            _ => AsStored,          // 1, 0, missing, out of range
        };

        /// <summary>The upright size of a picture stored as <paramref name="width"/> by <paramref name="height"/>.</summary>
        public (int Width, int Height) UprightSize(int width, int height) =>
            SwapsSides ? (height, width) : (width, height);
    }
}
