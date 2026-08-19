namespace Chatcheology.Data.Media
{
    /// <summary>How large one group scope's groups are, in four ways.</summary>
    /// <remarks>
    /// Reported pooled and per partition, never only pooled. Whether a scope component adds anything is
    /// exactly the question, and a pooled figure cannot answer it.
    /// <para>
    /// <see cref="PartitionID"/> is null for the pooled row and otherwise carries the source or device
    /// group identifier the row describes.
    /// </para>
    /// </remarks>
    public sealed class ScopeGroupPopulation
    {
        /// <summary>The group scope described.</summary>
        public required ScopeLevel Scope { get; init; }

        /// <summary>The source or device group, or null for the pooled row.</summary>
        public required long? PartitionID { get; init; }

        /// <summary>Groups holding supported evidence.</summary>
        public required int GroupCount { get; init; }

        /// <summary>Supported physical files per group.</summary>
        public required CountSummary FilesPerGroup { get; init; }

        /// <summary>Distinct assets per group.</summary>
        public required CountSummary DistinctMediaAssetsPerGroup { get; init; }

        /// <summary>Distinct sequence tokens per group.</summary>
        public required CountSummary DistinctTokensPerGroup { get; init; }

        /// <summary>Distinct recovered file names per group, compared ordinally.</summary>
        public required CountSummary DistinctFileNamesPerGroup { get; init; }
    }
}
