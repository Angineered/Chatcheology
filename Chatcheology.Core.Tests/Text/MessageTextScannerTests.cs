using Chatcheology.Core.Text;

namespace Chatcheology.Core.Tests.Text
{
    /// <summary>
    /// What the message scanner treats as followable, and what it leaves alone.
    /// </summary>
    /// <remarks>
    /// The invariant every test here rests on is that the message survives: joining the spans back
    /// together must reproduce the input exactly. A reader that quietly dropped or altered a
    /// character while looking for links would be changing what somebody wrote.
    /// </remarks>
    public class MessageTextScannerTests
    {
        // -----------------------------------------------------------------------------------
        // The three shapes that are followable.
        // -----------------------------------------------------------------------------------

        [Theory]
        [InlineData("https://example.com")]
        [InlineData("http://example.com")]
        [InlineData("https://example.com/path?query=1&other=2#fragment")]
        [InlineData("HTTPS://EXAMPLE.COM")]
        public void AWebAddress_IsFollowableAndKeepsItsOwnText(string address)
        {
            var span = Assert.Single(MessageTextScanner.Scan(address));

            Assert.Equal(MessageSpanKind.WebLink, span.Kind);
            Assert.Equal(address, span.Text);
            Assert.Equal(address, span.Target);
        }

        /// <remarks>
        /// A bare <c>www.</c> address is written without a scheme, and an operating system cannot
        /// be handed it as it stands, so it is given the one a browser would supply. The displayed
        /// text stays exactly as written.
        /// </remarks>
        [Fact]
        public void ABareWwwAddress_GainsTheSchemeItWasWrittenWithout()
        {
            var span = Assert.Single(MessageTextScanner.Scan("www.example.com"));

            Assert.Equal(MessageSpanKind.WebLink, span.Kind);
            Assert.Equal("www.example.com", span.Text);
            Assert.Equal("https://www.example.com", span.Target);
        }

        [Fact]
        public void AnEmailAddress_BecomesAMailtoTarget()
        {
            var span = Assert.Single(MessageTextScanner.Scan("someone@example.com"));

            Assert.Equal(MessageSpanKind.EmailAddress, span.Kind);
            Assert.Equal("someone@example.com", span.Text);
            Assert.Equal("mailto:someone@example.com", span.Target);
        }

        [Theory]
        [InlineData("first.last+tag@example.co.uk")]
        [InlineData("a_b-c%d@sub.example.com")]
        public void AnEmailAddressWithTheUsualPunctuation_IsStillOneAddress(string address)
        {
            var span = Assert.Single(MessageTextScanner.Scan(address));

            Assert.Equal(MessageSpanKind.EmailAddress, span.Kind);
            Assert.Equal(address, span.Text);
        }

        // -----------------------------------------------------------------------------------
        // Everything else is text.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void OrdinaryText_IsOneTextSpan()
        {
            var span = Assert.Single(MessageTextScanner.Scan("Nothing to follow here at all."));

            Assert.Equal(MessageSpanKind.Text, span.Kind);
            Assert.Equal("Nothing to follow here at all.", span.Text);
            Assert.Null(span.Target);
        }

        [Fact]
        public void AnEmptyMessage_HasNoSpans()
        {
            Assert.Empty(MessageTextScanner.Scan(string.Empty));
        }

        /// <remarks>
        /// <strong>The rule that matters most.</strong> Only three shapes are followable, and no
        /// other scheme is — including ones a shell would happily execute if handed them.
        /// </remarks>
        [Theory]
        [InlineData("ftp://example.com/file.zip")]
        [InlineData("file:///C:/somewhere/secret.txt")]
        [InlineData("javascript:alert(1)")]
        [InlineData("data:text/html;base64,AAAA")]
        [InlineData("chatcheology://open")]
        [InlineData("\\\\server\\share\\file.txt")]
        public void AnUnsupportedScheme_StaysPlainText(string text)
        {
            Assert.All(
                MessageTextScanner.Scan(text),
                span => Assert.Equal(MessageSpanKind.Text, span.Kind));
        }

        /// <remarks>
        /// Deliberately not recognised. Each would be the viewer deciding what a piece of somebody's
        /// message meant.
        /// </remarks>
        [Theory]
        [InlineData("Call me on +27 11 555 0199")]
        [InlineData("#hashtag and @mention")]
        [InlineData("**bold** and _italic_ and `code`")]
        public void ShapesThisViewerDoesNotRecognise_StayPlainText(string text)
        {
            var span = Assert.Single(MessageTextScanner.Scan(text));

            Assert.Equal(MessageSpanKind.Text, span.Kind);
            Assert.Equal(text, span.Text);
        }

        // -----------------------------------------------------------------------------------
        // Punctuation around an address.
        // -----------------------------------------------------------------------------------

        [Theory]
        [InlineData("See www.example.com.", "www.example.com", ".")]
        [InlineData("See https://example.com!", "https://example.com", "!")]
        [InlineData("See https://example.com, then go.", "https://example.com", ", then go.")]
        [InlineData("See https://example.com?", "https://example.com", "?")]
        [InlineData("Try (https://example.com)", "https://example.com", ")")]
        [InlineData("Quoted \"https://example.com\"", "https://example.com", "\"")]
        public void PunctuationAfterAnAddress_BelongsToTheSentence(
            string message, string expectedLink, string expectedTrailingText)
        {
            var spans = MessageTextScanner.Scan(message);

            var link = spans.Single(span => span.Kind == MessageSpanKind.WebLink);

            Assert.Equal(expectedLink, link.Text);
            Assert.Equal(expectedTrailingText, spans[^1].Text);
            Assert.Equal(message, Rejoined(spans));
        }

        /// <remarks>
        /// A closing bracket the address opened itself is part of the address, so a path that
        /// genuinely contains one keeps it.
        /// </remarks>
        [Fact]
        public void ABracketTheAddressOpenedItself_StaysPartOfIt()
        {
            var spans = MessageTextScanner.Scan("https://example.com/a_(b)_c and more");

            var link = spans.Single(span => span.Kind == MessageSpanKind.WebLink);

            Assert.Equal("https://example.com/a_(b)_c", link.Text);
        }

        [Fact]
        public void AnEmailAddressEndingASentence_KeepsItsOwnCharactersOnly()
        {
            var spans = MessageTextScanner.Scan("Write to someone@example.com.");

            var email = spans.Single(span => span.Kind == MessageSpanKind.EmailAddress);

            Assert.Equal("someone@example.com", email.Text);
            Assert.Equal("Write to someone@example.com.", Rejoined(spans));
        }

        /// <remarks>
        /// Trimming a sentence's punctuation can leave something that matched the shape of an
        /// address without being one. A fragment is text, not a link that cannot be followed.
        /// </remarks>
        [Theory]
        [InlineData("Look at http://.")]
        [InlineData("Look at www..")]
        public void AFragmentLeftAfterTrimming_StaysPlainText(string message)
        {
            var spans = MessageTextScanner.Scan(message);

            Assert.All(spans, span => Assert.Equal(MessageSpanKind.Text, span.Kind));
            Assert.Equal(message, Rejoined(spans));
        }

        // -----------------------------------------------------------------------------------
        // Several addresses, and several lines.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void SeveralAddressesInOneMessage_AreEachFound()
        {
            var spans = MessageTextScanner.Scan(
                "Start https://one.example.com then www.two.example.com then three@example.com end");

            Assert.Equal(
                [MessageSpanKind.WebLink, MessageSpanKind.WebLink, MessageSpanKind.EmailAddress],
                spans.Where(span => span.Kind != MessageSpanKind.Text).Select(span => span.Kind));

            Assert.Equal(
                ["https://one.example.com", "www.two.example.com", "three@example.com"],
                spans.Where(span => span.Kind != MessageSpanKind.Text).Select(span => span.Text));
        }

        /// <remarks>
        /// Message content separates its lines with the parser's own separator, deliberately not
        /// the running platform's. The scanner neither consumes nor normalises them.
        /// </remarks>
        [Fact]
        public void AMultilineMessage_KeepsItsLineBreaksAndFindsEveryAddress()
        {
            const string Message =
                "First line\nSee https://example.com\nWrite to someone@example.com\nLast line";

            var spans = MessageTextScanner.Scan(Message);

            Assert.Equal(Message, Rejoined(spans));

            Assert.Equal(
                2,
                spans.Count(span => span.Kind != MessageSpanKind.Text));

            Assert.Contains(spans, span => span.Text.Contains('\n', StringComparison.Ordinal));
        }

        [Fact]
        public void AnAddressAtTheVeryStartAndEnd_NeedsNoSurroundingText()
        {
            var spans = MessageTextScanner.Scan("https://example.com and www.other.example.com");

            Assert.Equal(MessageSpanKind.WebLink, spans[0].Kind);
            Assert.Equal(MessageSpanKind.WebLink, spans[^1].Kind);
        }

        // -----------------------------------------------------------------------------------
        // The message survives.
        // -----------------------------------------------------------------------------------

        [Theory]
        [InlineData("Plain")]
        [InlineData("https://example.com")]
        [InlineData("See https://example.com, or www.other.example.com; or mail a@b.example.com.")]
        [InlineData("Ünïcödé tekst met https://example.com/ünï en émail@example.com")]
        [InlineData("Emoji \U0001F600 then https://example.com then \U0001F602")]
        [InlineData("Line one\nLine two\n\nLine four")]
        [InlineData("   leading and trailing whitespace   ")]
        public void JoiningTheSpansBackTogether_ReproducesTheMessageExactly(string message)
        {
            Assert.Equal(message, Rejoined(MessageTextScanner.Scan(message)));
        }

        /// <remarks>
        /// The pattern decides what looks like an address; this decides what may be handed to the
        /// machine. Both have to agree before anything is followed.
        /// </remarks>
        [Theory]
        [InlineData("https://example.com", true)]
        [InlineData("http://example.com", true)]
        [InlineData("mailto:someone@example.com", true)]
        [InlineData("ftp://example.com", false)]
        [InlineData("javascript:alert(1)", false)]
        [InlineData("file:///C:/secret.txt", false)]
        [InlineData("example.com", false)]
        [InlineData("https://", false)]
        [InlineData("", false)]
        public void IsFollowable_AdmitsOnlyTheThreeSchemesTheViewerFollows(
            string target, bool expected)
        {
            Assert.Equal(expected, MessageTextScanner.IsFollowable(target));
        }

        [Fact]
        public void EveryFollowableSpan_CarriesATargetTheViewerWillAccept()
        {
            var spans = MessageTextScanner.Scan(
                "https://a.example.com www.b.example.com c@example.com plain ftp://d.example.com");

            var followable = spans.Where(span => span.Kind != MessageSpanKind.Text).ToList();

            Assert.Equal(3, followable.Count);

            Assert.All(
                followable,
                span => Assert.True(MessageTextScanner.IsFollowable(span.Target!)));
        }

        private static string Rejoined(IReadOnlyList<MessageTextSpan> spans) =>
            string.Concat(spans.Select(span => span.Text));
    }
}
