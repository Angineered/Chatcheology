using System.Collections.Specialized;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// Paging the conversation both ways, and the date separators where pages join: each day's
    /// separator exactly once, before its first loaded message, however the pages fall.
    /// </summary>
    public sealed class ConversationTimelineTests
    {
        private const int Count = 400;

        public static TheoryData<string> Layouts => ["join inside a day", "join at a day start", "page all one day", "times out of order"];

        private static Func<int, DateTime> Layout(string name) => name switch
        {
            // Pages from the newest: 251..400, 101..250, 1..100.
            "join inside a day" => s => TestArchives.OnDay(s <= 240 ? 1 : s <= 260 ? 2 : 3, s),
            "join at a day start" => s => TestArchives.OnDay(s <= 250 ? 1 : 2, s),
            "page all one day" => s => TestArchives.OnDay(s <= 100 ? 1 : 2, s),

            // Chat order decides, not time order: days that go back and forth.
            "times out of order" => s => TestArchives.OnDay((s / 7) % 5 + 1, s),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        [Theory]
        [MemberData(nameof(Layouts))]
        public void Scrolling_up_to_the_start_gives_the_rows_of_the_whole_conversation_built_at_once(string layout)
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, Count, Layout(layout));

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);

            timeline.OpenAtLatest();

            while (timeline.ReadEarlier() is { } page)
            {
                timeline.Prepend(page);
            }

            Assert.False(timeline.HasEarlier);
            Assert.Equal(TestArchives.Describe(WholeConversation(reader, timeline)), TestArchives.Describe(timeline.Rows));
        }

        [Theory]
        [MemberData(nameof(Layouts))]
        public void Scrolling_down_to_the_end_gives_the_rows_of_the_whole_conversation_built_at_once(string layout)
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, Count, Layout(layout));

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);

            timeline.OpenAt(1);

            while (timeline.ReadLater() is { } page)
            {
                timeline.Append(page);
            }

            Assert.False(timeline.HasLater);
            Assert.Equal(TestArchives.Describe(WholeConversation(reader, timeline)), TestArchives.Describe(timeline.Rows));
        }

        [Theory]
        [MemberData(nameof(Layouts))]
        public void Opening_in_the_middle_and_scrolling_both_ways_gives_the_whole_conversation(string layout)
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, Count, Layout(layout));

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);

            timeline.OpenAt(251);

            while (timeline.ReadEarlier() is { } earlier)
            {
                timeline.Prepend(earlier);
            }

            while (timeline.ReadLater() is { } later)
            {
                timeline.Append(later);
            }

            Assert.Equal(TestArchives.Describe(WholeConversation(reader, timeline)), TestArchives.Describe(timeline.Rows));
        }

        [Fact]
        public void A_join_inside_a_day_keeps_one_separator_above_the_days_first_loaded_message()
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, Count, Layout("join inside a day"));

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);

            timeline.OpenAtLatest();
            Assert.Equal("D 2025-03-02", TestArchives.Describe(timeline.Rows)[0]);

            timeline.Prepend(timeline.ReadEarlier()!);

            var rows = TestArchives.Describe(timeline.Rows);

            // Day 2 runs 241..260; the page join is between 250 and 251.
            Assert.Single(rows, row => row == "D 2025-03-02");
            Assert.Equal(rows.IndexOf("M 241") - 1, rows.IndexOf("D 2025-03-02"));
            Assert.Equal(rows.IndexOf("M 250") + 1, rows.IndexOf("M 251"));
        }

        [Fact]
        public void A_join_inside_a_day_keeps_the_row_above_every_loaded_message_the_same()
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, Count, Layout("join inside a day"));

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);

            timeline.OpenAtLatest();
            var before = timeline.Rows.OfType<MessageRow>().ToDictionary(m => m, m => timeline.Rows[timeline.Rows.IndexOf(m) - 1]);

            timeline.Prepend(timeline.ReadEarlier()!);

            // Only the first loaded message's row above changes (the old top separator gave way to
            // message 250); every other loaded message keeps the very same row object above it.
            var changed = before.Where(pair => !ReferenceEquals(timeline.Rows[timeline.Rows.IndexOf(pair.Key) - 1], pair.Value))
                .Select(pair => pair.Key.SequenceNumber)
                .ToList();

            Assert.Equal([251], changed);
        }

        [Fact]
        public void A_join_at_a_day_start_keeps_the_later_days_separator_between_the_pages()
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, Count, Layout("join at a day start"));

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);

            timeline.OpenAtLatest();
            timeline.Prepend(timeline.ReadEarlier()!);

            var rows = TestArchives.Describe(timeline.Rows);

            Assert.Equal(rows.IndexOf("M 250") + 1, rows.IndexOf("D 2025-03-02"));
            Assert.Equal(rows.IndexOf("M 251") - 1, rows.IndexOf("D 2025-03-02"));
            Assert.Single(rows, row => row == "D 2025-03-02");
        }

        [Theory]
        [InlineData("join inside a day", 2)]
        [InlineData("join at a day start", 1)]
        [InlineData("page all one day", 2)]
        public void An_earlier_page_moves_every_loaded_message_down_by_the_count_returned_in_few_notifications(
            string layout, int notifications)
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, Count, Layout(layout));

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);

            timeline.OpenAtLatest();
            var before = timeline.IndexOfMessage(300);

            var events = new List<NotifyCollectionChangedEventArgs>();
            timeline.Rows.CollectionChanged += (_, e) => events.Add(e);

            var added = timeline.Prepend(timeline.ReadEarlier()!);

            Assert.Equal(before + added, timeline.IndexOfMessage(300));
            // A join inside a day takes the top separator out (one notification), then adds the page
            // (one more); otherwise the page goes in with a single notification.
            Assert.Equal(notifications, events.Count);
            Assert.Equal(NotifyCollectionChangedAction.Add, events[^1].Action);
            Assert.Equal(0, events[^1].NewStartingIndex);
            Assert.Equal(added, events.Sum(e => e.NewItems?.Count ?? 0) - events.Sum(e => e.OldItems?.Count ?? 0));

            if (notifications == 2)
            {
                Assert.Equal(NotifyCollectionChangedAction.Remove, events[0].Action);
                Assert.Equal(0, events[0].OldStartingIndex);
            }
        }

        [Fact]
        public void Opening_at_the_latest_loads_the_newest_page_with_the_last_message_last()
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, Count, Layout("join inside a day"));

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);

            var last = timeline.OpenAtLatest();

            var message = Assert.IsType<MessageRow>(timeline.Rows[last]);
            Assert.Equal(Count, message.SequenceNumber);
            Assert.Equal(ConversationTimeline.PageSize, timeline.Rows.OfType<MessageRow>().Count());
            Assert.True(timeline.HasEarlier);
            Assert.False(timeline.HasLater);
            Assert.Null(timeline.ReadLater());
        }

        [Fact]
        public void Opening_at_a_message_returns_its_row_with_a_page_each_side()
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, Count, Layout("join inside a day"));

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);

            var index = timeline.OpenAt(200);

            Assert.Equal(200, Assert.IsType<MessageRow>(timeline.Rows[index]).SequenceNumber);
            Assert.Equal(50, timeline.Rows.OfType<MessageRow>().First().SequenceNumber);
            Assert.Equal(349, timeline.Rows.OfType<MessageRow>().Last().SequenceNumber);
            Assert.True(timeline.HasEarlier);
            Assert.True(timeline.HasLater);
        }

        [Fact]
        public void Opening_at_the_first_message_has_nothing_earlier()
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, Count, Layout("join inside a day"));

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);

            Assert.Equal(1, timeline.OpenAt(1));
            Assert.False(timeline.HasEarlier);
            Assert.Null(timeline.ReadEarlier());
            Assert.True(timeline.HasLater);
        }

        [Fact]
        public void An_archive_with_no_messages_opens_empty()
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, 0, s => TestArchives.OnDay(1, s));

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);

            Assert.Equal(-1, timeline.OpenAtLatest());
            Assert.Empty(timeline.Rows);
            Assert.False(timeline.HasEarlier);
            Assert.False(timeline.HasLater);
        }

        [Fact]
        public void Messages_from_me_are_mine_and_others_are_not()
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, 4, s => TestArchives.OnDay(1, s));

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);
            timeline.OpenAtLatest();

            var messages = timeline.Rows.OfType<MessageRow>().ToList();

            // TestArchives gives even sequence numbers to the local participant.
            Assert.Equal([false, true, false, true], messages.Select(m => m.IsMine));
            Assert.Equal(["Sam", "Alex", "Sam", "Alex"], messages.Select(m => m.SenderText));
        }

        [Fact]
        public void A_page_that_is_not_after_the_loaded_rows_is_refused_and_changes_nothing()
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, Count, Layout("join inside a day"));

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);

            timeline.OpenAt(1);
            var rows = TestArchives.Describe(timeline.Rows);

            // The first page again: it overlaps what is loaded, and it says there is more after it.
            var overlapping = reader.ReadPageFrom(1, ConversationTimeline.PageSize);

            Assert.Throws<ArgumentException>(() => timeline.Append(overlapping));
            Assert.Equal(rows, TestArchives.Describe(timeline.Rows));
            Assert.True(timeline.HasLater);
            Assert.False(timeline.HasEarlier);
        }

        [Fact]
        public void A_refused_append_keeps_has_later_as_it_was()
        {
            using var folder = new TemporaryFolder();
            TestArchives.Write(folder.Path, Count, Layout("join inside a day"));

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, TestArchives.Local);

            timeline.OpenAtLatest();
            Assert.False(timeline.HasLater);

            // An earlier page, which says there is more after it.
            var earlier = reader.ReadPageBefore(Count - 10, ConversationTimeline.PageSize);
            Assert.True(earlier.HasLater);

            Assert.Throws<ArgumentException>(() => timeline.Append(earlier));
            Assert.False(timeline.HasLater);
        }

        /// <summary>Every message, built into rows in one go.</summary>
        private static List<ConversationRow> WholeConversation(ArchiveReader reader, ConversationTimeline timeline)
        {
            var pages = reader.ReadAllPages().ToList();

            return timeline.BuildRows(
                pages.SelectMany(page => page.Messages).ToList(),
                pages.SelectMany(page => page.Media).DistinctBy(pair => pair.Key).ToDictionary(),
                previousDay: null);
        }
    }
}
