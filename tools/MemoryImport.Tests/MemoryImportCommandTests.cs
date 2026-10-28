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
            "zzsecretfile", "zzsecretdefinitions", "Invented", "questtrophy", "racertrophy", "Chatcheology.MemoryImport.Tests", _root,
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
            var (code, output, error) = Run(_psnFile, "--local", "alpha", "--other", "beta", "--pair-a", "alpha");

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
            var (code, _, error) = Run("psn-dry-run", _psnFile, "--local", "gamma", "--other", "beta", "--pair-a", "beta");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("Game at index 0", error, StringComparison.Ordinal);
            Assert.DoesNotContain("gamma", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DryRun_SamePrefixTwice_IsRefused()
        {
            var (code, _, error) = Run(_psnFile, "--local", "alpha", "--other", "ALPHA", "--pair-a", "alpha");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("same", error, StringComparison.Ordinal);
        }

        [Fact]
        public void DryRun_OtherSchema_IsRefused()
        {
            var file = Standard();
            file.Schema = "something-else";
            var path = file.WriteTo(_root, "zzsecretfile2.json");

            var (code, _, error) = Run(path, "--local", "alpha", "--other", "beta", "--pair-a", "alpha");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("schema", error, StringComparison.Ordinal);
        }

        [Fact]
        public void DryRun_TimeMismatch_StopsWithTheEventIndexOnly()
        {
            var file = Standard();
            file.Events[1].Local[0] = file.Events[1].Local[0] with { Time = "13:00:00" };
            var path = file.WriteTo(_root, "zzsecretfile3.json");

            var (code, _, error) = Run(path, "--local", "alpha", "--other", "beta", "--pair-a", "alpha");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("Event at index 1", error, StringComparison.Ordinal);
        }

        [Fact]
        public void DryRun_MissingFile_PrintsTypeOnly()
        {
            var (code, _, error) = Run(Path.Combine(_root, "zzsecretfile-absent.json"), "--local", "alpha", "--other", "beta", "--pair-a", "alpha");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("FileNotFoundException", error, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("--local", "alpha")]
        [InlineData("--local", "alpha", "--other", "beta", "--pair-a", "alpha", "--gap", "0")]
        [InlineData("--local", "alpha", "--other", "beta")]
        [InlineData("--local", "alpha", "--other", "beta", "--pair-a", "alpha", "--zzsecretfile")]
        [InlineData("--local", "alpha", "--other", "beta", "--pair-a", "alpha", "--max-bytes", "5")]
        [InlineData("--local", "alpha", "--other", "beta", "--pair-a", "alpha", "--create")]
        public void UsageErrors_NeverEchoValues(params string[] args)
        {
            var (code, _, error) = Run([_psnFile, .. args]);

            Assert.Equal(MemoryImportCommand.UsageError, code);
            Assert.StartsWith("Usage error:", error, StringComparison.Ordinal);
        }

        [Fact]
        public void DryRun_PairANamingNeitherPrefix_IsRefusedWithoutNamingIt()
        {
            var (code, _, error) = Run(_psnFile, "--local", "alpha", "--other", "beta", "--pair-a", "gamma");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("side a", error, StringComparison.Ordinal);
            Assert.DoesNotContain("gamma", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DryRun_PairAWrongAccount_StopsAtTheDifferentSetGame()
        {
            var (code, _, error) = Run(_psnFile, "--local", "alpha", "--other", "beta", "--pair-a", "beta");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("Game at index 1", error, StringComparison.Ordinal);
        }

        [Fact]
        public void DryRun_OtherAccountAsSideA_GivesTheSameCounts()
        {
            var file = Standard();
            file.LocalIsSideA = false;
            var path = file.WriteTo(_root, "zzsecretfile5.json");

            var (code, output, _) = Run(path, "--local", "alpha", "--other", "beta", "--pair-a", "beta");

            Assert.Equal(0, code);
            Assert.Contains("  strong: 1", output, StringComparison.Ordinal);
            Assert.Contains("  supporting: 1", output, StringComparison.Ordinal);
            Assert.Contains("  not located: 0", output, StringComparison.Ordinal);
        }

        [Fact]
        public void Import_WithoutCreate_RefusesAndWritesNothing()
        {
            var (code, _, error) = Run("psn-import", _psnFile, "--workspace-folder", _workspace, "--local", "alpha", "--other", "beta", "--pair-a", "alpha");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("--create", error, StringComparison.Ordinal);
            Assert.False(File.Exists(MemoriesPath));
        }

        [Fact]
        public void Import_ThenIdenticalReImport_ReportsZeroChanges()
        {
            var (code, output, error) = Run("psn-import", _psnFile, "--workspace-folder", _workspace, "--local", "alpha", "--other", "beta", "--pair-a", "alpha", "--create");

            Assert.Equal(0, code);
            Assert.Equal(string.Empty, error);
            Assert.Contains("Created the memories file.", output, StringComparison.Ordinal);
            Assert.Contains("  Records added: 2", output, StringComparison.Ordinal);
            Assert.Contains("  Changes: 3", output, StringComparison.Ordinal);
            Assert.Contains("Timeline cards (periods with both accounts): 2", output, StringComparison.Ordinal);

            (code, output, _) = Run("psn-import", _psnFile, "--workspace-folder", _workspace, "--local", "alpha", "--other", "beta", "--pair-a", "alpha", "--create");

            Assert.Equal(0, code);
            Assert.DoesNotContain("Created", output, StringComparison.Ordinal);
            Assert.Contains("  Records unchanged: 2", output, StringComparison.Ordinal);
            Assert.Contains("  Changes: 0", output, StringComparison.Ordinal);
        }

        [Fact]
        public void Import_RemovedRecord_IsReportedAsMissingAndKept()
        {
            Run("psn-import", _psnFile, "--workspace-folder", _workspace, "--local", "alpha", "--other", "beta", "--pair-a", "alpha", "--create");
            var smaller = Standard();
            smaller.Events.RemoveAt(0);
            var path = smaller.WriteTo(_root, "zzsecretfile4.json");

            var (code, output, _) = Run("psn-import", path, "--workspace-folder", _workspace, "--local", "alpha", "--other", "beta", "--pair-a", "alpha");

            Assert.Equal(0, code);
            Assert.Contains("  Entries missing from this file (kept): 1", output, StringComparison.Ordinal);
        }

        [Fact]
        public void Import_CollectionNameAndGap_AreUsed()
        {
            var (code, _, _) = Run("psn-import", _psnFile, "--workspace-folder", _workspace, "--local", "alpha", "--other", "beta", "--pair-a", "alpha",
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
            var (code, _, _) = Run("psn-import", _psnFile, "--workspace-folder", _workspace, "--local", "gamma", "--other", "beta", "--pair-a", "beta", "--create");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.False(File.Exists(MemoriesPath));
        }

        [Fact]
        public void Import_IntoAMainWorkspaceRenamed_IsRefused()
        {
            Chatcheology.Data.Workspace.WorkspaceDatabase.Initialise(MemoriesPath);

            var (code, _, error) = Run("psn-import", _psnFile, "--workspace-folder", _workspace, "--local", "alpha", "--other", "beta", "--pair-a", "alpha");

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("workspace", error, StringComparison.Ordinal);
        }

        [Fact]
        public void FetchImages_PrintsCountsOnly()
        {
            Run("psn-import", _psnFile, "--workspace-folder", _workspace, "--local", "alpha", "--other", "beta", "--pair-a", "alpha", "--create");
            var source = new FakeSource(new() { [Quest.Icon!] = Png });

            var (code, output, _) = RunWith(source, "fetch-images", "--workspace-folder", _workspace);

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("Game images.\n  Fetched: 1\n  Already present: 0\n  Failed: 1\n", Lines(output), StringComparison.Ordinal);
            Assert.Contains("Trophy icons.\n  Fetched: 0\n  Already present: 0\n  Failed: 0\n", Lines(output), StringComparison.Ordinal);

            (code, output, _) = RunWith(new FakeSource(new() { [Racer.Icon!] = Png }), "fetch-images", "--workspace-folder", _workspace);

            Assert.Equal(0, code);
            Assert.Contains("Game images.\n  Fetched: 1\n  Already present: 1\n  Failed: 0\n", Lines(output), StringComparison.Ordinal);
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

        private static string Lines(string text) => text.ReplaceLineEndings("\n");

        private void ImportStandard() =>
            Assert.Equal(0, Run("psn-import", _psnFile, "--workspace-folder", _workspace, "--local", "alpha", "--other", "beta", "--pair-a", "alpha", "--create").Code);

        private string DefinitionsFile(SyntheticTrophyDefinitions? file = null) =>
            (file ?? SyntheticTrophyDefinitions.Standard()).WriteTo(_root, "zzsecretdefinitions.json");

        private long Scalar(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={MemoriesPath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            SqliteConnection.ClearAllPools();
            return value;
        }

        [Fact]
        public void EnrichDryRun_PrintsCountsOnly_AndWritesNothing()
        {
            ImportStandard();
            SqliteConnection.ClearAllPools();
            var before = File.ReadAllBytes(MemoriesPath);

            var (code, output, error) = Run("psn-enrich-dry-run", "--workspace-folder", _workspace, DefinitionsFile());

            Assert.Equal(0, code);
            Assert.Equal(string.Empty, error);
            Assert.Equal(
                "Dry run: nothing is written.\n" +
                "Memories file format: 3\n" +
                "Sets: 2\n" +
                "Definitions: 4\n" +
                "  hidden: 1\n" +
                "Items with a definition: 5\n" +
                "Items without a definition: 1\n" +
                "Items whose definition has another type: 0\n" +
                "Definitions not stored (type mismatch): 0\n" +
                "Definitions added: 4\n" +
                "Definitions updated: 0\n" +
                "Definitions unchanged: 0\n" +
                "Stored definitions removed (type mismatch): 0\n" +
                "Changes: 4\n",
                Lines(output));
            SqliteConnection.ClearAllPools();
            Assert.Equal(before, File.ReadAllBytes(MemoriesPath));
        }

        [Fact]
        public void Enrich_ThenReRun_ReportsZeroChanges()
        {
            ImportStandard();

            var (code, output, _) = Run("psn-enrich", "--workspace-folder", _workspace, DefinitionsFile());

            Assert.Equal(0, code);
            Assert.StartsWith("Enriched.\n", Lines(output), StringComparison.Ordinal);
            Assert.Contains("Definitions added: 4\n", Lines(output), StringComparison.Ordinal);
            Assert.Contains("Changes: 4\n", Lines(output), StringComparison.Ordinal);
            Assert.Equal(4, Scalar("SELECT COUNT(*) FROM ItemDefinition;"));

            (code, output, _) = Run("psn-enrich", "--workspace-folder", _workspace, DefinitionsFile());

            Assert.Equal(0, code);
            Assert.Contains("Definitions unchanged: 4\n", Lines(output), StringComparison.Ordinal);
            Assert.Contains("Changes: 0\n", Lines(output), StringComparison.Ordinal);
        }

        [Fact]
        public void Enrich_TypeMismatch_IsCountedAndNotStored()
        {
            ImportStandard();
            var file = SyntheticTrophyDefinitions.Standard();
            file.Sets[1].Trophies[0] = file.Sets[1].Trophies[0] with { Type = "gold" };

            var (code, output, _) = Run("psn-enrich", "--workspace-folder", _workspace, DefinitionsFile(file));

            Assert.Equal(0, code);
            Assert.Contains("Items whose definition has another type: 1\n", Lines(output), StringComparison.Ordinal);
            Assert.Contains("Definitions not stored (type mismatch): 1\n", Lines(output), StringComparison.Ordinal);
            Assert.Contains("Items without a definition: 1\n", Lines(output), StringComparison.Ordinal);
            Assert.Equal(3, Scalar("SELECT COUNT(*) FROM ItemDefinition;"));
        }

        [Fact]
        public void Enrich_FormatOneFile_ReportsTheUpgrade()
        {
            ImportStandard();

            // Turn the new file back into the format 1 layout (marker check and no definition table).
            using (var connection = new SqliteConnection($"Data Source={MemoriesPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE MemoriesInfoOld (
                        MemoriesInfoID INTEGER PRIMARY KEY, Purpose TEXT NOT NULL, FormatVersion INTEGER NOT NULL,
                        CreatedDateTimeUtc TEXT NOT NULL, CHECK (FormatVersion = 1));
                    INSERT INTO MemoriesInfoOld SELECT MemoriesInfoID, Purpose, 1, CreatedDateTimeUtc FROM MemoriesInfo;
                    DROP TABLE MemoriesInfo;
                    ALTER TABLE MemoriesInfoOld RENAME TO MemoriesInfo;
                    DROP TABLE ItemDefinition;
                    DROP TABLE EntryImage;
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            var (_, dry, _) = Run("psn-enrich-dry-run", "--workspace-folder", _workspace, DefinitionsFile());
            Assert.Contains("The memories file is format 1; psn-enrich upgrades it to 3.\n", Lines(dry), StringComparison.Ordinal);
            Assert.Contains("Changes: 5\n", Lines(dry), StringComparison.Ordinal);

            var (code, output, _) = Run("psn-enrich", "--workspace-folder", _workspace, DefinitionsFile());

            Assert.Equal(0, code);
            Assert.Contains("Upgraded the memories file from format 1 to 3.\n", Lines(output), StringComparison.Ordinal);
            Assert.Contains("Changes: 5\n", Lines(output), StringComparison.Ordinal);
            Assert.Equal(3, Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
        }

        [Fact]
        public void Enrich_BadFile_StopsWithIndexesOnly_AndWritesNothing()
        {
            ImportStandard();
            var file = SyntheticTrophyDefinitions.Standard();
            file.Tamper = root => root["sets"]![1]!["trophies"]![0]!.AsObject().Remove("trophyHidden");

            var (code, _, error) = Run("psn-enrich", "--workspace-folder", _workspace, DefinitionsFile(file));

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("Set at index 1, trophy at index 0", error, StringComparison.Ordinal);
            Assert.Equal(0, Scalar("SELECT COUNT(*) FROM ItemDefinition;"));
        }

        [Fact]
        public void Enrich_OtherFormat_IsRefused()
        {
            ImportStandard();
            var file = SyntheticTrophyDefinitions.Standard();
            file.Format = "something-else";

            var (code, _, error) = Run("psn-enrich-dry-run", "--workspace-folder", _workspace, DefinitionsFile(file));

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("format", error, StringComparison.Ordinal);
        }

        [Fact]
        public void Enrich_WithoutAMemoriesFile_StopsAndCreatesNothing()
        {
            var (code, _, error) = Run("psn-enrich", "--workspace-folder", _workspace, DefinitionsFile());

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Contains("psn-import", error, StringComparison.Ordinal);
            Assert.False(File.Exists(MemoriesPath));
        }

        [Theory]
        [InlineData("psn-enrich", "--local", "alpha")]
        [InlineData("psn-enrich", "--create")]
        [InlineData("psn-enrich-dry-run", "--max-bytes", "5")]
        [InlineData("psn-enrich-dry-run", "--gap", "30")]
        public void Enrich_RefusesOtherArguments(string verb, params string[] extra)
        {
            var (code, _, error) = Run([verb, "--workspace-folder", _workspace, DefinitionsFile(), .. extra]);

            Assert.Equal(MemoryImportCommand.UsageError, code);
            Assert.StartsWith("Usage error:", error, StringComparison.Ordinal);
        }

        [Fact]
        public void Enrich_NeedsTheWorkspaceFolderAndAFile()
        {
            Assert.Equal(MemoryImportCommand.UsageError, Run("psn-enrich", DefinitionsFile()).Code);
            Assert.Equal(MemoryImportCommand.UsageError, Run("psn-enrich-dry-run", "--workspace-folder", _workspace).Code);
        }

        [Fact]
        public void FetchImages_SplitsGameImagesAndTrophyIcons_AndFetchesOnlyUsedIcons()
        {
            ImportStandard();
            Run("psn-enrich", "--workspace-folder", _workspace, DefinitionsFile());
            var source = new FakeSource(new()
            {
                [Quest.Icon!] = Png,
                [Racer.Icon!] = Png,
                [SyntheticTrophyDefinitions.QuestIconOne] = Png,
                [SyntheticTrophyDefinitions.QuestIconTwo] = Png,
                [SyntheticTrophyDefinitions.QuestIconThree] = Png,
            });

            var (code, output, _) = RunWith(source, "fetch-images", "--workspace-folder", _workspace);

            Assert.Equal(MemoryImportCommand.Failed, code);
            Assert.Equal(
                "Game images.\n  Fetched: 2\n  Already present: 0\n  Failed: 0\n" +
                "Trophy icons.\n  Fetched: 2\n  Already present: 0\n  Failed: 1\n",
                Lines(output));
            Assert.Equal(0, Scalar("SELECT COUNT(*) FROM ItemDefinition WHERE ItemId = '3' AND IconSha256 IS NOT NULL;"));
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
