namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// The dimensions the frozen comparison rule is defined over. Exactly two.
    /// </summary>
    /// <remarks>
    /// The comparator loops over these values rather than testing two hard-coded conditions, so no
    /// dimension has hidden precedence and adding a third would be a compile-time-visible change to a
    /// reviewed design rather than a quiet one.
    /// <para>
    /// There is deliberately no dimension for token uniqueness, for the remaining-position count, for
    /// adjacency, for copy counts, for source counts, for media type or for size. Those facts are
    /// stored, some of them gate availability, and none of them is a reason to prefer one candidate
    /// over another.
    /// </para>
    /// </remarks>
    internal enum Phase7SupportDimension
    {
        /// <summary>
        /// The candidate-level direction aggregate — the one many-to-one aggregate that already had
        /// defined semantics before Phase 7 existed.
        /// </summary>
        Direction,

        /// <summary>
        /// The occurrence-coherent order reading, derived from the candidate's own occurrences only
        /// where they agree.
        /// </summary>
        OrderSupport,
    }
}
