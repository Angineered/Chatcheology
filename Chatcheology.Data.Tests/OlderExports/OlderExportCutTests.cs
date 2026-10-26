using Chatcheology.Core.Importing;
using Chatcheology.Core.Models;
using Chatcheology.Data.OlderExports;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.OlderExports
{
    /// <summary>
    /// The revised D2 cut: the inner export is shown whole, the outer only after the one line that
    /// exactly matches the inner's last message.
    /// </summary>
    public class OlderExportCutTests
    {
        private static readonly DateTime Now = new(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);

        private const string Inner =
            """
            26/03/2016, 10:00 - Alex Example: a
            26/03/2016, 10:01 - Sam Sample: b.jpg (file attached)
            08/12/2016, 21:00 - Sam Sample: final zorp
            """;

        /// <summary>Same lines, a deleted photo, then two lines after the cut.</summary>
        private const string Outer =
            """
            26/03/2016, 10:00 - Alex Example: a
            26/03/2016, 10:01 - Sam Sample: <Media omitted>
            08/12/2016, 21:00 - Sam Sample: final zorp
            08/12/2016, 21:00 - Alex Example: same minute, after the cut
            06/09/2017, 12:59 - Alex Example: end
            """;

        [Fact]
        public void Find_ExactlyOnce_ShowsEverythingAfterTheMatch()
        {
            var cut = OlderExportCut.Find(Parse(Inner), Parse(Outer));

            Assert.True(cut.IsFound);
            Assert.Equal(1, cut.MatchCount);
            Assert.Equal(4, cut.ShownFromSequenceNumber);
            Assert.Equal(3, cut.HiddenCount);
            Assert.Equal(2, cut.ShownCount);
            Assert.Equal(new DateTime(2016, 12, 8, 21, 0, 0), cut.ShownFirst);
            Assert.Equal(new DateTime(2017, 9, 6, 12, 59, 0), cut.ShownLast);
            Assert.Equal([4, 5], cut.ShownMessages(Parse(Outer)).Select(message => message.SequenceNumber));
        }

        [Theory]
        [InlineData("08/12/2016, 21:00 - Sam Sample: final zorp.")]
        [InlineData("08/12/2016, 21:01 - Sam Sample: final zorp")]
        [InlineData("08/12/2016, 21:00 - Alex Example: last line")]
        [InlineData("08/12/2016, 21:00 - Sam Sample: final zorp\nmore")]
        public void Find_NotFoundExactly_HasNoCut(string changedLine)
        {
            var outer = Outer.Replace("08/12/2016, 21:00 - Sam Sample: final zorp", changedLine, StringComparison.Ordinal);

            var cut = OlderExportCut.Find(Parse(Inner), Parse(outer));

            Assert.False(cut.IsFound);
            Assert.Equal(0, cut.MatchCount);
            Assert.Null(cut.ShownFromSequenceNumber);
            Assert.Empty(cut.ShownMessages(Parse(outer)));
        }

        [Fact]
        public void Find_Twice_HasNoCut()
        {
            var outer = Outer + "\n08/12/2016, 21:00 - Sam Sample: final zorp";

            var cut = OlderExportCut.Find(Parse(Inner), Parse(outer));

            Assert.Equal(2, cut.MatchCount);
            Assert.Null(cut.ShownFromSequenceNumber);
        }

        [Fact]
        public void Find_AtTheOutersLastLine_ShowsNothing()
        {
            var outer =
                """
                26/03/2016, 10:00 - Alex Example: a
                08/12/2016, 21:00 - Sam Sample: final zorp
                """;

            var cut = OlderExportCut.Find(Parse(Inner), Parse(outer));

            Assert.True(cut.IsFound);
            Assert.Equal(3, cut.ShownFromSequenceNumber);
            Assert.Equal(2, cut.HiddenCount);
            Assert.Equal(0, cut.ShownCount);
            Assert.Null(cut.ShownFirst);
            Assert.Null(cut.ShownLast);
        }

        [Fact]
        public void Find_UsesTheInnersLastLineInSourceOrder()
        {
            // The inner's last line is earlier in time than its first; source order decides.
            var inner = "26/03/2016, 10:00 - Alex Example: a\n25/03/2016, 09:00 - Alex Example: back";
            var outer = "25/03/2016, 09:00 - Alex Example: back\n26/03/2016, 10:00 - Alex Example: a";

            var cut = OlderExportCut.Find(Parse(inner), Parse(outer));

            Assert.Equal(2, cut.ShownFromSequenceNumber);
        }

        [Fact]
        public void Import_ShownAfter_StoresTheCutAndKeepsEveryLine()
        {
            using var folders = new SyntheticExportFolders();
            var inner = folders.CreateExport("20161209", Inner, ("b.jpg", "pixels"));
            var outer = folders.CreateExport("20170906", Outer);
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, inner, position: 4);
            var result = Import(folders, outer, position: 5, shownAfter: inner, fallbackFolder: inner);

            Assert.Equal(5, result.MessageCount);
            Assert.Equal(2, result.ShownMessageCount);
            Assert.Equal(4, result.ShownFromSequenceNumber);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(folders.OlderExportsPath);
            Assert.Equal(4L, Scalar(connection, "SELECT ShownFromSequenceNumber FROM ExportOrder WHERE Position = 5;"));
            Assert.Equal(1L, Scalar(connection, "SELECT Shown FROM ExportOrder WHERE Position = 5;"));
            Assert.Equal(DBNull.Value, Scalar(connection, "SELECT ShownFromSequenceNumber FROM ExportOrder WHERE Position = 4;"));
            Assert.Equal(8L, Scalar(connection, "SELECT COUNT(*) FROM Message;"));
        }

        [Fact]
        public void Import_ShownAfter_CutAtTheLastLine_ShowsNothing()
        {
            using var folders = new SyntheticExportFolders();
            var inner = folders.CreateExport("20161209", Inner);
            var outer = folders.CreateExport(
                "20170906",
                "26/03/2016, 10:00 - Alex Example: a\n08/12/2016, 21:00 - Sam Sample: final zorp");
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, inner, position: 4);
            var result = Import(folders, outer, position: 5, shownAfter: inner);

            Assert.Equal(0, result.ShownMessageCount);
            Assert.Equal(3, result.ShownFromSequenceNumber);
        }

        [Fact]
        public void Import_ShownAfter_BeforeTheInnerIsImported_IsRefused()
        {
            using var folders = new SyntheticExportFolders();
            var inner = folders.CreateExport("20161209", Inner);
            var outer = folders.CreateExport("20170906", Outer);
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            var exception = Assert.Throws<InvalidOperationException>(
                () => Import(folders, outer, position: 5, shownAfter: inner));

            Assert.Contains("not been imported yet", exception.Message, StringComparison.Ordinal);
            AssertCounts(folders, conversations: 0, orders: 0);
        }

        [Fact]
        public void Import_ShownAfter_AnInnerNotShown_IsRefused()
        {
            using var folders = new SyntheticExportFolders();
            var inner = folders.CreateExport("20161209", Inner);
            var outer = folders.CreateExport("20170906", Outer);
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, inner, position: 4, shown: false);

            var exception = Assert.Throws<InvalidOperationException>(
                () => Import(folders, outer, position: 5, shownAfter: inner));

            Assert.Contains("not shown", exception.Message, StringComparison.Ordinal);
            AssertCounts(folders, conversations: 1, orders: 1);
        }

        [Theory]
        [InlineData("08/12/2016, 21:00 - Sam Sample: final zorp.", "0 time(s)")]
        [InlineData("08/12/2016, 21:00 - Sam Sample: final zorp\n08/12/2016, 21:00 - Sam Sample: final zorp", "2 time(s)")]
        public void Import_ShownAfter_CutNotFoundExactlyOnce_IsRefused(string replacement, string expected)
        {
            using var folders = new SyntheticExportFolders();
            var inner = folders.CreateExport("20161209", Inner);
            var outer = folders.CreateExport(
                "20170906",
                Outer.Replace("08/12/2016, 21:00 - Sam Sample: final zorp", replacement, StringComparison.Ordinal));
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, inner, position: 4);

            var exception = Assert.Throws<InvalidOperationException>(
                () => Import(folders, outer, position: 5, shownAfter: inner));

            Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("zorp", exception.Message, StringComparison.Ordinal);
            AssertCounts(folders, conversations: 1, orders: 1);
        }

        [Fact]
        public void Import_ShownAfter_MustItselfBeShown()
        {
            using var folders = new SyntheticExportFolders();
            var inner = folders.CreateExport("20161209", Inner);
            var outer = folders.CreateExport("20170906", Outer);
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, inner, position: 4);

            Assert.Throws<ArgumentException>(
                () => Import(folders, outer, position: 5, shown: false, shownAfter: inner));
        }

        [Fact]
        public void Import_ShownPartStartingBeforeTheInnersLastLine_IsRefused()
        {
            using var folders = new SyntheticExportFolders();
            var inner = folders.CreateExport("20161209", Inner);
            var outer = folders.CreateExport(
                "20170906",
                Outer.Replace(
                    "08/12/2016, 21:00 - Alex Example: same minute, after the cut",
                    "08/12/2016, 20:59 - Alex Example: a minute earlier, after the cut",
                    StringComparison.Ordinal));
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, inner, position: 4);

            var exception = Assert.Throws<InvalidOperationException>(
                () => Import(folders, outer, position: 5, shownAfter: inner));

            Assert.Contains("overlaps 1 export", exception.Message, StringComparison.Ordinal);
            AssertCounts(folders, conversations: 1, orders: 1);
        }

        [Fact]
        public void Import_ShownPartOverlappingAnotherShownExport_IsRefused()
        {
            using var folders = new SyntheticExportFolders();
            var inner = folders.CreateExport("20161209", Inner);
            var other = folders.CreateExport(
                "other",
                "01/03/2017, 09:00 - Alex Example: x\n02/03/2017, 09:00 - Sam Sample: y");
            var outer = folders.CreateExport("20170906", Outer);
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, inner, position: 4);
            Import(folders, other, position: 9);

            Assert.Throws<InvalidOperationException>(
                () => Import(folders, outer, position: 5, shownAfter: inner));
            AssertCounts(folders, conversations: 2, orders: 2);
        }

        [Fact]
        public void Import_ShownPartOnly_CountsForLaterOverlapChecks()
        {
            using var folders = new SyntheticExportFolders();
            var inner = folders.CreateExport("20161209", Inner);
            var outer = folders.CreateExport("20170906", Outer);
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, inner, position: 4);
            Import(folders, outer, position: 5, shownAfter: inner);

            // Inside the outer's shown part (after the cut): refused.
            var during = folders.CreateExport(
                "during",
                "01/03/2017, 09:00 - Alex Example: x\n02/03/2017, 09:00 - Sam Sample: y");
            Assert.Throws<InvalidOperationException>(() => Import(folders, during, position: 9));

            // After everything shown: accepted.
            var later = folders.CreateExport(
                "later",
                "01/10/2017, 09:00 - Alex Example: x\n02/10/2017, 09:00 - Sam Sample: y");
            Import(folders, later, position: 6);

            AssertCounts(folders, conversations: 3, orders: 3);
        }

        [Fact]
        public void Import_ShownAfter_FailureRollsBackAndLeavesTheInnerUntouched()
        {
            using var folders = new SyntheticExportFolders();
            var inner = folders.CreateExport("20161209", Inner);
            var outer = folders.CreateExport(
                "20170906",
                Outer + "\n07/09/2017, 08:00 - Alex Example: c.jpg (file attached)",
                ("c.jpg", "pixels"));
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, inner, position: 4);

            using (new FileStream(Path.Combine(outer, "c.jpg"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Throws<InvalidOperationException>(
                    () => Import(folders, outer, position: 5, shownAfter: inner));
            }

            AssertCounts(folders, conversations: 1, orders: 1);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(folders.OlderExportsPath);
            Assert.Equal(3L, Scalar(connection, "SELECT COUNT(*) FROM Message;"));
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM MediaFile;"));
        }

        [Fact]
        public void Find_SplitsHiddenLinesByTheInnersPeriod_AndCountsDuplicates()
        {
            var outer =
                """
                20/03/2016, 08:00 - Alex Example: before the inner
                26/03/2016, 10:00 - Alex Example: a
                09/12/2016, 07:00 - Alex Example: after, out of order
                08/12/2016, 21:00 - Sam Sample: final zorp
                06/09/2017, 12:59 - Alex Example: end
                """;

            var cut = OlderExportCut.Find(Parse(Inner), Parse(outer));

            Assert.Equal(4, cut.HiddenCount);
            Assert.Equal(2, cut.HiddenWithinInnerPeriodCount);
            Assert.Equal(1, cut.HiddenBeforeInnerPeriodCount);
            Assert.Equal(1, cut.HiddenAfterInnerPeriodCount);
            Assert.Equal(0, cut.ShownDuplicatingInnerCount);
            Assert.True(cut.IsUsable);
        }

        /// <summary>Review repro A: the inner's last line is earlier in time than its first.</summary>
        [Fact]
        public void Import_ReproA_ShownLineDuplicatingTheInner_IsRefused()
        {
            using var folders = new SyntheticExportFolders();
            var inner = folders.CreateExport(
                "inner",
                "26/03/2016, 10:00 - Alex Example: a\n25/03/2016, 09:00 - Sam Sample: back");
            var outer = folders.CreateExport(
                "outer",
                "25/03/2016, 09:00 - Sam Sample: back\n26/03/2016, 10:00 - Alex Example: a\n27/03/2016, 10:00 - Alex Example: new");
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Assert.Equal(1, OlderExportCut.Find(
                OlderExportSource.Load(inner).Messages, OlderExportSource.Load(outer).Messages).ShownDuplicatingInnerCount);

            Import(folders, inner, position: 4);

            var exception = Assert.Throws<InvalidOperationException>(
                () => Import(folders, outer, position: 5, shownAfter: inner));

            Assert.Contains("1 line(s)", exception.Message, StringComparison.Ordinal);
            Assert.Contains("shown twice", exception.Message, StringComparison.Ordinal);
            AssertCounts(folders, conversations: 1, orders: 1);
        }

        /// <summary>Review repro B: two lines in the same minute, in a different order.</summary>
        [Fact]
        public void Import_ReproB_SameMinuteReordered_IsRefused()
        {
            using var folders = new SyntheticExportFolders();
            var inner = folders.CreateExport(
                "inner",
                "08/12/2016, 21:00 - Alex Example: x\n08/12/2016, 21:00 - Sam Sample: final zorp");
            var outer = folders.CreateExport(
                "outer",
                "08/12/2016, 21:00 - Sam Sample: final zorp\n08/12/2016, 21:00 - Alex Example: x\n06/09/2017, 12:59 - Alex Example: end");
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, inner, position: 4);

            var exception = Assert.Throws<InvalidOperationException>(
                () => Import(folders, outer, position: 5, shownAfter: inner));

            Assert.Contains("shown twice", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("zorp", exception.Message, StringComparison.Ordinal);
            AssertCounts(folders, conversations: 1, orders: 1);
        }

        [Fact]
        public void Import_ShownAfter_AtAnEarlierPosition_IsRefused()
        {
            using var folders = new SyntheticExportFolders();
            var inner = folders.CreateExport("20161209", Inner);
            var outer = folders.CreateExport("20170906", Outer);
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, inner, position: 5);

            var exception = Assert.Throws<InvalidOperationException>(
                () => Import(folders, outer, position: 1, shownAfter: inner));

            Assert.Contains("later position", exception.Message, StringComparison.Ordinal);
            AssertCounts(folders, conversations: 1, orders: 1);
        }

        [Fact]
        public void Import_ShownExportsOutOfTimelineOrder_IsRefused()
        {
            using var folders = new SyntheticExportFolders();
            var later = folders.CreateExport(
                "later",
                "01/10/2017, 09:00 - Alex Example: x\n02/10/2017, 09:00 - Sam Sample: y");
            var earlier = folders.CreateExport(
                "earlier",
                "01/10/2015, 09:00 - Alex Example: x\n02/10/2015, 09:00 - Sam Sample: y");
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, later, position: 1);

            // No overlap, but position 2 would put 2015 after 2017.
            var exception = Assert.Throws<InvalidOperationException>(() => Import(folders, earlier, position: 2));

            Assert.Contains("1 pair(s)", exception.Message, StringComparison.Ordinal);
            AssertCounts(folders, conversations: 1, orders: 1);

            // Not shown, it has no shown period and takes no part in the order, so it is accepted.
            Import(folders, earlier, position: 2, shown: false);
            AssertCounts(folders, conversations: 2, orders: 2);
        }

        [Fact]
        public void Import_PositionsFollowingTheTimeline_AreAccepted_IncludingTheCutMinute()
        {
            using var folders = new SyntheticExportFolders();
            var first = folders.CreateExport(
                "first",
                "01/10/2015, 09:00 - Alex Example: x\n02/10/2015, 09:00 - Sam Sample: y");
            var inner = folders.CreateExport("20161209", Inner);
            var outer = folders.CreateExport("20170906", Outer);
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, inner, position: 4);
            Import(folders, outer, position: 5, shownAfter: inner);

            // Inserting an earlier export at an earlier position keeps the order.
            Import(folders, first, position: 2);

            AssertCounts(folders, conversations: 3, orders: 3);
        }

        [Fact]
        public void Import_AnExportPlacedBetweenAnInnerAndItsCutOuter_IsRefusedWhenOutOfOrder()
        {
            using var folders = new SyntheticExportFolders();
            var inner = folders.CreateExport("20161209", Inner);
            var outer = folders.CreateExport("20170906", Outer);
            var afterAll = folders.CreateExport(
                "afterall",
                "01/10/2017, 09:00 - Alex Example: x\n02/10/2017, 09:00 - Sam Sample: y");
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            Import(folders, inner, position: 4);
            Import(folders, outer, position: 6, shownAfter: inner);

            // Dated after both, but placed between them by position.
            Assert.Throws<InvalidOperationException>(() => Import(folders, afterAll, position: 5));

            Import(folders, afterAll, position: 7);
            AssertCounts(folders, conversations: 3, orders: 3);
        }

        private static OlderExportImportResult Import(
            SyntheticExportFolders folders,
            string folder,
            int position,
            bool shown = true,
            string? shownAfter = null,
            string? fallbackFolder = null) =>
            new OlderExportImporter().Import(
                folders.OlderExportsPath,
                new OlderExportImportRequest
                {
                    Export = OlderExportSource.Load(folder),
                    Roles = new Dictionary<string, OlderExportRole>
                    {
                        ["Alex Example"] = OlderExportRole.Local,
                        ["Sam Sample"] = OlderExportRole.Other,
                    },
                    Position = position,
                    Shown = shown,
                    ShownAfter = shownAfter is null ? null : OlderExportSource.Load(shownAfter),
                    FallbackMediaFolder = fallbackFolder,
                    ImportedDateTimeUtc = Now,
                });

        private static void AssertCounts(SyntheticExportFolders folders, long conversations, long orders)
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(folders.OlderExportsPath);

            Assert.Equal(conversations, Scalar(connection, "SELECT COUNT(*) FROM Conversation;"));
            Assert.Equal(conversations, Scalar(connection, "SELECT COUNT(*) FROM ImportSource;"));
            Assert.Equal(orders, Scalar(connection, "SELECT COUNT(*) FROM ExportOrder;"));
            Assert.Equal(orders, Scalar(connection, "SELECT COUNT(*) FROM ExportRole;") is long roles ? roles / 2 : -1);
        }

        private static object? Scalar(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            return command.ExecuteScalar();
        }

        private static IReadOnlyList<ParsedMessage> Parse(string text)
        {
            using var reader = new StringReader(text);

            return new WhatsAppAndroidChatParser().Parse(reader);
        }
    }
}
