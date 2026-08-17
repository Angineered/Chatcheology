using System.Globalization;
using Chatcheology.Core.Models;

namespace Chatcheology.Core.Importing
{
    /// <summary>
    /// Parses the pinned WhatsApp Android export layout
    /// <c>yyyy/MM/dd, HH:mm - Sender: Message</c> into logical messages.
    /// </summary>
    /// <remarks>
    /// The parser is deliberately narrow and conservative. It supports exactly one header layout
    /// and fails clearly rather than guessing at anything else, so that source data is never
    /// silently misinterpreted.
    /// <para>
    /// Supported within that layout: Unicode and emoji, senders containing spaces, colons inside
    /// the message body, multiline continuation lines, and repeated timestamps.
    /// </para>
    /// <para>
    /// Not supported in this phase: any other timestamp layout, 12-hour clocks, timestamps with
    /// seconds, and system messages that have no <c>": "</c> sender delimiter (for example the
    /// end-to-end encryption notice). Invisible direction markers such as U+200E are neither
    /// stripped nor normalised, so a header or media placeholder carrying them is not recognised.
    /// </para>
    /// <para>
    /// The parser only reads. It never writes, renames or otherwise touches the source.
    /// </para>
    /// </remarks>
    public sealed class WhatsAppAndroidChatParser
    {
        /// <summary>
        /// The exact timestamp format. The separators are quoted so that the <c>/</c> and <c>:</c>
        /// characters are matched literally instead of as culture-dependent separator
        /// placeholders.
        /// </summary>
        private const string TimestampFormat = "yyyy'/'MM'/'dd', 'HH':'mm";

        /// <summary>Length of <c>yyyy/MM/dd, HH:mm</c>.</summary>
        private const int TimestampLength = 17;

        /// <summary>Separates the timestamp from the sender.</summary>
        private const string HeaderSeparator = " - ";

        /// <summary>Separates the sender from the message content.</summary>
        private const string SenderSeparator = ": ";

        /// <summary>
        /// Reads every logical message from <paramref name="reader"/>, preserving source order.
        /// </summary>
        /// <param name="reader">The export text. Only read from; never modified.</param>
        /// <returns>
        /// The logical messages in source order, with 1-based
        /// <see cref="ParsedMessage.SequenceNumber"/> values. The result is never sorted by
        /// timestamp. Empty input, and input containing only whitespace, produce an empty list.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
        /// <exception cref="FormatException">
        /// Non-whitespace content appears before the first supported message header, or a line has
        /// the supported header shape but is not a valid header. Exception messages identify the
        /// 1-based physical line number and the reason only; they never include content read from
        /// the source.
        /// </exception>
        public IReadOnlyList<ParsedMessage> Parse(TextReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);

            var messages = new List<ParsedMessage>();
            PendingMessage? pending = null;
            var lineNumber = 0;

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lineNumber++;

                if (HasSupportedHeaderShape(line))
                {
                    // Header-shaped lines are held to the exact format, whether or not a message
                    // is already open. Degrading one to continuation text would silently absorb
                    // data that was probably meant to be a message of its own.
                    var header = ReadHeader(line, lineNumber);

                    if (pending is not null)
                    {
                        messages.Add(pending.ToParsedMessage(messages.Count + 1));
                    }

                    pending = new PendingMessage(header, line, lineNumber);
                    continue;
                }

                if (pending is not null)
                {
                    // Any line that does not start a new supported message continues the current
                    // one. Blank and whitespace-only lines are content and are preserved.
                    pending.AddContinuation(line, lineNumber);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    throw new FormatException(
                        $"Line {lineNumber}: content appears before the first supported message " +
                        $"header, so it cannot be attributed to a message. Expected the format " +
                        $"'yyyy/MM/dd, HH:mm - Sender: Message'.");
                }

                // Leading whitespace-only lines belong to no message and are skipped.
            }

            if (pending is not null)
            {
                messages.Add(pending.ToParsedMessage(messages.Count + 1));
            }

            return messages;
        }

        /// <summary>
        /// Tests whether a line begins with the fixed-width shape
        /// <c>dddd/dd/dd, dd:dd - </c>. This is a shape test only; <see cref="ReadHeader"/>
        /// decides whether the shape is a valid header.
        /// </summary>
        /// <remarks>
        /// Digits are matched as ASCII <c>'0'</c>–<c>'9'</c> rather than as any Unicode decimal
        /// digit, so the shape test agrees with what the invariant timestamp parse accepts.
        /// </remarks>
        private static bool HasSupportedHeaderShape(string line)
        {
            if (line.Length < TimestampLength + HeaderSeparator.Length)
            {
                return false;
            }

            return IsAsciiDigits(line, 0, 4)                                 // yyyy
                && line[4] == '/'
                && IsAsciiDigits(line, 5, 2)                                 // MM
                && line[7] == '/'
                && IsAsciiDigits(line, 8, 2)                                 // dd
                && line[10] == ','
                && line[11] == ' '
                && IsAsciiDigits(line, 12, 2)                                // HH
                && line[14] == ':'
                && IsAsciiDigits(line, 15, 2)                                // mm
                && string.CompareOrdinal(line, TimestampLength, HeaderSeparator, 0, HeaderSeparator.Length) == 0;
        }

        private static bool IsAsciiDigits(string value, int start, int length)
        {
            for (var index = start; index < start + length; index++)
            {
                if (!char.IsAsciiDigit(value[index]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Reads a header-shaped line as a header, or throws if it does not meet the exact format.
        /// </summary>
        private static MessageHeader ReadHeader(string line, int lineNumber)
        {
            var timestampText = line.AsSpan(0, TimestampLength);

            if (!DateTime.TryParseExact(
                    timestampText,
                    TimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var timestamp))
            {
                throw new FormatException(
                    $"Line {lineNumber}: the line has the supported message header shape but the " +
                    $"timestamp is not a valid 'yyyy/MM/dd, HH:mm' value.");
            }

            var remainder = line.Substring(TimestampLength + HeaderSeparator.Length);
            var senderSeparatorIndex = remainder.IndexOf(SenderSeparator, StringComparison.Ordinal);

            if (senderSeparatorIndex < 0)
            {
                throw new FormatException(
                    $"Line {lineNumber}: the line has the supported message header shape but is " +
                    $"missing the ': ' delimiter between the sender and the message content. " +
                    $"System messages without a sender are not supported.");
            }

            if (senderSeparatorIndex == 0)
            {
                throw new FormatException(
                    $"Line {lineNumber}: the line has the supported message header shape but the " +
                    $"sender is empty.");
            }

            var sender = remainder.Substring(0, senderSeparatorIndex);
            var content = remainder.Substring(senderSeparatorIndex + SenderSeparator.Length);

            // Only the ": " delimiter itself is consumed, so any further colons, extra leading
            // spaces and trailing spaces in the body survive untouched. An empty body is allowed.
            return new MessageHeader(timestamp, sender, content);
        }

        private readonly record struct MessageHeader(DateTime Timestamp, string Sender, string Content);

        /// <summary>
        /// A logical message being accumulated while its continuation lines are read.
        /// </summary>
        private sealed class PendingMessage
        {
            private readonly MessageHeader _header;
            private readonly List<string> _contentLines;
            private readonly List<string> _rawLines;
            private readonly int _sourceLineStart;
            private int _sourceLineEnd;

            internal PendingMessage(MessageHeader header, string headerLine, int lineNumber)
            {
                _header = header;
                _contentLines = [header.Content];
                _rawLines = [headerLine];
                _sourceLineStart = lineNumber;
                _sourceLineEnd = lineNumber;
            }

            internal void AddContinuation(string line, int lineNumber)
            {
                _contentLines.Add(line);
                _rawLines.Add(line);
                _sourceLineEnd = lineNumber;
            }

            internal ParsedMessage ToParsedMessage(int sequenceNumber) => new()
            {
                SequenceNumber = sequenceNumber,
                MessageDateTime = _header.Timestamp,
                Sender = _header.Sender,
                MessageContent = string.Join(ParsedMessage.LineSeparator, _contentLines),
                RawContent = string.Join(ParsedMessage.LineSeparator, _rawLines),
                SourceLineStart = _sourceLineStart,
                SourceLineEnd = _sourceLineEnd,
            };
        }
    }
}
