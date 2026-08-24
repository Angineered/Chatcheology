namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C4 — whether the whole message pattern embeds in the observed token order, and in how many ways.
    /// </summary>
    /// <remarks>
    /// A unique embedding is <b>not</b> a match. These are counts over two direction symbols; nothing
    /// here identifies which token belongs to which message, and nothing here may resolve an attachment
    /// or support an assignment.
    /// <para>
    /// Raw embedding counts are never summed or averaged across pairs — they scale combinatorially with
    /// token length, so a total would be dominated by whichever pair happened to have the longest token
    /// side. Only the classification and the normalised share aggregate.
    /// </para>
    /// <para>
    /// <see cref="ForcedDeterminateAdmissionCount"/> is the part of this census that was settled before
    /// the run. A binary-determinate pair has <c>q_r = 1</c>, meaning every arrangement of its own
    /// reference class admits its pattern, and the observed order is one of those arrangements — so it
    /// must admit. Separating it keeps an algebraic certainty from being read as a result.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceMultiplicityCensus
    {
        /// <summary>The frozen population, which is the classified pairs.</summary>
        public required string PopulationName { get; init; }

        /// <summary>How many pairs it holds.</summary>
        public required int PairCount { get; init; }

        /// <summary>Pairs whose pattern the observed order does not carry at all.</summary>
        public required int NoExactMonotoneDirectionAlignment { get; init; }

        /// <summary>Pairs it carries in exactly one way.</summary>
        public required int OneExactMonotoneDirectionAlignment { get; init; }

        /// <summary>Pairs it carries in more than one way.</summary>
        public required int SeveralExactMonotoneDirectionAlignments { get; init; }

        /// <summary>Pairs whose pattern embeds at all.</summary>
        public required int PairsWithValidEmbedding { get; init; }

        /// <summary>
        /// Admissions among binary-determinate pairs, which determinacy forces to be all of them.
        /// </summary>
        public required int ForcedDeterminateAdmissionCount { get; init; }

        /// <summary>Binary-determinate pairs in the population.</summary>
        public required int BinaryDeterminatePairCount { get; init; }

        /// <summary>Admissions among binary-informative pairs, which is the part that could vary.</summary>
        public required int InformativeAdmissionCount { get; init; }

        /// <summary>Binary-informative pairs in the population.</summary>
        public required int BinaryInformativePairCount { get; init; }

        /// <summary>Every pair falls in exactly one multiplicity class.</summary>
        public int ClassifiedTotal =>
            NoExactMonotoneDirectionAlignment
            + OneExactMonotoneDirectionAlignment
            + SeveralExactMonotoneDirectionAlignments;
    }
}
