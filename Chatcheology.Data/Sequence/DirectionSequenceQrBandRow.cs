using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C0b — what one descriptive <c>q_r</c> band is made of: how many pairs and observations it
    /// carries, what shape their message patterns have, and how widely it is spread across scope keys
    /// and dates.
    /// </summary>
    /// <remarks>
    /// The freeze review needs to know not only how many pairs sit in each band but what kind of pair
    /// they are. A band holding one scope key's pairs on a handful of dates supports a different
    /// decision from one spread across the whole archive, and neither is visible from a count alone.
    /// <para>
    /// Counts and distributions only. <see cref="DistinctDateCount"/> is how many dates the band
    /// touches, never which.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceQrBandRow
    {
        /// <summary>The band described.</summary>
        public required DirectionSequenceQrBand Band { get; init; }

        /// <summary>Pairs in it.</summary>
        public required int PairCount { get; init; }

        /// <summary>Message observations those pairs carry.</summary>
        public required int MessageObservationCount { get; init; }

        /// <summary>Emitted token positions those pairs carry.</summary>
        public required int TokenPositionCount { get; init; }

        /// <summary>Their message-sequence length as bounds, a middle and bands.</summary>
        public required CountSummary MessageSequenceLength { get; init; }

        /// <summary>How many of them carried each message transition count, ascending.</summary>
        public required IReadOnlyList<ValueCount> TransitionCountDistribution { get; init; }

        /// <summary>Distinct scope keys the band touches.</summary>
        public required int DistinctScopeKeyCount { get; init; }

        /// <summary>Distinct local dates it touches.</summary>
        public required int DistinctDateCount { get; init; }
    }
}
