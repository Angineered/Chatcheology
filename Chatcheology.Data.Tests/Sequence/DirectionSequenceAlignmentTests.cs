using System.Numerics;
using Chatcheology.Data.Sequence;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Tests the exact observed-order computations Stage B2C-1 adds, against brute force on shapes small
    /// enough to enumerate exhaustively.
    /// </summary>
    /// <remarks>
    /// Both dynamic programmes are checked against an oracle that does the obvious slow thing —
    /// enumerate every choice of positions, or every common subsequence — over every sequence pair up to
    /// a length the enumeration can actually cover. A dynamic programme that agreed with a
    /// hand-written expectation on a handful of cases could still be wrong; one that agrees with
    /// enumeration everywhere below length nine is not wrong in a way these shapes can express.
    /// </remarks>
    public class DirectionSequenceAlignmentTests
    {
        // ---------------------------------------------------------------------------------------
        // Longest common subsequence.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TheAlignmentOfTwoEmptySequencesIsZero()
        {
            Assert.Equal(0, DirectionSequenceAlignment.LongestCommonSubsequenceLength([], []));
            Assert.Equal(0, DirectionSequenceAlignment.LongestCommonSubsequenceLength([true], []));
            Assert.Equal(0, DirectionSequenceAlignment.LongestCommonSubsequenceLength([], [true]));
        }

        /// <remarks>
        /// Every sequence pair up to length six on each side, against enumeration of every subsequence
        /// of the shorter. Covers one-direction, alternating, clustered and repeated shapes without
        /// naming them, because it covers everything.
        /// </remarks>
        [Fact]
        public void TheAlignmentAgreesWithEnumerationOnEverySmallPair()
        {
            foreach (var message in Sequences(6))
            {
                foreach (var tokens in Sequences(6))
                {
                    Assert.Equal(
                        BruteForceLongestCommonSubsequence(message, tokens),
                        DirectionSequenceAlignment.LongestCommonSubsequenceLength(message, tokens));
                }
            }
        }

        /// <remarks>
        /// The invariant the supply decomposition rests on: a common subsequence cannot use more
        /// outgoing symbols than either side has, nor more incoming, so it cannot exceed the ceiling.
        /// </remarks>
        [Fact]
        public void TheAlignmentNeverExceedsTheDirectionCountCeiling()
        {
            foreach (var message in Sequences(6))
            {
                foreach (var tokens in Sequences(6))
                {
                    var messageOutgoing = DirectionSequenceReference.CountOutgoing(message);
                    var tokenOutgoing = DirectionSequenceReference.CountOutgoing(tokens);

                    var ceiling = DirectionSequenceAlignment.DirectionCountCeiling(
                        messageOutgoing,
                        message.Length - messageOutgoing,
                        tokenOutgoing,
                        tokens.Length - tokenOutgoing);

                    var alignment =
                        DirectionSequenceAlignment.LongestCommonSubsequenceLength(message, tokens);

                    Assert.True(alignment <= ceiling);
                    Assert.True(ceiling <= message.Length || message.Length == 0);
                }
            }
        }

        /// <remarks>
        /// The supply decomposition itself, over every small pair: what order loses plus what supply
        /// loses is everything unaligned, and supply loss is the two shortfalls.
        /// </remarks>
        [Fact]
        public void SupplyLossAndOrderLossDecomposeTheUnalignedSymbols()
        {
            foreach (var message in Sequences(6))
            {
                foreach (var tokens in Sequences(6))
                {
                    var messageOutgoing = DirectionSequenceReference.CountOutgoing(message);
                    var messageIncoming = message.Length - messageOutgoing;
                    var tokenOutgoing = DirectionSequenceReference.CountOutgoing(tokens);
                    var tokenIncoming = tokens.Length - tokenOutgoing;

                    var ceiling = DirectionSequenceAlignment.DirectionCountCeiling(
                        messageOutgoing, messageIncoming, tokenOutgoing, tokenIncoming);

                    var outgoingShortfall = Math.Max(0, messageOutgoing - tokenOutgoing);
                    var incomingShortfall = Math.Max(0, messageIncoming - tokenIncoming);

                    var supplyLoss = message.Length - ceiling;

                    Assert.Equal(outgoingShortfall + incomingShortfall, supplyLoss);

                    var alignment =
                        DirectionSequenceAlignment.LongestCommonSubsequenceLength(message, tokens);

                    var orderLoss = ceiling - alignment;
                    var totalUnaligned = message.Length - alignment;

                    Assert.True(orderLoss >= 0);
                    Assert.True(totalUnaligned >= 0);
                    Assert.Equal(supplyLoss + orderLoss, totalUnaligned);
                }
            }
        }

        /// <remarks>
        /// Lopsided pairs go down the shorter-inner path, so the same agreement has to hold there.
        /// </remarks>
        [Fact]
        public void TheAlignmentAgreesWithEnumerationWhenOneSideIsMuchLonger()
        {
            foreach (var message in Sequences(3))
            {
                foreach (var tokens in Sequences(8))
                {
                    Assert.Equal(
                        BruteForceLongestCommonSubsequence(message, tokens),
                        DirectionSequenceAlignment.LongestCommonSubsequenceLength(message, tokens));

                    // Symmetric, which is what makes the shorter-inner choice safe.
                    Assert.Equal(
                        DirectionSequenceAlignment.LongestCommonSubsequenceLength(message, tokens),
                        DirectionSequenceAlignment.LongestCommonSubsequenceLength(tokens, message));
                }
            }
        }

        // ---------------------------------------------------------------------------------------
        // Monotone embedding count.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TheEmptyMessageEmbedsExactlyOnce()
        {
            Assert.Equal(BigInteger.One, DirectionSequenceAlignment.MonotoneEmbeddingCount([], []));

            Assert.Equal(
                BigInteger.One,
                DirectionSequenceAlignment.MonotoneEmbeddingCount([], [true, false]));
        }

        [Fact]
        public void AMessageLongerThanTheTokenSideCannotEmbed()
        {
            Assert.Equal(
                BigInteger.Zero,
                DirectionSequenceAlignment.MonotoneEmbeddingCount([true, true], [true]));
        }

        /// <remarks>
        /// Every pair up to length seven on the token side, against enumeration of every choice of
        /// message-length positions. This is the check that the descending inner loop is right: an
        /// ascending one would count embeddings that reuse a token position, and enumeration would not.
        /// </remarks>
        [Fact]
        public void TheEmbeddingCountAgreesWithEnumerationOnEverySmallPair()
        {
            foreach (var message in Sequences(4))
            {
                foreach (var tokens in Sequences(7))
                {
                    Assert.Equal(
                        BruteForceEmbeddingCount(message, tokens),
                        DirectionSequenceAlignment.MonotoneEmbeddingCount(message, tokens));
                }
            }
        }

        /// <remarks>
        /// The cross-check the outcome stage relies on to tie its multiplicity census to its admission
        /// count: a message embeds at all exactly when the ordered alignment carries all of it.
        /// </remarks>
        [Fact]
        public void EmbeddingIsPossibleExactlyWhenTheAlignmentCarriesTheWholeMessage()
        {
            foreach (var message in Sequences(5))
            {
                foreach (var tokens in Sequences(7))
                {
                    var embeddings =
                        DirectionSequenceAlignment.MonotoneEmbeddingCount(message, tokens);

                    var alignment =
                        DirectionSequenceAlignment.LongestCommonSubsequenceLength(message, tokens);

                    Assert.Equal(alignment == message.Length, embeddings > BigInteger.Zero);
                }
            }
        }

        /// <remarks>
        /// The count is bounded by the number of position choices, which is the graded statistic's
        /// denominator. A count above it would make a share above one.
        /// </remarks>
        [Fact]
        public void TheEmbeddingCountNeverExceedsTheChoiceCount()
        {
            foreach (var message in Sequences(4))
            {
                foreach (var tokens in Sequences(7))
                {
                    var embeddings =
                        DirectionSequenceAlignment.MonotoneEmbeddingCount(message, tokens);

                    Assert.True(embeddings >= BigInteger.Zero);

                    Assert.True(
                        embeddings
                            <= DirectionSequenceReference.Binomial(tokens.Length, message.Length));
                }
            }
        }

        /// <remarks>
        /// A one-direction message in a one-direction token side is a pure choice problem, so the count
        /// must be exactly the binomial coefficient — an independent formula agreeing with the dynamic
        /// programme on a case where both are easy to be sure about.
        /// </remarks>
        [Theory]
        [InlineData(1, 4)]
        [InlineData(2, 5)]
        [InlineData(3, 3)]
        [InlineData(4, 9)]
        public void AOneDirectionMessageEmbedsInAsManyWaysAsThereAreChoices(int length, int tokens)
        {
            var message = Enumerable.Repeat(true, length).ToArray();
            var side = Enumerable.Repeat(true, tokens).ToArray();

            Assert.Equal(
                DirectionSequenceReference.Binomial(tokens, length),
                DirectionSequenceAlignment.MonotoneEmbeddingCount(message, side));
        }

        /// <remarks>
        /// The case <see cref="BigInteger"/> is there for. Sixty-six alternating symbols in a
        /// two-thousand-position token side gives a count no fixed-width integer holds, and the
        /// arithmetic must stay exact rather than saturating or overflowing.
        /// </remarks>
        [Fact]
        public void ALargeCountStaysExact()
        {
            var message = Enumerable.Range(0, 66).Select(index => index % 2 == 0).ToArray();
            var tokens = Enumerable.Range(0, 2_000).Select(index => index % 2 == 0).ToArray();

            var count = DirectionSequenceAlignment.MonotoneEmbeddingCount(message, tokens);

            Assert.True(count > BigInteger.Pow(10, 100));
            Assert.True(count <= DirectionSequenceReference.Binomial(2_000, 66));
        }

        // ---------------------------------------------------------------------------------------
        // Reversal and palindromes.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The two named palindrome cases the reversal diagnostic has to be able to explain. A
        /// palindromic message pattern gives the same alignment against a token order and its reverse,
        /// so the comparison is structurally a tie whatever the archive holds.
        /// </remarks>
        [Theory]
        [InlineData(true, false, true)]
        [InlineData(false, true, false)]
        public void APalindromicPatternCannotTellAnOrderFromItsReverse(
            bool first, bool second, bool third)
        {
            bool[] message = [first, second, third];

            Assert.True(DirectionSequenceAlignment.IsPalindrome(message));

            foreach (var tokens in Sequences(7))
            {
                var reversed = DirectionSequenceAlignment.Reversed(tokens);

                Assert.Equal(
                    DirectionSequenceAlignment.LongestCommonSubsequenceLength(message, tokens),
                    DirectionSequenceAlignment.LongestCommonSubsequenceLength(message, reversed));

                Assert.Equal(
                    DirectionSequenceAlignment.MonotoneEmbeddingCount(message, tokens),
                    DirectionSequenceAlignment.MonotoneEmbeddingCount(message, reversed));
            }
        }

        [Fact]
        public void ANonPalindromicPatternIsReportedAsSuch()
        {
            Assert.False(DirectionSequenceAlignment.IsPalindrome([true, false]));
            Assert.True(DirectionSequenceAlignment.IsPalindrome([]));
            Assert.True(DirectionSequenceAlignment.IsPalindrome([true]));
            Assert.True(DirectionSequenceAlignment.IsPalindrome([true, true]));
        }

        /// <remarks>
        /// Reversal preserves the composition and the run count exactly, which is why it cannot act as a
        /// control against a reference conditioned on them: the reversed sequence is a member of the same
        /// class and carries the same <c>q_r</c>.
        /// </remarks>
        [Fact]
        public void ReversalPreservesCompositionAndRunCount()
        {
            foreach (var tokens in Sequences(8))
            {
                var reversed = DirectionSequenceAlignment.Reversed(tokens);

                Assert.Equal(
                    DirectionSequenceReference.CountOutgoing(tokens),
                    DirectionSequenceReference.CountOutgoing(reversed));

                Assert.Equal(
                    DirectionSequenceReference.RunCount(tokens),
                    DirectionSequenceReference.RunCount(reversed));

                Assert.Equal(tokens.Length, reversed.Length);
            }
        }

        [Fact]
        public void ReversedLeavesTheOriginalAlone()
        {
            bool[] original = [true, false, false];

            var reversed = DirectionSequenceAlignment.Reversed(original);

            Assert.Equal([false, false, true], reversed);
            Assert.Equal([true, false, false], original);
        }

        // ---------------------------------------------------------------------------------------
        // Oracles and shapes.
        // ---------------------------------------------------------------------------------------

        /// <summary>Every two-symbol sequence up to <paramref name="maximumLength"/>, shortest first.</summary>
        internal static IEnumerable<bool[]> Sequences(int maximumLength)
        {
            for (var length = 0; length <= maximumLength; length++)
            {
                for (var pattern = 0; pattern < 1 << length; pattern++)
                {
                    var sequence = new bool[length];

                    for (var index = 0; index < length; index++)
                    {
                        sequence[index] = ((pattern >> index) & 1) == 1;
                    }

                    yield return sequence;
                }
            }
        }

        /// <summary>
        /// The longest common subsequence length by enumerating every subsequence of the message.
        /// </summary>
        private static int BruteForceLongestCommonSubsequence(bool[] message, bool[] tokens)
        {
            var best = 0;

            for (var choice = 0; choice < 1 << message.Length; choice++)
            {
                var candidate = new List<bool>();

                for (var index = 0; index < message.Length; index++)
                {
                    if (((choice >> index) & 1) == 1)
                    {
                        candidate.Add(message[index]);
                    }
                }

                if (candidate.Count > best && IsSubsequence([.. candidate], tokens))
                {
                    best = candidate.Count;
                }
            }

            return best;
        }

        /// <summary>
        /// The embedding count by enumerating every choice of positions of the right size.
        /// </summary>
        private static BigInteger BruteForceEmbeddingCount(bool[] message, bool[] tokens)
        {
            var count = BigInteger.Zero;

            for (var choice = 0; choice < 1 << tokens.Length; choice++)
            {
                if (System.Numerics.BitOperations.PopCount((uint)choice) != message.Length)
                {
                    continue;
                }

                var index = 0;
                var matches = true;

                for (var position = 0; position < tokens.Length && matches; position++)
                {
                    if (((choice >> position) & 1) == 0)
                    {
                        continue;
                    }

                    matches = tokens[position] == message[index];
                    index++;
                }

                if (matches && index == message.Length)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Whether <paramref name="candidate"/> appears in order inside <paramref name="within"/>.</summary>
        private static bool IsSubsequence(bool[] candidate, bool[] within)
        {
            var index = 0;

            foreach (var symbol in within)
            {
                if (index < candidate.Length && candidate[index] == symbol)
                {
                    index++;
                }
            }

            return index == candidate.Length;
        }
    }
}
