using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C0 — the message side of the gate: which attachments were considered, what shape their
    /// per-date direction patterns have, and what was excluded.
    /// </summary>
    /// <remarks>
    /// One direction symbol per unresolved attachment, not per message, ordered by
    /// <c>(Message.SequenceNumber, Attachment.Ordinal)</c>. A message carrying several attachments
    /// therefore keeps several symbols in ordinal order rather than being collapsed to one.
    /// <para>
    /// The population is every unresolved attachment of the conversation, and
    /// <see cref="EveryConversationUnresolvedAttachmentConsidered"/> is the evidence of that: candidate
    /// availability plays no part in eligibility here, so an attachment that the first pass found
    /// candidate-poor, or found no exact-date candidate for at all, is still counted.
    /// </para>
    /// <para>
    /// Distributions rather than per-date rows throughout. Length, composition, transitions and runs
    /// beside a real date would reconstruct the private direction pattern — at this archive's typical
    /// sequence length, uniquely.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceMessagePopulation
    {
        /// <summary>Unresolved attachments in the whole workspace, for context.</summary>
        public required int WorkspaceUnresolvedAttachmentCount { get; init; }

        /// <summary>Unresolved attachments belonging to the conversation analysed.</summary>
        public required int ConversationUnresolvedAttachmentCount { get; init; }

        /// <summary>How many of those the gate emitted a direction symbol for.</summary>
        public required int ConsideredAttachmentCount { get; init; }

        /// <summary>
        /// Attachments whose message direction could not be established, which is expected to be zero.
        /// </summary>
        /// <remarks>
        /// A non-zero count is a stop condition rather than a figure to carry forward, so a census that
        /// returns at all reports zero here. It is reported anyway: the reader should be able to see
        /// that the check ran, not infer it from the absence of a failure.
        /// </remarks>
        public required int UnknownDirectionAttachmentCount { get; init; }

        /// <summary>Distinct local message dates those attachments fall on.</summary>
        public required int DistinctAttachmentDateCount { get; init; }

        /// <summary>Attachments whose message was sent by the local participant.</summary>
        public required int OutgoingAttachmentCount { get; init; }

        /// <summary>Attachments whose message was sent by anyone else.</summary>
        public required int IncomingAttachmentCount { get; init; }

        /// <summary>
        /// How many attachments carried each <c>Attachment.Ordinal</c>, in ascending ordinal order.
        /// </summary>
        /// <remarks>
        /// Expected to be one row at ordinal one across this population, because each imported
        /// <c>&lt;Media omitted&gt;</c> placeholder currently has exactly one attachment. Censused
        /// rather than assumed: if that ever stops being true the ordering model has to be read
        /// differently, and this is where it shows.
        /// </remarks>
        public required IReadOnlyList<ValueCount> OrdinalDistribution { get; init; }

        /// <summary>Messages carrying more than one unresolved attachment.</summary>
        public required int MultiAttachmentMessageCount { get; init; }

        /// <summary>Messages carrying at least one.</summary>
        public required int MessageWithAttachmentCount { get; init; }

        /// <summary>The most unresolved attachments any one message carries.</summary>
        public required int MaximumAttachmentsOnOneMessage { get; init; }

        /// <summary>How many dates carried each message-sequence length, ascending.</summary>
        public required IReadOnlyList<ValueCount> SequenceLengthDistribution { get; init; }

        /// <summary>The same lengths as bounds, a middle and bands.</summary>
        public required CountSummary SequenceLength { get; init; }

        /// <summary>How many dates carried each direction-transition count, ascending.</summary>
        public required IReadOnlyList<ValueCount> TransitionCountDistribution { get; init; }

        /// <summary>How many dates carried each direction-run count, ascending.</summary>
        public required IReadOnlyList<ValueCount> RunCountDistribution { get; init; }

        /// <summary>Dates whose attachments are all outgoing.</summary>
        public required int OutgoingOnlyDateCount { get; init; }

        /// <summary>Dates whose attachments are all incoming.</summary>
        public required int IncomingOnlyDateCount { get; init; }

        /// <summary>Dates carrying attachments in both directions.</summary>
        public required int BothDirectionDateCount { get; init; }

        /// <summary>
        /// Whether every unresolved attachment of the conversation reached the message side.
        /// </summary>
        /// <remarks>
        /// The gate's evidence that no inclusion rule was applied. The query behind it selects
        /// unresolved attachments of one conversation and nothing else — no candidate, no asset, no
        /// media evidence takes part — so equality here is what "candidate availability was not used
        /// as an inclusion rule" actually means.
        /// </remarks>
        public bool EveryConversationUnresolvedAttachmentConsidered =>
            ConsideredAttachmentCount == ConversationUnresolvedAttachmentCount;
    }
}
