using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Memories;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// CH-68: a memory's details lay out its photos as nothing (none), one photo at its own shape (one), or a grid
    /// of square tiles three across, at most nine, the ninth carrying "+N" when there are more.
    /// </summary>
    /// <remarks>Invented names, paths and bytes only.</remarks>
    public sealed class MemoryPhotoLayoutTests : IDisposable
    {
        private readonly TemporaryFolder _root = new();
        private readonly MemorySettings _store = new();

        public void Dispose() => _root.Dispose();

        private static List<MemoryPhoto> Photos(int count, params int[] captioned) =>
            [.. Enumerable.Range(0, count).Select(i => new MemoryPhoto("/invented/photo-" + i + ".jpg", captioned.Contains(i) ? "Invented caption" : null))];

        [Fact]
        public void NoPhoto_DrawsNothing()
        {
            var layout = MemoryPhotoLayout.For([]);

            Assert.Equal(MemoryPhotoArrangement.None, layout.Arrangement);
            Assert.Empty(layout.Tiles);
            Assert.Equal(0, layout.Rows);
            Assert.Equal(0, layout.GridHeight(100));
        }

        [Fact]
        public void OnePhoto_IsTheSinglePhoto_NotAGrid()
        {
            var layout = MemoryPhotoLayout.For(Photos(1));

            Assert.Equal(MemoryPhotoArrangement.Single, layout.Arrangement);
            Assert.Empty(layout.Tiles);
            Assert.Equal(1, layout.PhotoCount);
            Assert.Equal("Photo", layout.SingleDescription);
            Assert.Equal("Photo, has a caption", MemoryPhotoLayout.For(Photos(1, 0)).SingleDescription);
        }

        [Theory]
        [InlineData(2, 2, 1)]
        [InlineData(3, 3, 1)]
        [InlineData(4, 4, 2)]
        [InlineData(6, 6, 2)]
        [InlineData(7, 7, 3)]
        [InlineData(9, 9, 3)]
        [InlineData(10, 9, 3)]
        [InlineData(40, 9, 3)]
        public void TwoOrMore_AGridThreeAcross_OnlyAsManyRowsAsNeeded_AtMostNine(int count, int tiles, int rows)
        {
            var layout = MemoryPhotoLayout.For(Photos(count));

            Assert.Equal(MemoryPhotoArrangement.Grid, layout.Arrangement);
            Assert.Equal(tiles, layout.Tiles.Count);
            Assert.Equal(rows, layout.Rows);
            Assert.Equal(count, layout.PhotoCount);

            // Cover first, in order, row by row.
            Assert.Equal(Enumerable.Range(0, tiles), layout.Tiles.Select(t => t.PhotoIndex));
            Assert.All(layout.Tiles, t => Assert.Equal((t.PhotoIndex / 3, t.PhotoIndex % 3), (t.Row, t.Column)));
        }

        [Theory]
        [InlineData(2, 0)]
        [InlineData(9, 0)]
        [InlineData(10, 1)]
        [InlineData(12, 3)]
        public void TheNinthTile_CarriesPlusTheNumberNotShown_OnlyWhenThereAreMore(int count, int more)
        {
            var layout = MemoryPhotoLayout.For(Photos(count));
            var last = layout.Tiles[^1];

            Assert.Equal(more, last.MoreCount);
            Assert.Equal(more > 0 ? "+" + more : string.Empty, last.MoreText);
            Assert.All(layout.Tiles.Take(layout.Tiles.Count - 1), t => Assert.Equal(0, t.MoreCount));

            // The "+N" counts the photos not drawn as tiles, so tiles plus N is every photo.
            Assert.Equal(count, layout.Tiles.Count + last.MoreCount);
        }

        [Fact]
        public void ScreenReaders_HearThePhotoNumber_ACaption_AndHowManyMore()
        {
            var layout = MemoryPhotoLayout.For(Photos(11, 1, 8));

            Assert.Equal("Photo 1 of 11", layout.Tiles[0].Description);
            Assert.Equal("Photo 2 of 11, has a caption", layout.Tiles[1].Description);
            Assert.Equal("Photo 9 of 11, has a caption, 2 more photos", layout.Tiles[8].Description);
            Assert.Equal([false, true, false, false, false, false, false, false, true], layout.Tiles.Select(t => t.HasCaption));

            Assert.Equal("Photo 9 of 10, 1 more photo", MemoryPhotoLayout.For(Photos(10)).Tiles[8].Description);
        }

        [Theory]
        [InlineData(328, 108)]
        [InlineData(360, 118)]
        [InlineData(100, 32)]
        public void Tiles_AreSizedFromTheWidth_ThreeTilesAndTwoGaps(double width, double tile)
        {
            Assert.Equal(tile, MemoryPhotoLayout.TileSize(width));
            Assert.True(3 * tile + 2 * MemoryPhotoLayout.Gap <= width);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void AWidthNotKnownYet_GivesNoTiles(double width) => Assert.Equal(0, MemoryPhotoLayout.TileSize(width));

        [Fact]
        public void TheGridHeight_IsItsRowsAndTheGapsBetweenThem()
        {
            Assert.Equal(100, MemoryPhotoLayout.For(Photos(3)).GridHeight(100));
            Assert.Equal(202, MemoryPhotoLayout.For(Photos(5)).GridHeight(100));
            Assert.Equal(304, MemoryPhotoLayout.For(Photos(30)).GridHeight(100));
        }

        [Fact]
        public void ALandscapePhoto_IsFullWidth_AtItsOwnShape()
        {
            Assert.Equal((328, 246), MemoryPhotoLayout.SingleSize(4000, 3000, 328, 700));
            Assert.Equal((328, 164), MemoryPhotoLayout.SingleSize(2000, 1000, 328, 700));
        }

        [Fact]
        public void ATallPhoto_StopsAtSixtyPercentOfThePage_AndKeepsItsShape()
        {
            // 3:4 at 328 wide would be 437 high; 60 % of 600 is 360, so 270 wide.
            var (width, height) = MemoryPhotoLayout.SingleSize(3000, 4000, 328, 600);

            Assert.Equal(360, height);
            Assert.Equal(270, width);

            // A very tall strip: still within the cap, still its shape.
            var strip = MemoryPhotoLayout.SingleSize(100, 2000, 328, 600);
            Assert.Equal(360, strip.Height);
            Assert.Equal(18, strip.Width);
        }

        [Fact]
        public void APhotoWhoseSizeCannotBeRead_UsesFourByThree()
        {
            Assert.Equal((320, 240), MemoryPhotoLayout.SingleSize(0, 0, 320, 800));
            Assert.Equal((320, 240), MemoryPhotoLayout.SingleSize(-1, 500, 320, 800));
        }

        [Theory]
        [InlineData(0, 700)]
        [InlineData(328, 0)]
        [InlineData(double.NaN, 700)]
        [InlineData(328, double.PositiveInfinity)]
        public void APageSizeNotKnownYet_GivesNoBox(double width, double height) =>
            Assert.Equal((0, 0), MemoryPhotoLayout.SingleSize(4000, 3000, width, height));

        [Fact]
        public void FromAnArchive_TheLayoutUsesOnlyPhotosWhoseFileIsThere_CoverFirst()
        {
            var builder = new MemoryArchiveBuilder();
            builder.Message(new DateTime(2025, 3, 10, 20, 0, 0));
            builder.Memory(20, "Invented trip", new DateTime(2025, 3, 10), image: true, photoCaptions: [.. Enumerable.Range(0, 11).Select(i => i == 2 ? "Invented" : null)]);
            builder.Memory(21, "Invented day", new DateTime(2025, 3, 10), image: true);
            builder.Memory(22, "Invented note", new DateTime(2025, 3, 10));

            var library = new ArchiveLibrary(Path.Combine(_root.Path, "library"));
            var settings = new ArchiveSettings(_store);
            var staging = library.CreateStagingFolder();
            builder.Write(staging);
            Assert.True(library.Install(staging).Installed);
            var archive = library.ListArchives(settings).Single();

            // One extra photo's file removed after install: neither a tile nor counted in "+N".
            using (var reader = ArchiveReader.Open(archive.Folder))
            {
                File.Delete(reader.EntryImageFullPath(reader.ReadEntryImages(20)[0]));
            }

            using var session = ArchiveSession.Open(archive, settings);

            var trip = session.MemoryDetails(new MemoryKey(20, null))!;
            var layout = MemoryPhotoLayout.For(trip.Photos);

            // Cover plus ten present extras: nine tiles, "+2".
            Assert.Equal(11, trip.Photos.Count);
            Assert.Equal(MemoryPhotoArrangement.Grid, layout.Arrangement);
            Assert.Equal(9, layout.Tiles.Count);
            Assert.Equal(2, layout.Tiles[^1].MoreCount);
            Assert.Equal(trip.ImagePath, trip.Photos[layout.Tiles[0].PhotoIndex].Path);

            // The captioned extra (the third; the first is gone) follows the cover and one extra: photo index 2.
            Assert.True(layout.Tiles[2].HasCaption);
            Assert.Equal("Photo 3 of 11, has a caption", layout.Tiles[2].Description);

            // Every tile opens the viewer over all the photos, at its own.
            Assert.All(layout.Tiles, t => Assert.Equal(trip.Photos[t.PhotoIndex].Path, trip.BrowsePhotos(t.PhotoIndex).Current.Path));
            Assert.Equal(11, trip.BrowsePhotos(8).Photos.Count);

            Assert.Equal(MemoryPhotoArrangement.Single, MemoryPhotoLayout.For(session.MemoryDetails(new MemoryKey(21, null))!.Photos).Arrangement);
            Assert.Equal(MemoryPhotoArrangement.None, MemoryPhotoLayout.For(session.MemoryDetails(new MemoryKey(22, null))!.Photos).Arrangement);
        }
    }
}
