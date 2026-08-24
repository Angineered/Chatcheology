namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C6 — one primary result split by a band that existed before the run.
    /// </summary>
    /// <remarks>
    /// The primary result itself stays exactly on its frozen population; this is a view of it, and no
    /// figure here replaces one there. Two metrics are never fused into a combined confidence or
    /// validation score, and no cell of a split may become a new primary filter once an outcome is
    /// visible.
    /// </remarks>
    public sealed class DirectionSequenceDescriptiveSplit
    {
        /// <summary>Which pre-existing band set this split uses.</summary>
        public required string Dimension { get; init; }

        /// <summary>The population it splits.</summary>
        public required string PopulationName { get; init; }

        /// <summary>How many pairs that population holds.</summary>
        public required int PairCount { get; init; }

        /// <summary>The cells, in band order, including empty and suppressed ones.</summary>
        public required IReadOnlyList<DirectionSequenceDescriptiveCell> Cells { get; init; }

        /// <summary>Cells withheld for holding too few pairs to print.</summary>
        public required int SuppressedCellCount { get; init; }

        /// <summary>Pairs sitting in those withheld cells, so the split still reconciles.</summary>
        public required int SuppressedPairCount { get; init; }

        /// <summary>Printed pairs plus withheld pairs, which must be the population.</summary>
        public int AccountedPairCount =>
            Cells.Sum(cell => cell.PairCount) + SuppressedPairCount;
    }
}
