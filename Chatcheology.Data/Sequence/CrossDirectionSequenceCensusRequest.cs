namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// What one cross-direction sequence census is asked to look at.
    /// </summary>
    /// <remarks>
    /// The conversation and the local participant are supplied, never derived. Direction decides the
    /// whole cohort, so a census that guessed which participant was the exporting user would be
    /// measuring a different population from the preserved first pass while looking identical.
    /// <para>
    /// <c>LocalParticipantID</c> is not nullable here, unlike the matching request it is passed to.
    /// Without it every message direction is unknown, no candidate is direction-compatible, and the
    /// cohort this census exists to measure is empty.
    /// </para>
    /// </remarks>
    public sealed class CrossDirectionSequenceCensusRequest
    {
        /// <summary>An existing workspace at the current schema version, opened read-only.</summary>
        public required string DatabasePath { get; init; }

        /// <summary>The conversation whose unresolved attachments form the cohort.</summary>
        public required long ConversationID { get; init; }

        /// <summary>Which participant of that conversation is the local, exporting user.</summary>
        public required long LocalParticipantID { get; init; }
    }
}
