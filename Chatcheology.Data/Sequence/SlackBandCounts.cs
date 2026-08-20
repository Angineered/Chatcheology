namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// Counts banded by <c>SequenceSlack = T - M</c>, the leading Stage B2B measurement.
    /// </summary>
    /// <remarks>
    /// A negative band is not an error state: it says the recovered supported sequence evidence cannot
    /// give every unresolved media message in that group one distinct increasing token position. Zero
    /// means every message has one forced token position. Everything above zero is how much positional
    /// freedom each message has before asset multiplicity at those positions is considered.
    /// </remarks>
    public sealed class SlackBandCounts
    {
        /// <summary>Fewer token positions than messages.</summary>
        public required int Negative { get; init; }

        /// <summary>Exactly as many token positions as messages.</summary>
        public required int Zero { get; init; }

        public required int One { get; init; }

        public required int Two { get; init; }

        public required int ThreeToFive { get; init; }

        public required int SixToTen { get; init; }

        public required int ElevenToTwentyFive { get; init; }

        public required int MoreThanTwentyFive { get; init; }

        public int Total =>
            Negative + Zero + One + Two + ThreeToFive + SixToTen + ElevenToTwentyFive
            + MoreThanTwentyFive;
    }
}
