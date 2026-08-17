using System.Globalization;
using Chatcheology.Core.Importing;
using Chatcheology.Core.Models;

namespace Chatcheology.Core.Tests.Importing
{
    /// <summary>
    /// Tests for the pinned WhatsApp Android export layout
    /// <c>yyyy/MM/dd, HH:mm - Sender: Message</c>.
    /// </summary>
    /// <remarks>
    /// Emoji are written as escape sequences rather than literal characters so the expected values
    /// cannot be altered by the encoding this source file happens to be stored or checked out with.
    /// </remarks>
    public class WhatsAppAndroidChatParserTests
    {
        private const string FixtureFileName = "SampleChatAndroid.txt";

        /// <summary>WAVING HAND SIGN, U+1F44B, as it appears on fixture line 1.</summary>
        private const string WavingHand = "\U0001F44B";

        /// <summary>SLIGHTLY SMILING FACE, U+1F642, as it appears on fixture line 6.</summary>
        private const string SlightlySmilingFace = "\U0001F642";

        [Fact]
        public void Fixture_ProducesFiveLogicalMessagesFromSixPhysicalLines()
        {
            var messages = ParseFixture();

            Assert.Equal(5, messages.Count);
        }

        [Fact]
        public void Fixture_SequenceNumbersAreOneThroughFiveInSourceOrder()
        {
            var messages = ParseFixture();

            Assert.Equal(
                new[] { 1, 2, 3, 4, 5 },
                messages.Select(message => message.SequenceNumber).ToArray());
        }

        [Fact]
        public void Fixture_MessagesSharingATimestampRemainInSourceOrder()
        {
            var messages = ParseFixture();

            var expectedTimestamp = new DateTime(2026, 1, 5, 14, 3, 0);

            Assert.Equal(expectedTimestamp, messages[0].MessageDateTime);
            Assert.Equal(expectedTimestamp, messages[1].MessageDateTime);

            // Equal timestamps, so only source order can distinguish them.
            Assert.Equal("Alex", messages[0].Sender);
            Assert.Equal("Sam", messages[1].Sender);
            Assert.Equal("Hi Alex", messages[1].MessageContent);
        }

        [Fact]
        public void Fixture_TimestampsAreParsedWithoutTimezoneInformation()
        {
            var messages = ParseFixture();

            Assert.All(
                messages,
                message => Assert.Equal(DateTimeKind.Unspecified, message.MessageDateTime.Kind));
        }

        [Fact]
        public void Fixture_MultilineEntryBecomesASingleLogicalMessage()
        {
            var messages = ParseFixture();

            var multiline = messages[2];

            Assert.Equal("Alex", multiline.Sender);
            Assert.Equal("This message has\na second line.", multiline.MessageContent);
        }

        [Fact]
        public void Fixture_MultilineEntrySpansItsTwoPhysicalSourceLines()
        {
            var messages = ParseFixture();

            Assert.Equal(3, messages[2].SourceLineStart);
            Assert.Equal(4, messages[2].SourceLineEnd);
        }

        [Fact]
        public void Fixture_SingleLineMessagesStartAndEndOnTheSamePhysicalLine()
        {
            var messages = ParseFixture();

            Assert.Equal((1, 1), (messages[0].SourceLineStart, messages[0].SourceLineEnd));
            Assert.Equal((2, 2), (messages[1].SourceLineStart, messages[1].SourceLineEnd));
            Assert.Equal((5, 5), (messages[3].SourceLineStart, messages[3].SourceLineEnd));
            Assert.Equal((6, 6), (messages[4].SourceLineStart, messages[4].SourceLineEnd));
        }

        [Fact]
        public void Fixture_EmojiSurvivesParsingUnchanged()
        {
            var messages = ParseFixture();

            Assert.Equal($"Hi Sam {WavingHand}", messages[0].MessageContent);
            Assert.Equal($"See you tomorrow {SlightlySmilingFace}", messages[4].MessageContent);
        }

        [Fact]
        public void Fixture_RawContentKeepsTheHeaderLineAndEveryContinuationLine()
        {
            var messages = ParseFixture();

            Assert.Equal(
                "2026/01/05, 14:04 - Alex: This message has\na second line.",
                messages[2].RawContent);
            Assert.Equal($"2026/01/05, 14:03 - Alex: Hi Sam {WavingHand}", messages[0].RawContent);
        }

        [Fact]
        public void Fixture_MediaOmittedMessageIsRecognisedAsAPlaceholder()
        {
            var messages = ParseFixture();

            Assert.Equal(ParsedMessage.MediaPlaceholderContent, messages[3].MessageContent);
            Assert.True(messages[3].IsMediaPlaceholder);
        }

        [Fact]
        public void Fixture_NormalMessagesAreNotMediaPlaceholders()
        {
            var messages = ParseFixture();

            Assert.False(messages[0].IsMediaPlaceholder);
            Assert.False(messages[1].IsMediaPlaceholder);
            Assert.False(messages[2].IsMediaPlaceholder);
            Assert.False(messages[4].IsMediaPlaceholder);
        }

        [Fact]
        public void MediaPlaceholderWithSurroundingText_IsNotAPlaceholder()
        {
            var messages = ParseText("2026/01/05, 14:05 - Sam: see this <Media omitted> please");

            Assert.False(messages[0].IsMediaPlaceholder);
        }

        [Fact]
        public void MediaPlaceholderFollowedByAContinuationLine_IsNotAPlaceholder()
        {
            var messages = ParseText("2026/01/05, 14:05 - Sam: <Media omitted>\nand a caption");

            Assert.Equal("<Media omitted>\nand a caption", messages[0].MessageContent);
            Assert.False(messages[0].IsMediaPlaceholder);
        }

        [Fact]
        public void ColonsInsideMessageContent_DoNotBreakTheSenderOrContentSplit()
        {
            var messages = ParseText(
                "2026/01/05, 14:03 - Alex: The URL is https://example.test:8443/path");

            Assert.Equal("Alex", messages[0].Sender);
            Assert.Equal("The URL is https://example.test:8443/path", messages[0].MessageContent);
        }

        [Fact]
        public void SenderWithSpaces_IsPreservedAndBodySpacingIsNotTrimmed()
        {
            var messages = ParseText("2026/01/05, 14:03 - Alex Smith Jr:   indented body ");

            Assert.Equal("Alex Smith Jr", messages[0].Sender);
            Assert.Equal("  indented body ", messages[0].MessageContent);
        }

        [Fact]
        public void MessageContentContainingTheHeaderSeparator_IsPreserved()
        {
            var messages = ParseText("2026/01/05, 14:03 - Alex: pros - cons - verdict");

            Assert.Equal("Alex", messages[0].Sender);
            Assert.Equal("pros - cons - verdict", messages[0].MessageContent);
        }

        [Fact]
        public void EmptyMessageBody_IsAllowed()
        {
            var messages = ParseText("2026/01/05, 14:03 - Alex: ");

            Assert.Single(messages);
            Assert.Equal("Alex", messages[0].Sender);
            Assert.Equal(string.Empty, messages[0].MessageContent);
            Assert.False(messages[0].IsMediaPlaceholder);
        }

        [Fact]
        public void ContinuationLine_StaysAttachedToThePrecedingMessage()
        {
            var messages = ParseText(
                """
                2026/01/05, 14:03 - Alex: first
                2026/01/05, 14:04 - Sam: second
                continues second
                2026/01/05, 14:05 - Alex: third
                """);

            Assert.Equal(3, messages.Count);
            Assert.Equal("first", messages[0].MessageContent);
            Assert.Equal("second\ncontinues second", messages[1].MessageContent);
            Assert.Equal(2, messages[1].SourceLineStart);
            Assert.Equal(3, messages[1].SourceLineEnd);
            Assert.Equal("third", messages[2].MessageContent);
            Assert.Equal(4, messages[2].SourceLineStart);
        }

        [Fact]
        public void BlankAndWhitespaceOnlyContinuationLines_ArePreserved()
        {
            var messages = ParseText("2026/01/05, 14:03 - Alex: one\n\n   \ntwo");

            Assert.Single(messages);
            Assert.Equal("one\n\n   \ntwo", messages[0].MessageContent);
            Assert.Equal(1, messages[0].SourceLineStart);
            Assert.Equal(4, messages[0].SourceLineEnd);
        }

        [Fact]
        public void CrlfAndLfInput_ProduceIdenticalResults()
        {
            string[] lines =
            [
                "2026/01/05, 14:03 - Alex: first",
                "2026/01/05, 14:04 - Sam: second",
                "continues second",
            ];

            var fromLf = ParseText(string.Join("\n", lines));
            var fromCrlf = ParseText(string.Join("\r\n", lines));

            Assert.Equal(fromLf.Count, fromCrlf.Count);

            for (var index = 0; index < fromLf.Count; index++)
            {
                Assert.Equal(fromLf[index].MessageContent, fromCrlf[index].MessageContent);
                Assert.Equal(fromLf[index].RawContent, fromCrlf[index].RawContent);
                Assert.Equal(fromLf[index].SourceLineStart, fromCrlf[index].SourceLineStart);
                Assert.Equal(fromLf[index].SourceLineEnd, fromCrlf[index].SourceLineEnd);
            }

            Assert.Equal("second\ncontinues second", fromCrlf[1].MessageContent);
        }

        [Fact]
        public void EmptyInput_ReturnsNoMessages()
        {
            Assert.Empty(ParseText(string.Empty));
        }

        [Fact]
        public void WhitespaceOnlyInput_ReturnsNoMessages()
        {
            Assert.Empty(ParseText("\n   \n\t\n"));
        }

        [Fact]
        public void WhitespaceOnlyLinesBeforeTheFirstHeader_AreSkipped()
        {
            var messages = ParseText("\n   \n2026/01/05, 14:03 - Alex: first");

            Assert.Single(messages);
            Assert.Equal(1, messages[0].SequenceNumber);
            Assert.Equal(3, messages[0].SourceLineStart);
            Assert.Equal("first", messages[0].MessageContent);
        }

        [Fact]
        public void NullReader_ThrowsArgumentNullException()
        {
            var parser = new WhatsAppAndroidChatParser();

            Assert.Throws<ArgumentNullException>(() => parser.Parse(null!));
        }

        [Fact]
        public void ContentBeforeTheFirstValidMessage_ThrowsWithoutRevealingContent()
        {
            var exception = Assert.Throws<FormatException>(
                () => ParseText("stray notes about Alex\n2026/01/05, 14:03 - Alex: first"));

            Assert.Contains("Line 1", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("stray", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void HeaderShapedLineWithAnImpossibleDate_ThrowsEvenAfterAValidMessage()
        {
            var exception = Assert.Throws<FormatException>(
                () => ParseText(
                    """
                    2026/01/05, 14:03 - Alex: first
                    2026/02/30, 14:04 - Sam: second
                    """));

            Assert.Contains("Line 2", exception.Message, StringComparison.Ordinal);
            Assert.Contains("timestamp", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("second", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void HeaderShapedSystemMessageWithoutASender_Throws()
        {
            var exception = Assert.Throws<FormatException>(
                () => ParseText("2026/01/05, 14:03 - Messages and calls are end-to-end encrypted."));

            Assert.Contains("Line 1", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void HeaderShapedLineWithAnEmptySender_Throws()
        {
            var exception = Assert.Throws<FormatException>(
                () => ParseText("2026/01/05, 14:03 - : orphaned"));

            Assert.Contains("sender is empty", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void HeaderShapedLineWithoutTheSpaceAfterTheSenderColon_Throws()
        {
            // The delimiter is exactly ": ", so "Alex:first" is not a supported header.
            Assert.Throws<FormatException>(
                () => ParseText("2026/01/05, 14:03 - Alex:first"));
        }

        [Fact]
        public void UnsupportedTimestampLayout_IsNotReinterpretedAsAMessage()
        {
            // A different layout does not have the supported header shape, so before any valid
            // message it fails rather than being accepted as a message of its own.
            Assert.Throws<FormatException>(
                () => ParseText("05/01/2026, 2:03 pm - Alex: first"));
        }

        [Fact]
        public void TimestampParsing_IsUnaffectedByTheCurrentCulture()
        {
            var originalCulture = CultureInfo.CurrentCulture;

            try
            {
                // de-DE uses '.' as both its date and time separator, so a culture-dependent
                // format string would fail to match '/' and ':' here.
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                var messages = ParseText("2026/01/05, 14:03 - Alex: first");

                Assert.Equal(new DateTime(2026, 1, 5, 14, 3, 0), messages[0].MessageDateTime);
                Assert.Equal(DateTimeKind.Unspecified, messages[0].MessageDateTime.Kind);
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }

        private static IReadOnlyList<ParsedMessage> ParseFixture()
        {
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "TestData", FixtureFileName);

            // The fixture is UTF-8; StreamReader also skips a byte order mark if one is present.
            using var reader = new StreamReader(fixturePath);

            return new WhatsAppAndroidChatParser().Parse(reader);
        }

        private static IReadOnlyList<ParsedMessage> ParseText(string text)
        {
            using var reader = new StringReader(text);

            return new WhatsAppAndroidChatParser().Parse(reader);
        }
    }
}
