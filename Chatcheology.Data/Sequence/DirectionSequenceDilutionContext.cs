using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C0 — how foreign-dominated the emitted token streams are, as the message side's share of the
    /// positions it sits among.
    /// </summary>
    /// <remarks>
    /// Context and nothing else. A token position may belong to any conversation, to a forwarding
    /// session, to an album sent in one action or to a post-restore download batch, and the gate cannot
    /// tell which. These figures say how diluted the streams are, and stop there.
    /// <para>
    /// <b>Not an effect-size bound.</b> A small share does not bound what any later comparison can
    /// detect: one correctly placed conversation token can turn a failed embedding into a successful
    /// one. Presenting it as a ceiling on a detectable effect would be wrong in a way that looks
    /// conservative.
    /// </para>
    /// <para>
    /// Distributions, not a per-date table. The message symbol count beside a real date is one of the
    /// combinations that reconstructs a private direction pattern.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceDilutionContext
    {
        /// <summary>Pairs carrying both message symbols and emitted token positions.</summary>
        public required int Population { get; init; }

        /// <summary>How many pairs carried each message symbol count, ascending.</summary>
        public required IReadOnlyList<ValueCount> MessageSymbolDistribution { get; init; }

        /// <summary>The emitted token position count as bounds, a middle and bands.</summary>
        public required CountSummary TokenPositions { get; init; }

        /// <summary>
        /// The message side's share of the emitted positions, as a distribution over pairs.
        /// </summary>
        public required DirectionSequenceRatioSummary ConversationShare { get; init; }
    }
}
