namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// One <c>(date, direction)</c> group's finished shape: its compatible candidate assets, how many
    /// in-scope attachments it holds, and the ordered token positions its members could occupy.
    /// </summary>
    /// <remarks>
    /// A narrow re-implementation of the only measured Stage B2B primary model, element for element:
    /// the group key, the exact-date restriction, the direction-compatible asset filter, the
    /// group-invariant assertion, the supported token positions with acquisition duplicates collapsed,
    /// <c>T</c>, the four feasibility classes in the same order, and the per-rank admissible window.
    /// The census <em>service</em> is never called: its output is bands, distributions and totals, and
    /// it exposes no per-attachment or per-occurrence value at all.
    /// <para>
    /// The direction-agreeing sensitivity arm (<c>BuildPositions(group, agreeingOnly: true)</c>) is
    /// deliberately not imported. It is a robustness check on a population figure, not a per-candidate
    /// rule.
    /// </para>
    /// <para>
    /// One difference from B2B, and only one: B2B <em>refuses</em> an <c>Unknown</c>-direction
    /// attachment that has exact-date candidates, because the preserved first pass records none.
    /// Stage 7.1 must be general, so such an attachment is simply out of the group model — it still
    /// receives its snapshot row, its candidates and its occurrence rows, and every occurrence reads
    /// <c>NotAssessable</c>.
    /// </para>
    /// <para>
    /// Nothing here is a ranking input. <see cref="IsForcedPositionShape"/> and
    /// <see cref="RemainingCompatiblePositionCount"/> reach the snapshot row as explanatory context
    /// and are read by nothing that ranks.
    /// </para>
    /// </remarks>
    internal sealed class Phase7GroupShape
    {
        /// <summary>Which date and direction this group is.</summary>
        internal required Phase7GroupKey Key { get; init; }

        /// <summary>
        /// The group's direction-compatible candidate assets, in <c>MediaAssetID</c> order.
        /// </summary>
        /// <remarks>
        /// The filter is inherited, not chosen: <c>GroupCollector.Add</c> builds
        /// <c>CompatibleAssets</c> from <c>DirectionCompatibility == Compatible</c> only, and that set
        /// is what <c>TokenEvidence</c>'s wanted keys are drawn from and what <c>BuildPositions</c>
        /// iterates. There is no position set in the committed model that includes a non-compatible
        /// candidate's tokens, and building one would be a population Phase 6 never measured.
        /// </remarks>
        internal required long[] CompatibleAssets { get; init; }

        /// <summary>
        /// <c>M</c> — how many in-scope attachments belong to this group.
        /// </summary>
        internal required int MemberCount { get; init; }

        /// <summary>
        /// The group's ordered, distinct token positions: every supported token carried by any copy of
        /// a compatible candidate asset dated to this group's date, ascending.
        /// </summary>
        /// <remarks>
        /// Two properties, both inherited rather than chosen. Acquisition duplication collapses,
        /// because a token is a key: one payload recovered into several stores at one position
        /// contributes one position, while a genuine repeat carries a different name and therefore a
        /// different token and survives as a second position. And a colliding token still contributes
        /// its position — the <c>DateTokenKeyUniqueToPayload</c> gate acts on the occurrence, never on
        /// group shape, because removing a position would change every other occurrence's window on
        /// the strength of one occurrence's ambiguity.
        /// </remarks>
        internal required ushort[] Positions { get; init; }

        /// <summary>Which of the four feasibility classes this group falls in.</summary>
        internal required Phase7GroupFeasibility Feasibility { get; init; }

        /// <summary><c>T</c> — how many ordered token positions the group has.</summary>
        internal int PositionCount => Positions.Length;

        /// <summary>Whether the group is in scope for the order computation at all.</summary>
        internal bool IsFeasible =>
            Feasibility == Phase7GroupFeasibility.EnoughTokenPositions;

        /// <summary>
        /// Whether the group is feasible with <c>T = M</c> — every rank's position forced by shape
        /// alone.
        /// </summary>
        /// <remarks>
        /// The codebase's own <c>ForcedPositionCounts</c> notion: <c>SequenceSlack == 0</c> and "every
        /// message has one forced token position" are the same statement.
        /// <para>
        /// Its relationship to <c>UniquelyConstrained</c> is <strong>one-way</strong>. <c>T = M</c>
        /// implies every admissible occurrence in the group is uniquely constrained; the converse is
        /// false, because a first or last rank at an edge position, or a single-member group, is
        /// uniquely constrained with <c>T &gt; M</c>. The two facts are computed independently and
        /// neither is derived from the other.
        /// </para>
        /// <para>
        /// It earns nothing. Because <c>CompatibleWithAmbiguity</c> and <c>UniquelyConstrained</c>
        /// reduce to identical ranking strength, this flag has no ranking consequence even indirectly.
        /// </para>
        /// </remarks>
        internal bool IsForcedPositionShape => IsFeasible && PositionCount == MemberCount;

        /// <summary>
        /// The width of every member's admissible window, <c>T - M + 1</c>, when the group is
        /// feasible; <see langword="null"/> otherwise.
        /// </summary>
        /// <remarks>
        /// Constant across a group's members, which is exactly why the frozen design moved it off the
        /// occurrence row and onto the attachment's snapshot: every occurrence in one group would
        /// carry the identical number, which is not an occurrence fact at all.
        /// <para>
        /// Explanatory only. No comparison, branch or ordering anywhere in Stage 7.1 reads it, and no
        /// code path awards support because a remaining count is small.
        /// </para>
        /// </remarks>
        internal int? RemainingCompatiblePositionCount =>
            IsFeasible ? PositionCount - MemberCount + 1 : null;

        /// <summary>
        /// The index of <paramref name="token"/> in <see cref="Positions"/>, or <c>-1</c> when the
        /// group has no such position.
        /// </summary>
        internal int PositionIndexOf(ushort token)
        {
            var index = Array.BinarySearch(Positions, token);

            return index < 0 ? -1 : index;
        }

        /// <summary>
        /// How many complete admissible arrangements place <paramref name="rank"/> at
        /// <paramref name="position"/>, classified.
        /// </summary>
        internal Phase7ArrangementClass ClassifyPosition(int rank, int position) =>
            Phase7ArrangementCount.Classify(rank, position, MemberCount, PositionCount);
    }

    /// <summary>
    /// The four feasibility classes, in the order <c>WithinDirectionAssignmentCensusService.Classify</c>
    /// evaluates them.
    /// </summary>
    /// <remarks>
    /// The three impossible classes are exactly <c>ImpossibleGroupCensus</c>'s, and its own
    /// documentation is what makes them a group-level fact rather than per-candidate negative
    /// evidence: <c>T &lt; M</c> "does not prove the sequence model wrong, the messages invalid, media
    /// deleted, recovery incomplete or the direction evidence mistaken. Any of those may contribute,
    /// and this census cannot separate them."
    /// </remarks>
    internal enum Phase7GroupFeasibility
    {
        /// <summary>The group has no direction-compatible candidate asset at all.</summary>
        NoCompatibleCandidateAsset,

        /// <summary>Its compatible assets carry no supported token, so <c>T = 0</c>.</summary>
        NoSupportedTokenPosition,

        /// <summary>It has fewer token positions than members, so <c>T &lt; M</c>.</summary>
        TooFewTokenPositions,

        /// <summary>It has at least as many token positions as members.</summary>
        EnoughTokenPositions,
    }
}
