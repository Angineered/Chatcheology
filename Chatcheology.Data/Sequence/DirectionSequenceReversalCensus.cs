namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C5c — the tertiary reversal diagnostic, retained for continuity and for the palindrome census.
    /// </summary>
    /// <remarks>
    /// Not a control, and it must drive no conclusion. Reversal preserves the outgoing count, the
    /// incoming count and the run count exactly, so the reversed sequence sits inside the same
    /// <c>(o, i, r)</c> reference class as the observed one and carries the same <c>q_r</c>: it varies
    /// none of the quantities the reference conditions on, so it cannot test against it.
    /// <para>
    /// Two large parts of this census were settled before the run and are separated from the rest.
    /// Binary-determinate pairs have <c>q_r = 1</c>, so both the observed order and its reverse — both
    /// members of that class — must admit, and every one of them is a forced <c>BothAdmit</c>.
    /// Graded-determinate pairs have zero share variance across the class, so both orders must give the
    /// identical share, and every one of them is a forced tie. Reporting those inside the contingency
    /// would present an algebraic certainty as observed agreement.
    /// </para>
    /// <para>
    /// No composite score is formed across the metrics below, and none may be.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceReversalCensus
    {
        /// <summary>The frozen diagnostic population, which is the classified pairs.</summary>
        public required string PopulationName { get; init; }

        /// <summary>How many pairs it holds.</summary>
        public required int PairCount { get; init; }

        /// <summary>
        /// Pairs whose message pattern reads the same in both directions, and which therefore cannot
        /// distinguish an order from its reverse at all.
        /// </summary>
        public required int PalindromicPatternPairCount { get; init; }

        /// <summary>
        /// Binary-determinate pairs, every one of which is a <c>BothAdmit</c> before the run.
        /// </summary>
        public required int ForcedBothAdmitPairCount { get; init; }

        /// <summary>
        /// Graded-determinate pairs, every one of which is a share tie before the run.
        /// </summary>
        public required int ForcedGradedTiePairCount { get; init; }

        /// <summary>
        /// The admission contingency over the binary-informative pairs, which are the only ones whose
        /// admission could differ between the two orders.
        /// </summary>
        public required DirectionSequenceAdmissionContingency VariableAdmission { get; init; }

        /// <summary>Binary-informative pairs the contingency covers.</summary>
        public required int VariableAdmissionPairCount { get; init; }

        /// <summary>
        /// The alignment-length comparison over the whole population, where a tie may be structural.
        /// </summary>
        public required DirectionSequenceOrderComparisonCounts AlignmentComparison { get; init; }

        /// <summary>
        /// The embedding-share comparison over the graded-informative pairs, which are the only ones
        /// whose share could differ.
        /// </summary>
        public required DirectionSequenceOrderComparisonCounts VariableShareComparison { get; init; }

        /// <summary>Graded-informative pairs the share comparison covers.</summary>
        public required int VariableSharePairCount { get; init; }
    }
}
