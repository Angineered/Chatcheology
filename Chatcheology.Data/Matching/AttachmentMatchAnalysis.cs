namespace Chatcheology.Data.Matching
{
    /// <summary>
    /// The complete Phase 6 evidence for one unresolved attachment.
    /// </summary>
    /// <remarks>
    /// One of these exists at a time. They are handed to the caller's sink as the analysis walks
    /// the conversation in order and are not retained afterwards, because a real conversation's
    /// attachments and their candidates together are far too many relationships to hold at once.
    /// <para>
    /// Nothing here resolves anything. The analysis produces evidence and stops: no field is a
    /// confidence level, and the uniqueness flags exist to be reported, never to be acted on.
    /// A single candidate under date and direction evidence is still not proof.
    /// </para>
    /// </remarks>
    public sealed class AttachmentMatchAnalysis
    {
        /// <summary>The unresolved attachment this analysis is about.</summary>
        public required long AttachmentID { get; init; }

        /// <summary>The message the attachment belongs to.</summary>
        public required long MessageID { get; init; }

        /// <summary>The attachment's position within its message.</summary>
        public required int Ordinal { get; init; }

        /// <summary>
        /// The message's position in the conversation, preserved so later ordering work has the
        /// context it will need. This phase implements no ordering evidence.
        /// </summary>
        public required int MessageSequenceNumber { get; init; }

        /// <summary>The message's local wall-clock reading, exactly as stored.</summary>
        public required DateTime MessageDateTimeLocal { get; init; }

        /// <summary>
        /// The message's local calendar date — the one date candidate generation compares against.
        /// </summary>
        public required DateOnly MessageDate { get; init; }

        /// <summary>
        /// Who sent the message, or <see langword="null"/> for a system message that has no sender.
        /// </summary>
        public required long? SenderParticipantID { get; init; }

        /// <summary>Which way the message travelled, as far as the caller's input allows.</summary>
        public required MessageDirection MessageDirection { get; init; }

        /// <summary>
        /// Candidates with a supporting copy dated to the message's own date, ordered by
        /// <c>MediaAssetID</c> ascending. The primary candidate set.
        /// </summary>
        public required IReadOnlyList<AttachmentMatchCandidate> ExactDateCandidates { get; init; }

        /// <summary>
        /// Candidates with no copy on the message's date but a supporting copy on the day before or
        /// after, ordered by <c>MediaAssetID</c> ascending.
        /// </summary>
        /// <remarks>
        /// A separate, weaker set, never merged into the exact-date one and never promoted into it
        /// because that one came back empty. It exists because a media file's name is dated by the
        /// device that wrote it while a message carries the export's local reading, so a real item
        /// can legitimately sit one day either side.
        /// </remarks>
        public required IReadOnlyList<AttachmentMatchCandidate> AdjacentDateCandidates { get; init; }

        /// <summary>How many exact-date candidates there are.</summary>
        public int ExactDateCandidateCount => ExactDateCandidates.Count;

        /// <summary>
        /// How many exact-date candidates are <see cref="DirectionCompatibility.Compatible"/>.
        /// </summary>
        public int ExactDateDirectionCompatibleCandidateCount { get; init; }

        /// <summary>Whether there is any adjacent-date candidate at all.</summary>
        public bool HasAdjacentDateCandidates => AdjacentDateCandidates.Count > 0;

        /// <summary>
        /// Whether exactly one asset carries exact-date evidence for this attachment.
        /// </summary>
        /// <remarks>
        /// Reported, and nothing more. Uniqueness under a heuristic is not sufficient for automatic
        /// resolution, and there is deliberately no path from this flag to a resolved attachment.
        /// </remarks>
        public bool HasUniqueExactDateCandidate => ExactDateCandidateCount == 1;

        /// <summary>
        /// Whether exactly one exact-date candidate is direction-compatible. Reported under the
        /// same rule as <see cref="HasUniqueExactDateCandidate"/>.
        /// </summary>
        public bool HasUniqueExactDateDirectionCompatibleCandidate =>
            ExactDateDirectionCompatibleCandidateCount == 1;
    }
}
