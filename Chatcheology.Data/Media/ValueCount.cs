namespace Chatcheology.Data.Media
{
    /// <summary>How often one exact value occurred.</summary>
    /// <remarks>
    /// Used where banding would decide something the census is supposed to measure: the distribution
    /// of observed minima, and the signed difference between a group's distinct asset count and its
    /// distinct token count. That difference is two-sided, and inventing bands over it would choose in
    /// advance which side mattered.
    /// <para>
    /// Reported in ascending numeric order of <see cref="Value"/>, so two runs are comparable.
    /// </para>
    /// </remarks>
    public sealed class ValueCount
    {
        /// <summary>The value observed, which may be negative.</summary>
        public required int Value { get; init; }

        /// <summary>How many observations carried it.</summary>
        public required int Count { get; init; }
    }
}
