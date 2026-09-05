namespace Chatcheology.Data.Viewer
{
    /// <summary>
    /// Where an attachment sits in its message.
    /// </summary>
    /// <remarks>
    /// <strong>Position only.</strong> Whether this attachment is resolved, to what, and why it is
    /// not, is <c>Phase7ReconstructionReader</c>'s to answer; carrying any of it here would be a
    /// second reading of the same question, and two readings of one question are how they come to
    /// disagree.
    /// <para>
    /// <see cref="Ordinal"/> is carried rather than assumed. The current importer creates one
    /// attachment per media placeholder, but the schema's <c>UNIQUE (MessageID, Ordinal)</c>
    /// deliberately permits more, and a viewer that assumed one would silently drop the rest.
    /// </para>
    /// </remarks>
    public sealed class ConversationAttachment
    {
        /// <summary>The attachment, as <c>Phase7ReconstructionReader</c> identifies it.</summary>
        public required long AttachmentID { get; init; }

        /// <summary>Its one-based position within its message.</summary>
        public required int Ordinal { get; init; }
    }
}
