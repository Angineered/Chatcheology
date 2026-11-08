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
    /// <see cref="IsForwarded"/> is <see langword="true"/> or <see langword="false"/> when the
    /// producer had a recorded fact for the message, and <see langword="null"/> when it had none.
    /// Null means "not known", never "not forwarded"; a viewer shows a Forwarded label only on
    /// <see langword="true"/>. <see cref="ForwardScore"/> is the recorded score, or null.
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

        /// <summary>
        /// Which file the message came from (format version 2). Every message of a version 1 archive is
        /// <see cref="ArchiveMessageSource.Workspace"/>. Recorded, never displayed (CH-33 D4).
        /// </summary>
        public ArchiveMessageSource Source { get; init; } = ArchiveMessageSource.Workspace;
    }

    /// <summary>The file a message of the combined conversation came from.</summary>
    public enum ArchiveMessageSource
    {
        /// <summary>The main workspace's conversation.</summary>
        Workspace,

        /// <summary>An older chat export, shown before the workspace's conversation.</summary>
        Export,

        /// <summary>
        /// Format version 4 (CH-71): a message read straight from WhatsApp's own message store, shown after the
        /// workspace's conversation (written from CH-71 step 3). Same rules as <see cref="Workspace"/>: it may carry
        /// a forwarded fact, and null means "not known".
        /// </summary>
        Msgstore,
    }
}
