using System.Globalization;

namespace Chatcheology.Mobile.Memories
{
    /// <summary>How the details page lays out a memory's photos (CH-68).</summary>
    public enum MemoryPhotoArrangement
    {
        /// <summary>No photo: nothing is drawn and no space is kept.</summary>
        None,

        /// <summary>One photo: full width at its own shape, at most a share of the page height, centred.</summary>
        Single,

        /// <summary>Two or more: a grid of square tiles, three across, at most nine tiles.</summary>
        Grid,
    }

    /// <summary>One tile of the photo grid.</summary>
    /// <param name="PhotoIndex">The photo it shows, and where the viewer opens on a tap.</param>
    /// <param name="Row">Its row, from 0.</param>
    /// <param name="Column">Its column, from 0.</param>
    /// <param name="MoreCount">How many photos are not shown as tiles when this is the last tile and there are more; otherwise 0.</param>
    /// <param name="HasCaption">Whether its photo has a caption (the tile then carries a small caption mark).</param>
    /// <param name="Description">What a screen reader says: "Photo 2 of 5", ", has a caption", ", 4 more photos".</param>
    public sealed record MemoryPhotoTile(int PhotoIndex, int Row, int Column, int MoreCount, bool HasCaption, string Description)
    {
        /// <summary>The "+N" the last tile shows over its photo, or empty.</summary>
        public string MoreText => MoreCount > 0 ? "+" + MoreCount.ToString(CultureInfo.CurrentCulture) : string.Empty;
    }

    /// <summary>
    /// The photo section of a memory's details (CH-68), worked out from the photos <see cref="MemoryDetails.Photos"/>
    /// holds: only photos whose file is there, the cover first (when the cover's file is gone the first present
    /// photo leads, as CH-46 has it). No photo: nothing. One: <see cref="MemoryPhotoArrangement.Single"/>. Two or
    /// more: a grid of square tiles, three across, at most nine; with more than nine the ninth carries "+N".
    /// </summary>
    public sealed class MemoryPhotoLayout
    {
        /// <summary>Tiles across a row.</summary>
        public const int Columns = 3;

        /// <summary>The most tiles drawn.</summary>
        public const int MaxTiles = 9;

        /// <summary>The space between tiles, in device-independent units.</summary>
        public const double Gap = 2;

        /// <summary>The highest a single photo may be, as a share of the page height.</summary>
        public const double SingleMaxHeightShare = 0.6;

        /// <summary>The shape a single photo is drawn at when its size cannot be read (width over height).</summary>
        public const double FallbackAspect = 4.0 / 3.0;

        private MemoryPhotoLayout(MemoryPhotoArrangement arrangement, IReadOnlyList<MemoryPhotoTile> tiles, int photoCount)
        {
            Arrangement = arrangement;
            Tiles = tiles;
            PhotoCount = photoCount;
        }

        /// <summary>How the photos are drawn.</summary>
        public MemoryPhotoArrangement Arrangement { get; }

        /// <summary>The grid's tiles, in order (empty unless <see cref="MemoryPhotoArrangement.Grid"/>).</summary>
        public IReadOnlyList<MemoryPhotoTile> Tiles { get; }

        /// <summary>Every photo the viewer swipes through.</summary>
        public int PhotoCount { get; }

        /// <summary>The grid's rows: only as many as its tiles need.</summary>
        public int Rows => (Tiles.Count + Columns - 1) / Columns;

        /// <summary>What a screen reader says for the single photo: "Photo", with ", has a caption" when it has one.</summary>
        public string SingleDescription { get; private init; } = string.Empty;

        /// <summary>The layout of <paramref name="photos"/> (each with its file, the cover first).</summary>
        public static MemoryPhotoLayout For(IReadOnlyList<MemoryPhoto> photos)
        {
            ArgumentNullException.ThrowIfNull(photos);

            if (photos.Count == 0)
            {
                return new MemoryPhotoLayout(MemoryPhotoArrangement.None, [], 0);
            }

            if (photos.Count == 1)
            {
                return new MemoryPhotoLayout(MemoryPhotoArrangement.Single, [], 1)
                {
                    SingleDescription = Describe(0, 1, photos[0].HasCaption, 0),
                };
            }

            var shown = Math.Min(photos.Count, MaxTiles);
            var tiles = new List<MemoryPhotoTile>(shown);

            for (var index = 0; index < shown; index++)
            {
                var more = index == shown - 1 ? photos.Count - shown : 0;

                tiles.Add(new MemoryPhotoTile(
                    index,
                    index / Columns,
                    index % Columns,
                    more,
                    photos[index].HasCaption,
                    Describe(index, photos.Count, photos[index].HasCaption, more)));
            }

            return new MemoryPhotoLayout(MemoryPhotoArrangement.Grid, tiles, photos.Count);
        }

        /// <summary>
        /// A tile's side for a grid <paramref name="width"/> wide (three tiles and two gaps), whole units so the
        /// tiles line up; 0 when the width is not known yet.
        /// </summary>
        public static double TileSize(double width)
        {
            if (!(width > 0) || double.IsInfinity(width))
            {
                return 0;
            }

            return Math.Max(0, Math.Floor((width - Gap * (Columns - 1)) / Columns));
        }

        /// <summary>The grid's height for tiles of <paramref name="tileSize"/>.</summary>
        public double GridHeight(double tileSize) =>
            Rows == 0 || tileSize <= 0 ? 0 : Rows * tileSize + (Rows - 1) * Gap;

        /// <summary>
        /// The box a single photo of <paramref name="imageWidth"/> by <paramref name="imageHeight"/> (its upright
        /// size) is drawn in: as wide as <paramref name="availableWidth"/> at the photo's own shape, unless that is
        /// taller than <see cref="SingleMaxHeightShare"/> of <paramref name="pageHeight"/>, then that high and
        /// narrower (the page centres it). The box has the photo's shape, so nothing is cropped. A size that is
        /// not known (0 or less) uses <see cref="FallbackAspect"/>. (0, 0) when the page size is not known yet.
        /// </summary>
        public static (double Width, double Height) SingleSize(int imageWidth, int imageHeight, double availableWidth, double pageHeight)
        {
            if (!(availableWidth > 0) || double.IsInfinity(availableWidth) || !(pageHeight > 0) || double.IsInfinity(pageHeight))
            {
                return (0, 0);
            }

            var aspect = imageWidth > 0 && imageHeight > 0 ? (double)imageWidth / imageHeight : FallbackAspect;
            var maxHeight = Math.Floor(pageHeight * SingleMaxHeightShare);
            var width = Math.Floor(availableWidth);
            var height = Math.Round(width / aspect);

            if (height > maxHeight)
            {
                height = maxHeight;
                width = Math.Round(height * aspect);
            }

            return (Math.Max(1, width), Math.Max(1, height));
        }

        private static string Describe(int index, int count, bool hasCaption, int more)
        {
            var text = MemoryWording.PhotoTitle(index, count);

            if (hasCaption)
            {
                text += ", has a caption";
            }

            if (more > 0)
            {
                text += ", " + more.ToString(CultureInfo.CurrentCulture) + (more == 1 ? " more photo" : " more photos");
            }

            return text;
        }
    }
}
