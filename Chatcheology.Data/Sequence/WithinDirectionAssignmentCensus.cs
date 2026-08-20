using Chatcheology.Data.Matching;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// Everything one within-direction assignment census measured.
    /// </summary>
    /// <remarks>
    /// A hypothetical candidate-space census. Every feasibility and narrowing figure is conditional on
    /// one untested assumption — that within a <c>(date, direction)</c> group, message source order
    /// corresponds to strictly increasing recovered WA token order. Stage B2A was the stage that would
    /// have tested it and terminated with insufficient power, so nothing here is evidence that the
    /// assumption holds.
    /// <para>
    /// Measurements only: no threshold, no verdict, no resolution, no persistence. Nothing is written to
    /// <c>Attachment</c>, no candidate is ranked, and no asset becomes an anchor.
    /// </para>
    /// </remarks>
    public sealed class WithinDirectionAssignmentCensus
    {
        public required long ConversationID { get; init; }

        public required long LocalParticipantID { get; init; }

        /// <summary>
        /// The frozen first-pass census this analysis was built on, returned unchanged so the run
        /// harness can reconcile it without a second analysis.
        /// </summary>
        public required MatchAnalysisCensus MatchingCensus { get; init; }

        /// <summary>
        /// Unresolved attachments left out because they have no exact-date candidate at all.
        /// </summary>
        /// <remarks>
        /// The preserved first pass recorded exactly one, whose only evidence is a copy dated a day
        /// either side. It is reported rather than moved onto the adjacent date.
        /// </remarks>
        public required int ExcludedAdjacentDateOnlyAttachmentCount { get; init; }

        // C0.

        public required AssignmentGroupPopulation OutgoingPopulation { get; init; }

        public required AssignmentGroupPopulation IncomingPopulation { get; init; }

        public required AssignmentGroupPopulation PooledPopulation { get; init; }

        // C1.

        public required SequenceSlackDistribution OutgoingSlack { get; init; }

        public required SequenceSlackDistribution IncomingSlack { get; init; }

        public required SequenceSlackDistribution PooledSlack { get; init; }

        public required FeasibilityCounts Feasibility { get; init; }

        public required ForcedPositionCounts ForcedPositions { get; init; }

        // C2.

        public required TokenPositionAmbiguityCensus PositionAmbiguity { get; init; }

        // C3.

        public required CandidateNarrowingCensus OutgoingNarrowing { get; init; }

        public required CandidateNarrowingCensus IncomingNarrowing { get; init; }

        public required CandidateNarrowingCensus PooledNarrowing { get; init; }

        // C4 to C8.

        public required AssignmentCountCensus AssignmentCounts { get; init; }

        public required AssetTokenMultiplicityCensus AssetMultiplicity { get; init; }

        public required ImpossibleGroupCensus ImpossibleGroups { get; init; }

        public required SensitivityDecompositionCensus Sensitivity { get; init; }

        public required CollisionParticipationCensus Collisions { get; init; }
    }
}
