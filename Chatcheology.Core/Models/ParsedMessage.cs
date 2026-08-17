namespace Chatcheology.Core.Models
{
    /// <summary>
    /// A single logical message read from a chat export.
    /// </summary>
    /// <remarks>
    /// This is an in-memory parse result only. It is not a database entity and carries no
    /// persistence, media or UI concerns.
    /// <para>
    /// Multiline content is represented with the line feed character <c>"\n"</c> rather than
    /// <see cref="Environment.NewLine"/>, so that parsing the same export produces identical
    /// content on every platform.
    /// </para>
    /// </remarks>
    public sealed class ParsedMessage
    {
        /// <summary>
        /// The exact content that marks a message as a media placeholder in the supported
        /// export format.
        /// </summary>
        public const string MediaPlaceholderContent = "<Media omitted>";

        /// <summary>
        /// The line separator used inside <see cref="MessageContent"/> and
        /// <see cref="RawContent"/>. Deliberately not <see cref="Environment.NewLine"/>.
        /// </summary>
        public const string LineSeparator = "\n";

        /// <summary>
        /// The authoritative conversation order, 1-based, in the order the logical messages
        /// occur in the source file.
        /// </summary>
        /// <remarks>
        /// The supported export format has minute-level timestamps only, so several messages
        /// can share a <see cref="MessageDateTime"/>. Source order is authoritative;
        /// <see cref="MessageDateTime"/> is not.
        /// </remarks>
        public required int SequenceNumber { get; init; }

        /// <summary>
        /// The timestamp as written in the export, with no timezone or UTC conversion applied.
        /// <see cref="DateTimeKind"/> is always <see cref="DateTimeKind.Unspecified"/>.
        /// </summary>
        public required DateTime MessageDateTime { get; init; }

        /// <summary>
        /// The sender text exactly as written in the export header, untrimmed.
        /// </summary>
        public required string Sender { get; init; }

        /// <summary>
        /// The logical message content: the text after the <c>": "</c> delimiter on the header
        /// line, plus every continuation line, joined with <see cref="LineSeparator"/>.
        /// </summary>
        public required string MessageContent { get; init; }

        /// <summary>
        /// The complete logical source block, including the original timestamp and sender header
        /// line and all continuation lines, joined with <see cref="LineSeparator"/>.
        /// </summary>
        /// <remarks>
        /// Source-preserving at the logical-text level, retained for later troubleshooting. It is
        /// not a byte-for-byte copy: a <see cref="System.IO.TextReader"/> does not report whether
        /// the source used CRLF or LF, so the original line-ending style is not recoverable from
        /// this value. No other normalisation is applied — Unicode content is preserved as read.
        /// </remarks>
        public required string RawContent { get; init; }

        /// <summary>
        /// The 1-based first physical source line this logical message came from.
        /// </summary>
        public required int SourceLineStart { get; init; }

        /// <summary>
        /// The 1-based final physical source line this logical message came from. Equal to
        /// <see cref="SourceLineStart"/> for a single-line message.
        /// </summary>
        public required int SourceLineEnd { get; init; }

        /// <summary>
        /// True only when the complete <see cref="MessageContent"/> is exactly
        /// <see cref="MediaPlaceholderContent"/>. Computed, so it cannot drift from the content.
        /// </summary>
        /// <remarks>
        /// The comparison is ordinal. No media type is inferred and no attachment is resolved in
        /// this phase.
        /// </remarks>
        public bool IsMediaPlaceholder =>
            string.Equals(MessageContent, MediaPlaceholderContent, StringComparison.Ordinal);
    }
}
