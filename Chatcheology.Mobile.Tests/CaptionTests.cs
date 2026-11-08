using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Gallery;
using Chatcheology.Mobile.Presentation;
using Chatcheology.Mobile.Search;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// Picture captions on the phone (CH-71, archive v4): what a card shows under its picture and in the full-screen
    /// viewer, its links, and that search finds a message by a caption of a card it draws. Invented text only.
    /// </summary>
    public sealed class CaptionTests : IDisposable
    {
        private readonly TemporaryFolder _folder = new();

        public void Dispose() => _folder.Dispose();

        private List<MessageRow> Messages(ArchiveBuilder builder)
        {
            builder.Write(_folder.Path);

            using var reader = ArchiveReader.Open(_folder.Path);
            var timeline = new ConversationTimeline(reader, ArchiveBuilder.Local);
            timeline.OpenAt(1);

            while (timeline.ReadLater() is { } page)
            {
                timeline.Append(page);
            }

            return timeline.Rows.OfType<MessageRow>().ToList();
        }

        private static AttachmentCard Card(List<MessageRow> messages, long id) =>
            messages.SelectMany(m => m.Attachments).Single(card => card.AttachmentID == id);

        [Fact]
        public void A_card_shows_its_caption_exactly_as_stored_whatever_its_state()
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1, Caption: "Invented caption\nsecond line"));
            builder.Media(new AttachmentSpec(2, ArchiveAttachmentState.Missing, Caption: "Invented missing picture caption"));
            builder.Media(new AttachmentSpec(3, ArchiveAttachmentState.Unmatched, Caption: " Invented, with spaces "));
            builder.Media(new AttachmentSpec(4));
            builder.Media(new AttachmentSpec(5, Caption: "   "));

            var messages = Messages(builder);

            var restored = Card(messages, 1);
            Assert.True(restored.HasCaption);
            Assert.Equal("Invented caption\nsecond line", restored.Caption);
            Assert.Equal(AttachmentCardKind.Image, restored.Kind);

            Assert.Equal("Invented missing picture caption", Card(messages, 2).Caption);
            Assert.Equal(AttachmentCardKind.Missing, Card(messages, 2).Kind);
            Assert.Equal(" Invented, with spaces ", Card(messages, 3).Caption);

            // No caption, and a caption of only spaces, show nothing.
            Assert.False(Card(messages, 4).HasCaption);
            Assert.Equal(string.Empty, Card(messages, 4).Caption);
            Assert.False(Card(messages, 5).HasCaption);
            Assert.Empty(Card(messages, 5).CaptionSpans);

            // The caption is not the message text: the placeholder bubble still has none.
            Assert.All(messages, m => Assert.False(m.HasText));
        }

        [Fact]
        public void A_caption_is_link_scanned_like_message_text()
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1, Caption: "See www.example.org/invented and write to someone@example.org"));
            builder.Media(new AttachmentSpec(2, Caption: "No address here"));

            var messages = Messages(builder);
            var linked = Card(messages, 1);

            Assert.True(linked.CaptionHasLinks);
            Assert.Equal(linked.Caption, string.Concat(linked.CaptionSpans.Select(span => span.Text)));
            Assert.Equal(LinkScanner.Scan(linked.Caption), linked.CaptionSpans);
            Assert.Contains(linked.CaptionSpans, span => span.Kind == TextSpanKind.WebLink);
            Assert.Contains(linked.CaptionSpans, span => span.Kind == TextSpanKind.EmailAddress);

            Assert.False(Card(messages, 2).CaptionHasLinks);
        }

        [Fact]
        public void The_viewer_shows_each_photos_own_caption_as_it_moves()
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1, Caption: "First invented caption"));
            builder.Media(new AttachmentSpec(2));

            var messages = Messages(builder);
            var browser = new PhotoBrowser([Card(messages, 1), Card(messages, 2)], 0);

            Assert.Equal("First invented caption", browser.Current.Caption);
            Assert.True(browser.Current.HasCaption);

            Assert.True(browser.Next());
            Assert.False(browser.Current.HasCaption);
        }

        [Fact]
        public void Search_finds_a_message_by_the_caption_of_a_card_it_draws()
        {
            var builder = new ArchiveBuilder();
            builder.Add("Plain text first", ArchiveBuilder.Local);
            builder.Media(new AttachmentSpec(1, Caption: "Invented café picture"));
            builder.Add("Text and a picture", ArchiveBuilder.Other, null, null, null, new AttachmentSpec(2, Caption: "with a cafe caption"));
            builder.Media(new AttachmentSpec(3), new AttachmentSpec(4, LowQualityOf: 3, Caption: "hidden copy cafe"));
            builder.Media(new AttachmentSpec(5, Caption: "   "));
            builder.Write(_folder.Path);

            var index = ConversationSearch.Build(_folder.Path);

            var results = index.Find("CAFE");

            // Newest first; the hidden low-quality copy's caption is not searched (its card is not drawn).
            Assert.Equal([3, 2], results.Hits.Select(hit => hit.SequenceNumber));

            var both = results.Hits[0];
            Assert.Equal("Text and a picture with a cafe caption", both.Snippet);
            Assert.Single(both.Highlights);

            Assert.Equal("Invented café picture", results.Hits[1].Snippet);

            // A message that is only a picture with a caption is in the index; one whose only caption is spaces, or
            // sits on a hidden copy, is not.
            Assert.Equal(3, index.Count);
        }

        [Fact]
        public void Search_text_is_the_bubble_text_then_each_shown_caption_on_its_own_line()
        {
            var message = new ArchiveMessage
            {
                MessageID = 1,
                SequenceNumber = 1,
                DateTimeLocal = new DateTime(2025, 3, 1, 8, 0, 0),
                Kind = ArchiveMessageKind.User,
                SenderParticipantID = 2,
                Text = "<Media omitted>",
                IsForwarded = null,
                ForwardScore = null,
                Attachments =
                [
                    Attachment(1, "One"),
                    Attachment(2, null),
                    Attachment(3, "Hidden"),
                    Attachment(4, "Two"),
                ],
            };

            Assert.Equal("One\nTwo", MessageRow.SearchText(message, a => a.AttachmentID != 3));
            Assert.Equal(string.Empty, MessageRow.SearchText(message, _ => false));
        }

        [Fact]
        public void An_archive_before_version_four_reads_with_no_captions_and_no_id()
        {
            using var other = new TemporaryFolder();
            Synthetic.SyntheticArchive.Write(other.Path, new Synthetic.SyntheticArchiveOptions(Messages: 300, Days: 10, NotedDates: true, Captions: 3));

            using var v2 = ArchiveReader.Open(other.Path);
            Assert.False(v2.HasCaptions);
            Assert.Null(v2.Info.ArchiveId);
            Assert.All(v2.ReadAllPages().SelectMany(p => p.Messages).SelectMany(m => m.Attachments), a => Assert.Null(a.Caption));
        }

        private static ArchiveAttachment Attachment(long id, string? caption) => new()
        {
            AttachmentID = id,
            MessageID = 1,
            Ordinal = (int)id,
            State = ArchiveAttachmentState.Unmatched,
            MediaSha256 = null,
            OriginalFileName = null,
            IsForwarded = null,
            ForwardScore = null,
            LowQualityCopyOfAttachmentID = null,
            Basis = null,
            Caption = caption,
        };
    }
}
