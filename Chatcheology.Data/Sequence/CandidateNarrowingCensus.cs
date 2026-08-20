namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C3 — how far the sequence-order hypothesis would narrow each message's compatible candidate
    /// assets, for one direction or pooled.
    /// </summary>
    /// <remarks>
    /// Every figure here is the mathematical consequence of a hypothesis no stage has tested. A message
    /// counted in <see cref="UniqueCandidateUnderSequenceOrderHypothesis"/> has one candidate
    /// <em>under that hypothesis</em>; it is not matched, resolved, confirmed or anchored.
    /// <para>
    /// The classes are mutually exclusive in declaration order, which matters where the baseline is
    /// already one: such a message had one candidate before the hypothesis was applied, so it counts as
    /// no reduction and is reported separately by
    /// <see cref="MessagesAlreadyUniqueWithoutHypothesis"/>.
    /// </para>
    /// </remarks>
    public sealed class CandidateNarrowingCensus
    {
        /// <summary>The union over the message's valid range is the whole frozen candidate set.</summary>
        public required int NoReduction { get; init; }

        /// <summary>One candidate asset remains, from a baseline of more than one.</summary>
        public required int UniqueCandidateUnderSequenceOrderHypothesis { get; init; }

        /// <summary>Fewer candidates than the baseline, more than one.</summary>
        public required int ReducedUnderSequenceOrderHypothesis { get; init; }

        /// <summary>
        /// Messages whose baseline was already one candidate, so no narrowing was available to measure.
        /// </summary>
        public required int MessagesAlreadyUniqueWithoutHypothesis { get; init; }

        /// <summary>Baseline compatible candidate relations in feasible groups.</summary>
        public required int BaselineCandidateRelations { get; init; }

        /// <summary>The same relations after the hypothetical order constraint.</summary>
        public required int SequenceCompatibleCandidateRelations { get; init; }

        /// <summary>Relations removed by the constraint.</summary>
        public int AbsoluteReduction => BaselineCandidateRelations - SequenceCompatibleCandidateRelations;

        /// <summary>Messages the classes above divide.</summary>
        public int MessageTotal =>
            NoReduction + UniqueCandidateUnderSequenceOrderHypothesis
            + ReducedUnderSequenceOrderHypothesis;
    }
}
