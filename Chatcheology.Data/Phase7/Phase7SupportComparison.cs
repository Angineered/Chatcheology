namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// What the frozen comparison rule says about two candidates of one attachment.
    /// </summary>
    /// <remarks>
    /// A partial order, not a ranking. There is no third candidate implied, no magnitude, and no way to
    /// turn a sequence of these into a score.
    /// </remarks>
    public enum Phase7SupportComparison
    {
        /// <summary>
        /// Neither candidate is evidentially preferred to the other.
        /// </summary>
        /// <remarks>
        /// <strong>This covers three genuinely different situations, and does not distinguish them:</strong>
        /// the two candidates are equal on every dimension available to both; each wins on a different
        /// dimension, so the evidence conflicts; or no dimension is available to both, so there is
        /// nothing to compare. All three mean "same tier", which is faithful to the frozen model — but a
        /// caller must not read this value as "equal evidence", because two of the three are not that.
        /// </remarks>
        Incomparable,

        /// <summary>
        /// The left candidate is no worse than the right on every dimension available to both, and
        /// strictly better on at least one.
        /// </summary>
        LeftPreferred,

        /// <summary>The mirror image: the right candidate is preferred.</summary>
        RightPreferred,
    }
}
