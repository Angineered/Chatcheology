namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// One acquisition source's preserved Stage A token-coverage figures, declared by the caller so
    /// the gate can prove it is measuring the same population Stage A measured.
    /// </summary>
    /// <remarks>
    /// Carried forward rather than re-derived. The gate necessarily recounts supported tokens — it
    /// cannot read a preserved report — so the only way for "carried forward" to mean anything is for
    /// the caller to state the preserved figures and for the recount to be refused when it disagrees.
    /// A silent recount would look identical whether the grammar had drifted or not.
    /// <para>
    /// Optional as a whole: a caller with no preserved figures to hand gets the recount and a census
    /// that says so. Supplying it is what turns the recount into a reconciliation.
    /// </para>
    /// <para>
    /// Counts only. No display name, root path or file name appears here.
    /// </para>
    /// </remarks>
    public sealed class StageATokenCoverageDeclaration
    {
        /// <summary>The source these preserved figures describe.</summary>
        public required long MediaSourceID { get; init; }

        /// <summary>How many physical media rows Stage A counted for it.</summary>
        public required int PhysicalObservationCount { get; init; }

        /// <summary>
        /// How many of those carried the approved four-digit token under the Stage A grammar.
        /// </summary>
        public required int SupportedTokenObservationCount { get; init; }
    }
}
