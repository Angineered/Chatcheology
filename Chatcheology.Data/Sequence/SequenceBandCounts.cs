namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// A count of observations falling in each of the project's fixed size bands.
    /// </summary>
    /// <remarks>
    /// One band carrier serves the token-cardinality distribution, the displacement magnitudes and
    /// the direction-transition counts, because all three are "how many of these fell in that band"
    /// and a separate type per section would differ only in name.
    /// <para>
    /// The bands are the finest the brief asks for anywhere, so a section wanting a coarser grouping
    /// reads a computed total rather than being given its own type. Nothing is hidden by that: a
    /// coarser band is the sum of finer ones.
    /// </para>
    /// </remarks>
    public sealed class SequenceBandCounts
    {
        /// <summary>No observation at all, where a section counts that as a band.</summary>
        public required int Zero { get; init; }

        public required int One { get; init; }

        public required int Two { get; init; }

        public required int ThreeToFive { get; init; }

        public required int SixToTen { get; init; }

        public required int ElevenToTwentyFive { get; init; }

        public required int TwentySixToFifty { get; init; }

        public required int MoreThanFifty { get; init; }

        /// <summary>The three highest bands together, for sections banding at ten.</summary>
        public int MoreThanTen => ElevenToTwentyFive + TwentySixToFifty + MoreThanFifty;

        /// <summary>Every band, which is the population the bands were built from.</summary>
        public int Total =>
            Zero + One + Two + ThreeToFive + SixToTen + MoreThanTen;
    }
}
