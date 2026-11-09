namespace Chatcheology.Data.NewerMessages
{
    /// <summary>The marker row: which conversation and msgstore chat the file continues.</summary>
    /// <param name="ConversationID">The main workspace conversation (pinned: data/phase6_request_scope).</param>
    /// <param name="LocalParticipantID">The workspace participant <c>from_me = 1</c> maps to.</param>
    /// <param name="OtherParticipantID">The conversation's other participant (<c>from_me = 0</c>).</param>
    /// <param name="ChatRowID">The msgstore <c>chat._id</c> the messages are read from.</param>
    /// <param name="WorkspaceLastDay">
    /// The main workspace's last local day at the first run. Every later run refuses a message after its hand-off
    /// on or before this day, and a message before its hand-off after this day that was never imported.
    /// </param>
    /// <param name="CreatedDateTimeUtc">When the file was created (first run).</param>
    public sealed record NewerMessagesInfo(
        long ConversationID, long LocalParticipantID, long OtherParticipantID, long ChatRowID, DateOnly WorkspaceLastDay,
        DateTime CreatedDateTimeUtc);

    /// <summary>One refresh run, as it is written and as it is read back.</summary>
    public sealed class NewerRefreshRun
    {
        /// <summary>Assigned by the file; 0 before writing.</summary>
        public long RunID { get; init; }

        public required DateTime RunDateTimeUtc { get; init; }

        /// <summary>The approved plan's manifest SHA-256 (lower-case hex). A second apply of it writes nothing.</summary>
        public required string ManifestSha256 { get; init; }

        /// <summary>The msgstore fingerprint: its size, its <c>user_version</c> and a SHA-256 of its table and column names.</summary>
        public required long MsgstoreSizeBytes { get; init; }

        public required long MsgstoreUserVersion { get; init; }

        public required string MsgstoreSchemaSha256 { get; init; }

        /// <summary><see cref="NewerMessagesDatabase.HandOffWorkspace"/> or <see cref="NewerMessagesDatabase.HandOffPreviousRun"/>.</summary>
        public required string HandOff { get; init; }

        /// <summary>The msgstore key_id right before the first message of this run.</summary>
        public required string HandOffKeyID { get; init; }

        /// <summary>The hand-off message's direction; with <see cref="HandOffKeyID"/> its identity.</summary>
        public required bool HandOffFromMe { get; init; }

        /// <summary>The media folder files were checked under; null when none was given (every attachment Unmatched).</summary>
        public string? MediaRootPath { get; init; }

        // Read back only (the writer computes them from the rows).
        public int Messages { get; init; }
        public int UserMessages { get; init; }
        public int SystemMessages { get; init; }
        public int Placeholders { get; init; }
        public int Attachments { get; init; }
        public int Restored { get; init; }
        public int Unmatched { get; init; }
        public string? LastKeyID { get; init; }
        public bool? LastFromMe { get; init; }
        public long? LastSortID { get; init; }
    }

    /// <summary>One newer message.</summary>
    public sealed class NewerMessageRow
    {
        /// <summary>Assigned by the file in timeline order; 0 before writing.</summary>
        public long NewerMessageID { get; init; }

        /// <summary>Assigned by the file; 0 before writing.</summary>
        public long RunID { get; init; }

        /// <summary>msgstore <c>message.key_id</c>; unique, so a message is never imported twice.</summary>
        public required string KeyID { get; init; }

        public required long MsgstoreRowID { get; init; }

        /// <summary>msgstore <c>sort_id</c> (its <c>_id</c> when the column is absent or null).</summary>
        public required long SortID { get; init; }

        public required bool FromMe { get; init; }

        /// <summary>The workspace participant; null for a <see cref="NewerMessagesDatabase.KindSystem"/> row.</summary>
        public long? SenderParticipantID { get; init; }

        public required long TimestampUtcMs { get; init; }

        /// <summary><c>yyyy-MM-ddTHH:mm:ss</c> in <see cref="NewerMessagesTimeZone"/>.</summary>
        public required string DateTimeLocal { get; init; }

        public long? MessageType { get; init; }

        /// <summary><see cref="NewerMessagesDatabase.KindUser"/>, <see cref="NewerMessagesDatabase.KindSystem"/> or <see cref="NewerMessagesDatabase.KindPlaceholder"/>.</summary>
        public required string Kind { get; init; }

        /// <summary>The placeholder's kind (Poll, Location...); null unless <see cref="Kind"/> is Placeholder.</summary>
        public string? PlaceholderKind { get; init; }

        /// <summary>
        /// A user text message: the text exactly as the msgstore holds it. A media message: null (its caption is
        /// <see cref="Caption"/>). System and Placeholder: the fixed wording only, never the item's content.
        /// </summary>
        public string? Text { get; init; }

        /// <summary>A media message's caption exactly as the msgstore holds it; null when none.</summary>
        public string? Caption { get; init; }

        /// <summary>1/0 when the msgstore records forwarding; null when it cannot tell (not known).</summary>
        public bool? IsForwarded { get; init; }

        public int? ForwardScore { get; init; }

        public NewerAttachmentRow? Attachment { get; init; }
    }

    /// <summary>The one attachment of a media message.</summary>
    public sealed class NewerAttachmentRow
    {
        /// <summary>Assigned by the file in timeline order; 0 before writing.</summary>
        public long NewerAttachmentID { get; init; }

        public string? FileName { get; init; }

        public long? FileSizeBytes { get; init; }

        public string? MimeType { get; init; }

        /// <summary>msgstore <c>file_hash</c> as stored (normally base64 of the SHA-256).</summary>
        public string? FileHash { get; init; }

        /// <summary>msgstore <c>file_path</c> with forward slashes, relative to the media root; null when unusable.</summary>
        public string? RelativePath { get; init; }

        /// <summary><see cref="NewerMessagesDatabase.StateRestored"/> or <see cref="NewerMessagesDatabase.StateUnmatched"/>.</summary>
        public required string State { get; init; }

        /// <summary>SHA-256 (lower-case hex) of the file under the media root; only when Restored.</summary>
        public string? Sha256 { get; init; }

        /// <summary>
        /// Written: the key_id of the message whose attachment is the full copy this one is the low-quality copy of.
        /// That message is in the same direction (the link rule requires it), so (this message's from_me, this key) names it.
        /// </summary>
        public string? LowQualityCopyOfKeyID { get; init; }

        /// <summary>Read back: the full copy's <see cref="NewerAttachmentID"/>.</summary>
        public long? LowQualityCopyOfAttachmentID { get; init; }

        /// <summary>Read back: the media root's full path; only when Restored.</summary>
        public string? MediaRootPath { get; init; }
    }

    /// <summary>What <see cref="NewerMessagesWriter.Append"/> did.</summary>
    public sealed record NewerMessagesWriteResult(bool AlreadyApplied, bool Created, long RunID, int Messages, int Attachments, int Links);

    /// <summary>Everything a newer-messages file holds, in timeline order.</summary>
    public sealed record NewerMessagesContents(
        NewerMessagesInfo Info, IReadOnlyList<NewerRefreshRun> Runs, IReadOnlyList<NewerMessageRow> Messages)
    {
        /// <summary>The key_id of the last imported message, or null when nothing has been imported.</summary>
        public string? LastKeyID => Runs.LastOrDefault(r => r.LastKeyID is not null)?.LastKeyID;

        /// <summary>The identity (from_me, key_id) of the last imported message, or null when nothing has been imported.</summary>
        public (bool FromMe, string KeyID)? LastMessage =>
            Runs.LastOrDefault(r => r.LastKeyID is not null) is { LastKeyID: { } key, LastFromMe: { } fromMe } ? (fromMe, key) : null;
    }
}
