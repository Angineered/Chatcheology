namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// The spread of one per-pair signed difference that is not confined to the unit interval, such as
    /// an observed run count against its exchangeable expectation.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="DirectionSequenceRatioSummary"/> because the quantity is different in
    /// kind: a run-count difference is counted in runs and can be tens either way, so unit-interval
    /// bands would collapse the whole distribution into one bucket.
    /// <para>
    /// A distribution rather than per-date rows, for the same privacy reason as every other C0b figure:
    /// a run count beside a date and a composition can pin down a short private direction pattern.
    /// </para>
    /// <para>
    /// The magnitude bands are contiguous and cover everything, with an exact zero counted both in
    /// <see cref="Zero"/> and in the first band.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceDifferenceSummary
    {
        /// <summary>Pairs described.</summary>
        public required int Population { get; init; }

        /// <summary>The smallest value observed, or zero when there is nothing to describe.</summary>
        public required double Minimum { get; init; }

        /// <summary>The lower median, or zero when there is nothing to describe.</summary>
        public required double Median { get; init; }

        /// <summary>The largest value observed, or zero when there is nothing to describe.</summary>
        public required double Maximum { get; init; }

        /// <summary>Values strictly below zero, which are less clustered than expected.</summary>
        public required int Negative { get; init; }

        /// <summary>Values exactly zero.</summary>
        public required int Zero { get; init; }

        /// <summary>Values strictly above zero, which are more clustered than expected.</summary>
        public required int Positive { get; init; }

        /// <summary>Magnitude up to one.</summary>
        public required int MagnitudeAtMostOne { get; init; }

        /// <summary>Magnitude above one, up to two.</summary>
        public required int MagnitudeAtMostTwo { get; init; }

        /// <summary>Magnitude above two, up to five.</summary>
        public required int MagnitudeAtMostFive { get; init; }

        /// <summary>Magnitude above five, up to ten.</summary>
        public required int MagnitudeAtMostTen { get; init; }

        /// <summary>Magnitude above ten, up to twenty-five.</summary>
        public required int MagnitudeAtMostTwentyFive { get; init; }

        /// <summary>Magnitude above twenty-five.</summary>
        public required int MagnitudeAboveTwentyFive { get; init; }

        /// <summary>Every sign counted, which must equal <see cref="Population"/>.</summary>
        public int SignTotal => Negative + Zero + Positive;

        /// <summary>Every magnitude band, which must equal <see cref="Population"/>.</summary>
        public int BandTotal =>
            MagnitudeAtMostOne
            + MagnitudeAtMostTwo
            + MagnitudeAtMostFive
            + MagnitudeAtMostTen
            + MagnitudeAtMostTwentyFive
            + MagnitudeAboveTwentyFive;
    }
}
