namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// Whether an attachment is currently linked to a media asset.
    /// </summary>
    /// <remarks>
    /// The typed reading of the pre-existing <c>Attachment.ResolutionStatus</c> column, whose own
    /// schema comment already records that it "is deliberately not a matching or confidence state".
    /// Two values, matching the column's <c>CHECK</c> exactly.
    /// </remarks>
    public enum Phase7ResolutionStatus
    {
        /// <summary>No asset is linked. <c>ResolvedMediaAssetID</c> is null.</summary>
        Unresolved,

        /// <summary>One asset is linked. <c>ResolvedMediaAssetID</c> is that asset.</summary>
        Resolved,
    }
}
