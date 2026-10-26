using System.Globalization;
using System.Text;
using Chatcheology.Core.Models;

namespace Chatcheology.Core.Importing
{
    /// <summary>
    /// Parses WhatsApp Android text exports into logical messages. Three header layouts are
    /// supported, one per file: the pinned workspace layout
    /// <c>yyyy/MM/dd, HH:mm - Sender: Message</c> and the two older layouts
    /// <c>HH:mm, d MMM yyyy - Sender: Message</c> and <c>dd/MM/yyyy, HH:mm - Sender: Message</c>
    /// (see <see cref="WhatsAppHeaderShape"/>).
    /// </summary>
    /// <remarks>
    /// The parser is deliberately narrow and conservative. It supports exactly these three
    /// timestamp layouts, detects which one a file uses from its first message header, holds the
    /// rest of the file to that one layout, and fails clearly rather than guessing at anything
    /// else, so that source data is never silently misinterpreted. A header-shaped line of a
    /// different supported layout later in the same file stops the parse with its line number;
    /// it is never read as continuation text and never as a message of its own.
    /// <para>
    /// The three layouts cannot be confused with one another: <see cref="WhatsAppHeaderShape.TimeFirst"/>
    /// starts with <c>dd:</c>, <see cref="WhatsAppHeaderShape.YearFirst"/> with <c>dddd/</c> and
    /// <see cref="WhatsAppHeaderShape.DayFirst"/> with <c>dd/</c>.
    /// </para>
    /// <para>
    /// Supported within each layout: Unicode and emoji, senders containing spaces, colons inside
    /// the message body, multiline continuation lines, repeated timestamps, WhatsApp system
    /// messages that carry no sender, and the invisible direction marks U+200E and U+200F.
    /// </para>
    /// <para>
    /// A timestamped line is read as a <see cref="MessageType.User"/> message when the text after
    /// the timestamp prefix contains the exact <c>": "</c> delimiter with a non-empty sender before
    /// it, and as a <see cref="MessageType.System"/> message when that delimiter is absent
    /// entirely. A known ambiguity remains: genuine system prose that happens to contain <c>": "</c>
    /// is structurally indistinguishable from a user message and is read as one. No heuristic
    /// attempts to tell them apart, because any such guess would be unreliable and would risk
    /// misattributing a message to a participant who did not write it.
    /// </para>
    /// <para>
    /// Month names in the time-first layout are the invariant-culture English abbreviations
    /// (<c>Jan</c> to <c>Dec</c>), matched ordinally, so <c>jan</c> or <c>Sept</c> is a malformed
    /// header rather than a month. The day may be written with one or two digits.
    /// </para>
    /// <para>
    /// Not supported: any other timestamp layout, 12-hour clocks, timestamps with seconds, and
    /// system-message subtypes. Direction marks other than U+200E and U+200F are not recognised,
    /// and no other invisible or control character is stripped.
    /// </para>
    /// <para>
    /// The parser only reads. It never writes, renames or otherwise touches the source.
    /// </para>
    /// </remarks>
    public sealed class WhatsAppAndroidChatParser
    {
        /// <summary>
        /// The exact year-first timestamp format. The separators are quoted so that the <c>/</c>
        /// and <c>:</c> characters are matched literally instead of as culture-dependent separator
        /// placeholders.
        /// </summary>
        private const string YearFirstTimestampFormat = "yyyy'/'MM'/'dd', 'HH':'mm";

        /// <summary>The exact day-first timestamp format, quoted for the same reason.</summary>
        private const string DayFirstTimestampFormat = "dd'/'MM'/'yyyy', 'HH':'mm";

        /// <summary>
        /// The time-first timestamp format. <c>d</c> accepts a one- or two-digit day; the month
        /// abbreviation is checked ordinally before this format is applied.
        /// </summary>
        private const string TimeFirstTimestampFormat = "HH':'mm', 'd' 'MMM' 'yyyy";

        /// <summary>Length of <c>yyyy/MM/dd, HH:mm</c> and of <c>dd/MM/yyyy, HH:mm</c>.</summary>
        private const int FixedTimestampLength = 17;

        /// <summary>Length of <c>HH:mm, </c>, the fixed start of the time-first layout.</summary>
        private const int TimeFirstPrefixLength = 7;

        /// <summary>Separates the timestamp from the rest of the line.</summary>
        private const string HeaderSeparator = " - ";

        /// <summary>Separates the sender from the message content.</summary>
        private const string SenderSeparator = ": ";

        /// <summary>
        /// LEFT-TO-RIGHT MARK. Written as an escape sequence because the character is invisible,
        /// so the value cannot be altered by the encoding this file is stored or checked out with.
        /// </summary>
        private const char LeftToRightMark = '‎';

        /// <summary>RIGHT-TO-LEFT MARK. Written as an escape sequence for the same reason.</summary>
        private const char RightToLeftMark = '‏';

        /// <summary>
        /// Reads every logical message from <paramref name="reader"/>, preserving source order.
        /// </summary>
        /// <param name="reader">The export text. Only read from; never modified.</param>
        /// <returns>
        /// The logical messages in source order, with 1-based
        /// <see cref="ParsedMessage.SequenceNumber"/> values. The result is never sorted by
        /// timestamp. Empty input, and input containing only whitespace, produce an empty list.
        /// </returns>
        /// <remarks>
        /// The same parse as <see cref="ParseExport"/>, without the detected layout.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
        /// <exception cref="FormatException">
        /// As for <see cref="ParseExport"/>.
        /// </exception>
        public IReadOnlyList<ParsedMessage> Parse(TextReader reader) => ParseExport(reader).Messages;

        /// <summary>
        /// Reads every logical message from <paramref name="reader"/>, preserving source order, and
        /// reports which header layout the export uses.
        /// </summary>
        /// <param name="reader">The export text. Only read from; never modified.</param>
        /// <returns>
        /// The detected layout and the logical messages in source order, with 1-based
        /// <see cref="ParsedMessage.SequenceNumber"/> values. Empty or whitespace-only input
        /// produces no messages and no layout.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
        /// <exception cref="FormatException">
        /// Non-whitespace content appears before the first supported message header, a line has a
        /// supported header shape but is not a valid header, or a line has the header shape of a
        /// supported layout other than the one the file's first header established. Exception
        /// messages identify the 1-based physical line number and the reason only; they never
        /// include content read from the source.
        /// </exception>
        public ParsedChatExport ParseExport(TextReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);

            var messages = new List<ParsedMessage>();
            PendingMessage? pending = null;
            WhatsAppHeaderShape? fileShape = null;
            var lineNumber = 0;

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lineNumber++;

                // Every structural decision is made on a direction-mark-free working copy, so a
                // mark sitting before the timestamp cannot hide the start of a new message. The
                // original line is what gets kept in RawContent.
                var workingLine = RemoveDirectionMarks(line);

                if (TryMatchHeaderShape(workingLine, out var lineShape, out var timestampLength))
                {
                    if (fileShape is null)
                    {
                        // Decided once, by the first header, and never revisited.
                        fileShape = lineShape;
                    }
                    else if (fileShape != lineShape)
                    {
                        // Absorbing it as continuation text would hide a message; reading it as a
                        // header would mix two layouts in one file. Neither is safe to guess.
                        throw new FormatException(
                            $"Line {lineNumber}: the line has the {Describe(lineShape)} message " +
                            $"header layout, but the file's first header uses the " +
                            $"{Describe(fileShape.Value)} layout. One export uses one layout, so " +
                            $"the parse stops here.");
                    }

                    // Header-shaped lines are held to the exact format, whether or not a message
                    // is already open. Degrading one to continuation text would silently absorb
                    // data that was probably meant to be a message of its own.
                    var header = ReadHeader(workingLine, lineNumber, lineShape, timestampLength);

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
                    pending.AddContinuation(line, workingLine, lineNumber);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(workingLine))
                {
                    throw new FormatException(
                        $"Line {lineNumber}: content appears before the first supported message " +
                        $"header, so it cannot be attributed to a message. Expected one of the " +
                        $"formats 'yyyy/MM/dd, HH:mm - Sender: Message', " +
                        $"'dd/MM/yyyy, HH:mm - Sender: Message' or " +
                        $"'HH:mm, d MMM yyyy - Sender: Message'.");
                }

                // Leading whitespace-only lines belong to no message and are skipped.
            }

            if (pending is not null)
            {
                messages.Add(pending.ToParsedMessage(messages.Count + 1));
            }

            return new ParsedChatExport
            {
                Shape = fileShape,
                Messages = messages,
            };
        }

        /// <summary>
        /// Returns <paramref name="line"/> without any U+200E or U+200F character, or the same
        /// instance when it contains neither.
        /// </summary>
        /// <remarks>
        /// Only these two code points are removed. Other invisible characters — zero-width spaces,
        /// no-break spaces, other bidirectional controls — are content and are left alone.
        /// </remarks>
        private static string RemoveDirectionMarks(string line)
        {
            if (line.AsSpan().IndexOfAny(LeftToRightMark, RightToLeftMark) < 0)
            {
                return line;
            }

            var builder = new StringBuilder(line.Length);

            foreach (var character in line)
            {
                if (character is not (LeftToRightMark or RightToLeftMark))
                {
                    builder.Append(character);
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Tests whether a line begins with the header shape of one of the supported layouts, and
        /// if so which one and how long its timestamp is. This is a shape test only;
        /// <see cref="ReadHeader"/> decides whether the shape is a valid header.
        /// </summary>
        /// <remarks>
        /// Digits are matched as ASCII <c>'0'</c>–<c>'9'</c> rather than as any Unicode decimal
        /// digit, and month letters as ASCII letters, so the shape test agrees with what the
        /// invariant timestamp parse accepts. The three shapes differ within their first five
        /// characters, so at most one can match.
        /// </remarks>
        private static bool TryMatchHeaderShape(
            string line,
            out WhatsAppHeaderShape shape,
            out int timestampLength)
        {
            if (HasYearFirstShape(line))
            {
                shape = WhatsAppHeaderShape.YearFirst;
                timestampLength = FixedTimestampLength;
                return true;
            }

            if (HasDayFirstShape(line))
            {
                shape = WhatsAppHeaderShape.DayFirst;
                timestampLength = FixedTimestampLength;
                return true;
            }

            if (TryMatchTimeFirstShape(line, out timestampLength))
            {
                shape = WhatsAppHeaderShape.TimeFirst;
                return true;
            }

            shape = default;
            timestampLength = 0;
            return false;
        }

        /// <summary>The fixed-width shape <c>dddd/dd/dd, dd:dd - </c>.</summary>
        private static bool HasYearFirstShape(string line)
        {
            if (line.Length < FixedTimestampLength + HeaderSeparator.Length)
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
                && HasHeaderSeparatorAt(line, FixedTimestampLength);
        }

        /// <summary>The fixed-width shape <c>dd/dd/dddd, dd:dd - </c>.</summary>
        private static bool HasDayFirstShape(string line)
        {
            if (line.Length < FixedTimestampLength + HeaderSeparator.Length)
            {
                return false;
            }

            return IsAsciiDigits(line, 0, 2)                                 // dd
                && line[2] == '/'
                && IsAsciiDigits(line, 3, 2)                                 // MM
                && line[5] == '/'
                && IsAsciiDigits(line, 6, 4)                                 // yyyy
                && line[10] == ','
                && line[11] == ' '
                && IsAsciiDigits(line, 12, 2)                                // HH
                && line[14] == ':'
                && IsAsciiDigits(line, 15, 2)                                // mm
                && HasHeaderSeparatorAt(line, FixedTimestampLength);
        }

        /// <summary>
        /// The shape <c>dd:dd, d[d] LLL dddd - </c>, whose length depends on whether the day has
        /// one digit or two.
        /// </summary>
        private static bool TryMatchTimeFirstShape(string line, out int timestampLength)
        {
            timestampLength = 0;

            if (line.Length < TimeFirstPrefixLength
                || !IsAsciiDigits(line, 0, 2)                                // HH
                || line[2] != ':'
                || !IsAsciiDigits(line, 3, 2)                                // mm
                || line[5] != ','
                || line[6] != ' ')
            {
                return false;
            }

            foreach (var dayDigits in (ReadOnlySpan<int>)[1, 2])
            {
                var monthStart = TimeFirstPrefixLength + dayDigits + 1;
                var yearStart = monthStart + 4;
                var length = yearStart + 4;

                if (line.Length < length + HeaderSeparator.Length)
                {
                    continue;
                }

                if (IsAsciiDigits(line, TimeFirstPrefixLength, dayDigits)    // d or dd
                    && line[monthStart - 1] == ' '
                    && IsAsciiLetters(line, monthStart, 3)                   // MMM
                    && line[yearStart - 1] == ' '
                    && IsAsciiDigits(line, yearStart, 4)                     // yyyy
                    && HasHeaderSeparatorAt(line, length))
                {
                    timestampLength = length;
                    return true;
                }
            }

            return false;
        }

        private static bool HasHeaderSeparatorAt(string line, int index) =>
            string.CompareOrdinal(line, index, HeaderSeparator, 0, HeaderSeparator.Length) == 0;

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

        private static bool IsAsciiLetters(string value, int start, int length)
        {
            for (var index = start; index < start + length; index++)
            {
                if (!char.IsAsciiLetter(value[index]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Reads a header-shaped line as a user or system message header, or throws if it does not
        /// meet the exact format of its layout.
        /// </summary>
        private static MessageHeader ReadHeader(
            string line,
            int lineNumber,
            WhatsAppHeaderShape shape,
            int timestampLength)
        {
            if (!TryParseTimestamp(line.Substring(0, timestampLength), shape, out var timestamp))
            {
                throw new FormatException(
                    $"Line {lineNumber}: the line has the {Describe(shape)} message header shape " +
                    $"but the timestamp is not a valid '{FormatPattern(shape)}' value.");
            }

            var remainder = line.Substring(timestampLength + HeaderSeparator.Length);
            var senderSeparatorIndex = remainder.IndexOf(SenderSeparator, StringComparison.Ordinal);

            if (senderSeparatorIndex < 0)
            {
                // No sender delimiter anywhere, so there is no sender to attribute the text to.
                // WhatsApp's own notices take exactly this shape. The whole remainder is content,
                // including any colon that is not followed by a space.
                return new MessageHeader(timestamp, MessageType.System, Sender: null, remainder);
            }

            if (senderSeparatorIndex == 0)
            {
                // The delimiter is present but there is nothing before it. That is structurally
                // broken rather than a system message, so it fails instead of being guessed at.
                throw new FormatException(
                    $"Line {lineNumber}: the line has the supported message header shape but the " +
                    $"sender is empty.");
            }

            var sender = remainder.Substring(0, senderSeparatorIndex);
            var content = remainder.Substring(senderSeparatorIndex + SenderSeparator.Length);

            // Only the ": " delimiter itself is consumed, so any further colons, extra leading
            // spaces and trailing spaces in the body survive untouched. An empty body is allowed.
            return new MessageHeader(timestamp, MessageType.User, sender, content);
        }

        /// <summary>
        /// Parses a timestamp of the given layout with the invariant culture.
        /// </summary>
        /// <remarks>
        /// For the time-first layout the month abbreviation is first required to be exactly one of
        /// the invariant English abbreviations, compared ordinally, because the framework's own
        /// month-name matching ignores case and would accept <c>jan</c> or <c>JAN</c>.
        /// </remarks>
        private static bool TryParseTimestamp(
            string timestampText,
            WhatsAppHeaderShape shape,
            out DateTime timestamp)
        {
            if (shape == WhatsAppHeaderShape.TimeFirst)
            {
                // The month token always occupies the three characters before " yyyy".
                var month = timestampText.Substring(timestampText.Length - 8, 3);

                if (Array.IndexOf(
                        CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames,
                        month) < 0)
                {
                    timestamp = default;
                    return false;
                }
            }

            return DateTime.TryParseExact(
                timestampText,
                shape switch
                {
                    WhatsAppHeaderShape.TimeFirst => TimeFirstTimestampFormat,
                    WhatsAppHeaderShape.DayFirst => DayFirstTimestampFormat,
                    _ => YearFirstTimestampFormat,
                },
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out timestamp);
        }

        /// <summary>A short name for a layout, for diagnostics.</summary>
        private static string Describe(WhatsAppHeaderShape shape) => shape switch
        {
            WhatsAppHeaderShape.TimeFirst => "time-first (A)",
            WhatsAppHeaderShape.DayFirst => "day-first (B)",
            _ => "year-first (C)",
        };

        /// <summary>The readable pattern of a layout's timestamp, for diagnostics.</summary>
        private static string FormatPattern(WhatsAppHeaderShape shape) => shape switch
        {
            WhatsAppHeaderShape.TimeFirst => "HH:mm, d MMM yyyy",
            WhatsAppHeaderShape.DayFirst => "dd/MM/yyyy, HH:mm",
            _ => "yyyy/MM/dd, HH:mm",
        };

        private readonly record struct MessageHeader(
            DateTime Timestamp,
            MessageType Type,
            string? Sender,
            string Content);

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

            /// <param name="line">The line as read, kept for <see cref="ParsedMessage.RawContent"/>.</param>
            /// <param name="workingLine">
            /// The direction-mark-free copy, used for <see cref="ParsedMessage.MessageContent"/>.
            /// </param>
            /// <param name="lineNumber">The 1-based physical source line.</param>
            internal void AddContinuation(string line, string workingLine, int lineNumber)
            {
                _contentLines.Add(workingLine);
                _rawLines.Add(line);
                _sourceLineEnd = lineNumber;
            }

            internal ParsedMessage ToParsedMessage(int sequenceNumber) => new()
            {
                SequenceNumber = sequenceNumber,
                MessageDateTime = _header.Timestamp,
                MessageType = _header.Type,
                Sender = _header.Sender,
                MessageContent = string.Join(ParsedMessage.LineSeparator, _contentLines),
                RawContent = string.Join(ParsedMessage.LineSeparator, _rawLines),
                SourceLineStart = _sourceLineStart,
                SourceLineEnd = _sourceLineEnd,
            };
        }
    }
}
