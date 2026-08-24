using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C2 — how much of the message side the token side could not supply, whatever the order.
    /// </summary>
    /// <remarks>
    /// Reported over the frozen supply-assessed population rather than the order-testable one, because
    /// supply failure is what removes pairs from the order-testable population: measuring it only where
    /// it had already been survived would report a column of zeros and call the archive complete.
    /// <para>
    /// A pair short of supply cannot fully embed however its tokens are ordered, so this is a coverage
    /// and recovery limitation — incomplete recovery, grammar coverage, direction coverage, the scope
    /// choice — and not evidence that message order fails to reach token order.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceSupplyLossCensus
    {
        /// <summary>The frozen population, which is every pair supply was assessed over.</summary>
        public required string PopulationName { get; init; }

        /// <summary>How many pairs it holds.</summary>
        public required int PairCount { get; init; }

        /// <summary>Message observations those pairs carry.</summary>
        public required int MessageObservationCount { get; init; }

        /// <summary>Emitted token positions those pairs carry.</summary>
        public required int TokenPositionCount { get; init; }

        /// <summary>Pairs the token side can supply completely.</summary>
        public required int ZeroSupplyLossPairCount { get; init; }

        /// <summary>Pairs it cannot.</summary>
        public required int PositiveSupplyLossPairCount { get; init; }

        /// <summary>Message observations sitting in pairs it cannot.</summary>
        public required int MessageObservationsInPositiveSupplyLossPairs { get; init; }

        /// <summary>How many pairs carried each outgoing shortfall, ascending.</summary>
        public required IReadOnlyList<ValueCount> OutgoingShortfallDistribution { get; init; }

        /// <summary>How many pairs carried each incoming shortfall, ascending.</summary>
        public required IReadOnlyList<ValueCount> IncomingShortfallDistribution { get; init; }

        /// <summary>How many pairs carried each total supply loss, ascending.</summary>
        public required IReadOnlyList<ValueCount> SupplyLossDistribution { get; init; }

        /// <summary>Every pair is either short of supply or not.</summary>
        public int ClassifiedTotal => ZeroSupplyLossPairCount + PositiveSupplyLossPairCount;
    }
}
