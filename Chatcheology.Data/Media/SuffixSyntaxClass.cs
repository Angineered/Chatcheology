namespace Chatcheology.Data.Media
{
    /// <summary>
    /// What the characters after a WhatsApp-style <c>-YYYYMMDD-WA</c> marker look like, as syntax
    /// and nothing more.
    /// </summary>
    /// <remarks>
    /// These are shapes, not meanings. Nothing here says a numeric suffix is a sequence number, that
    /// a decorated one is a copy, or that a non-numeric one is broken: the committed parser stops at
    /// the marker and has never looked past it, so what follows is genuinely unmeasured. Naming a
    /// class after a guess would smuggle that guess into every count taken with it.
    /// <para>
    /// The four are mutually exclusive and cover every possible suffix, so the counts always sum to
    /// the files examined.
    /// </para>
    /// </remarks>
    public enum SuffixSyntaxClass
    {
        /// <summary>Nothing follows the marker once the extension is removed.</summary>
        EmptySuffix,

        /// <summary>One or more ASCII digits and nothing else.</summary>
        PurelyNumericSuffix,

        /// <summary>
        /// One or more ASCII digits followed by at least one further character of any kind.
        /// </summary>
        NumericPrefixWithTrailingDecoration,

        /// <summary>Non-empty, and does not begin with an ASCII digit.</summary>
        NonNumericSuffix,
    }
}
