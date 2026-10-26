namespace Chatcheology.Data.OlderExports
{
    /// <summary>
    /// Which side of the conversation a sender name in one older export stands for.
    /// </summary>
    /// <remarks>
    /// Given by the person importing, per export, and never inferred from names, counts or anything
    /// else: the same person appears under different display names across the older exports.
    /// Stored as the text of the member name in <c>ExportRole.Role</c>.
    /// </remarks>
    public enum OlderExportRole
    {
        /// <summary>The phone's owner, the local participant.</summary>
        Local,

        /// <summary>The other participant.</summary>
        Other,
    }
}
