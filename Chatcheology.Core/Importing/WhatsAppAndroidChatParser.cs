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
    /// it is never read as continuation text and never as a message of its own. A line that is
    /// recognisably a header but not in an accepted format (a one-digit hour, <c>Sept</c>, a
    /// one-digit day or month in a numeric layout, a two- or five-digit year, an en dash or a
    /// no-break space in the separator) stops the parse in the same way, rather than
    /// being absorbed into the previous message.
    /// <para>
    /// Time-first exports write their final year's headers without a year
    /// (<c>HH:mm, d MMM - </c>). Such a header is accepted only under the approved year rule:
    /// it takes the year after the last dated header, and only as part of one final, non-decreasing
    /// run of yearless headers that follows at least one dated header. Every other case stops with
    /// the line number. <see cref="ParsedChatExport.YearSuppliedByRuleCount"/> reports how many
    /// messages were dated this way.
    /// </para>
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

        /// <summary>EN DASH, written as its code point so the source holds no look-alike character.</summary>
        private const char EnDash = (char)0x2013;

        /// <summary>EM DASH, written as its code point for the same reason.</summary>
        private const char EmDash = (char)0x2014;

        /// <summary>MINUS SIGN, written as its code point for the same reason.</summary>
        private const char MinusSign = (char)0x2212;

        /// <summary>NO-BREAK SPACE, written as its code point because it is invisible.</summary>
        private const char NoBreakSpace = (char)0x00A0;

        /// <summary>NARROW NO-BREAK SPACE, written as its code point because it is invisible.</summary>
        private const char NarrowNoBreakSpace = (char)0x202F;

        /// <summary>Separates the timestamp from the rest of the line.</summary>
        private const string HeaderSeparator = " - ";

        /// <summary>Separates the sender from the message content.</summary>
        private const string SenderSeparator = ": ";

        /// <summary>
        /// LEFT-TO-RIGHT MARK. Written as an escape sequence because the character is invisible,
        /// so the value cannot be altered by the encoding this file is stored or checked out with.
        /// </summary>
        private const char LeftToRightMark = '\u200E';

        /// <summary>RIGHT-TO-LEFT MARK. Written as an escape sequence for the same reason.</summary>
        private const char RightToLeftMark = '\u200F';

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
            var yearRule = new YearRule();
            var lineNumber = 0;

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lineNumber++;

                // Every structural decision is made on a direction-mark-free working copy, so a
                // mark sitting before the timestamp cannot hide the start of a new message. The
                // original line is what gets kept in RawContent.
                var workingLine = RemoveDirectionMarks(line);

                if (TryMatchHeaderShape(workingLine, out var match))
                {
                    var lineShape = match.Shape;

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
                    var header = ReadHeader(workingLine, lineNumber, match, yearRule);

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
                YearSuppliedByRuleCount = yearRule.SuppliedCount,
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
        /// if so which one, how long its timestamp is, and whether it is written exactly as the
        /// layout's accepted format requires. This is a shape test only;
        /// <see cref="ReadHeader"/> decides whether the shape is a valid header.
        /// </summary>
        /// <remarks>
        /// The shapes are deliberately much wider than the accepted formats, so that a line that is
        /// recognisably a header but not an accepted one reaches <see cref="ReadHeader"/> and stops
        /// the parse with its line number, instead of being absorbed as continuation text and
        /// credited to the previous message's sender. Widened: hours, minutes, days and months of
        /// one or two digits; years and time-first days of any number of digits; a time-first
        /// month of any run of letters, optionally followed by <c>.</c>; any whitespace (including
        /// no-break spaces) around the parts; and a dash separator that may be a hyphen, en dash,
        /// em dash or minus sign, with any whitespace before it and at least one whitespace
        /// character after it.
        /// <para>
        /// What is accepted is unchanged: a match is exact only when every field has the accepted
        /// width and every separator is exactly the accepted ASCII text (<c>", "</c>, a single
        /// space, <c>" - "</c>), and <see cref="TryParseTimestamp"/> refuses anything inexact.
        /// </para>
        /// <para>
        /// Digits are matched as ASCII <c>'0'</c>–<c>'9'</c> and month letters as ASCII letters, so
        /// the shape test agrees with what the invariant timestamp parse accepts. The three shapes
        /// cannot both match one line: after its first run of digits, a time-first header has
        /// <c>:</c>, a year-first header has three or more digits then <c>/</c>, and a day-first
        /// header has one or two digits then <c>/</c>.
        /// </para>
        /// </remarks>
        private static bool TryMatchHeaderShape(string line, out HeaderShapeMatch match)
        {
            if (TryMatchYearFirstShape(line, out match)
                || TryMatchDayFirstShape(line, out match)
                || TryMatchTimeFirstShape(line, out match))
            {
                return true;
            }

            match = default;
            return false;
        }

        /// <summary>
        /// The shape <c>ddd…/d[d]/d[d], d[d]:d[d] - </c>. Exact when the year has four digits,
        /// month, day, hour and minute have two, and the separators are exact.
        /// </summary>
        private static bool TryMatchYearFirstShape(string line, out HeaderShapeMatch match)
        {
            match = default;
            var position = 0;

            var year = ReadAsciiDigits(line, ref position, int.MaxValue);
            if (year < 3 || !ReadLiteral(line, ref position, "/"))
            {
                return false;
            }

            var month = ReadAsciiDigits(line, ref position, 2);
            if (month == 0 || !ReadLiteral(line, ref position, "/"))
            {
                return false;
            }

            var day = ReadAsciiDigits(line, ref position, 2);
            if (day == 0 || !TryReadComma(line, ref position, out var exactComma))
            {
                return false;
            }

            if (!TryReadTime(line, ref position, out var hour, out var minute))
            {
                return false;
            }

            return Complete(
                line,
                position,
                WhatsAppHeaderShape.YearFirst,
                exact: year == 4 && month == 2 && day == 2 && hour == 2 && minute == 2 && exactComma,
                isMissingYear: false,
                out match);
        }

        /// <summary>
        /// The shape <c>d[d]/d[d]/d…, d[d]:d[d] - </c>. Exact when day, month, hour and minute
        /// have two digits, the year has four, and the separators are exact.
        /// </summary>
        private static bool TryMatchDayFirstShape(string line, out HeaderShapeMatch match)
        {
            match = default;
            var position = 0;

            var day = ReadAsciiDigits(line, ref position, 2);
            if (day == 0 || !ReadLiteral(line, ref position, "/"))
            {
                return false;
            }

            var month = ReadAsciiDigits(line, ref position, 2);
            if (month == 0 || !ReadLiteral(line, ref position, "/"))
            {
                return false;
            }

            var year = ReadAsciiDigits(line, ref position, int.MaxValue);
            if (year == 0 || !TryReadComma(line, ref position, out var exactComma))
            {
                return false;
            }

            if (!TryReadTime(line, ref position, out var hour, out var minute))
            {
                return false;
            }

            return Complete(
                line,
                position,
                WhatsAppHeaderShape.DayFirst,
                exact: day == 2 && month == 2 && year == 4 && hour == 2 && minute == 2 && exactComma,
                isMissingYear: false,
                out match);
        }

        /// <summary>
        /// The shape <c>d[d]:d[d], d… L…[.][ d…] - </c>: a time, a day of any digits, a month of
        /// any letters with an optional full stop, and an optional year of any digits. Exact when
        /// hour and minute have two digits, the day one or two, the month three letters with no
        /// full stop, the year (if present) four digits, and the separators are exact.
        /// </summary>
        /// <remarks>
        /// A match with no year at all is the yearless form old exports use for their final
        /// year. It is matched with <see cref="HeaderShapeMatch.IsMissingYear"/> set; whether it is
        /// accepted is the year rule's decision (see <see cref="YearRule"/>), not the shape test's.
        /// </remarks>
        private static bool TryMatchTimeFirstShape(string line, out HeaderShapeMatch match)
        {
            match = default;
            var position = 0;

            if (!TryReadTime(line, ref position, out var hour, out var minute)
                || !TryReadComma(line, ref position, out var exactComma))
            {
                return false;
            }

            var day = ReadAsciiDigits(line, ref position, int.MaxValue);
            if (day == 0)
            {
                return false;
            }

            var exactDaySpace = ReadSpaces(line, ref position) == 1 && line[position - 1] == ' ';

            var month = ReadAsciiLetters(line, ref position);
            if (month == 0)
            {
                return false;
            }

            var hasFullStop = ReadLiteral(line, ref position, ".");

            // An optional year: whitespace, then digits. Without digits nothing is consumed, so
            // the whitespace is read again as part of the separator.
            var beforeYear = position;
            var yearSpaces = ReadSpaces(line, ref position);
            var exactYearSpace = yearSpaces == 1 && line[position - 1] == ' ';
            var year = ReadAsciiDigits(line, ref position, int.MaxValue);

            if (year == 0)
            {
                position = beforeYear;
            }

            var exact = hour == 2
                && minute == 2
                && exactComma
                && day <= 2
                && exactDaySpace
                && month == 3
                && !hasFullStop
                && (year == 0 || (year == 4 && exactYearSpace));

            return Complete(
                line,
                position,
                WhatsAppHeaderShape.TimeFirst,
                exact,
                isMissingYear: year == 0,
                out match);
        }

        /// <summary>Reads <c>d[d]:d[d]</c>, reporting the width of each part.</summary>
        private static bool TryReadTime(string line, ref int position, out int hour, out int minute)
        {
            minute = 0;
            hour = ReadAsciiDigits(line, ref position, 2);

            if (hour == 0 || !ReadLiteral(line, ref position, ":"))
            {
                return false;
            }

            minute = ReadAsciiDigits(line, ref position, 2);

            return minute > 0;
        }

        /// <summary>
        /// Reads a comma followed by at least one whitespace character. Exact when that is the
        /// accepted <c>", "</c>.
        /// </summary>
        private static bool TryReadComma(string line, ref int position, out bool exact)
        {
            exact = false;

            if (!ReadLiteral(line, ref position, ","))
            {
                return false;
            }

            var spaces = ReadSpaces(line, ref position);
            exact = spaces == 1 && line[position - 1] == ' ';

            return spaces > 0;
        }

        /// <summary>
        /// Requires a dash separator where the timestamp ends, at <paramref name="position"/>:
        /// any whitespace, one dash-like character, then at least one whitespace character.
        /// </summary>
        /// <remarks>
        /// The whitespace required after the dash is what keeps ordinary text such as
        /// <c>Mar-ish</c> out of the shape. The match is exact only when the separator begins with
        /// the accepted <c>" - "</c>; any further spaces after it belong to the sender, as before.
        /// </remarks>
        private static bool Complete(
            string line,
            int position,
            WhatsAppHeaderShape shape,
            bool exact,
            bool isMissingYear,
            out HeaderShapeMatch match)
        {
            match = default;
            var cursor = position;

            ReadSpaces(line, ref cursor);

            if (cursor >= line.Length || !IsDash(line[cursor]))
            {
                return false;
            }

            cursor++;

            if (ReadSpaces(line, ref cursor) == 0)
            {
                return false;
            }

            var exactSeparator =
                string.CompareOrdinal(line, position, HeaderSeparator, 0, HeaderSeparator.Length) == 0;

            match = new HeaderShapeMatch(shape, position, exact && exactSeparator, isMissingYear);
            return true;
        }

        /// <summary>
        /// Whether <paramref name="character"/> is one of the dash-like characters a near-miss
        /// separator may use: hyphen-minus, en dash, em dash or minus sign.
        /// </summary>
        private static bool IsDash(char character) =>
            character is '-' or EnDash or EmDash or MinusSign;

        /// <summary>
        /// Reads a run of whitespace a near-miss header may use — space, tab, no-break space and
        /// narrow no-break space — and returns its length.
        /// </summary>
        private static int ReadSpaces(string line, ref int position)
        {
            var start = position;

            while (position < line.Length
                && line[position] is ' ' or '\t' or NoBreakSpace or NarrowNoBreakSpace)
            {
                position++;
            }

            return position - start;
        }

        /// <summary>
        /// Reads up to <paramref name="maximum"/> ASCII digits from <paramref name="position"/>,
        /// advancing past them, and returns how many were read. A longer run leaves the next
        /// character a digit, which the following literal then fails on.
        /// </summary>
        private static int ReadAsciiDigits(string line, ref int position, int maximum)
        {
            var start = position;

            while (position < line.Length
                && position - start < maximum
                && char.IsAsciiDigit(line[position]))
            {
                position++;
            }

            return position - start;
        }

        /// <summary>Reads a run of ASCII letters and returns its length.</summary>
        private static int ReadAsciiLetters(string line, ref int position)
        {
            var start = position;

            while (position < line.Length && char.IsAsciiLetter(line[position]))
            {
                position++;
            }

            return position - start;
        }

        /// <summary>Consumes <paramref name="literal"/> at <paramref name="position"/> if it is there.</summary>
        private static bool ReadLiteral(string line, ref int position, string literal)
        {
            if (line.Length < position + literal.Length
                || string.CompareOrdinal(line, position, literal, 0, literal.Length) != 0)
            {
                return false;
            }

            position += literal.Length;
            return true;
        }

        /// <summary>
        /// A header-shaped line: its layout, the length of its timestamp, whether it is written
        /// exactly as the accepted format requires (fields and separators), and whether it is the
        /// time-first shape written without a year.
        /// </summary>
        private readonly record struct HeaderShapeMatch(
            WhatsAppHeaderShape Shape,
            int TimestampLength,
            bool HasExactWidths,
            bool IsMissingYear);

        /// <summary>
        /// Reads a header-shaped line as a user or system message header, or throws if it does not
        /// meet the exact format of its layout.
        /// </summary>
        private static MessageHeader ReadHeader(
            string line,
            int lineNumber,
            HeaderShapeMatch match,
            YearRule yearRule)
        {
            var timestampText = line.Substring(0, match.TimestampLength);

            DateTime timestamp;

            if (match.IsMissingYear)
            {
                timestamp = yearRule.SupplyYear(timestampText, match, lineNumber);
            }
            else
            {
                if (!TryParseTimestamp(timestampText, match, out timestamp))
                {
                    throw new FormatException(
                        $"Line {lineNumber}: the line has the {Describe(match.Shape)} message header " +
                        $"shape but the timestamp is not a valid '{FormatPattern(match.Shape)}' value.");
                }

                if (match.Shape == WhatsAppHeaderShape.TimeFirst)
                {
                    yearRule.RecordDated(timestamp, lineNumber);
                }
            }

            var remainder = line.Substring(match.TimestampLength + HeaderSeparator.Length);
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
        /// Parses a header-shaped timestamp with the invariant culture, accepting only the exact
        /// format of its layout.
        /// </summary>
        /// <remarks>
        /// A shape match whose fields do not all have the accepted widths is refused before any
        /// parse, so a widened shape never widens what is accepted. For the time-first layout the
        /// month abbreviation is then required to be exactly one of the invariant English
        /// abbreviations, compared ordinally, because the framework's own month-name matching
        /// ignores case and would accept <c>jan</c> or <c>JAN</c>.
        /// </remarks>
        private static bool TryParseTimestamp(
            string timestampText,
            HeaderShapeMatch match,
            out DateTime timestamp)
        {
            timestamp = default;

            if (!match.HasExactWidths)
            {
                return false;
            }

            if (match.Shape == WhatsAppHeaderShape.TimeFirst)
            {
                // With exact widths, the month token occupies the three characters before " yyyy".
                var month = timestampText.Substring(timestampText.Length - 8, 3);

                if (Array.IndexOf(
                        CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames,
                        month) < 0)
                {
                    return false;
                }
            }

            return DateTime.TryParseExact(
                timestampText,
                match.Shape switch
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

        /// <summary>
        /// The approved year rule (D6) for time-first exports, which write headers in the
        /// export's final year without a year: <c>HH:mm, d MMM - </c>.
        /// </summary>
        /// <remarks>
        /// A yearless header is accepted, with the year after the year of the last dated header,
        /// only when every condition holds; anything else stops the parse with the line number:
        /// <list type="bullet">
        /// <item>its fields have the accepted widths and an exact month abbreviation, as for the
        /// dated form;</item>
        /// <item>at least one dated header precedes it;</item>
        /// <item>no dated header follows the first yearless one, so the yearless headers are one
        /// final run;</item>
        /// <item>yearless timestamps never step backwards in source order (a backwards step
        /// would mean the run crosses a year end the rule cannot see);</item>
        /// <item>the date exists in the supplied year (29 Feb in a non-leap year does not).</item>
        /// </list>
        /// Only the parsed timestamp carries the supplied year; <c>RawContent</c> keeps the line
        /// as written.
        /// </remarks>
        private sealed class YearRule
        {
            /// <summary>A leap year, used only to check a yearless header's day and month.</summary>
            private const int ValidationYear = 2000;

            private int? _lastDatedYear;
            private bool _yearlessStarted;
            private DateTime? _lastYearlessTimestamp;

            /// <summary>How many headers had their year supplied.</summary>
            internal int SuppliedCount { get; private set; }

            /// <summary>Records a dated time-first header, refusing one after a yearless run.</summary>
            internal void RecordDated(DateTime timestamp, int lineNumber)
            {
                if (_yearlessStarted)
                {
                    throw new FormatException(
                        $"Line {lineNumber}: a dated header follows a header written without a " +
                        $"year. The year rule applies only to a final run of headers without a " +
                        $"year, so the parse stops here.");
                }

                _lastDatedYear = timestamp.Year;
            }

            /// <summary>Returns the timestamp of a yearless header with its year supplied.</summary>
            internal DateTime SupplyYear(string timestampText, HeaderShapeMatch match, int lineNumber)
            {
                if (!TryParseTimestamp(WithYear(timestampText, ValidationYear), match, out _))
                {
                    throw new FormatException(
                        $"Line {lineNumber}: the line has the time-first (A) message header shape " +
                        $"without a year, but the timestamp is not a valid 'HH:mm, d MMM' value.");
                }

                if (_lastDatedYear is not { } lastDatedYear)
                {
                    throw new FormatException(
                        $"Line {lineNumber}: the line has the time-first (A) message header shape " +
                        $"but its timestamp has no year ('HH:mm, d MMM'), and no dated header " +
                        $"precedes it, so no year can be supplied.");
                }

                if (!TryParseTimestamp(WithYear(timestampText, lastDatedYear + 1), match, out var timestamp))
                {
                    throw new FormatException(
                        $"Line {lineNumber}: the header without a year is not a valid date in the " +
                        $"supplied year (the year after the last dated header).");
                }

                if (_lastYearlessTimestamp is { } previous && timestamp < previous)
                {
                    throw new FormatException(
                        $"Line {lineNumber}: this header without a year is earlier than the one " +
                        $"before it. The supplied year would date it wrongly, so the parse stops " +
                        $"here.");
                }

                _yearlessStarted = true;
                _lastYearlessTimestamp = timestamp;
                SuppliedCount++;

                return timestamp;
            }

            private static string WithYear(string timestampText, int year) =>
                $"{timestampText} {year.ToString("D4", CultureInfo.InvariantCulture)}";
        }

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
