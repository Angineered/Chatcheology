namespace Chatcheology.Data.Sequence
{
    /// <summary>What one within-direction assignment census is asked to look at.</summary>
    /// <remarks>
    /// The conversation and the local participant are supplied, never derived: direction decides every
    /// group's candidate set, so a census that guessed the exporting user would measure a different
    /// population while looking identical.
    /// </remarks>
    public sealed class WithinDirectionAssignmentCensusRequest
    {
        /// <summary>An existing workspace at the current schema version, opened read-only.</summary>
        public required string DatabasePath { get; init; }

        /// <summary>The conversation whose unresolved attachments are grouped.</summary>
        public required long ConversationID { get; init; }

        /// <summary>Which participant of that conversation is the local, exporting user.</summary>
        public required long LocalParticipantID { get; init; }
    }
}
