using System.Reflection;
using System.Security.Cryptography;
using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport.Tests;

/// <summary><c>migrate</c> (CH-71: version 5 to 6) on synthetic workspaces only.</summary>
public sealed class MigrateTests
{
    /// <summary>Turns a fresh (current, version 6) synthetic workspace into an exact version 5 one.</summary>
    internal static void MakeVersionFive(SyntheticData d, string extraSql = "")
    {
        d.Workspace("DROP TABLE AttachmentCaptionFact; PRAGMA user_version = 5;" + extraSql);
        d.CloseAll();
    }

    /// <summary>Turns a fresh synthetic workspace into an exact version 4 one.</summary>
    internal static void MakeVersionFour(SyntheticData d, string extraSql = "")
    {
        d.Workspace("DROP TABLE AttachmentCaptionFact; DROP TABLE MessageMsgstoreFact; PRAGMA user_version = 4;" + extraSql);
        d.CloseAll();
    }

    /// <summary>Turns a fresh synthetic workspace into an exact version 3 one.</summary>
    internal static void MakeVersionThree(SyntheticData d, string extraSql = "")
    {
        d.Workspace("DROP TABLE AttachmentCaptionFact; DROP TABLE MessageMsgstoreFact; DROP TABLE AttachmentMsgstoreFact; PRAGMA user_version = 3;" + extraSql);
        d.CloseAll();
    }

    internal static string Hash(string path)
    {
        SqliteConnection.ClearAllPools();

        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static (int Exit, string Out, string Err) Migrate(string path, bool apply) =>
        Scenario.Cli(apply ? ["migrate", "--workspace", path, "--apply"] : ["migrate", "--workspace", path]);

    private static int Version(string path)
    {
        try
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(path);

            return WorkspaceDatabase.ReadSchemaVersion(connection);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static string[] ProductList(string name) =>
        (string[])typeof(WorkspaceDatabase).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [Fact]
    public void Gate_lists_match_the_product_version_five_gate_exactly()
    {
        static string ProductName(string name) =>
            (string)typeof(WorkspaceDatabase).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        var tables = ProductList("VersionTwoTableNames").Concat(ProductList("VersionThreeTableNames"))
            .Append(ProductName("MsgstoreFactTableName"))
            .Append(ProductName("MessageMsgstoreFactTableName"));
        var indexes = ProductList("VersionThreeIndexNames").Concat(ProductList("VersionFourIndexNames"))
            .Concat(ProductList("VersionFiveIndexNames"));

        Assert.Equal(tables.Order(StringComparer.Ordinal), WorkspaceMigrator.VersionFiveTables.Order(StringComparer.Ordinal));
        Assert.Equal(indexes.Order(StringComparer.Ordinal), WorkspaceMigrator.VersionFiveIndexes.Order(StringComparer.Ordinal));
        Assert.Equal(6, WorkspaceDatabase.SchemaVersion);
        Assert.Equal(5, WorkspaceMigrator.FromVersion);
    }

    [Fact]
    public void Missing_path_creates_nothing_in_either_mode()
    {
        using var d = new SyntheticData();
        d.CloseAll();
        var path = Path.Combine(d.DirectoryPath, "no-such-workspace.db");

        foreach (var apply in new[] { false, true })
        {
            var (exit, stdout, stderr) = Migrate(path, apply);

            Assert.Equal(2, exit);
            Assert.Equal("", stdout);
            Assert.Contains("There is no file at the --workspace path. Nothing was opened or created.", stderr);
            Assert.DoesNotContain(d.DirectoryPath, stderr);
        }

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".lock"));
    }

    [Fact]
    public void Version_six_is_refused_and_unchanged()
    {
        using var d = new SyntheticData();
        d.CloseAll();
        var before = Hash(d.WorkspacePath);

        foreach (var apply in new[] { false, true })
        {
            var (exit, _, stderr) = Migrate(d.WorkspacePath, apply);

            Assert.Equal(2, exit);
            Assert.Contains("already schema version 6", stderr);
        }

        Assert.Equal(before, Hash(d.WorkspacePath));
    }

    [Fact]
    public void Version_three_is_refused_with_its_own_message_and_unchanged()
    {
        using var d = new SyntheticData();
        MakeVersionThree(d);
        var before = Hash(d.WorkspacePath);

        foreach (var apply in new[] { false, true })
        {
            var (exit, stdout, stderr) = Migrate(d.WorkspacePath, apply);

            Assert.Equal(2, exit);
            Assert.Equal("", stdout);
            Assert.Contains("schema version 3. migrate takes only a version 5 workspace", stderr);
            Assert.Contains("product's own migration path", stderr);
        }

        Assert.Equal(before, Hash(d.WorkspacePath));
        Assert.Equal(3, Version(d.WorkspacePath));
        Assert.False(File.Exists(d.WorkspacePath + ".lock"));
    }

    [Fact]
    public void Version_four_is_refused_with_the_older_message_and_unchanged()
    {
        using var d = new SyntheticData();
        MakeVersionFour(d);
        var before = Hash(d.WorkspacePath);

        foreach (var apply in new[] { false, true })
        {
            var (exit, stdout, stderr) = Migrate(d.WorkspacePath, apply);

            Assert.Equal(2, exit);
            Assert.Equal("", stdout);
            Assert.Contains("schema version 4. migrate takes only a version 5 workspace", stderr);
        }

        Assert.Equal(before, Hash(d.WorkspacePath));
        Assert.Equal(4, Version(d.WorkspacePath));
    }

    [Fact]
    public void Other_versions_are_refused()
    {
        using var d = new SyntheticData();
        d.Workspace("PRAGMA user_version = 9;");
        d.CloseAll();

        var (exit, _, stderr) = Migrate(d.WorkspacePath, apply: true);

        Assert.Equal(2, exit);
        Assert.Contains("reports schema version 9. migrate takes only a version 5 workspace.", stderr);
        Assert.Equal(9, Version(d.WorkspacePath));
    }

    [Fact]
    public void Dry_run_on_version_five_reports_the_gate_and_writes_nothing()
    {
        using var d = new SyntheticData();
        MakeVersionFive(d);
        var before = Hash(d.WorkspacePath);

        var (exit, stdout, stderr) = Migrate(d.WorkspacePath, apply: false);

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);
        Assert.Matches(@"schema version before\s+5", stdout);
        Assert.Matches(@"v5-to-v6 shape gate\s+PASS", stdout);
        Assert.Matches(@"missing tables or indexes\s+0", stdout);
        Assert.Matches(@"unexpected tables or indexes\s+0", stdout);
        Assert.Contains("Dry run: nothing was written.", stdout);
        Assert.DoesNotContain("schema version after", stdout);
        Assert.DoesNotContain(d.DirectoryPath, stdout);

        Assert.Equal(before, Hash(d.WorkspacePath));
        Assert.Equal(5, Version(d.WorkspacePath));
        Assert.False(File.Exists(d.WorkspacePath + ".lock"));
    }

    [Fact]
    public void Apply_migrates_version_five_to_six()
    {
        using var d = new SyntheticData();
        var text = d.AddWorkspaceMessage("09:00:00", false);
        d.AddAttachment("09:01:00", false);
        d.Workspace(
            """
            INSERT INTO AttachmentMsgstoreFact (AttachmentID, IsForwarded, ForwardScore, SourceBatch, RecordedDateTimeUtc)
            VALUES (1, 1, 3, 'abcdef123456', '2026-10-01T00:00:00.0000000Z');
            """);
        d.Workspace(
            $"""
            INSERT INTO MessageMsgstoreFact (MessageID, IsForwarded, ForwardScore, SourceBatch, RecordedDateTimeUtc)
            VALUES ({text}, 0, NULL, 'fedcba654321', '2026-10-01T00:00:00.0000000Z');
            """);
        MakeVersionFive(d);
        var tablesBefore = d.DumpExcept();

        var (exit, stdout, stderr) = Migrate(d.WorkspacePath, apply: true);

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);
        Assert.Matches(@"schema version before\s+5", stdout);
        Assert.Matches(@"v5-to-v6 shape gate\s+PASS", stdout);
        Assert.Matches(@"schema version after\s+6", stdout);
        Assert.Contains("Migrated to version 6 in one transaction.", stdout);

        Assert.Equal(6, Version(d.WorkspacePath));

        // Additive: every version-5 table's rows are as they were (the CH-10 and CH-22 facts too); the new table is empty.
        var after = d.DumpExcept("AttachmentCaptionFact");
        Assert.Equal(tablesBefore.Keys.Order(), after.Keys.Order());

        foreach (var (table, rows) in tablesBefore)
        {
            Assert.Equal(rows, after[table]);
        }

        Assert.Equal(1, d.Count("SELECT COUNT(*) FROM AttachmentMsgstoreFact;"));
        Assert.Equal(1, d.Count("SELECT COUNT(*) FROM MessageMsgstoreFact;"));
        Assert.Equal(0, d.Count("SELECT COUNT(*) FROM AttachmentCaptionFact;"));

        using (var connection = WorkspaceDatabase.OpenReadOnlyConnection(d.WorkspacePath))
        {
            Assert.Single(new MessageMsgstoreFactReader().Read(connection, [text]));
            Assert.Single(new MsgstoreFactReader().Read(connection, [1]).Facts);
            Assert.Empty(new AttachmentCaptionFactReader().Read(connection, [1]));
        }

        SqliteConnection.ClearAllPools();

        // A second run refuses: it is version 6 now.
        var (again, _, againErr) = Migrate(d.WorkspacePath, apply: true);
        Assert.Equal(2, again);
        Assert.Contains("already schema version 6", againErr);
    }

    [Fact]
    public void Malformed_version_five_is_refused_and_the_file_is_unchanged()
    {
        using var d = new SyntheticData();
        MakeVersionFive(d, "CREATE TABLE Stray (Id INTEGER PRIMARY KEY);");
        var before = Hash(d.WorkspacePath);

        var (dryExit, dryOut, _) = Migrate(d.WorkspacePath, apply: false);

        Assert.Equal(1, dryExit);
        Assert.Matches(@"v5-to-v6 shape gate\s+FAIL", dryOut);
        Assert.Matches(@"unexpected tables or indexes\s+1", dryOut);
        Assert.DoesNotContain("Stray", dryOut);

        var (exit, stdout, stderr) = Migrate(d.WorkspacePath, apply: true);

        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.Contains("does not have the version-5 shape (0 missing, 1 unexpected", stderr);
        Assert.Equal(before, Hash(d.WorkspacePath));
        Assert.Equal(5, Version(d.WorkspacePath));
    }

    [Fact]
    public void Version_five_already_holding_the_new_table_is_refused()
    {
        using var d = new SyntheticData();
        d.Workspace("PRAGMA user_version = 5;");
        d.CloseAll();
        var before = Hash(d.WorkspacePath);

        var (exit, _, stderr) = Migrate(d.WorkspacePath, apply: true);

        Assert.Equal(2, exit);
        Assert.Contains("(0 missing, 2 unexpected", stderr);
        Assert.Equal(before, Hash(d.WorkspacePath));
    }

    [Fact]
    public void Missing_index_is_refused_and_the_file_is_unchanged()
    {
        using var d = new SyntheticData();
        MakeVersionFive(d, "DROP INDEX IX_MessageMsgstoreFact_SourceBatch;");
        var before = Hash(d.WorkspacePath);

        var (exit, _, stderr) = Migrate(d.WorkspacePath, apply: true);

        Assert.Equal(2, exit);
        Assert.Contains("(1 missing, 0 unexpected", stderr);
        Assert.Equal(before, Hash(d.WorkspacePath));
    }

    [Fact]
    public void Held_lock_is_refused_and_the_file_is_unchanged()
    {
        using var d = new SyntheticData();
        MakeVersionFive(d);
        var before = Hash(d.WorkspacePath);

        using (WorkspaceExclusiveLock.Acquire(d.WorkspacePath))
        {
            foreach (var apply in new[] { false, true })
            {
                var (exit, stdout, stderr) = Migrate(d.WorkspacePath, apply);

                Assert.Equal(2, exit);
                Assert.Equal("", stdout);
                Assert.Contains("claimed by another operation", stderr);
            }
        }

        Assert.Equal(before, Hash(d.WorkspacePath));
        Assert.Equal(5, Version(d.WorkspacePath));

        // Released: it migrates.
        Assert.Equal(0, Migrate(d.WorkspacePath, apply: true).Exit);
        Assert.Equal(6, Version(d.WorkspacePath));
    }

    [Fact]
    public void Migrate_takes_only_workspace_and_apply()
    {
        var (exit, _, stderr) = Scenario.Cli(["migrate", "--workspace", "x.db", "--i-have-a-backup"]);

        Assert.Equal(2, exit);
        Assert.Contains("migrate takes only --workspace and --apply.", stderr);

        Assert.Equal(2, Scenario.Cli(["plan", "--workspace", "x.db", "--apply"]).Exit);
    }
}
