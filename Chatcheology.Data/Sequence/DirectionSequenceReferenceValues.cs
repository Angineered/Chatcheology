using System.Numerics;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// The four exact reference counts one <c>(pattern, outgoing, incoming, runs)</c> class carries.
    /// </summary>
    /// <remarks>
    /// Held together because they are always wanted together and because the identities between them are
    /// what make a class usable: <c>A</c> must not be zero for a class a sequence was actually built
    /// from, <c>A * Q</c> is at least <c>P * P</c> by Cauchy–Schwarz, and graded determinacy —
    /// <c>A * Q = P * P</c>, zero variance of the share across the class — implies binary determinacy.
    /// <para>
    /// Exact integers throughout. Every classification derived from them is an integer comparison, and no
    /// decimal reading takes part in deciding anything.
    /// </para>
    /// </remarks>
    internal readonly record struct DirectionSequenceReferenceValues(
        BigInteger ArrangementCount,
        BigInteger AdmittingArrangementCount,
        BigInteger EmbeddingPairCount,
        BigInteger SquaredEmbeddingCount)
    {
        /// <summary>Whether the conditioning data fixes whether the pattern is admitted.</summary>
        internal bool BinaryDeterminate =>
            AdmittingArrangementCount.IsZero
            || AdmittingArrangementCount == ArrangementCount;

        /// <summary>Whether it fixes the normalised embedding share.</summary>
        internal bool GradedDeterminate =>
            ArrangementCount * SquaredEmbeddingCount == EmbeddingPairCount * EmbeddingPairCount;
    }
}
