namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// The kind of independently justified individual basis on which a human confirmed a resolution.
    /// </summary>
    /// <remarks>
    /// A closed vocabulary of exactly two values, and <strong>there is deliberately no
    /// <c>Other</c></strong>. The Phase 6 closure record requires any individual attachment to remain
    /// unresolved unless Phase 7 has an independently justified basis that does not merely depend on
    /// Phase 6 evidence, so there must be no value here — and no escape hatch — that could express
    /// "the ranking was convincing". Neither member can.
    /// <para>
    /// Adding a third value would be an explicit reviewed design and schema change, never an
    /// implementation convenience: the column's <c>CHECK</c> constrains the stored text to these two.
    /// </para>
    /// </remarks>
    public enum Phase7IndependentBasisType
    {
        /// <summary>
        /// A person examined the actual content, or context available outside the ranked evidence, and
        /// can state why this specific asset is this specific attachment in terms that do not reduce to
        /// "the evidence pointed here".
        /// </summary>
        HumanContentOrContextInspection,

        /// <summary>
        /// An external record, not derived from this workspace's Phase 6 evidence, establishes the
        /// specific correspondence.
        /// </summary>
        /// <remarks>
        /// Because such a record is by construction independent of the exact-date candidate
        /// computation, a target established this way legitimately need not appear in any run's
        /// candidate set.
        /// </remarks>
        ExternalProvenanceOrGroundTruth,
    }
}
