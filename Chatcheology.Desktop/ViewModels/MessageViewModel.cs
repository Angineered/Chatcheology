using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Chatcheology.Core.Models;
using Chatcheology.Core.Text;
using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Viewer;

namespace Chatcheology.Desktop.ViewModels
{
    /// <summary>
    /// One message as a bubble, with its date separator and its attachments.
    /// </summary>
    /// <remarks>
    /// <para>The date separator is computed here rather than by grouping the collection, because
    /// WPF's own grouping turns off container virtualisation — and a bounded page of a very long
    /// conversation is exactly the thing virtualisation is for.</para>
    /// <para>Which side a message sits on is a presentation claim made for this session only. Until
    /// the operator names a local participant, no message claims to be theirs, which is the honest
    /// reading: the workspace does not record who exported the conversation, and nothing here
    /// invents it.</para>
    /// </remarks>
    public sealed class MessageViewModel : ObservableObject
    {
        private bool _isFromLocalParticipant;

        internal MessageViewModel(
            ConversationMessage message,
            DateOnly? dateSeparator,
            Brush senderBrush,
            IReadOnlyList<AttachmentViewModel> attachments,
            MessageMsgstoreFact? fact = null)
        {
            SequenceNumber = message.SequenceNumber;
            SenderParticipantID = message.SenderParticipantID;
            IsSystemMessage = message.MessageType == MessageType.System;
            SenderBrush = senderBrush;
            Attachments = attachments;

            SenderText = message.SenderDisplayName ?? "System";

            TimeText = message.MessageDateTimeLocal.ToString("HH:mm", CultureInfo.CurrentCulture);

            DateSeparatorText = dateSeparator is { } date
                ? date.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)
                : string.Empty;

            HasDateSeparator = dateSeparator is not null;

            // The placeholder text is the export's marker for an omitted item, not something the
            // sender wrote, so the attachment card stands in its place rather than beneath it.
            IsPlaceholderOnly = string.Equals(
                message.MessageContent,
                ParsedMessage.MediaPlaceholderContent,
                StringComparison.Ordinal);

            MessageText = IsPlaceholderOnly ? string.Empty : message.MessageContent;
            HasMessageText = MessageText.Length > 0;

            // Scanned here, on the background thread that builds the page, rather than in a
            // converter that would run during layout for every message on screen.
            TextSpans = MessageTextScanner.Scan(MessageText);

            // WhatsApp's own label and threshold (CH-8). Only a msgstore forwarded mark produces
            // a label; a score alone, without the mark, says nothing here. The mark comes from the
            // message's attachments or, for a text message, from the message's own fact (CH-22).
            var forwardScores = attachments
                .Where(attachment => attachment.IsForwarded)
                .Select(attachment => attachment.ForwardScore)
                .ToList();

            if (fact?.IsForwarded == true)
            {
                forwardScores.Add(fact.ForwardScore);
            }

            ForwardedText = forwardScores.Count == 0
                ? string.Empty
                : forwardScores.Any(score => score >= ForwardedManyTimesScore)
                    ? "Forwarded many times"
                    : "Forwarded";
        }

        /// <summary>
        /// The forward score from which WhatsApp labels a message "Forwarded many times".
        /// </summary>
        internal const int ForwardedManyTimesScore = 5;

        /// <summary>"Forwarded", "Forwarded many times", or empty.</summary>
        public string ForwardedText { get; }

        /// <summary>Whether a forwarded label applies.</summary>
        public bool HasForwardedLabel => ForwardedText.Length > 0;

        /// <summary>
        /// Whether the bubble is on screen. A message that carries no text of its own and whose
        /// every attachment is a hidden low-quality copy has nothing left to show, so the bubble
        /// goes with them; its date separator stays.
        /// </summary>
        public bool IsShown =>
            HasMessageText
            || Attachments.Count == 0
            || Attachments.Any(attachment => attachment.IsShown);

        /// <summary>Shows or hides low-quality copies on this message, for this session only.</summary>
        internal void SetShowLowQualityCopies(bool show)
        {
            foreach (var attachment in Attachments)
            {
                attachment.ShowLowQualityCopies = show;
            }

            OnPropertyChanged(nameof(IsShown));
        }

        /// <summary>Its position in the authoritative conversation order.</summary>
        public int SequenceNumber { get; }

        /// <summary>Its sender, or <see langword="null"/> for a system message.</summary>
        public long? SenderParticipantID { get; }

        /// <summary>The sender's display name, or a neutral word for a system message.</summary>
        public string SenderText { get; }

        /// <summary>The stored local wall-clock reading, to the minute.</summary>
        public string TimeText { get; }

        /// <summary>The message text, empty when the message is only a media placeholder.</summary>
        public string MessageText { get; }

        /// <summary>Whether there is any text to show.</summary>
        public bool HasMessageText { get; }

        /// <summary>
        /// The message text split into ordinary runs and the web or email addresses in it.
        /// </summary>
        /// <remarks>
        /// Joining these back together reproduces <see cref="MessageText"/> exactly. Displaying
        /// them contacts nothing; an address is only followed when the reader clicks it.
        /// </remarks>
        public IReadOnlyList<MessageTextSpan> TextSpans { get; }

        /// <summary>Whether the stored content is the export's media placeholder marker.</summary>
        public bool IsPlaceholderOnly { get; }

        /// <summary>Whether this message begins a new calendar day on the page.</summary>
        public bool HasDateSeparator { get; }

        /// <summary>The separator's text, or empty.</summary>
        public string DateSeparatorText { get; }

        /// <summary>Whether the export system wrote this rather than a participant.</summary>
        public bool IsSystemMessage { get; }

        /// <summary>A stable colour for this sender within the conversation.</summary>
        public Brush SenderBrush { get; }

        /// <summary>This message's attachments, in ordinal order.</summary>
        public IReadOnlyList<AttachmentViewModel> Attachments { get; }

        /// <summary>Whether this message has any attachment.</summary>
        public bool HasAttachments => Attachments.Count > 0;

        /// <summary>
        /// Whether the operator has named this message's sender as the local participant, for this
        /// session only.
        /// </summary>
        public bool IsFromLocalParticipant
        {
            get => _isFromLocalParticipant;
            internal set
            {
                if (SetProperty(ref _isFromLocalParticipant, value))
                {
                    OnPropertyChanged(nameof(BubbleAlignment));
                }
            }
        }

        /// <summary>Which side of the conversation this bubble sits on.</summary>
        /// <remarks>
        /// A system message stays centred whatever has been claimed, because it has no sender to be
        /// anybody's.
        /// </remarks>
        public HorizontalAlignment BubbleAlignment =>
            IsSystemMessage
                ? HorizontalAlignment.Center
                : IsFromLocalParticipant
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left;
    }
}
