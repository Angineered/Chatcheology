namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C0b — the two determinacy classifications, side by side, with their cross-tabulation.
    /// </summary>
    /// <remarks>
    /// Two classifications, computed separately over the same fixed reference class, because the
    /// statistics they protect are different. Binary determinacy — <c>q_r</c> at zero or one — fixes
    /// whether the pattern is admitted at all. Graded determinacy — <c>A * Q = P * P</c> — fixes
    /// whether every arrangement of the class carries the identical embedding count, and therefore the
    /// identical normalised share. Neither implies the other in general.
    /// <para>
    /// One implication does hold, and only in one direction: if every embedding count in a class is the
    /// same constant, then either that constant is zero and nothing admits the pattern or it is
    /// positive and everything does. So graded determinacy implies binary determinacy, and equivalently
    /// every binary-informative pair is graded-informative — which makes
    /// <see cref="BinaryInformativeAndGradedDeterminate"/> provably empty.
    /// </para>
    /// <para>
    /// That row is computed rather than derived from the argument. A non-zero value means one of the two
    /// classifications is wrong, and the census refuses to return rather than reporting it.
    /// </para>
    /// <para>
    /// <c>Q</c> itself appears nowhere here. It exists to decide one boolean and is not a variance, a
    /// dispersion, a confidence measure or anything that may be reported.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceDeterminacyCensus
    {
        /// <summary>Classified pairs.</summary>
        public required int Population { get; init; }

        /// <summary>Pairs whose binary admission the conditioning data fixes.</summary>
        public required int BinaryDeterminatePairCount { get; init; }

        /// <summary>Pairs whose binary admission genuinely varies within the class.</summary>
        public required int BinaryInformativePairCount { get; init; }

        /// <summary>Message observations the binary-determinate pairs carry.</summary>
        public required int BinaryDeterminateMessageObservations { get; init; }

        /// <summary>Message observations the binary-informative pairs carry.</summary>
        public required int BinaryInformativeMessageObservations { get; init; }

        /// <summary>Pairs whose embedding count is the same in every arrangement of the class.</summary>
        public required int GradedDeterminatePairCount { get; init; }

        /// <summary>Pairs whose embedding count varies within the class.</summary>
        public required int GradedInformativePairCount { get; init; }

        /// <summary>Message observations the graded-determinate pairs carry.</summary>
        public required int GradedDeterminateMessageObservations { get; init; }

        /// <summary>Message observations the graded-informative pairs carry.</summary>
        public required int GradedInformativeMessageObservations { get; init; }

        /// <summary>Informative for both statistics.</summary>
        public required int BinaryInformativeAndGradedInformative { get; init; }

        /// <summary>
        /// Determinate for binary admission and informative for the graded share, which is legitimate
        /// and expected.
        /// </summary>
        public required int BinaryDeterminateAndGradedInformative { get; init; }

        /// <summary>
        /// Informative for binary admission and determinate for the graded share, which cannot occur.
        /// </summary>
        public required int BinaryInformativeAndGradedDeterminate { get; init; }

        /// <summary>Determinate for both.</summary>
        public required int BinaryDeterminateAndGradedDeterminate { get; init; }

        /// <summary>Every cell added together, which must equal <see cref="Population"/>.</summary>
        public int CrossTabulationTotal =>
            BinaryInformativeAndGradedInformative
            + BinaryDeterminateAndGradedInformative
            + BinaryInformativeAndGradedDeterminate
            + BinaryDeterminateAndGradedDeterminate;
    }
}
