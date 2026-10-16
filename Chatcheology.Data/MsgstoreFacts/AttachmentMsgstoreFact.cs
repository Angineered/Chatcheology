namespace Chatcheology.Data.MsgstoreFacts
{
    /// <summary>
    /// One attachment's facts as WhatsApp's own message store records them, read back from the
    /// workspace.
    /// </summary>
    /// <remarks>
    /// Facts copied from the msgstore by an operator-run batch. Not a matching decision, not a
    /// Phase 7 record, and carrying no confidence: an attachment with no row simply has no recorded
    /// msgstore fact.
    /// </remarks>
    public sealed class AttachmentMsgstoreFact
    {
        /// <summary>The attachment the facts belong to.</summary>
        public required long AttachmentID { get; init; }

        /// <summary>Whether WhatsApp marks the message carrying this attachment as forwarded.</summary>
        public required bool IsForwarded { get; init; }

        /// <summary>WhatsApp's forward score, as recorded, or <see langword="null"/>.</summary>
        public required int? ForwardScore { get; init; }

        /// <summary>
        /// The full copy this attachment is WhatsApp's low-quality copy of, or
        /// <see langword="null"/> when no such link is recorded.
        /// </summary>
        public required long? LowQualityCopyOfAttachmentID { get; init; }

        /// <summary>The batch that recorded the row.</summary>
        public required string SourceBatch { get; init; }

        /// <summary>When the batch recorded the row, in UTC.</summary>
        public required DateTime RecordedDateTimeUtc { get; init; }
    }

    /// <summary>
    /// One row a batch asks <see cref="MsgstoreFactWriter"/> to record.
    /// </summary>
    /// <param name="AttachmentID">The attachment the facts belong to.</param>
    /// <param name="IsForwarded">Whether WhatsApp marks the message as forwarded.</param>
    /// <param name="ForwardScore">WhatsApp's forward score, or <see langword="null"/>.</param>
    /// <param name="LowQualityCopyOfAttachmentID">
    /// The full copy this attachment is WhatsApp's low-quality copy of, or <see langword="null"/>.
    /// </param>
    public sealed record MsgstoreFactRow(
        long AttachmentID,
        bool IsForwarded,
        int? ForwardScore,
        long? LowQualityCopyOfAttachmentID);

    /// <summary>
    /// The msgstore facts that bear on a set of attachments.
    /// </summary>
    public sealed class MsgstoreFactSet
    {
        /// <summary>An empty set, for a workspace or page with no facts.</summary>
        public static MsgstoreFactSet Empty { get; } = new()
        {
            Facts = new Dictionary<long, AttachmentMsgstoreFact>(),
            LowQualityCopiesByFullCopy = new Dictionary<long, IReadOnlyList<long>>(),
        };

        /// <summary>The fact row of each requested attachment that has one.</summary>
        public required IReadOnlyDictionary<long, AttachmentMsgstoreFact> Facts { get; init; }

        /// <summary>
        /// For each requested attachment that some attachment is recorded as the low-quality copy
        /// of, those copies' identifiers in ascending order. The copies need not be among the
        /// requested attachments.
        /// </summary>
        public required IReadOnlyDictionary<long, IReadOnlyList<long>> LowQualityCopiesByFullCopy
        {
            get; init;
        }
    }
}
