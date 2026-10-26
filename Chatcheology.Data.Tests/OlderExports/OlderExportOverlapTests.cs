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
        public void Analyse_SplitsEachCategoryByLineKind_InBothDirections()
        {
            var inner = Parse(
                """
                26/03/2016, 10:00 - Sam Sample: a.jpg (file attached)
                26/03/2016, 10:01 - Alex Example: <Media omitted>
                26/03/2016, 10:02 - Alex Example: words
                26/03/2016, 10:03 - Messages to this chat are now secured.
                26/03/2016, 10:04 - Sam Sample: b.jpg (file attached)
                26/03/2016, 10:05 - Sam Sample: <Media omitted>
                26/03/2016, 10:09 - Sam Sample: end
                """);

            var outer = Parse(
                """
                26/03/2016, 10:00 - Sam Sample: <Media omitted>
                26/03/2016, 10:01 - Alex Example: c.jpg (file attached)
                26/03/2016, 10:02 - Alex Example: other words
                26/03/2016, 10:03 - A different notice.
                26/03/2016, 10:06 - Alex Example: outer only
                26/03/2016, 10:09 - Sam Sample: end
                """);

            var forward = OlderExportOverlap.Analyse(inner, outer).Forward;

            Assert.Equal(7, forward.ComparedCount);
            Assert.Equal(6, forward.NotFoundCount);

            // Both media mismatches, either way round.
            Assert.Equal(1, forward.Count(OverlapCategory.FileAttachedVersusMediaOmitted, OverlapLineKind.UserFileAttached));
            Assert.Equal(1, forward.Count(OverlapCategory.FileAttachedVersusMediaOmitted, OverlapLineKind.UserMediaOmitted));
            Assert.Equal(1, forward.Count(OverlapCategory.OtherContent, OverlapLineKind.UserText));
            Assert.Equal(1, forward.Count(OverlapCategory.OtherContent, OverlapLineKind.System));
            Assert.Equal(1, forward.Count(OverlapCategory.NoLineAtTimestampAndSender, OverlapLineKind.UserFileAttached));
            Assert.Equal(1, forward.Count(OverlapCategory.NoLineAtTimestampAndSender, OverlapLineKind.UserMediaOmitted));
            Assert.Equal(
                [1, 2, 3, 4, 5, 6],
                forward.Unmatched.Select(line => line.SourceLineStart));

            var reverse = OlderExportOverlap.Analyse(inner, outer).Reverse;

            // Outer within 10:00 to 10:09: all six lines; "end" is found.
            Assert.Equal(6, reverse.ComparedCount);
            Assert.Equal(5, reverse.NotFoundCount);
            Assert.Equal(1, reverse.Count(OverlapCategory.FileAttachedVersusMediaOmitted, OverlapLineKind.UserMediaOmitted));
            Assert.Equal(1, reverse.Count(OverlapCategory.FileAttachedVersusMediaOmitted, OverlapLineKind.UserFileAttached));
            Assert.Equal(1, reverse.Count(OverlapCategory.OtherContent, OverlapLineKind.UserText));
            Assert.Equal(1, reverse.Count(OverlapCategory.OtherContent, OverlapLineKind.System));
            Assert.Equal(1, reverse.Count(OverlapCategory.NoLineAtTimestampAndSender, OverlapLineKind.UserText));
        }

        [Fact]
        public void Analyse_ClockShiftHints_CountNearbyAndShiftedIdenticalLines()
        {
            var inner = Parse(
                """
                26/03/2016, 10:00 - Alex Example: one minute off
                26/03/2016, 11:00 - Alex Example: one hour off
                26/03/2016, 13:30 - Alex Example: two hours off
                26/03/2016, 17:00 - Alex Example: three hours off
                26/03/2016, 20:00 - Sam Sample: nothing near
                """);

            // No outer line shares a timestamp and sender with an inner line.
            var outer = Parse(
                """
                26/03/2016, 10:01 - Alex Example: one minute off
                26/03/2016, 12:00 - Alex Example: one hour off
                26/03/2016, 15:30 - Alex Example: two hours off
                26/03/2016, 20:00 - Alex Example: three hours off
                26/03/2016, 20:01 - Alex Example: someone else nearby
                """);

            var forward = OlderExportOverlap.Analyse(inner, outer).Forward;

            Assert.Equal(5, forward.Count(OverlapCategory.NoLineAtTimestampAndSender));

            // 10:00 has Alex at 10:01; 20:00 (Sam) has only Alex nearby, which does not count.
            Assert.Equal(1, forward.SameSenderWithinOneMinuteCount);

            // Identical content: one minute and one hour off are within 60; two hours within 120.
            Assert.Equal(2, forward.IdenticalWithinSixtyMinutesCount);
            Assert.Equal(3, forward.IdenticalWithinOneHundredTwentyMinutesCount);
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
