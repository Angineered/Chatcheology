namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C5b — the binary admission comparison against one neighbouring calendar date's token order.
    /// </summary>
    /// <remarks>
    /// A co-primary control, not a secondary diagnostic. Unlike reversal it actually breaks the
    /// date-to-token pairing while preserving real observed sequence structure, and unlike reversal its
    /// shifted comparison carries its own composition, its own run count, its own supply state and its
    /// own <c>q_r</c> — computed from the shifted date's tokens, never inherited from the correct date.
    /// <para>
    /// Paired but <b>not</b> independent, and no independence-based statistic may be derived from it. The
    /// neighbour's token sequence appears both as its own correct-date evidence and as the comparator for
    /// the date beside it, and each date serves as a comparator twice. The question it answers is only
    /// whether correct-date pairing departs from its own conditional expectation differently from how
    /// neighbour-date pairing departs from its own.
    /// </para>
    /// <para>
    /// <see cref="CorrectDate"/> is restricted to the intersection and is therefore a different
    /// population from the full-population primary result. Comparing a full-population figure against a
    /// shifted figure here would be comparing two different populations; only
    /// intersection-to-intersection comparisons are valid.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceBinaryAdjacentControl
    {
        /// <summary>Which neighbouring calendar date this control pairs with.</summary>
        public required DirectionSequenceAdjacentOffset Offset { get; init; }

        /// <summary>How the intersection was formed, with every dropped pair accounted for.</summary>
        public required DirectionSequenceIntersectionAccounting Accounting { get; init; }

        /// <summary>The correct-date outcome, restricted to the intersection.</summary>
        public required DirectionSequenceBinaryFigures CorrectDate { get; init; }

        /// <summary>The shifted-date outcome, over the same intersection.</summary>
        public required DirectionSequenceBinaryFigures ShiftedDate { get; init; }

        /// <summary>What the intersection is made of, against the population it came from.</summary>
        public required DirectionSequenceIntersectionComposition Composition { get; init; }
    }
}
