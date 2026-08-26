namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// How many complete admissible arrangements of a group contain one rank at one token position:
    /// none, exactly one, or several.
    /// </summary>
    /// <remarks>
    /// A complete admissible arrangement for a group is a strictly increasing assignment of its
    /// <c>M</c> ranks to <c>M</c> of its <c>T</c> ordered token positions — a choice
    /// <c>p_0 &lt; p_1 &lt; ... &lt; p_(M-1)</c> with rank <c>r</c> at position <c>p_r</c>.
    /// Uniqueness is therefore a statement about <em>positions</em>, never about assets: one position
    /// may hold several assets, which this project reports separately and never conflates with
    /// identity.
    /// <para>
    /// Fixing rank <c>r</c> at position <c>p</c> splits the remaining choice into two independent
    /// halves — the <c>r</c> earlier ranks must occupy <c>r</c> of the <c>p</c> positions below, and
    /// the <c>M - 1 - r</c> later ranks must occupy that many of the <c>T - 1 - p</c> positions above:
    /// </para>
    /// <code>
    /// N(r, p) = C(p, r) * C(T - 1 - p, M - 1 - r)
    /// </code>
    /// <para>
    /// <c>N &gt;= 1</c> exactly when <c>r &lt;= p &lt;= r + (T - M)</c>, which is the same sliding
    /// window of width <c>T - M + 1</c> that <c>WithinDirectionAssignmentCensusService.MeasureRanges</c>
    /// walks. Given admissibility, a product of positive integers is 1 only when both factors are,
    /// and <c>C(n, k) = 1</c> iff <c>k = 0</c> or <c>k = n</c>, so
    /// </para>
    /// <code>
    /// N = 1  &lt;=&gt;  (r = 0 or p = r) and (r = M - 1 or p = r + (T - M))
    /// </code>
    /// <para>
    /// <strong>An earlier draft of this stage claimed <c>N = 1</c> is equivalent to <c>T = M</c>.
    /// That is false and is retracted.</strong> The counterexample is <c>M = 2, T = 4, r = 0,
    /// p = 2</c>: one arrangement, <c>{2, 3}</c>, with <c>T &gt; M</c>. <c>T = M</c> is only the
    /// special case where the second clause of both disjunctions holds for every rank; it
    /// <em>implies</em> uniqueness for every admissible occurrence but is not implied by it, which is
    /// why <c>IsForcedPositionGroupShape</c> is a separate group-level fact computed independently
    /// and stored in a different table.
    /// </para>
    /// <para>
    /// The four uniqueness shapes are: a forced group (<c>T = M</c>, every rank); the <em>first</em>
    /// rank at its <em>highest</em> admissible position; the <em>last</em> rank at its <em>lowest</em>;
    /// and any single-member group, where every admissible position is trivially the whole
    /// arrangement. Uniqueness is therefore commoner than the retracted claim implied — and that
    /// inflates nothing, because <c>UniquelyConstrained</c> and <c>CompatibleWithAmbiguity</c> reduce
    /// to identical ranking strength.
    /// </para>
    /// <para>
    /// <see cref="Classify"/> is the production path: it is O(1), needs no arithmetic at all, cannot
    /// overflow, and materialises no arrangement. <see cref="SaturatingCount"/> is the definitional
    /// form — the binomial product itself, evaluated with a multiply that stops at
    /// <see cref="Saturation"/> — and exists so the cheap predicate is <em>proved</em> against the
    /// count rather than assumed to agree with it. Both are pinned against brute-force enumeration by
    /// <c>Phase7ArrangementCountTests</c>.
    /// </para>
    /// <para>
    /// <c>N</c> itself is never returned to a caller that stores it and is never persisted. It is
    /// computed, used to choose among three frozen annotation values, and discarded — because a
    /// numeric quantity on an evidence row is something a future reader could be tempted to rank by,
    /// which is precisely the failure mode the frozen design removed
    /// <c>WithinDirectionHypotheticalCandidateCount</c> to prevent.
    /// </para>
    /// </remarks>
    internal static class Phase7ArrangementCount
    {
        /// <summary>
        /// The value <see cref="SaturatingCount"/> stops at. Two is enough: the classification only
        /// ever distinguishes none, exactly one, and several.
        /// </summary>
        internal const int Saturation = 2;

        /// <summary>
        /// Whether at least one complete admissible arrangement places <paramref name="rank"/> at
        /// <paramref name="position"/>.
        /// </summary>
        /// <remarks>
        /// The per-rank admissible window, <c>r .. r + (T - M)</c> inclusive. Every index in it is
        /// attainable in some complete increasing assignment, which is what makes the window exact
        /// rather than an approximation.
        /// </remarks>
        internal static bool IsAdmissible(int rank, int position, int memberCount, int positionCount) =>
            memberCount >= 1
            && positionCount >= memberCount
            && rank >= 0
            && rank < memberCount
            && position >= rank
            && position <= rank + (positionCount - memberCount);

        /// <summary>
        /// Classifies how many complete admissible arrangements place <paramref name="rank"/> at
        /// <paramref name="position"/>, in constant time and without arithmetic.
        /// </summary>
        internal static Phase7ArrangementClass Classify(
            int rank, int position, int memberCount, int positionCount)
        {
            if (!IsAdmissible(rank, position, memberCount, positionCount))
            {
                return Phase7ArrangementClass.None;
            }

            // C(p, r) = 1: either there is nothing to place below p, or the space below it is
            // exactly filled by the earlier ranks.
            var belowForced = rank == 0 || position == rank;

            // C(T - 1 - p, M - 1 - r) = 1: either there is nothing to place above p, or the space
            // above it is exactly filled by the later ranks.
            var aboveForced =
                rank == memberCount - 1 || position == rank + (positionCount - memberCount);

            return belowForced && aboveForced
                ? Phase7ArrangementClass.ExactlyOne
                : Phase7ArrangementClass.Several;
        }

        /// <summary>
        /// The binomial product <c>N(r, p)</c>, evaluated exactly but stopping at
        /// <see cref="Saturation"/>.
        /// </summary>
        /// <remarks>
        /// The definitional cross-check for <see cref="Classify"/>, deliberately computed a different
        /// way — real binomial coefficients multiplied together — so that agreement between the two is
        /// evidence rather than restatement. Not used in production.
        /// </remarks>
        internal static int SaturatingCount(
            int rank, int position, int memberCount, int positionCount)
        {
            if (memberCount < 1
                || positionCount < 0
                || rank < 0
                || rank >= memberCount
                || position < 0
                || position >= positionCount)
            {
                return 0;
            }

            var below = SaturatingBinomial(position, rank);

            if (below == 0)
            {
                return 0;
            }

            var above = SaturatingBinomial(positionCount - 1 - position, memberCount - 1 - rank);

            if (above == 0)
            {
                return 0;
            }

            return Math.Min(Saturation, below * above);
        }

        /// <summary>
        /// <c>C(n, k)</c>, exact up to <see cref="Saturation"/> and reported as
        /// <see cref="Saturation"/> beyond it.
        /// </summary>
        /// <remarks>
        /// The standard incremental formula, taken over the smaller of <c>k</c> and <c>n - k</c> so
        /// the running product stays small, and abandoned as soon as the value reaches the saturation
        /// point. Nothing here can overflow: the loop exits at the first step whose partial product
        /// is at least two.
        /// </remarks>
        private static int SaturatingBinomial(int n, int k)
        {
            if (n < 0 || k < 0 || k > n)
            {
                return 0;
            }

            var limit = Math.Min(k, n - k);

            long result = 1;

            for (var i = 1; i <= limit; i++)
            {
                result = result * (n - limit + i) / i;

                if (result >= Saturation)
                {
                    return Saturation;
                }
            }

            return (int)result;
        }
    }

    /// <summary>
    /// How many complete admissible arrangements contain one rank at one position.
    /// </summary>
    /// <remarks>
    /// Three classes rather than a count, because three is all the frozen annotation vocabulary
    /// distinguishes and a count on an evidence row would invite ranking by it.
    /// </remarks>
    internal enum Phase7ArrangementClass
    {
        /// <summary>No admissible arrangement places that rank at that position.</summary>
        None,

        /// <summary>Exactly one does.</summary>
        ExactlyOne,

        /// <summary>At least two do.</summary>
        Several,
    }
}
