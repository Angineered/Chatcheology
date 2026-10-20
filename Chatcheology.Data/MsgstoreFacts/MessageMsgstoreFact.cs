namespace Chatcheology.Data.MsgstoreFacts
{
    /// <summary>
    /// One text message's facts as WhatsApp's own message store records them, read back from the
    /// workspace (CH-22).
    /// </summary>
    /// <remarks>
    /// Facts copied from the msgstore by an operator-run batch. Not a matching decision, not a
    /// Phase 7 record, and carrying no confidence. A row with <see cref="IsForwarded"/> false is a
    /// known "not forwarded"; a message with no row simply has no recorded msgstore fact.
    /// </remarks>
    public sealed class MessageMsgstoreFact
    {
        /// <summary>The message the facts belong to.</summary>
        public required long MessageID { get; init; }

        /// <summary>Whether WhatsApp marks the message as forwarded.</summary>
        public required bool IsForwarded { get; init; }

        /// <summary>WhatsApp's forward score, as recorded, or <see langword="null"/>.</summary>
        public required int? ForwardScore { get; init; }

        /// <summary>The batch that recorded the row.</summary>
        public required string SourceBatch { get; init; }

        /// <summary>When the batch recorded the row, in UTC.</summary>
        public required DateTime RecordedDateTimeUtc { get; init; }
    }

    /// <summary>
    /// One row a batch asks <see cref="MessageMsgstoreFactWriter"/> to record.
    /// </summary>
    /// <param name="MessageID">The text message the facts belong to.</param>
    /// <param name="IsForwarded">Whether WhatsApp marks the message as forwarded.</param>
    /// <param name="ForwardScore">WhatsApp's forward score, or <see langword="null"/>.</param>
    public sealed record MessageMsgstoreFactRow(
        long MessageID,
        bool IsForwarded,
        int? ForwardScore);
}
