namespace Chatcheology.Data.Media
{
    /// <summary>
    /// The spread of one per-group or per-key count, as bounds, a middle and bands.
    /// </summary>
    /// <remarks>
    /// Bands as well as bounds, because the shape is the question. A median of ten with a maximum of
    /// one hundred and fifty describes an archive whose typical day is small and whose worst day is
    /// not, and neither figure alone says that.
    /// <para>
    /// <see cref="Median"/> is the <em>lower</em> median: the lower of the two middle values when the
    /// population is even. Stated rather than left to the reader, and matching the convention the
    /// Phase 6 first pass already used, so two censuses are comparable and no fractional count of
    /// files is ever printed.
    /// </para>
    /// <para>
    /// Every count summarised here is at least one for any group or key that exists, so the bands
    /// start at one, are contiguous, and cover every value.
    /// </para>
    /// </remarks>
    public sealed class CountSummary
    {
        /// <summary>Groups or keys described.</summary>
        public required int Population { get; init; }

        /// <summary>The smallest count observed, or zero when there is nothing to describe.</summary>
        public required int Minimum { get; init; }

        /// <summary>The lower median, or zero when there is nothing to describe.</summary>
        public required int Median { get; init; }

        /// <summary>The largest count observed, or zero when there is nothing to describe.</summary>
        public required int Maximum { get; init; }

        /// <summary>Exactly one.</summary>
        public required int One { get; init; }

        /// <summary>Exactly two.</summary>
        public required int Two { get; init; }

        /// <summary>Three to five.</summary>
        public required int ThreeToFive { get; init; }

        /// <summary>Six to ten.</summary>
        public required int SixToTen { get; init; }

        /// <summary>Eleven to twenty-five.</summary>
        public required int ElevenToTwentyFive { get; init; }

        /// <summary>More than twenty-five.</summary>
        public required int MoreThanTwentyFive { get; init; }

        /// <summary>Every band added together, which must equal <see cref="Population"/>.</summary>
        public int BandTotal =>
            One + Two + ThreeToFive + SixToTen + ElevenToTwentyFive + MoreThanTwentyFive;
    }
}
