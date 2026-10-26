using System.Security.Cryptography;
using Chatcheology.Core.Models;
using Chatcheology.Data.OlderExports;

namespace Chatcheology.Data.Tests.OlderExports
{
    public class OlderExportSourceTests
    {
        private const string Chat =
            """
            10/04/2015, 08:01 - Alex Example: snap.jpg (file attached)
            10/04/2015, 08:02 - Sam Sample: <Media omitted>
            """;

        [Fact]
        public void Load_FindsTheSingleTxtAndHashesExactlyItsBytes()
        {
            using var folders = new SyntheticExportFolders();
            var folder = folders.CreateExport("20150808", Chat, ("snap.jpg", "pixels"));

            var export = OlderExportSource.Load(folder);

            var expected = Convert.ToHexString(
                SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, SyntheticExportFolders.ChatFileName))));

            Assert.Equal(SyntheticExportFolders.ChatFileName, export.ChatFileName);
            Assert.Equal(expected, export.SHA256);
            Assert.Equal(WhatsAppHeaderShape.DayFirst, export.Shape);
            Assert.Equal(2, export.Messages.Count);
            Assert.Equal(["snap.jpg"], export.MediaFileNames.Order());
        }

        [Fact]
        public void Load_ZeroTxtFiles_StopsWithTheCount()
        {
            using var folders = new SyntheticExportFolders();
            var folder = Path.Combine(folders.RootPath, "empty");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "snap.jpg"), "pixels");

            var exception = Assert.Throws<InvalidOperationException>(() => OlderExportSource.Load(folder));

            Assert.Contains("0 .txt", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("snap", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(folders.RootPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Load_TwoTxtFiles_StopsWithTheCountAndRenamesNothing()
        {
            using var folders = new SyntheticExportFolders();
            var folder = folders.CreateExport("20150808", Chat, ("notes.TXT", "zorp"));

            var exception = Assert.Throws<InvalidOperationException>(() => OlderExportSource.Load(folder));

            Assert.Contains("2 .txt", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Sam", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("notes", exception.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(folder, SyntheticExportFolders.ChatFileName)));
            Assert.True(File.Exists(Path.Combine(folder, "notes.TXT")));
        }

        [Fact]
        public void Load_MissingFolder_ThrowsWithoutThePath()
        {
            using var folders = new SyntheticExportFolders();
            var missing = Path.Combine(folders.RootPath, "nowhere");

            var exception = Assert.Throws<DirectoryNotFoundException>(() => OlderExportSource.Load(missing));

            Assert.DoesNotContain("nowhere", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Load_InvalidUtf8_IsRefusedWithoutQuotingBytes()
        {
            using var folders = new SyntheticExportFolders();
            var folder = Path.Combine(folders.RootPath, "bad");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(
                Path.Combine(folder, SyntheticExportFolders.ChatFileName),
                [.. "10/04/2015, 08:01 - Alex Example: "u8.ToArray(), 0xFF, 0xFE, 0x41]);

            var exception = Assert.Throws<FormatException>(() => OlderExportSource.Load(folder));

            Assert.Contains("UTF-8", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("FF", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Load_ParserRejection_PropagatesWithTheLineNumber()
        {
            using var folders = new SyntheticExportFolders();
            var folder = folders.CreateExport(
                "mixed",
                "10/04/2015, 08:01 - Alex Example: a\n08:02, 10 Apr 2015 - Sam Sample: b");

            var exception = Assert.Throws<FormatException>(() => OlderExportSource.Load(folder));

            Assert.Contains("Line 2", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Summary_CountsOnly()
        {
            using var folders = new SyntheticExportFolders();
            var fallback = folders.CreateExport("20161209", "10/04/2015, 08:01 - Alex Example: x", ("old.jpg", "o"));
            var folder = folders.CreateExport(
                "20170906",
                """
                30/12/2016, 23:59 - Messages to this chat are now secured.
                30/12/2016, 23:59 - Alex Example: snap.jpg (file attached)
                caption words
                01/01/2017, 00:01 - Sam Sample: old.jpg (file attached)
                01/01/2017, 00:02 - Sam Sample: gone.jpg (file attached)
                01/01/2017, 00:03 - Sam Sample: <Media omitted>
                """,
                ("snap.jpg", "pixels"));

            var summary = OlderExportSummary.From(OlderExportSource.Load(folder), fallback);

            Assert.Equal(WhatsAppHeaderShape.DayFirst, summary.Shape);
            Assert.Equal(new DateTime(2016, 12, 30, 23, 59, 0), summary.First);
            Assert.Equal(new DateTime(2017, 1, 1, 0, 3, 0), summary.Last);
            Assert.Equal(6 - 1, summary.MessageCount);
            Assert.Equal([new(2016, 2), new KeyValuePair<int, int>(2017, 3)], summary.MessagesPerYear);
            Assert.Equal(1, summary.SystemMessageCount);
            Assert.Equal(1, summary.FileAttachedPresentCount);
            Assert.Equal(2, summary.FileAttachedAbsentCount);
            Assert.Equal(1, summary.FileAttachedAbsentButInFallbackCount);
            Assert.Equal(1, summary.MediaOmittedCount);
            Assert.Equal(2, summary.DistinctSenderCount);
        }

        [Fact]
        public void CountBySender_ListsNamesWithCounts()
        {
            using var folders = new SyntheticExportFolders();
            var folder = folders.CreateExport(
                "x",
                "10/04/2015, 08:01 - Alex Example: a\n10/04/2015, 08:02 - Sam Sample: b\n10/04/2015, 08:03 - Sam Sample: c");

            var counts = OlderExportSummary.CountBySender(OlderExportSource.Load(folder));

            Assert.Equal([new("Sam Sample", 2), new KeyValuePair<string, int>("Alex Example", 1)], counts);
        }
    }
}
