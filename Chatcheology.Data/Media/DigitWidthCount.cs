namespace Chatcheology.Data.Media
{
    /// <summary>How many numeric observations were seen at one digit width.</summary>
    /// <remarks>
    /// The bounds are kept as digit strings rather than parsed numbers. Within a single width an
    /// ordinal string comparison and a numeric comparison give the same order, and keeping the text
    /// means a suffix of any length can be reported without a parse that could overflow.
    /// </remarks>
    public sealed class DigitWidthCount
    {
        /// <summary>The number of digits, leading zeroes included.</summary>
        public required int Width { get; init; }

        /// <summary>Physical files observed at this width.</summary>
        public required int FileCount { get; init; }

        /// <summary>The lowest digit string seen at this width, ordinally.</summary>
        public required string MinimumDigitString { get; init; }

        /// <summary>The highest digit string seen at this width, ordinally.</summary>
        public required string MaximumDigitString { get; init; }
    }
}
