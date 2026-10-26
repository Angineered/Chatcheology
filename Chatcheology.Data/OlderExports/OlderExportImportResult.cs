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
