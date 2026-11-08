using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Gallery;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// The photo gallery (CH-37): only Restored images, the chat's low-quality rule, newest first, month
    /// headings in chat order, rows of three; and the viewer's next and previous.
    /// </summary>
    public sealed class GalleryAndViewerTests : IDisposable
    {
        private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

        private readonly TemporaryFolder _folder = new();

        public void Dispose() => _folder.Dispose();

        private PhotoGallery Gallery(ArchiveBuilder builder, Action<string>? afterWrite = null)
        {
            builder.Write(_folder.Path);
            afterWrite?.Invoke(_folder.Path);

            using var reader = ArchiveReader.Open(_folder.Path);
            var timeline = new ConversationTimeline(reader, ArchiveBuilder.Local);

            var gallery = PhotoGallery.Build(reader.ReadAllPages(), timeline, English);

            // CH-66: the batched load, newest first in batches of one photo or more, gives the same gallery.
            var batched = PhotoGallery.FromBatches(
                PhotoGalleryLoad.Batches(PhotoGalleryLoad.ReadPagesNewestFirst(reader), timeline, English, 1, 1));
            GalleryLoadTests.AssertSameGallery(gallery, batched);

            return gallery;
        }

        private static List<long> Ids(PhotoGallery gallery) => gallery.Photos.Select(photo => photo.AttachmentID).ToList();

        [Fact]
        public void Only_restored_images_whose_file_is_there_are_in_the_gallery()
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1));
            builder.Media(new AttachmentSpec(2, State: ArchiveAttachmentState.Missing));
            builder.Media(new AttachmentSpec(3, State: ArchiveAttachmentState.Unmatched));
            builder.Media(new AttachmentSpec(4, MediaType: "Video", Extension: ".mp4"));
            builder.Media(new AttachmentSpec(5, MediaType: "Audio", Extension: ".opus"));
            builder.Media(new AttachmentSpec(6, MediaType: "Unknown", Extension: ".bin"));
            builder.Media(new AttachmentSpec(7));
            builder.Add("Just text");

            var gallery = Gallery(builder, folder =>
            {
                // Attachment 7's file is gone: "Media file unavailable" in the chat, not a photo here.
                var sha = ArchiveHashing.Hex(System.Security.Cryptography.SHA256.HashData("synthetic media payload 7"u8.ToArray()));
                File.Delete(Path.Combine(folder, ArchiveFormat.MediaFolderName, ArchiveFormat.MediaFileName(sha, "FILE-7.jpg")));
            });

            Assert.Equal([1L], Ids(gallery));
            Assert.All(gallery.Photos, photo => Assert.Equal(AttachmentCardKind.Image, photo.Kind));
        }

        [Fact]
        public void A_photo_that_will_not_decode_is_still_listed_so_its_cell_can_say_cannot_preview()
        {
            // The builder's bytes are no picture at all: the card is an Image all the same, as in the chat.
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1, Extension: ".jpg"));

            var photo = Assert.Single(Gallery(builder).Photos);

            Assert.NotNull(photo.FullPath);
            Assert.Equal(AttachmentCardKind.Image, photo.Kind);
        }

        [Fact]
        public void Photos_are_newest_first_in_the_exact_reverse_of_chat_order()
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1));
            builder.Media(new AttachmentSpec(2), new AttachmentSpec(3));
            builder.Add("Between");
            builder.Media(new AttachmentSpec(4));

            var gallery = Gallery(builder);

            Assert.Equal([4L, 3L, 2L, 1L], Ids(gallery));
            Assert.Equal([4, 2, 2, 1], gallery.Photos.Select(photo => photo.SequenceNumber).ToList());
        }

        [Fact]
        public void A_hidden_low_quality_copy_is_left_out_and_a_shown_one_keeps_its_label()
        {
            var builder = new ArchiveBuilder();

            // 1 is restored with its copy 2 (hidden); 3 is Missing, so its copy 4 is the only one (shown).
            builder.Media(new AttachmentSpec(1), new AttachmentSpec(2, LowQualityOf: 1));
            builder.Media(new AttachmentSpec(3, State: ArchiveAttachmentState.Missing), new AttachmentSpec(4, LowQualityOf: 3));

            var gallery = Gallery(builder);

            Assert.Equal([4L, 1L], Ids(gallery));
            Assert.Equal(AttachmentCard.LowQualityCopyLabel, gallery.Photos[0].LowQualityLabel);
            Assert.Equal(string.Empty, gallery.Photos[1].LowQualityLabel);
            Assert.Equal(1, gallery.Photos[1].HiddenCopies);
        }

        [Fact]
        public void Photos_are_grouped_under_month_headings_newest_month_first()
        {
            var builder = new ArchiveBuilder();
            builder.Add("Jan", ArchiveBuilder.Other, new DateTime(2023, 1, 30, 9, 0, 0), null, null, new AttachmentSpec(1));
            builder.Add("Mar", ArchiveBuilder.Other, new DateTime(2023, 3, 1, 9, 0, 0), null, null, new AttachmentSpec(2));
            builder.Add("Mar", ArchiveBuilder.Other, new DateTime(2023, 3, 31, 23, 59, 0), null, null, new AttachmentSpec(3));
            builder.Add("Jan next year", ArchiveBuilder.Other, new DateTime(2024, 1, 2, 9, 0, 0), null, null, new AttachmentSpec(4));

            var gallery = Gallery(builder);

            Assert.Equal(["January 2024", "March 2023", "January 2023"], gallery.Months.Select(month => month.Title).ToList());
            Assert.Equal([[4L], [3L, 2L], [1L]], gallery.Months.Select(month => month.Photos.Select(p => p.AttachmentID).ToList()).ToList());
        }

        [Fact]
        public void Months_follow_chat_order_never_the_stored_times()
        {
            // Message 2 is stamped earlier than message 1: chat order decides, so March appears twice.
            var builder = new ArchiveBuilder();
            builder.Add("a", ArchiveBuilder.Other, new DateTime(2023, 3, 5, 9, 0, 0), null, null, new AttachmentSpec(1));
            builder.Add("b", ArchiveBuilder.Other, new DateTime(2023, 2, 5, 9, 0, 0), null, null, new AttachmentSpec(2));
            builder.Add("c", ArchiveBuilder.Other, new DateTime(2023, 3, 6, 9, 0, 0), null, null, new AttachmentSpec(3));

            var gallery = Gallery(builder);

            Assert.Equal(["March 2023", "February 2023", "March 2023"], gallery.Months.Select(month => month.Title).ToList());
            Assert.Equal([3L, 2L, 1L], Ids(gallery));
        }

        [Fact]
        public void Rows_put_each_heading_above_its_photos_three_to_a_row()
        {
            var builder = new ArchiveBuilder();

            for (var i = 1; i <= 4; i++)
            {
                builder.Add("Feb", ArchiveBuilder.Other, new DateTime(2023, 2, i, 9, 0, 0), null, null, new AttachmentSpec(i));
            }

            builder.Add("Mar", ArchiveBuilder.Other, new DateTime(2023, 3, 1, 9, 0, 0), null, null, new AttachmentSpec(5));

            var rows = Gallery(builder).Rows(3);

            static string Describe(GalleryRow row) => row switch
            {
                GalleryHeaderRow header => "H " + header.Title,
                GalleryPhotoRow photos => $"P {photos.FirstIndex}: " + string.Join(",", photos.Photos.Select(p => p.AttachmentID)),
                _ => "?",
            };

            Assert.Equal(
                ["H March 2023", "P 0: 5", "H February 2023", "P 1: 4,3,2", "P 4: 1"],
                rows.Select(Describe).ToList());
        }

        [Fact]
        public void An_archive_without_photos_has_an_empty_gallery()
        {
            var builder = new ArchiveBuilder();
            builder.Add("Hello");
            builder.Media(new AttachmentSpec(1, State: ArchiveAttachmentState.Missing));

            var gallery = Gallery(builder);

            Assert.True(gallery.IsEmpty);
            Assert.Empty(gallery.Rows(3));
        }

        [Fact]
        public void IndexOf_finds_a_photo_by_attachment()
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1));
            builder.Media(new AttachmentSpec(2));

            var gallery = Gallery(builder);

            Assert.Equal(0, gallery.IndexOf(2));
            Assert.Equal(1, gallery.IndexOf(1));
            Assert.Equal(-1, gallery.IndexOf(99));
        }

        // ---------------------------------------------------------------------------------------
        // The viewer's next and previous.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void The_viewer_moves_through_the_gallery_order_and_stops_at_either_end()
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1));
            builder.Media(new AttachmentSpec(2));
            builder.Media(new AttachmentSpec(3));

            var gallery = Gallery(builder);
            var browser = new PhotoBrowser(gallery.Photos, gallery.IndexOf(2));

            Assert.Equal(2, browser.Current.AttachmentID);
            Assert.True(browser.Next());
            Assert.Equal(1, browser.Current.AttachmentID);
            Assert.False(browser.HasNext);
            Assert.False(browser.Next());
            Assert.Equal(1, browser.Current.AttachmentID);

            Assert.True(browser.Previous());
            Assert.True(browser.Previous());
            Assert.Equal(3, browser.Current.AttachmentID);
            Assert.False(browser.HasPrevious);
            Assert.False(browser.Previous());
            Assert.Equal(0, browser.Index);
        }

        [Fact]
        public void A_viewer_of_one_photo_has_no_neighbours()
        {
            var builder = new ArchiveBuilder();
            builder.Media(new AttachmentSpec(1));

            var browser = new PhotoBrowser(Gallery(builder).Photos, 0);

            Assert.False(browser.HasNext);
            Assert.False(browser.HasPrevious);
        }

        [Fact]
        public void A_viewer_index_outside_the_list_is_refused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PhotoBrowser([], 0));
        }
    }
}
