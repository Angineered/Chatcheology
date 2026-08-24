using System.Numerics;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// The exact observed-order computations Stage B2C-1 adds: how much of a message-direction sequence
    /// the observed token order can carry in order, and how many ways it can carry all of it.
    /// </summary>
    /// <remarks>
    /// Deterministic dynamic programmes over two-symbol sequences, and nothing else. No randomness, no
    /// sampling, no enumeration of alignments or of arrangements, no heuristic shortcut, no length cap:
    /// a cap or a shortcut would silently reduce the frozen population, and the population is not this
    /// code's to change.
    /// <para>
    /// Written as a companion to <see cref="DirectionSequenceReference"/> and in the same style — public
    /// static, exact, no state — so the two halves of the question are testable in the same way. The
    /// reference computes what a class of token orders would give on average; this computes what the
    /// observed order actually gave. Neither knows about the other.
    /// </para>
    /// <para>
    /// Nothing here identifies which token belongs to which message, and a unique embedding is not a
    /// match. These are counts over direction symbols; the symbols carry no identity to resolve.
    /// </para>
    /// </remarks>
    public static class DirectionSequenceAlignment
    {
        /// <summary>
        /// The longest common subsequence length of a message sequence and a token sequence.
        /// </summary>
        /// <remarks>
        /// The ordered direction alignment: how many message symbols the observed token order can carry
        /// without reordering either side. Classic row-wise dynamic programme, with the shorter sequence
        /// on the inner axis so the working set is two rows of <c>min(m, n) + 1</c> integers however
        /// lopsided the pair is. Time is <c>O(m * n)</c>.
        /// <para>
        /// Symmetric in its arguments, which is what makes putting the shorter one inside free rather
        /// than a special case.
        /// </para>
        /// </remarks>
        public static int LongestCommonSubsequenceLength(bool[] message, bool[] tokens)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(tokens);

            if (message.Length == 0 || tokens.Length == 0)
            {
                return 0;
            }

            var inner = message.Length <= tokens.Length ? message : tokens;
            var outer = ReferenceEquals(inner, message) ? tokens : message;

            var previous = new int[inner.Length + 1];
            var current = new int[inner.Length + 1];

            foreach (var symbol in outer)
            {
                for (var index = 1; index <= inner.Length; index++)
                {
                    current[index] = inner[index - 1] == symbol
                        ? previous[index - 1] + 1
                        : Math.Max(previous[index], current[index - 1]);
                }

                (previous, current) = (current, previous);
            }

            return previous[inner.Length];
        }

        /// <summary>
        /// How many ways the whole message sequence embeds monotonically in the token sequence.
        /// </summary>
        /// <remarks>
        /// <c>dp[j]</c> is the number of ways the first <c>j</c> message symbols embed in the token
        /// prefix processed so far, so <c>dp[m]</c> at the end is the number of increasing index tuples
        /// whose token symbols spell the message. Exact, and <see cref="BigInteger"/> because the count
        /// grows combinatorially with token length — it is bounded by <c>C(n, m)</c>, never materialised.
        /// <para>
        /// The inner loop descends deliberately. Ascending would let one token symbol be consumed twice
        /// within a single pass, counting embeddings that reuse a position.
        /// </para>
        /// <para>
        /// Zero exactly when the message does not embed at all, which is exactly when the longest common
        /// subsequence is shorter than the message. The two are computed independently and that identity
        /// is checked rather than assumed.
        /// </para>
        /// </remarks>
        public static BigInteger MonotoneEmbeddingCount(bool[] message, bool[] tokens)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(tokens);

            // The empty message embeds exactly once, in the empty way. Stated rather than falling out,
            // because it is the base case the dynamic programme below relies on.
            if (message.Length == 0)
            {
                return BigInteger.One;
            }

            if (tokens.Length < message.Length)
            {
                return BigInteger.Zero;
            }

            var ways = new BigInteger[message.Length + 1];
            ways[0] = BigInteger.One;

            foreach (var symbol in tokens)
            {
                for (var index = message.Length; index >= 1; index--)
                {
                    if (message[index - 1] == symbol)
                    {
                        ways[index] += ways[index - 1];
                    }
                }
            }

            return ways[message.Length];
        }

        /// <summary>
        /// The most message symbols the token side could carry if order were free.
        /// </summary>
        /// <remarks>
        /// A supply ceiling, per direction: outgoing message symbols cannot exceed outgoing token
        /// positions however they are arranged, and likewise incoming. Order can only lose more.
        /// </remarks>
        public static int DirectionCountCeiling(
            int messageOutgoing, int messageIncoming, int tokenOutgoing, int tokenIncoming)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(messageOutgoing);
            ArgumentOutOfRangeException.ThrowIfNegative(messageIncoming);
            ArgumentOutOfRangeException.ThrowIfNegative(tokenOutgoing);
            ArgumentOutOfRangeException.ThrowIfNegative(tokenIncoming);

            return Math.Min(messageOutgoing, tokenOutgoing) + Math.Min(messageIncoming, tokenIncoming);
        }

        /// <summary>Whether a sequence reads the same in both directions.</summary>
        /// <remarks>
        /// A palindromic message pattern cannot distinguish the observed token order from its reverse for
        /// the embedding or alignment question, so the reversal diagnostic has to be able to say how much
        /// of its population is structurally unable to answer.
        /// </remarks>
        public static bool IsPalindrome(bool[] sequence)
        {
            ArgumentNullException.ThrowIfNull(sequence);

            for (var (low, high) = (0, sequence.Length - 1); low < high; low++, high--)
            {
                if (sequence[low] != sequence[high])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>A reversed copy, leaving the original alone.</summary>
        public static bool[] Reversed(bool[] sequence)
        {
            ArgumentNullException.ThrowIfNull(sequence);

            var reversed = new bool[sequence.Length];

            for (var index = 0; index < sequence.Length; index++)
            {
                reversed[index] = sequence[sequence.Length - 1 - index];
            }

            return reversed;
        }
    }
}
