namespace Chatcheology.Data.OlderExports
{
    /// <summary>
    /// What one successful older-export import wrote. Counts and generated identifiers only.
    /// </summary>
    public sealed class OlderExportImportResult
    {
        /// <summary>The generated <c>ImportSource.ImportSourceID</c>.</summary>
        public required long ImportSourceID { get; init; }

        /// <summary>The generated <c>Conversation.ConversationID</c>.</summary>
        public required long ConversationID { get; init; }

        /// <summary>How many messages were stored.</summary>
        public required int MessageCount { get; init; }

        /// <summary>
        /// How many of them are shown: all for a shown export, those after the cut for one shown
        /// after another export, none for an export kept as evidence.
        /// </summary>
        public required int ShownMessageCount { get; init; }

        /// <summary>
        /// The first shown sequence number when the export is shown after another (the cut), or
        /// null when there is no cut.
        /// </summary>
        public required int? ShownFromSequenceNumber { get; init; }

        /// <summary>
        /// Attachments created for <c>&lt;Media omitted&gt;</c> messages, all unresolved.
        /// </summary>
        public required int MediaOmittedAttachmentCount { get; init; }

        /// <summary>Attachments created for <c>(file attached)</c> messages.</summary>
        public required int FileAttachedAttachmentCount { get; init; }

        /// <summary>
        /// Of those, resolved to a file of exactly that name in the export's own folder.
        /// </summary>
        public required int ResolvedInOwnFolderCount { get; init; }

        /// <summary>Of those, resolved to a file of exactly that name in the fallback folder.</summary>
        public required int ResolvedInFallbackFolderCount { get; init; }

        /// <summary>Of those, left unresolved: no file of exactly that name was found.</summary>
        public required int UnresolvedFileAttachedCount { get; init; }
    }
}
