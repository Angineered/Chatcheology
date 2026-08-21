namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C0 — pairs the conditioning data leaves nothing for order to decide in, censused explicitly by
    /// kind.
    /// </summary>
    /// <remarks>
    /// Counted rather than filtered. Each of these pairs would contribute exactly zero to a primary
    /// statistic, so leaving them out of the classified population is right — but letting them vanish
    /// through a threshold would hide how much of the archive is in this state, which is one of the
    /// things the gate exists to measure.
    /// <para>
    /// The classes overlap on purpose and are each counted independently, with
    /// <see cref="DegeneratePairCount"/> as the distinct total. A pair with one message symbol on a
    /// date whose token side holds a single arrangement falls in two of them, and netting that off
    /// would misreport both.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceDegenerateCounts
    {
        /// <summary>Pairs whose scope emits no direction-labelled token position on the date.</summary>
        public required int NoTokenPositionPairCount { get; init; }

        /// <summary>Pairs whose date carries no eligible message symbol.</summary>
        public required int NoMessageSymbolPairCount { get; init; }

        /// <summary>
        /// Pairs carrying exactly one message symbol.
        /// </summary>
        /// <remarks>
        /// Determinate for both statistics wherever supply exists: one symbol admits whenever the
        /// direction is present at all, and its embedding count is the same in every arrangement of the
        /// class.
        /// </remarks>
        public required int SingleMessageSymbolPairCount { get; init; }

        /// <summary>Pairs whose token side emits no outgoing symbol.</summary>
        public required int NoOutgoingTokenPositionPairCount { get; init; }

        /// <summary>Pairs whose token side emits no incoming symbol.</summary>
        public required int NoIncomingTokenPositionPairCount { get; init; }

        /// <summary>
        /// Pairs whose <c>(o, i, r)</c> class holds exactly one arrangement, so the conditioning data
        /// determines the sequence outright.
        /// </summary>
        public required int SingleArrangementPairCount { get; init; }

        /// <summary>
        /// Distinct pairs falling in at least one of the classes above, among pairs that carry message
        /// symbols and were not excluded.
        /// </summary>
        public required int DegeneratePairCount { get; init; }

        /// <summary>Message observations those distinct pairs carry.</summary>
        public required int MessageObservationsInDegeneratePairs { get; init; }
    }
}
