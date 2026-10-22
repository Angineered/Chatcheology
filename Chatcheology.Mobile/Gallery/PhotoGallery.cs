using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Mobile.Gallery
{
    /// <summary>The photos of one month in the gallery, under a heading such as "March 2023".</summary>
    /// <param name="Year">The year.</param>
    /// <param name="Month">The month, 1 to 12.</param>
    /// <param name="Title">The heading.</param>
    /// <param name="Photos">Its photos, in gallery order.</param>
    public sealed record GalleryMonth(int Year, int Month, string Title, IReadOnlyList<AttachmentCard> Photos);

    /// <summary>One row of the gallery's list: a month heading, or up to a row's worth of photos.</summary>
    public abstract class GalleryRow
    {
        private protected GalleryRow()
        {
        }
    }

    /// <summary>A month heading.</summary>
    public sealed class GalleryHeaderRow : GalleryRow
    {
        internal GalleryHeaderRow(string title)
        {
            Title = title;
        }

        /// <summary>"March 2023".</summary>
        public string Title { get; }
    }

    /// <summary>A row of photos of one month, left to right in gallery order.</summary>
    public sealed class GalleryPhotoRow : GalleryRow
    {
        internal GalleryPhotoRow(IReadOnlyList<AttachmentCard> photos, int firstIndex)
        {
            Photos = photos;
            FirstIndex = firstIndex;
        }

        /// <summary>The photos, at most one row's worth.</summary>
        public IReadOnlyList<AttachmentCard> Photos { get; }

        /// <summary>The index of the first photo in <see cref="PhotoGallery.Photos"/>, for the viewer.</summary>
        public int FirstIndex { get; }
    }

    /// <summary>
    /// Every restored photo of the conversation, newest first, grouped by month (CH-37).
    /// </summary>
    /// <remarks>
    /// <para><strong>The chat's rules.</strong> Each photo is the chat's own card
    /// (<see cref="ConversationTimeline.BuildMessageRow"/>): only a Restored image whose file is there
    /// (<see cref="AttachmentCardKind.Image"/>); a hidden low-quality copy is left out, a shown one keeps
    /// its label. A file that will not decode is still listed; its cell shows "Can't preview", as the chat
    /// does. Missing, Unmatched and unavailable media are not photos, so they are not in the gallery.</para>
    /// <para><strong>Order.</strong> The exact reverse of chat order: the last message first, and within a
    /// message the last attachment first. Chat order decides, never the stored times, so a month whose
    /// photos are interrupted by another month's in chat order gets a second heading rather than being
    /// re-sorted.</para>
    /// <para>Built once, in the background, from the whole conversation; kept in memory for the session.</para>
    /// </remarks>
    public sealed class PhotoGallery
    {
        private PhotoGallery(IReadOnlyList<AttachmentCard> photos, IReadOnlyList<GalleryMonth> months)
        {
            Photos = photos;
            Months = months;
        }

        /// <summary>A gallery with no photos.</summary>
        public static PhotoGallery Empty { get; } = new([], []);

        /// <summary>Every photo, in gallery order: the viewer's next and previous.</summary>
        public IReadOnlyList<AttachmentCard> Photos { get; }

        /// <summary>The photos grouped by month, in gallery order.</summary>
        public IReadOnlyList<GalleryMonth> Months { get; }

        /// <summary>Whether there are no photos.</summary>
        public bool IsEmpty => Photos.Count == 0;

        /// <summary>
        /// Builds the gallery from every page of the conversation, with <paramref name="timeline"/>'s rules
        /// for the cards (its reader is used for the media paths only; nothing is loaded into it).
        /// </summary>
        public static PhotoGallery Build(
            IEnumerable<ArchivePage> pages, ConversationTimeline timeline, CultureInfo? culture = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(pages);
            ArgumentNullException.ThrowIfNull(timeline);

            var inChatOrder = new List<AttachmentCard>();

            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var message in page.Messages)
                {
                    if (message.Attachments.Count == 0)
                    {
                        continue;
                    }

                    var row = timeline.BuildMessageRow(message, page.Media);
                    inChatOrder.AddRange(row.ShownAttachments.Where(card => card.Kind == AttachmentCardKind.Image));
                }
            }

            inChatOrder.Reverse();

            return new PhotoGallery(inChatOrder, Group(inChatOrder, culture ?? CultureInfo.CurrentCulture));
        }

        /// <summary>Builds the gallery by reading the whole conversation through <paramref name="reader"/>.</summary>
        public static PhotoGallery Build(
            ArchiveReader reader, long localParticipantID, LowQualityIndex lowQuality, Stars? stars, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(reader);

            return Build(reader.ReadAllPages(), new ConversationTimeline(reader, localParticipantID, lowQuality, stars), null, cancellationToken);
        }

        /// <summary>The index of the photo of attachment <paramref name="attachmentID"/>, or -1.</summary>
        public int IndexOf(long attachmentID)
        {
            for (var i = 0; i < Photos.Count; i++)
            {
                if (Photos[i].AttachmentID == attachmentID)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// The list's rows: each month's heading, then its photos <paramref name="columns"/> to a row (the
        /// last row of a month may be shorter).
        /// </summary>
        public IReadOnlyList<GalleryRow> Rows(int columns)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);

            var rows = new List<GalleryRow>();
            var index = 0;

            foreach (var month in Months)
            {
                rows.Add(new GalleryHeaderRow(month.Title));

                for (var i = 0; i < month.Photos.Count; i += columns)
                {
                    rows.Add(new GalleryPhotoRow(month.Photos.Skip(i).Take(columns).ToList(), index + i));
                }

                index += month.Photos.Count;
            }

            return rows;
        }

        /// <summary>Sets every photo's star flag from <paramref name="stars"/> (after a star is toggled).</summary>
        public void RefreshStars(Stars stars)
        {
            ArgumentNullException.ThrowIfNull(stars);

            foreach (var photo in Photos)
            {
                stars.Apply(photo);
            }
        }

        private static List<GalleryMonth> Group(IReadOnlyList<AttachmentCard> photos, CultureInfo culture)
        {
            var months = new List<GalleryMonth>();
            var run = new List<AttachmentCard>();
            (int Year, int Month)? current = null;

            foreach (var photo in photos)
            {
                var key = (photo.MessageTimeLocal.Year, photo.MessageTimeLocal.Month);

                if (current is { } open && open != key)
                {
                    months.Add(Month(open, run, culture));
                    run = [];
                }

                current = key;
                run.Add(photo);
            }

            if (current is { } last)
            {
                months.Add(Month(last, run, culture));
            }

            return months;
        }

        private static GalleryMonth Month((int Year, int Month) key, List<AttachmentCard> photos, CultureInfo culture) =>
            new(key.Year, key.Month, new DateOnly(key.Year, key.Month, 1).ToString("MMMM yyyy", culture), photos);
    }

    /// <summary>
    /// Where the full-screen viewer is in a list of photos (CH-37): a swipe moves to the next or previous
    /// photo in that list's order, and stops at either end.
    /// </summary>
    public sealed class PhotoBrowser
    {
        /// <summary>A browser over <paramref name="photos"/>, at <paramref name="index"/>.</summary>
        public PhotoBrowser(IReadOnlyList<AttachmentCard> photos, int index)
        {
            ArgumentNullException.ThrowIfNull(photos);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, photos.Count);

            Photos = photos;
            Index = index;
        }

        /// <summary>The photos, in the order a swipe moves through them.</summary>
        public IReadOnlyList<AttachmentCard> Photos { get; }

        /// <summary>The index of the photo shown.</summary>
        public int Index { get; private set; }

        /// <summary>The photo shown.</summary>
        public AttachmentCard Current => Photos[Index];

        /// <summary>Whether a swipe to the left has a photo to show.</summary>
        public bool HasNext => Index < Photos.Count - 1;

        /// <summary>Whether a swipe to the right has a photo to show.</summary>
        public bool HasPrevious => Index > 0;

        /// <summary>Moves to the next photo. False, and nothing changes, at the end.</summary>
        public bool Next()
        {
            if (!HasNext)
            {
                return false;
            }

            Index++;
            return true;
        }

        /// <summary>Moves to the previous photo. False, and nothing changes, at the start.</summary>
        public bool Previous()
        {
            if (!HasPrevious)
            {
                return false;
            }

            Index--;
            return true;
        }
    }
}
