namespace Chatcheology.Data.Media
{
    /// <summary>The spread of digit widths across one population of numeric observations.</summary>
    /// <remarks>
    /// Whether one width dominates, and by how much, is the question a grammar decision rests on. A
    /// single width across the whole archive supports a fixed-width rule; several widths mean any
    /// rule has to say what it does with each of them.
    /// </remarks>
    public sealed class DigitWidthDistribution
    {
        /// <summary>Each observed width, ascending.</summary>
        public required IReadOnlyList<DigitWidthCount> Widths { get; init; }

        /// <summary>Observations counted here.</summary>
        public required int TotalObservations { get; init; }

        /// <summary>The most common width, or zero when there were no observations.</summary>
        /// <remarks>
        /// Ties are broken by the smaller width, so the value is deterministic rather than
        /// dependent on enumeration order.
        /// </remarks>
        public required int DominantWidth { get; init; }

        /// <summary>Observations at <see cref="DominantWidth"/>.</summary>
        public required int DominantWidthCount { get; init; }

        /// <summary>Distinct digit strings seen, leading zeroes making a difference.</summary>
        public required int DistinctDigitStrings { get; init; }
    }
}
