using System.Text;
using Chatcheology.Tools.OlderExportImport;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Tools.OlderExportImport.Tests
{
    /// <summary>
    /// Runs the tool in-process on synthetic export folders and checks what it prints.
    /// </summary>
    public sealed class OlderExportCommandTests : IDisposable
    {
        private const string ChatFileName = "WhatsApp Chat with Sam Sample.txt";

        /// <summary>Every invented string that must never reach default output.</summary>
        private static readonly string[] Secrets =
        [
            "Alex", "Example", "Sam", "Sample", "zorbly", "wibble", "caption", "blorp", "snapzz",
            "WhatsApp Chat", ".jpg", "olderzz", "supersetzz",
        ];

        private static readonly DateTime Now = new(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);

        private readonly string _root;

        public OlderExportCommandTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "Chatcheology.Tools.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(Path.Combine(_root, "workspace"));

            CreateExport(
                "olderzz",
                """
                14:03, 15 Jun 2013 - Messages to this chat are now secured.
                14:03, 15 Jun 2013 - Alex Example: zorbly wibble
                09:41, 5 Jan 2014 - Sam Sample: snapzz.jpg (file attached)
                caption words
                09:42, 5 Jan 2014 - Sam Sample: blorp.jpg (file attached)
                09:43, 5 Jan 2014 - Alex Example: <Media omitted>
                """,
                "snapzz.jpg");

            CreateExport(
                "supersetzz",
                """
                10/04/2015, 08:01 - Alex Example: zorbly
                10/04/2015, 08:02 - Sam Sample: wibble
                """);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }

        [Fact]
        public void DryRun_IsTheDefault_AndPrintsCountsOnly()
        {
            var (code, output, error) = Run(Folder("olderzz"), Folder("supersetzz"),
                "--overlap", Folder("olderzz"), Folder("supersetzz"));

            Assert.Equal(0, code);
            Assert.Equal(string.Empty, error);
            Assert.Contains("Export 1", output, StringComparison.Ordinal);
            Assert.Contains("Shape: A", output, StringComparison.Ordinal);
            Assert.Contains("Shape: B", output, StringComparison.Ordinal);
            Assert.Contains("First: 2013-06-15 14:03", output, StringComparison.Ordinal);
            Assert.Contains("Messages per year: 2013=2, 2014=3", output, StringComparison.Ordinal);
            Assert.Contains("System lines: 1", output, StringComparison.Ordinal);
            Assert.Contains("File attached, file present: 1", output, StringComparison.Ordinal);
            Assert.Contains("File attached, file absent: 1", output, StringComparison.Ordinal);
            Assert.Contains("Media omitted: 1", output, StringComparison.Ordinal);
            Assert.Contains("Distinct senders: 2", output, StringComparison.Ordinal);
            Assert.Contains("Largest message, source lines: 2", output, StringComparison.Ordinal);
            Assert.Contains("Year supplied by rule: 0", output, StringComparison.Ordinal);
            Assert.Contains("Overlap: 5 of 5 lines", output, StringComparison.Ordinal);
            Assert.Contains("media omitted (same time and sender): 0", output, StringComparison.Ordinal);
            Assert.Contains("no line in the second with the same time and sender: 5", output, StringComparison.Ordinal);
            Assert.Contains("Reverse: 0 of 0 lines", output, StringComparison.Ordinal);
            AssertNoSecrets(output);
        }

        [Fact]
        public void DryRun_ReportsYearsSuppliedByTheRule()
        {
            CreateExport(
                "yearlesszz",
                "07:18, 31 Dec 2014 - Alex Example: zorbly\n20:29, 22 Mar - Sam Sample: wibble");

            var (code, output, error) = Run(Folder("yearlesszz"));

            Assert.Equal(0, code);
            Assert.Equal(string.Empty, error);
            Assert.Contains("Last: 2015-03-22 20:29", output, StringComparison.Ordinal);
            Assert.Contains("Year supplied by rule: 1", output, StringComparison.Ordinal);
            AssertNoSecrets(output);
        }

        [Fact]
        public void DryRun_YearlessFirstHeader_StopsWithTheLineNumberOnly()
        {
            CreateExport("yearlesszz", "20:29, 22 Mar - Sam Sample: wibble");

            var (code, output, _) = Run(Folder("yearlesszz"));

            Assert.Equal(1, code);
            Assert.Contains("Export 1: Stopped: Line 1", output, StringComparison.Ordinal);
            Assert.Contains("no year", output, StringComparison.Ordinal);
            AssertNoSecrets(output);
            Assert.DoesNotContain("yearlesszz", output, StringComparison.Ordinal);
        }

        [Fact]
        public void DryRun_AStoppedExport_DoesNotHideTheOthers()
        {
            CreateExport("mixedzz", "10/04/2015, 08:01 - Alex Example: a\n08:02, 10 Apr 2015 - Sam Sample: zorbly");

            var (code, output, error) = Run(Folder("olderzz"), Folder("mixedzz"), Folder("supersetzz"));

            Assert.Equal(1, code);
            Assert.Equal(string.Empty, error);
            Assert.Contains("Export 1\n", output.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.Contains("Export 2: Stopped: Line 2", output, StringComparison.Ordinal);
            Assert.Contains("Export 3\n", output.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.Contains("Shape: B", output, StringComparison.Ordinal);
            AssertNoSecrets(output);
        }

        [Fact]
        public void DryRun_OverlapWithAStoppedExport_ReportsItsOwnStop()
        {
            CreateExport("mixedzz", "10/04/2015, 08:01 - Alex Example: a\n08:02, 10 Apr 2015 - Sam Sample: zorbly");

            var (code, output, _) = Run(
                Folder("olderzz"), "--overlap", Folder("supersetzz"), Folder("mixedzz"));

            Assert.Equal(1, code);
            Assert.Contains("Export 1\n", output.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.Contains("Overlap: Stopped (second --overlap export): Line 2", output, StringComparison.Ordinal);
            AssertNoSecrets(output);
        }

        [Fact]
        public void DryRun_OverlapSplitsByKind_AndLineNumbersOnlyWhenAsked()
        {
            CreateExport(
                "innerzz",
                """
                10/04/2015, 08:01 - Alex Example: zorbly
                caption
                10/04/2015, 08:02 - Sam Sample: snapzz.jpg (file attached)
                10/04/2015, 08:03 - Sam Sample: wibble
                """);
            CreateExport(
                "outerzz",
                """
                10/04/2015, 08:01 - Alex Example: zorbly
                caption
                10/04/2015, 08:02 - Sam Sample: <Media omitted>
                10/04/2015, 08:04 - Sam Sample: wibble
                """);

            var (defaultCode, defaultOutput, _) = Run("--overlap", Folder("innerzz"), Folder("outerzz"));

            Assert.Equal(0, defaultCode);
            Assert.Contains("Overlap: 2 of 3 lines", defaultOutput, StringComparison.Ordinal);
            Assert.Contains(
                "file attached vs media omitted (same time and sender): 1 (0 / 1 / 0 / 0)",
                defaultOutput,
                StringComparison.Ordinal);
            Assert.Contains(
                "no line in the second with the same time and sender: 1 (1 / 0 / 0 / 0)",
                defaultOutput,
                StringComparison.Ordinal);
            Assert.Contains("same sender within +/-1 minute: 1", defaultOutput, StringComparison.Ordinal);
            Assert.Contains("identical line within +/-60 minutes: 1", defaultOutput, StringComparison.Ordinal);
            // Only 08:01 and 08:02 of the outer lie within the inner period (08:01 to 08:03).
            Assert.Contains("Reverse: 1 of 2 lines", defaultOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("line numbers", defaultOutput, StringComparison.Ordinal);
            AssertNoSecrets(defaultOutput);

            var (code, output, _) = Run("--overlap", Folder("innerzz"), Folder("outerzz"), "--overlap-lines");

            Assert.Equal(0, code);
            Assert.Contains("Unmatched source line numbers (line numbers only):", output, StringComparison.Ordinal);
            Assert.Contains(
                "First export, file attached vs media omitted (same time and sender): 3",
                output,
                StringComparison.Ordinal);
            Assert.Contains(
                "First export, no line in the other with the same time and sender: 4",
                output,
                StringComparison.Ordinal);
            Assert.Contains(
                "Second export, file attached vs media omitted (same time and sender): 3",
                output,
                StringComparison.Ordinal);
            Assert.Contains(
                "Second export, no line in the other with the same time and sender: none",
                output,
                StringComparison.Ordinal);
            AssertNoSecrets(output);
            Assert.DoesNotContain("caption", output, StringComparison.Ordinal);
        }

        [Fact]
        public void OverlapLines_WithoutOverlap_IsAUsageError()
        {
            var (code, _, error) = Run(Folder("olderzz"), "--overlap-lines");

            Assert.Equal(2, code);
            Assert.Contains("--overlap-lines needs --overlap", error, StringComparison.Ordinal);
        }

        [Fact]
        public void DryRun_WithParentFolder_Works()
        {
            var (code, output, _) = Run("dry-run", "--parent", _root, "olderzz", "--fallback-folder", "supersetzz");

            Assert.Equal(0, code);
            Assert.Contains("File attached, absent but in fallback folder: 0", output, StringComparison.Ordinal);
            AssertNoSecrets(output);
        }

        [Fact]
        public void Senders_PrintsNamesOnlyWhenAsked()
        {
            var (code, output, _) = Run("senders", Folder("olderzz"));

            Assert.Equal(0, code);
            Assert.Contains("Alex Example", output, StringComparison.Ordinal);
            Assert.Contains("Sam Sample", output, StringComparison.Ordinal);
            Assert.DoesNotContain("zorbly", output, StringComparison.Ordinal);
        }

        [Fact]
        public void Import_WritesAndPrintsCountsOnly()
        {
            var (code, output, error) = Run(
                "import", Folder("olderzz"),
                "--workspace-folder", Path.Combine(_root, "workspace"),
                "--local", "Alex Example", "--other", "Sam Sample",
                "--position", "1", "--shown", "yes", "--create");

            Assert.Equal(string.Empty, error);
            Assert.Equal(0, code);
            Assert.Contains("Messages: 5", output, StringComparison.Ordinal);
            Assert.Contains("resolved in own folder: 1", output, StringComparison.Ordinal);
            Assert.Contains("unresolved: 1", output, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(_root, "workspace", "older-exports.db")));
            AssertNoSecrets(output);
        }

        [Fact]
        public void Import_WithoutCreate_DoesNotCreateTheFile()
        {
            var (code, _, error) = Run(
                "import", Folder("olderzz"),
                "--workspace-folder", Path.Combine(_root, "workspace"),
                "--local", "Alex Example", "--other", "Sam Sample",
                "--position", "1", "--shown", "yes");

            Assert.Equal(1, code);
            Assert.Contains("--create", error, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(_root, "workspace", "older-exports.db")));
        }

        [Fact]
        public void Import_ThirdSenderOrWrongRole_StopsWithoutNames()
        {
            var (code, output, error) = Run(
                "import", Folder("olderzz"),
                "--workspace-folder", Path.Combine(_root, "workspace"),
                "--local", "Alex Example", "--other", "Sam Samplezz",
                "--position", "1", "--shown", "yes", "--create");

            Assert.Equal(1, code);
            Assert.Contains("Stopped", error, StringComparison.Ordinal);
            AssertNoSecrets(error);
            AssertNoSecrets(output);

            // Refused before --create acted: no empty older-exports file is left behind.
            Assert.False(File.Exists(Path.Combine(_root, "workspace", "older-exports.db")));
            Assert.DoesNotContain("Created", output, StringComparison.Ordinal);
        }

        [Fact]
        public void Import_WithCreate_FailingAfterCreation_RemovesTheNewFile()
        {
            string error;
            int code;

            // A media file held open exclusively makes the import fail after the file was created.
            using (new FileStream(Path.Combine(Folder("olderzz"), "snapzz.jpg"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                (code, _, error) = Run(
                    "import", Folder("olderzz"),
                    "--workspace-folder", Path.Combine(_root, "workspace"),
                    "--local", "Alex Example", "--other", "Sam Sample",
                    "--position", "1", "--shown", "yes", "--create");
            }

            Assert.Equal(1, code);
            Assert.Contains("rolled back", error, StringComparison.Ordinal);
            AssertNoSecrets(error);
            Assert.False(File.Exists(Path.Combine(_root, "workspace", "older-exports.db")));
        }

        [Fact]
        public void ZeroOrTwoTxtFiles_StopWithACountOnly()
        {
            var empty = Path.Combine(_root, "emptyzz");
            Directory.CreateDirectory(empty);
            File.WriteAllText(Path.Combine(Folder("supersetzz"), "notes.txt"), "zorbly");

            // Dry-run stops are reported per export in the output.
            var (zeroCode, zeroError, _) = Run(empty);
            var (twoCode, twoError, _) = Run(Folder("supersetzz"));

            Assert.Equal(1, zeroCode);
            Assert.Contains("0 .txt", zeroError, StringComparison.Ordinal);
            Assert.Equal(1, twoCode);
            Assert.Contains("2 .txt", twoError, StringComparison.Ordinal);
            AssertNoSecrets(zeroError + twoError);
            Assert.DoesNotContain("emptyzz", zeroError, StringComparison.Ordinal);
            Assert.DoesNotContain("notes", twoError, StringComparison.Ordinal);
        }

        [Fact]
        public void MissingFolder_StopsWithoutThePath()
        {
            var (code, error, _) = Run(Path.Combine(_root, "nowherezz"));

            Assert.Equal(1, code);
            Assert.Contains("Export 1: Stopped: DirectoryNotFoundException", error, StringComparison.Ordinal);
            Assert.DoesNotContain("nowherezz", error, StringComparison.Ordinal);
            Assert.DoesNotContain(_root, error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void UsageErrors_DoNotEchoValues()
        {
            var (code, _, error) = Run("--frobnicate-Alex", Folder("olderzz"));

            Assert.Equal(2, code);
            AssertNoSecrets(error);
        }

        [Fact]
        public void ParseErrors_ReportTheLineNumberOnly()
        {
            CreateExport("mixedzz", "10/04/2015, 08:01 - Alex Example: a\n08:02, 10 Apr 2015 - Sam Sample: zorbly");

            var (code, output, _) = Run(Folder("mixedzz"));

            Assert.Equal(1, code);
            Assert.Contains("Line 2", output, StringComparison.Ordinal);
            AssertNoSecrets(output);

            // Import keeps stopping as before, on the error stream.
            var (importCode, _, importError) = Run(
                "import", Folder("mixedzz"),
                "--workspace-folder", Path.Combine(_root, "workspace"),
                "--local", "Alex Example", "--other", "Sam Sample",
                "--position", "1", "--shown", "yes", "--create");

            Assert.Equal(1, importCode);
            Assert.Contains("Stopped: Line 2", importError, StringComparison.Ordinal);
            AssertNoSecrets(importError);
            Assert.False(File.Exists(Path.Combine(_root, "workspace", "older-exports.db")));
        }

        private static void AssertNoSecrets(string text)
        {
            // Case-sensitive: the invented names are capitalised, and ignoring case would flag
            // ordinary words in the tool's own labels ("same" contains "sam").
            foreach (var secret in Secrets)
            {
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            }
        }

        private string Folder(string name) => Path.Combine(_root, name);

        private void CreateExport(string folderName, string chat, params string[] mediaNames)
        {
            var folder = Path.Combine(_root, folderName);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, ChatFileName), chat, new UTF8Encoding(false));

            foreach (var name in mediaNames)
            {
                File.WriteAllText(Path.Combine(folder, name), "pixels");
            }
        }

        private static (int Code, string Output, string Error) Run(params string[] args)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();

            var code = OlderExportCommand.Run(args, output, error, () => Now);

            return (code, output.ToString(), error.ToString());
        }
    }
}
