namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// One dimension's reading, in the generic form the Pareto comparator works over.
    /// </summary>
    /// <remarks>
    /// <strong>Internal on purpose.</strong> The comparator has to be written once, over a dimension it
    /// knows nothing about, so that no dimension acquires hidden precedence and so that its
    /// conflicting-dimension and no-common-dimension branches are directly testable. But the public
    /// read model must not be able to express a direction reading of
    /// <see cref="LessSupportive"/> — a state the frozen direction dimension has no meaning for — so the
    /// generic level stays here and the public types are typed separately.
    /// <para>
    /// The order is the comparison order: <see cref="Supportive"/> beats
    /// <see cref="LessSupportive"/>, and <see cref="Unavailable"/> participates in nothing.
    /// </para>
    /// </remarks>
    internal enum Phase7SupportLevel
    {
        /// <summary>
        /// The dimension has nothing to say about this candidate. Excluded from the comparison rather
        /// than counted against it.
        /// </summary>
        Unavailable,

        /// <summary>The dimension is available and its reading is unfavourable.</summary>
        LessSupportive,

        /// <summary>The dimension is available and its reading is favourable.</summary>
        Supportive,
    }
}
