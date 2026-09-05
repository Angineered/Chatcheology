namespace Chatcheology.Data.Viewer
{
    /// <summary>
    /// One participant of a conversation, as the viewer needs to name them.
    /// </summary>
    /// <remarks>
    /// Identity and display name only. Nothing here says which participant exported the
    /// conversation, because the workspace does not record that: it is a claim a caller makes, and
    /// the viewer treats it as a presentation choice made for one session rather than as a stored
    /// fact.
    /// </remarks>
    public sealed class ConversationParticipantSummary
    {
        /// <summary>The participant.</summary>
        public required long ParticipantID { get; init; }

        /// <summary>The stored display name.</summary>
        public required string DisplayName { get; init; }
    }
}
