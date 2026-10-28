using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Memories;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// CH-46 on the phone: a memory's extra photos give its card a "+N" badge, its details a strip with
    /// captions, and the viewer moves through that memory's photos only. The Photos gallery is unchanged.
    /// </summary>
    /// <remarks>Invented names and bytes only.</remarks>
    public sealed class MemoryPhotosTests : IDisposable
    {
        private readonly TemporaryFolder _root = new();
        private readonly MemorySettings _store = new();

        public void Dispose() => _root.Dispose();

        private static readonly DateTime Day = new(2025, 3, 10, 20, 0, 0);

        private static MemoryArchiveBuilder Sample(bool photos)
        {
            var builder = new MemoryArchiveBuilder();
            builder.Message(Day);
            builder.Photo(Day.AddMinutes(1));
            builder.Photo(Day.AddMinutes(2));
            builder.Message(Day.AddMinutes(3));

            if (photos)
            {
                builder.Memory(20, "Beach week", new DateTime(2025, 3, 10), image: true, photoCaptions: ["By the sea", null, "Sunset"]);
                builder.Memory(21, "Plain day", new DateTime(2025, 3, 10), image: true);
                builder.Memory(22, "No cover", new DateTime(2025, 3, 10));
            }
            else
            {
                builder.Memory(20, "Beach week", new DateTime(2025, 3, 10), image: true);
                builder.Memory(21, "Plain day", new DateTime(2025, 3, 10), image: true);
                builder.Memory(22, "No cover", new DateTime(2025, 3, 10));
            }

            return builder;
        }

        private ArchiveSession Open(MemoryArchiveBuilder builder)
        {
            var library = new ArchiveLibrary(Path.Combine(_root.Path, "library-" + Guid.NewGuid().ToString("N")));
            var settings = new ArchiveSettings(_store);

            var staging = library.CreateStagingFolder();
            builder.Write(staging);

            var outcome = library.Install(staging);
            Assert.True(outcome.Installed, string.Join(", ", outcome.Validation.FirstProblems));

            return ArchiveSession.Open(library.ListArchives(settings).Single(), settings);
        }

        private static List<EntryCardRow> Cards(ArchiveSession session)
        {
            var timeline = session.CreateTimeline();
            timeline.OpenAtLatest();

            return timeline.Rows.OfType<EntryCardRow>().OrderBy(r => r.Key.EntryID).ToList();
        }

        [Fact]
        public void TheCard_ShowsTheCover_WithAPlusNBadge_OnlyWhenThereAreExtraPhotos()
        {
            using var session = Open(Sample(photos: true));

            var cards = Cards(session);

            Assert.True(cards[0].HasImage);
            Assert.Equal(3, cards[0].ExtraPhotoCount);
            Assert.True(cards[0].HasExtraPhotos);
            Assert.Equal("+3", cards[0].ExtraPhotosBadge);

            Assert.True(cards[1].HasImage);
            Assert.False(cards[1].HasExtraPhotos);
            Assert.Equal(string.Empty, cards[1].ExtraPhotosBadge);

            Assert.False(cards[2].HasImage);
            Assert.False(cards[2].HasExtraPhotos);
        }

        [Fact]
        public void TheDetails_ShowEveryPhotoInAStrip_CoverFirst_WithCaptions()
        {
            using var session = Open(Sample(photos: true));

            var details = session.MemoryDetails(new MemoryKey(20, null))!;

            Assert.True(details.HasPhotoStrip);
            Assert.Equal(4, details.Photos.Count);
            Assert.Equal(details.ImagePath, details.Photos[0].Path);
            Assert.Equal([null, "By the sea", null, "Sunset"], details.Photos.Select(p => p.Caption));
            Assert.All(details.Photos, p => Assert.True(File.Exists(p.Path)));
            Assert.Equal(string.Empty, details.Photos[0].CaptionText);

            // One photo: the single image as before, no strip.
            var plain = session.MemoryDetails(new MemoryKey(21, null))!;
            Assert.False(plain.HasPhotoStrip);
            Assert.Single(plain.Photos);
            Assert.True(plain.HasImage);
        }

        [Fact]
        public void TheViewer_MovesThroughThisMemorysPhotosOnly_AndStopsAtEitherEnd()
        {
            using var session = Open(Sample(photos: true));
            var details = session.MemoryDetails(new MemoryKey(20, null))!;

            var viewer = details.BrowsePhotos(1);

            Assert.Equal("Photo 2 of 4", viewer.Title);
            Assert.Equal("By the sea", viewer.Current.CaptionText);
            Assert.True(viewer.Previous());
            Assert.False(viewer.Previous());
            Assert.Equal(0, viewer.Index);

            var seen = new List<string?> { viewer.Current.Path };

            while (viewer.Next())
            {
                seen.Add(viewer.Current.Path);
            }

            Assert.Equal(details.Photos.Select(p => p.Path), seen);
            Assert.Equal("Photo 4 of 4", viewer.Title);
            Assert.False(viewer.HasNext);

            // No path of another memory or of the chat's photos.
            var other = session.MemoryDetails(new MemoryKey(21, null))!.ImagePath;
            Assert.DoesNotContain(other, seen);

            Assert.Equal("Photo", session.MemoryDetails(new MemoryKey(21, null))!.BrowsePhotos(0).Title);
            Assert.Throws<ArgumentOutOfRangeException>(() => details.BrowsePhotos(4));
        }

        [Fact]
        public void AnExtraPhotoWhoseFileIsGone_IsNeitherCountedNorShown()
        {
            var builder = Sample(photos: true);
            var library = new ArchiveLibrary(Path.Combine(_root.Path, "library"));
            var settings = new ArchiveSettings(_store);
            var staging = library.CreateStagingFolder();
            builder.Write(staging);
            Assert.True(library.Install(staging).Installed);
            var archive = library.ListArchives(settings).Single();

            // A file removed after install (the session does not re-check the contents list).
            using (var reader = ArchiveReader.Open(archive.Folder))
            {
                File.Delete(reader.EntryImageFullPath(reader.ReadEntryImages(20)[1]));
            }

            using var session = ArchiveSession.Open(archive, settings);

            Assert.Equal(2, Cards(session)[0].ExtraPhotoCount);
            Assert.Equal([null, "By the sea", "Sunset"], session.MemoryDetails(new MemoryKey(20, null))!.Photos.Select(p => p.Caption));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void WhenTheCoversFileIsGone_TheFirstPresentPhotoIsShown_AndTheBadgeCountsTheRest(int extras)
        {
            var builder = new MemoryArchiveBuilder();
            builder.Message(Day);
            builder.Memory(20, "Beach week", new DateTime(2025, 3, 10), image: true, photoCaptions: [.. Enumerable.Repeat<string?>("Invented", extras)]);

            var library = new ArchiveLibrary(Path.Combine(_root.Path, "library"));
            var settings = new ArchiveSettings(_store);
            var staging = library.CreateStagingFolder();
            builder.Write(staging);
            Assert.True(library.Install(staging).Installed);
            var archive = library.ListArchives(settings).Single();

            string firstExtra;

            using (var reader = ArchiveReader.Open(archive.Folder))
            {
                File.Delete(reader.ImageFullPath(reader.ReadEntriesOf(MemoryArchiveBuilder.Personal).Single())!);
                firstExtra = reader.EntryImageFullPath(reader.ReadEntryImages(20)[0]);
            }

            using var session = ArchiveSession.Open(archive, settings);

            var card = Cards(session).Single();
            Assert.Equal(firstExtra, card.ImagePath);
            Assert.Equal(extras - 1, card.ExtraPhotoCount);
            Assert.Equal(extras > 1, card.HasExtraPhotos);

            var details = session.MemoryDetails(new MemoryKey(20, null))!;
            Assert.Equal(firstExtra, details.ImagePath);
            Assert.Equal(firstExtra, details.Photos[0].Path);
            Assert.Equal(extras > 1, details.HasPhotoStrip);
            Assert.Equal(firstExtra, details.BrowsePhotos(0).Current.Path);
        }

        [Fact]
        public async Task ThePhotosGallery_IsUnchanged_ByMemoryPhotos()
        {
            using var with = Open(Sample(photos: true));
            using var without = Open(Sample(photos: false));

            var withPhotos = await with.GetGalleryAsync();
            var withoutPhotos = await without.GetGalleryAsync();

            Assert.Equal(2, withPhotos.Photos.Count);
            Assert.Equal(withoutPhotos.Photos.Select(p => p.AttachmentID), withPhotos.Photos.Select(p => p.AttachmentID));

            var memoryPaths = with.MemoryDetails(new MemoryKey(20, null))!.Photos.Select(p => p.Path).ToHashSet();
            Assert.DoesNotContain(withPhotos.Photos, p => memoryPaths.Contains(p.FullPath));
        }

        [Fact]
        public void TheBadgeWording_IsPlusN()
        {
            Assert.Equal("+4", MemoryWording.ExtraPhotosBadge(4));
            Assert.Equal("Photo 1 of 2", MemoryWording.PhotoTitle(0, 2));
        }
    }
}
