using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Search;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>Searching message text, and when the list asks for another page.</summary>
    public sealed class SearchAndPagingTests : IDisposable
    {
        private readonly TemporaryFolder _folder = new();

        public void Dispose() => _folder.Dispose();

        private ConversationSearch Index(ArchiveBuilder builder)
        {
            builder.Write(_folder.Path);

            return ConversationSearch.Build(_folder.Path);
        }

        private static ArchiveBuilder Sample()
        {
            var builder = new ArchiveBuilder();
            builder.Add("Coffee at the café?", ArchiveBuilder.Local, new DateTime(2025, 3, 1, 8, 5, 0));
            builder.Add("CAFE is closed", ArchiveBuilder.Other, new DateTime(2025, 3, 1, 9, 15, 0));
            builder.Add("<Media omitted>", ArchiveBuilder.Other, new DateTime(2025, 3, 2, 10, 0, 0), null, null, new AttachmentSpec(1));
            builder.Add("The media omitted note", ArchiveBuilder.Local, new DateTime(2025, 3, 2, 10, 1, 0));
            builder.Add("Cafe changed its name", sender: null, at: new DateTime(2025, 3, 3, 11, 30, 0));
            builder.Add("Nothing to see", ArchiveBuilder.Other, new DateTime(2025, 3, 4, 12, 0, 0));

            return builder;
        }

        [Fact]
        public void Search_ignores_case_and_accents_and_lists_newest_first()
        {
            var index = Index(Sample());

            var results = index.Find("cafe");

            Assert.Equal([5, 2, 1], results.Hits.Select(hit => hit.SequenceNumber));
            Assert.Equal(3, results.TotalMatches);
            Assert.Equal([5, 2, 1], index.Find("CAFÉ").Hits.Select(hit => hit.SequenceNumber));
        }

        [Fact]
        public void The_media_placeholder_is_not_in_the_index()
        {
            var index = Index(Sample());

            Assert.Equal(5, index.Count);
            Assert.Equal([4], index.Find("Media omitted").Hits.Select(hit => hit.SequenceNumber));
            Assert.Empty(index.Find("<Media").Hits);
        }

        [Fact]
        public void A_hit_has_date_time_sender_snippet_and_highlights()
        {
            var hit = Index(Sample()).Find("café").Hits.Single(hit => hit.SequenceNumber == 1);

            Assert.Equal(new DateOnly(2025, 3, 1).ToString("d MMMM yyyy", CultureInfo.CurrentCulture), hit.DateText);
            Assert.Equal(new DateTime(2025, 3, 1, 8, 5, 0).ToString("HH:mm", CultureInfo.CurrentCulture), hit.TimeText);
            Assert.Equal("Alex", hit.SenderText);
            Assert.Equal("Coffee at the café?", hit.Snippet);
            Assert.Equal([new MatchRange(14, 4)], hit.Highlights);
            Assert.Equal([new MatchRange(14, 4)], hit.Matches);
        }

        [Fact]
        public void A_system_message_is_searched_and_named_as_system()
        {
            var hit = Index(Sample()).Find("name").Hits.Single();

            Assert.Equal(5, hit.SequenceNumber);
            Assert.Equal("System", hit.SenderText);
        }

        [Fact]
        public void Highlights_stay_right_when_folding_changes_the_length()
        {
            // "e" plus a combining acute accent is two characters that match the one-character "é".
            var builder = new ArchiveBuilder();
            builder.Add("Un café et un CAFÉ, cafe!");
            var hit = Assert.Single(Index(builder).Find("café").Hits);

            Assert.Equal(3, hit.Matches.Count);
            Assert.All(hit.Highlights, range =>
                Assert.Equal(0, CultureInfo.InvariantCulture.CompareInfo.Compare(
                    hit.Snippet.Substring(range.Start, range.Length), "café", CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace)));
            Assert.Equal(4, hit.Matches[1].Length);
        }

        [Fact]
        public void A_long_message_gives_a_cut_snippet_whose_highlights_point_into_it()
        {
            var text = string.Join(' ', Enumerable.Repeat("filler", 30)) + " NEEDLE here\nand " + string.Join(' ', Enumerable.Repeat("more", 40)) + " needle";
            var builder = new ArchiveBuilder();
            builder.Add(text);

            var hit = Assert.Single(Index(builder).Find("needle").Hits);

            Assert.StartsWith("…", hit.Snippet);
            Assert.EndsWith("…", hit.Snippet);
            Assert.DoesNotContain('\n', hit.Snippet);
            Assert.Equal(2, hit.Matches.Count);

            var highlight = Assert.Single(hit.Highlights);
            Assert.Equal("NEEDLE", hit.Snippet.Substring(highlight.Start, highlight.Length));
            Assert.Equal(ConversationSearch.ContextBefore + 1, highlight.Start);
        }

        [Fact]
        public void An_empty_or_blank_query_finds_nothing_and_spaces_around_a_query_are_ignored()
        {
            var index = Index(Sample());

            Assert.Same(SearchResults.None, index.Find(string.Empty));
            Assert.Same(SearchResults.None, index.Find("   "));
            Assert.Equal(3, index.Find("  cafe ").TotalMatches);
        }

        [Fact]
        public void Results_stop_at_the_maximum_but_count_every_match()
        {
            var builder = new ArchiveBuilder();

            for (var i = 1; i <= 30; i++)
            {
                builder.Add("Message " + i);
            }

            var results = Index(builder).Find("message", maximumHits: 10);

            Assert.Equal(30, results.TotalMatches);
            Assert.Equal(Enumerable.Range(21, 10).Reverse(), results.Hits.Select(hit => hit.SequenceNumber));
        }

        [Fact]
        public async Task A_session_builds_its_index_once_in_the_background()
        {
            var library = new ArchiveLibrary(Path.Combine(_folder.Path, "files"));
            var staging = library.CreateStagingFolder();
            Sample().Write(staging);
            library.Install(staging);

            var settings = new ArchiveSettings(new MemorySettings());
            using var session = ArchiveSession.Open(library.ListArchives(settings).Single(), settings);

            var first = session.GetSearchAsync();
            var second = session.GetSearchAsync();

            Assert.Same(first, second);
            Assert.Equal(3, (await first).Find("cafe").TotalMatches);
        }

        // ---------------------------------------------------------------------------------------
        // Paging.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void A_list_shorter_than_the_screen_wants_both_ways()
        {
            Assert.Equal(new PagingWant(true, true), PagingRule.Wanted(0, 9, 10, hasEarlier: true, hasLater: true));
        }

        [Theory]
        [InlineData(0, 10, 400, true, true, true, false)]
        [InlineData(15, 25, 400, true, true, true, false)]
        [InlineData(16, 26, 400, true, true, false, false)]
        [InlineData(370, 384, 400, true, true, false, true)]
        [InlineData(370, 383, 400, true, true, false, false)]
        [InlineData(0, 399, 400, false, false, false, false)]
        [InlineData(0, 399, 400, false, true, false, true)]
        [InlineData(-1, -1, 400, true, true, false, false)]
        [InlineData(0, 0, 0, true, true, false, false)]
        public void More_is_wanted_near_an_end_that_has_more(int first, int last, int count, bool hasEarlier, bool hasLater, bool earlier, bool later)
        {
            Assert.Equal(new PagingWant(earlier, later), PagingRule.Wanted(first, last, count, hasEarlier, hasLater));
        }

        [Fact]
        public void A_conversation_opened_in_the_middle_with_everything_on_screen_loads_both_ways_by_the_rule()
        {
            var builder = new ArchiveBuilder();

            for (var i = 1; i <= 320; i++)
            {
                builder.Add("Message " + i, at: TestArchives.OnDay(1, i));
            }

            builder.Write(_folder.Path);

            using var reader = ArchiveReader.Open(_folder.Path);
            var timeline = new ConversationTimeline(reader, ArchiveBuilder.Local, LowQualityIndex.Empty);
            timeline.OpenAt(160);

            // All rows on screen: both ways are wanted; load one page at a time as the pager does.
            for (var want = PagingRule.Wanted(0, timeline.Rows.Count - 1, timeline.Rows.Count, timeline.HasEarlier, timeline.HasLater);
                 want.Any;
                 want = PagingRule.Wanted(0, timeline.Rows.Count - 1, timeline.Rows.Count, timeline.HasEarlier, timeline.HasLater))
            {
                if (want.Earlier)
                {
                    timeline.Prepend(timeline.ReadEarlier()!);
                }
                else
                {
                    timeline.Append(timeline.ReadLater()!);
                }
            }

            Assert.Equal(320, timeline.Rows.OfType<MessageRow>().Count());
            Assert.False(timeline.HasEarlier);
            Assert.False(timeline.HasLater);
        }
    }
}
