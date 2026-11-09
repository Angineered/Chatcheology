using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Gallery;
using Chatcheology.Mobile.Search;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// CH-71 step 3b: messages read from WhatsApp's message store (<c>Source = Msgstore</c>) on the phone. They are shown
    /// exactly like workspace messages (text, sender, forwarded label, captions, gallery, search, days); a placeholder
    /// for something the archive cannot show yet is its fixed wording, muted, never the sender's text; a system row is a
    /// System line. Invented text only.
    /// </summary>
    public sealed class NewerMessagesPresentationTests : IDisposable
    {
        private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

        private readonly TemporaryFolder _folder = new();

        public void Dispose() => _folder.Dispose();

        /// <summary>Workspace messages, then the newer ones as the archive export writes them.</summary>
        private static ArchiveBuilder Build()
        {
            var builder = new ArchiveBuilder();
            builder.Add("Synthetic workspace line", ArchiveBuilder.Local, new DateTime(2025, 3, 1, 8, 0, 0));
            builder.Add("Poll (not shown in this archive)", ArchiveBuilder.Other, new DateTime(2025, 3, 1, 8, 1, 0));

            builder.Source = ArchiveMessageSource.Msgstore;
            builder.Add("Synthetic newer text about a cafe", ArchiveBuilder.Other, new DateTime(2025, 3, 2, 9, 0, 0), forwarded: true, score: 1);
            builder.Add("Synthetic newer text of mine", ArchiveBuilder.Local, new DateTime(2025, 3, 2, 9, 1, 0), forwarded: null);
            builder.Add("", ArchiveBuilder.Other, new DateTime(2025, 3, 2, 9, 2, 0), null, null,
                new AttachmentSpec(601, Caption: "Synthetic newer cafe caption", IsForwarded: true, ForwardScore: 5));
            builder.Add("", ArchiveBuilder.Local, new DateTime(2025, 3, 2, 9, 3, 0), null, null,
                new AttachmentSpec(602, ArchiveAttachmentState.Unmatched, Caption: "Synthetic caption of a lost picture"));
            builder.Add("Poll (not shown in this archive)", ArchiveBuilder.Other, new DateTime(2025, 3, 2, 9, 4, 0));
            builder.Add("Call (not shown in this archive)", ArchiveBuilder.Local, new DateTime(2025, 3, 2, 9, 5, 0));
            builder.Add("System message (not shown in this archive)", sender: null, at: new DateTime(2025, 3, 2, 9, 6, 0));

            return builder;
        }

        private List<MessageRow> Messages()
        {
            Build().Write(_folder.Path);

            using var reader = ArchiveReader.Open(_folder.Path);
            var timeline = new ConversationTimeline(reader, ArchiveBuilder.Local);
            timeline.OpenAt(1);

            while (timeline.ReadLater() is { } page)
            {
                timeline.Append(page);
            }

            return timeline.Rows.OfType<MessageRow>().ToList();
        }

        [Fact]
        public void Newer_text_and_media_are_shown_like_workspace_messages()
        {
            var rows = Messages();

            Assert.Equal(9, rows.Count);

            var text = rows[2];
            Assert.Equal("Synthetic newer text about a cafe", text.Text);
            Assert.Equal("Sam", text.SenderText);
            Assert.False(text.IsMine);
            Assert.Equal("Forwarded", text.ForwardedText);
            Assert.True(text.CanShareText);
            Assert.False(text.HasNotShownText);

            // Null forwarded = not known: no label.
            Assert.True(rows[3].IsMine);
            Assert.Equal(string.Empty, rows[3].ForwardedText);

            // A media message: no text of its own, the card and its caption; the attachment's forwarded fact labels it.
            var photo = rows[4];
            Assert.False(photo.HasText);
            Assert.False(photo.HasNotShownText);
            var card = Assert.Single(photo.ShownAttachments);
            Assert.Equal(AttachmentCardKind.Image, card.Kind);
            Assert.Equal("Synthetic newer cafe caption", card.Caption);
            Assert.Equal("Forwarded many times", photo.ForwardedText);

            var unmatched = Assert.Single(rows[5].ShownAttachments);
            Assert.Equal(AttachmentCardKind.Unmatched, unmatched.Kind);
            Assert.Equal("Media not yet matched", unmatched.Heading);
            Assert.Equal("Synthetic caption of a lost picture", unmatched.Caption);
        }

        [Fact]
        public void A_placeholder_is_a_muted_line_in_its_senders_bubble_never_the_senders_text()
        {
            var rows = Messages();

            var poll = rows[6];
            Assert.True(poll.HasNotShownText);
            Assert.Equal("Poll (not shown in this archive)", poll.NotShownText);
            Assert.False(poll.HasText);
            Assert.Null(poll.ShareText);
            Assert.False(poll.CanShareText);
            Assert.True(poll.CanStar);
            Assert.True(poll.IsShown);
            Assert.False(poll.IsSystem);
            Assert.False(poll.IsMine);
            Assert.Equal("Sam", poll.SenderText);

            var call = rows[7];
            Assert.True(call.IsMine);
            Assert.Equal("Call (not shown in this archive)", call.NotShownText);

            // The same words in the chat itself (a workspace message) are the sender's own text.
            var typed = rows[1];
            Assert.False(typed.HasNotShownText);
            Assert.Equal("Poll (not shown in this archive)", typed.Text);
            Assert.True(typed.CanShareText);
        }

        [Fact]
        public void A_system_row_is_a_system_line_with_its_fixed_wording()
        {
            var system = Messages()[8];

            Assert.True(system.IsSystem);
            Assert.False(system.IsMine);
            Assert.False(system.CanStar);
            Assert.False(system.HasNotShownText);
            Assert.Equal("System message (not shown in this archive)", system.Text);
        }

        [Fact]
        public void Every_placeholder_wording_is_recognised_only_on_a_msgstore_user_message_without_attachments()
        {
            foreach (var wording in MessageRow.NotShownWordings)
            {
                Assert.True(MessageRow.IsNotShownPlaceholder(Message(wording, ArchiveMessageSource.Msgstore)));
                Assert.Equal(string.Empty, MessageRow.PresentedText(Message(wording, ArchiveMessageSource.Msgstore)));

                Assert.False(MessageRow.IsNotShownPlaceholder(Message(wording, ArchiveMessageSource.Workspace)));
                Assert.False(MessageRow.IsNotShownPlaceholder(Message(wording, ArchiveMessageSource.Export)));
                Assert.False(MessageRow.IsNotShownPlaceholder(Message(wording, ArchiveMessageSource.Msgstore, system: true)));
                Assert.False(MessageRow.IsNotShownPlaceholder(Message(wording + " ", ArchiveMessageSource.Msgstore)));
            }

            Assert.Equal(7, MessageRow.NotShownWordings.Count);
            Assert.DoesNotContain("System message (not shown in this archive)", MessageRow.NotShownWordings);
        }

        [Fact]
        public void Search_finds_newer_text_and_captions_but_never_a_placeholders_wording()
        {
            Build().Write(_folder.Path);

            var index = ConversationSearch.Build(_folder.Path);

            Assert.Equal([5, 3], index.Find("cafe").Hits.Select(hit => hit.SequenceNumber));

            // Only the workspace message whose text happens to say it is found; placeholders are not text.
            Assert.Equal([2], index.Find("poll").Hits.Select(hit => hit.SequenceNumber));
            Assert.Empty(index.Find("call (not shown").Hits);
        }

        [Fact]
        public void Newer_photos_are_in_the_gallery_and_newer_days_in_the_calendar()
        {
            Build().Write(_folder.Path);

            using var reader = ArchiveReader.Open(_folder.Path);
            var timeline = new ConversationTimeline(reader, ArchiveBuilder.Local);

            var gallery = PhotoGallery.Build(reader.ReadAllPages(), timeline, English);

            Assert.Equal([601L], gallery.Photos.Select(photo => photo.AttachmentID));
            Assert.Equal([new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 2)], reader.ReadDays());
        }

        [Fact]
        public void A_starred_placeholder_lists_its_wording()
        {
            Build().Write(_folder.Path);

            using var reader = ArchiveReader.Open(_folder.Path);
            var settings = new Settings.ArchiveSettings(new MemorySettings());
            settings.SetStarredMessages("hash", [7]);
            var stars = new Stars(settings, "hash");

            var entry = Assert.Single(StarredList.Build(reader, ArchiveBuilder.Local, LowQualityIndex.Build(reader), stars, default));

            Assert.Equal("Poll (not shown in this archive)", entry.Snippet);
        }

        private static ArchiveMessage Message(string text, ArchiveMessageSource source, bool system = false) => new()
        {
            MessageID = 1,
            SequenceNumber = 1,
            DateTimeLocal = new DateTime(2025, 3, 2, 9, 0, 0),
            Kind = system ? ArchiveMessageKind.System : ArchiveMessageKind.User,
            SenderParticipantID = system ? null : ArchiveBuilder.Other,
            Text = text,
            IsForwarded = null,
            ForwardScore = null,
            Attachments = [],
            Source = source,
        };
    }
}
