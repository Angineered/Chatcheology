using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C7 — the same outcomes computed at a non-primary numbering scope, as labelled sensitivity.
    /// </summary>
    /// <remarks>
    /// <b>Not replication, and not corroboration.</b> The recovered sources substantially overlap as
    /// copies of one media library, so a second scope over the same files is not a second observation of
    /// the archive. This has its own population and its own totals and is never merged into a primary
    /// aggregate.
    /// <para>
    /// Near-identity with the primary result is expected in advance, and the expectation is stated before
    /// the run rather than discovered after it. Under the frozen grouping one device group's positions
    /// reduce to its single direction-capable source — the other source in it records no folder direction
    /// at all, and every position it holds is already represented — and the other group is a single source.
    /// So the direction sequences at the two scopes are largely the same sequences. If they match, that
    /// confirms the scope reduction behaved as documented; a material divergence is an integrity signal to
    /// investigate, not a finding.
    /// </para>
    /// <para>
    /// The overlap figures below are what make the dependence visible: a scope whose positions are mostly
    /// shared with another source is mostly not its own evidence.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceScopeSensitivityCensus
    {
        /// <summary>The non-primary scope described.</summary>
        public required ScopeLevel Scope { get; init; }

        /// <summary>Classified pairs at that scope, by its own classification.</summary>
        public required int ClassifiedPairCount { get; init; }

        /// <summary>The binary admission comparison at that scope, on its own informative population.</summary>
        public required DirectionSequenceBinaryFigures Binary { get; init; }

        /// <summary>The graded comparison at that scope, on its own informative population.</summary>
        public required DirectionSequenceGradedFigures Graded { get; init; }

        /// <summary>Logical positions archive-wide that more than one source observed.</summary>
        public required int SharedLogicalPositionCount { get; init; }

        /// <summary>Logical positions exactly one source observed.</summary>
        public required int SingleSourceLogicalPositionCount { get; init; }

        /// <summary>
        /// Whether this scope's result is expected to resemble the primary one, and why.
        /// </summary>
        /// <remarks>
        /// Carried in the census rather than left to the report, so the pre-registered expectation
        /// travels with the figures it applies to.
        /// </remarks>
        public required string PreRegisteredExpectation { get; init; }
    }
}
