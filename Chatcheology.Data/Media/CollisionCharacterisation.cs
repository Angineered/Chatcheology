namespace Chatcheology.Data.Media
{
    /// <summary>
    /// What the multi-asset keys at one scope level look like.
    /// </summary>
    /// <remarks>
    /// A key holding several payloads arrives in two shapes, and the difference matters enough to
    /// measure rather than assume.
    /// <para>
    /// <b>One recovered name, several payloads.</b> Consistent with a recovery-integrity artefact or
    /// with some other provenance artefact; cause is not observable. Read as a statement about the
    /// numbering it says nothing — it is not evidence that a token was reused across different
    /// recovered names. It is still a genuine multi-asset ambiguity for any later matching use of the
    /// key, so it is never subtracted from any ambiguity total.
    /// </para>
    /// <para>
    /// <b>Several recovered names, several payloads.</b> Differently-named files sharing the scoped
    /// token.
    /// </para>
    /// <para>
    /// Extension homogeneity is recorded because a systematically extension-mixed collision would
    /// suggest a type-partitioned namespace rather than a numbering failure. Recording the shape is not
    /// interpreting it, and no cause is attributed to either shape.
    /// </para>
    /// </remarks>
    public sealed class CollisionCharacterisation
    {
        /// <summary>The key level described.</summary>
        public required ScopeLevel Level { get; init; }

        /// <summary>Keys holding more than one asset.</summary>
        public required int MultiAssetKeyCount { get; init; }

        /// <summary>Of those, keys carrying exactly one distinct recovered name.</summary>
        public required int KeysWithOneDistinctFileName { get; init; }

        /// <summary>Of those, keys carrying several.</summary>
        public required int KeysWithSeveralDistinctFileNames { get; init; }

        /// <summary>Keys whose files all record the same extension.</summary>
        public required int KeysWhereExtensionsAreAllEqual { get; init; }

        /// <summary>Keys whose files record more than one extension.</summary>
        public required int KeysWhereExtensionsDiffer { get; init; }

        /// <summary>Keys one of whose assets holds no payload.</summary>
        public required int KeysInvolvingZeroByteAsset { get; init; }

        /// <summary>Keys none of whose assets is empty.</summary>
        public required int KeysNotInvolvingZeroByteAsset { get; init; }

        /// <summary>Physical files per colliding key.</summary>
        public required CountSummary PhysicalFilesPerKey { get; init; }

        /// <summary>Distinct recovered names per colliding key.</summary>
        public required CountSummary DistinctFileNamesPerKey { get; init; }

        /// <summary>Distinct assets per colliding key.</summary>
        public required CountSummary DistinctMediaAssetsPerKey { get; init; }

        /// <summary>Name shape against extension homogeneity.</summary>
        public required JointDistribution NameShapeByExtensionHomogeneity { get; init; }

        /// <summary>Name shape against whether an empty payload is involved.</summary>
        public required JointDistribution NameShapeByZeroByteInvolvement { get; init; }
    }
}
