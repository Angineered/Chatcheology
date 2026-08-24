namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// One cell of a predeclared coarse composition band, or the fact that it was suppressed.
    /// </summary>
    /// <remarks>
    /// The bands these cells sit in are declared before any outcome is visible and are exact unions of
    /// the frozen descriptive bands, so no cell edge can be chosen after seeing what fell where.
    /// <para>
    /// A small cell is suppressed rather than printed. Individually harmless per-pair shape data
    /// reconstructs a short private direction pattern when combined — a length of three with two outgoing
    /// and one incoming in three runs is uniquely one pattern — and a cell holding one or two pairs is
    /// that combination in table form. <see cref="PairCount"/> is zero on a suppressed cell, and the
    /// suppression is counted so the report can say how much it withheld.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceCoarseCell
    {
        /// <summary>Which composition dimension this cell belongs to.</summary>
        public required string Dimension { get; init; }

        /// <summary>The predeclared band.</summary>
        public required string Band { get; init; }

        /// <summary>Pairs in it, or zero where it was suppressed.</summary>
        public required int PairCount { get; init; }

        /// <summary>Whether the cell was withheld for holding too few pairs to print.</summary>
        public required bool Suppressed { get; init; }
    }
}
