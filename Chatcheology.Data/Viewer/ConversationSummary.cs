namespace Chatcheology.Data.Viewer
{
    /// <summary>
    /// Enough about one conversation to choose it and describe it, without loading any of it.
    /// </summary>
    /// <remarks>
    /// The bounds are the messages at the smallest and largest <c>SequenceNumber</c>, not the
    /// smallest and largest stored timestamp. Two reasons: the sequence extremes are served by the
    /// <c>UNIQUE (ConversationID, SequenceNumber)</c> index while the timestamp extremes are a
    /// scan, and "the date of the first message in conversation order" is what the viewer actually
    /// shows. The earliest timestamp anywhere in the conversation is a different claim, and the
    /// viewer has no reason to make it.
    /// <para>
    /// The three nullable members are null together, for an empty conversation.
    /// </para>
    /// </remarks>
    public sealed class ConversationSummary
    {
        /// <summary>The conversation described.</summary>
        public required long ConversationID { get; init; }

        /// <summary>The stored conversation title.</summary>
        public required string Title { get; init; }

        /// <summary>How many messages the conversation holds.</summary>
        public required int MessageCount { get; init; }

        /// <summary>
        /// The first message's sequence number, or <see langword="null"/> for an empty
        /// conversation.
        /// </summary>
        public required int? FirstSequenceNumber { get; init; }

        /// <summary>
        /// The last message's sequence number, or <see langword="null"/> for an empty
        /// conversation.
        /// </summary>
        public required int? LastSequenceNumber { get; init; }

        /// <summary>
        /// The wall-clock reading stored on the message at
        /// <see cref="FirstSequenceNumber"/>, or <see langword="null"/>.
        /// </summary>
        public required DateTime? FirstMessageDateTimeLocal { get; init; }

        /// <summary>
        /// The wall-clock reading stored on the message at
        /// <see cref="LastSequenceNumber"/>, or <see langword="null"/>.
        /// </summary>
        public required DateTime? LastMessageDateTimeLocal { get; init; }

        /// <summary>The conversation's participants, ordered by identifier.</summary>
        public required IReadOnlyList<ConversationParticipantSummary> Participants { get; init; }
    }
}
