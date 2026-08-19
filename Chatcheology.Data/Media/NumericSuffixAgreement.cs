namespace Chatcheology.Data.Media
{
    /// <summary>
    /// How the purely numeric suffixes on one deduplicated asset's several file names relate.
    /// </summary>
    /// <remarks>
    /// Three outcomes rather than agree-or-disagree, because the middle case is a different finding
    /// from either extreme. A width convention that changed part way through an archive and a
    /// genuine numbering disagreement would both read as "conflict" under a binary, and the two
    /// would then be impossible to tell apart afterwards.
    /// <para>
    /// Nothing here is normalised into agreement. <c>0003</c> and <c>3</c> are recorded as the same
    /// value at different widths, not as a match.
    /// </para>
    /// </remarks>
    public enum NumericSuffixAgreement
    {
        /// <summary>Every observation is the same digit string, leading zeroes included.</summary>
        ExactSameDigitString,

        /// <summary>
        /// Every observation carries the same numeric value, but at least two digit strings differ.
        /// </summary>
        SameValueDifferentWidth,

        /// <summary>At least two observations carry different numeric values.</summary>
        DifferentNumericValue,
    }
}
