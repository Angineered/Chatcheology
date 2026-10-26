using Chatcheology.Core.Importing;
using Chatcheology.Core.Models;
using Chatcheology.Data.OlderExports;

namespace Chatcheology.Data.Tests.OlderExports
{
    public class OlderExportOverlapTests
    {
        private const string Outer =
            """
            26/03/2016, 10:00 - Messages to this chat are now secured.
            26/03/2016, 10:01 - Alex Example: blorp
            second line
            27/03/2016, 11:00 - Sam Sample: zorp
            27/03/2016, 11:00 - Sam Sample: zorp
            06/09/2017, 12:59 - Sam Sample: last
            """;

        [Fact]
        public void InnerWhollyInsideOuter_CountsZero()
        {
            var inner = Parse(
                """
                26/03/2016, 10:01 - Alex Example: blorp
                second line
                27/03/2016, 11:00 - Sam Sample: zorp
                """);

            Assert.Equal(0, OlderExportOverlap.CountNotFoundIn(inner, Parse(Outer)));
        }

        [Fact]
        public void EachDifferingField_CountsAsNotFound()
        {
            var inner = Parse(
                """
                26/03/2016, 10:02 - Alex Example: blorp
                second line
                27/03/2016, 11:00 - Alex Example: zorp
                27/03/2016, 11:00 - Sam Sample: Zorp
                26/03/2016, 10:01 - Alex Example: blorp
                26/03/2016, 10:00 - Sam Sample: Messages to this chat are now secured.
                """);

            // Time, sender, content (case), a missing continuation line, and type each differ.
            Assert.Equal(5, OlderExportOverlap.CountNotFoundIn(inner, Parse(Outer)));
        }

        [Fact]
        public void RepeatedLines_AreComparedAsAMultiset()
        {
            var inner = Parse(
                """
                27/03/2016, 11:00 - Sam Sample: zorp
                27/03/2016, 11:00 - Sam Sample: zorp
                27/03/2016, 11:00 - Sam Sample: zorp
                """);

            Assert.Equal(1, OlderExportOverlap.CountNotFoundIn(inner, Parse(Outer)));
        }

        [Fact]
        public void LayoutDoesNotMatter_OnlyTheParsedFields()
        {
            var inner = Parse("11:00, 27 Mar 2016 - Sam Sample: zorp");

            Assert.Equal(0, OlderExportOverlap.CountNotFoundIn(inner, Parse(Outer)));
        }

        private static IReadOnlyList<ParsedMessage> Parse(string text)
        {
            using var reader = new StringReader(text);

            return new WhatsAppAndroidChatParser().Parse(reader);
        }
    }
}
