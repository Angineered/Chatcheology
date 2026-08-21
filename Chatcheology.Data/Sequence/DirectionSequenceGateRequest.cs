using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>What one direction-sequence gate census is asked to look at.</summary>
    /// <remarks>
    /// Everything the gate could otherwise guess is required here instead. The conversation and the
    /// local participant decide every message-side direction symbol, and the device grouping decides
    /// what a numbering scope is; a census that inferred either would measure a different population
    /// while looking identical to one that did not.
    /// <para>
    /// <see cref="DeviceGroups"/> is a list rather than a dictionary for the same reason the earlier
    /// sequence-scope census made that choice: a dictionary cannot hold one source twice, so a
    /// duplicate assignment would be lost at construction instead of refused.
    /// </para>
    /// <para>
    /// There is deliberately no candidate, asset, first-pass or Stage B2B input. The gate tests
    /// whether the archive carries an independent direction-order signal, and an input carrying an
    /// assignment outcome would make that test circular.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceGateRequest
    {
        /// <summary>
        /// An existing workspace at the current schema version, opened read-only. Never created,
        /// never migrated, never written.
        /// </summary>
        public required string DatabasePath { get; init; }

        /// <summary>The conversation whose unresolved attachments form the message side.</summary>
        public required long ConversationID { get; init; }

        /// <summary>
        /// Which participant of that conversation is the local, exporting user.
        /// </summary>
        /// <remarks>
        /// Not nullable. Without it every message direction is unknown, and a gate over a
        /// direction-free message side would measure nothing while reporting a full population.
        /// </remarks>
        public required long LocalParticipantID { get; init; }

        /// <summary>
        /// Which device group each source belongs to. Must name every source in the workspace exactly
        /// once and name nothing else.
        /// </summary>
        public required IReadOnlyList<DeviceGroupAssignment> DeviceGroups { get; init; }

        /// <summary>
        /// The preserved Stage A coverage figures to reconcile the gate's own recount against, or
        /// <see langword="null"/> to run without that reconciliation.
        /// </summary>
        /// <remarks>
        /// When supplied it must name every source in the workspace exactly once, and every figure
        /// must match what the gate counts. A mismatch means the grammar or the population has moved
        /// since Stage A, which is a provenance failure rather than something to report and continue
        /// through.
        /// </remarks>
        public IReadOnlyList<StageATokenCoverageDeclaration>? StageATokenCoverage { get; init; }
    }
}
