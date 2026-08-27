namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// What a human recorded about one attachment's evidence, in one Stage 7.1 snapshot.
    /// </summary>
    /// <remarks>
    /// One value, deliberately. The frozen design removed
    /// <c>ReviewedAllCandidatesIndependentlyDisproven</c> as a storable outcome, because whether every
    /// candidate of a run has been individually ruled out is a question to ask of that run rather than
    /// an answer to keep: a later run's changed candidate set would make a stored answer quietly wrong.
    /// It survives only as a derived read and as a reconstruction placeholder reason.
    /// <para>
    /// The enum exists with one member rather than being omitted so that adding a genuinely different
    /// outcome later is an explicit reviewed change here <em>and</em> a reviewed schema migration —
    /// the column's <c>CHECK</c> constrains the stored text to this same single literal.
    /// </para>
    /// </remarks>
    public enum Phase7ReviewOutcome
    {
        /// <summary>
        /// The evidence was reviewed and found insufficient to identify this attachment's media.
        /// </summary>
        /// <remarks>
        /// The correct record for a reviewer who found the evidence persuasive but has no independent
        /// individual basis. It is not a resolution and it is not silence.
        /// </remarks>
        ReviewedInsufficientEvidence,
    }
}
