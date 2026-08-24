namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// The four figures a binary admission comparison reports, over a named population.
    /// </summary>
    /// <remarks>
    /// Four figures and no fifth. There is no variance, spread, <c>z</c>, <c>p</c>-value, interval,
    /// posterior or score here and no member one could be put in, because any of them would rest on an
    /// independence model across date and scope pairs that this project has not established: pairs share
    /// sources, devices, dates and one person's behaviour. The empirical context for the difference is
    /// the paired adjacent-date control, not an invented yardstick.
    /// <para>
    /// <see cref="PopulationName"/> and <see cref="PairCount"/> travel with the figures because a
    /// difference is meaningless without the population it was computed over, and because a population
    /// that drifted from its frozen size must be visible in the result rather than inferable from the
    /// code.
    /// </para>
    /// <para>
    /// <c>q_r</c> is an exact conditional reference. It is not a confidence, a probability that the
    /// hypothesis is false, or a significance threshold.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceBinaryFigures
    {
        /// <summary>The frozen population these figures were computed over.</summary>
        public required string PopulationName { get; init; }

        /// <summary>How many pairs it holds.</summary>
        public required int PairCount { get; init; }

        /// <summary>
        /// Pairs whose whole message-direction pattern embeds in the observed token order.
        /// </summary>
        public required int ObservedAdmissions { get; init; }

        /// <summary>
        /// The sum of exact <c>q_r</c> over the same pairs: the conditional expectation holding each
        /// pair's direction composition and observed burstiness fixed.
        /// </summary>
        public required double SumOfConditionalAdmissionProbability { get; init; }

        /// <summary>
        /// <see cref="ObservedAdmissions"/> less <see cref="SumOfConditionalAdmissionProbability"/>,
        /// reported as a difference and described as one.
        /// </summary>
        public required double ObservedMinusExpected { get; init; }

        /// <summary>
        /// The smallest difference this population could algebraically produce, which is zero
        /// admissions.
        /// </summary>
        /// <remarks>
        /// Pre-registered scale context, computable before any outcome exists and reported so the
        /// wording of a result cannot be chosen after seeing its size. Not a threshold.
        /// </remarks>
        public double MinimumPossibleObservedMinusExpected =>
            -SumOfConditionalAdmissionProbability;

        /// <summary>
        /// The largest difference it could algebraically produce, which is every pair admitting.
        /// </summary>
        public double MaximumPossibleObservedMinusExpected =>
            PairCount - SumOfConditionalAdmissionProbability;
    }
}
