namespace Chatcheology.Data.MsgstoreFacts
{
    /// <summary>
    /// One attachment's caption as WhatsApp's own message store records it, read back from the
    /// workspace (CH-63, CH-71).
    /// </summary>
    /// <remarks>
    /// Copied from the msgstore by an operator-run batch. Not a matching decision, not a Phase 7
    /// record, and carrying no confidence. An attachment with no row simply has no known caption.
    /// </remarks>
    public sealed class AttachmentCaptionFact
    {
        /// <summary>The attachment the caption belongs to.</summary>
        public required long AttachmentID { get; init; }

        /// <summary>The caption exactly as the msgstore holds it; never empty.</summary>
        public required string CaptionText { get; init; }

        /// <summary>
        /// True when the caption was read from the type-12 linked partner of the attachment's own
        /// msgstore message (either side of the link: the hidden low-quality copy or the shown full
        /// copy) rather than from its own message.
        /// </summary>
        public required bool FromPartner { get; init; }

        /// <summary>The batch that recorded the row.</summary>
        public required string SourceBatch { get; init; }

        /// <summary>When the batch recorded the row, in UTC.</summary>
        public required DateTime RecordedDateTimeUtc { get; init; }
    }

    /// <summary>
    /// One row a batch asks <see cref="AttachmentCaptionFactWriter"/> to record.
    /// </summary>
    /// <param name="AttachmentID">The attachment the caption belongs to.</param>
    /// <param name="CaptionText">The caption exactly as the msgstore holds it; never empty.</param>
    /// <param name="FromPartner">Whether it was read from the type-12 linked partner (either side).</param>
    public sealed record AttachmentCaptionFactRow(
        long AttachmentID,
        string CaptionText,
        bool FromPartner);
}
