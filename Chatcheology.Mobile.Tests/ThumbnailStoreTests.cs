using Chatcheology.Mobile.Import;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>CH-66 saved thumbnails: keying, failures fall back (nothing saved, nothing left), cleanup.</summary>
    public sealed class ThumbnailStoreTests : IDisposable
    {
        private static readonly string Archive = new('a', 64);
        private static readonly string OtherArchive = new('b', 64);
        private static readonly string Photo = new('1', 64);
        private static readonly string OtherPhoto = new('2', 64);

        private readonly TemporaryFolder _root = new();

        public void Dispose() => _root.Dispose();

        private ThumbnailStore Store(long? freeBytes = null) =>
            new(Path.Combine(_root.Path, "files"), freeBytes is { } bytes ? new FixedSpace(bytes) : null);

        private static ThumbnailKey Key(string archive = "", long id = 7, string photo = "", int width = 300, int height = 300) =>
            new(archive.Length == 0 ? Archive : archive, id, photo.Length == 0 ? Photo : photo, width, height);

        private static bool Jpeg(Stream stream)
        {
            stream.Write([0xFF, 0xD8, 0xFF, 0xD9]);
            return true;
        }

        private string[] FilesLeft(ThumbnailStore store) =>
            Directory.Exists(store.Root) ? Directory.GetFiles(store.Root, "*", SearchOption.AllDirectories) : [];

        [Fact]
        public void A_saved_thumbnail_is_found_by_archive_attachment_media_and_size_only()
        {
            var store = Store();

            Assert.Null(store.Find(Key()));
            Assert.True(store.Save(Key(), Jpeg));

            var path = store.Find(Key());
            Assert.NotNull(path);
            Assert.Equal([0xFF, 0xD8, 0xFF, 0xD9], File.ReadAllBytes(path));
            Assert.Equal(Path.Combine(store.Root, Archive, "7-" + Photo + "-300x300.jpg"), path);

            Assert.Null(store.Find(Key(archive: OtherArchive)));
            Assert.Null(store.Find(Key(id: 8)));
            Assert.Null(store.Find(Key(photo: OtherPhoto)));
            Assert.Null(store.Find(Key(width: 200, height: 200)));
            Assert.Equal(Path.Combine(_root.Path, "files", ThumbnailStore.FolderName, ThumbnailStore.Version), store.Root);
            Assert.Equal(Path.Combine(_root.Path, "files", ThumbnailStore.FolderName), store.Folder);
        }

        [Theory]
        [InlineData("../escape", 7, "", 300)]
        [InlineData("..", 7, "", 300)]
        [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 7, "", 300)]
        [InlineData("", 0, "", 300)]
        [InlineData("", -3, "", 300)]
        [InlineData("", 7, "", 0)]
        [InlineData("", 7, "..", 300)]
        [InlineData("", 7, "../../escape", 300)]
        [InlineData("", 7, "1111111111111111111111111111111111111111111111111111111111111111/..", 300)]
        [InlineData("", 7, "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", 300)]
        [InlineData("", 7, "111111111111111111111111111111111111111111111111111111111111111", 300)]
        [InlineData("", 7, "11111111111111111111111111111111111111111111111111111111111111111", 300)]
        [InlineData("", 7, "not-a-hash", 300)]
        public void A_key_that_cannot_name_a_file_is_never_saved_or_found(string archive, long id, string photo, int size)
        {
            var store = Store();
            var key = photo.Length == 0
                ? Key(archive: archive, id: id, width: size, height: size)
                : Key(archive: archive, id: id, width: size, height: size) with { MediaSha256 = photo };

            Assert.False(key.IsValid);
            Assert.False(store.Save(key, Jpeg));
            Assert.Null(store.Find(key));
            store.Remove(key);
            Assert.Empty(FilesLeft(store));
            Assert.False(Directory.Exists(store.Folder));
        }

        [Fact]
        public void One_size_is_kept_per_attachment_so_a_new_cell_size_replaces_the_old_thumbnail()
        {
            var store = Store();
            Assert.True(store.Save(Key(id: 7, width: 300, height: 300), Jpeg));
            Assert.True(store.Save(Key(id: 70), Jpeg));
            Assert.True(store.Save(Key(id: 8), Jpeg));

            // Rotated or split screen: a new size for attachment 7 replaces its thumbnail.
            Assert.True(store.Save(Key(id: 7, width: 540, height: 540), Jpeg));

            Assert.Null(store.Find(Key(id: 7, width: 300, height: 300)));
            Assert.NotNull(store.Find(Key(id: 7, width: 540, height: 540)));
            Assert.NotNull(store.Find(Key(id: 70)));   // "70-" is not attachment 7
            Assert.NotNull(store.Find(Key(id: 8)));
            Assert.Equal(3, FilesLeft(store).Length);

            // A failed save at another size keeps the one there is.
            Assert.False(store.Save(Key(id: 7, width: 200, height: 200), _ => false));
            Assert.NotNull(store.Find(Key(id: 7, width: 540, height: 540)));
        }

        [Fact]
        public void Start_up_cleanup_removes_everything_not_under_the_current_version()
        {
            var store = Store();
            Assert.True(store.Save(Key(), Jpeg));

            var earlier = Path.Combine(store.Folder, "v0", Archive);
            Directory.CreateDirectory(earlier);
            File.WriteAllBytes(Path.Combine(earlier, "7-" + Photo + "-300x300.jpg"), [1]);
            Directory.CreateDirectory(Path.Combine(store.Folder, Archive));   // the unversioned layout
            File.WriteAllBytes(Path.Combine(store.Folder, "stray"), [1]);

            store.RemoveOrphans([Archive]);

            Assert.Equal([store.Root], Directory.GetFileSystemEntries(store.Folder));
            Assert.NotNull(store.Find(Key()));
        }

        [Fact]
        public void A_failed_or_empty_write_saves_nothing_and_leaves_nothing()
        {
            var store = Store();

            Assert.False(store.Save(Key(), _ => throw new IOException("synthetic: storage full")));
            Assert.False(store.Save(Key(), _ => false));
            Assert.False(store.Save(Key(), _ => true));   // claimed success, wrote no bytes

            Assert.False(store.Save(Key(), stream =>
            {
                stream.Write([1, 2, 3]);
                throw new InvalidOperationException("synthetic: encoder failed half-way");
            }));

            Assert.Null(store.Find(Key()));
            Assert.Empty(FilesLeft(store));
        }

        [Fact]
        public void Nothing_is_saved_when_free_space_is_low_and_a_saved_thumbnail_is_kept_until_replaced()
        {
            var low = Store(ThumbnailStore.MinimumFreeBytes - 1);
            Assert.False(low.Save(Key(), Jpeg));
            Assert.Empty(FilesLeft(low));

            var enough = Store(ThumbnailStore.MinimumFreeBytes);
            Assert.True(enough.Save(Key(), Jpeg));

            // A failed second write leaves the first in place.
            Assert.False(enough.Save(Key(), _ => throw new IOException("synthetic")));
            Assert.NotNull(enough.Find(Key()));
            Assert.Single(FilesLeft(enough));
        }

        [Fact]
        public void A_thumbnail_that_does_not_decode_can_be_removed_alone()
        {
            var store = Store();
            Assert.True(store.Save(Key(), Jpeg));
            Assert.True(store.Save(Key(id: 8), Jpeg));

            store.Remove(Key());

            Assert.Null(store.Find(Key()));
            Assert.NotNull(store.Find(Key(id: 8)));
        }

        [Fact]
        public void Removing_an_archive_deletes_its_folder_only()
        {
            var store = Store();
            Assert.True(store.Save(Key(), Jpeg));
            Assert.True(store.Save(Key(archive: OtherArchive), Jpeg));

            store.RemoveArchive(Archive);
            store.RemoveArchive(null);
            store.RemoveArchive("..");

            Assert.False(Directory.Exists(Path.Combine(store.Root, Archive)));
            Assert.NotNull(store.Find(Key(archive: OtherArchive)));
        }

        [Fact]
        public void Carry_moves_the_kept_attachments_to_the_new_archive_and_deletes_the_old_folder()
        {
            var store = Store();
            Assert.True(store.Save(Key(id: 7), Jpeg));
            Assert.True(store.Save(Key(id: 8, photo: OtherPhoto), Jpeg));
            Assert.True(store.Save(Key(id: 9), Jpeg));
            File.WriteAllBytes(Path.Combine(store.Root, Archive, "not-a-thumbnail.jpg"), [1]);

            store.Carry(Archive, OtherArchive, (id, sha) => (id, sha) == (7, Photo) || (id, sha) == (8, Photo));

            Assert.NotNull(store.Find(Key(archive: OtherArchive, id: 7)));
            Assert.Null(store.Find(Key(archive: OtherArchive, id: 8, photo: OtherPhoto)));   // same id, other media
            Assert.Null(store.Find(Key(archive: OtherArchive, id: 9)));
            Assert.False(Directory.Exists(Path.Combine(store.Root, Archive)));
            Assert.Single(FilesLeft(store));
        }

        [Fact]
        public void Orphans_and_unfinished_writes_are_removed_and_installed_archives_kept()
        {
            var store = Store();
            Assert.True(store.Save(Key(), Jpeg));
            Assert.True(store.Save(Key(archive: OtherArchive), Jpeg));
            File.WriteAllBytes(Path.Combine(store.Root, Archive, ".tmp-unfinished"), [1, 2]);
            File.WriteAllBytes(Path.Combine(store.Root, "stray"), [1]);

            store.RemoveOrphans([Archive, null]);

            Assert.NotNull(store.Find(Key()));
            Assert.False(Directory.Exists(Path.Combine(store.Root, OtherArchive)));
            Assert.Single(FilesLeft(store));

            // No folder yet: nothing to do, no throw.
            Store().RemoveOrphans([]);
            new ThumbnailStore(Path.Combine(_root.Path, "elsewhere")).RemoveOrphans([]);
        }

        [Fact]
        public void The_library_cleans_thumbnails_of_archives_no_longer_installed()
        {
            var library = new ArchiveLibrary(Path.Combine(_root.Path, "files"));
            Assert.True(library.Thumbnails.Save(Key(), Jpeg));

            library.CleanThumbnails();

            Assert.Null(library.Thumbnails.Find(Key()));
            Assert.Empty(FilesLeft(library.Thumbnails));
        }
    }
}
