using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Synthetic;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// The synthetic archive the scrolling trial and the Debug app use: a valid archive of the current format
    /// with everything the conversation screen has to show.
    /// </summary>
    public sealed class SyntheticArchiveTests
    {
        private static readonly SyntheticArchiveOptions Options = new();

        [Fact]
        public void The_synthetic_archive_passes_every_validator_check()
        {
            using var folder = new TemporaryFolder();
            SyntheticArchive.Write(folder.Path, Options);

            var result = ArchiveValidator.Validate(folder.Path);

            Assert.True(result.Passed);
            Assert.All(result.Checks, check => Assert.True(check.Passed));
        }

        [Fact]
        public void Every_Debug_install_writes_version_four_with_its_yearly_memories_and_captions_and_validates()
        {
            foreach (var options in new[] { SyntheticDebugInstalls.Standard, SyntheticDebugInstalls.MidDay, SyntheticDebugInstalls.WithMemories })
            {
                using var folder = new TemporaryFolder();
                var counts = SyntheticArchive.Write(folder.Path, options);

                Assert.Equal(0, counts.NotedDates);
                var validation = ArchiveValidator.Validate(folder.Path);
                Assert.True(validation.Passed, string.Join(", ", validation.FirstProblems));

                using var reader = ArchiveReader.Open(folder.Path);
                Assert.Equal(ArchiveFormat.FormatVersion, reader.Info.FormatVersion);
                Assert.Equal(4, reader.Info.FormatVersion);
                Assert.False(reader.HasNotedDates);

                // CH-71: the invented archive id, and a few captioned pictures (at least one with a link).
                Assert.Equal(SyntheticArchive.ArchiveId, reader.Info.ArchiveId);
                Assert.Equal(options.Captions, counts.Captions);
                Assert.InRange(counts.Captions, 3, 10);

                var captions = reader.ReadAllPages()
                    .SelectMany(p => p.Messages)
                    .SelectMany(m => m.Attachments)
                    .Select(a => a.Caption)
                    .OfType<string>()
                    .ToList();

                Assert.Equal(options.Captions, captions.Count);
                Assert.Contains(captions, c => c.Contains("www.example.org", StringComparison.Ordinal));
                Assert.Contains(captions, c => c.Contains('\n'));

                // CH-53: at least two yearly memories, one dated in the future and one with a gallery link.
                var yearly = reader.ReadYearlyEntries();
                Assert.Equal(["Our synthetic anniversary", "Invented leap day", "Invented future day"], yearly.Select(e => e.Title).ToList());
                Assert.All(yearly, e => Assert.Equal(ArchiveMemoryVocabulary.KindPersonal, e.CollectionKind));
                Assert.All(yearly, e => Assert.Equal("Day", e.DatePrecision));
                Assert.Contains(yearly, e => DateOnly.FromDateTime(e.StartLocal) > new DateOnly(2026, 11, 2));
                Assert.Equal(SyntheticArchive.FutureYearlyDay, DateOnly.FromDateTime(yearly[2].StartLocal));
                Assert.Single(reader.ReadExternalLinks(ExternalLinkOwnerKind.Entry, yearly[0].EntryID));
            }

            Assert.True(SyntheticDebugInstalls.WithMemories.Memories);
        }

        [Fact]
        public void The_noted_dates_fixture_is_version_two_with_gifted_noted_dates_and_validates()
        {
            using var folder = new TemporaryFolder();
            var counts = SyntheticArchive.Write(folder.Path, Options with { NotedDates = true });

            Assert.Equal(3, counts.NotedDates);
            Assert.True(ArchiveValidator.Validate(folder.Path).Passed);

            using var reader = ArchiveReader.Open(folder.Path);
            Assert.Equal(2, reader.Info.FormatVersion);
            Assert.True(reader.HasNotedDates);
            Assert.Empty(reader.ReadYearlyEntries());
            Assert.Equal(["Our synthetic anniversary", "Invented birthday", "Invented leap day"], reader.ReadNotedDates().Select(d => d.Title).ToList());

            Assert.Throws<ArgumentException>(() => SyntheticArchive.Write(Path.Combine(folder.Path, "x"), Options with { NotedDates = true, YearlyMemories = true }));
        }

        [Fact]
        public void The_synthetic_archive_holds_what_its_options_ask_for()
        {
            using var folder = new TemporaryFolder();
            var counts = SyntheticArchive.Write(folder.Path, Options);

            Assert.Equal(new ArchiveCounts(2, 3000, 46, 40, 3, 3, 40) { WorkspaceMessages = 3000 }, counts);

            using var reader = ArchiveReader.Open(folder.Path);
            var days = reader.ReadDays();

            Assert.Equal(Options.Days, days.Count);
            Assert.Contains(days.Zip(days.Skip(1)), pair => pair.Second.DayNumber - pair.First.DayNumber > 1);
        }

        [Fact]
        public void With_memories_it_holds_every_kind_of_memory_row_and_validates()
        {
            using var folder = new TemporaryFolder();
            var counts = SyntheticArchive.Write(folder.Path, Options with { Memories = true });

            var validation = ArchiveValidator.Validate(folder.Path);
            Assert.True(validation.Passed, string.Join(", ", validation.FirstProblems));
            Assert.Equal(5, counts.ItemDefinitions);
            Assert.Equal(2, counts.EntryLinks);

            using var reader = ArchiveReader.Open(folder.Path);

            var images = reader.ReadEntryImages(10);
            Assert.Equal([1, 2, 3], images.Select(i => i.Ordinal));
            Assert.Equal(["Invented ferry", null, "Invented sunset over an invented sea"], images.Select(i => i.Caption));
            Assert.All(images, image => Assert.True(File.Exists(reader.EntryImageFullPath(image))));
            Assert.Equal(3, counts.EntryImages);

            var memories = Memories.MemoryIndex.Build(reader);
            Assert.Equal(["Shared gaming", "Holidays"], memories.Collections.Select(c => c.Name));
            Assert.Equal([false, true], memories.Collections.Select(c => c.ShownByDefault));

            var timeline = new ConversationTimeline(reader, SyntheticArchive.Local, memories: memories.Layout(new Memories.MemoryView([1, 2])));
            timeline.OpenAt(1);

            while (timeline.PrepareLater() is { } later)
            {
                timeline.Append(later);
            }

            var rows = timeline.Rows;
            var cards = rows.OfType<PeriodCardRow>().ToList();

            Assert.Equal(
                ["This period: same period", "This period: same period", "This period: strong", "This period: strong", "This period: supporting"],
                cards.Select(c => c.LabelText).Order());
            Assert.Single(rows.OfType<DifferentTimesRow>());
            Assert.Single(rows.OfType<EntryCardRow>(), card => card.HasImage);

            // Rows before the first message and after the last.
            Assert.IsType<PeriodCardRow>(rows.First(r => r is not DateSeparatorRow));
            Assert.IsType<PeriodCardRow>(rows[^1]);
            Assert.Equal(Enumerable.Range(1, 3000), rows.OfType<MessageRow>().Select(m => m.SequenceNumber));

            var details = memories.Details(new MemoryKey(1, 0))!;
            Assert.Equal(["First Invented Lap", "First Invented Lap", "Invented Drift"], details.Items.Select(i => i.Name));
            Assert.All(details.Items, i => Assert.True(i.HasIcon));
            Assert.True(memories.Details(new MemoryKey(1, 2))!.Items[0].IsHidden);
            Assert.Equal("Bronze trophy #5", memories.Details(new MemoryKey(1, 2))!.Items[1].Name);
            Assert.Equal(2, memories.Details(new MemoryKey(10, null))!.Links.Count);
        }

        [Fact]
        public void An_earlier_start_spreads_the_chat_over_years_with_the_same_day_in_several()
        {
            using var folder = new TemporaryFolder();
            var options = new SyntheticArchiveOptions(Messages: 2000, Days: 1000, Images: 0, Missing: 0, Unmatched: 0, Start: new DateOnly(2016, 1, 6));
            SyntheticArchive.Write(folder.Path, options);

            using var reader = ArchiveReader.Open(folder.Path);
            var days = reader.ReadDays();

            Assert.Equal(new DateOnly(2016, 1, 6), days[0]);
            Assert.True(days[^1].Year >= 2019);
            Assert.Contains(days, day => OnThisDay.Find(days, new DateOnly(2026, day.Month, day.Day)).Count >= 3);
            Assert.True(ArchiveValidator.Validate(folder.Path).Passed);
        }

        [Fact]
        public void Videos_are_written_with_the_bytes_given_each_distinct_and_valid()
        {
            using var folder = new TemporaryFolder();
            var options = new SyntheticArchiveOptions(Messages: 200, Days: 10, Images: 2, Missing: 0, Unmatched: 0, Videos: 2, VideoBytes: "not a real video, a stand-in"u8.ToArray());
            SyntheticArchive.Write(folder.Path, options);

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, SyntheticArchive.Local);
            timeline.OpenAt(1);

            while (timeline.ReadLater() is { } page)
            {
                timeline.Append(page);
            }

            var videos = timeline.Rows.OfType<MessageRow>().SelectMany(message => message.Attachments).Where(card => card.IsVideo).ToList();

            Assert.Equal(2, videos.Count);
            Assert.Equal(2, videos.Select(card => card.MediaSha256).Distinct().Count());
            Assert.All(videos, card => Assert.Equal("Video · 0:02", card.Heading));
            Assert.True(ArchiveValidator.Validate(folder.Path).Passed);
            Assert.Throws<ArgumentException>(() => SyntheticArchive.Write(Path.Combine(folder.Path, "x"), options with { VideoBytes = null }));
        }

        [Fact]
        public void The_synthetic_images_have_mixed_aspect_ratios_and_real_png_bytes()
        {
            using var folder = new TemporaryFolder();
            SyntheticArchive.Write(folder.Path, Options);

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, SyntheticArchive.Local);
            timeline.OpenAt(1);

            while (timeline.ReadLater() is { } page)
            {
                timeline.Append(page);
            }

            var images = timeline.Rows.OfType<MessageRow>()
                .SelectMany(message => message.Attachments)
                .Where(card => card.Kind == AttachmentCardKind.Image)
                .ToList();

            Assert.Equal(40, images.Count);
            Assert.Contains(images, card => card.Width > card.Height);
            Assert.Contains(images, card => card.Width < card.Height);
            Assert.Contains(images, card => card.Width == card.Height);

            var bytes = File.ReadAllBytes(images[0].FullPath!);
            Assert.Equal([0x89, 0x50, 0x4E, 0x47], bytes[..4]);
        }

        [Fact]
        public void The_rich_synthetic_archive_has_every_card_and_label_the_screens_show_and_is_valid()
        {
            var options = new SyntheticArchiveOptions(
                Messages: 1200, Days: 40, Images: 8, Missing: 2, Unmatched: 2,
                Audio: 2, Documents: 1, BrokenImages: 1, LowQualityCopies: 3, Forwarded: 12, Links: 6);

            using var folder = new TemporaryFolder();
            SyntheticArchive.Write(folder.Path, options);

            Assert.True(ArchiveValidator.Validate(folder.Path).Passed);

            using var reader = ArchiveReader.Open(folder.Path);
            var timeline = new ConversationTimeline(reader, SyntheticArchive.Local);
            timeline.OpenAt(1);

            while (timeline.ReadLater() is { } page)
            {
                timeline.Append(page);
            }

            var messages = timeline.Rows.OfType<MessageRow>().ToList();
            var cards = messages.SelectMany(message => message.ShownAttachments).ToList();

            Assert.Equal(2, cards.Count(card => card.Kind == AttachmentCardKind.File && card.MediaType == "Audio" && card.DurationText == "0:01"));
            Assert.Equal("audio/x-wav", cards.First(card => card.MediaType == "Audio").MimeType);
            Assert.Equal("application/pdf", Assert.Single(cards, card => card.MediaType == "Document").MimeType);

            // Eight images, the undecodable one (the app finds out when it decodes it) and the shown copy.
            Assert.Equal(10, cards.Count(card => card.Kind == AttachmentCardKind.Image));

            // Two copies hidden beside their images, one shown with its label beside a Missing card.
            Assert.Equal(2, cards.Count(card => card.HiddenCopies > 0));
            var shownCopy = Assert.Single(cards, card => card.LowQualityLabel.Length > 0);
            Assert.Contains(messages, message => message.ShownAttachments.Contains(shownCopy)
                && message.ShownAttachments.Any(card => card.Kind == AttachmentCardKind.Missing));

            Assert.Contains(messages, message => message.ForwardedText == "Forwarded");
            Assert.Contains(messages, message => message.ForwardedText == "Forwarded many times");
            Assert.Contains(messages, message => message.TextSpans.Any(span => span.Target == "https://www.example.org/synthetic"));
            Assert.Contains(messages, message => message.TextSpans.Any(span => span.Target == "mailto:someone@example.org"));
        }

        [Fact]
        public void The_same_options_write_the_same_archive()
        {
            using var first = new TemporaryFolder();
            using var second = new TemporaryFolder();

            SyntheticArchive.Write(first.Path, Options);
            SyntheticArchive.Write(second.Path, Options);

            Assert.Equal(
                File.ReadAllBytes(Path.Combine(first.Path, ArchiveContents.FileName)),
                File.ReadAllBytes(Path.Combine(second.Path, ArchiveContents.FileName)));
        }
    }
}
