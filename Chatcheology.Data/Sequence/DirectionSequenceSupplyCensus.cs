namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C0b — supply adequacy before and after equivalent-position collapse, with the pairs collapse
    /// moved.
    /// </summary>
    /// <remarks>
    /// Collapse is the correct integrity call and it is also power-relevant: counting physical copies
    /// as separate logical positions would inflate the token side, and collapsing them reduces the
    /// outgoing and incoming counts, which makes insufficiency more likely and changes the run count
    /// every reference is conditioned on. Reporting adequacy only after collapse would absorb that
    /// effect instead of showing it.
    /// <para>
    /// Collapse can only ever remove positions, so <see cref="PairsBecomingSufficientAfterCollapse"/>
    /// is expected to be zero. It is computed rather than assumed, because a non-zero value would mean
    /// the pre-collapse and post-collapse counts are not counting the same thing.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceSupplyCensus
    {
        /// <summary>Adequacy measured over direction-labelled physical observations.</summary>
        public required DirectionSequenceSupplyCounts BeforeCollapse { get; init; }

        /// <summary>Adequacy measured over collapsed logical positions, which is what is used.</summary>
        public required DirectionSequenceSupplyCounts AfterCollapse { get; init; }

        /// <summary>Pairs that were sufficient before collapse and are not after it.</summary>
        public required int PairsBecomingInsufficientAfterCollapse { get; init; }

        /// <summary>
        /// Pairs that were insufficient before collapse and are sufficient after it, which cannot
        /// happen.
        /// </summary>
        public required int PairsBecomingSufficientAfterCollapse { get; init; }

        /// <summary>Message observations lost by the pairs collapse made insufficient.</summary>
        public required int MessageObservationsLostToCollapse { get; init; }
    }
}
