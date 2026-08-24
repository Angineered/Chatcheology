namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// How the failed embeddings divide between those a coverage limitation can explain and those it
    /// cannot.
    /// </summary>
    /// <remarks>
    /// The message side holds every eligible unresolved attachment. The token side holds only positions
    /// that survived two filters — the deliberately narrow supported-token grammar, and having a usable
    /// folder-direction label — and both delete positions from the middle of a sequence. Deleting an
    /// incoming symbol between two outgoing ones can turn a pattern that would not embed into one that
    /// does, or the reverse. So where either coverage is incomplete, a failure cannot be attributed to
    /// message order failing to reach token order.
    /// <para>
    /// The label changes nothing it is attached to. It does not erase the failure, remove the pair from
    /// its frozen census, or move an admission count. It exists so a report cannot claim a cause the
    /// evidence does not carry.
    /// </para>
    /// <para>
    /// Note the units: grammar coverage is counted in observations, because a name failing the grammar
    /// never becomes a logical position at all, while direction coverage is counted in collapsed
    /// positions. Both are used only as "was any lost here", so nothing arithmetic mixes them.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceFailureAttributionCensus
    {
        /// <summary>The population the failures were counted over.</summary>
        public required string PopulationName { get; init; }

        /// <summary>How many pairs it holds.</summary>
        public required int PairCount { get; init; }

        /// <summary>Pairs whose pattern the observed order did not carry.</summary>
        public required int FailedEmbeddingCount { get; init; }

        /// <summary>
        /// Failures on a token side that lost observations to the grammar or positions to missing
        /// direction, so the failure cannot be attributed to order.
        /// </summary>
        public required int FailureNotAttributable { get; init; }

        /// <summary>
        /// Failures on a token side that lost nothing to either filter, which are the only failures the
        /// order question can be discussed over.
        /// </summary>
        public required int FailureWithCompleteTokenCoverage { get; init; }

        /// <summary>Failing pairs whose token side lost observations to the grammar only.</summary>
        public required int FailuresWithGrammarCoverageLossOnly { get; init; }

        /// <summary>Failing pairs whose token side lost positions to missing direction only.</summary>
        public required int FailuresWithDirectionCoverageLossOnly { get; init; }

        /// <summary>Failing pairs whose token side lost both.</summary>
        public required int FailuresWithBothCoverageLosses { get; init; }

        /// <summary>Pairs of the population whose token side lost nothing to either filter.</summary>
        public required int PairsWithCompleteTokenCoverage { get; init; }

        /// <summary>Every failure is attributable or it is not.</summary>
        public int AttributionTotal => FailureNotAttributable + FailureWithCompleteTokenCoverage;

        /// <summary>Every unattributable failure falls in exactly one loss combination.</summary>
        public int LossCombinationTotal =>
            FailuresWithGrammarCoverageLossOnly
            + FailuresWithDirectionCoverageLossOnly
            + FailuresWithBothCoverageLosses;
    }
}
