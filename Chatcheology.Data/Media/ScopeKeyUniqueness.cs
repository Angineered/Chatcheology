namespace Chatcheology.Data.Media
{
    /// <summary>
    /// What one scope level's keys look like: how many there are, how many collide, and how much
    /// ambiguity is left.
    /// </summary>
    /// <remarks>
    /// Physical-file multiplicity is reported but is not the informative figure. One physical file is
    /// one path under one source, and the same recovered name legitimately appears in many folders, so
    /// a key holding several files says nothing about the numbering. The distinct-name and
    /// distinct-asset counts are what carry information.
    /// <para>
    /// The share of <em>files</em> on a colliding key is reported alongside the count of colliding
    /// keys, because keys are very unevenly sized: a headline given only as a percentage of keys can
    /// look excellent while a large share of files sits inside the collisions.
    /// </para>
    /// </remarks>
    public sealed class ScopeKeyUniqueness
    {
        /// <summary>The key level described.</summary>
        public required ScopeLevel Level { get; init; }

        /// <summary>Distinct keys at this level.</summary>
        public required int KeyCount { get; init; }

        /// <summary>Keys represented by exactly one physical file.</summary>
        public required int KeysWithOnePhysicalFile { get; init; }

        /// <summary>Keys represented by more than one.</summary>
        public required int KeysWithSeveralPhysicalFiles { get; init; }

        /// <summary>Keys carrying exactly one distinct recovered file name, compared ordinally.</summary>
        public required int KeysWithOneDistinctFileName { get; init; }

        /// <summary>Keys carrying more than one.</summary>
        public required int KeysWithSeveralDistinctFileNames { get; init; }

        /// <summary>Keys mapping to exactly one <c>MediaAsset</c>.</summary>
        public required int KeysWithOneMediaAsset { get; init; }

        /// <summary>Keys mapping to more than one.</summary>
        public required int KeysWithSeveralMediaAssets { get; init; }

        /// <summary>The most physical files any one key holds.</summary>
        public required int MaximumPhysicalFilesOnOneKey { get; init; }

        /// <summary>The most distinct recovered names any one key holds.</summary>
        public required int MaximumDistinctFileNamesOnOneKey { get; init; }

        /// <summary>The most distinct assets any one key holds.</summary>
        public required int MaximumDistinctMediaAssetsOnOneKey { get; init; }

        /// <summary>Distinct assets per key, as bounds and bands.</summary>
        public required CountSummary DistinctMediaAssetsPerKey { get; init; }

        /// <summary>Supported files sitting on a key that holds exactly one asset.</summary>
        public required int FilesOnSingleAssetKeys { get; init; }

        /// <summary>Supported files sitting on a key that holds more than one.</summary>
        public required int FilesOnMultiAssetKeys { get; init; }

        /// <summary>
        /// Keys whose distinct-name count would differ if names were compared ignoring case.
        /// </summary>
        /// <remarks>
        /// Descriptive, never gated. Stage A established that no single asset carries names differing
        /// only by case; it established nothing about names compared across different assets, which is
        /// what a scoped key does.
        /// </remarks>
        public required int KeysWhereIgnoringCaseChangesTheNameCount { get; init; }

        /// <summary>Ambiguity metrics with every asset counted.</summary>
        public required ScopeAmbiguityMetrics Ambiguity { get; init; }

        /// <summary>Ambiguity metrics with zero-byte assets excluded.</summary>
        public required ScopeAmbiguityMetrics AmbiguityExcludingZeroByte { get; init; }

        /// <summary>Multi-asset keys one of whose assets holds no payload.</summary>
        public required int MultiAssetKeysInvolvingZeroByteAsset { get; init; }
    }
}
