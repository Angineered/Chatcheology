namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// How far one <c>(scope key, local date)</c> pair got through the gate.
    /// </summary>
    /// <remarks>
    /// Every state is censused. None of them is a filter that makes a pair disappear: a pair that
    /// cannot carry the question is counted under the reason it cannot, so the population that
    /// reaches <see cref="Classified"/> can be read against everything that did not.
    /// </remarks>
    public enum DirectionSequencePairState
    {
        /// <summary>
        /// A logical token position in this pair carried conflicting direction labels, so the whole
        /// pair is out of every primary population. Never resolved by preferring a source, and never
        /// repaired by dropping only the conflicting position.
        /// </summary>
        ExcludedByDirectionConflict = 0,

        /// <summary>No eligible message symbol falls on this date.</summary>
        NoMessageSymbols = 1,

        /// <summary>No token position of this scope emits a direction symbol on this date.</summary>
        NoTokenPositions = 2,

        /// <summary>
        /// The token side cannot supply the message side's direction counts, so the pair is not
        /// order-testable at all.
        /// </summary>
        SupplyInsufficient = 3,

        /// <summary>
        /// Supply is sufficient, but the conditioning data leaves only one arrangement or only one
        /// message symbol, so there is nothing for order to decide.
        /// </summary>
        Degenerate = 4,

        /// <summary>Supply-sufficient and non-degenerate: classified for both statistics.</summary>
        Classified = 5,
    }
}
