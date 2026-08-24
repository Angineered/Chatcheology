namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C5b — the graded embedding-share comparison against one neighbouring calendar date's token order.
    /// </summary>
    /// <remarks>
    /// The graded counterpart of <see cref="DirectionSequenceBinaryAdjacentControl"/>, and reported over
    /// its <b>own</b> intersection. Binary and graded informativeness are different classifications, so
    /// forcing one common intersection across the two statistics would compute each of them over pairs the
    /// other's rule admitted — which is a third population belonging to neither.
    /// <para>
    /// Everything the binary control says about pairing, independence and intersection-to-intersection
    /// comparison applies here unchanged.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceGradedAdjacentControl
    {
        /// <summary>Which neighbouring calendar date this control pairs with.</summary>
        public required DirectionSequenceAdjacentOffset Offset { get; init; }

        /// <summary>How the intersection was formed, with every dropped pair accounted for.</summary>
        public required DirectionSequenceIntersectionAccounting Accounting { get; init; }

        /// <summary>The correct-date outcome, restricted to the intersection.</summary>
        public required DirectionSequenceGradedFigures CorrectDate { get; init; }

        /// <summary>The shifted-date outcome, over the same intersection.</summary>
        public required DirectionSequenceGradedFigures ShiftedDate { get; init; }

        /// <summary>What the intersection is made of, against the population it came from.</summary>
        public required DirectionSequenceIntersectionComposition Composition { get; init; }
    }
}
