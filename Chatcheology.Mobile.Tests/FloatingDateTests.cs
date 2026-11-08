using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// The floating date (CH-65): the top row's day in the separators' wording, shown only while the reader
    /// scrolls and hidden after the list rests, never by a stale hide.
    /// </summary>
    public sealed class FloatingDateTests
    {
        /// <summary>Messages 1..10 on day one, 11..20 on day two, 21..30 on day three.</summary>
        private static DateTime ThreeDays(int sequence) => TestArchives.OnDay((sequence - 1) / 10 + 1, sequence);

        [Fact]
        public void The_top_row_gives_its_day_in_the_separator_wording_for_messages_and_separators()
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, 30, ThreeDays);

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);
            timeline.OpenAtLatest();

            var rows = timeline.Rows;
            var floating = new FloatingDate();

            for (var i = 0; i < rows.Count; i++)
            {
                floating.TopRowChanged(rows, i);

                Assert.Equal(rows[i].Day, floating.Day);
                Assert.Equal(DateSeparatorRow.DayText(rows[i].Day), floating.Text);

                if (rows[i] is DateSeparatorRow separator)
                {
                    Assert.Equal(separator.Text, floating.Text);
                }
            }
        }

        [Fact]
        public void The_text_changes_only_when_the_day_changes()
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, 30, ThreeDays);

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);
            timeline.OpenAtLatest();

            var rows = timeline.Rows;
            var floating = new FloatingDate();
            var changes = 0;

            for (var i = 0; i < rows.Count; i++)
            {
                if (floating.TopRowChanged(rows, i))
                {
                    changes++;
                }
            }

            Assert.Equal(3, changes);

            // Back up within the last day: no change; the separator of day two: one.
            var text = floating.Text;
            Assert.False(floating.TopRowChanged(rows, rows.Count - 2));
            Assert.Same(text, floating.Text);

            var dayTwo = rows.ToList().FindIndex(row => row is DateSeparatorRow && row.Day == DateOnly.FromDateTime(ThreeDays(11)));
            Assert.True(floating.TopRowChanged(rows, dayTwo));
            Assert.Equal(DateSeparatorRow.DayText(DateOnly.FromDateTime(ThreeDays(11))), floating.Text);
        }

        [Fact]
        public void An_index_outside_the_rows_keeps_the_day_and_never_throws()
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, 30, ThreeDays);

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);
            timeline.OpenAtLatest();

            var floating = new FloatingDate();

            Assert.False(floating.TopRowChanged(timeline.Rows, -1));
            Assert.Null(floating.Day);
            Assert.Equal(string.Empty, floating.Text);

            Assert.True(floating.TopRowChanged(timeline.Rows, 0));
            var day = floating.Day;

            Assert.False(floating.TopRowChanged(timeline.Rows, timeline.Rows.Count));
            Assert.False(floating.TopRowChanged([], 0));
            Assert.Equal(day, floating.Day);
            Assert.Null(FloatingDate.DayAt(timeline.Rows, -1));
            Assert.Null(FloatingDate.DayAt(timeline.Rows, timeline.Rows.Count));
        }

        [Fact]
        public void A_page_added_above_moves_the_index_but_the_day_follows_the_row()
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, 400, s => TestArchives.OnDay(s <= 240 ? 1 : s <= 260 ? 2 : 3, s));

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);
            timeline.OpenAtLatest();

            var floating = new FloatingDate();
            floating.TopRowChanged(timeline.Rows, 0);
            var top = timeline.Rows[0];

            var page = timeline.ReadEarlier();
            Assert.NotNull(page);
            timeline.Prepend(page);

            // The list keeps the same row in view; its new index gives the same day, so nothing changes.
            var moved = timeline.Rows.IndexOf(top);

            if (moved >= 0)
            {
                Assert.False(floating.TopRowChanged(timeline.Rows, moved));
                Assert.Equal(top.Day, floating.Day);
            }

            // A row of an earlier day now at the top changes it.
            Assert.True(floating.TopRowChanged(timeline.Rows, 0));
            Assert.Equal(timeline.Rows[0].Day, floating.Day);
        }

        [Fact]
        public void Hidden_until_the_reader_scrolls()
        {
            var floating = new FloatingDate();

            Assert.False(floating.IsShown);
            Assert.Null(floating.ScrollStopped());

            floating.ScrollStarted();
            Assert.True(floating.IsShown);
        }

        [Fact]
        public void Hidden_after_the_delay_once_the_list_rests()
        {
            var floating = new FloatingDate();

            floating.ScrollStarted();
            var ticket = floating.ScrollStopped();

            Assert.NotNull(ticket);
            Assert.True(floating.IsShown);
            Assert.True(floating.HideIfStill(ticket.Value));
            Assert.False(floating.IsShown);

            // Already hidden: a second call does nothing.
            Assert.False(floating.HideIfStill(ticket.Value));
            Assert.Equal(TimeSpan.FromMilliseconds(1500), FloatingDate.HideDelay);
        }

        [Fact]
        public void Scrolling_again_before_the_delay_keeps_it_shown()
        {
            var floating = new FloatingDate();

            floating.ScrollStarted();
            var first = floating.ScrollStopped()!.Value;

            floating.ScrollStarted();
            Assert.False(floating.HideIfStill(first));
            Assert.True(floating.IsShown);

            var second = floating.ScrollStopped()!.Value;
            Assert.False(floating.HideIfStill(first));
            Assert.True(floating.HideIfStill(second));
            Assert.False(floating.IsShown);
        }

        [Fact]
        public void Reset_hides_at_once_and_drops_a_pending_hide()
        {
            var floating = new FloatingDate();

            floating.ScrollStarted();
            var ticket = floating.ScrollStopped()!.Value;

            floating.Reset();
            Assert.False(floating.IsShown);

            floating.ScrollStarted();
            Assert.False(floating.HideIfStill(ticket));
            Assert.True(floating.IsShown);
        }
    }
}
