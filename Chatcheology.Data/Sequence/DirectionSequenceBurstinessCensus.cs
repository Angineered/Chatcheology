using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C0b — how clustered the emitted token sequences are, against exchangeable expectation, and how
    /// clustered the message patterns are.
    /// </summary>
    /// <remarks>
    /// Token-side only, and therefore outcome-blind: a run count is a property of the token sequence's
    /// own arrangement and says nothing about how that arrangement lines up against a message pattern.
    /// <para>
    /// This diagnostic says whether the archive's token sequences are clustered relative to
    /// exchangeable expectation at all. What that clustering does to the reference is a separate
    /// figure — the <c>q - q_r</c> distribution — because a difference in run counts and a difference
    /// in admission probability are not interchangeable.
    /// </para>
    /// <para>
    /// Distributions rather than per-date rows. A message-pattern run count beside that pattern's
    /// composition and length pins the pattern down exactly, which is the combination permanent
    /// evidence must not carry.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceBurstinessCensus
    {
        /// <summary>Pairs described.</summary>
        public required int Population { get; init; }

        /// <summary>The observed token run count as bounds, a middle and bands.</summary>
        public required CountSummary TokenRunCounts { get; init; }

        /// <summary>
        /// The observed token run count less <c>1 + 2 o i / (o + i)</c>, as a distribution.
        /// </summary>
        /// <remarks>
        /// Positive is more clustered than exchangeable expectation, negative less. Both directions
        /// occur, and both move the reference.
        /// </remarks>
        public required DirectionSequenceDifferenceSummary
            ObservedLessExpectedTokenRunCount { get; init; }

        /// <summary>How many pairs carried each message-pattern run count, ascending.</summary>
        public required IReadOnlyList<ValueCount> MessageRunCountDistribution { get; init; }

        /// <summary>How many pairs carried each message-pattern transition count, ascending.</summary>
        public required IReadOnlyList<ValueCount> MessageTransitionCountDistribution { get; init; }

        /// <summary>
        /// Pairs whose message pattern shows only one direction, so order cannot be informative
        /// whatever the token side does.
        /// </summary>
        /// <remarks>
        /// Retained from the earlier direction-transition vocabulary for comparability, and descriptive
        /// only: transition complexity selects no population here. Exact <c>q_r</c>, after supply
        /// adequacy, is the capacity measure, which avoids treating a short palindrome such as
        /// <c>OIO</c> as strongly informative merely because it holds two transitions.
        /// </remarks>
        public required int NotOrderInformativePairCount { get; init; }

        /// <summary>Pairs whose message pattern holds exactly one direction transition.</summary>
        public required int WeaklyOrderInformativePairCount { get; init; }

        /// <summary>Pairs whose message pattern holds at least two.</summary>
        public required int StrictlyOrderInformativePairCount { get; init; }
    }
}
