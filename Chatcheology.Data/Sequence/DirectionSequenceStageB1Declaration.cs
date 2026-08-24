using Chatcheology.Data.Media;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// The preserved Stage B1 facts the alignment stage rests on, declared by the caller so they are
    /// carried forward rather than re-derived.
    /// </summary>
    /// <remarks>
    /// Stage B2C-1 tests a conjunction, and one member of it — that the supported four-digit token is a
    /// meaningful local ordering signal at the selected scope — was established at Stage B1 and cannot be
    /// re-established here: every figure this stage produces is downstream of it. Declaring it is what
    /// keeps that dependency visible instead of implicit.
    /// <para>
    /// Two kinds of member. <see cref="DistinctLogicalPositionCount"/> and
    /// <see cref="SupportedTokenDigitWidth"/> are reconciled: the gate recounts them, and a declaration
    /// that disagrees means the population or the grammar has moved since Stage B1.
    /// <see cref="SupportedTokenIsMonotoneWithinLocalDate"/> and
    /// <see cref="PositionsWithMoreThanOneAssetCount"/> are carried forward and cannot be recomputed
    /// here — the first is the ordering authority this whole stage sits downstream of, and the second is
    /// an asset-identity finding the gate has no reason to track. Both are stated assumptions the report
    /// must carry as such.
    /// </para>
    /// <para>
    /// Cross-source overlap is deliberately <b>not</b> here. Whether a logical position was observed by
    /// one source or several is a quantity the gate recomputes for its own cross-source and scope work,
    /// not a Stage B1 finding: Stage B1 established the <c>(date, token)</c> key universe and its asset
    /// cardinality, and said nothing about how those keys distribute across acquisition stores. An
    /// earlier draft required it as provenance, which was too broad — see the Stage B2C-1 review
    /// correction note.
    /// </para>
    /// <para>
    /// Counts and identifiers only. No display name, root path, file name or date appears here.
    /// </para>
    /// </remarks>
    public sealed record DirectionSequenceStageB1Declaration
    {
        /// <summary>
        /// The Stage B1 finding that within one local date the supported token orders the archive's
        /// media monotonically at the declared scope.
        /// </summary>
        /// <remarks>
        /// Not reconcilable against the workspace, and deliberately not defaulted. A run declaring
        /// <see langword="false"/> is refused: the alignment question is meaningless without it, and a
        /// stage that continued anyway would report an order result whose ordering authority it had
        /// itself denied.
        /// </remarks>
        public required bool SupportedTokenIsMonotoneWithinLocalDate { get; init; }

        /// <summary>The approved token width, which the gate's own grammar must agree with.</summary>
        public required int SupportedTokenDigitWidth { get; init; }

        /// <summary>Distinct <c>(date, token)</c> logical positions Stage B1 counted archive-wide.</summary>
        /// <remarks>
        /// Reconciled: the gate recounts the same universe — supported four-digit tokens, keyed by date
        /// and token, across every source — so a disagreement means the key universe has moved since
        /// Stage B1.
        /// </remarks>
        public required int DistinctLogicalPositionCount { get; init; }

        /// <summary>
        /// Logical positions Stage B1 found mapping to more than one <c>MediaAsset</c>.
        /// </summary>
        /// <remarks>
        /// The collision half of the Stage B1 cardinality finding: a <c>(date, token)</c> key that names
        /// two payloads is a key doing two jobs, and how many of those exist bounds how far a token can
        /// be read as an occurrence identity.
        /// <para>
        /// Carried forward rather than recomputed. The gate reads asset identity only to prove a file
        /// and its asset agree about the payload, and giving it a reason to track which assets share a
        /// key would be adding a Stage B1 census to a stage that already has its own question. Checked
        /// here only for internal consistency — a collision count cannot be negative, and cannot exceed
        /// the number of positions it is counted over.
        /// </para>
        /// </remarks>
        public required int PositionsWithMoreThanOneAssetCount { get; init; }

        /// <summary>
        /// The preserved device grouping, which must equal the grouping the run is given.
        /// </summary>
        /// <remarks>
        /// Declared twice on purpose — here as provenance and on the request as the mapping actually
        /// applied — so a run using a grouping Stage B1 did not establish is refused rather than
        /// silently measuring a different numbering authority.
        /// </remarks>
        public required IReadOnlyList<DeviceGroupAssignment> DeviceGroups { get; init; }

        /// <summary>
        /// The sources Stage B1 found record no folder direction at all, in identifier order.
        /// </summary>
        public required IReadOnlyList<long> DirectionBlindMediaSourceIDs { get; init; }
    }
}
