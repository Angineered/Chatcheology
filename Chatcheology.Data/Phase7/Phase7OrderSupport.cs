namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// What the order dimension says about one candidate, derived from its occurrences only where they
    /// agree.
    /// </summary>
    /// <remarks>
    /// The one dimension that can produce a strict preference, and the reduction that produces it is
    /// deliberately unanimous rather than best-of. Take every supporting occurrence's stored
    /// annotation, drop the unavailable ones, and if what remains is unanimous that is the candidate's
    /// witnessed reading; if it disagrees the dimension is <see cref="Unavailable"/>.
    /// <para>
    /// Disagreement is never resolved in the favourable direction. Doing that would manufacture a
    /// profile no single physical copy witnesses — a value taken from one occurrence beside a value
    /// taken from another — which is exactly what the frozen design forbids. The disagreement itself
    /// stays fully visible in the stored per-occurrence rows.
    /// </para>
    /// <para>
    /// There is no maximum, no best, no majority, no any-positive rule and no weighting by how many
    /// copies survived. A payload surviving in five places is not more likely to be this attachment's
    /// media than one surviving in one place.
    /// </para>
    /// </remarks>
    public enum Phase7OrderSupport
    {
        /// <summary>
        /// The dimension says nothing about this candidate: every occurrence was unassessable, or the
        /// assessable ones disagreed. Excluded from its comparisons, and never a mark against it.
        /// </summary>
        Unavailable,

        /// <summary>
        /// Every assessable occurrence found no arrangement of its group placing this attachment's
        /// rank at its token position.
        /// </summary>
        /// <remarks>
        /// Genuinely weaker than order-compatible support, per the closure's own preference for an
        /// order-consistent arrangement over an order-inconsistent one — and still never an exclusion.
        /// The candidate remains a candidate, is never removed, and is never ranked last.
        /// </remarks>
        LessSupportive,

        /// <summary>
        /// Every assessable occurrence found at least one arrangement of its group placing this
        /// attachment's rank at its token position.
        /// </summary>
        /// <remarks>
        /// One value covers both <c>CompatibleWithAmbiguity</c> and <c>UniquelyConstrained</c>, because
        /// they carry <strong>identical</strong> ranking strength. Uniqueness earns no bonus: the
        /// closure is explicit that a unique embedding is not automatically a match and that treating a
        /// forced position as one would be reading arithmetic as evidence. The explanatory distinction
        /// between the two survives in storage, for audit and display, and never as ranking weight.
        /// </remarks>
        Supportive,
    }
}
