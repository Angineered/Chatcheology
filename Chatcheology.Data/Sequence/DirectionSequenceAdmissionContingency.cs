namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// The paired admission outcome of the observed token order against its reverse.
    /// </summary>
    /// <remarks>
    /// Reported as the full contingency rather than as a better-or-worse label, because
    /// <c>NeitherAdmits</c> and <c>BothAdmit</c> are very different states that a label would render
    /// identically as "a tie".
    /// <para>
    /// Equal marginal rates between the two columns are not evidence against the monotone hypothesis.
    /// Reversal preserves the outgoing count, the incoming count and the run count exactly, so a reversed
    /// sequence belongs to the same reference class as the observed one and draws its admission
    /// probability from the same <c>q_r</c>. It varies nothing the reference conditions on, which is why
    /// this is a diagnostic and not a control.
    /// </remarks>
    public sealed class DirectionSequenceAdmissionContingency
    {
        /// <summary>Pairs the observed order admits and the reversed order does not.</summary>
        public required int ActualOnlyAdmits { get; init; }

        /// <summary>Pairs both admit.</summary>
        public required int BothAdmit { get; init; }

        /// <summary>Pairs neither admits.</summary>
        public required int NeitherAdmits { get; init; }

        /// <summary>Pairs the reversed order admits and the observed order does not.</summary>
        public required int ReversedOnlyAdmits { get; init; }

        /// <summary>Every pair falls in exactly one cell.</summary>
        public int Total => ActualOnlyAdmits + BothAdmit + NeitherAdmits + ReversedOnlyAdmits;
    }
}
