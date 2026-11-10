using System.Collections.Specialized;
using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Gallery;
using Chatcheology.Mobile.Presentation;
using Chatcheology.Mobile.Synthetic;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>The CH-66 scroll follow-up: thumbnail crop, the decode queue and the gallery's range inserts.</summary>
    public sealed class GalleryScrollTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

        [Theory]
        [InlineData(330, 440, 330, 330, 0, 55, 330, 330, 1.0)]     // portrait at cover size: the middle square
        [InlineData(440, 330, 330, 330, 55, 0, 330, 330, 1.0)]     // landscape: the middle square
        [InlineData(330, 330, 330, 330, 0, 0, 330, 330, 1.0)]      // square: whole
        [InlineData(1300, 330, 330, 330, 485, 0, 330, 330, 1.0)]   // panorama: a square, not its whole width
        [InlineData(400, 400, 330, 330, 0, 0, 400, 400, 1.0)]      // a little too large: whole, unscaled, as before
        [InlineData(100, 50, 330, 330, 25, 0, 50, 50, 1.0)]        // smaller than the box: the part shown, never enlarged
        public void The_cover_crop_is_the_part_a_centre_crop_view_shows(
            int width, int height, int boxWidth, int boxHeight, int x, int y, int cropWidth, int cropHeight, double scale)
        {
            var crop = ImageSampling.CoverCropOf(width, height, boxWidth, boxHeight);

            Assert.Equal(new CoverCrop(x, y, cropWidth, cropHeight, scale), crop);
        }

        [Fact]
        public void A_picture_still_far_too_large_is_cropped_and_scaled_to_just_cover_the_box()
        {
            // Cover scale 0.5 (below 0.67): the visible part is the box at twice the size, kept at half.
            var crop = ImageSampling.CoverCropOf(800, 600, 300, 300);

            Assert.Equal(new CoverCrop(100, 0, 600, 600, 0.5), crop);
            Assert.Equal(300, crop.Width * crop.Scale);
            Assert.False(crop.IsWhole(800, 600));
            Assert.True(ImageSampling.CoverCropOf(300, 300, 300, 300).IsWhole(300, 300));
        }

        [Fact]
        public void The_cover_crop_refuses_empty_sizes()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ImageSampling.CoverCropOf(0, 10, 10, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => ImageSampling.CoverCropOf(10, 10, 10, 0));
        }

        [Fact]
        public void The_decode_queue_runs_at_most_its_workers_at_once_and_the_latest_waiting_request_first()
        {
            var queue = new DecodeQueue(1);
            using var blocker = new ManualResetEventSlim();
            using var started = new ManualResetEventSlim();
            using var done = new CountdownEvent(4);
            var order = new List<int>();
            var running = 0;
            var most = 0;

            void Record(int id)
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref most, now);

                lock (order)
                {
                    order.Add(id);
                }

                Interlocked.Decrement(ref running);
                done.Signal();
            }

            queue.Post(() =>
            {
                started.Set();
                Assert.True(blocker.Wait(Wait));
                Record(0);
            });

            Assert.True(started.Wait(Wait));

            // Bound while the worker is busy: the last one bound is the one the reader is looking at.
            queue.Post(() => Record(1));
            queue.Post(() => Record(2));
            queue.Post(() => Record(3));

            blocker.Set();
            Assert.True(done.Wait(Wait));

            Assert.Equal([0, 3, 2, 1], order);
            Assert.Equal(1, most);
        }

        [Fact]
        public void The_decode_queue_uses_several_workers_and_a_failing_request_does_not_stop_it()
        {
            var queue = new DecodeQueue(3);
            using var done = new CountdownEvent(50);
            var running = 0;
            var most = 0;

            for (var i = 0; i < 50; i++)
            {
                var fail = i % 7 == 0;

                queue.Post(() =>
                {
                    var now = Interlocked.Increment(ref running);
                    InterlockedMax(ref most, now);
                    Thread.Sleep(2);
                    Interlocked.Decrement(ref running);
                    done.Signal();

                    if (fail)
                    {
                        throw new InvalidOperationException("synthetic failure");
                    }
                });
            }

            Assert.True(done.Wait(Wait));
            Assert.InRange(most, 1, 3);
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 1)]
        [InlineData(4, 2)]
        [InlineData(8, 3)]
        [InlineData(16, 3)]
        public void The_decode_workers_are_half_the_processors_from_one_to_three(int processors, int workers)
        {
            Assert.Equal(workers, DecodeQueue.WorkersFor(processors));
            Assert.Throws<ArgumentOutOfRangeException>(() => new DecodeQueue(0));
        }

        [Fact]
        public void Later_batches_are_added_below_as_one_change_in_the_same_order_as_one_by_one()
        {
            using var folder = new TemporaryFolder();
            SyntheticArchive.Write(folder.Path, new SyntheticArchiveOptions(Messages: 600, Days: 90, Images: 240));

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, SyntheticArchive.Local);
            var gallery = PhotoGalleryLoad.Batches(
                PhotoGalleryLoad.ReadPagesNewestFirst(reader), timeline, CultureInfo.GetCultureInfo("en-GB"), 20, 40).ToList();
            Assert.True(gallery.Count >= 3, "the synthetic archive gives several batches");

            var rows = new GalleryRowList(gallery[0].Rows(3));
            var events = new List<NotifyCollectionChangedEventArgs>();
            rows.CollectionChanged += (_, e) => events.Add(e);

            foreach (var batch in gallery.Skip(1))
            {
                var before = rows.Count;
                var added = batch.Rows(3);

                rows.AddRange(added);

                var change = events[^1];
                Assert.Equal(NotifyCollectionChangedAction.Add, change.Action);
                Assert.Equal(before, change.NewStartingIndex);
                Assert.Equal(added, change.NewItems!.Cast<GalleryRow>().ToList());
            }

            Assert.Equal(gallery.Count - 1, events.Count);
            Assert.Equal(gallery.SelectMany(batch => batch.Rows(3)).Select(Describe).ToList(), rows.Select(Describe).ToList());

            rows.AddRange([]);
            Assert.Equal(gallery.Count - 1, events.Count);
        }

        private static string Describe(GalleryRow row) => row switch
        {
            GalleryHeaderRow header => "H " + header.Title,
            GalleryPhotoRow photos => $"P {photos.FirstIndex}: " + string.Join(",", photos.Photos.Select(p => p.AttachmentID)),
            _ => "?",
        };

        private static void InterlockedMax(ref int target, int value)
        {
            int current;

            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }
}
