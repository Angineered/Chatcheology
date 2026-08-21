namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// The spread of one per-pair ratio, as bounds, a middle, signs and magnitude bands.
    /// </summary>
    /// <remarks>
    /// A distribution rather than a table of rows, which is what makes it safe to keep. Every ratio
    /// summarised here is computed per <c>(scope key, local date)</c> pair, and a pair-by-pair listing
    /// beside a real date would let a short private direction pattern be reconstructed from fields
    /// that are individually harmless.
    /// <para>
    /// Signs and bands are assigned by exact integer comparison on the ratio's own numerator and
    /// denominator. <see cref="Minimum"/>, <see cref="Median"/> and <see cref="Maximum"/> are decimal
    /// conveniences for reading, never the basis of a comparison; <see cref="Median"/> is the lower of
    /// the two middle values, matching the convention the earlier Phase 6 censuses used.
    /// </para>
    /// <para>
    /// The bands partition by magnitude and are contiguous: zero is counted on its own, then each band
    /// holds the values above the previous band's edge up to its own.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceRatioSummary
    {
        /// <summary>Pairs described.</summary>
        public required int Population { get; init; }

        /// <summary>The smallest value observed, or zero when there is nothing to describe.</summary>
        public required double Minimum { get; init; }

        /// <summary>The lower median, or zero when there is nothing to describe.</summary>
        public required double Median { get; init; }

        /// <summary>The largest value observed, or zero when there is nothing to describe.</summary>
        public required double Maximum { get; init; }

        /// <summary>Values strictly below zero.</summary>
        public required int Negative { get; init; }

        /// <summary>Values exactly zero.</summary>
        public required int Zero { get; init; }

        /// <summary>Values strictly above zero.</summary>
        public required int Positive { get; init; }

        /// <summary>Magnitude above zero, up to five hundredths.</summary>
        public required int MagnitudeAtMostFiveHundredths { get; init; }

        /// <summary>Magnitude above five hundredths, up to one quarter.</summary>
        public required int MagnitudeAtMostOneQuarter { get; init; }

        /// <summary>Magnitude above one quarter, up to one half.</summary>
        public required int MagnitudeAtMostOneHalf { get; init; }

        /// <summary>Magnitude above one half, up to ninety-five hundredths.</summary>
        public required int MagnitudeAtMostNinetyFiveHundredths { get; init; }

        /// <summary>Magnitude above ninety-five hundredths, but short of one.</summary>
        public required int MagnitudeBelowOne { get; init; }

        /// <summary>Magnitude exactly one.</summary>
        public required int MagnitudeExactlyOne { get; init; }

        /// <summary>Magnitude above one.</summary>
        public required int MagnitudeAboveOne { get; init; }

        /// <summary>Every sign counted, which must equal <see cref="Population"/>.</summary>
        public int SignTotal => Negative + Zero + Positive;

        /// <summary>Every magnitude band with zero, which must equal <see cref="Population"/>.</summary>
        public int BandTotal =>
            Zero
            + MagnitudeAtMostFiveHundredths
            + MagnitudeAtMostOneQuarter
            + MagnitudeAtMostOneHalf
            + MagnitudeAtMostNinetyFiveHundredths
            + MagnitudeBelowOne
            + MagnitudeExactlyOne
            + MagnitudeAboveOne;
    }
}
