namespace Chatcheology.Core.Models
{
    /// <summary>
    /// The result of parsing one whole export: its logical messages and the header layout they
    /// were read in.
    /// </summary>
    /// <remarks>
    /// An in-memory parse result only, like <see cref="ParsedMessage"/>.
    /// </remarks>
    public sealed class ParsedChatExport
    {
        /// <summary>
        /// The layout detected from the first message header, or null when the export holds no
        /// message at all (empty or whitespace-only input).
        /// </summary>
        public required WhatsAppHeaderShape? Shape { get; init; }

        /// <summary>The logical messages in source order, numbered from 1.</summary>
        public required IReadOnlyList<ParsedMessage> Messages { get; init; }
    }
}
