using System.Numerics;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// The two exact counts a binary admission reference needs: the class's size and how much of it
    /// admits the pattern.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="DirectionSequenceReferenceValues"/> because the binary rule genuinely
    /// does not mention the graded quantities. <c>q_r</c> is <c>S / A</c> and binary determinacy is
    /// <c>S = 0</c> or <c>S = A</c>, so a binary comparison that also computed the embedding-pair and
    /// squared-embedding counts would be paying for the most expensive part of the reference to answer a
    /// question that does not use it.
    /// </remarks>
    internal readonly record struct DirectionSequenceAdmissionValues(
        BigInteger ArrangementCount, BigInteger AdmittingArrangementCount)
    {
        /// <summary>Whether the conditioning data fixes whether the pattern is admitted.</summary>
        internal bool Determinate =>
            AdmittingArrangementCount.IsZero
            || AdmittingArrangementCount == ArrangementCount;
    }
}
