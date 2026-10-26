using System.Globalization;
using Chatcheology.Core.Importing;
using Chatcheology.Core.Models;

namespace Chatcheology.Core.Tests.Importing
{
    /// <summary>
    /// Tests for the older export layouts (shape A <c>HH:mm, d MMM yyyy</c> and shape B
    /// <c>dd/MM/yyyy, HH:mm</c>), layout detection, and <see cref="ParsedMessage.AttachedFileName"/>.
    /// </summary>
    /// <remarks>
    /// Synthetic data only: invented names and words.
    /// </remarks>
    public class WhatsAppAndroidChatParserOlderLayoutTests
    {
        private const string LeftToRightMark = "\u200E";

        [Fact]
        public void ShapeA_TwoDigitDay_ParsesTimestampSenderAndContent()
        {
            var export = ParseExport("14:03, 15 Jun 2013 - Alex Example: zorbly wibble");

            Assert.Equal(WhatsAppHeaderShape.TimeFirst, export.Shape);
            var message = Assert.Single(export.Messages);
            Assert.Equal(new DateTime(2013, 6, 15, 14, 3, 0), message.MessageDateTime);
            Assert.Equal(DateTimeKind.Unspecified, message.MessageDateTime.Kind);
            Assert.Equal(MessageType.User, message.MessageType);
            Assert.Equal("Alex Example", message.Sender);
            Assert.Equal("zorbly wibble", message.MessageContent);
        }

        [Fact]
        public void ShapeA_OneDigitDay_IsAccepted()
        {
            var export = ParseExport(
                """
                09:41, 5 Jan 2014 - Sam Sample: plonk
                23:59, 31 Dec 2014 - Alex Example: florp
                """);

            Assert.Equal(WhatsAppHeaderShape.TimeFirst, export.Shape);
            Assert.Equal(new DateTime(2014, 1, 5, 9, 41, 0), export.Messages[0].MessageDateTime);
            Assert.Equal(new DateTime(2014, 12, 31, 23, 59, 0), export.Messages[1].MessageDateTime);
        }

        [Fact]
        public void ShapeA_EveryInvariantMonthAbbreviation_IsAccepted()
        {
            var months = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
            var text = string.Join("\n", months.Select(month => $"10:00, 1 {month} 2014 - Sam Sample: x"));

            var messages = ParseExport(text).Messages;

            Assert.Equal(Enumerable.Range(1, 12), messages.Select(message => message.MessageDateTime.Month));
        }

        [Theory]
        [InlineData("10:00, 1 jan 2014 - Sam Sample: x")]
        [InlineData("10:00, 1 JAN 2014 - Sam Sample: x")]
        [InlineData("10:00, 1 Foo 2014 - Sam Sample: x")]
        [InlineData("10:00, 32 Jan 2014 - Sam Sample: x")]
        [InlineData("25:00, 1 Jan 2014 - Sam Sample: x")]
        [InlineData("10:00, 30 Feb 2014 - Sam Sample: x")]
        public void ShapeA_MalformedTimestamp_Throws(string line)
        {
            var exception = Assert.Throws<FormatException>(() => ParseExport(line));

            Assert.Contains("Line 1", exception.Message, StringComparison.Ordinal);
            Assert.Contains("timestamp", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Sam", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ShapeA_IsUnaffectedByTheCurrentCulture()
        {
            var original = CultureInfo.CurrentCulture;

            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                var messages = ParseExport("10:00, 3 Mar 2014 - Sam Sample: x").Messages;

                Assert.Equal(new DateTime(2014, 3, 3, 10, 0, 0), messages[0].MessageDateTime);
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public void ShapeB_ParsesDayFirstTimestamp()
        {
            var export = ParseExport(
                """
                10/04/2015, 08:01 - Alex Example: blimble
                07/08/2015, 22:10 - Sam Sample: quorx
                """);

            Assert.Equal(WhatsAppHeaderShape.DayFirst, export.Shape);
            Assert.Equal(new DateTime(2015, 4, 10, 8, 1, 0), export.Messages[0].MessageDateTime);
            Assert.Equal(new DateTime(2015, 8, 7, 22, 10, 0), export.Messages[1].MessageDateTime);
            Assert.Equal("Sam Sample", export.Messages[1].Sender);
        }

        [Fact]
        public void ShapeB_ImpossibleDate_Throws()
        {
            var exception = Assert.Throws<FormatException>(
                () => ParseExport("10/04/2015, 08:01 - Alex Example: a\n31/04/2015, 08:02 - Sam Sample: b"));

            Assert.Contains("Line 2", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ShapeC_IsDetectedAndUnchanged()
        {
            var export = ParseExport("2026/01/05, 14:03 - Alex: first");

            Assert.Equal(WhatsAppHeaderShape.YearFirst, export.Shape);
            Assert.Equal(new DateTime(2026, 1, 5, 14, 3, 0), export.Messages[0].MessageDateTime);
        }

        [Fact]
        public void EmptyInput_HasNoShape()
        {
            var export = ParseExport("  \n");

            Assert.Null(export.Shape);
            Assert.Empty(export.Messages);
        }

        [Fact]
        public void Parse_ReturnsTheSameMessagesAsParseExport()
        {
            const string text = "10/04/2015, 08:01 - Alex Example: a\nmore\n10/04/2015, 08:02 - Sam Sample: b";

            using var first = new StringReader(text);
            using var second = new StringReader(text);
            var parser = new WhatsAppAndroidChatParser();

            var viaParse = parser.Parse(first);
            var viaExport = parser.ParseExport(second).Messages;

            Assert.Equal(
                viaExport.Select(message => message.RawContent),
                viaParse.Select(message => message.RawContent));
        }

        [Theory]
        [InlineData("10/04/2015, 08:01 - Alex Example: a\n2015/04/10, 08:02 - Sam Sample: b")]
        [InlineData("10/04/2015, 08:01 - Alex Example: a\n08:02, 10 Apr 2015 - Sam Sample: b")]
        [InlineData("08:01, 10 Apr 2015 - Alex Example: a\n10/04/2015, 08:02 - Sam Sample: b")]
        [InlineData("2015/04/10, 08:01 - Alex Example: a\n10/04/2015, 08:02 - Sam Sample: b")]
        public void MixedShapes_StopWithTheLineNumber(string text)
        {
            var exception = Assert.Throws<FormatException>(() => ParseExport(text));

            Assert.Contains("Line 2", exception.Message, StringComparison.Ordinal);
            Assert.Contains("layout", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Sam", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void MixedShapeOnAContinuationPosition_StillStops()
        {
            // Even deep inside a multi-line message, a header of another layout is not absorbed.
            var exception = Assert.Throws<FormatException>(
                () => ParseExport(
                    "08:01, 10 Apr 2015 - Alex Example: a\nline two\n\n10/04/2015, 08:02 - Sam Sample: b"));

            Assert.Contains("Line 4", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ContentBeforeTheFirstHeader_NamesAllThreeFormats()
        {
            var exception = Assert.Throws<FormatException>(
                () => ParseExport("stray zorp\n10/04/2015, 08:01 - Alex Example: a"));

            Assert.Contains("Line 1", exception.Message, StringComparison.Ordinal);
            Assert.Contains("dd/MM/yyyy", exception.Message, StringComparison.Ordinal);
            Assert.Contains("d MMM yyyy", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("zorp", exception.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("08:01, 10 Apr 2015 - ")]
        [InlineData("10/04/2015, 08:01 - ")]
        public void MultiLineAndSystemLines_KeepTodaysRules(string prefix)
        {
            var messages = ParseExport(
                $"{prefix}Messages to this chat are now secured.\n" +
                $"{prefix}Alex Example: first line\nsecond line\n\n   \n" +
                $"{prefix}Sam Sample: Alex:colon kept: here").Messages;

            Assert.Equal(3, messages.Count);
            Assert.Equal(MessageType.System, messages[0].MessageType);
            Assert.Null(messages[0].Sender);
            Assert.Equal("Messages to this chat are now secured.", messages[0].MessageContent);
            Assert.Equal("first line\nsecond line\n\n   ", messages[1].MessageContent);
            Assert.Equal(2, messages[1].SourceLineStart);
            Assert.Equal(5, messages[1].SourceLineEnd);
            Assert.Equal("Alex:colon kept: here", messages[2].MessageContent);
        }

        [Theory]
        [InlineData("08:01, 10 Apr 2015 - ")]
        [InlineData("10/04/2015, 08:01 - ")]
        public void DirectionMarks_AreHandledAsInShapeC(string prefix)
        {
            var messages = ParseExport(
                $"{prefix}Alex Example: a\n" +
                $"{LeftToRightMark}{prefix}Sam Sample: {LeftToRightMark}<Media omitted>").Messages;

            Assert.Equal(2, messages.Count);
            Assert.Equal("Sam Sample", messages[1].Sender);
            Assert.True(messages[1].IsMediaPlaceholder);
            Assert.StartsWith(LeftToRightMark, messages[1].RawContent, StringComparison.Ordinal);
        }

        [Fact]
        public void FileAttached_WithoutCaption_SetsTheName()
        {
            var message = ParseExport(
                "10/04/2015, 08:01 - Alex Example: IMG-20150410-WA0001.jpg (file attached)").Messages[0];

            Assert.Equal("IMG-20150410-WA0001.jpg", message.AttachedFileName);
            Assert.False(message.IsMediaPlaceholder);
            Assert.Equal("IMG-20150410-WA0001.jpg (file attached)", message.MessageContent);
        }

        [Fact]
        public void FileAttached_WithCaption_KeepsTheCaptionAsContent()
        {
            var message = ParseExport(
                "10/04/2015, 08:01 - Alex Example: snap one.jpg (file attached)\nlook at the florp").Messages[0];

            Assert.Equal("snap one.jpg", message.AttachedFileName);
            Assert.Equal("snap one.jpg (file attached)\nlook at the florp", message.MessageContent);
            Assert.Equal(
                "10/04/2015, 08:01 - Alex Example: snap one.jpg (file attached)\nlook at the florp",
                message.RawContent);
        }

        [Fact]
        public void FileAttached_WithDirectionMarks_IsRecognised()
        {
            var message = ParseExport(
                $"10/04/2015, 08:01 - Alex Example: {LeftToRightMark}doc.pdf (file attached)").Messages[0];

            Assert.Equal("doc.pdf", message.AttachedFileName);
        }

        [Theory]
        [InlineData(" (file attached)")]
        [InlineData("    (file attached)")]
        [InlineData("folder/pic.jpg (file attached)")]
        [InlineData("folder\\pic.jpg (file attached)")]
        [InlineData("pic.jpg (file attached) ")]
        [InlineData("pic.jpg (File Attached)")]
        [InlineData("pic.jpg(file attached)")]
        [InlineData("pic.jpg (file attached)x")]
        [InlineData("caption first\npic.jpg (file attached)")]
        public void FileAttached_MalformedText_IsNotAMediaLine(string content)
        {
            var message = ParseExport($"10/04/2015, 08:01 - Alex Example: {content}").Messages[0];

            Assert.Null(message.AttachedFileName);
            Assert.Equal(content, message.MessageContent);
        }

        [Fact]
        public void FileAttached_OnASystemMessage_IsNotAMediaLine()
        {
            var message = ParseExport("10/04/2015, 08:01 - pic.jpg (file attached)").Messages[0];

            Assert.Equal(MessageType.System, message.MessageType);
            Assert.Null(message.AttachedFileName);
        }

        [Fact]
        public void MediaOmitted_HasNoAttachedFileName()
        {
            var message = ParseExport("10/04/2015, 08:01 - Alex Example: <Media omitted>").Messages[0];

            Assert.True(message.IsMediaPlaceholder);
            Assert.Null(message.AttachedFileName);
        }

        [Theory]
        [InlineData("10:00, 3 Aug 2014 - Alex Example: x", "10:00, 3 Sept 2014 - Sam Sample: y")]
        [InlineData("10:00, 3 Aug 2014 - Alex Example: x", "9:00, 4 Aug 2014 - Sam Sample: y")]
        [InlineData("10:00, 3 Aug 2014 - Alex Example: x", "10:0, 4 Aug 2014 - Sam Sample: y")]
        [InlineData("10:00, 3 Aug 2014 - Alex Example: x", "10:00, 4 August 2014 - Sam Sample: y")]
        [InlineData("10/04/2015, 08:01 - Alex Example: x", "1/05/2015, 08:02 - Sam Sample: y")]
        [InlineData("10/04/2015, 08:01 - Alex Example: x", "10/5/2015, 08:02 - Sam Sample: y")]
        [InlineData("10/04/2015, 08:01 - Alex Example: x", "10/05/2015, 8:02 - Sam Sample: y")]
        [InlineData("2026/01/05, 14:03 - Alex: x", "2026/1/05, 14:04 - Sam: y")]
        [InlineData("2026/01/05, 14:03 - Alex: x", "2026/01/5, 14:04 - Sam: y")]
        [InlineData("2026/01/05, 14:03 - Alex: x", "2026/01/05, 9:04 - Sam: y")]
        public void NearMissHeader_StopsWithTheLineNumberInsteadOfBeingAbsorbed(string first, string second)
        {
            var exception = Assert.Throws<FormatException>(() => ParseExport($"{first}\n{second}"));

            Assert.Contains("Line 2", exception.Message, StringComparison.Ordinal);
            Assert.Contains("timestamp", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Sam", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void YearRule_YearlessRunAfterDatedHeaders_TakesTheNextYear()
        {
            var export = ParseExport(
                """
                09:00, 3 Jun 2013 - Alex Example: early
                07:18, 31 Dec 2014 - Alex Example: x
                more of x
                20:29, 1 Jan - Sam Sample: y
                20:29, 1 Jan - Messages to this chat are now secured.
                08:05, 22 Mar - Alex Example: z
                """);

            Assert.Equal(WhatsAppHeaderShape.TimeFirst, export.Shape);
            Assert.Equal(5, export.Messages.Count);
            Assert.Equal(3, export.YearSuppliedByRuleCount);
            Assert.Equal(new DateTime(2015, 1, 1, 20, 29, 0), export.Messages[2].MessageDateTime);
            Assert.Equal(new DateTime(2015, 1, 1, 20, 29, 0), export.Messages[3].MessageDateTime);
            Assert.Equal(MessageType.System, export.Messages[3].MessageType);
            Assert.Equal(new DateTime(2015, 3, 22, 8, 5, 0), export.Messages[4].MessageDateTime);
            Assert.Equal("Sam Sample", export.Messages[2].Sender);
            Assert.Equal("y", export.Messages[2].MessageContent);
            Assert.Equal("20:29, 1 Jan - Sam Sample: y", export.Messages[2].RawContent);
            Assert.Equal("x\nmore of x", export.Messages[1].MessageContent);
        }

        [Fact]
        public void YearRule_UsesTheLastDatedHeadersYear_NotTheLatest()
        {
            // Source order decides "last", even if an earlier header carried a later year.
            var export = ParseExport(
                "09:00, 3 Jun 2015 - Alex Example: a\n09:00, 3 Jun 2013 - Alex Example: b\n10:00, 4 Jun - Sam Sample: c");

            Assert.Equal(new DateTime(2014, 6, 4, 10, 0, 0), export.Messages[2].MessageDateTime);
        }

        [Fact]
        public void YearRule_DatedFilesReportZeroSupplied()
        {
            Assert.Equal(0, ParseExport("09:00, 3 Jun 2013 - Alex Example: a").YearSuppliedByRuleCount);
            Assert.Equal(0, ParseExport("10/04/2015, 08:01 - Alex Example: a").YearSuppliedByRuleCount);
        }

        [Fact]
        public void YearRule_DatedHeaderAfterAYearlessOne_Stops()
        {
            var exception = Assert.Throws<FormatException>(
                () => ParseExport(
                    "07:18, 31 Dec 2014 - Alex Example: x\n20:29, 1 Jan - Sam Sample: y\n20:30, 1 Jan 2015 - Sam Sample: z"));

            Assert.Contains("Line 3", exception.Message, StringComparison.Ordinal);
            Assert.Contains("dated header follows", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Sam", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void YearRule_BackwardsStep_Stops()
        {
            var exception = Assert.Throws<FormatException>(
                () => ParseExport(
                    "07:18, 31 Dec 2014 - Alex Example: x\n20:29, 3 Feb - Sam Sample: y\n20:29, 2 Feb - Sam Sample: z"));

            Assert.Contains("Line 3", exception.Message, StringComparison.Ordinal);
            Assert.Contains("earlier than the one before it", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void YearRule_EqualTimestamps_AreNotABackwardsStep()
        {
            var export = ParseExport(
                "07:18, 31 Dec 2014 - Alex Example: x\n20:29, 3 Feb - Sam Sample: y\n20:29, 3 Feb - Sam Sample: z");

            Assert.Equal(2, export.YearSuppliedByRuleCount);
        }

        [Fact]
        public void YearRule_DateMissingFromTheSuppliedYear_Stops()
        {
            // 2014 + 1 = 2015, which has no 29 February.
            var exception = Assert.Throws<FormatException>(
                () => ParseExport("07:18, 31 Dec 2014 - Alex Example: x\n20:29, 29 Feb - Sam Sample: y"));

            Assert.Contains("Line 2", exception.Message, StringComparison.Ordinal);
            Assert.Contains("supplied year", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void YearRule_LeapDayInALeapSuppliedYear_IsAccepted()
        {
            var export = ParseExport("07:18, 31 Dec 2015 - Alex Example: x\n20:29, 29 Feb - Sam Sample: y");

            Assert.Equal(new DateTime(2016, 2, 29, 20, 29, 0), export.Messages[1].MessageDateTime);
        }

        [Theory]
        [InlineData("9:05, 1 Jan - Sam Sample: y")]
        [InlineData("20:5, 1 Jan - Sam Sample: y")]
        [InlineData("20:29, 22 March - Sam Sample: y")]
        [InlineData("20:29, 22 mar - Sam Sample: y")]
        [InlineData("20:29, 32 Mar - Sam Sample: y")]
        [InlineData("20:29, 31 Apr - Sam Sample: y")]
        public void YearRule_NearMissYearlessHeaders_StillStop(string second)
        {
            var exception = Assert.Throws<FormatException>(
                () => ParseExport($"07:18, 31 Dec 2014 - Alex Example: x\nmore of x\n{second}"));

            Assert.Contains("Line 3", exception.Message, StringComparison.Ordinal);
            Assert.Contains("not a valid", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Sam", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void YearlessShapeA_AsTheFirstLine_StopsAtLineOne()
        {
            var exception = Assert.Throws<FormatException>(
                () => ParseExport("20:29, 22 Mar - Sam Sample: y"));

            Assert.Contains("Line 1", exception.Message, StringComparison.Ordinal);
            Assert.Contains("no year", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void YearlessShapeA_InAShapeBFile_StopsAsAMixedLayout()
        {
            var exception = Assert.Throws<FormatException>(
                () => ParseExport("10/04/2015, 08:01 - Alex Example: a\n20:29, 22 Mar - Sam Sample: y"));

            Assert.Contains("Line 2", exception.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("20:29, 22 Mar works for me")]
        [InlineData("20:29, 22 Mar-ish - fine")]
        [InlineData("20:29, 22 Mar 15 - short year")]
        public void TextThatOnlyResemblesAYearlessHeader_StaysContinuation(string line)
        {
            var messages = ParseExport($"07:18, 31 Dec 2014 - Alex Example: x\n{line}").Messages;

            Assert.Equal($"x\n{line}", Assert.Single(messages).MessageContent);
        }

        [Theory]
        [InlineData("9:00, 4 Aug 2014 - Sam Sample: y")]
        [InlineData("1/05/2015, 08:02 - Sam Sample: y")]
        [InlineData("2026/1/05, 14:04 - Sam: y")]
        public void NearMissHeader_AsTheFirstLine_StopsAtLineOne(string line)
        {
            var exception = Assert.Throws<FormatException>(() => ParseExport(line));

            Assert.Contains("Line 1", exception.Message, StringComparison.Ordinal);
            Assert.Contains("timestamp", exception.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("08:01, 10 Apr 2015 - ")]
        [InlineData("10/04/2015, 08:01 - ")]
        [InlineData("2015/04/10, 08:01 - ")]
        public void ContinuationLinesThatMerelyStartWithATimeOrDate_StayContinuation(string prefix)
        {
            var messages = ParseExport(
                $"{prefix}Alex Example: when?\n" +
                "10:00 works for me\n" +
                "10:00, maybe 11 - or later\n" +
                "10/04 - the day after\n" +
                "3 Aug 2014 - a date on its own").Messages;

            var message = Assert.Single(messages);
            Assert.Equal(
                "when?\n10:00 works for me\n10:00, maybe 11 - or later\n10/04 - the day after\n3 Aug 2014 - a date on its own",
                message.MessageContent);
        }

        private static ParsedChatExport ParseExport(string text)
        {
            using var reader = new StringReader(text);

            return new WhatsAppAndroidChatParser().ParseExport(reader);
        }
    }
}
