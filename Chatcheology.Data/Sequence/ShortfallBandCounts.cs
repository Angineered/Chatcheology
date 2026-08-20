namespace Chatcheology.Data.Sequence
{
    /// <summary>How far short of one position per message an impossible group falls.</summary>
    /// <remarks>
    /// Reported for groups where the hypothesis admits no assignment, so the size of the gap is visible
    /// rather than a single "impossible" count. A shortfall says nothing about why the evidence is
    /// missing.
    /// </remarks>
    public sealed class ShortfallBandCounts
    {
        public required int OneToFive { get; init; }

        public required int SixToTen { get; init; }

        public required int ElevenToTwentyFive { get; init; }

        public required int MoreThanTwentyFive { get; init; }

        public int Total => OneToFive + SixToTen + ElevenToTwentyFive + MoreThanTwentyFive;
    }
}
