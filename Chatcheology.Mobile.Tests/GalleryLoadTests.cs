using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Gallery;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Synthetic;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// One synthetic archive of several pages and many months (invented text and images only), written
    /// once for the gallery load tests.
    /// </summary>
    public sealed class GalleryLoadArchive : IDisposable
    {
        private readonly TemporaryFolder _folder = new();

        public GalleryLoadArchive()
        {
            SyntheticArchive.Write(_folder.Path, new SyntheticArchiveOptions(
                Messages: 3000, Days: 500, Images: 240, Missing: 6, Unmatched: 6, BrokenImages: 4, LowQualityCopies: 5));
        }

        internal string Path => _folder.Path;

        public void Dispose() => _folder.Dispose();
    }

    /// <summary>
    /// The gallery loaded newest months first (CH-66): batches end only where a month heading ends, put
    /// together they are exactly the gallery built in one go, and the shared load survives a screen closed
    /// and opened again, a closed session and a failed read.
    /// </summary>
    public sealed class GalleryLoadTests(GalleryLoadArchive archive) : IClassFixture<GalleryLoadArchive>
    {
        private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

        /// <summary>The same photos, cards, order, month headings and rows.</summary>
        internal static void AssertSameGallery(PhotoGallery expected, PhotoGallery actual)
        {
            Assert.Equal(expected.Photos.Count, actual.Photos.Count);

            for (var i = 0; i < expected.Photos.Count; i++)
            {
                Assert.Equal(Describe(expected.Photos[i]), Describe(actual.Photos[i]));
            }

            Assert.Equal(
                expected.Months.Select(m => (m.Year, m.Month, m.Title, Ids(m.Photos))).ToList(),
                actual.Months.Select(m => (m.Year, m.Month, m.Title, Ids(m.Photos))).ToList());

            Assert.Equal(expected.Rows(3).Select(Describe).ToList(), actual.Rows(3).Select(Describe).ToList());
        }

        private static string Describe(AttachmentCard card) => string.Join(
            "|",
            card.AttachmentID,
            card.SequenceNumber,
            card.Kind,
            card.FullPath,
            card.Heading,
            card.LowQualityLabel,
            card.HiddenCopies,
            card.IsStarred,
            card.MessageTimeLocal.ToString("O", CultureInfo.InvariantCulture));

        private static string Describe(GalleryRow row) => row switch
        {
            GalleryHeaderRow header => "H " + header.Title,
            GalleryPhotoRow photos => $"P {photos.FirstIndex}: " + Ids(photos.Photos),
            _ => "?",
        };

        private static string Ids(IEnumerable<AttachmentCard> photos) => string.Join(",", photos.Select(p => p.AttachmentID));

        private PhotoGallery FullBuild(ArchiveReader reader, ConversationTimeline timeline) =>
            PhotoGallery.Build(reader.ReadAllPages(), timeline, English);

        private List<GalleryBatch> Batches(ArchiveReader reader, ConversationTimeline timeline, int first, int later) =>
            PhotoGalleryLoad.Batches(PhotoGalleryLoad.ReadPagesNewestFirst(reader), timeline, English, first, later).ToList();

        // ---------------------------------------------------------------------------------------
        // Batches.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData(1, 1)]
        [InlineData(10, 25)]
        [InlineData(PhotoGalleryLoad.FirstBatchPhotos, PhotoGalleryLoad.LaterBatchPhotos)]
        [InlineData(100000, 100000)]
        public void The_batches_put_together_are_the_gallery_built_in_one_go(int first, int later)
        {
            using var reader = ArchiveReader.Open(archive.Path);
            var timeline = new ConversationTimeline(reader, SyntheticArchive.Local);

            var expected = FullBuild(reader, timeline);
            var batches = Batches(reader, timeline, first, later);

            Assert.True(expected.Photos.Count > 200, "the fixture should have many photos");
            Assert.True(expected.Months.Count > 10, "the fixture should have many months");
            AssertSameGallery(expected, PhotoGallery.FromBatches(batches));

            // The batches' rows, one after another, are the gallery's rows.
            Assert.Equal(
                expected.Rows(3).Select(Describe).ToList(),
                batches.SelectMany(batch => batch.Rows(3)).Select(Describe).ToList());
        }

        [Fact]
        public void Each_batch_ends_where_a_month_heading_ends_and_holds_at_least_its_share()
        {
            using var reader = ArchiveReader.Open(archive.Path);
            var timeline = new ConversationTimeline(reader, SyntheticArchive.Local);

            var batches = Batches(reader, timeline, 10, 25);

            Assert.True(batches.Count > 3);

            var next = 0;

            for (var i = 0; i < batches.Count; i++)
            {
                var batch = batches[i];

                Assert.Equal(next, batch.FirstIndex);
                Assert.Equal(batch.Photos.Count, batch.Months.Sum(month => month.Photos.Count));
                next += batch.Photos.Count;

                if (i < batches.Count - 1)
                {
                    Assert.True(batch.Photos.Count >= (i == 0 ? 10 : 25));

                    // The next batch starts a new heading: its first photo is of another month.
                    var last = batch.Photos[^1].MessageTimeLocal;
                    var following = batches[i + 1].Photos[0].MessageTimeLocal;
                    Assert.NotEqual((last.Year, last.Month), (following.Year, following.Month));
                }
            }
        }

        [Fact]
        public void The_first_batch_is_the_newest_photos_and_needs_only_the_newest_pages()
        {
            using var reader = ArchiveReader.Open(archive.Path);
            var timeline = new ConversationTimeline(reader, SyntheticArchive.Local);
            var expected = FullBuild(reader, timeline);

            var pagesRead = 0;
            var pages = PhotoGalleryLoad.ReadPagesNewestFirst(reader).Select(page =>
            {
                pagesRead++;
                return page;
            });

            var first = PhotoGalleryLoad.Batches(pages, timeline, English, 10, 25).First();

            Assert.Equal(expected.Photos.Take(first.Photos.Count).Select(p => p.AttachmentID), first.Photos.Select(p => p.AttachmentID));
            Assert.True(pagesRead < 3, $"read {pagesRead} pages for the first batch");
        }

        [Fact]
        public void A_month_with_more_photos_than_a_batch_stays_whole()
        {
            using var folder = new TemporaryFolder();
            var builder = new ArchiveBuilder();
            var id = 1;

            builder.Add("Jan", ArchiveBuilder.Other, new DateTime(2023, 1, 5, 9, 0, 0), null, null, new AttachmentSpec(id++));

            for (var day = 1; day <= 7; day++)
            {
                builder.Add("Feb", ArchiveBuilder.Other, new DateTime(2023, 2, day, 9, 0, 0), null, null, new AttachmentSpec(id++));
            }

            builder.Add("Mar", ArchiveBuilder.Other, new DateTime(2023, 3, 5, 9, 0, 0), null, null, new AttachmentSpec(id++));
            builder.Write(folder.Path);

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, ArchiveBuilder.Local);

            var batches = Batches(reader, timeline, 1, 2);

            Assert.Equal(["March 2023", "February 2023", "January 2023"], batches.Select(b => string.Join("+", b.Months.Select(m => m.Title))).ToList());
            Assert.Equal([1, 7, 1], batches.Select(b => b.Photos.Count).ToList());
        }

        [Fact]
        public void A_month_interrupted_in_chat_order_keeps_its_second_heading_across_batches()
        {
            using var folder = new TemporaryFolder();
            var builder = new ArchiveBuilder();
            builder.Add("a", ArchiveBuilder.Other, new DateTime(2023, 3, 5, 9, 0, 0), null, null, new AttachmentSpec(1));
            builder.Add("b", ArchiveBuilder.Other, new DateTime(2023, 2, 5, 9, 0, 0), null, null, new AttachmentSpec(2));
            builder.Add("c", ArchiveBuilder.Other, new DateTime(2023, 3, 6, 9, 0, 0), null, null, new AttachmentSpec(3));
            builder.Write(folder.Path);

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, ArchiveBuilder.Local);

            var batches = Batches(reader, timeline, 1, 1);

            Assert.Equal(["March 2023", "February 2023", "March 2023"], batches.SelectMany(b => b.Months).Select(m => m.Title).ToList());
            AssertSameGallery(FullBuild(reader, timeline), PhotoGallery.FromBatches(batches));
        }

        [Fact]
        public void Pages_newest_first_are_every_page_in_reverse()
        {
            using var reader = ArchiveReader.Open(archive.Path);

            var forward = reader.ReadAllPages().SelectMany(page => page.Messages).Select(m => m.SequenceNumber).ToList();
            var backward = PhotoGalleryLoad.ReadPagesNewestFirst(reader)
                .SelectMany(page => page.Messages.Reverse())
                .Select(m => m.SequenceNumber)
                .ToList();

            backward.Reverse();
            Assert.Equal(forward, backward);
        }

        [Fact]
        public void An_archive_without_messages_has_no_pages_and_no_batches()
        {
            using var folder = new TemporaryFolder();
            new ArchiveBuilder().Write(folder.Path);

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, ArchiveBuilder.Local);

            Assert.Empty(PhotoGalleryLoad.ReadPagesNewestFirst(reader));
            Assert.Empty(Batches(reader, timeline, 1, 1));
            Assert.True(PhotoGallery.FromBatches([]).IsEmpty);
        }

        // ---------------------------------------------------------------------------------------
        // The shared load.
        // ---------------------------------------------------------------------------------------

        /// <summary>Real batches, handed out one at a time when the test says so.</summary>
        private sealed class GatedBatches(IReadOnlyList<GalleryBatch> batches, int failAfter = -1)
        {
            private readonly SemaphoreSlim _gate = new(0);

            internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            internal int Enumerations { get; private set; }

            internal void Release(int count) => _gate.Release(count);

            internal IEnumerable<GalleryBatch> Produce(CancellationToken token)
            {
                Enumerations++;

                try
                {
                    for (var i = 0; i < batches.Count; i++)
                    {
                        if (i == failAfter)
                        {
                            throw new ArchiveFormatException("A synthetic damaged page.");
                        }

                        _gate.Wait(token);
                        yield return batches[i];
                    }
                }
                finally
                {
                    // Stands for the load's own reader being closed.
                    Closed.TrySetResult();
                }
            }
        }

        private List<GalleryBatch> SomeBatches()
        {
            using var reader = ArchiveReader.Open(archive.Path);
            var timeline = new ConversationTimeline(reader, SyntheticArchive.Local);

            var batches = Batches(reader, timeline, 10, 25);
            Assert.True(batches.Count >= 4);

            return batches;
        }

        private static async Task<List<GalleryBatch>> ReadAllAsync(PhotoGalleryLoad load, CancellationToken token = default)
        {
            var read = new List<GalleryBatch>();

            while (await load.NextAsync(read.Count, token).WaitAsync(Patience) is { } batch)
            {
                read.Add(batch);
            }

            return read;
        }

        [Fact]
        public async Task A_gallery_closed_and_opened_again_mid_load_gets_every_batch_once()
        {
            var batches = SomeBatches();
            var gated = new GatedBatches(batches);
            using var closing = new CancellationTokenSource();
            var load = PhotoGalleryLoad.Start(gated.Produce, closing.Token);

            // The first screen sees two batches, then closes while waiting for the third.
            gated.Release(2);
            using var firstScreen = new CancellationTokenSource();
            Assert.Same(batches[0], await load.NextAsync(0, firstScreen.Token).WaitAsync(Patience));
            Assert.Same(batches[1], await load.NextAsync(1, firstScreen.Token).WaitAsync(Patience));

            var waiting = load.NextAsync(2, firstScreen.Token);
            firstScreen.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Patience));

            // The load went on; the second screen reads from the first batch.
            Assert.False(load.Completion.IsCompleted);
            var secondScreen = ReadAllAsync(load);
            gated.Release(batches.Count);

            var read = await secondScreen;

            Assert.Equal(batches.Count, read.Count);
            Assert.All(batches.Zip(read), pair => Assert.Same(pair.First, pair.Second));
            Assert.Equal(1, gated.Enumerations);

            var gallery = await load.Completion.WaitAsync(Patience);
            Assert.Same(gallery, load.Gallery);
            Assert.Equal(batches.SelectMany(b => b.Photos).Select(p => p.AttachmentID), gallery.Photos.Select(p => p.AttachmentID));
            await gated.Closed.Task.WaitAsync(Patience);

            // A screen opened after the load is complete gets the same batches at once.
            Assert.Equal(batches.Count, (await ReadAllAsync(load)).Count);
        }

        [Fact]
        public async Task Closing_the_session_mid_load_stops_the_load_closes_its_reader_and_tells_every_screen()
        {
            var batches = SomeBatches();
            var gated = new GatedBatches(batches);
            using var closing = new CancellationTokenSource();
            var load = PhotoGalleryLoad.Start(gated.Produce, closing.Token);

            gated.Release(1);
            Assert.Same(batches[0], await load.NextAsync(0).WaitAsync(Patience));

            var waiting = load.NextAsync(1);
            closing.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Patience));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.Completion.WaitAsync(Patience));
            await gated.Closed.Task.WaitAsync(Patience);

            Assert.True(load.Completion.IsCanceled);
            Assert.Null(load.Gallery);
            Assert.Equal(1, load.BatchCount);

            // A screen asking afterwards is told the same, never left waiting.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.NextAsync(1).WaitAsync(Patience));
        }

        [Fact]
        public async Task A_load_cancelled_before_it_starts_says_so_and_reads_nothing()
        {
            var gated = new GatedBatches(SomeBatches());
            using var closing = new CancellationTokenSource();
            closing.Cancel();

            var load = PhotoGalleryLoad.Start(gated.Produce, closing.Token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.NextAsync(0).WaitAsync(Patience));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.Completion.WaitAsync(Patience));
            Assert.Equal(0, gated.Enumerations);
        }

        [Fact]
        public async Task A_read_that_fails_mid_load_fails_every_screen_with_the_readers_exception()
        {
            var batches = SomeBatches();
            var gated = new GatedBatches(batches, failAfter: 2);
            using var closing = new CancellationTokenSource();
            var load = PhotoGalleryLoad.Start(gated.Produce, closing.Token);

            gated.Release(batches.Count);

            await Assert.ThrowsAsync<ArchiveFormatException>(() => ReadAllAsync(load));
            await Assert.ThrowsAsync<ArchiveFormatException>(() => ReadAllAsync(load));
            await Assert.ThrowsAsync<ArchiveFormatException>(() => load.Completion.WaitAsync(Patience));
            await gated.Closed.Task.WaitAsync(Patience);

            Assert.Null(load.Gallery);
        }

        [Fact]
        public async Task RefreshStars_sets_the_flags_of_the_photos_loaded_so_far()
        {
            var batches = SomeBatches();
            var gated = new GatedBatches(batches);
            using var closing = new CancellationTokenSource();
            var load = PhotoGalleryLoad.Start(gated.Produce, closing.Token);

            gated.Release(1);
            var first = await load.NextAsync(0).WaitAsync(Patience);
            Assert.NotNull(first);

            var settings = new ArchiveSettings(new MemorySettings());
            var stars = new Stars(settings, "hash");
            var photo = first.Photos[0];
            stars.SetAttachment(photo.SequenceNumber, photo.AttachmentID, true);

            load.RefreshStars(stars);

            Assert.True(photo.IsStarred);
            Assert.All(first.Photos.Skip(1), p => Assert.False(p.IsStarred));

            closing.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.Completion.WaitAsync(Patience));
        }

        // ---------------------------------------------------------------------------------------
        // The session.
        // ---------------------------------------------------------------------------------------

        private static InstalledArchive Install(TemporaryFolder root, ArchiveSettings settings, ArchiveBuilder builder)
        {
            var library = new ArchiveLibrary(root.Path);
            var staging = library.CreateStagingFolder();
            builder.Write(staging);

            var outcome = library.Install(staging);
            Assert.True(outcome.Installed);

            return library.ListArchives(settings).Single(a => a.Folder == outcome.InstalledFolder);
        }

        /// <summary>Photos over many months, more than one page of messages.</summary>
        private static ArchiveBuilder ManyMonths()
        {
            var builder = new ArchiveBuilder();
            var start = new DateTime(2021, 1, 3, 9, 0, 0);

            for (var i = 0; i < 1400; i++)
            {
                var at = start.AddDays(i * 0.75);

                if (i % 3 == 0)
                {
                    builder.Add("photo", ArchiveBuilder.Other, at, null, null, new AttachmentSpec(i + 1));
                }
                else
                {
                    builder.Add("text", ArchiveBuilder.Other, at);
                }
            }

            return builder;
        }

        [Fact]
        public async Task The_session_keeps_one_load_whose_gallery_is_the_one_built_in_one_go()
        {
            using var root = new TemporaryFolder();
            var settings = new ArchiveSettings(new MemorySettings());
            var installed = Install(root, settings, ManyMonths());

            using var session = ArchiveSession.Open(installed, settings);

            var load = session.GetGalleryLoad();
            Assert.Same(load, session.GetGalleryLoad());
            Assert.Same(load, session.GalleryLoadIfStarted);

            var read = await ReadAllAsync(load);
            var gallery = await session.GetGalleryAsync().WaitAsync(Patience);

            Assert.Same(gallery, await session.GetGalleryAsync());
            Assert.Same(gallery, session.GalleryIfBuilt);
            Assert.True(read.Count >= 2, "the photos should need more than one batch");
            Assert.True(read[0].Photos.Count >= PhotoGalleryLoad.FirstBatchPhotos);

            using var reader = ArchiveReader.Open(installed.Folder);
            var timeline = new ConversationTimeline(reader, session.Me, session.LowQuality, session.Stars);
            var expected = PhotoGallery.Build(reader.ReadAllPages(), timeline, CultureInfo.CurrentCulture);

            AssertSameGallery(expected, gallery);
            AssertSameGallery(expected, PhotoGallery.FromBatches(read));
        }

        [Fact]
        public async Task Closing_the_session_mid_load_ends_the_load_quietly_and_no_new_load_starts()
        {
            using var root = new TemporaryFolder();
            var settings = new ArchiveSettings(new MemorySettings());
            var installed = Install(root, settings, ManyMonths());

            var session = ArchiveSession.Open(installed, settings);
            var load = session.GetGalleryLoad();
            session.Dispose();

            // Either it finished first or it was stopped: never any other failure.
            try
            {
                await ReadAllAsync(load);
                await load.Completion.WaitAsync(Patience);
            }
            catch (OperationCanceledException)
            {
                Assert.True(load.Completion.IsCanceled);
            }

            Assert.Throws<ObjectDisposedException>(() => session.GetGalleryLoad());
        }
    }
}
