namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// Whether the token side's own conditioning data already decides a pair's outcome for one
    /// primary statistic.
    /// </summary>
    /// <remarks>
    /// Classified once per statistic, never once per pair. A pair may be
    /// <see cref="Determinate"/> for binary admission and <see cref="Informative"/> for the graded
    /// share, and that combination is legitimate rather than an inconsistency: the binary outcome can
    /// be fixed by the class while the number of monotone embeddings still varies within it.
    /// <para>
    /// The one relationship that holds runs in a single direction — graded determinacy implies binary
    /// determinacy — and the census computes both classifications rather than deriving either from
    /// that argument.
    /// </para>
    /// </remarks>
    public enum DirectionSequenceDeterminacyClass
    {
        /// <summary>
        /// The pair was not classified: it is degenerate, supply-insufficient, or excluded.
        /// </summary>
        NotClassified = 0,

        /// <summary>
        /// The conditioning data fixes the outcome, so the pair contributes exactly zero to this
        /// statistic however it is treated.
        /// </summary>
        Determinate = 1,

        /// <summary>The outcome genuinely varies within the reference class.</summary>
        Informative = 2,
    }
}
