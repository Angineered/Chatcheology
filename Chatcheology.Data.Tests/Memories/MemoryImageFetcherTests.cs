using System.Security.Cryptography;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Psn;
using Chatcheology.Tests.Memories;
using Microsoft.Data.Sqlite;
using static Chatcheology.Tests.Memories.SyntheticPsnFile;

namespace Chatcheology.Data.Tests.Memories
{
    public class MemoryImageFetcherTests
    {
        internal static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

        internal static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 9, 9, 9];

        /// <summary>Serves bytes per URL from memory; records what it was asked for.</summary>
        internal sealed class FakeSource(Dictionary<string, byte[]> bodies) : IMemoryImageSource
        {
            internal List<string> Requested { get; } = [];

            public byte[]? Fetch(Uri uri, long maxBytes)
            {
                Requested.Add(uri.AbsoluteUri);
                return bodies.TryGetValue(uri.AbsoluteUri, out var body) ? body : null;
            }
        }

        private static string ImportWithIcons(MemoriesTestFolder folder, string? questIcon, string? racerIcon)
        {
            var path = folder.CreateMemories();
            var file = new SyntheticPsnFile();
            file.Games.Add(Quest with { Icon = questIcon });
            file.Games.Add(Racer with { Icon = racerIcon });
            var standard = Standard();
            file.Events.Add(new SyntheticEvent { Game = file.Games[0], Date = "2015-03-01", Local = standard.Events[0].Local, Other = standard.Events[0].Other });
            file.Events.Add(new SyntheticEvent { Game = file.Games[1], Date = "2016-07-04", Local = standard.Events[1].Local, Other = standard.Events[1].Other });
            new PsnImporter().Import(path, PsnSharedActivityReader.Parse(file.ToBytes(), LocalPrefix, OtherPrefix, file.PairAPrefix));
            SqliteConnection.ClearAllPools();
            return path;
        }

        [Theory]
        [InlineData("https://image.api.playstation.com/a.png", true)]
        [InlineData("https://IMAGE.API.PLAYSTATION.COM/a.png", true)]
        [InlineData("https://psnobj.prod.dl.playstation.net/a.png", true)]
        [InlineData("http://image.api.playstation.com/a.png", false)]
        [InlineData("https://image.api.playstation.com:8443/a.png", false)]
        [InlineData("https://user@image.api.playstation.com/a.png", false)]
        [InlineData("https://image.api.playstation.com.example.invalid/a.png", false)]
        [InlineData("https://example.invalid/a.png", false)]
        [InlineData("ftp://image.api.playstation.com/a.png", false)]
        [InlineData("not a url", false)]
        public void Allowlist_HttpsAndListedHostsOnly(string url, bool allowed) =>
            Assert.Equal(allowed, MemoryImageFetcher.IsAllowed(url));

        [Fact]
        public void Fetch_StoresByHash_AndSetsTheEntryImage()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportWithIcons(folder, Quest.Icon, Racer.Icon);
            var source = new FakeSource(new() { [Quest.Icon!] = Png, [Racer.Icon!] = Jpeg });

            var result = MemoryImageFetcher.FetchMissing(path, source);

            Assert.Equal(new MemoryImageFetchResult(2, 0, 0), result);
            var pngSha = Convert.ToHexStringLower(SHA256.HashData(Png));
            Assert.True(File.Exists(Path.Combine(folder.MediaFolder, pngSha + ".png")));
            Assert.Equal(Png, File.ReadAllBytes(Path.Combine(folder.MediaFolder, pngSha + ".png")));
            Assert.Equal([pngSha], folder.Strings("SELECT ImageSha256 FROM Entry WHERE Title = 'Example Quest';"));
            Assert.Equal(2, folder.Scalar("SELECT COUNT(*) FROM MediaFile;"));
        }

        [Fact]
        public void Fetch_SecondRun_FindsEverythingPresent_AndFetchesNothing()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportWithIcons(folder, Quest.Icon, Racer.Icon);
            MemoryImageFetcher.FetchMissing(path, new FakeSource(new() { [Quest.Icon!] = Png, [Racer.Icon!] = Jpeg }));
            var again = new FakeSource([]);

            var result = MemoryImageFetcher.FetchMissing(path, again);

            Assert.Equal(new MemoryImageFetchResult(0, 2, 0), result);
            Assert.Empty(again.Requested);
        }

        [Fact]
        public void Fetch_MediaFileDeletedFromDisk_IsFetchedAgain()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportWithIcons(folder, Quest.Icon, null);
            MemoryImageFetcher.FetchMissing(path, new FakeSource(new() { [Quest.Icon!] = Png }));
            var file = Path.Combine(folder.MediaFolder, Convert.ToHexStringLower(SHA256.HashData(Png)) + ".png");
            File.Delete(file);

            var result = MemoryImageFetcher.FetchMissing(path, new FakeSource(new() { [Quest.Icon!] = Png }));

            Assert.Equal(new MemoryImageFetchResult(1, 0, 0), result);
            Assert.Equal(Png, File.ReadAllBytes(file));
        }

        [Fact]
        public void Fetch_ExistingFileWithWrongContent_IsReplaced()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportWithIcons(folder, Quest.Icon, null);
            var file = Path.Combine(folder.MediaFolder, Convert.ToHexStringLower(SHA256.HashData(Png)) + ".png");
            Directory.CreateDirectory(folder.MediaFolder);
            File.WriteAllBytes(file, [1, 2, 3]);

            var result = MemoryImageFetcher.FetchMissing(path, new FakeSource(new() { [Quest.Icon!] = Png }));

            Assert.Equal(1, result.Fetched);
            Assert.Equal(Png, File.ReadAllBytes(file));
        }

        [Fact]
        public void Fetch_IdenticalBytesFromTwoUrls_AreStoredOnce()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportWithIcons(folder, Quest.Icon, Racer.Icon);

            var result = MemoryImageFetcher.FetchMissing(path, new FakeSource(new() { [Quest.Icon!] = Png, [Racer.Icon!] = Png }));

            Assert.Equal(2, result.Fetched);
            Assert.Equal(1, folder.Scalar("SELECT COUNT(*) FROM MediaFile;"));
            Assert.Single(Directory.GetFiles(folder.MediaFolder));
            Assert.Equal(1, folder.Scalar("SELECT COUNT(DISTINCT ImageSha256) FROM Entry;"));
        }

        [Fact]
        public void Fetch_NonHttpsOrUnlistedHost_IsRefusedWithoutAsking()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportWithIcons(folder, "http://image.api.playstation.com/zz/a.png", "https://example.invalid/zz/b.png");
            var source = new FakeSource([]);

            var result = MemoryImageFetcher.FetchMissing(path, source);

            Assert.Equal(new MemoryImageFetchResult(0, 0, 2), result);
            Assert.Empty(source.Requested);
            Assert.False(Directory.Exists(folder.MediaFolder));
        }

        [Fact]
        public void Fetch_OverTheSizeCap_Fails()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportWithIcons(folder, Quest.Icon, null);

            var result = MemoryImageFetcher.FetchMissing(path, new FakeSource(new() { [Quest.Icon!] = Png }), maxBytes: Png.Length - 1);

            Assert.Equal(new MemoryImageFetchResult(0, 0, 1), result);
            Assert.Equal(0, folder.Scalar("SELECT COUNT(*) FROM MediaFile;"));
        }

        [Fact]
        public void Fetch_AtTheSizeCap_Succeeds()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportWithIcons(folder, Quest.Icon, null);

            var result = MemoryImageFetcher.FetchMissing(path, new FakeSource(new() { [Quest.Icon!] = Png }), maxBytes: Png.Length);

            Assert.Equal(1, result.Fetched);
        }

        [Fact]
        public void Fetch_NotAnImage_OrNoBody_OrThrowing_Fails()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportWithIcons(folder, Quest.Icon, Racer.Icon);

            var result = MemoryImageFetcher.FetchMissing(path, new FakeSource(new() { [Quest.Icon!] = "<html>"u8.ToArray() }));

            Assert.Equal(new MemoryImageFetchResult(0, 0, 2), result);
            Assert.Equal(0, folder.Scalar("SELECT COUNT(*) FROM MediaFile;"));
        }

        [Fact]
        public void Fetch_SourceThatThrows_CountsAsFailed()
        {
            using var folder = new MemoriesTestFolder();
            var path = ImportWithIcons(folder, Quest.Icon, null);

            var result = MemoryImageFetcher.FetchMissing(path, new ThrowingSource());

            Assert.Equal(new MemoryImageFetchResult(0, 0, 1), result);
        }

        [Fact]
        public void Https_Source_RefusesUnlistedHostsItself()
        {
            using var source = new HttpsMemoryImageSource();

            Assert.Null(source.Fetch(new Uri("http://image.api.playstation.com/a.png"), 10));
            Assert.Null(source.Fetch(new Uri("https://example.invalid/a.png"), 10));
        }

        private sealed class ThrowingSource : IMemoryImageSource
        {
            public byte[]? Fetch(Uri uri, long maxBytes) => throw new HttpRequestException("synthetic");
        }
    }
}
