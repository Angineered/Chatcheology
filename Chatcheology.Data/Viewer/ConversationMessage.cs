using Chatcheology.Core.Models;

namespace Chatcheology.Data.Viewer
{
    /// <summary>
    /// One message, as the viewer displays it.
    /// </summary>
    /// <remarks>
    /// Presentation facts only. <c>RawContent</c>, <c>ImportSourceID</c>,
    /// <c>SourceLineStart</c>, <c>SourceLineEnd</c> and <c>ExpectedMediaType</c> are all stored and
    /// none of them is here: a read model that carries a column because it exists invites a caller
    /// to depend on provenance the viewer has no feature for.
    /// <para>
    /// <see cref="MessageDateTimeLocal"/> is <see cref="DateTimeKind.Unspecified"/>, matching what
    /// is stored — a wall-clock reading with no zone must not become one that claims local time or
    /// UTC just by being read.
    /// </para>
    /// <para>
    /// <see cref="MessageContent"/> separates its lines with <see cref="ParsedMessage.LineSeparator"/>,
    /// deliberately not the running platform's newline.
    /// </para>
    /// </remarks>
    public sealed class ConversationMessage
    {
        /// <summary>The message.</summary>
        public required long MessageID { get; init; }

        /// <summary>The conversation it belongs to.</summary>
        public required long ConversationID { get; init; }

        /// <summary>Its position in the authoritative conversation order.</summary>
        public required int SequenceNumber { get; init; }

        /// <summary>The stored local wall-clock reading.</summary>
        public required DateTime MessageDateTimeLocal { get; init; }

        /// <summary>
        /// The sender, or <see langword="null"/> for a system message, which has none.
        /// </summary>
        public required long? SenderParticipantID { get; init; }

        /// <summary>
        /// The sender's stored display name, or <see langword="null"/> when there is no sender.
        /// </summary>
        public required string? SenderDisplayName { get; init; }

        /// <summary>Whether a participant wrote this message or the export system did.</summary>
        public required MessageType MessageType { get; init; }

        /// <summary>The stored message text.</summary>
        public required string MessageContent { get; init; }

        /// <summary>
        /// The message's attachments, ordered by <see cref="ConversationAttachment.Ordinal"/>.
        /// Empty for the great majority of messages.
        /// </summary>
        public required IReadOnlyList<ConversationAttachment> Attachments { get; init; }
    }
}
