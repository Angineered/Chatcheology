using System.Diagnostics;
using System.Numerics;
using Chatcheology.Data.Sequence;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Establishes the execution envelope of the outcome computations on synthetic shapes, including the
    /// absolute bound the grammar allows.
    /// </summary>
    /// <remarks>
    /// Synthetic only. No real date is measured, and no shape here was chosen by looking at an outcome:
    /// the message-side maximum comes from the accepted gate census and the token-side bound is proved by
    /// the grammar — a supported token is exactly four ASCII digits, so one scope and date holds at most
    /// ten thousand logical positions after collapse.
    /// <para>
    /// The timings are generous. They exist to catch a hidden combinatorial path — an enumeration, a
    /// materialised set of choices, a quadratic allocation — rather than to police milliseconds on
    /// whatever machine happens to run them.
    /// </para>
    /// </remarks>
    public class DirectionSequenceAlignmentPerformanceTests
    {
        /// <summary>The archive's longest message-direction sequence.</summary>
        private const int MaximumMessageLength = 66;

        /// <summary>
        /// The most logical positions one scope and date can hold, from the grammar's four digits.
        /// </summary>
        private const int MaximumTokenPositions = 10_000;

        /// <summary>
        /// The most comparison opportunities the frozen populations create.
        /// </summary>
        /// <remarks>
        /// The order-testable population, once for its own date and once for each neighbouring calendar
        /// date. The aggregate is what a multi-hour all-or-nothing run actually spends, not the single
        /// worst pair.
        /// </remarks>
        private const int MaximumComparisons = 1_294 * 3;

        // ---------------------------------------------------------------------------------------
        // The single-pair extreme.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TheAbsoluteBoundShapeFinishesQuicklyAndExactly()
        {
            var message = Alternating(MaximumMessageLength);
            var tokens = Alternating(MaximumTokenPositions);

            var elapsed = Stopwatch.StartNew();

            var alignment = DirectionSequenceAlignment.LongestCommonSubsequenceLength(
                message, tokens);

            var embeddings = DirectionSequenceAlignment.MonotoneEmbeddingCount(message, tokens);

            elapsed.Stop();

            Assert.Equal(MaximumMessageLength, alignment);
            Assert.True(embeddings > BigInteger.Zero);

            Assert.True(
                embeddings
                    <= DirectionSequenceReference.Binomial(
                        MaximumTokenPositions, MaximumMessageLength));

            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(10),
                $"the absolute-bound shape took {elapsed.Elapsed}");
        }

        /// <remarks>
        /// The dynamic programmes keep two rows and one row respectively, so working set must not scale
        /// with the product of the two lengths. Measured as allocation rather than as time, because a
        /// materialised alignment table is what would show up here.
        /// </remarks>
        [Fact]
        public void TheDynamicProgrammesAllocateNoTable()
        {
            var message = Alternating(MaximumMessageLength);
            var tokens = Alternating(MaximumTokenPositions);

            // Warm, so the measurement is of the work rather than of first-call setup.
            _ = DirectionSequenceAlignment.LongestCommonSubsequenceLength(message, tokens);
            _ = DirectionSequenceAlignment.MonotoneEmbeddingCount(message, tokens);

            var before = GC.GetAllocatedBytesForCurrentThread();

            _ = DirectionSequenceAlignment.LongestCommonSubsequenceLength(message, tokens);

            var alignmentAllocation = GC.GetAllocatedBytesForCurrentThread() - before;

            // Two rows of sixty-seven integers is well under a kilobyte; a table would be megabytes.
            Assert.True(
                alignmentAllocation < 64 * 1024,
                $"the alignment allocated {alignmentAllocation} bytes");
        }

        // ---------------------------------------------------------------------------------------
        // The aggregate workload.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The archive's median message length is two and its maximum is sixty-six, so the aggregate cost
        /// is dominated by a great many short comparisons rather than by a few long ones. This runs the
        /// full number of comparison opportunities the frozen populations create.
        /// </remarks>
        [Fact]
        public void TheAggregateComparisonWorkloadFinishesQuickly()
        {
            var shapes = SyntheticPopulation(MaximumComparisons);

            var elapsed = Stopwatch.StartNew();

            var admissions = 0;

            foreach (var (message, tokens) in shapes)
            {
                if (DirectionSequenceAlignment.MonotoneEmbeddingCount(message, tokens)
                    > BigInteger.Zero)
                {
                    admissions++;
                }

                _ = DirectionSequenceAlignment.LongestCommonSubsequenceLength(message, tokens);
            }

            elapsed.Stop();

            Assert.True(admissions > 0);

            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(60),
                $"{MaximumComparisons} comparisons took {elapsed.Elapsed}");
        }

        /// <remarks>
        /// Enough of the absolute-bound shape to expose aggregate allocation and loop cost rather than one
        /// extreme pair. Kept to the median message length, which is where the real population sits.
        /// </remarks>
        [Fact]
        public void ManyAbsoluteBoundTokenSidesFinishQuickly()
        {
            var message = Alternating(2);
            var tokens = Alternating(MaximumTokenPositions);

            var elapsed = Stopwatch.StartNew();

            for (var repetition = 0; repetition < 200; repetition++)
            {
                _ = DirectionSequenceAlignment.MonotoneEmbeddingCount(message, tokens);
                _ = DirectionSequenceAlignment.LongestCommonSubsequenceLength(message, tokens);
            }

            elapsed.Stop();

            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(30),
                $"two hundred bound-shape comparisons took {elapsed.Elapsed}");
        }

        // ---------------------------------------------------------------------------------------
        // The reference cache.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The exact reference is the expensive part of the stage, and the adjacent-date controls ask for
        /// one class per pair per neighbour. The cache needs no eviction policy because the number of
        /// distinct keys a run can ask for is bounded by that count.
        /// <para>
        /// The admission path is the one every binary comparison takes, and it is cheap: the class size
        /// and the admitting count are quick even at large run counts.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheAdmissionCacheIsBoundedByTheComparisonsARunCanAskFor()
        {
            var cache = new DirectionSequenceReferenceCache();

            var elapsed = Stopwatch.StartNew();

            foreach (var (message, tokens) in SyntheticPopulation(MaximumComparisons))
            {
                var outgoing = DirectionSequenceReference.CountOutgoing(tokens);

                _ = cache.ForAdmission(
                    message,
                    outgoing,
                    tokens.Length - outgoing,
                    DirectionSequenceReference.RunCount(tokens));
            }

            elapsed.Stop();

            Assert.True(cache.Count <= MaximumComparisons);
            Assert.Equal(MaximumComparisons, cache.Hits + cache.Misses);

            // Short patterns repeat, so a run of this shape must actually hit.
            Assert.True(cache.Hits > 0);

            Assert.True(
                elapsed.Elapsed < TimeSpan.FromMinutes(5),
                $"{MaximumComparisons} admission references took {elapsed.Elapsed}");
        }

        /// <remarks>
        /// A hit must return the identical values the dynamic programme would have produced, because the
        /// key is the whole input the reference depends on.
        /// </remarks>
        [Fact]
        public void ACachedClassEqualsTheUncachedOne()
        {
            var cached = new DirectionSequenceReferenceCache();
            var uncached = DirectionSequenceReferenceCache.Disabled;

            foreach (var (message, tokens) in SyntheticPopulation(400))
            {
                var outgoing = DirectionSequenceReference.CountOutgoing(tokens);
                var incoming = tokens.Length - outgoing;
                var runs = DirectionSequenceReference.RunCount(tokens);

                Assert.Equal(
                    uncached.ForAdmission(message, outgoing, incoming, runs),
                    cached.ForAdmission(message, outgoing, incoming, runs));
            }

            Assert.Equal(0, uncached.Count);
            Assert.True(cached.Count > 0);
        }

        // ---------------------------------------------------------------------------------------
        // The squared-embedding bound.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The squared-embedding count is the stage's one genuinely expensive quantity, and the graded
        /// adjacent-date control is the only thing that asks for a class the accepted gate never computed:
        /// an own-date pattern against a neighbouring date's run structure. Beyond the measured practical
        /// bound the computation is not slow, it is impossible — the largest shape measured allocated
        /// three and a half gigabytes — so the run stops and says so.
        /// </remarks>
        [Fact]
        public void AGradedClassBeyondThePracticalBoundStopsTheRun()
        {
            var cache = new DirectionSequenceReferenceCache();

            var pattern = Alternating(66);

            var failure = Assert.Throws<InvalidOperationException>(
                () => cache.ForShare(pattern, 5_000, 5_000, 5_000));

            Assert.Contains("design-review condition", failure.Message, StringComparison.Ordinal);
            Assert.Contains("cannot be approximated", failure.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// Nothing is dropped to avoid the bound. A stop is a stop: the alternative — quietly excluding
        /// the pair from its control — would shrink a population because of a resource limit, which is
        /// exactly the silent cap the stage forbids.
        /// </remarks>
        [Fact]
        public void TheBoundIsAStopRatherThanACap()
        {
            var cache = new DirectionSequenceReferenceCache();

            // Inside the bound: computed exactly.
            Assert.True(
                DirectionSequenceReferenceCache.SquaredEmbeddingCostOf(6, 100)
                    <= DirectionSequenceReferenceCache.MaximumSquaredEmbeddingCost);

            var values = cache.ForShare(Alternating(6), 100, 100, 100);

            Assert.True(values.ArrangementCount > BigInteger.Zero);

            // Outside it: refused, and refused again rather than silently answered from a cache.
            Assert.Throws<InvalidOperationException>(
                () => cache.ForShare(Alternating(66), 5_000, 5_000, 5_000));

            Assert.Throws<InvalidOperationException>(
                () => cache.ForShare(Alternating(66), 5_000, 5_000, 5_000));
        }

        [Fact]
        public void TheCostProxyIsThePatternLengthToTheFourthTimesTheRunCount()
        {
            Assert.Equal(
                16L * 1_000, DirectionSequenceReferenceCache.SquaredEmbeddingCostOf(2, 1_000));

            Assert.Equal(
                1_296L * 1_000, DirectionSequenceReferenceCache.SquaredEmbeddingCostOf(6, 1_000));

            // The measured shapes either side of the bound.
            Assert.True(
                DirectionSequenceReferenceCache.SquaredEmbeddingCostOf(8, 500)
                    <= DirectionSequenceReferenceCache.MaximumSquaredEmbeddingCost);

            Assert.True(
                DirectionSequenceReferenceCache.SquaredEmbeddingCostOf(12, 250)
                    > DirectionSequenceReferenceCache.MaximumSquaredEmbeddingCost);
        }

        /// <remarks>
        /// The key is the pattern by value. Two equal patterns in different arrays are one key, and a
        /// pattern of a different length is never the same key as a shorter one padded with zeros.
        /// </remarks>
        [Fact]
        public void ThePatternKeyIsAValue()
        {
            Assert.Equal(
                DirectionSequencePatternKey.Of([true, false, true]),
                DirectionSequencePatternKey.Of([true, false, true]));

            Assert.NotEqual(
                DirectionSequencePatternKey.Of([true, false]),
                DirectionSequencePatternKey.Of([true, false, false]));

            // Sixty-six symbols is inside the two words the key uses; the boundary at sixty-four is the
            // one worth pinning.
            var wide = Alternating(66);
            var same = Alternating(66);

            Assert.Equal(
                DirectionSequencePatternKey.Of(wide), DirectionSequencePatternKey.Of(same));

            wide[65] = !wide[65];

            Assert.NotEqual(
                DirectionSequencePatternKey.Of(wide), DirectionSequencePatternKey.Of(same));
        }

        [Fact]
        public void APatternLongerThanTheKeyCanHoldIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => DirectionSequencePatternKey.Of(Alternating(129)));
        }

        // ---------------------------------------------------------------------------------------
        // Shapes.
        // ---------------------------------------------------------------------------------------

        private static bool[] Alternating(int length) =>
            [.. Enumerable.Range(0, length).Select(index => index % 2 == 0)];

        /// <summary>
        /// A deterministic population whose message lengths have a median of two and a maximum of
        /// sixty-six, against token sides drawn from predeclared length buckets.
        /// </summary>
        private static List<(bool[] Message, bool[] Tokens)> SyntheticPopulation(int count)
        {
            int[] lengths = [1, 2, 2, 2, 3, 3, 4, 5, 8, 12, 25, 66];
            int[] buckets = [10, 50, 100, 500, 1_000, 10_000];

            var shapes = new List<(bool[], bool[])>(count);

            for (var index = 0; index < count; index++)
            {
                var length = lengths[index % lengths.Length];
                var tokens = buckets[index % buckets.Length];

                // Deterministic and shape-varying without randomness: the pattern is a function of the
                // index, so the same workload is measured every time.
                var message = new bool[length];

                for (var symbol = 0; symbol < length; symbol++)
                {
                    message[symbol] = ((index + symbol) % 3) != 0;
                }

                var side = new bool[tokens];

                for (var position = 0; position < tokens; position++)
                {
                    side[position] = ((position + index) % 2) == 0;
                }

                shapes.Add((message, side));
            }

            return shapes;
        }
    }
}
