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

        /// <summary>
        /// LEFT-TO-RIGHT MARK, U+200E. Real exports place these around media placeholders and
        /// system notices. Written as an escape sequence because the character is invisible.
        /// </summary>
        private const string LeftToRightMark = "\u200E";

        /// <summary>RIGHT-TO-LEFT MARK, U+200F.</summary>
        private const string RightToLeftMark = "\u200F";

        /// <summary>
        /// ZERO WIDTH SPACE, U+200B. Invisible, but not one of the two supported direction marks,
        /// so the parser must leave it alone.
        /// </summary>
        private const string ZeroWidthSpace = "\u200B";

        /// <summary>NO-BREAK SPACE, U+00A0. Also invisible-ish, also not a direction mark.</summary>
        private const string NoBreakSpace = "\u00A0";

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
        public void Fixture_EveryMessageIsTypedAsAUserMessageWithASender()
        {
            var messages = ParseFixture();

            Assert.All(messages, message => Assert.Equal(MessageType.User, message.MessageType));
            Assert.All(messages, message => Assert.NotNull(message.Sender));
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
        public void HeaderShapedLineWithAnEmptySender_Throws()
        {
            // The ": " delimiter is present with nothing before it. That is structurally broken
            // rather than a system message, so it still fails instead of being absorbed.
            var exception = Assert.Throws<FormatException>(
                () => ParseText("2026/01/05, 14:03 - : orphaned"));

            Assert.Contains("sender is empty", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("orphaned", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void HeaderShapedLineWithoutTheSpaceAfterTheSenderColon_IsASystemMessage()
        {
            // The delimiter is exactly ": ", so "Alex:first" carries no sender delimiter at all.
            // It is indistinguishable from system prose containing a colon, and no heuristic tries
            // to tell the two apart.
            var messages = ParseText("2026/01/05, 14:03 - Alex:first");

            Assert.Single(messages);
            Assert.Equal(MessageType.System, messages[0].MessageType);
            Assert.Null(messages[0].Sender);
            Assert.Equal("Alex:first", messages[0].MessageContent);
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

        [Fact]
        public void SystemMessage_ParsesAsASingleLogicalMessage()
        {
            var messages = ParseText(
                "2026/01/05, 14:03 - Messages and calls are end-to-end encrypted.");

            Assert.Single(messages);
            Assert.Equal(1, messages[0].SequenceNumber);
            Assert.Equal(new DateTime(2026, 1, 5, 14, 3, 0), messages[0].MessageDateTime);
            Assert.Equal(DateTimeKind.Unspecified, messages[0].MessageDateTime.Kind);
        }

        [Fact]
        public void SystemMessage_IsTypedAsSystemAndHasNoSender()
        {
            var messages = ParseText(
                "2026/01/05, 14:03 - Messages and calls are end-to-end encrypted.");

            Assert.Equal(MessageType.System, messages[0].MessageType);
            Assert.Null(messages[0].Sender);
        }

        [Fact]
        public void SystemMessage_ContentIsEveryCharacterAfterTheTimestampPrefix()
        {
            var messages = ParseText(
                "2026/01/05, 14:03 - Messages and calls are end-to-end encrypted. Tap to learn more.");

            Assert.Equal(
                "Messages and calls are end-to-end encrypted. Tap to learn more.",
                messages[0].MessageContent);
        }

        [Fact]
        public void SystemMessage_RawContentKeepsTheWholeSourceBlock()
        {
            const string line = "2026/01/05, 14:03 - Messages and calls are end-to-end encrypted.";

            var messages = ParseText(line);

            Assert.Equal(line, messages[0].RawContent);
        }

        [Fact]
        public void SystemMessage_TracksItsPhysicalSourceLines()
        {
            var messages = ParseText(
                "2026/01/05, 14:03 - Alex: first\n" +
                "2026/01/05, 14:04 - Alex changed the group description\n" +
                "2026/01/05, 14:05 - Sam: third");

            Assert.Equal((2, 2), (messages[1].SourceLineStart, messages[1].SourceLineEnd));
        }

        [Fact]
        public void SystemMessages_ShareTheSequenceNumberRunWithUserMessages()
        {
            var messages = ParseText(
                "2026/01/05, 14:03 - Messages and calls are end-to-end encrypted.\n" +
                "2026/01/05, 14:04 - Alex: Hi Sam\n" +
                "2026/01/05, 14:05 - Alex changed the group description\n" +
                "2026/01/05, 14:06 - Sam: Hi Alex");

            Assert.Equal(
                new[] { 1, 2, 3, 4 },
                messages.Select(message => message.SequenceNumber).ToArray());

            Assert.Equal(
                new[] { MessageType.System, MessageType.User, MessageType.System, MessageType.User },
                messages.Select(message => message.MessageType).ToArray());
        }

        [Fact]
        public void UserAndSystemMessagesSharingATimestamp_KeepSourceOrder()
        {
            // Every line carries the same minute, so only source order can distinguish them.
            var messages = ParseText(
                "2026/01/05, 14:03 - Messages and calls are end-to-end encrypted.\n" +
                "2026/01/05, 14:03 - Alex: Hi Sam\n" +
                "2026/01/05, 14:03 - Alex changed the group description\n" +
                "2026/01/05, 14:03 - Sam: Hi Alex");

            Assert.Equal(4, messages.Count);
            Assert.All(
                messages,
                message => Assert.Equal(new DateTime(2026, 1, 5, 14, 3, 0), message.MessageDateTime));

            Assert.Equal(new string?[] { null, "Alex", null, "Sam" },
                messages.Select(message => message.Sender).ToArray());
        }

        [Fact]
        public void ContinuationLineAfterASystemMessage_StaysAttachedToIt()
        {
            var messages = ParseText(
                "2026/01/05, 14:03 - Messages and calls are end-to-end\n" +
                "encrypted. Tap to learn more.\n" +
                "2026/01/05, 14:04 - Alex: Hi Sam");

            Assert.Equal(2, messages.Count);
            Assert.Equal(MessageType.System, messages[0].MessageType);
            Assert.Equal(
                "Messages and calls are end-to-end\nencrypted. Tap to learn more.",
                messages[0].MessageContent);
            Assert.Equal((1, 2), (messages[0].SourceLineStart, messages[0].SourceLineEnd));
            Assert.Equal(3, messages[1].SourceLineStart);
        }

        [Fact]
        public void SystemMessageWithNoTextAfterTheTimestampPrefix_IsAllowed()
        {
            // Structurally complete: a valid timestamp prefix and nothing after it. An empty user
            // message body is already allowed, so an empty system message is not treated as broken.
            var messages = ParseText("2026/01/05, 14:03 - ");

            Assert.Single(messages);
            Assert.Equal(MessageType.System, messages[0].MessageType);
            Assert.Equal(string.Empty, messages[0].MessageContent);
        }

        [Fact]
        public void SystemProseContainingTheSenderDelimiter_IsReadAsAUserMessage()
        {
            // A known and accepted ambiguity. Structurally this is a sender followed by ": ", and
            // no heuristic guesses otherwise, so it is attributed as a user message.
            var messages = ParseText("2026/01/05, 14:03 - Group name changed to: Weekend plans");

            Assert.Equal(MessageType.User, messages[0].MessageType);
            Assert.Equal("Group name changed to", messages[0].Sender);
            Assert.Equal("Weekend plans", messages[0].MessageContent);
        }

        [Fact]
        public void LeftToRightMarkBeforeAMediaPlaceholder_IsStillRecognised()
        {
            var messages = ParseText($"2026/01/05, 14:05 - Sam: {LeftToRightMark}<Media omitted>");

            Assert.Equal(ParsedMessage.MediaPlaceholderContent, messages[0].MessageContent);
            Assert.True(messages[0].IsMediaPlaceholder);
        }

        [Fact]
        public void LeftToRightMarkAfterAMediaPlaceholder_IsStillRecognised()
        {
            var messages = ParseText($"2026/01/05, 14:05 - Sam: <Media omitted>{LeftToRightMark}");

            Assert.True(messages[0].IsMediaPlaceholder);
        }

        [Fact]
        public void RightToLeftMarkAroundAMediaPlaceholder_IsStillRecognised()
        {
            var messages = ParseText(
                $"2026/01/05, 14:05 - Sam: {RightToLeftMark}<Media omitted>{RightToLeftMark}");

            Assert.True(messages[0].IsMediaPlaceholder);
        }

        [Fact]
        public void DirectionMarks_AreRemovedFromMessageContent()
        {
            var messages = ParseText(
                $"2026/01/05, 14:03 - Alex: {LeftToRightMark}mixed {RightToLeftMark}direction text");

            Assert.Equal("mixed direction text", messages[0].MessageContent);
        }

        [Fact]
        public void DirectionMarks_AreRemovedFromTheSender()
        {
            var messages = ParseText($"2026/01/05, 14:03 - {LeftToRightMark}Alex: hello");

            Assert.Equal("Alex", messages[0].Sender);
        }

        [Fact]
        public void DirectionMarks_RemainInRawContent()
        {
            var headerLine = $"2026/01/05, 14:05 - {LeftToRightMark}Sam: {LeftToRightMark}<Media omitted>";
            var continuationLine = $"{RightToLeftMark}a caption";

            var messages = ParseText($"{headerLine}\n{continuationLine}");

            Assert.Equal($"{headerLine}\n{continuationLine}", messages[0].RawContent);
            Assert.Contains(LeftToRightMark, messages[0].RawContent, StringComparison.Ordinal);
            Assert.Contains(RightToLeftMark, messages[0].RawContent, StringComparison.Ordinal);

            // The same message's normalised content carries neither mark.
            Assert.Equal("<Media omitted>\na caption", messages[0].MessageContent);
        }

        [Fact]
        public void DirectionMarkBeforeTheTimestamp_StillStartsANewMessage()
        {
            var messages = ParseText(
                "2026/01/05, 14:03 - Alex: first\n" +
                $"{LeftToRightMark}2026/01/05, 14:04 - Sam: second");

            Assert.Equal(2, messages.Count);
            Assert.Equal("Sam", messages[1].Sender);
            Assert.Equal("second", messages[1].MessageContent);
            Assert.Equal(2, messages[1].SourceLineStart);
            Assert.StartsWith(LeftToRightMark, messages[1].RawContent, StringComparison.Ordinal);
        }

        [Fact]
        public void DirectionMarkOnlyLineBeforeTheFirstHeader_IsSkipped()
        {
            // Nothing structural remains once the mark is ignored, so there is no orphaned content.
            var messages = ParseText($"{LeftToRightMark}\n2026/01/05, 14:03 - Alex: first");

            Assert.Single(messages);
            Assert.Equal(2, messages[0].SourceLineStart);
        }

        [Fact]
        public void OtherInvisibleCharacters_AreNotStripped()
        {
            var messages = ParseText(
                $"2026/01/05, 14:03 - Alex: a{ZeroWidthSpace}b{NoBreakSpace}c");

            Assert.Equal($"a{ZeroWidthSpace}b{NoBreakSpace}c", messages[0].MessageContent);
        }

        [Fact]
        public void MediaPlaceholderWithAnUnsupportedInvisibleCharacter_IsNotAPlaceholder()
        {
            // Only U+200E and U+200F are removed, so a zero width space still breaks the exact
            // match. This pins the narrowness of the normalisation.
            var messages = ParseText($"2026/01/05, 14:05 - Sam: {ZeroWidthSpace}<Media omitted>");

            Assert.False(messages[0].IsMediaPlaceholder);
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
