namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// What the direction dimension says about one candidate.
    /// </summary>
    /// <remarks>
    /// <strong>Two members, deliberately.</strong> The frozen direction dimension has exactly two
    /// readings — <c>Compatible</c> is available and positive, and <c>Mixed</c>, <c>Unknown</c> and
    /// <c>ContradictoryOnly</c> are all simply unavailable — so a third member would be a state Phase 7
    /// has no meaning for. Making it unrepresentable is stronger than rejecting it at run time and
    /// cannot be bypassed by a caller, a serializer or a later refactor.
    /// <para>
    /// <c>ContradictoryOnly</c> in particular is <strong>unavailable, never negative and never an
    /// exclusion</strong>. The Phase 6 closure attributes those relations to folder structure rather
    /// than to evidence and says in terms that nothing may act on one as an exclusion.
    /// </para>
    /// <para>
    /// A consequence worth stating, because it is easy to be surprised by later: under the frozen
    /// Pareto rule this dimension <strong>can never produce a strict preference</strong>. A dimension
    /// enters the comparison only when it is available for both candidates, and when it is, both are
    /// <see cref="Supportive"/> — equal. Every strict preference Phase 7 can express therefore comes
    /// from order support alone. That is an intentional conservative consequence of the frozen design,
    /// not a defect in it.
    /// </para>
    /// </remarks>
    public enum Phase7DirectionSupport
    {
        /// <summary>
        /// The dimension says nothing about this candidate, and is excluded from its comparisons.
        /// Never a mark against it.
        /// </summary>
        Unavailable,

        /// <summary>
        /// The dimension is available and positive for this candidate: at least one supporting copy
        /// agrees with the message's direction and none disagrees.
        /// </summary>
        /// <remarks>
        /// Available and positive <em>for this candidate</em>. It is not a claim that this candidate is
        /// supported over any other: under the frozen value set the most it can ever do against another
        /// available direction reading is tie with it.
        /// </remarks>
        Supportive,
    }
}
