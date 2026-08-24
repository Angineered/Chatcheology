using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>What one Stage B2C-1 alignment run is asked to do, with nothing left to be inferred.</summary>
    /// <remarks>
    /// The gate request this builds on makes its Stage A coverage optional, because a caller with no
    /// preserved figures to hand can still take a census and be told it was not reconciled. An alignment
    /// run cannot: it compares an observed order against a frozen reference, so a population that has
    /// drifted since Stage A would produce a comparison against the wrong thing. Here the declaration is
    /// required, and so are the Stage B1 provenance and the frozen gate state.
    /// <para>
    /// There is deliberately no candidate, asset, first-pass or Stage B2B member, and no member able to
    /// carry one. The stage asks whether the archive independently supports the monotone hypothesis, and
    /// an input carrying an assignment outcome would make that question circular.
    /// </para>
    /// <para>
    /// No threshold, cutoff, band or population size appears here either. Which pairs each statistic is
    /// computed over is frozen, and <see cref="FrozenGate"/> is where it is stated.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceAlignmentRequest
    {
        /// <summary>
        /// An existing workspace at the current schema version, opened read-only. Never created, never
        /// migrated, never written.
        /// </summary>
        public required string DatabasePath { get; init; }

        /// <summary>The conversation whose unresolved attachments form the message side.</summary>
        public required long ConversationID { get; init; }

        /// <summary>Which participant of that conversation is the local, exporting user.</summary>
        public required long LocalParticipantID { get; init; }

        /// <summary>Which device group each source belongs to.</summary>
        public required IReadOnlyList<DeviceGroupAssignment> DeviceGroups { get; init; }

        /// <summary>
        /// The preserved Stage A coverage figures. Required, unlike on the gate request.
        /// </summary>
        public required IReadOnlyList<StageATokenCoverageDeclaration> StageATokenCoverage { get; init; }

        /// <summary>The preserved Stage B1 sequence and provenance facts.</summary>
        public required DirectionSequenceStageB1Declaration StageB1 { get; init; }

        /// <summary>The accepted Stage B2C-0 gate state this run must reproduce first.</summary>
        public required DirectionSequenceFrozenGateDeclaration FrozenGate { get; init; }

        /// <summary>The gate request this alignment run reconciles against.</summary>
        internal DirectionSequenceGateRequest ToGateRequest() =>
            new()
            {
                DatabasePath = DatabasePath,
                ConversationID = ConversationID,
                LocalParticipantID = LocalParticipantID,
                DeviceGroups = DeviceGroups,
                StageATokenCoverage = StageATokenCoverage,
            };
    }
}
