namespace Chatcheology.Data.Sequence
{
    /// <summary>How far an alignment run has got, and nothing about what it found.</summary>
    /// <remarks>
    /// There is deliberately no member an outcome could be reported through: no admission count, no
    /// difference, no share total, no per-pair result, no interpretation. A long run has to be able to
    /// say it is alive without saying what it is finding, because a partial result read before the run
    /// finishes is a result chosen by when somebody looked.
    /// <para>
    /// Adding an outcome-bearing member here would defeat that, so the type is closed on purpose: stage,
    /// counts, time and memory.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceAlignmentProgress
    {
        /// <summary>Which part of the run is executing.</summary>
        public required string Stage { get; init; }

        /// <summary>Pairs processed in that part so far.</summary>
        public required int ProcessedPairCount { get; init; }

        /// <summary>Pairs that part has to process.</summary>
        public required int TotalPairCount { get; init; }

        /// <summary>How long the run has been going.</summary>
        public required TimeSpan Elapsed { get; init; }

        /// <summary>The process's working set, for a long run's memory behaviour to be visible.</summary>
        public required long WorkingSetBytes { get; init; }
    }
}
