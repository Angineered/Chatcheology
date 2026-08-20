namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C4 — how far apart the two tokens of an observation sit.
    /// </summary>
    /// <remarks>
    /// Descriptive only. The conversation does not contain every media occurrence the recovered
    /// sequence represents — other conversations, captioned media that produced no attachment row
    /// under the current backfill rule, incomplete recovery, repeated occurrences of one payload —
    /// so a displacement greater than one is expected and counts nothing.
    /// <para>
    /// Discordant magnitudes are banded as well as concordant ones. Disagreement concentrated at one
    /// or two would say something quite different from disagreement spread across the range, and a
    /// single discordant count cannot show which it is.
    /// </para>
    /// </remarks>
    public sealed class TokenDisplacementCensus
    {
        /// <summary>Positive differences, later message's token minus the earlier one's.</summary>
        public required SequenceBandCounts Concordant { get; init; }

        /// <summary>The same differences where they are negative, banded by magnitude.</summary>
        public required SequenceBandCounts Discordant { get; init; }
    }
}
