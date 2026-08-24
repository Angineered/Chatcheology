namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// How an adjacent-date intersection was formed, with every dropped pair accounted for by reason.
    /// </summary>
    /// <remarks>
    /// The correct-date and shifted-date figures of a control are computed over the same pairs — the same
    /// collection, iterated twice — and this is what makes that checkable from the outside rather than
    /// asserted. The eligible own-date count must equal the intersection plus every drop reason below.
    /// <para>
    /// The drops are not noise. Neighbour availability favours denser periods of the archive, so which
    /// pairs survive to an intersection is itself a selection, which is why the control also reports the
    /// composition of what it kept against the population it came from.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceIntersectionAccounting
    {
        /// <summary>Pairs eligible on their own date, before intersecting.</summary>
        public required int OwnDateEligiblePairCount { get; init; }

        /// <summary>Pairs eligible under the shifted comparison as well.</summary>
        public required int IntersectionPairCount { get; init; }

        /// <summary>Dropped because the neighbouring calendar date is not in the pair universe at all.</summary>
        public required int DroppedNeighbourDateAbsent { get; init; }

        /// <summary>Dropped because the neighbouring date emits no direction-labelled token position.</summary>
        public required int DroppedNeighbourTokenPopulationEmpty { get; init; }

        /// <summary>Dropped because the neighbouring date holds a direction-conflicting position.</summary>
        public required int DroppedNeighbourDateConflict { get; init; }

        /// <summary>Dropped because the shifted token side cannot supply the message pattern.</summary>
        public required int DroppedShiftedSupplyInsufficient { get; init; }

        /// <summary>Dropped because the shifted comparison leaves order nothing to decide.</summary>
        public required int DroppedShiftedDegenerate { get; init; }

        /// <summary>
        /// Dropped because the shifted conditioning data fixes the outcome, so the pair is determinate
        /// under the shifted comparison and would contribute exactly zero to a difference.
        /// </summary>
        public required int DroppedShiftedDeterminate { get; init; }

        /// <summary>Every dropped pair, by reason.</summary>
        public int DroppedTotal =>
            DroppedNeighbourDateAbsent
            + DroppedNeighbourTokenPopulationEmpty
            + DroppedNeighbourDateConflict
            + DroppedShiftedSupplyInsufficient
            + DroppedShiftedDegenerate
            + DroppedShiftedDeterminate;

        /// <summary>The eligible pairs are the intersection plus the drops, with nothing unexplained.</summary>
        public int AccountedTotal => IntersectionPairCount + DroppedTotal;
    }
}
