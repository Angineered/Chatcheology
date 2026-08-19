namespace Chatcheology.Data.Media
{
    /// <summary>
    /// How assets carrying several purely numeric suffix observations divide between the three
    /// agreement outcomes.
    /// </summary>
    /// <remarks>
    /// Only assets with more than one observation are counted here: a single observation cannot
    /// agree or disagree with anything.
    /// <para>
    /// An asset lands in exactly one outcome, by precedence — all digit strings equal, else all
    /// values equal, else different values. An asset in
    /// <see cref="DifferentNumericValue"/> may also carry width differences, which is why the raw
    /// distinct-count distributions are reported beside this.
    /// </para>
    /// </remarks>
    public sealed class NumericAgreementCounts
    {
        /// <summary>Assets whose observations are all the same digit string.</summary>
        public required int ExactSameDigitString { get; init; }

        /// <summary>
        /// Assets whose observations share one numeric value across at least two different digit
        /// strings.
        /// </summary>
        public required int SameValueDifferentWidth { get; init; }

        /// <summary>Assets carrying at least two different numeric values.</summary>
        public required int DifferentNumericValue { get; init; }

        /// <summary>Assets described here.</summary>
        public int Total =>
            ExactSameDigitString + SameValueDifferentWidth + DifferentNumericValue;
    }
}
