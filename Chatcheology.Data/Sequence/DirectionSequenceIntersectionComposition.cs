namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// What an adjacent-date intersection is made of, against the population it was drawn from.
    /// </summary>
    /// <remarks>
    /// Descriptive selection context, and nothing else: it creates no eligibility rule and no cell here
    /// filters anything. Neighbour availability favours denser periods of the archive, so a correct-date
    /// figure restricted to an intersection can differ from the full-population figure for reasons that
    /// have nothing to do with which date the tokens came from. This is what lets a reader see that.
    /// <para>
    /// Both sides use the same predeclared coarse bands, so the comparison is like for like, and every
    /// cell is subject to the same suppression floor.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceIntersectionComposition
    {
        /// <summary>The frozen population's composition.</summary>
        public required IReadOnlyList<DirectionSequenceCoarseCell> FullPopulation { get; init; }

        /// <summary>The paired intersection's composition.</summary>
        public required IReadOnlyList<DirectionSequenceCoarseCell> Intersection { get; init; }

        /// <summary>Cells withheld across both sides for holding too few pairs to print.</summary>
        public required int SuppressedCellCount { get; init; }
    }
}
