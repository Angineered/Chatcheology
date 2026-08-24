namespace Chatcheology.Data.Sequence
{
    /// <summary>One band's slice of a primary result.</summary>
    /// <remarks>
    /// Descriptive only. No band here may become a primary filter after an outcome is visible, and every
    /// band used is one that existed before the run — the frozen <c>q_r</c> bands and the project's fixed
    /// length bands — rather than an edge chosen to suit a result.
    /// <para>
    /// A cell holding too few pairs to print is suppressed, for the same reconstruction reason as
    /// <see cref="DirectionSequenceCoarseCell"/>: a one-pair cell beside a length and a composition is a
    /// private direction pattern.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceDescriptiveCell
    {
        /// <summary>The band.</summary>
        public required string Band { get; init; }

        /// <summary>Pairs in it, or zero where it was suppressed.</summary>
        public required int PairCount { get; init; }

        /// <summary>Message observations they carry, or zero where suppressed.</summary>
        public required int MessageObservationCount { get; init; }

        /// <summary>Admissions among them, or zero where suppressed.</summary>
        public required int ObservedAdmissions { get; init; }

        /// <summary>The conditional expectation over them, or zero where suppressed.</summary>
        public required double SumOfConditionalAdmissionProbability { get; init; }

        /// <summary>The difference over them, or zero where suppressed.</summary>
        public required double ObservedMinusExpected { get; init; }

        /// <summary>Whether the cell was withheld for holding too few pairs to print.</summary>
        public required bool Suppressed { get; init; }
    }
}
