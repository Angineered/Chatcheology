using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C0 — what the analysed <c>(date, direction)</c> groups are made of, for one direction or pooled.
    /// </summary>
    public sealed class AssignmentGroupPopulation
    {
        public required int GroupCount { get; init; }

        public required int MessageCount { get; init; }

        /// <summary>Messages per group, as a spread and in bands.</summary>
        public required CountSummary MessagesPerGroup { get; init; }

        /// <summary>Frozen compatible candidate assets per group.</summary>
        public required SequenceBandCounts CompatibleAssetsPerGroup { get; init; }

        /// <summary>Distinct token positions per group.</summary>
        public required SequenceBandCounts TokenPositionsPerGroup { get; init; }

        /// <summary>Distinct <c>(asset, token)</c> candidate occurrences per group.</summary>
        public required SequenceBandCounts OccurrencesPerGroup { get; init; }

        /// <summary>
        /// The group's contribution to the frozen compatible relation total: the sum of
        /// <c>M * A</c>.
        /// </summary>
        /// <remarks>
        /// Pooled, this must equal the First Pass <c>ExactCandidateRelationsCompatible</c>, because
        /// every message in one group shares the same compatible candidate set. It is the denominator
        /// for every primary reduction figure.
        /// </remarks>
        public required int BaselineCompatibleRelationCount { get; init; }

        /// <summary>Of that baseline, the part sitting in groups the hypothesis can satisfy.</summary>
        public required int BaselineRelationsInFeasibleGroups { get; init; }

        /// <summary>And the part sitting in groups it cannot — reported, never netted off.</summary>
        public required int BaselineRelationsInImpossibleGroups { get; init; }
    }
}
