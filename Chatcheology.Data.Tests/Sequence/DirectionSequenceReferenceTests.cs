using System.Numerics;
using Chatcheology.Data.Sequence;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Verifies the exact reference counts <c>A</c>, <c>S</c>, <c>P</c> and <c>Q</c> against direct
    /// enumeration of whole reference classes, and against every cross-check identity the design
    /// requires.
    /// </summary>
    /// <remarks>
    /// The oracle here is not a second dynamic programme but brute force: every arrangement of the
    /// class is built, its embeddings of the pattern are counted one by one, and the four quantities
    /// are accumulated from those. That is the only check strong enough to catch a recurrence that is
    /// plausible and wrong.
    /// <para>
    /// Every shape is synthetic. Nothing here reads a workspace, a file name or a real date.
    /// </para>
    /// </remarks>
    public class DirectionSequenceReferenceTests
    {
        /// <summary>The exhaustive bound the design review used, reproduced here.</summary>
        private const int ExhaustiveSymbolBound = 9;

        /// <summary>The exhaustive pattern-length bound the design review used.</summary>
        private const int ExhaustivePatternBound = 4;

        // -------------------------------------------------------------------------------------------
        // The named graded-determinacy counterexample, and its contrast.
        // -------------------------------------------------------------------------------------------

        /// <remarks>
        /// The case that distinguishes the <c>A * Q = P * P</c> test from the three obvious
        /// graded-determinate cases: the class is <c>{ OOI, IOO }</c>, both admit <c>OO</c>, and both
        /// hold exactly one embedding of it. So the pair is determinate for both statistics even
        /// though it is neither <c>q_r = 0</c>, nor <c>m = 1</c>, nor a single-arrangement class.
        /// </remarks>
        [Fact]
        public void NamedCounterexample_IsBinaryDeterminateAndGradedDeterminate()
        {
            bool[] pattern = [true, true];

            var arrangements = DirectionSequenceReference.ArrangementCount(2, 1, 2);
            var admitting = DirectionSequenceReference.AdmittingArrangementCount(pattern, 2, 1, 2);
            var embeddings = DirectionSequenceReference.EmbeddingPairCount(pattern, 2, 1, 2);
            var squared = DirectionSequenceReference.SquaredEmbeddingCount(pattern, 2, 1, 2);

            Assert.Equal(new BigInteger(2), arrangements);
            Assert.Equal(new BigInteger(2), admitting);
            Assert.Equal(new BigInteger(2), embeddings);
            Assert.Equal(new BigInteger(2), squared);

            // q_r = 1, so binary determinate.
            Assert.Equal(arrangements, admitting);

            // A * Q = P * P, so graded determinate as well.
            Assert.Equal(arrangements * squared, embeddings * embeddings);

            Assert.Equal(["OOI", "IOO"], Enumerate(2, 1, 2).Select(Spell).ToArray());
            Assert.Equal([1, 1], Enumerate(2, 1, 2).Select(sequence => EmbeddingCount(pattern, sequence)).ToArray());
        }

        /// <remarks>
        /// The contrast the design names: <c>q_r = 1</c>, so the pair is binary determinate, but the
        /// class's embedding counts are <c>{2, 1}</c>, so the graded statistic can still move. This is
        /// the combination v3.1 exists to make representable, and it must not be treated as an
        /// inconsistency.
        /// </remarks>
        [Fact]
        public void ContrastingCase_IsBinaryDeterminateAndGradedInformative()
        {
            bool[] pattern = [true, false];

            var arrangements = DirectionSequenceReference.ArrangementCount(1, 3, 3);
            var admitting = DirectionSequenceReference.AdmittingArrangementCount(pattern, 1, 3, 3);
            var embeddings = DirectionSequenceReference.EmbeddingPairCount(pattern, 1, 3, 3);
            var squared = DirectionSequenceReference.SquaredEmbeddingCount(pattern, 1, 3, 3);

            Assert.Equal(new BigInteger(2), arrangements);
            Assert.Equal(new BigInteger(2), admitting);
            Assert.Equal(new BigInteger(3), embeddings);
            Assert.Equal(new BigInteger(5), squared);

            Assert.Equal(arrangements, admitting);
            Assert.NotEqual(arrangements * squared, embeddings * embeddings);

            Assert.Equal(
                [2, 1],
                Enumerate(1, 3, 3).Select(sequence => EmbeddingCount(pattern, sequence)).ToArray());
        }

        // -------------------------------------------------------------------------------------------
        // Exhaustive verification against enumeration.
        // -------------------------------------------------------------------------------------------

        /// <remarks>
        /// Every <c>(o, i, r, p)</c> with <c>o + i &lt;= 9</c> and <c>1 &lt;= m &lt;= 4</c>, checked
        /// against enumeration of the whole class. Each case also asserts the two always-true
        /// invariants — <c>A * Q &gt;= P * P</c>, and that <c>A * Q = P * P</c> holds exactly when the
        /// enumerated embedding counts are all equal — and that the provably empty
        /// <c>BinaryInformative + GradedDeterminate</c> combination never occurs.
        /// </remarks>
        [Fact]
        public void EveryDynamicProgrammeAgreesWithEnumerationOnEverySmallShape()
        {
            var cases = 0;

            for (var outgoing = 0; outgoing <= ExhaustiveSymbolBound; outgoing++)
            {
                for (var incoming = 0; outgoing + incoming <= ExhaustiveSymbolBound; incoming++)
                {
                    if (outgoing + incoming == 0)
                    {
                        continue;
                    }

                    for (var runs = 1; runs <= outgoing + incoming; runs++)
                    {
                        foreach (var pattern in Patterns(ExhaustivePatternBound))
                        {
                            AssertAgreesWithEnumeration(pattern, outgoing, incoming, runs);
                            cases++;
                        }
                    }
                }
            }

            // A guard on the guard: a bound that silently stopped enumerating would still pass every
            // assertion above.
            Assert.Equal(9900, cases);
        }

        /// <remarks>
        /// Targeted shapes past the exhaustive bound, including longer patterns than the exhaustive
        /// sweep reaches, so a recurrence that only misbehaves once several runs of one direction
        /// carry pattern symbols is still caught.
        /// </remarks>
        [Theory]
        [InlineData(6, 6, 5, "OIOIO")]
        [InlineData(6, 6, 8, "OOIIOI")]
        [InlineData(7, 5, 4, "OIIO")]
        [InlineData(3, 9, 6, "IOIOII")]
        [InlineData(8, 4, 9, "OOOIO")]
        [InlineData(5, 5, 10, "OIOIOI")]
        [InlineData(2, 10, 3, "IIIIII")]
        [InlineData(10, 2, 5, "OOOOO")]
        public void LargerTargetedShapesAgreeWithEnumeration(
            int outgoing, int incoming, int runs, string pattern) =>
            AssertAgreesWithEnumeration(Parse(pattern), outgoing, incoming, runs);

        // -------------------------------------------------------------------------------------------
        // The required cross-check identities.
        // -------------------------------------------------------------------------------------------

        /// <remarks><c>sum over r of A(o, i, r) = C(o + i, o)</c>.</remarks>
        [Fact]
        public void ArrangementCountsSumToTheBinomialOverEveryRunCount()
        {
            for (var outgoing = 0; outgoing <= 12; outgoing++)
            {
                for (var incoming = 0; outgoing + incoming <= 12; incoming++)
                {
                    var total = BigInteger.Zero;

                    for (var runs = 0; runs <= outgoing + incoming; runs++)
                    {
                        total += DirectionSequenceReference.ArrangementCount(
                            outgoing, incoming, runs);
                    }

                    Assert.Equal(
                        DirectionSequenceReference.Binomial(outgoing + incoming, outgoing),
                        total);
                }
            }
        }

        /// <remarks>
        /// <c>sum over r of S(p, o, i, r)</c> is the exchangeable admitting count, so the
        /// <c>A</c>-weighted mean of <c>q_r</c> over run counts is exactly <c>q(p, o, i)</c>.
        /// </remarks>
        [Fact]
        public void AdmittingCountsSumToTheExchangeableAdmittingCount()
        {
            foreach (var pattern in Patterns(ExhaustivePatternBound))
            {
                for (var outgoing = 0; outgoing <= 8; outgoing++)
                {
                    for (var incoming = 0; outgoing + incoming <= 8; incoming++)
                    {
                        var total = BigInteger.Zero;

                        for (var runs = 0; runs <= outgoing + incoming; runs++)
                        {
                            total += DirectionSequenceReference.AdmittingArrangementCount(
                                pattern, outgoing, incoming, runs);
                        }

                        Assert.Equal(
                            DirectionSequenceReference.ExchangeableAdmittingCount(
                                pattern, outgoing, incoming),
                            total);
                    }
                }
            }
        }

        /// <remarks>
        /// <c>sum over r of P</c>, divided by <c>C(o + i, o) * C(o + i, m)</c>, is the order-free
        /// exchangeable share <c>(o)_k (i)_(m - k) / (o + i)_m</c>. Its independence from the order of
        /// <c>p</c> is itself the point: it is the strongest available check that <c>P</c> is right.
        /// </remarks>
        [Fact]
        public void EmbeddingPairCountsSumToTheExchangeableFallingFactorialShare()
        {
            foreach (var pattern in Patterns(ExhaustivePatternBound))
            {
                var length = pattern.Length;
                var patternOutgoing = DirectionSequenceReference.CountOutgoing(pattern);

                for (var outgoing = 0; outgoing <= 8; outgoing++)
                {
                    for (var incoming = 0; outgoing + incoming <= 8; incoming++)
                    {
                        var total = outgoing + incoming;

                        if (total < length)
                        {
                            continue;
                        }

                        var summed = BigInteger.Zero;

                        for (var runs = 0; runs <= total; runs++)
                        {
                            summed += DirectionSequenceReference.EmbeddingPairCount(
                                pattern, outgoing, incoming, runs);
                        }

                        var denominator =
                            DirectionSequenceReference.Binomial(total, outgoing)
                            * DirectionSequenceReference.Binomial(total, length);

                        var expectedNumerator =
                            DirectionSequenceReference.FallingFactorial(outgoing, patternOutgoing)
                            * DirectionSequenceReference.FallingFactorial(
                                incoming, length - patternOutgoing);

                        var expectedDenominator =
                            DirectionSequenceReference.FallingFactorial(total, length);

                        // summed / denominator = expectedNumerator / expectedDenominator, compared as
                        // an exact integer cross-multiplication rather than as two divisions.
                        Assert.Equal(
                            summed * expectedDenominator, expectedNumerator * denominator);
                    }
                }
            }
        }

        /// <remarks>
        /// The exchangeable expected run count, on shapes simple enough to state by hand.
        /// </remarks>
        [Theory]
        [InlineData(5, 0, 1d)]
        [InlineData(0, 5, 1d)]
        [InlineData(1, 1, 2d)]
        [InlineData(2, 2, 3d)]
        [InlineData(3, 3, 4d)]
        [InlineData(1, 3, 2.5d)]
        public void ExpectedRunCountFollowsTheExchangeableFormula(
            int outgoing, int incoming, double expected) =>
            Assert.Equal(
                expected, DirectionSequenceReference.ExpectedRunCount(outgoing, incoming), 12);

        [Fact]
        public void ExpectedRunCountOfAnEmptySequenceIsZero() =>
            Assert.Equal(0d, DirectionSequenceReference.ExpectedRunCount(0, 0));

        // -------------------------------------------------------------------------------------------
        // Run counting.
        // -------------------------------------------------------------------------------------------

        [Theory]
        [InlineData("O", 1, 0)]
        [InlineData("OOOO", 1, 0)]
        [InlineData("IIII", 1, 0)]
        [InlineData("OI", 2, 1)]
        [InlineData("OIOI", 4, 3)]
        [InlineData("IOIOIO", 6, 5)]
        [InlineData("OOIIOO", 3, 2)]
        public void RunAndTransitionCountsAreCountedFromAdjacency(
            string sequence, int expectedRuns, int expectedTransitions)
        {
            var parsed = Parse(sequence);

            Assert.Equal(expectedRuns, DirectionSequenceReference.RunCount(parsed));
            Assert.Equal(expectedTransitions, DirectionSequenceReference.TransitionCount(parsed));
        }

        [Fact]
        public void AnEmptySequenceHasNoRuns()
        {
            Assert.Equal(0, DirectionSequenceReference.RunCount([]));
            Assert.Equal(0, DirectionSequenceReference.TransitionCount([]));
        }

        /// <remarks>
        /// A single-direction sequence has one run and an alternating one has as many runs as
        /// symbols, which are the two extremes the run-conditioned reference is bounded by.
        /// </remarks>
        [Fact]
        public void OnlyOneArrangementIsSingleDirectionAndTheAlternatingExtremeIsMaximal()
        {
            Assert.Equal(BigInteger.One, DirectionSequenceReference.ArrangementCount(4, 0, 1));
            Assert.Equal(BigInteger.Zero, DirectionSequenceReference.ArrangementCount(4, 0, 2));

            Assert.Equal(new BigInteger(2), DirectionSequenceReference.ArrangementCount(3, 3, 6));
            Assert.Equal(BigInteger.Zero, DirectionSequenceReference.ArrangementCount(3, 3, 7));
        }

        // -------------------------------------------------------------------------------------------
        // Determinacy behaviour the census depends on.
        // -------------------------------------------------------------------------------------------

        /// <remarks>
        /// <c>q_r = 0</c>: no arrangement of the class admits the pattern, so the conditioning data
        /// alone decides admission.
        /// </remarks>
        [Fact]
        public void NoArrangementAdmitsAPatternTheCompositionCannotSupply()
        {
            bool[] pattern = [true, false, true];

            Assert.Equal(
                BigInteger.Zero,
                DirectionSequenceReference.AdmittingArrangementCount(pattern, 2, 1, 2));

            Assert.Equal(
                BigInteger.Zero,
                DirectionSequenceReference.EmbeddingPairCount(pattern, 2, 1, 2));
        }

        /// <remarks>A genuinely informative shape: some arrangements admit the pattern and some do not.</remarks>
        [Fact]
        public void SomeClassesAreSplitBetweenAdmittingAndNotAdmitting()
        {
            bool[] pattern = [true, false];

            var arrangements = DirectionSequenceReference.ArrangementCount(2, 2, 2);
            var admitting = DirectionSequenceReference.AdmittingArrangementCount(pattern, 2, 2, 2);

            Assert.Equal(new BigInteger(2), arrangements);
            Assert.Equal(BigInteger.One, admitting);
            Assert.True(admitting > BigInteger.Zero && admitting < arrangements);
        }

        // -------------------------------------------------------------------------------------------
        // Binomials and falling factorials.
        // -------------------------------------------------------------------------------------------

        [Theory]
        [InlineData(-1, 0)]
        [InlineData(3, -1)]
        [InlineData(3, 4)]
        public void ImpossibleChoicesAreZeroRatherThanAnError(int n, int k) =>
            Assert.Equal(BigInteger.Zero, DirectionSequenceReference.Binomial(n, k));

        [Fact]
        public void BinomialsAgreeWithPascalsTriangle()
        {
            var previous = new BigInteger[] { BigInteger.One };

            for (var row = 1; row <= 40; row++)
            {
                var current = new BigInteger[row + 1];

                for (var column = 0; column <= row; column++)
                {
                    current[column] =
                        (column == 0 ? BigInteger.Zero : previous[column - 1])
                        + (column == row ? BigInteger.Zero : previous[column]);

                    Assert.Equal(current[column], DirectionSequenceReference.Binomial(row, column));
                }

                previous = current;
            }
        }

        [Theory]
        [InlineData(5, 0, 1)]
        [InlineData(5, 1, 5)]
        [InlineData(5, 3, 60)]
        [InlineData(5, 6, 0)]
        [InlineData(0, 2, 0)]
        public void FallingFactorialsMultiplyDownwards(int value, int length, int expected) =>
            Assert.Equal(
                new BigInteger(expected),
                DirectionSequenceReference.FallingFactorial(value, length));

        // -------------------------------------------------------------------------------------------
        // The enumeration oracle.
        // -------------------------------------------------------------------------------------------

        private static void AssertAgreesWithEnumeration(
            bool[] pattern, int outgoing, int incoming, int runs)
        {
            var arrangements = BigInteger.Zero;
            var admitting = BigInteger.Zero;
            var embeddings = BigInteger.Zero;
            var squared = BigInteger.Zero;
            var counts = new List<BigInteger>();

            foreach (var sequence in Enumerate(outgoing, incoming, runs))
            {
                var count = EmbeddingCount(pattern, sequence);

                arrangements += BigInteger.One;
                admitting += count > BigInteger.Zero ? BigInteger.One : BigInteger.Zero;
                embeddings += count;
                squared += count * count;
                counts.Add(count);
            }

            var label = $"{Spell(pattern)} in ({outgoing}, {incoming}, {runs})";

            Assert.Equal(
                arrangements,
                DirectionSequenceReference.ArrangementCount(outgoing, incoming, runs));

            Assert.Equal(
                admitting,
                DirectionSequenceReference.AdmittingArrangementCount(
                    pattern, outgoing, incoming, runs));

            Assert.Equal(
                embeddings,
                DirectionSequenceReference.EmbeddingPairCount(pattern, outgoing, incoming, runs));

            Assert.Equal(
                squared,
                DirectionSequenceReference.SquaredEmbeddingCount(
                    pattern, outgoing, incoming, runs));

            // A * Q >= P * P always, by Cauchy-Schwarz. A strict violation is an accumulator defect.
            Assert.True(
                arrangements * squared >= embeddings * embeddings,
                $"A * Q < P * P for {label}");

            var constantCounts = counts.Count == 0 || counts.TrueForAll(count => count == counts[0]);

            // A * Q = P * P holds exactly when the enumerated counts are all equal.
            Assert.Equal(
                constantCounts, arrangements * squared == embeddings * embeddings);

            var binaryDeterminate = admitting.IsZero || admitting == arrangements;
            var gradedDeterminate = arrangements * squared == embeddings * embeddings;

            // GradedDeterminate implies BinaryDeterminate, so the cross-tabulation row
            // "BinaryInformative and GradedDeterminate" is provably empty.
            Assert.False(
                !binaryDeterminate && gradedDeterminate,
                $"BinaryInformative and GradedDeterminate for {label}");
        }

        /// <summary>Every arrangement with that composition and exactly that many runs.</summary>
        private static List<bool[]> Enumerate(int outgoing, int incoming, int runs)
        {
            var matching = new List<bool[]>();
            var total = outgoing + incoming;

            foreach (var sequence in AllSequences(total))
            {
                if (DirectionSequenceReference.CountOutgoing(sequence) != outgoing
                    || DirectionSequenceReference.RunCount(sequence) != runs)
                {
                    continue;
                }

                matching.Add(sequence);
            }

            return matching;
        }

        private static IEnumerable<bool[]> AllSequences(int length)
        {
            var limit = 1L << length;

            for (var mask = 0L; mask < limit; mask++)
            {
                var sequence = new bool[length];

                for (var index = 0; index < length; index++)
                {
                    // The high bit is the first symbol, so sequences come out in a stable order.
                    sequence[index] = ((mask >> (length - 1 - index)) & 1) == 0;
                }

                yield return sequence;
            }
        }

        /// <summary>Every pattern of length one to <paramref name="maximumLength"/>.</summary>
        private static IEnumerable<bool[]> Patterns(int maximumLength)
        {
            for (var length = 1; length <= maximumLength; length++)
            {
                foreach (var pattern in AllSequences(length))
                {
                    yield return pattern;
                }
            }
        }

        /// <summary>How many increasing index tuples of a sequence spell the pattern.</summary>
        private static BigInteger EmbeddingCount(bool[] pattern, bool[] sequence)
        {
            var ways = new BigInteger[pattern.Length + 1];
            ways[0] = BigInteger.One;

            foreach (var symbol in sequence)
            {
                for (var position = pattern.Length; position >= 1; position--)
                {
                    if (pattern[position - 1] == symbol)
                    {
                        ways[position] += ways[position - 1];
                    }
                }
            }

            return ways[pattern.Length];
        }

        private static bool[] Parse(string sequence)
        {
            var parsed = new bool[sequence.Length];

            for (var index = 0; index < sequence.Length; index++)
            {
                parsed[index] = sequence[index] == 'O';
            }

            return parsed;
        }

        private static string Spell(bool[] sequence) =>
            string.Concat(sequence.Select(symbol => symbol ? 'O' : 'I'));
    }
}
