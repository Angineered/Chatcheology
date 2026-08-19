namespace Chatcheology.Data.Media
{
    /// <summary>One joint distribution over two banded or flagged quantities.</summary>
    /// <remarks>
    /// Four marginal histograms cannot answer whether a group's token range tracks its population, so
    /// the relationships are reported as cross-tabs rather than as separate distributions the reader
    /// would have to recombine by eye.
    /// <para>
    /// Cells are ordered by row then column, both as declared by the producing band set, so two runs
    /// over unchanged data produce byte-identical tables.
    /// </para>
    /// </remarks>
    public sealed class JointDistribution
    {
        /// <summary>What this cross-tab relates, as a fixed label.</summary>
        public required string Name { get; init; }

        /// <summary>The scope whose groups or keys are counted.</summary>
        public required ScopeLevel Scope { get; init; }

        /// <summary>Non-empty cells, ordered by row then column.</summary>
        public required IReadOnlyList<JointCell> Cells { get; init; }

        /// <summary>Every cell added together, which is the groups or keys described.</summary>
        public int Total => Cells.Sum(cell => cell.Count);
    }
}
