using System.Globalization;
using Chatcheology.Archive;

namespace Chatcheology.Mobile.Conversation
{
    /// <summary>One row of the conversation list: a date separator or a message.</summary>
    /// <remarks>
    /// The separator is a row of its own rather than a header inside the first message of a day. When
    /// an earlier page is added above, the separator of the message that used to be first may have to
    /// go; as a row of its own it moves without changing the height of any message, so the message
    /// the reader is looking at stays exactly where it was.
    /// </remarks>
    public abstract class ConversationRow
    {
        private protected ConversationRow(DateOnly day)
        {
            Day = day;
        }

        /// <summary>The local calendar day the row belongs to.</summary>
        public DateOnly Day { get; }
    }

    /// <summary>The start of a calendar day.</summary>
    public sealed class DateSeparatorRow : ConversationRow
    {
        internal DateSeparatorRow(DateOnly day)
            : base(day)
        {
            Text = day.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
        }

        /// <summary>The day as the separator shows it.</summary>
        public string Text { get; }
    }

    /// <summary>One message as a bubble.</summary>
    public sealed class MessageRow : ConversationRow
    {
        internal MessageRow(ArchiveMessage message, string senderText, bool isMine, IReadOnlyList<AttachmentCard> attachments)
            : base(DateOnly.FromDateTime(message.DateTimeLocal))
        {
            SequenceNumber = message.SequenceNumber;
            IsSystem = message.Kind == ArchiveMessageKind.System;
            IsMine = isMine && !IsSystem;
            SenderText = senderText;
            TimeText = message.DateTimeLocal.ToString("HH:mm", CultureInfo.CurrentCulture);

            // The export's marker for an omitted item, not something the sender wrote: the media card
            // stands in its place, exactly as the desktop does it (exact match only).
            Text = string.Equals(message.Text, MediaPlaceholderContent, StringComparison.Ordinal)
                ? string.Empty
                : message.Text;

            Attachments = attachments;
        }

        /// <summary>
        /// The export's placeholder for an omitted media item. The same text as
        /// <c>ParsedMessage.MediaPlaceholderContent</c> in Core, which this library does not reference.
        /// </summary>
        public const string MediaPlaceholderContent = "<Media omitted>";

        /// <summary>Its position in the chat order.</summary>
        public int SequenceNumber { get; }

        /// <summary>Whether the chat system wrote it; shown centred and muted.</summary>
        public bool IsSystem { get; }

        /// <summary>Whether "me" sent it; shown on the right.</summary>
        public bool IsMine { get; }

        /// <summary>The sender's display name, or a neutral word for a system message.</summary>
        public string SenderText { get; }

        /// <summary>The local wall-clock time, to the minute.</summary>
        public string TimeText { get; }

        /// <summary>The message text, empty when it is only the media placeholder.</summary>
        public string Text { get; }

        /// <summary>Whether there is any text to show.</summary>
        public bool HasText => Text.Length > 0;

        /// <summary>The message's attachment cards, in ordinal order.</summary>
        public IReadOnlyList<AttachmentCard> Attachments { get; }
    }

    /// <summary>What an attachment card shows.</summary>
    public enum AttachmentCardKind
    {
        /// <summary>A restored image, shown inline.</summary>
        Image,

        /// <summary>A restored file of another kind, shown as a card.</summary>
        File,

        /// <summary>Reviewed as missing.</summary>
        Missing,

        /// <summary>Not yet matched. Never implies reviewed.</summary>
        Unmatched,
    }

    /// <summary>One attachment of a message, as its card shows it.</summary>
    public sealed class AttachmentCard
    {
        internal AttachmentCard(ArchiveAttachment attachment, ArchiveMedia? media, string? fullPath)
        {
            Kind = attachment.State switch
            {
                ArchiveAttachmentState.Missing => AttachmentCardKind.Missing,
                ArchiveAttachmentState.Unmatched => AttachmentCardKind.Unmatched,
                _ when string.Equals(media?.MediaType, "Image", StringComparison.Ordinal) => AttachmentCardKind.Image,
                _ => AttachmentCardKind.File,
            };

            Heading = Kind switch
            {
                AttachmentCardKind.Missing => "Media missing",
                AttachmentCardKind.Unmatched => "Media not yet matched",
                AttachmentCardKind.Image => "Photo",
                _ => media?.MediaType ?? "File",
            };

            FullPath = fullPath;
            Width = media?.Width;
            Height = media?.Height;
        }

        /// <summary>Which card it is.</summary>
        public AttachmentCardKind Kind { get; }

        /// <summary>The card's heading.</summary>
        public string Heading { get; }

        /// <summary>The media file's full path, or <see langword="null"/> when not restored.</summary>
        public string? FullPath { get; }

        /// <summary>The recorded pixel width, or <see langword="null"/>.</summary>
        public int? Width { get; }

        /// <summary>The recorded pixel height, or <see langword="null"/>.</summary>
        public int? Height { get; }

        /// <summary>
        /// The height to show the image at for a given width, from the recorded size, so a row has
        /// its final height before the image is decoded and the list does not move when it arrives.
        /// A square when no size is recorded.
        /// </summary>
        public double HeightFor(double width) =>
            Width is int w and > 0 && Height is int h and > 0 ? Math.Round(width * h / w) : width;
    }
}
