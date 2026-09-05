namespace Chatcheology.Data.Viewer
{
    /// <summary>
    /// One bounded window of a conversation, always in ascending <c>SequenceNumber</c> order.
    /// </summary>
    /// <remarks>
    /// Ascending whichever direction it was fetched from. A previous-page read walks backwards
    /// internally and reverses its rows before returning, so a caller never has to know which of
    /// the four read methods produced the page it is rendering.
    /// <para>
    /// <see cref="HasEarlier"/> and <see cref="HasLater"/> are derived by comparing this page's
    /// bounds with the conversation's own, so no extra statement is issued to discover whether a
    /// Previous or Next button should be enabled.
    /// </para>
    /// </remarks>
    public sealed class ConversationPage
    {
        /// <summary>The conversation this page came from.</summary>
        public required long ConversationID { get; init; }

        /// <summary>The messages, ascending. Empty when the conversation holds none.</summary>
        public required IReadOnlyList<ConversationMessage> Messages { get; init; }

        /// <summary>
        /// The first sequence number on this page, or <see langword="null"/> when it is empty.
        /// </summary>
        public required int? FirstSequenceNumber { get; init; }

        /// <summary>
        /// The last sequence number on this page, or <see langword="null"/> when it is empty.
        /// </summary>
        public required int? LastSequenceNumber { get; init; }

        /// <summary>Whether the conversation holds a message before this page.</summary>
        public required bool HasEarlier { get; init; }

        /// <summary>Whether the conversation holds a message after this page.</summary>
        public required bool HasLater { get; init; }
    }
}
