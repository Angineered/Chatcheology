using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Presentation;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// The conversation screen's guard between reads and closing the archive, the decode sample sizes,
    /// and the search highlight that belongs to the message.
    /// </summary>
    public sealed class ReadGateAndSamplingTests
    {
        // ---------------------------------------------------------------------------------------
        // ArchiveReadGate.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public async Task A_close_that_begins_while_a_read_prepares_stops_the_read_before_it_starts()
        {
            var gate = new ArchiveReadGate();
            var preparing = new TaskCompletionSource();
            var readRan = false;
            var closed = false;

            var run = gate.RunAsync(() => preparing.Task, () => readRan = true);
            var close = gate.CloseAsync(() => Task.CompletedTask, () => closed = true);

            // The close waits for the read it knows about.
            Assert.False(closed);

            preparing.SetResult();
            var result = await run;
            await close;

            Assert.False(result.Ran);
            Assert.False(readRan);
            Assert.True(closed);
        }

        [Fact]
        public async Task A_read_under_way_finishes_before_the_archive_is_closed()
        {
            var gate = new ArchiveReadGate();
            using var reading = new ManualResetEventSlim();
            using var started = new ManualResetEventSlim();
            var order = new List<string>();

            var run = gate.RunAsync(() => Task.CompletedTask, () =>
            {
                started.Set();
                reading.Wait();
                lock (order)
                {
                    order.Add("read");
                }

                return 7;
            });

            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

            var close = gate.CloseAsync(() => Task.CompletedTask, () =>
            {
                lock (order)
                {
                    order.Add("close");
                }
            });

            Assert.False(close.IsCompleted);

            reading.Set();
            var result = await run;
            await close;

            Assert.True(result.Ran);
            Assert.Equal(7, result.Value);
            Assert.Equal(["read", "close"], order);
        }

        [Fact]
        public async Task Nothing_is_read_after_closing_and_closing_twice_closes_once()
        {
            var gate = new ArchiveReadGate();
            var closes = 0;

            await gate.CloseAsync(() => Task.CompletedTask, () => closes++);
            await gate.CloseAsync(() => Task.CompletedTask, () => closes++);

            var result = await gate.RunAsync<int>(() => Task.CompletedTask, () => throw new InvalidOperationException("Must not run."));

            Assert.True(gate.IsClosed);
            Assert.False(result.Ran);
            Assert.Equal(1, closes);
        }

        [Fact]
        public async Task A_failed_read_reaches_its_caller_and_the_archive_still_closes()
        {
            var gate = new ArchiveReadGate();
            var closed = false;

            var run = gate.RunAsync<int>(() => Task.CompletedTask, () => throw new ArchiveFormatException("Simulated."));

            await Assert.ThrowsAsync<ArchiveFormatException>(() => run);
            await gate.CloseAsync(() => Task.CompletedTask, () => closed = true);

            Assert.True(closed);
        }

        [Fact]
        public async Task The_archive_closes_even_when_stopping_the_other_reads_fails()
        {
            var gate = new ArchiveReadGate();
            var closed = false;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                gate.CloseAsync(() => throw new InvalidOperationException("Simulated."), () => closed = true));

            Assert.True(closed);
        }

        // ---------------------------------------------------------------------------------------
        // Decode sample sizes.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData(4000, 3000, 1080, 2400, 2)]
        [InlineData(4000, 3000, 2160, 4800, 1)]
        [InlineData(1080, 2400, 1080, 2400, 1)]
        [InlineData(8000, 6000, 1080, 2400, 4)]
        [InlineData(640, 480, 1080, 2400, 1)]
        public void The_full_screen_sample_keeps_the_picture_at_least_as_large_as_it_is_shown(
            int width, int height, int screenWidth, int screenHeight, int expected)
        {
            var sample = ImageSampling.FitSample(width, height, screenWidth, screenHeight);

            Assert.Equal(expected, sample);

            // At that sample the picture still covers its fitted size; at the next it would not.
            Assert.True(Math.Min((double)screenWidth * sample / width, (double)screenHeight * sample / height) <= 1
                        || sample == 1);
            Assert.True(Math.Min((double)screenWidth * sample * 2 / width, (double)screenHeight * sample * 2 / height) > 1);
        }

        [Fact]
        public void A_large_photo_is_never_decoded_at_full_size_for_the_viewer()
        {
            // 4000 x 3000 at 4 bytes a pixel is about 48 MB; the viewer's decode is a quarter of that.
            var sample = ImageSampling.FitSample(4000, 3000, 1080, 2400);
            var decodedBytes = (4000 / sample) * (3000 / sample) * 4L;

            Assert.True(decodedBytes <= 12_000_000);
            Assert.Equal((1080, 810), ImageSampling.FitInside(4000 / sample, 3000 / sample, 1080, 2400));
        }

        [Theory]
        [InlineData(4000, 3000, 720, 540, 4)]
        [InlineData(640, 480, 720, 540, 1)]
        [InlineData(1920, 1080, 480, 480, 2)]
        public void The_thumbnail_sample_still_covers_the_box(int width, int height, int boxWidth, int boxHeight, int expected)
        {
            var sample = ImageSampling.CoverSample(width, height, boxWidth, boxHeight);

            Assert.Equal(expected, sample);
            Assert.True(sample == 1 || (width / sample >= boxWidth && height / sample >= boxHeight));
        }

        [Fact]
        public void Fit_inside_keeps_the_shape_and_never_enlarges()
        {
            Assert.Equal((2160, 1620), ImageSampling.FitInside(4000, 3000, 2160, 4096));
            Assert.Equal((300, 200), ImageSampling.FitInside(300, 200, 2160, 4096));
            Assert.Throws<ArgumentOutOfRangeException>(() => ImageSampling.FitInside(0, 200, 10, 10));
        }

        // ---------------------------------------------------------------------------------------
        // The search highlight.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void The_highlight_belongs_to_the_message_and_says_when_it_changes()
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, 400, s => TestArchives.OnDay(1, s));

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);
            timeline.OpenAt(300);

            var row = (MessageRow)timeline.Rows[timeline.IndexOfMessage(300)];
            var changes = new List<string?>();
            row.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

            row.IsHighlighted = true;
            row.IsHighlighted = true;

            // A page inserted above moves the row's index, not the highlight.
            timeline.Prepend(timeline.PrepareEarlier()!);
            var moved = (MessageRow)timeline.Rows[timeline.IndexOfMessage(300)];

            Assert.Same(row, moved);
            Assert.True(moved.IsHighlighted);
            Assert.Single(timeline.Rows.OfType<MessageRow>(), message => message.IsHighlighted);

            row.IsHighlighted = false;
            Assert.Equal([nameof(MessageRow.IsHighlighted), nameof(MessageRow.IsHighlighted)], changes);
        }
    }
}
