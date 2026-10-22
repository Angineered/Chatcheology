using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Presentation;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// What bubbles and cards show: the desktop's rules for the placeholder, forwarded labels and
    /// low-quality copies, every card state, MIME types and links.
    /// </summary>
    public sealed class PresentationTests : IDisposable
    {
        private readonly TemporaryFolder _folder = new();

        public void Dispose() => _folder.Dispose();

        /// <summary>Writes the archive and loads the whole conversation as rows.</summary>
        private List<ConversationRow> Rows(ArchiveBuilder builder, Action<string>? afterWrite = null)
        {
            builder.Write(_folder.Path);
            afterWrite?.Invoke(_folder.Path);

            using var reader = ArchiveReader.Open(_folder.Path);
            var timeline = new ConversationTimeline(reader, ArchiveBuilder.Local);
            timeline.OpenAt(1);

            while (timeline.ReadLater() is { } page)
            {
                timeline.Append(page);
            }

            return timeline.Rows.ToList();
        }

        private List<MessageRow> Messages(ArchiveBuilder builder, Action<string>? afterWrite = null) =>
            Rows(builder, afterWrite).OfType<MessageRow>().ToList();

        private static AttachmentCard Card(List<MessageRow> messages, long attachmentID) =>
            messages.SelectMany(message => message.Attachments).Single(card => card.AttachmentID == attachmentID);

        // ---------------------------------------------------------------------------------------
        // The placeholder.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("<Media omitted>", "")]
        [InlineData("<Media omitted> ", "<Media omitted> ")]
        [InlineData("<media omitted>", "<media omitted>")]
        [InlineData("Look: <Media omitted>", "Look: <Media omitted>")]
        [InlineData("", "")]
        public void Only_the_exact_media_placeholder_is_hidden(string stored, string shown)
        {
            var builder = new ArchiveBuilder();
            builder.Add(stored, ArchiveBuilder.Other, null, null, null, new AttachmentSpec(1));

            var message = Assert.Single(Messages(builder));

            Assert.Equal(shown, message.Text);
            Assert.Equal(shown.Length > 0, message.HasText);
        }

        // ---------------------------------------------------------------------------------------
        // Forwarded.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData(null, null, "")]
        [InlineData(false, null, "")]
        [InlineData(false, 9, "")]
        [InlineData(true, null, "Forwarded")]
        [InlineData(true, 1, "Forwarded")]
        [InlineData(true, 4, "Forwarded")]
        [InlineData(true, 5, "Forwarded many times")]
        [InlineData(true, 127, "Forwarded many times")]
        public void A_text_message_is_labelled_from_its_own_forwarded_fact(bool? forwarded, int? score, string label)
        {
            var builder = new ArchiveBuilder();
            builder.Add("Hello", ArchiveBuilder.Other, null, forwarded, score);

            var message = Assert.Single(Messages(builder));

            Assert.Equal(label, message.ForwardedText);
            Assert.Equal(label.Length > 0, message.IsForwarded);
        }

        [Theory]
        [InlineData(null, null, false, null, null, null, "")]
        [InlineData(null, null, null, null, true, 2, "Forwarded")]
        [InlineData(false, null, true, 6, false, null, "Forwarded many times")]
        [InlineData(null, null, false, 8, true, 3, "Forwarded")]
        [InlineData(true, null, null, null, true, 5, "Forwarded many times")]
        [InlineData(true, 2, null, null, null, null, "Forwarded")]
        [InlineData(false, 7, false, 9, null, null, "")]
        public void A_message_is_forwarded_when_it_or_any_attachment_stores_one(
            bool? messageForwarded, int? messageScore, bool? first, int? firstScore, bool? second, int? secondScore, string label)
        {
            var builder = new ArchiveBuilder();
            builder.Add(
                "<Media omitted>",
                ArchiveBuilder.Other,
                null,
                messageForwarded,
                messageScore,
                new AttachmentSpec(1, IsForwarded: first, ForwardScore: firstScore),
                new AttachmentSpec(2, IsForwarded: second, ForwardScore: secondScore));

            Assert.Equal(label, Assert.Single(Messages(builder)).ForwardedText);
        }

        [Fact]
        public void The_many_times_score_is_the_desktops()
        {
            Assert.Equal(5, MessageRow.ForwardedManyTimesScore);
        }

        // ---------------------------------------------------------------------------------------
        // Sides.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Me_is_on_the_right_and_a_system_message_is_nobodys()
        {
            var builder = new ArchiveBuilder();
            builder.Add("Mine", ArchiveBuilder.Local);
            builder.Add("Theirs", ArchiveBuilder.Other);
            builder.Add("Messages are end-to-end encrypted.", sender: null);

            var messages = Messages(builder);

            Assert.Equal([true, false, false], messages.Select(m => m.IsMine));
            Assert.Equal([false, false, true], messages.Select(m => m.IsSystem));
            Assert.Equal("System", messages[2].SenderText);
        }

        // ---------------------------------------------------------------------------------------
        // Card states.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Every_card_state_has_its_heading()
        {
            var builder = new ArchiveBuilder();
            builder.Media(
                new AttachmentSpec(1, MediaType: "Image", Extension: ".jpg", Width: 640, Height: 480),
                new AttachmentSpec(2, MediaType: "Video", Extension: ".mp4", DurationMS: 65_400),
                new AttachmentSpec(3, MediaType: "Audio", Extension: ".opus", DurationMS: 3_725_000),
                new AttachmentSpec(4, MediaType: "Document", Extension: ".pdf"),
                new AttachmentSpec(5, MediaType: "Unknown", Extension: ".xyz"),
                new AttachmentSpec(6, MediaType: "Image", Extension: ".png"),
                new AttachmentSpec(7, ArchiveAttachmentState.Missing),
                new AttachmentSpec(8, ArchiveAttachmentState.Unmatched));

            // Restored, but its file is gone (should not happen after validation): never shown as missing.
            var messages = Messages(builder, folder => File.Delete(Directory.EnumerateFiles(Path.Combine(folder, "media"), "*.png").Single()));

            var expected = new (long Id, AttachmentCardKind Kind, string Heading, string? Mime)[]
            {
                (1, AttachmentCardKind.Image, "Photo", "image/jpeg"),
                (2, AttachmentCardKind.File, "Video · 1:05", "video/mp4"),
                (3, AttachmentCardKind.File, "Audio · 1:02:05", "audio/ogg"),
                (4, AttachmentCardKind.File, "Document", "application/pdf"),
                (5, AttachmentCardKind.CannotPreview, "Can't preview · Open in another app", "application/octet-stream"),
                (6, AttachmentCardKind.Unavailable, "Media file unavailable", null),
                (7, AttachmentCardKind.Missing, "Media missing", null),
                (8, AttachmentCardKind.Unmatched, "Media not yet matched", null),
            };

            foreach (var (id, kind, heading, mime) in expected)
            {
                var card = Card(messages, id);

                Assert.Equal(kind, card.Kind);
                Assert.Equal(heading, card.Heading);
                Assert.Equal(mime, card.MimeType);
                Assert.Equal(mime is not null, card.CanOpen);
                Assert.Equal(mime is not null, card.FullPath is not null && File.Exists(card.FullPath));
            }

            Assert.Equal("1:05", Card(messages, 2).DurationText);
            Assert.Equal(string.Empty, Card(messages, 4).DurationText);
            Assert.Equal(AttachmentCard.CannotPreviewHeading, Card(messages, 5).Heading);
        }

        [Theory]
        [InlineData(640, 480, 240, 180)]
        [InlineData(480, 640, 240, 320)]
        [InlineData(600, 600, 240, 240)]
        [InlineData(360, 960, 240, 320)]
        [InlineData(100, 1000, 300, 400)]
        public void An_image_has_its_final_height_from_the_recorded_size_capped_at_four_thirds(int width, int height, double shownWidth, double expected)
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1, Width: width, Height: height));

            Assert.Equal(expected, Card(Messages(builder), 1).HeightFor(shownWidth));
        }

        [Fact]
        public void An_image_without_a_recorded_size_is_square()
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1));

            Assert.Equal(240, Card(Messages(builder), 1).HeightFor(240));
        }

        // ---------------------------------------------------------------------------------------
        // MIME types.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("a.jpg", "image/jpeg")]
        [InlineData("a.JPEG", "image/jpeg")]
        [InlineData("a.png", "image/png")]
        [InlineData("a.webp", "image/webp")]
        [InlineData("a.heic", "image/heic")]
        [InlineData("a.mp4", "video/mp4")]
        [InlineData("a.3gp", "video/3gpp")]
        [InlineData("a.opus", "audio/ogg")]
        [InlineData("a.m4a", "audio/mp4")]
        [InlineData("a.mp3", "audio/mpeg")]
        [InlineData("a.pdf", "application/pdf")]
        [InlineData("a.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
        [InlineData("a.vcf", "text/x-vcard")]
        [InlineData("a.txt", "text/plain")]
        [InlineData("a.xyz", "application/octet-stream")]
        [InlineData("noextension", "application/octet-stream")]
        [InlineData("", "application/octet-stream")]
        [InlineData(null, "application/octet-stream")]
        public void The_mime_type_comes_from_the_extension(string? fileName, string expected)
        {
            Assert.Equal(expected, MediaMimeTypes.FromFileName(fileName));
        }

        [Fact]
        public void Every_extension_the_desktop_classifies_has_a_specific_mime_type()
        {
            // The extensions of Data's MediaClassification table (images, video, audio, documents).
            string[] classified =
            [
                ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".heif", ".svg", ".eps",
                ".mp4", ".3gp", ".mkv", ".mov", ".webm",
                ".opus", ".ogg", ".mp3", ".m4a", ".aac", ".wav", ".amr", ".flac",
                ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv", ".rtf", ".zip",
                ".vcf", ".7z", ".rar", ".json", ".md", ".sql",
            ];

            Assert.All(classified, extension => Assert.NotEqual(MediaMimeTypes.Fallback, MediaMimeTypes.FromFileName("x" + extension)));
            Assert.Equal(classified.Order(), MediaMimeTypes.KnownExtensions.Order());
        }

        // ---------------------------------------------------------------------------------------
        // Low-quality copies.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void A_low_quality_copy_of_a_restored_photo_is_hidden_with_the_note_on_the_full_copy()
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1));
            builder.Media(new AttachmentSpec(2, LowQualityOf: 1));

            var messages = Messages(builder);
            var full = Card(messages, 1);

            Assert.True(full.IsShown);
            Assert.Equal(1, full.HiddenCopies);
            Assert.Equal("WhatsApp also kept a smaller duplicate (hidden)", full.LowQualityNote);
            Assert.Equal(string.Empty, full.LowQualityLabel);

            // The copy's bubble had nothing else, so it is not shown at all.
            Assert.Single(messages);
        }

        [Fact]
        public void Two_hidden_copies_are_counted_in_the_note()
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1));
            builder.Media(new AttachmentSpec(2, LowQualityOf: 1), new AttachmentSpec(3, LowQualityOf: 1));

            Assert.Equal("WhatsApp also kept 2 smaller duplicates (hidden)", Card(Messages(builder), 1).LowQualityNote);
        }

        [Theory]
        [InlineData(ArchiveAttachmentState.Missing)]
        [InlineData(ArchiveAttachmentState.Unmatched)]
        public void A_restored_copy_whose_full_copy_is_not_restored_is_shown_and_labelled(ArchiveAttachmentState fullState)
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1, fullState));
            builder.Media(new AttachmentSpec(2, LowQualityOf: 1));

            var messages = Messages(builder);
            var copy = Card(messages, 2);

            Assert.Equal(2, messages.Count);
            Assert.True(copy.IsShown);
            Assert.Equal("Low-quality copy", copy.LowQualityLabel);
            Assert.Equal(AttachmentCardKind.Image, copy.Kind);
            Assert.Equal(0, Card(messages, 1).HiddenCopies);
            Assert.Equal(string.Empty, Card(messages, 1).LowQualityNote);
        }

        [Fact]
        public void A_copy_whose_full_copy_is_outside_the_conversation_has_no_link_and_shows_as_an_ordinary_attachment()
        {
            // Format v1 writes such a link as null, so nothing marks it as a copy.
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(2, LowQualityOf: null));

            var copy = Card(Messages(builder), 2);

            Assert.True(copy.IsShown);
            Assert.False(copy.IsLowQualityCopy);
            Assert.Equal(string.Empty, copy.LowQualityLabel);
        }

        [Fact]
        public void An_unrestored_copy_is_hidden_whatever_its_full_copy_is()
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1, ArchiveAttachmentState.Missing));
            builder.Media(new AttachmentSpec(2, ArchiveAttachmentState.Missing, LowQualityOf: 1));

            var messages = Messages(builder);

            Assert.Single(messages);
            Assert.Equal(1, Card(messages, 1).HiddenCopies);
        }

        [Fact]
        public void A_hidden_bubble_keeps_its_days_separator()
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1));
            builder.Add("<Media omitted>", ArchiveBuilder.Other, new DateTime(2025, 3, 2, 9, 0, 0), null, null, new AttachmentSpec(2, LowQualityOf: 1));
            builder.Add("Next day", ArchiveBuilder.Other, new DateTime(2025, 3, 3, 9, 0, 0));

            var rows = TestArchives.Describe(Rows(builder));

            Assert.Equal(["D 2025-03-01", "M 1", "D 2025-03-02", "D 2025-03-03", "M 3"], rows);
        }

        [Fact]
        public void A_bubble_with_text_stays_when_its_only_attachment_is_a_hidden_copy()
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1));
            builder.Add("Caption", ArchiveBuilder.Other, null, null, null, new AttachmentSpec(2, LowQualityOf: 1));

            var messages = Messages(builder);
            var captioned = messages[1];

            Assert.Equal(2, messages.Count);
            Assert.Equal("Caption", captioned.Text);
            Assert.Single(captioned.Attachments);
            Assert.Empty(captioned.ShownAttachments);
        }

        [Fact]
        public void A_copy_and_its_full_copy_on_different_pages_are_still_matched()
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1, LowQualityOf: 2));

            for (var i = 0; i < 400; i++)
            {
                builder.Add("Filler " + i);
            }

            builder.Media(new AttachmentSpec(2));
            builder.Write(_folder.Path);

            using var reader = ArchiveReader.Open(_folder.Path);
            var timeline = new ConversationTimeline(reader, ArchiveBuilder.Local);
            timeline.OpenAtLatest();

            Assert.Equal(1, Card(timeline.Rows.OfType<MessageRow>().ToList(), 2).HiddenCopies);

            while (timeline.ReadEarlier() is { } page)
            {
                timeline.Prepend(page);
            }

            Assert.Equal(-1, timeline.IndexOfMessage(1));
            Assert.Equal(1, timeline.IndexOfMessageAtOrAfter(1));
        }

        [Fact]
        public void Paging_with_hidden_bubbles_at_the_joins_gives_the_rows_of_the_whole_conversation()
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1));

            for (var s = 2; s <= 500; s++)
            {
                var at = TestArchives.OnDay(s / 37 + 1, s);

                if (s % 50 == 0 || s % 50 == 1)
                {
                    // Hidden bubbles right at and around the page joins (350/351, 200/201, 50/51).
                    builder.Add("<Media omitted>", ArchiveBuilder.Other, at, null, null, new AttachmentSpec(10_000 + s, LowQualityOf: 1));
                }
                else
                {
                    builder.Add("Message " + s, ArchiveBuilder.Other, at);
                }
            }

            builder.Write(_folder.Path);

            using var reader = ArchiveReader.Open(_folder.Path);
            var timeline = new ConversationTimeline(reader, ArchiveBuilder.Local);
            timeline.OpenAtLatest();

            while (timeline.ReadEarlier() is { } page)
            {
                timeline.Prepend(page);
            }

            var pages = reader.ReadAllPages().ToList();
            var whole = timeline.BuildRows(
                pages.SelectMany(page => page.Messages).ToList(),
                pages.SelectMany(page => page.Media).DistinctBy(pair => pair.Key).ToDictionary(),
                previousDay: null);

            Assert.Equal(TestArchives.Describe(whole), TestArchives.Describe(timeline.Rows));
            Assert.Equal(500 - 19, timeline.Rows.OfType<MessageRow>().Count());

            // Every day with any message keeps exactly one separator.
            var days = pages.SelectMany(page => page.Messages).Select(m => DateOnly.FromDateTime(m.DateTimeLocal)).Distinct().Count();
            Assert.Equal(days, timeline.Rows.OfType<DateSeparatorRow>().Count());
        }

        // ---------------------------------------------------------------------------------------
        // Links.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Addresses_become_tappable_spans_and_the_text_survives_intact()
        {
            const string text = "See https://example.org/a_(b). Or www.example.com, mail sam@example.net!\nThanks";

            var spans = LinkScanner.Scan(text);

            Assert.Equal(text, string.Concat(spans.Select(span => span.Text)));
            Assert.Equal(
                [
                    (TextSpanKind.WebLink, "https://example.org/a_(b)", "https://example.org/a_(b)"),
                    (TextSpanKind.WebLink, "www.example.com", "https://www.example.com"),
                    (TextSpanKind.EmailAddress, "sam@example.net", "mailto:sam@example.net"),
                ],
                spans.Where(span => span.Kind != TextSpanKind.Text).Select(span => (span.Kind, span.Text, span.Target!)));
        }

        [Theory]
        [InlineData("")]
        [InlineData("No address here")]
        [InlineData("http://.")]
        [InlineData("ftp://example.org")]
        [InlineData("+27 82 000 0000")]
        public void Text_without_a_followable_address_has_no_link(string text)
        {
            Assert.All(LinkScanner.Scan(text), span => Assert.Equal(TextSpanKind.Text, span.Kind));
        }

        [Fact]
        public void A_message_row_carries_its_spans()
        {
            var builder = new ArchiveBuilder();
            builder.Add("Read www.example.org today");

            var message = Assert.Single(Messages(builder));

            Assert.True(message.HasLinks);
            Assert.Equal(3, message.TextSpans.Count);
        }
    }
}
