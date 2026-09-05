namespace Chatcheology.Core.Text
{
    /// <summary>
    /// One run of a message: either plain text, or something the reader may follow.
    /// </summary>
    /// <remarks>
    /// <see cref="Text"/> is the message's own characters, unchanged — trailing punctuation a
    /// sentence put after a link belongs to the sentence and is returned as text, so joining every
    /// span's <see cref="Text"/> in order reproduces the message exactly.
    /// </remarks>
    public sealed class MessageTextSpan
    {
        /// <summary>What this run is.</summary>
        public required MessageSpanKind Kind { get; init; }

        /// <summary>The characters exactly as the message carries them.</summary>
        public required string Text { get; init; }

        /// <summary>
        /// Where following this run leads, or <see langword="null"/> for
        /// <see cref="MessageSpanKind.Text"/>.
        /// </summary>
        /// <remarks>
        /// Always an absolute <c>http</c>, <c>https</c> or <c>mailto</c> address. A bare
        /// <c>www.</c> address gains the <c>https</c> scheme it was written without, and an email
        /// address becomes a <c>mailto:</c> address, because neither is something an operating
        /// system can be handed as it stands.
        /// </remarks>
        public required string? Target { get; init; }
    }
}
