using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Synthetic;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>What one message row and its cards show.</summary>
    public sealed class MessageRowTests
    {
        [Fact]
        public void The_media_placeholder_is_hidden_and_its_card_stands_in()
        {
            var rows = SyntheticRows();

            var withMedia = rows.First(message => message.Attachments.Count > 0);

            Assert.Equal(string.Empty, withMedia.Text);
            Assert.False(withMedia.HasText);
        }

        [Fact]
        public void Missing_and_unmatched_attachments_get_their_own_headings()
        {
            var cards = SyntheticRows().SelectMany(message => message.Attachments).ToList();

            Assert.Equal(3, cards.Count(card => card.Kind == AttachmentCardKind.Missing && card.Heading == "Media missing"));
            Assert.Equal(3, cards.Count(card => card.Kind == AttachmentCardKind.Unmatched && card.Heading == "Media not yet matched"));
            Assert.All(cards.Where(card => card.Kind != AttachmentCardKind.Image), card => Assert.Null(card.FullPath));
        }

        [Fact]
        public void A_system_message_is_never_mine()
        {
            var system = SyntheticRows().First(message => message.IsSystem);

            Assert.False(system.IsMine);
            Assert.Equal("System", system.SenderText);
        }

        [Theory]
        [InlineData(640, 480, 240, 180)]
        [InlineData(480, 640, 240, 320)]
        [InlineData(600, 600, 240, 240)]
        public void An_image_has_its_final_height_from_the_recorded_size(int width, int height, double shownWidth, double expected)
        {
            var card = SyntheticRows()
                .SelectMany(message => message.Attachments)
                .First(card => card.Width == width && card.Height == height);

            Assert.Equal(expected, card.HeightFor(shownWidth));
        }

        private static List<MessageRow> SyntheticRows()
        {
            using var folder = new TemporaryFolder();
            SyntheticArchive.Write(folder.Path, new SyntheticArchiveOptions());

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, SyntheticArchive.Local);
            timeline.OpenAt(1);

            while (timeline.ReadLater() is { } page)
            {
                timeline.Append(page);
            }

            return timeline.Rows.OfType<MessageRow>().ToList();
        }
    }
}
