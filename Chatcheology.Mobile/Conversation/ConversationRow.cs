using System.ComponentModel;
using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Presentation;

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
            Text = DayText(day);
        }

        /// <summary>The day as the separator shows it.</summary>
        public string Text { get; }

        /// <summary>A day as separators and search results show it.</summary>
        public static string DayText(DateOnly day) => day.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>One message as a bubble.</summary>
    /// <remarks>
    /// The desktop's rules (<c>MessageViewModel</c>), copied because this library references neither
    /// Desktop nor Data: the export's media placeholder is hidden (exact match only); "Forwarded" when
    /// the message or any attachment stores 1, "Forwarded many times" from score 5, nothing for null;
    /// a system message is never "me"; a bubble with no text whose attachments are all hidden is not
    /// shown.
    /// </remarks>
    public sealed class MessageRow : ConversationRow, INotifyPropertyChanged
    {
        private bool _isHighlighted;
        private bool _isStarred;

        /// <summary>
        /// The export's placeholder for an omitted media item. The same text as
        /// <c>ParsedMessage.MediaPlaceholderContent</c> in Core, which this library does not reference.
        /// </summary>
        public const string MediaPlaceholderContent = "<Media omitted>";

        /// <summary>
        /// The forward score from which WhatsApp labels a message "Forwarded many times"; the desktop's
        /// <c>MessageViewModel.ForwardedManyTimesScore</c>.
        /// </summary>
        public const int ForwardedManyTimesScore = 5;

        internal MessageRow(ArchiveMessage message, string senderText, bool isMine, IReadOnlyList<AttachmentCard> attachments)
            : base(DateOnly.FromDateTime(message.DateTimeLocal))
        {
            SequenceNumber = message.SequenceNumber;
            DateTimeLocal = message.DateTimeLocal;
            IsSystem = message.Kind == ArchiveMessageKind.System;
            IsMine = isMine && !IsSystem;
            SenderText = senderText;
            TimeText = TimeTextOf(message.DateTimeLocal);

            // The export's marker for an omitted item, not something the sender wrote: the media card
            // stands in its place, exactly as the desktop does it (exact match only).
            Text = IsPlaceholder(message.Text) ? string.Empty : message.Text;
            TextSpans = LinkScanner.Scan(Text);

            Attachments = attachments;
            ShownAttachments = attachments.Where(card => card.IsShown).ToList();
            ForwardedText = ForwardedLabel(message);

            // A photo starred in the viewer shows its star on the bubble too.
            foreach (var card in ShownAttachments)
            {
                card.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(AttachmentCard.IsStarred))
                    {
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsStar)));
                    }
                };
            }
        }

        /// <summary>Its position in the chat order.</summary>
        public int SequenceNumber { get; }

        /// <summary>The stored local wall-clock reading.</summary>
        public DateTime DateTimeLocal { get; }

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

        /// <summary>
        /// What Share hands to the share sheet: the message text exactly as written (links and emoji as
        /// they are), with no date and no sender. <see langword="null"/> when there is nothing to share: a
        /// message that is only the media placeholder, or a system line.
        /// </summary>
        public string? ShareText => HasText && !IsSystem ? Text : null;

        /// <summary>Whether a long press offers Share.</summary>
        public bool CanShareText => ShareText is not null;

        /// <summary>The text split into plain runs and tappable addresses; joined, it is <see cref="Text"/>.</summary>
        public IReadOnlyList<TextSpan> TextSpans { get; }

        /// <summary>Whether the text holds an address to tap.</summary>
        public bool HasLinks => TextSpans.Any(span => span.Kind != TextSpanKind.Text);

        /// <summary>Every attachment card of the message, hidden ones included, in ordinal order.</summary>
        public IReadOnlyList<AttachmentCard> Attachments { get; }

        /// <summary>The cards to draw: every attachment that is not a hidden low-quality copy.</summary>
        public IReadOnlyList<AttachmentCard> ShownAttachments { get; }

        /// <summary>"Forwarded", "Forwarded many times", or empty.</summary>
        public string ForwardedText { get; }

        /// <summary>Raised when <see cref="IsHighlighted"/> changes.</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Whether the bubble is briefly highlighted (a message opened from search). It belongs to the
        /// message, not to whichever view shows it, so recycled views and inserted pages cannot move it.
        /// </summary>
        public bool IsHighlighted
        {
            get => _isHighlighted;
            set
            {
                if (_isHighlighted != value)
                {
                    _isHighlighted = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
                }
            }
        }

        /// <summary>
        /// Whether the message itself is starred (CH-38). Like <see cref="IsHighlighted"/>, it belongs to the
        /// message, so a recycled view cannot show another message's star.
        /// </summary>
        public bool IsStarred
        {
            get => _isStarred;
            set
            {
                if (_isStarred != value)
                {
                    _isStarred = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsStarred)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsStar)));
                }
            }
        }

        /// <summary>Whether the bubble shows a star: the message is starred, or one of its shown photos is.</summary>
        public bool ShowsStar => _isStarred || ShownAttachments.Any(card => card.IsStarred);

        /// <summary>Whether a long press offers Star / Unstar: any message but a system line.</summary>
        public bool CanStar => !IsSystem;

        /// <summary>Whether a long press offers anything at all.</summary>
        public bool HasActions => CanShareText || CanStar;

        /// <summary>Whether a forwarded label applies.</summary>
        public bool IsForwarded => ForwardedText.Length > 0;

        /// <summary>
        /// Whether the bubble is shown. A message with no text of its own whose every attachment is a
        /// hidden low-quality copy has nothing left to show; its day's separator stays.
        /// </summary>
        public bool IsShown => HasText || Attachments.Count == 0 || Attachments.Any(card => card.IsShown);

        /// <summary>Whether <paramref name="text"/> is exactly the export's media placeholder.</summary>
        public static bool IsPlaceholder(string? text) =>
            string.Equals(text, MediaPlaceholderContent, StringComparison.Ordinal);

        /// <summary>A time as bubbles and search results show it.</summary>
        public static string TimeTextOf(DateTime local) => local.ToString("HH:mm", CultureInfo.CurrentCulture);

        /// <summary>
        /// The forwarded label: only a stored 1 produces one, from the message or any of its attachments
        /// (null means not known and shows nothing); "Forwarded many times" when any of those carries a
        /// score of <see cref="ForwardedManyTimesScore"/> or more.
        /// </summary>
        public static string ForwardedLabel(ArchiveMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            var scores = message.Attachments
                .Where(attachment => attachment.IsForwarded == true)
                .Select(attachment => attachment.ForwardScore)
                .ToList();

            if (message.IsForwarded == true)
            {
                scores.Add(message.ForwardScore);
            }

            return scores.Count == 0
                ? string.Empty
                : scores.Any(score => score >= ForwardedManyTimesScore) ? "Forwarded many times" : "Forwarded";
        }
    }

    /// <summary>What an attachment card shows.</summary>
    public enum AttachmentCardKind
    {
        /// <summary>A restored image, shown inline.</summary>
        Image,

        /// <summary>A restored video, audio or document: a card with its type and duration where known.</summary>
        File,

        /// <summary>Restored, but its type is not classified: "Can't preview · Open in another app".</summary>
        CannotPreview,

        /// <summary>Restored, but its file cannot be reached: "Media file unavailable". Never worded as missing.</summary>
        Unavailable,

        /// <summary>Reviewed as missing: "Media missing", heading only.</summary>
        Missing,

        /// <summary>Not yet matched: "Media not yet matched", heading only. Never implies reviewed.</summary>
        Unmatched,
    }

    /// <summary>One attachment of a message, as its card shows it.</summary>
    public sealed class AttachmentCard : INotifyPropertyChanged
    {
        /// <summary>The heading of a restored file that cannot be previewed, also used when an image fails to decode.</summary>
        public const string CannotPreviewHeading = "Can't preview · Open in another app";

        /// <summary>The label on a low-quality copy that is shown.</summary>
        public const string LowQualityCopyLabel = "Low-quality copy";

        /// <summary>The tallest an image is shown, as a multiple of the width it is shown at.</summary>
        public const double MaximumHeightRatio = 4.0 / 3.0;

        private readonly ArchiveAttachment _attachment;
        private readonly ArchiveMedia? _media;
        private readonly DateTime _messageTime;
        private readonly string? _senderName;
        private bool _isStarred;

        internal AttachmentCard(
            ArchiveAttachment attachment,
            ArchiveMedia? media,
            string? fullPath,
            bool fileExists,
            LowQualityIndex lowQuality,
            DateTime messageTime,
            string? senderName,
            int sequenceNumber)
        {
            _attachment = attachment;
            _media = media;
            _messageTime = messageTime;
            _senderName = senderName;

            AttachmentID = attachment.AttachmentID;
            SequenceNumber = sequenceNumber;
            MessageTimeLocal = messageTime;
            MediaSha256 = attachment.MediaSha256;
            DurationMS = media?.DurationMS;
            MediaType = media?.MediaType;

            Kind = attachment.State switch
            {
                ArchiveAttachmentState.Missing => AttachmentCardKind.Missing,
                ArchiveAttachmentState.Unmatched => AttachmentCardKind.Unmatched,
                _ when fullPath is null || !fileExists => AttachmentCardKind.Unavailable,
                _ => media?.MediaType switch
                {
                    "Image" => AttachmentCardKind.Image,
                    "Video" or "Audio" or "Document" => AttachmentCardKind.File,
                    _ => AttachmentCardKind.CannotPreview,
                },
            };

            DurationText = Kind == AttachmentCardKind.File && media?.DurationMS is { } duration and >= 0
                ? Duration(duration)
                : string.Empty;

            Heading = Kind switch
            {
                AttachmentCardKind.Missing => "Media missing",
                AttachmentCardKind.Unmatched => "Media not yet matched",
                AttachmentCardKind.Unavailable => "Media file unavailable",
                AttachmentCardKind.CannotPreview => CannotPreviewHeading,
                AttachmentCardKind.Image => "Photo",
                _ => DurationText.Length > 0 ? $"{media!.MediaType} · {DurationText}" : media!.MediaType,
            };

            FullPath = Kind is AttachmentCardKind.Image or AttachmentCardKind.File or AttachmentCardKind.CannotPreview ? fullPath : null;
            MimeType = FullPath is null ? null : MediaMimeTypes.FromFileName(FullPath);
            Width = media?.Width;
            Height = media?.Height;

            IsLowQualityCopy = attachment.LowQualityCopyOfAttachmentID is not null;
            IsShown = lowQuality.IsShown(attachment);
            HiddenCopies = lowQuality.HiddenCopiesOf(attachment.AttachmentID);
        }

        /// <summary>The attachment.</summary>
        public long AttachmentID { get; }

        /// <summary>Its message's sequence number, to open the chat at (Show in chat, CH-37).</summary>
        public int SequenceNumber { get; }

        /// <summary>Its message's stored local time.</summary>
        public DateTime MessageTimeLocal { get; }

        /// <summary>The media's hash, or <see langword="null"/> when not restored: the key of anything cached for it.</summary>
        public string? MediaSha256 { get; }

        /// <summary>The recorded duration in milliseconds, or <see langword="null"/>.</summary>
        public long? DurationMS { get; }

        /// <summary>Whether a restored audio file plays in the app (CH-36).</summary>
        public bool IsAudio => Kind == AttachmentCardKind.File && MediaType == "Audio";

        /// <summary>Whether a restored video shows a preview frame (CH-40).</summary>
        public bool IsVideo => Kind == AttachmentCardKind.File && MediaType == "Video";

        /// <summary>Raised when <see cref="IsStarred"/> changes.</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Whether the photo is starred (CH-38). It belongs to the card, not to a view.</summary>
        public bool IsStarred
        {
            get => _isStarred;
            set
            {
                if (_isStarred != value)
                {
                    _isStarred = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsStarred)));
                }
            }
        }

        /// <summary>Which card it is.</summary>
        public AttachmentCardKind Kind { get; }

        /// <summary>The card's heading: the state, or for a restored file its type and duration.</summary>
        public string Heading { get; }

        /// <summary>The archive's media category (Image, Video, Audio, Document, Unknown), or <see langword="null"/>.</summary>
        public string? MediaType { get; }

        /// <summary>The duration as <c>m:ss</c> (<c>h:mm:ss</c> from an hour), or empty when not known.</summary>
        public string DurationText { get; }

        /// <summary>The media file's full path when it can be shown or opened; otherwise <see langword="null"/>.</summary>
        public string? FullPath { get; }

        /// <summary>The MIME type to open the file with, from its extension; <see langword="null"/> without a file.</summary>
        public string? MimeType { get; }

        /// <summary>
        /// The photo viewer's Info lines, read from the archive's message, attachment and media rows
        /// (<see cref="MediaDetails"/>). Built when asked for, not for every card.
        /// </summary>
        public IReadOnlyList<MediaDetail> Details => MediaDetails.Of(_messageTime, _senderName, _attachment, _media);

        /// <summary>Whether a tap may open the file in another app.</summary>
        public bool CanOpen => FullPath is not null;

        /// <summary>The recorded pixel width, or <see langword="null"/>.</summary>
        public int? Width { get; }

        /// <summary>The recorded pixel height, or <see langword="null"/>.</summary>
        public int? Height { get; }

        /// <summary>Whether the archive records this attachment as WhatsApp's low-quality copy of another.</summary>
        public bool IsLowQualityCopy { get; }

        /// <summary>Whether the card is drawn. Only a low-quality copy is ever hidden.</summary>
        public bool IsShown { get; }

        /// <summary>"Low-quality copy" on a copy that is shown because its full copy is not restored; otherwise empty.</summary>
        public string LowQualityLabel => IsLowQualityCopy && IsShown ? LowQualityCopyLabel : string.Empty;

        /// <summary>How many hidden low-quality copies this attachment has.</summary>
        public int HiddenCopies { get; }

        /// <summary>The note on a card whose low-quality copies are hidden, or empty.</summary>
        public string LowQualityNote => HiddenCopies switch
        {
            0 => string.Empty,
            1 => "WhatsApp also kept a smaller duplicate (hidden)",
            _ => $"WhatsApp also kept {HiddenCopies.ToString(CultureInfo.CurrentCulture)} smaller duplicates (hidden)",
        };

        /// <summary>
        /// The height to show the image at for a given width, from the recorded size, so a row has its
        /// final height before the image is decoded and the list does not move when it arrives. A square
        /// when no size is recorded; never taller than <see cref="MaximumHeightRatio"/> times the width
        /// (a taller image is cropped to fit).
        /// </summary>
        public double HeightFor(double width)
        {
            var height = Width is int w and > 0 && Height is int h and > 0 ? width * h / w : width;

            return Math.Round(Math.Min(height, width * MaximumHeightRatio));
        }

        private static string Duration(long milliseconds)
        {
            var time = TimeSpan.FromMilliseconds(milliseconds);

            return time.TotalHours >= 1
                ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }
    }
}
