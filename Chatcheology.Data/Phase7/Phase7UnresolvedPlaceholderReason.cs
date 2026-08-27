namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// Why an unresolved attachment has no asset, as the minimal reconstruction read model reports it.
    /// </summary>
    /// <remarks>
    /// Exactly eight values, carried forward from the frozen design with the one adjacency addition,
    /// and there is no ninth. None of them is a score, a rank, a confidence or a judgement: each names
    /// a fact the workspace already records, or the absence of one.
    /// <para>
    /// The reconstruction model reports one of these as a headline, chosen by an explicit precedence,
    /// and separately exposes every orthogonal fact the precedence had to choose between — so
    /// selecting a headline never erases information.
    /// </para>
    /// </remarks>
    public enum Phase7UnresolvedPlaceholderReason
    {
        /// <summary>
        /// No completed run has ever evaluated this attachment.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="NoCandidates"/>, and the distinction is load-bearing: an absent
        /// evidence snapshot row is a fact about a run's scope, and a present row recording zero
        /// candidates is a recorded finding about the attachment. Nothing converts one into the other.
        /// </remarks>
        NeverEvaluated,

        /// <summary>
        /// Evaluated, with no exact-date candidate and no adjacent-date context either.
        /// </summary>
        NoCandidates,

        /// <summary>
        /// Evaluated, with no exact-date candidate, but adjacent-date context does exist.
        /// </summary>
        /// <remarks>
        /// The adjacent band is contextual, investigative evidence. It may be displayed as context and
        /// is <strong>never</strong> promoted into a candidate set, so this is deliberately not
        /// reported as a candidate-bearing state.
        /// </remarks>
        NoExactDateCandidates,

        /// <summary>
        /// At least one candidate, and the frozen partial order does not discriminate between them.
        /// </summary>
        /// <remarks>
        /// A single candidate reads as unranked, which is the honest reading: one candidate is not
        /// evidence of anything, and calling it ranked would invite exactly the inference the project
        /// rule forbids.
        /// </remarks>
        CandidateSetUnranked,

        /// <summary>
        /// At least one candidate, and the frozen partial order does discriminate — more than one tier.
        /// </summary>
        /// <remarks>
        /// A tier index carries no magnitude and no identity claim. That some candidate sits in a
        /// better tier than another is not a reason to resolve anything.
        /// </remarks>
        CandidateSetRanked,

        /// <summary>
        /// The attachment's message belongs to a group the within-direction hypothesis cannot satisfy.
        /// </summary>
        /// <remarks>
        /// Reported ahead of candidate-set shape because the closure record lists impossible groups as
        /// their own must-remain-unresolved cause, and describing such an attachment merely by its
        /// candidate set would hide that.
        /// </remarks>
        ImpossibleGroupFlagged,

        /// <summary>
        /// A human reviewed this attachment's current snapshot and found the evidence insufficient.
        /// </summary>
        ReviewedInsufficientEvidence,

        /// <summary>
        /// Every candidate of the attachment's current run carries an independent disproof.
        /// </summary>
        /// <remarks>
        /// <strong>Derived, never stored.</strong> Computed against the current run's own candidate
        /// identities, and false when that run holds no candidate at all — an attachment nobody has
        /// ruled anything out for is not one where everything has been ruled out.
        /// </remarks>
        ReviewedAllCandidatesIndependentlyDisproven,
    }
}
