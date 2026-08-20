namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// C5 — how often one payload occupies several sequence positions.
    /// </summary>
    /// <remarks>
    /// Stage B1 found 804 same-source, same-asset, same-date groups in which no two supported copies
    /// shared a token, so a payload occupying several positions on one date is ordinary. Keeping it
    /// visible is what stops a later stage assuming one asset equals one occurrence.
    /// </remarks>
    public sealed class AssetTokenMultiplicityCensus
    {
        /// <summary>Asset/group pairs where the asset holds exactly one token position.</summary>
        public required int AssetGroupRelationsWithOneToken { get; init; }

        /// <summary>Asset/group pairs where it holds several.</summary>
        public required int AssetGroupRelationsWithSeveralTokens { get; init; }

        /// <summary>Feasible groups containing an asset at several token positions.</summary>
        public required int GroupsWithARepeatedAsset { get; init; }

        /// <summary>The most token positions one candidate asset carries in one group.</summary>
        public required int MaximumTokenPositionsForOneAsset { get; init; }
    }
}
