namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// A descriptive band of the exact run-conditioned admission reference <c>q_r</c>.
    /// </summary>
    /// <remarks>
    /// Descriptive, and nothing more. No band here is a threshold, a cut-off or a primary population
    /// rule: which pairs a primary statistic is computed over is frozen by review after the real gate
    /// census, and a band that arrived in the code first would be that decision made early and
    /// invisibly.
    /// <para>
    /// The two endpoints are exact and are separated from everything else on purpose. <c>q_r = 0</c>
    /// and <c>q_r = 1</c> are the binary-determinate cases, decided by composition and burstiness
    /// alone, and rounding either of them into a neighbouring band would hide the very population the
    /// gate exists to size.
    /// </para>
    /// <para>
    /// Bands are contiguous, cover the whole unit interval, and are assigned by exact integer
    /// comparison on <c>S</c> against <c>A</c> rather than on a decimal.
    /// </para>
    /// </remarks>
    public enum DirectionSequenceQrBand
    {
        /// <summary>Exactly zero: no arrangement of the class admits the pattern.</summary>
        ExactlyZero = 0,

        /// <summary>Above zero, up to five hundredths.</summary>
        AboveZeroToFiveHundredths = 1,

        /// <summary>Above five hundredths, up to one quarter.</summary>
        AboveFiveHundredthsToOneQuarter = 2,

        /// <summary>Above one quarter, up to one half.</summary>
        AboveOneQuarterToOneHalf = 3,

        /// <summary>Above one half, up to three quarters.</summary>
        AboveOneHalfToThreeQuarters = 4,

        /// <summary>Above three quarters, up to ninety-five hundredths.</summary>
        AboveThreeQuartersToNinetyFiveHundredths = 5,

        /// <summary>Above ninety-five hundredths, but short of one.</summary>
        AboveNinetyFiveHundredthsBelowOne = 6,

        /// <summary>Exactly one: every arrangement of the class admits the pattern.</summary>
        ExactlyOne = 7,
    }
}
