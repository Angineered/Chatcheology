using Chatcheology.Mobile.Presentation;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>The EXIF orientation rule both apps turn pictures by (CH-57).</summary>
    public sealed class ImageOrientationTests
    {
        [Theory]
        [InlineData(1, 0, false, false)]
        [InlineData(2, 0, true, false)]
        [InlineData(3, 180, false, false)]
        [InlineData(4, 180, true, false)]
        [InlineData(5, 270, true, true)]
        [InlineData(6, 90, false, true)]
        [InlineData(7, 90, true, true)]
        [InlineData(8, 270, false, true)]
        public void Each_tag_value_maps_to_its_turn(int tag, int degrees, bool flip, bool swaps)
        {
            var orientation = ImageOrientation.FromExif(tag);

            Assert.Equal(degrees, orientation.RotateDegrees);
            Assert.Equal(flip, orientation.FlipHorizontal);
            Assert.Equal(swaps, orientation.SwapsSides);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0)]
        [InlineData(9)]
        [InlineData(-1)]
        [InlineData(65535)]
        public void Missing_zero_or_out_of_range_values_mean_as_stored(int? tag)
        {
            var orientation = ImageOrientation.FromExif(tag);

            Assert.Equal(ImageOrientation.AsStored, orientation);
            Assert.True(orientation.IsAsStored);
            Assert.False(orientation.SwapsSides);
        }

        [Fact]
        public void The_upright_size_swaps_only_when_the_sides_swap()
        {
            Assert.Equal((40, 30), ImageOrientation.FromExif(3).UprightSize(40, 30));
            Assert.Equal((30, 40), ImageOrientation.FromExif(6).UprightSize(40, 30));
            Assert.Equal((30, 40), ImageOrientation.FromExif(5).UprightSize(40, 30));
            Assert.Equal((40, 30), ImageOrientation.FromExif(null).UprightSize(40, 30));
        }

        /// <summary>
        /// Flip first, then turn clockwise, lands the stored corners where the EXIF definition puts
        /// them: the stored corner shown at the top left, and the one shown at the top right.
        /// </summary>
        [Theory]
        [InlineData(1, "TL", "TR")]
        [InlineData(2, "TR", "TL")]
        [InlineData(3, "BR", "BL")]
        [InlineData(4, "BL", "BR")]
        [InlineData(5, "TL", "BL")]
        [InlineData(6, "BL", "TL")]
        [InlineData(7, "BR", "TR")]
        [InlineData(8, "TR", "BR")]
        public void Flip_then_turn_puts_the_corners_where_EXIF_says(int tag, string shownTopLeft, string shownTopRight)
        {
            // A non-square stored picture, 3 wide by 2 high, its corners named.
            string[,] stored =
            {
                { "TL", "-", "TR" },
                { "BL", "-", "BR" },
            };

            var shown = Apply(stored, ImageOrientation.FromExif(tag));

            Assert.Equal(shownTopLeft, shown[0, 0]);
            Assert.Equal(shownTopRight, shown[0, shown.GetLength(1) - 1]);
        }

        private static string[,] Apply(string[,] picture, ImageOrientation orientation)
        {
            if (orientation.FlipHorizontal)
            {
                picture = Flip(picture);
            }

            for (var turn = 0; turn < orientation.RotateDegrees / 90; turn++)
            {
                picture = TurnClockwise(picture);
            }

            return picture;
        }

        private static string[,] Flip(string[,] picture)
        {
            int rows = picture.GetLength(0), columns = picture.GetLength(1);
            var result = new string[rows, columns];

            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    result[row, column] = picture[row, columns - 1 - column];
                }
            }

            return result;
        }

        private static string[,] TurnClockwise(string[,] picture)
        {
            int rows = picture.GetLength(0), columns = picture.GetLength(1);
            var result = new string[columns, rows];

            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    result[column, rows - 1 - row] = picture[row, column];
                }
            }

            return result;
        }
    }
}
