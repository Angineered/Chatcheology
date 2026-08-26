namespace Chatcheology.Data.Phase7
{
    // The four evidence rows Stage 7.1 writes, held as value types while the payload is built.
    //
    // readonly record struct deliberately, and not as a style preference: the payload for a real
    // conversation holds order 10^5 candidate rows and a small multiple of that in occurrence rows,
    // and value types are what keep the whole in-memory payload at tens of megabytes rather than
    // several hundred.
    //
    // No row carries a name, a path, an extension, a source identity, a rank, a score, a weight, a
    // confidence or any per-group or per-population count. An occurrence row holds four derived
    // facts and two references, and the arrangement count that chose its annotation is discarded.

    /// <summary>
    /// One <c>AttachmentEvidenceSnapshot</c> row: a coverage record for one attachment in one run.
    /// </summary>
    /// <remarks>
    /// Every in-scope attachment gets exactly one of these, whatever its evidence looks like. Absence
    /// of a row means the attachment was <em>not evaluated</em>; a zero count means it was evaluated
    /// and the answer was zero. The frozen design is explicit that those are different facts and must
    /// not look alike.
    /// <para>
    /// It carries no rank, no score, no "best candidate", no confidence and no resolution hint. The
    /// three group-shape fields are explanatory context and are read by nothing that ranks.
    /// </para>
    /// </remarks>
    /// <param name="AttachmentID">The attachment this row is about.</param>
    /// <param name="ExactDateCandidateCount">
    /// How many exact-date candidates it had — equal to the number of <c>AttachmentCandidate</c> rows
    /// written for it, which reconciliation checks.
    /// </param>
    /// <param name="AdjacentDateContextCount">
    /// How many adjacent-date context <em>rows</em> were written for it — rows, not distinct assets,
    /// because an asset supported on both adjacent days legitimately yields two.
    /// </param>
    /// <param name="HasAnyCompatibleAnnotatedCandidate">
    /// Whether any exact-date candidate is annotated <c>Compatible</c>.
    /// </param>
    /// <param name="HasAnyContradictoryOnlyAnnotatedCandidate">
    /// Whether any exact-date candidate is annotated <c>ContradictoryOnly</c>. Recorded, never acted
    /// on: the closure attributes those relations to folder structure rather than to evidence, and
    /// nothing may treat one as an exclusion.
    /// </param>
    /// <param name="IsWithinDirectionImpossibleGroup">
    /// Whether the attachment belongs to a group whose feasibility class is not
    /// <c>EnoughTokenPositions</c>. <c>0</c> therefore means "not known to belong to an impossible
    /// group", which includes every out-of-scope case.
    /// </param>
    /// <param name="IsForcedPositionGroupShape">
    /// Whether its group is feasible with <c>T = M</c>. A separate group-level fact, not a restatement
    /// of <c>UniquelyConstrained</c>.
    /// </param>
    /// <param name="RemainingCompatiblePositionCountUnderHypothesis">
    /// The width of its admissible window, <c>T - M + 1</c>, when its group is in scope and feasible;
    /// <see langword="null"/> otherwise. Explanatory only — no smaller-is-better rule exists anywhere
    /// in Stage 7.1.
    /// </param>
    internal readonly record struct Phase7SnapshotRow(
        long AttachmentID,
        int ExactDateCandidateCount,
        int AdjacentDateContextCount,
        bool HasAnyCompatibleAnnotatedCandidate,
        bool HasAnyContradictoryOnlyAnnotatedCandidate,
        bool IsWithinDirectionImpossibleGroup,
        bool IsForcedPositionGroupShape,
        int? RemainingCompatiblePositionCountUnderHypothesis);

    /// <summary>
    /// One <c>AttachmentCandidate</c> row: one exact-date candidate relation.
    /// </summary>
    /// <remarks>
    /// Exact date only. Adjacent-date evidence is a weaker, separately modelled context and is never
    /// promoted into this table, however empty this table is for an attachment. There is no
    /// candidate-band column to set and no column anywhere that could record a weaker band.
    /// </remarks>
    /// <param name="AttachmentID">The attachment.</param>
    /// <param name="MediaAssetID">The candidate payload.</param>
    /// <param name="DirectionCompatibility">
    /// The frozen candidate-level direction aggregate, persisted verbatim.
    /// </param>
    /// <param name="ExpectedOccurrenceCount">
    /// The frozen analysis's <c>SupportingPhysicalCopyCount</c> for this relation. Not persisted: it
    /// is carried so the number of occurrence rows written can be proved against it, per candidate,
    /// in memory and again set-based inside the publication transaction.
    /// </param>
    internal readonly record struct Phase7CandidateRow(
        long AttachmentID,
        long MediaAssetID,
        Matching.DirectionCompatibility DirectionCompatibility,
        int ExpectedOccurrenceCount);

    /// <summary>
    /// One <c>AttachmentCandidateOccurrenceEvidence</c> row: sequence evidence at the scope Phase 6
    /// actually established it, one physical file rather than one deduplicated payload.
    /// </summary>
    /// <param name="CandidateIndex">
    /// The zero-based index of this row's candidate in the payload's candidate list.
    /// <para>
    /// The parent's <c>AttachmentCandidateID</c> does not exist yet — SQLite assigns it during the
    /// publication transaction, while the payload is built before it. The writer keeps the identifiers
    /// each candidate insert returns and resolves index to identifier as it writes these rows, so no
    /// per-candidate <c>SELECT</c> is ever issued.
    /// </para>
    /// </param>
    /// <param name="MediaFileID">The physical copy, stored as a plain reference.</param>
    /// <param name="SequenceToken">
    /// The approved four-digit token, or <see langword="null"/> where the grammar is unsupported.
    /// </param>
    /// <param name="DateTokenKeyUniqueToPayload">
    /// Whether this occurrence's <c>(FileDate, SequenceToken)</c> key maps to exactly one payload
    /// archive-wide, or <see langword="null"/> where the grammar is unsupported. An occurrence fact
    /// and an availability gate, never positive ranking evidence.
    /// </param>
    /// <param name="OrderConsistencyAnnotation">This occurrence's order reading.</param>
    internal readonly record struct Phase7OccurrenceRow(
        int CandidateIndex,
        long MediaFileID,
        ushort? SequenceToken,
        bool? DateTokenKeyUniqueToPayload,
        OrderConsistencyAnnotation OrderConsistencyAnnotation)
    {
        /// <summary>
        /// Whether the name yielded a supported token. Derived from
        /// <see cref="SequenceToken"/> rather than stored separately, so the two halves of the
        /// schema's biconditional cannot be written into disagreement.
        /// </summary>
        internal bool TokenGrammarSupported => SequenceToken is not null;
    }

    /// <summary>
    /// One <c>AttachmentAdjacentDateContext</c> row: an asset with no copy on the message's date but
    /// one on the day before or after.
    /// </summary>
    /// <remarks>
    /// Contextual only, and deliberately inert. The table has no evidence column and no ranking
    /// column at all, so there is nothing here a later ranking computation could read even by
    /// accident, and it has no occurrence children because occurrence rows reference a candidate and
    /// adjacent context is not a candidate.
    /// </remarks>
    /// <param name="AttachmentID">The attachment.</param>
    /// <param name="MediaAssetID">The neighbouring-day payload.</param>
    /// <param name="AdjacentOffsetDays">
    /// <c>-1</c> for the day before, <c>+1</c> for the day after. An asset supported on both yields
    /// two rows, because each day is an independent qualifying reason the asset is there at all.
    /// </param>
    internal readonly record struct Phase7AdjacentContextRow(
        long AttachmentID,
        long MediaAssetID,
        int AdjacentOffsetDays);
}
