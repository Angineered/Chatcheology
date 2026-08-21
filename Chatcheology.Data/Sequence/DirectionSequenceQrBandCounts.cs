namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C0b — how the classified pairs fall across the descriptive <c>q_r</c> bands.
    /// </summary>
    /// <remarks>
    /// Descriptive. No band becomes a primary threshold until one is explicitly frozen after the gate
    /// review, and the bands exist so that decision can be made from evidence rather than invented in
    /// code.
    /// <para>
    /// The two exact endpoints are separated from everything else because they are the
    /// binary-determinate population: at <c>q_r = 0</c> no arrangement of the class admits the pattern
    /// and at <c>q_r = 1</c> every one of them does, so in both cases the observed and expected sides
    /// agree by construction and the pair cannot move the binary figure.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceQrBandCounts
    {
        /// <summary>Pairs banded.</summary>
        public required int Population { get; init; }

        /// <summary>Exactly zero.</summary>
        public required int ExactlyZero { get; init; }

        /// <summary>Above zero, up to five hundredths.</summary>
        public required int AboveZeroToFiveHundredths { get; init; }

        /// <summary>Above five hundredths, up to one quarter.</summary>
        public required int AboveFiveHundredthsToOneQuarter { get; init; }

        /// <summary>Above one quarter, up to one half.</summary>
        public required int AboveOneQuarterToOneHalf { get; init; }

        /// <summary>Above one half, up to three quarters.</summary>
        public required int AboveOneHalfToThreeQuarters { get; init; }

        /// <summary>Above three quarters, up to ninety-five hundredths.</summary>
        public required int AboveThreeQuartersToNinetyFiveHundredths { get; init; }

        /// <summary>Above ninety-five hundredths, but short of one.</summary>
        public required int AboveNinetyFiveHundredthsBelowOne { get; init; }

        /// <summary>Exactly one.</summary>
        public required int ExactlyOne { get; init; }

        /// <summary>Every band added together, which must equal <see cref="Population"/>.</summary>
        public int BandTotal =>
            ExactlyZero
            + AboveZeroToFiveHundredths
            + AboveFiveHundredthsToOneQuarter
            + AboveOneQuarterToOneHalf
            + AboveOneHalfToThreeQuarters
            + AboveThreeQuartersToNinetyFiveHundredths
            + AboveNinetyFiveHundredthsBelowOne
            + ExactlyOne;
    }
}
