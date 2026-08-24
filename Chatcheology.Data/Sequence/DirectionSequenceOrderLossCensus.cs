using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C3 — how much of the message side the observed token order could not carry in order.
    /// </summary>
    /// <remarks>
    /// Over the frozen classified population, which is supply-sufficient by construction. So on real
    /// data <see cref="SupplyLossPairCount"/> is zero, the direction-count ceiling equals the message
    /// length, and order loss equals everything unaligned. That is expected in advance, and the identity
    /// <c>total unaligned = supply loss + order loss</c> is retained as a population-integrity
    /// assertion: if it ever fails here the population is wrong, not the arithmetic. The non-degenerate
    /// two-term decomposition is proven in synthetic supply-insufficient shapes, and C3 is deliberately
    /// not widened to make it visible on real data.
    /// <para>
    /// The ratio of alignment to message length is deliberately absent. It is a per-pair quantity that
    /// invites being read as a score, and a score is what this stage must not produce.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceOrderLossCensus
    {
        /// <summary>The frozen population, which is the order-testable classified pairs.</summary>
        public required string PopulationName { get; init; }

        /// <summary>How many pairs it holds.</summary>
        public required int PairCount { get; init; }

        /// <summary>Message observations those pairs carry.</summary>
        public required int MessageObservationCount { get; init; }

        /// <summary>Emitted token positions those pairs carry.</summary>
        public required int TokenPositionCount { get; init; }

        /// <summary>
        /// Pairs of this population short of supply, which construction makes zero.
        /// </summary>
        public required int SupplyLossPairCount { get; init; }

        /// <summary>Pairs the observed order carries completely.</summary>
        public required int ZeroOrderLossPairCount { get; init; }

        /// <summary>Pairs it does not.</summary>
        public required int PositiveOrderLossPairCount { get; init; }

        /// <summary>How many pairs carried each order loss, ascending.</summary>
        public required IReadOnlyList<ValueCount> OrderLossDistribution { get; init; }

        /// <summary>The ordered alignment length as bounds, a middle and bands.</summary>
        public required CountSummary OrderedDirectionAlignment { get; init; }

        /// <summary>The message length as bounds, a middle and bands, for the alignment to be read against.</summary>
        public required CountSummary MessageLength { get; init; }

        /// <summary>Every pair either loses order or does not.</summary>
        public int ClassifiedTotal => ZeroOrderLossPairCount + PositiveOrderLossPairCount;
    }
}
