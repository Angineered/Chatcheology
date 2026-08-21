namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C0 — one acquisition source's token-side population, coverage and direction capability.
    /// </summary>
    /// <remarks>
    /// Reported under stable identifiers only. A source's display name and root path are the caller's
    /// own text and neither belongs in permanent evidence; <see cref="MediaSourceID"/> and
    /// <see cref="DeviceGroupID"/> carry no private identity and are safe to keep.
    /// <para>
    /// Date coverage appears as counts and a range. That is token-side information carrying no
    /// message-side shape, and it is the one place a real date is permitted in the gate's output.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceSourcePopulation
    {
        /// <summary>The source described.</summary>
        public required long MediaSourceID { get; init; }

        /// <summary>The device group the caller assigned it to.</summary>
        public required long DeviceGroupID { get; init; }

        /// <summary>Every physical media row recovered from it.</summary>
        public required int PhysicalObservationCount { get; init; }

        /// <summary>How many of those carry a naming-derived calendar date.</summary>
        public required int DatedObservationCount { get; init; }

        /// <summary>How many carry the approved four-digit token under the frozen grammar.</summary>
        public required int SupportedTokenObservationCount { get; init; }

        /// <summary>How many supported observations also carry usable folder direction evidence.</summary>
        public required int DirectionCapableObservationCount { get; init; }

        /// <summary>
        /// Supported observations whose <c>IsSent</c> is null, which emit no symbol.
        /// </summary>
        /// <remarks>
        /// Direction-coverage loss, treated exactly like grammar-coverage loss: a real token position
        /// deleted from the middle of a sequence, never coerced to incoming.
        /// </remarks>
        public required int SupportedObservationsWithoutDirectionCount { get; init; }

        /// <summary>
        /// Whether any row of this source records folder direction at all.
        /// </summary>
        /// <remarks>
        /// A source that records none cannot emit a direction symbol, so it contributes nothing to any
        /// direction sequence however many files it holds. Its exclusion is reported rather than left
        /// to be inferred from a low count.
        /// </remarks>
        public required bool RecordsAnyDirection { get; init; }

        /// <summary>Distinct local dates its supported observations cover.</summary>
        public required int DistinctSupportedDateCount { get; init; }

        /// <summary>The earliest of those dates, or null when it has none.</summary>
        public required DateOnly? EarliestSupportedDate { get; init; }

        /// <summary>The latest of those dates, or null when it has none.</summary>
        public required DateOnly? LatestSupportedDate { get; init; }

        /// <summary>
        /// Logical <c>(date, token)</c> positions before equivalent-position collapse, which is one per
        /// supported observation.
        /// </summary>
        public required int LogicalPositionsBeforeCollapse { get; init; }

        /// <summary>Distinct <c>(date, token)</c> positions after collapse within this source.</summary>
        public required int LogicalPositionsAfterCollapse { get; init; }

        /// <summary>Collapsed positions carrying a usable direction symbol.</summary>
        public required int DirectionLabelledLogicalPositionCount { get; init; }

        /// <summary>Collapsed positions dropped because no copy of them records direction.</summary>
        public required int LogicalPositionsWithoutDirectionCount { get; init; }

        /// <summary>
        /// Collapsed positions whose own copies disagree about direction within this one source.
        /// </summary>
        public required int ConflictingLogicalPositionCount { get; init; }

        /// <summary>
        /// Positions this source shares with at least one other source, keyed on <c>(date, token)</c>.
        /// </summary>
        public required int SharedLogicalPositionCount { get; init; }

        /// <summary>Positions no other source observed.</summary>
        public required int SourceOnlyLogicalPositionCount { get; init; }

        /// <summary>The preserved Stage A physical count the caller declared, or null when none was.</summary>
        public required int? DeclaredStageAPhysicalObservationCount { get; init; }

        /// <summary>
        /// The preserved Stage A supported-token count the caller declared, or null when none was.
        /// </summary>
        public required int? DeclaredStageASupportedTokenObservationCount { get; init; }
    }
}
