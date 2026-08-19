namespace Chatcheology.Data.Media
{
    /// <summary>
    /// How much ambiguity one scope level leaves, in the three gated quantities and the three
    /// descriptive ones.
    /// </summary>
    /// <remarks>
    /// The distinction between the two families is the whole point of this type, so it is stated here
    /// rather than left to a report.
    /// <para>
    /// <b>G1, G2 and G4 are monotone</b> under refinement: a child key's asset set is always a subset
    /// of its parent's, so files sitting in a collision, the worst single collision, and assets caught
    /// in any collision can only fall as scope is added. Each is a hard gate.
    /// </para>
    /// <para>
    /// <b>R1, R2 and R3 are not monotone</b>, and their rising across the ladder is not a finding. One
    /// colliding parent key can split into several colliding child keys, and the same asset pair is
    /// then counted once per child. They are the useful ambiguity magnitudes; they are never gates.
    /// </para>
    /// <para>
    /// There is no G3. A gate over distinct co-occurring asset pairs would be monotone, but it would
    /// require materialising pair sets — the one genuinely expensive structure in this census — and
    /// <see cref="AssetPairMagnitude"/> already carries the magnitude arithmetically. The identifier is
    /// retired rather than renumbered so G1, G2 and G4 keep their meanings.
    /// </para>
    /// </remarks>
    public sealed class ScopeAmbiguityMetrics
    {
        /// <summary>G1 — physical files sitting in a key that holds more than one asset.</summary>
        public required int FilesInMultiAssetKeys { get; init; }

        /// <summary>G2 — the most distinct assets any one key holds.</summary>
        public required int MaximumDistinctAssetsOnOneKey { get; init; }

        /// <summary>G4 — distinct assets appearing in at least one multi-asset key.</summary>
        public required int AssetsInMultiAssetKeys { get; init; }

        /// <summary>R1 — keys holding more than one asset. Descriptive only.</summary>
        public required int MultiAssetKeyCount { get; init; }

        /// <summary>
        /// R2 — the sum over keys of <c>n * (n - 1) / 2</c>. Descriptive only.
        /// </summary>
        /// <remarks>
        /// Computed arithmetically from each key's asset count; no asset pairs are materialised. The
        /// accumulator is 64-bit defensively.
        /// </remarks>
        public required long AssetPairMagnitude { get; init; }

        /// <summary>R3 — the sum over keys of <c>n - 1</c>. Descriptive only.</summary>
        public required long ExcessAmbiguity { get; init; }
    }
}
