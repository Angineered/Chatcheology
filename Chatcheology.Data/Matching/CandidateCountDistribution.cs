namespace Chatcheology.Data.Matching
{
    /// <summary>
    /// How many attachments fell into each candidate-count band.
    /// </summary>
    /// <remarks>
    /// Bands rather than a mean, because the shape is the question. An average of four candidates
    /// per attachment would hide both the attachments with exactly one and the attachments with two
    /// hundred, and those two populations are the whole point of the census.
    /// <para>
    /// The bands are contiguous and cover every count, so the members always sum to the number of
    /// attachments analysed. They are a way of reading a distribution, not a ranking: an attachment
    /// in <see cref="One"/> is not closer to being resolved than one in <see cref="MoreThanFifty"/>.
    /// </para>
    /// </remarks>
    public sealed class CandidateCountDistribution
    {
        /// <summary>Attachments with no candidate at all.</summary>
        public required int Zero { get; init; }

        /// <summary>Attachments with exactly one candidate.</summary>
        public required int One { get; init; }

        /// <summary>Attachments with exactly two candidates.</summary>
        public required int Two { get; init; }

        /// <summary>Attachments with three to five candidates.</summary>
        public required int ThreeToFive { get; init; }

        /// <summary>Attachments with six to ten candidates.</summary>
        public required int SixToTen { get; init; }

        /// <summary>Attachments with eleven to twenty-five candidates.</summary>
        public required int ElevenToTwentyFive { get; init; }

        /// <summary>Attachments with twenty-six to fifty candidates.</summary>
        public required int TwentySixToFifty { get; init; }

        /// <summary>Attachments with more than fifty candidates.</summary>
        public required int MoreThanFifty { get; init; }

        /// <summary>Every band added together, which is the attachments this describes.</summary>
        public int Total =>
            Zero + One + Two + ThreeToFive + SixToTen + ElevenToTwentyFive + TwentySixToFifty
            + MoreThanFifty;
    }
}
