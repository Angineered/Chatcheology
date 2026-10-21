using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Synthetic;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// The synthetic archive the scrolling trial and the Debug app use: a valid v1 archive with
    /// everything the conversation screen has to show.
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
        public void The_synthetic_archive_holds_what_its_options_ask_for()
        {
            using var folder = new TemporaryFolder();
            var counts = SyntheticArchive.Write(folder.Path, Options);

            Assert.Equal(new ArchiveCounts(2, 3000, 46, 40, 3, 3, 40), counts);

            using var reader = ArchiveReader.Open(folder.Path);
            var days = reader.ReadDays();

            Assert.Equal(Options.Days, days.Count);
            Assert.Contains(days.Zip(days.Skip(1)), pair => pair.Second.DayNumber - pair.First.DayNumber > 1);
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
