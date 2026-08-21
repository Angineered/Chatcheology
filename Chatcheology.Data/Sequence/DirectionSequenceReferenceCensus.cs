namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C0b — the exact reference expectations the classified population carries, and how much of them
    /// burstiness alone accounts for.
    /// </summary>
    /// <remarks>
    /// Expectations only. Every quantity here depends on the message pattern and on the token side's
    /// own <c>(outgoing, incoming, runs)</c> class, never on the observed token order, so nothing here
    /// has an observed counterpart yet. The counterparts belong to the later alignment stage, and
    /// computing one now would reveal the outcome before the population rules are frozen.
    /// <para>
    /// The two sums are the gate's whole numerical output: they fix what an observed admission count
    /// and an observed embedding share will later be compared against, on the populations named here.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceReferenceCensus
    {
        /// <summary>Classified pairs.</summary>
        public required int Population { get; init; }

        /// <summary>How the classified pairs fall across the descriptive bands.</summary>
        public required DirectionSequenceQrBandCounts Bands { get; init; }

        /// <summary>What each band is made of, in band order.</summary>
        public required IReadOnlyList<DirectionSequenceQrBandRow> BandRows { get; init; }

        /// <summary>The sum of exact <c>q_r</c> over every classified pair.</summary>
        /// <remarks>
        /// A decimal sum of exact rationals. Each <c>q_r</c> is computed and compared as exact
        /// integers; the addition is where a reading convenience takes over, and no classification
        /// depends on it.
        /// </remarks>
        public required double SumOfConditionalAdmissionProbability { get; init; }

        /// <summary>The same sum restricted to the binary-informative pairs.</summary>
        /// <remarks>
        /// The figure an observed admission count will be compared against if the binary primary
        /// population is frozen as the binary-informative pairs. The determinate pairs contribute
        /// exactly zero to that comparison either way.
        /// </remarks>
        public required double SumOfConditionalAdmissionProbabilityOverInformative { get; init; }

        /// <summary>The sum of exact <c>E_r[share]</c> over every classified pair.</summary>
        public required double SumOfExpectedEmbeddingShare { get; init; }

        /// <summary>The same sum restricted to the graded-informative pairs.</summary>
        public required double SumOfExpectedEmbeddingShareOverInformative { get; init; }

        /// <summary>
        /// The distribution of <c>q(p, o, i) - q_r(p, o, i, r)</c> across classified pairs.
        /// </summary>
        /// <remarks>
        /// How much of an apparent order effect the composition-only reference would have attributed to
        /// order when burstiness alone accounts for it. The difference runs in both directions, because
        /// clustering moves the binary and graded references opposite ways.
        /// </remarks>
        public required DirectionSequenceRatioSummary
            ExchangeableLessConditionalAdmission { get; init; }

        /// <summary>
        /// Classified pairs the composition-only reference would have called informative.
        /// </summary>
        public required int InformativeUnderExchangeableReferenceCount { get; init; }

        /// <summary>
        /// Classified pairs the composition-only reference would have called determinate.
        /// </summary>
        public required int DeterminateUnderExchangeableReferenceCount { get; init; }

        /// <summary>
        /// Pairs the composition-only reference would have called informative and run conditioning
        /// makes determinate.
        /// </summary>
        /// <remarks>
        /// The honest statement of what run conditioning costs the binary test. Run conditioning is a
        /// bias correction that removes a false signal, not a power gain, and it necessarily shrinks the
        /// informative population; this is the size of that shrinkage.
        /// </remarks>
        public required int InformativeLostToRunConditioningCount { get; init; }
    }
}
