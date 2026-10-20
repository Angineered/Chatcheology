namespace Chatcheology.Archive
{
    /// <summary>Whether a participant wrote a message or the chat system did.</summary>
    public enum ArchiveMessageKind
    {
        /// <summary>Written by a participant, who is its sender.</summary>
        User,

        /// <summary>A system line, which has no sender.</summary>
        System,
    }

    /// <summary>One message of the archived conversation, with its attachments.</summary>
    /// <remarks>
    /// <see cref="IsForwarded"/> and <see cref="ForwardScore"/> are <see langword="null"/> in
    /// format version 1 whenever the producer has no safe way to know them. Null means "not known",
    /// never "not forwarded"; a viewer shows a Forwarded label only on <see langword="true"/>.
    /// </remarks>
    public sealed class ArchiveMessage
    {
        /// <summary>The message.</summary>
        public required long MessageID { get; init; }

        /// <summary>Its position in the order of the chat. Unique within the archive.</summary>
        public required int SequenceNumber { get; init; }

        /// <summary>
        /// The local wall-clock time as the export gave it, with no zone attached
        /// (<see cref="DateTimeKind.Unspecified"/>), to the second.
        /// </summary>
        public required DateTime DateTimeLocal { get; init; }

        /// <summary>Whether a participant or the system wrote it.</summary>
        public required ArchiveMessageKind Kind { get; init; }

        /// <summary>The sender, or <see langword="null"/> for a system message.</summary>
        public required long? SenderParticipantID { get; init; }

        /// <summary>The parsed message text; lines are separated by <c>\n</c>.</summary>
        public required string Text { get; init; }

        /// <summary>Whether the message was forwarded, or <see langword="null"/> when not known.</summary>
        public required bool? IsForwarded { get; init; }

        /// <summary>The chat app's forward score, or <see langword="null"/> when not known.</summary>
        public required int? ForwardScore { get; init; }

        /// <summary>The message's attachments, in ordinal order. Empty for most messages.</summary>
        public required IReadOnlyList<ArchiveAttachment> Attachments { get; init; }
    }
}
