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

        [Fact]
        public void Analyse_SortsEveryUnmatchedLineIntoOneCategory()
        {
            // Separate strings so the trailing spaces after "zorp" are explicit and survive editors.
            var outer = Parse(string.Join(
                "\n",
                "01/01/2016, 09:00 - Alex Example: before the inner period",
                "26/03/2016, 10:00 - Sam Sample: <Media omitted>",
                "26/03/2016, 10:01 - Alex Example: blorp",
                "26/03/2016, 10:02 - Sam Sample: zorp" + "  ",
                "26/03/2016, 10:03 - Alex Example: something else",
                "26/03/2016, 10:04 - Sam Sample: only in outer",
                "26/03/2016, 10:05 - Alex Example: same",
                "31/12/2016, 09:00 - Alex Example: after the inner period"));

            var inner = Parse(
                """
                26/03/2016, 10:00 - Sam Sample: snap.jpg (file attached)
                26/03/2016, 10:01 - Alex Example: blorp
                26/03/2016, 10:01 - Alex Example: blorp
                26/03/2016, 10:02 - Sam Sample: zorp
                26/03/2016, 10:03 - Alex Example: different words
                26/03/2016, 10:05 - Alex Example: same
                26/03/2016, 10:06 - Sam Sample: only in inner
                """);

            var breakdown = OlderExportOverlap.Analyse(inner, outer);

            Assert.Equal(7, breakdown.InnerCount);
            Assert.Equal(5, breakdown.NotFoundCount);
            Assert.Equal(1, breakdown.FileAttachedWhereOuterHasMediaOmittedCount);
            Assert.Equal(1, breakdown.RepeatedMoreOftenThanInOuterCount);
            Assert.Equal(1, breakdown.WhitespaceOnlyDifferenceCount);
            Assert.Equal(1, breakdown.OtherContentDifferenceCount);
            Assert.Equal(1, breakdown.NoOuterLineAtTimestampAndSenderCount);

            // Outer lines from 10:00 to 10:06: six of them; only "blorp" and "same" are in inner.
            Assert.Equal(6, breakdown.OuterInInnerPeriodCount);
            Assert.Equal(4, breakdown.OuterInInnerPeriodNotFoundInInnerCount);
        }

        [Fact]
        public void Analyse_FileAttachedWithoutAMediaOmittedCounterpart_IsAContentDifference()
        {
            var breakdown = OlderExportOverlap.Analyse(
                Parse("26/03/2016, 10:00 - Sam Sample: snap.jpg (file attached)"),
                Parse("26/03/2016, 10:00 - Sam Sample: other.jpg (file attached)"));

            Assert.Equal(0, breakdown.FileAttachedWhereOuterHasMediaOmittedCount);
            Assert.Equal(1, breakdown.OtherContentDifferenceCount);
        }

        [Fact]
        public void Analyse_WhitespaceOnInnerContinuationLines_IsWhitespaceOnly()
        {
            var breakdown = OlderExportOverlap.Analyse(
                Parse("26/03/2016, 10:00 - Sam Sample: first\n  second  \n"),
                Parse("26/03/2016, 10:00 - Sam Sample: first\nsecond"));

            Assert.Equal(1, breakdown.WhitespaceOnlyDifferenceCount);
        }

        [Fact]
        public void Analyse_EmptyInner_CountsNothing()
        {
            var breakdown = OlderExportOverlap.Analyse([], Parse(Outer));

            Assert.Equal(0, breakdown.NotFoundCount);
            Assert.Equal(0, breakdown.OuterInInnerPeriodCount);
        }

        private static IReadOnlyList<ParsedMessage> Parse(string text)
        {
            using var reader = new StringReader(text);

            return new WhatsAppAndroidChatParser().Parse(reader);
        }
    }
}
