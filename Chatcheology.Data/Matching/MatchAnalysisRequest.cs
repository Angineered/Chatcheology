namespace Chatcheology.Data.Matching
{
    /// <summary>
    /// What one matching analysis is asked to look at.
    /// </summary>
    /// <param name="ConversationID">
    /// The conversation whose unresolved attachments are analysed. Media is not owned by a
    /// conversation, so every eligible asset in the workspace remains a possible candidate.
    /// </param>
    /// <param name="LocalParticipantID">
    /// Which conversation participant is the local, exporting user, or <see langword="null"/> when
    /// that is not known.
    /// <para>
    /// Supplied explicitly and never guessed. Nothing infers it from a display name, from message
    /// counts or from which side sent more media. When it is null every message direction is
    /// <see cref="MessageDirection.Unknown"/> and direction evidence simply does not participate,
    /// which is the honest result rather than a degraded one.
    /// </para>
    /// </param>
    public sealed record MatchAnalysisRequest(
        long ConversationID,
        long? LocalParticipantID = null);
}
