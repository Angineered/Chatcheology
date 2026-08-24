namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// How an ordered outcome compared between the observed token order and its reverse.
    /// </summary>
    /// <remarks>
    /// Three cells, kept apart. Collapsing them into "how often the actual order won" would hide which
    /// outcome produced the label, and at this archive's sequence lengths the ties are mostly structural
    /// rather than informative: a palindromic message pattern cannot distinguish an order from its
    /// reverse at all, and short patterns are frequently palindromic.
    /// </remarks>
    public sealed class DirectionSequenceOrderComparisonCounts
    {
        /// <summary>Pairs where the observed order carried more.</summary>
        public required int ActualOrderBetter { get; init; }

        /// <summary>Pairs where the two carried the same.</summary>
        public required int Tie { get; init; }

        /// <summary>Pairs where the reversed order carried more.</summary>
        public required int ReversedOrderBetter { get; init; }

        /// <summary>Every pair falls in exactly one cell.</summary>
        public int Total => ActualOrderBetter + Tie + ReversedOrderBetter;
    }
}
