namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// The four figures a graded embedding-share comparison reports, over a named population.
    /// </summary>
    /// <remarks>
    /// The graded counterpart of <see cref="DirectionSequenceBinaryFigures"/>, and subject to the same
    /// restraint: four figures, no dispersion, no derived score. The two statistics are co-primary and
    /// may move in opposite directions under a shared confound — clustering can reduce the chance a
    /// pattern embeds at all while increasing how many ways it embeds when it does — and that is expected
    /// rather than a defect. Neither is promoted after the fact.
    /// <para>
    /// Both sums are taken through the same per-pair decimal reading of an exact ratio, so the observed
    /// and expected totals are commensurable and the difference does not carry a conversion asymmetry.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceGradedFigures
    {
        /// <summary>The frozen population these figures were computed over.</summary>
        public required string PopulationName { get; init; }

        /// <summary>How many pairs it holds.</summary>
        public required int PairCount { get; init; }

        /// <summary>
        /// The sum of observed normalised embedding share: for each pair, how many monotone embeddings
        /// the observed token order admits as a share of all choices of that many positions.
        /// </summary>
        public required double SumOfObservedNormalisedEmbeddingShare { get; init; }

        /// <summary>The sum of exact <c>E_r[share]</c> over the same pairs.</summary>
        public required double SumOfExpectedEmbeddingShare { get; init; }

        /// <summary>The observed sum less the expected sum, reported as a difference.</summary>
        public required double ObservedMinusExpected { get; init; }

        /// <summary>The smallest difference this population could algebraically produce.</summary>
        /// <remarks>Pre-registered scale context, not a threshold.</remarks>
        public double MinimumPossibleObservedMinusExpected => -SumOfExpectedEmbeddingShare;

        /// <summary>
        /// The largest difference it could algebraically produce, which every pair's share reaching one
        /// would give.
        /// </summary>
        /// <remarks>
        /// Algebraic only. A share of one means every choice of positions spells the pattern, which the
        /// archive's shapes do not approach; the bound is here to frame the scale, not to suggest one.
        /// </remarks>
        public double MaximumPossibleObservedMinusExpected =>
            PairCount - SumOfExpectedEmbeddingShare;
    }
}
