using System.Numerics;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// The exact combinatorial quantities the direction-sequence gate conditions on: how many
    /// two-symbol arrangements share an observed composition and burstiness, how many of them admit a
    /// message pattern, how many monotone embeddings they hold between them, and whether that
    /// embedding count is constant across the class.
    /// </summary>
    /// <remarks>
    /// Every quantity here is a property of the message pattern and of the token side's own
    /// <c>(outgoing, incoming, runs)</c> class. None of them looks at the observed token order, so
    /// none of them can carry an alignment outcome. That is what makes them legal in the gate.
    /// <para>
    /// Exact integers throughout, in <see cref="BigInteger"/>. No randomness, no sampled
    /// permutations, no floating point: the classifications are integer comparisons
    /// (<c>S = 0</c>, <c>S = A</c>, <c>A * Q = P * P</c>), and a tolerance would turn any of them
    /// into a judgement call.
    /// </para>
    /// <para>
    /// <b>How the counting is organised, and why not symbol by symbol.</b> An arrangement in the
    /// class is exactly a choice of starting direction plus positive lengths for its <c>r</c>
    /// alternating runs. Each count below is therefore a dynamic programme over the <em>runs</em>,
    /// carrying how far the message pattern has been consumed, with the run <em>lengths</em> summed in
    /// closed form rather than enumerated. The closed forms are single coefficient extractions:
    /// <c>sum over L &gt;= 1 of C(L, t) x^L</c> is <c>x^t / (1 - x)^(t + 1)</c> for <c>t &gt;= 1</c>
    /// and <c>x / (1 - x)</c> for <c>t = 0</c>, so one direction's whole set of lengths collapses to
    /// one binomial. The alternative — a state carrying outgoing-remaining and incoming-remaining per
    /// emitted symbol — is the same mathematics, but its state space is the product of the two
    /// remaining counts with the run count, which at this archive's per-date token counts is not
    /// computable. The state used here is sufficient in the sense that matters: it separates every
    /// case the counts depend on, and it is verified against direct enumeration of entire classes on
    /// every small shape.
    /// </para>
    /// <para>
    /// A pattern is a <c>bool</c> array in which <see langword="true"/> is outgoing. No token-side
    /// arrangement is ever materialised.
    /// </para>
    /// </remarks>
    public static class DirectionSequenceReference
    {
        /// <summary>The two starting directions a run structure may take.</summary>
        private static readonly bool[] StartingDirections = [true, false];

        /// <summary>
        /// <c>C(n, k)</c>, and zero wherever the choice is impossible rather than an error.
        /// </summary>
        /// <remarks>
        /// Zero for a negative <paramref name="n"/> or <paramref name="k"/> and for
        /// <c>k &gt; n</c>, because every use below is a coefficient extraction whose out-of-range
        /// cases are genuinely empty. Guarding them here keeps the callers free of arithmetic that
        /// exists only to avoid an exception.
        /// </remarks>
        public static BigInteger Binomial(int n, int k)
        {
            if (n < 0 || k < 0 || k > n)
            {
                return BigInteger.Zero;
            }

            var smaller = Math.Min(k, n - k);
            var result = BigInteger.One;

            for (var step = 1; step <= smaller; step++)
            {
                result = result * (n - smaller + step) / step;
            }

            return result;
        }

        /// <summary>
        /// The falling factorial <c>(value)_length</c>, which the exchangeable graded closed form is
        /// written in.
        /// </summary>
        public static BigInteger FallingFactorial(int value, int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);

            var result = BigInteger.One;

            for (var step = 0; step < length; step++)
            {
                result *= value - step;
            }

            return result;
        }

        /// <summary>
        /// The exchangeable expected number of direction runs for a composition, which the observed
        /// run count is compared against as a burstiness diagnostic.
        /// </summary>
        /// <remarks>
        /// <c>1 + 2 * o * i / (o + i)</c>. Zero for an empty sequence, which has no runs at all.
        /// </remarks>
        public static double ExpectedRunCount(int outgoing, int incoming)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(outgoing);
            ArgumentOutOfRangeException.ThrowIfNegative(incoming);

            var total = outgoing + incoming;

            return total == 0 ? 0d : 1d + (2d * outgoing * incoming / total);
        }

        /// <summary>
        /// <c>A(o, i, r)</c> — arrangements with exactly this composition and this many runs.
        /// </summary>
        /// <remarks>
        /// A run structure is a starting direction plus positive lengths for its runs, so this is a
        /// sum of two products of composition counts. Summed over every <c>r</c> it must equal
        /// <c>C(o + i, o)</c>, which is one of the required cross-checks.
        /// </remarks>
        public static BigInteger ArrangementCount(int outgoing, int incoming, int runCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(outgoing);
            ArgumentOutOfRangeException.ThrowIfNegative(incoming);
            ArgumentOutOfRangeException.ThrowIfNegative(runCount);

            if (runCount == 0)
            {
                return outgoing == 0 && incoming == 0 ? BigInteger.One : BigInteger.Zero;
            }

            var total = BigInteger.Zero;

            foreach (var startsOutgoing in StartingDirections)
            {
                var (outgoingRuns, incomingRuns) = RunCounts(runCount, startsOutgoing);

                total +=
                    CompositionCount(outgoing, outgoingRuns)
                    * CompositionCount(incoming, incomingRuns);
            }

            return total;
        }

        /// <summary>
        /// <c>S(p, o, i, r)</c> — arrangements of that class which admit <paramref name="pattern"/>
        /// as a subsequence.
        /// </summary>
        /// <remarks>
        /// Counted through each arrangement's <em>leftmost</em> embedding, which is unique, so no
        /// arrangement is counted twice and none that admits the pattern is missed. Within one run of
        /// direction <c>d</c> the leftmost match consumes <c>min(length, k)</c> pattern symbols, where
        /// <c>k</c> is the length of the pattern's own maximal <c>d</c>-block at the current position.
        /// Each run therefore falls in exactly one of three cases — no progress with a free length,
        /// progress of <c>v &lt; k</c> with the length pinned to <c>v</c>, or progress of <c>k</c>
        /// with the length free from <c>k</c> upwards — and the free lengths are summed in closed
        /// form once every run is placed.
        /// </remarks>
        public static BigInteger AdmittingArrangementCount(
            bool[] pattern, int outgoing, int incoming, int runCount)
        {
            ArgumentNullException.ThrowIfNull(pattern);
            ArgumentOutOfRangeException.ThrowIfNegative(outgoing);
            ArgumentOutOfRangeException.ThrowIfNegative(incoming);
            ArgumentOutOfRangeException.ThrowIfNegative(runCount);

            var length = pattern.Length;

            if (runCount == 0)
            {
                return outgoing == 0 && incoming == 0 && length == 0
                    ? BigInteger.One
                    : BigInteger.Zero;
            }

            var total = BigInteger.Zero;

            foreach (var startsOutgoing in StartingDirections)
            {
                var (outgoingRuns, incomingRuns) = RunCounts(runCount, startsOutgoing);

                var states = new Dictionary<GreedyState, BigInteger>
                {
                    [default] = BigInteger.One,
                };

                for (var run = 1; run <= runCount; run++)
                {
                    var direction = DirectionOfRun(run, startsOutgoing);
                    var remainingRuns = runCount - run;
                    var next = new Dictionary<GreedyState, BigInteger>();

                    foreach (var (state, ways) in states)
                    {
                        if (state.PatternPosition == length
                            || pattern[state.PatternPosition] != direction)
                        {
                            // No length gives any progress here, so the state carries over unchanged:
                            // the free-length case with a lower bound of one.
                            Accumulate(next, state, ways);

                            continue;
                        }

                        // Leftmost matching is deterministic, so a run whose direction the pattern
                        // wants always advances. The state cannot also stay as it is — one of the
                        // branches below is what happened.
                        var block = PatternBlockLength(pattern, state.PatternPosition, direction);

                        for (var consumed = 1; consumed <= block; consumed++)
                        {
                            var pinned = consumed < block;

                            var advanced = state.Advance(
                                direction,
                                consumed,
                                pinned ? consumed : consumed - 1,
                                pinned);

                            if (!CanStillFinish(pattern, advanced.PatternPosition, remainingRuns))
                            {
                                continue;
                            }

                            Accumulate(next, advanced, ways);
                        }
                    }

                    states = next;
                }

                foreach (var (state, ways) in states)
                {
                    if (state.PatternPosition != length)
                    {
                        continue;
                    }

                    var freeOutgoing = outgoingRuns - state.PinnedOutgoingRuns;
                    var freeIncoming = incomingRuns - state.PinnedIncomingRuns;

                    if (freeOutgoing < 0 || freeIncoming < 0)
                    {
                        continue;
                    }

                    total +=
                        ways
                        * FreeLengthCount(outgoing, state.OutgoingLowerBound, freeOutgoing)
                        * FreeLengthCount(incoming, state.IncomingLowerBound, freeIncoming);
                }
            }

            return total;
        }

        /// <summary>
        /// <c>P(p, o, i, r)</c> — <c>(arrangement, embedding)</c> pairs over that class.
        /// </summary>
        /// <remarks>
        /// The graded reference's numerator: summed over the class, the number of increasing index
        /// tuples whose symbols spell <paramref name="pattern"/>. The run dynamic programme carries
        /// how much of the pattern each run absorbs and how many runs of each direction absorbed
        /// anything; the lengths and the within-run position choices are then one coefficient
        /// extraction per direction.
        /// </remarks>
        public static BigInteger EmbeddingPairCount(
            bool[] pattern, int outgoing, int incoming, int runCount)
        {
            ArgumentNullException.ThrowIfNull(pattern);
            ArgumentOutOfRangeException.ThrowIfNegative(outgoing);
            ArgumentOutOfRangeException.ThrowIfNegative(incoming);
            ArgumentOutOfRangeException.ThrowIfNegative(runCount);

            var length = pattern.Length;

            if (runCount == 0)
            {
                return outgoing == 0 && incoming == 0 && length == 0
                    ? BigInteger.One
                    : BigInteger.Zero;
            }

            var outgoingSymbols = CountOutgoing(pattern);
            var incomingSymbols = length - outgoingSymbols;

            var total = BigInteger.Zero;

            foreach (var startsOutgoing in StartingDirections)
            {
                var (outgoingRuns, incomingRuns) = RunCounts(runCount, startsOutgoing);

                var states = new Dictionary<EmbeddingState, BigInteger>
                {
                    [default] = BigInteger.One,
                };

                for (var run = 1; run <= runCount; run++)
                {
                    var direction = DirectionOfRun(run, startsOutgoing);
                    var remainingRuns = runCount - run;

                    foreach (var (state, ways) in states.ToArray())
                    {
                        var block = state.PatternPosition == length
                            ? 0
                            : PatternBlockLength(pattern, state.PatternPosition, direction);

                        for (var consumed = 1; consumed <= block; consumed++)
                        {
                            var next = state.Absorb(direction, consumed);

                            if (!CanStillFinish(pattern, next.PatternPosition, remainingRuns))
                            {
                                continue;
                            }

                            Accumulate(states, next, ways);
                        }
                    }
                }

                foreach (var (state, ways) in states)
                {
                    if (state.PatternPosition != length)
                    {
                        continue;
                    }

                    var emptyOutgoing = outgoingRuns - state.UsedOutgoingRuns;
                    var emptyIncoming = incomingRuns - state.UsedIncomingRuns;

                    if (emptyOutgoing < 0 || emptyIncoming < 0)
                    {
                        continue;
                    }

                    total +=
                        ways
                        * LengthAndChoiceCount(
                            outgoing, outgoingRuns, emptyOutgoing, outgoingSymbols)
                        * LengthAndChoiceCount(
                            incoming, incomingRuns, emptyIncoming, incomingSymbols);
                }
            }

            return total;
        }

        /// <summary>
        /// <c>Q(p, o, i, r)</c> — the sum over the class of the <em>square</em> of each arrangement's
        /// embedding count.
        /// </summary>
        /// <remarks>
        /// Two independent embeddings are tracked over one arrangement. Within a run the two take
        /// <c>t1</c> and <c>t2</c> of its positions, and <c>C(L, t1) * C(L, t2)</c> is re-expressed as
        /// <c>sum over s of C(L, s) * C(s, t1) * C(t1, s - t2)</c>, where <c>s</c> is the size of the
        /// union of the two chosen sets. That leaves one power of <c>L</c> per run, so the lengths
        /// collapse by the same coefficient extraction <c>P</c> uses.
        /// <para>
        /// <c>Q</c> exists to decide one boolean — whether <c>A * Q = P * P</c>, the Cauchy–Schwarz
        /// equality case, and therefore whether every arrangement in the class carries the identical
        /// embedding count. It is not a variance, a dispersion, a spread, a confidence measure or a
        /// significance quantity, and nothing derived from it belongs in a reported result.
        /// </para>
        /// </remarks>
        public static BigInteger SquaredEmbeddingCount(
            bool[] pattern, int outgoing, int incoming, int runCount)
        {
            ArgumentNullException.ThrowIfNull(pattern);
            ArgumentOutOfRangeException.ThrowIfNegative(outgoing);
            ArgumentOutOfRangeException.ThrowIfNegative(incoming);
            ArgumentOutOfRangeException.ThrowIfNegative(runCount);

            var length = pattern.Length;

            if (runCount == 0)
            {
                return outgoing == 0 && incoming == 0 && length == 0
                    ? BigInteger.One
                    : BigInteger.Zero;
            }

            var total = BigInteger.Zero;

            foreach (var startsOutgoing in StartingDirections)
            {
                var (outgoingRuns, incomingRuns) = RunCounts(runCount, startsOutgoing);

                var states = new Dictionary<PairedEmbeddingState, BigInteger>
                {
                    [default] = BigInteger.One,
                };

                for (var run = 1; run <= runCount; run++)
                {
                    var direction = DirectionOfRun(run, startsOutgoing);
                    var remainingRuns = runCount - run;

                    foreach (var (state, ways) in states.ToArray())
                    {
                        var firstBlock = state.FirstPosition == length
                            ? 0
                            : PatternBlockLength(pattern, state.FirstPosition, direction);

                        var secondBlock = state.SecondPosition == length
                            ? 0
                            : PatternBlockLength(pattern, state.SecondPosition, direction);

                        for (var first = 0; first <= firstBlock; first++)
                        {
                            for (var second = 0; second <= secondBlock; second++)
                            {
                                if (first == 0 && second == 0)
                                {
                                    // The run holds no position of either embedding, so it
                                    // contributes only a free length and the state stays as it is.
                                    continue;
                                }

                                AccumulateUnionSizes(
                                    pattern,
                                    states,
                                    state,
                                    ways,
                                    direction,
                                    first,
                                    second,
                                    remainingRuns);
                            }
                        }
                    }
                }

                foreach (var (state, ways) in states)
                {
                    if (state.FirstPosition != length || state.SecondPosition != length)
                    {
                        continue;
                    }

                    var emptyOutgoing = outgoingRuns - state.UsedOutgoingRuns;
                    var emptyIncoming = incomingRuns - state.UsedIncomingRuns;

                    if (emptyOutgoing < 0 || emptyIncoming < 0)
                    {
                        continue;
                    }

                    total +=
                        ways
                        * LengthAndChoiceCount(
                            outgoing, outgoingRuns, emptyOutgoing, state.OutgoingUnionTotal)
                        * LengthAndChoiceCount(
                            incoming, incomingRuns, emptyIncoming, state.IncomingUnionTotal);
                }
            }

            return total;
        }

        /// <summary>
        /// The exchangeable admitting count: arrangements with this composition, whatever their
        /// burstiness, which admit <paramref name="pattern"/>.
        /// </summary>
        /// <remarks>
        /// The earlier composition-only reference, retained for one purpose — the <c>q - q_r</c>
        /// comparison that says how much of an apparent order effect burstiness alone accounts for.
        /// Summed over every run count, <c>S</c> must equal this, which is the second required
        /// cross-check.
        /// <para>
        /// Symbol by symbol here, because the composition alone is a small enough state to need no
        /// closed form, and because an independently written second mechanism is a stronger check on
        /// the run-structured one than a variation of it would be.
        /// </para>
        /// </remarks>
        public static BigInteger ExchangeableAdmittingCount(
            bool[] pattern, int outgoing, int incoming)
        {
            ArgumentNullException.ThrowIfNull(pattern);
            ArgumentOutOfRangeException.ThrowIfNegative(outgoing);
            ArgumentOutOfRangeException.ThrowIfNegative(incoming);

            var length = pattern.Length;
            var table = new BigInteger[outgoing + 1, incoming + 1, length + 1];

            for (var position = 0; position <= length; position++)
            {
                table[0, 0, position] = position == length ? BigInteger.One : BigInteger.Zero;
            }

            for (var outgoingUsed = 0; outgoingUsed <= outgoing; outgoingUsed++)
            {
                for (var incomingUsed = 0; incomingUsed <= incoming; incomingUsed++)
                {
                    if (outgoingUsed == 0 && incomingUsed == 0)
                    {
                        continue;
                    }

                    for (var position = 0; position <= length; position++)
                    {
                        var ways = BigInteger.Zero;

                        if (outgoingUsed > 0)
                        {
                            ways += table[
                                outgoingUsed - 1,
                                incomingUsed,
                                AdvancePosition(pattern, position, direction: true)];
                        }

                        if (incomingUsed > 0)
                        {
                            ways += table[
                                outgoingUsed,
                                incomingUsed - 1,
                                AdvancePosition(pattern, position, direction: false)];
                        }

                        table[outgoingUsed, incomingUsed, position] = ways;
                    }
                }
            }

            return table[outgoing, incoming, 0];
        }

        /// <summary>
        /// How many outgoing symbols a pattern holds, which is all the exchangeable graded closed
        /// form depends on.
        /// </summary>
        public static int CountOutgoing(bool[] pattern)
        {
            ArgumentNullException.ThrowIfNull(pattern);

            var count = 0;

            foreach (var symbol in pattern)
            {
                if (symbol)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>How many maximal direction runs a sequence holds.</summary>
        public static int RunCount(bool[] sequence)
        {
            ArgumentNullException.ThrowIfNull(sequence);

            if (sequence.Length == 0)
            {
                return 0;
            }

            var runs = 1;

            for (var position = 1; position < sequence.Length; position++)
            {
                if (sequence[position] != sequence[position - 1])
                {
                    runs++;
                }
            }

            return runs;
        }

        /// <summary>
        /// How many direction transitions a sequence holds, which is its run count less one.
        /// </summary>
        public static int TransitionCount(bool[] sequence) => Math.Max(0, RunCount(sequence) - 1);

        // -----------------------------------------------------------------------------------------
        // Shared closed forms and dynamic-programme plumbing.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// How many ordered tuples of <paramref name="parts"/> positive integers sum to
        /// <paramref name="total"/>.
        /// </summary>
        internal static BigInteger CompositionCount(int total, int parts) =>
            parts == 0
                ? total == 0 ? BigInteger.One : BigInteger.Zero
                : Binomial(total - 1, parts - 1);

        /// <summary>
        /// How many outgoing and incoming runs a structure of <paramref name="runCount"/> runs holds,
        /// given which direction it starts with.
        /// </summary>
        private static (int Outgoing, int Incoming) RunCounts(int runCount, bool startsOutgoing) =>
            startsOutgoing
                ? ((runCount + 1) / 2, runCount / 2)
                : (runCount / 2, (runCount + 1) / 2);

        /// <summary>The direction of a one-based run index.</summary>
        private static bool DirectionOfRun(int runIndex, bool startsOutgoing) =>
            runIndex % 2 == 1 ? startsOutgoing : !startsOutgoing;

        /// <summary>
        /// The length of the pattern's maximal block of <paramref name="direction"/> beginning at
        /// <paramref name="position"/>, which is zero when the pattern turns the other way there.
        /// </summary>
        private static int PatternBlockLength(bool[] pattern, int position, bool direction)
        {
            var length = 0;

            while (position + length < pattern.Length && pattern[position + length] == direction)
            {
                length++;
            }

            return length;
        }

        /// <summary>
        /// Where leftmost matching stands after one emitted symbol of <paramref name="direction"/>.
        /// </summary>
        private static int AdvancePosition(bool[] pattern, int position, bool direction) =>
            position < pattern.Length && pattern[position] == direction ? position + 1 : position;

        /// <summary>
        /// Whether the pattern's remainder can still be placed in the runs that are left.
        /// </summary>
        /// <remarks>
        /// A pure pruning test, and a conservative one: the remainder needs at least one run per
        /// direction block it contains, so a state with fewer runs left than that can never reach an
        /// accepting configuration. Dropping it changes no count, and it keeps dead branches from
        /// being carried through the rest of the run structure.
        /// </remarks>
        private static bool CanStillFinish(bool[] pattern, int position, int remainingRuns)
        {
            if (position == pattern.Length)
            {
                return true;
            }

            var blocks = 1;

            for (var index = position + 1; index < pattern.Length; index++)
            {
                if (pattern[index] != pattern[index - 1])
                {
                    blocks++;
                }
            }

            return blocks <= remainingRuns;
        }

        /// <summary>
        /// The number of free run-length assignments for one direction, given the lengths already
        /// pinned and the lower bounds its free runs carry.
        /// </summary>
        /// <remarks>
        /// Every free run carries a lower bound of at least one, and
        /// <paramref name="lowerBoundExcess"/> is the total of the pinned lengths plus whatever each
        /// free run's own bound exceeds one by. With no free runs at all the pinned lengths must
        /// account for the whole total exactly.
        /// </remarks>
        private static BigInteger FreeLengthCount(int total, int lowerBoundExcess, int freeRuns) =>
            freeRuns == 0
                ? total == lowerBoundExcess ? BigInteger.One : BigInteger.Zero
                : Binomial(total - lowerBoundExcess - 1, freeRuns - 1);

        /// <summary>
        /// One direction's contribution to <c>P</c> or <c>Q</c>: its run lengths and its within-run
        /// position choices, summed in closed form.
        /// </summary>
        /// <remarks>
        /// <c>C(total + runs - 1 - emptyRuns, chosen + runs - 1)</c>, from multiplying
        /// <c>x^t / (1 - x)^(t + 1)</c> over the runs that hold chosen positions and
        /// <c>x / (1 - x)</c> over those that hold none, then reading off the coefficient of
        /// <c>x^total</c>. With no runs of this direction at all, nothing of it may be required.
        /// </remarks>
        private static BigInteger LengthAndChoiceCount(
            int total, int runs, int emptyRuns, int chosen) =>
            runs == 0
                ? total == 0 && chosen == 0 ? BigInteger.One : BigInteger.Zero
                : Binomial(total + runs - 1 - emptyRuns, chosen + runs - 1);

        private static void Accumulate<TState>(
            Dictionary<TState, BigInteger> states, TState state, BigInteger ways)
            where TState : notnull =>
            states[state] = states.TryGetValue(state, out var existing) ? existing + ways : ways;

        /// <summary>
        /// Adds every union size the two embeddings' choices within one run can have, each with its
        /// weight.
        /// </summary>
        private static void AccumulateUnionSizes(
            bool[] pattern,
            Dictionary<PairedEmbeddingState, BigInteger> states,
            PairedEmbeddingState state,
            BigInteger ways,
            bool direction,
            int first,
            int second,
            int remainingRuns)
        {
            for (var union = Math.Max(first, second); union <= first + second; union++)
            {
                var weight = Binomial(union, first) * Binomial(first, union - second);

                if (weight.IsZero)
                {
                    continue;
                }

                var next = state.Absorb(direction, first, second, union);

                if (!CanStillFinish(pattern, next.FirstPosition, remainingRuns)
                    || !CanStillFinish(pattern, next.SecondPosition, remainingRuns))
                {
                    continue;
                }

                Accumulate(states, next, ways * weight);
            }
        }

        /// <summary>
        /// The <c>S</c> state: pattern progress, and per direction how many runs had their length
        /// pinned and how much length is committed beyond one per free run.
        /// </summary>
        private readonly record struct GreedyState(
            int PatternPosition,
            int PinnedOutgoingRuns,
            int OutgoingLowerBound,
            int PinnedIncomingRuns,
            int IncomingLowerBound)
        {
            internal GreedyState Advance(
                bool direction, int consumed, int lowerBound, bool pinned) =>
                direction
                    ? this with
                    {
                        PatternPosition = PatternPosition + consumed,
                        PinnedOutgoingRuns = PinnedOutgoingRuns + (pinned ? 1 : 0),
                        OutgoingLowerBound = OutgoingLowerBound + lowerBound,
                    }
                    : this with
                    {
                        PatternPosition = PatternPosition + consumed,
                        PinnedIncomingRuns = PinnedIncomingRuns + (pinned ? 1 : 0),
                        IncomingLowerBound = IncomingLowerBound + lowerBound,
                    };
        }

        /// <summary>
        /// The <c>P</c> state: pattern progress, and how many runs of each direction absorbed any of
        /// it.
        /// </summary>
        private readonly record struct EmbeddingState(
            int PatternPosition, int UsedOutgoingRuns, int UsedIncomingRuns)
        {
            internal EmbeddingState Absorb(bool direction, int consumed) =>
                direction
                    ? this with
                    {
                        PatternPosition = PatternPosition + consumed,
                        UsedOutgoingRuns = UsedOutgoingRuns + 1,
                    }
                    : this with
                    {
                        PatternPosition = PatternPosition + consumed,
                        UsedIncomingRuns = UsedIncomingRuns + 1,
                    };
        }

        /// <summary>
        /// The <c>Q</c> state: two independent pattern progresses, and per direction the union sizes
        /// accumulated with how many runs contributed one.
        /// </summary>
        private readonly record struct PairedEmbeddingState(
            int FirstPosition,
            int SecondPosition,
            int OutgoingUnionTotal,
            int UsedOutgoingRuns,
            int IncomingUnionTotal,
            int UsedIncomingRuns)
        {
            internal PairedEmbeddingState Absorb(
                bool direction, int first, int second, int union) =>
                direction
                    ? this with
                    {
                        FirstPosition = FirstPosition + first,
                        SecondPosition = SecondPosition + second,
                        OutgoingUnionTotal = OutgoingUnionTotal + union,
                        UsedOutgoingRuns = UsedOutgoingRuns + 1,
                    }
                    : this with
                    {
                        FirstPosition = FirstPosition + first,
                        SecondPosition = SecondPosition + second,
                        IncomingUnionTotal = IncomingUnionTotal + union,
                        UsedIncomingRuns = UsedIncomingRuns + 1,
                    };
        }
    }
}
