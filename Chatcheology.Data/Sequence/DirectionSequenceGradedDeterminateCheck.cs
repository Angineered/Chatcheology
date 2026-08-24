namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// The observed-share computation checked against the frozen expected reading on the pairs where the
    /// two must agree exactly.
    /// </summary>
    /// <remarks>
    /// A graded-determinate pair satisfies <c>A * Q = P * P</c>, which is zero variance of the normalised
    /// embedding share across its own reference class. Zero variance means every arrangement of the class
    /// carries the same share, and the observed token order is one of those arrangements — so its observed
    /// share must equal the expected share exactly, pair by pair, not approximately.
    /// <para>
    /// Both readings are produced by the same decimal conversion of an exact ratio, so the equality is an
    /// equality of readings and not a tolerance. That makes this the strongest available check on the
    /// whole observed-share path: it runs on real data, before any primary graded figure is interpreted,
    /// and it says nothing whatever about the graded-informative population that carries the primary
    /// statistic.
    /// </para>
    /// <para>
    /// The frozen record's printed remainder — the classified total less the informative total — is a
    /// display confirmation of the same thing, and is deliberately not the authority: subtracting two
    /// six-decimal renderings is a weaker statement than checking every pair.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceGradedDeterminateCheck
    {
        /// <summary>Graded-determinate pairs checked.</summary>
        public required int PairCount { get; init; }

        /// <summary>Pairs whose observed reading equalled its expected reading exactly.</summary>
        public required int ReadingsIdenticalPairCount { get; init; }

        /// <summary>The observed share summed over those pairs.</summary>
        public required double SumOfObservedNormalisedEmbeddingShare { get; init; }

        /// <summary>The expected share summed over the same pairs.</summary>
        public required double SumOfExpectedEmbeddingShare { get; init; }

        /// <summary>Whether every pair agreed, which a returned census requires.</summary>
        public bool EveryReadingIdentical => ReadingsIdenticalPairCount == PairCount;
    }
}
