using Chatcheology.Data.Memories;
using Chatcheology.Tests.Memories;
using Microsoft.Data.Sqlite;
using static Chatcheology.Tests.Memories.SyntheticPsnFile;

namespace Chatcheology.Tools.MemoryImport.Tests
{
    /// <summary>Runs the tool in-process on synthetic PSN files and checks what it prints.</summary>
    public sealed class MemoryImportCommandTests : IDisposable
    {
        private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

        private static readonly DateTime Now = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);

        private readonly string _root;
        private readonly string _workspace;
        private readonly string _psnFile;

        public MemoryImportCommandTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "Chatcheology.MemoryImport.Tests", Guid.NewGuid().ToString("N"));
            _workspace = Path.Combine(_root, "workspace");
            Directory.CreateDirectory(_workspace);
            _psnFile = Standard().WriteTo(_root, "zzsecretfile.json");
        }

        /// <summary>Every invented string that must never reach output or error text.</summary>
        private string[] Secrets =>
        [
            "Example", "Quest", "Sample", "Racer", "alpha", "beta", "NPWR", "playstation", "https", "examplequest",
            "zzsecretfile", "Chatcheology.MemoryImport.Tests", _root,
        ];

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder must not fail a test.
            }
        }

        private sealed class FakeSource(Dictionary<string, byte[]> bodies) : IMemoryImageSource
        {
            public byte[]? Fetch(Uri uri, long maxBytes) => bodies.GetValueOrDefault(uri.AbsoluteUri);
        }

        private (int Code, string Output, string Error) Run(params string[] args) => RunWith(new FakeSource([]), args);

        private (int Code, string Output, string Error) RunWith(IMemoryImageSource source, params string[] args)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = MemoryImportCommand.Run(args, output, error, () => Now, () => source);
            var (o, e) = (output.ToString(), error.ToString());

            foreach (var secret in Secrets)
            {
                Assert.DoesNotContain(secret, o, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(secret, e, StringComparison.OrdinalIgnoreCase);
            }

            return (code, o, e);
        }

        private string MemoriesPath => Path.Combine(_workspace, MemoriesDatabase.FileName);

        [Fact]
        public void DryRun_IsTheDefault_AndPrintsAggregatesOnly()
        {
            var (code, output, error) = Run(_psnFile, "--local", "alpha", "--other", "beta");

            Assert.Equal(0, code);
            Assert.Equal(string.Empty, error);
            Assert.Contains("Dry run: nothing is written.", output, StringComparison.Ordinal);
            Assert.Contains("Games: 2", output, StringComparison.Ordinal);
            Assert.Contains("Records: 2", output, StringComparison.Ordinal);
            Assert.Contains("Items: 6", output, StringComparison.Ordinal);
            Assert.Contains("Periods: 4", output, StringComparison.Ordinal);
            Assert.Contains("  strong: 1", output, StringComparison.Ordinal);
            Assert.Contains("  supporting: 1", output, StringComparison.Ordinal);
            Assert.Contains("  same-period: 0", output, StringComparison.Ordinal);
            Assert.Contains("  one-account: 2", output, StringComparison.Ordinal);
            Assert.Contains("Timeline cards (periods with both accounts): 2", output, StringComparison.Ordinal);
            Assert.Contains("Records with no shared period: 1", output, StringComparison.Ordinal);
            Assert.Contains("  not located: 0", output, StringComparison.Ordinal);
            Assert.Contains("  split across periods: 0", output, StringComparison.Ordinal);
            Assert.Contains("Records per year: 2015=1, 2016=1", output, StringComparison.Ordinal);
            Assert.Contains("First date: 2015-03-01", output, StringComparison.Ordinal);
            Assert.Contains("Last date: 2016-07-04", output, StringComparison.Ordinal);
            Assert.False(File.Exists(MemoriesPath));
        }

        [Fact]
        public void DryRun_UnknownPrefix_StopsWithoutNamingIt()
        {
            var (code, _, error) = Run("psn-dry-run", _psnFile, "--local", "gamma", "--other", "beta");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("Game at index 0", error, StringComparison.Ordinal);
            Assert.DoesNotContain("gamma", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DryRun_SamePrefixTwice_IsRefused()
        {
            var (code, _, error) = Run(_psnFile, "--local", "alpha", "--other", "ALPHA");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("same", error, StringComparison.Ordinal);
        }

        [Fact]
        public void DryRun_OtherSchema_IsRefused()
        {
            var file = Standard();
            file.Schema = "something-else";
            var path = file.WriteTo(_root, "zzsecretfile2.json");

            var (code, _, error) = Run(path, "--local", "alpha", "--other", "beta");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("schema", error, StringComparison.Ordinal);
        }

        [Fact]
        public void DryRun_TimeMismatch_StopsWithTheEventIndexOnly()
        {
            var file = Standard();
            file.Events[1].Local[0] = file.Events[1].Local[0] with { Time = "2016-07-04 11:00:00" };
            var path = file.WriteTo(_root, "zzsecretfile3.json");

            var (code, _, error) = Run(path, "--local", "alpha", "--other", "beta");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("Event at index 1", error, StringComparison.Ordinal);
        }

        [Fact]
        public void DryRun_MissingFile_PrintsTypeOnly()
        {
            var (code, _, error) = Run(Path.Combine(_root, "zzsecretfile-absent.json"), "--local", "alpha", "--other", "beta");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("FileNotFoundException", error, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("--local", "alpha")]
        [InlineData("--other", "beta", "--gap", "0")]
        [InlineData("--local", "alpha", "--other", "beta", "--zzsecretfile")]
        [InlineData("--local", "alpha", "--other", "beta", "--max-bytes", "5")]
        [InlineData("--local", "alpha", "--other", "beta", "--create")]
        public void UsageErrors_NeverEchoValues(params string[] args)
        {
            var (code, _, error) = Run([_psnFile, .. args]);

            Assert.Equal(MemoryImportCommand.UsageError, code);
            Assert.StartsWith("Usage error:", error, StringComparison.Ordinal);
        }

        [Fact]
        public void Import_WithoutCreate_RefusesAndWritesNothing()
        {
            var (code, _, error) = Run("psn-import", _psnFile, "--workspace-folder", _workspace, "--local", "alpha", "--other", "beta");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("--create", error, StringComparison.Ordinal);
            Assert.False(File.Exists(MemoriesPath));
        }

        [Fact]
        public void Import_ThenIdenticalReImport_ReportsZeroChanges()
        {
            var (code, output, error) = Run("psn-import", _psnFile, "--workspace-folder", _workspace, "--local", "alpha", "--other", "beta", "--create");

            Assert.Equal(0, code);
            Assert.Equal(string.Empty, error);
            Assert.Contains("Created the memories file.", output, StringComparison.Ordinal);
            Assert.Contains("  Records added: 2", output, StringComparison.Ordinal);
            Assert.Contains("  Changes: 3", output, StringComparison.Ordinal);
            Assert.Contains("Timeline cards (periods with both accounts): 2", output, StringComparison.Ordinal);

            (code, output, _) = Run("psn-import", _psnFile, "--workspace-folder", _workspace, "--local", "alpha", "--other", "beta", "--create");

            Assert.Equal(0, code);
            Assert.DoesNotContain("Created", output, StringComparison.Ordinal);
            Assert.Contains("  Records unchanged: 2", output, StringComparison.Ordinal);
            Assert.Contains("  Changes: 0", output, StringComparison.Ordinal);
        }

        [Fact]
        public void Import_RemovedRecord_IsReportedAsMissingAndKept()
        {
            Run("psn-import", _psnFile, "--workspace-folder", _workspace, "--local", "alpha", "--other", "beta", "--create");
            var smaller = Standard();
            smaller.Events.RemoveAt(0);
            var path = smaller.WriteTo(_root, "zzsecretfile4.json");

            var (code, output, _) = Run("psn-import", path, "--workspace-folder", _workspace, "--local", "alpha", "--other", "beta");

            Assert.Equal(0, code);
            Assert.Contains("  Entries missing from this file (kept): 1", output, StringComparison.Ordinal);
        }

        [Fact]
        public void Import_CollectionNameAndGap_AreUsed()
        {
            var (code, _, _) = Run("psn-import", _psnFile, "--workspace-folder", _workspace, "--local", "alpha", "--other", "beta",
                "--create", "--collection", "Invented layer", "--gap", "240");

            Assert.Equal(0, code);
            using var connection = new SqliteConnection($"Data Source={MemoriesPath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Name || '|' || PeriodGapMinutes || '|' || ShownByDefault FROM Collection;";
            Assert.Equal("Invented layer|240|0", command.ExecuteScalar());
        }

        [Fact]
        public void Import_BadFile_StopsBeforeCreatingAnything()
        {
            var (code, _, _) = Run("psn-import", _psnFile, "--workspace-folder", _workspace, "--local", "gamma", "--other", "beta", "--create");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.False(File.Exists(MemoriesPath));
        }

        [Fact]
        public void Import_IntoAMainWorkspaceRenamed_IsRefused()
        {
            Chatcheology.Data.Workspace.WorkspaceDatabase.Initialise(MemoriesPath);

            var (code, _, error) = Run("psn-import", _psnFile, "--workspace-folder", _workspace, "--local", "alpha", "--other", "beta");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("workspace", error, StringComparison.Ordinal);
        }

        [Fact]
        public void FetchImages_PrintsCountsOnly()
        {
            Run("psn-import", _psnFile, "--workspace-folder", _workspace, "--local", "alpha", "--other", "beta", "--create");
            var source = new FakeSource(new() { [Quest.Icon!] = Png });

            var (code, output, _) = RunWith(source, "fetch-images", "--workspace-folder", _workspace);

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("  Fetched: 1", output, StringComparison.Ordinal);
            Assert.Contains("  Already present: 0", output, StringComparison.Ordinal);
            Assert.Contains("  Failed: 1", output, StringComparison.Ordinal);

            (code, output, _) = RunWith(new FakeSource(new() { [Racer.Icon!] = Png }), "fetch-images", "--workspace-folder", _workspace);

            Assert.Equal(0, code);
            Assert.Contains("  Fetched: 1", output, StringComparison.Ordinal);
            Assert.Contains("  Already present: 1", output, StringComparison.Ordinal);
            Assert.Single(Directory.GetFiles(Path.Combine(_workspace, MemoriesDatabase.MediaFolderName)));
        }

        [Fact]
        public void FetchImages_WithoutAMemoriesFile_Stops()
        {
            var (code, _, error) = Run("fetch-images", "--workspace-folder", _workspace);

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("FileNotFoundException", error, StringComparison.Ordinal);
            Assert.False(File.Exists(MemoriesPath));
        }

        [Fact]
        public void FetchImages_RefusesPsnArguments()
        {
            var (code, _, _) = Run("fetch-images", "--workspace-folder", _workspace, "--local", "alpha");

            Assert.Equal(MemoryImportCommand.UsageError, code);
        }

        [Fact]
        public void Help_ListsTheCommands()
        {
            var (code, output, _) = Run("--help");

            Assert.Equal(0, code);
            Assert.Contains("psn-import", output, StringComparison.Ordinal);
            Assert.Contains("fetch-images", output, StringComparison.Ordinal);
        }
    }
}
