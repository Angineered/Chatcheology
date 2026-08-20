namespace Chatcheology.Data.Sequence
{
    /// <summary>Groups banded by their exact weighted assignment count.</summary>
    /// <remarks>
    /// Secondary descriptive output. The count itself is dominated by how many token positions a group
    /// holds relative to its message count, which the slack distribution already reports; these bands
    /// exist so the magnitude is on record, not as a decision metric.
    /// </remarks>
    public sealed class AssignmentCountBandCounts
    {
        public required int Zero { get; init; }

        public required int One { get; init; }

        public required int TwoToTen { get; init; }

        public required int ElevenToOneHundred { get; init; }

        public required int OneHundredOneToOneThousand { get; init; }

        public required int OneThousandOneToOneMillion { get; init; }

        public required int MoreThanOneMillion { get; init; }

        public int Total =>
            Zero + One + TwoToTen + ElevenToOneHundred + OneHundredOneToOneThousand
            + OneThousandOneToOneMillion + MoreThanOneMillion;
    }
}
