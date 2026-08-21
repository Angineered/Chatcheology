using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C0b — supply adequacy at one stage of equivalent-position collapse.
    /// </summary>
    /// <remarks>
    /// Supply sufficiency and order-discriminating capacity are different questions and are never
    /// conflated. A pair whose token side cannot even supply the message side's direction counts is not
    /// order-testable at all, and its arithmetic <c>q_r</c> of zero says nothing about order — the
    /// causes include incomplete recovery, grammar coverage, direction coverage, the scope choice and a
    /// genuine mismatch, and the gate cannot separate them.
    /// </remarks>
    public sealed class DirectionSequenceSupplyCounts
    {
        /// <summary>Pairs assessed.</summary>
        public required int Population { get; init; }

        /// <summary>Pairs where both shortfalls are zero.</summary>
        public required int SupplySufficientPairCount { get; init; }

        /// <summary>Pairs where either shortfall is positive.</summary>
        public required int SupplyInsufficientPairCount { get; init; }

        /// <summary>How many pairs carried each outgoing shortfall, ascending.</summary>
        public required IReadOnlyList<ValueCount> OutgoingShortfallDistribution { get; init; }

        /// <summary>How many pairs carried each incoming shortfall, ascending.</summary>
        public required IReadOnlyList<ValueCount> IncomingShortfallDistribution { get; init; }

        /// <summary>Message observations sitting in supply-insufficient pairs.</summary>
        public required int MessageObservationsInSupplyInsufficientPairs { get; init; }
    }
}
