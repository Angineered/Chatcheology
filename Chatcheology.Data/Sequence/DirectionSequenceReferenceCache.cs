namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// Remembers the exact reference counts for one <c>(pattern, outgoing, incoming, runs)</c> class for
    /// the duration of one run, and refuses a class too large to compute exactly.
    /// </summary>
    /// <remarks>
    /// The adjacent-date controls ask for a reference class per pair per neighbour, and the reference
    /// dynamic programmes are the expensive part of this whole stage. The archive's median message
    /// pattern is two symbols long, and there are only four distinct patterns of that length, so the same
    /// class is asked for repeatedly.
    /// <para>
    /// <b>What a hit cannot do.</b> It returns the identical values the dynamic programme would have
    /// produced, because the key is the entire input: the pattern by value, the composition and the run
    /// count are all the reference depends on. So the cache cannot change a population, a classification
    /// or an arithmetic result — only how long the run takes.
    /// </para>
    /// <para>
    /// Two stores, because the two rules need different things. A binary comparison needs the class size
    /// and the admitting count; a graded one also needs the embedding-pair and squared-embedding counts,
    /// and the squared count is by far the most expensive quantity in the stage.
    /// </para>
    /// <para>
    /// In memory, for one run, and discarded with it. Never persisted: a cache surviving a run would be a
    /// computational resume, and a failed run must restart rather than continue from a state nothing
    /// verified. No eviction policy, because the number of distinct keys is bounded by the pairs a run
    /// examines times the comparisons each pair takes.
    /// </para>
    /// <para>
    /// <see cref="Disabled"/> exists so a test can prove the run produces identical figures with and
    /// without it.
    /// </para>
    /// </remarks>
    internal sealed class DirectionSequenceReferenceCache
    {
        /// <summary>
        /// The largest <c>m⁴ · r</c> a squared-embedding count may be asked for.
        /// </summary>
        /// <remarks>
        /// Measured, not chosen. The squared-embedding dynamic programme tracks a pair of embeddings, so
        /// its state space grows with the square of the pattern length and linearly with the run count,
        /// and the width of the integers it accumulates grows with the pattern length again. On the
        /// development machine the observed cost fits <c>m⁴ · r</c> closely:
        /// <list type="bullet">
        /// <item><description><c>m = 2, r = 1000</c> — 9 ms, 7 MB</description></item>
        /// <item><description><c>m = 6, r = 1000</c> — 0.58 s, 162 MB</description></item>
        /// <item><description><c>m = 8, r = 500</c> — 0.72 s, 239 MB</description></item>
        /// <item><description><c>m = 12, r = 250</c> — 2.5 s, 734 MB</description></item>
        /// <item><description><c>m = 12, r = 1000</c> — 11.4 s, 3.6 GB</description></item>
        /// </list>
        /// The bound is the largest of those measurements that stayed inside a second and a quarter of a
        /// gigabyte — <c>m = 8, r = 500</c>, whose proxy is exactly <c>8⁴ · 500</c> — rather than a round
        /// number chosen for its shape. The next measurement up cost three and a half times as long and
        /// three times the memory; two steps up it exhausts memory outright.
        /// <para>
        /// This is <b>not</b> a cap on the population. Nothing is dropped, sampled or approximated: a
        /// class beyond the bound stops the whole run with an explanation, which is what the stage's own
        /// rules require of an impractical exact computation. The own-date references are never affected —
        /// they were computed by the accepted gate and are read from its pair rows.
        /// </para>
        /// </remarks>
        internal const long MaximumSquaredEmbeddingCost = 2_048_000L;

        private readonly Dictionary<Key, DirectionSequenceAdmissionValues>? _admission;
        private readonly Dictionary<Key, DirectionSequenceReferenceValues>? _full;

        private int _hits;
        private int _misses;

        internal DirectionSequenceReferenceCache(bool enabled = true)
        {
            _admission = enabled ? [] : null;
            _full = enabled ? [] : null;
        }

        /// <summary>A cache that remembers nothing, for proving the run does not depend on it.</summary>
        internal static DirectionSequenceReferenceCache Disabled => new(enabled: false);

        /// <summary>Distinct classes remembered, across both stores.</summary>
        internal int Count => (_admission?.Count ?? 0) + (_full?.Count ?? 0);

        /// <summary>Requests answered from memory.</summary>
        internal int Hits => _hits;

        /// <summary>Requests that had to be computed.</summary>
        internal int Misses => _misses;

        /// <summary>
        /// The class size and admitting count for one class, computed once.
        /// </summary>
        internal DirectionSequenceAdmissionValues ForAdmission(
            bool[] pattern, int outgoing, int incoming, int runCount)
        {
            var key = new Key(
                DirectionSequencePatternKey.Of(pattern), outgoing, incoming, runCount);

            if (_admission is not null && _admission.TryGetValue(key, out var existing))
            {
                _hits++;

                return existing;
            }

            _misses++;

            var computed = new DirectionSequenceAdmissionValues(
                DirectionSequenceReference.ArrangementCount(outgoing, incoming, runCount),
                DirectionSequenceReference.AdmittingArrangementCount(
                    pattern, outgoing, incoming, runCount));

            if (_admission is not null)
            {
                _admission[key] = computed;
            }

            return computed;
        }

        /// <summary>
        /// All four exact counts for one class, computed once.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The class is too large for the squared-embedding count to be computed exactly. The run stops;
        /// nothing is approximated and no pair is dropped.
        /// </exception>
        internal DirectionSequenceReferenceValues ForShare(
            bool[] pattern, int outgoing, int incoming, int runCount)
        {
            var key = new Key(
                DirectionSequencePatternKey.Of(pattern), outgoing, incoming, runCount);

            if (_full is not null && _full.TryGetValue(key, out var existing))
            {
                _hits++;

                return existing;
            }

            _misses++;

            RequirePracticalSquaredEmbeddingCost(pattern.Length, runCount);

            var admission = new DirectionSequenceAdmissionValues(
                DirectionSequenceReference.ArrangementCount(outgoing, incoming, runCount),
                DirectionSequenceReference.AdmittingArrangementCount(
                    pattern, outgoing, incoming, runCount));

            var computed = new DirectionSequenceReferenceValues(
                admission.ArrangementCount,
                admission.AdmittingArrangementCount,
                DirectionSequenceReference.EmbeddingPairCount(
                    pattern, outgoing, incoming, runCount),
                DirectionSequenceReference.SquaredEmbeddingCount(
                    pattern, outgoing, incoming, runCount));

            if (_full is not null)
            {
                _full[key] = computed;
            }

            return computed;
        }

        /// <summary>
        /// The cost proxy for one squared-embedding count: <c>m⁴ · r</c>.
        /// </summary>
        internal static long SquaredEmbeddingCostOf(int patternLength, int runCount)
        {
            var length = (long)patternLength;

            return length * length * length * length * runCount;
        }

        private static void RequirePracticalSquaredEmbeddingCost(int patternLength, int runCount)
        {
            var cost = SquaredEmbeddingCostOf(patternLength, runCount);

            if (cost <= MaximumSquaredEmbeddingCost)
            {
                return;
            }

            throw new InvalidOperationException(
                $"The Stage B2C-1 alignment run stopped: a graded adjacent-date comparison needs the "
                + $"exact squared-embedding count for a message pattern of {patternLength} symbol(s) "
                + $"against a token side of {runCount} direction run(s), whose cost proxy of {cost} "
                + $"exceeds the practical bound of {MaximumSquaredEmbeddingCost}. That quantity decides "
                + $"graded determinacy, so it cannot be approximated, sampled or skipped, and no pair "
                + $"may be dropped to avoid it. This is a design-review condition: the shifted "
                + $"comparison pairs an own-date pattern with a neighbouring date's run structure, which "
                + $"is a combination the accepted gate never had to compute. No result is returned, "
                + $"nothing partial is retained, and the workspace is unchanged.");
        }

        /// <summary>The whole input the reference depends on, and nothing else.</summary>
        private readonly record struct Key(
            DirectionSequencePatternKey Pattern, int Outgoing, int Incoming, int RunCount);
    }
}
