namespace Chatcheology.Archive
{
    /// <summary>One participant of the archived conversation.</summary>
    public sealed class ArchiveParticipant
    {
        /// <summary>The participant, with the identifier the producer gave it.</summary>
        public required long ParticipantID { get; init; }

        /// <summary>The name shown for the participant.</summary>
        public required string DisplayName { get; init; }

        /// <summary>Whether this is the local participant, whose side is "me". Exactly one is.</summary>
        public required bool IsLocal { get; init; }
    }
}
