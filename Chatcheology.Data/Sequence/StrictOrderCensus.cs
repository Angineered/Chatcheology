namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C2 and C3 — the strict cross-direction order result and its exact chance baseline.
    /// </summary>
    /// <remarks>
    /// Each eligible date contributes exactly one observation: one message and one provisional asset
    /// on each side, ordered by the committed message sequence number. That is what makes a sign test
    /// legitimate here — the observations are one per date, not one per relation pair.
    /// <para>
    /// The probability is carried as the exact rational it is: an integer numerator over
    /// <c>2 ^ ObservationCount</c>. The rendered form is derived from those two, so no floating-point
    /// value decides anything and the same census renders the same digits everywhere.
    /// </para>
    /// </remarks>
    public sealed class StrictOrderCensus
    {
        /// <summary>Independent observations, one per eligible date.</summary>
        public required int ObservationCount { get; init; }

        /// <summary>Earlier message's token is lower.</summary>
        public required int ConcordantCount { get; init; }

        /// <summary>Earlier message's token is higher.</summary>
        public required int DiscordantCount { get; init; }

        /// <summary>
        /// The numerator of the exact one-sided sign probability, or <see langword="null"/> when
        /// there is no observation to compute one from.
        /// </summary>
        /// <remarks>
        /// Decimal digits of an exact integer, which may exceed 64 bits, so it is carried as text
        /// rather than silently truncated to a numeric type.
        /// </remarks>
        public required string? ExactOneSidedProbabilityNumerator { get; init; }

        /// <summary>
        /// The exponent of the probability's denominator: the probability is the numerator over
        /// <c>2 ^ this</c>. Equal to <see cref="ObservationCount"/> whenever a numerator exists.
        /// </summary>
        public required int ExactOneSidedProbabilityDenominatorExponent { get; init; }

        /// <summary>
        /// The same probability rendered to twelve significant digits, or <see langword="null"/> when
        /// there is no observation.
        /// </summary>
        public required string? ExactOneSidedProbability { get; init; }
    }
}
