namespace Chatcheology.Archive
{
    /// <summary>What the archive holds for one attachment slot.</summary>
    public enum ArchiveAttachmentState
    {
        /// <summary>Confirmed, and its media file is in the archive's media folder.</summary>
        Restored,

        /// <summary>Unresolved, and reviewed as having insufficient evidence to restore it.</summary>
        Missing,

        /// <summary>
        /// Any other unresolved slot. Kept so the format never pretends an unlooked-at slot was
        /// reviewed.
        /// </summary>
        Unmatched,
    }

    /// <summary>One attachment slot of a message.</summary>
    /// <remarks>
    /// <para><see cref="MediaSha256"/>, <see cref="OriginalFileName"/> and <see cref="Basis"/> are
    /// set exactly when the state is <see cref="ArchiveAttachmentState.Restored"/>.</para>
    /// <para><see cref="IsForwarded"/> is <see langword="null"/> when the producer recorded no
    /// forwarded fact for this attachment: null means "not known", never "not forwarded".</para>
    /// </remarks>
    public sealed class ArchiveAttachment
    {
        /// <summary>The attachment.</summary>
        public required long AttachmentID { get; init; }

        /// <summary>The message that carries it.</summary>
        public required long MessageID { get; init; }

        /// <summary>Its one-based position within the message.</summary>
        public required int Ordinal { get; init; }

        /// <summary>Restored, Missing or Unmatched.</summary>
        public required ArchiveAttachmentState State { get; init; }

        /// <summary>The media payload's SHA-256 (lower-case hex), or <see langword="null"/>.</summary>
        public required string? MediaSha256 { get; init; }

        /// <summary>
        /// The file name of the copy the media was taken from, for "save a copy", or
        /// <see langword="null"/>.
        /// </summary>
        public required string? OriginalFileName { get; init; }

        /// <summary>Whether the message carrying it was forwarded, or <see langword="null"/>.</summary>
        public required bool? IsForwarded { get; init; }

        /// <summary>The chat app's forward score, or <see langword="null"/>.</summary>
        public required int? ForwardScore { get; init; }

        /// <summary>
        /// The full copy this attachment is the chat app's low-quality copy of, or
        /// <see langword="null"/>.
        /// </summary>
        public required long? LowQualityCopyOfAttachmentID { get; init; }

        /// <summary>How the restoration is known, or <see langword="null"/> when not restored.</summary>
        public required ArchiveAttachmentBasis? Basis { get; init; }

        /// <summary>
        /// The media file's path inside the archive folder (<c>/</c> separated), or
        /// <see langword="null"/> when not restored.
        /// </summary>
        public string? MediaRelativePath =>
            State == ArchiveAttachmentState.Restored && MediaSha256 is { } sha && OriginalFileName is { } name
                ? ArchiveFormat.MediaRelativePath(sha, name)
                : null;
    }

    /// <summary>
    /// The current confirmation's "how we know" line for one restored attachment.
    /// </summary>
    public sealed class ArchiveAttachmentBasis
    {
        /// <summary>The kind of basis, as the producer names it.</summary>
        public required string BasisType { get; init; }

        /// <summary>The basis statement, as written when the attachment was confirmed.</summary>
        public required string BasisText { get; init; }

        /// <summary>Who confirmed it, or <see langword="null"/> when not recorded.</summary>
        public required string? Actor { get; init; }

        /// <summary>When it was confirmed, in UTC, to the second.</summary>
        public required DateTime ConfirmedUtc { get; init; }
    }

    /// <summary>One distinct media payload, as the producer recorded it. Nothing is recomputed.</summary>
    public sealed class ArchiveMedia
    {
        /// <summary>The payload's SHA-256, lower-case hex.</summary>
        public required string Sha256 { get; init; }

        /// <summary>The recorded media type, such as <c>Image</c>.</summary>
        public required string MediaType { get; init; }

        /// <summary>The size in bytes.</summary>
        public required long SizeBytes { get; init; }

        /// <summary>The recorded duration in milliseconds, or <see langword="null"/>.</summary>
        public required long? DurationMS { get; init; }

        /// <summary>The recorded pixel width, or <see langword="null"/>.</summary>
        public required int? Width { get; init; }

        /// <summary>The recorded pixel height, or <see langword="null"/>.</summary>
        public required int? Height { get; init; }
    }
}
