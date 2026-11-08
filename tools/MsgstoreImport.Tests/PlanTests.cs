using System.Globalization;
using System.Security.Cryptography;
using Chatcheology.Data.Workspace;

namespace MsgstoreImport.Tests;

public sealed class PlanTests
{
    [Fact]
    public void Plan_selects_exactly_the_hash_matched_headline_rows()
    {
        using var s = new Scenario();

        var result = s.Plan();
        var manifest = Manifest.Parse(File.ReadAllBytes(s.ManifestPath));

        Assert.Equal(
            s.ExpectedManifest().OrderBy(r => r.Item1),
            manifest.Rows.Select(r => (r.AttachmentID, r.MediaAssetID, r.Kind)));

        Assert.Equal(Scenario.ExpectedRows, result.Rows);
        Assert.Equal(5, result.HashAndNameSize);
        Assert.Equal(2, result.HashOnly);
        Assert.All(manifest.Rows, r => Assert.StartsWith("SYNTHKEY", r.KeyID));
        Assert.Equal(new PairingParameters(1, SyntheticData.ChatID, SyntheticData.Offset, 1), manifest.Parameters);
    }

    [Fact]
    public void Plan_counts_every_exclusion_by_reason_and_reconciles()
    {
        using var s = new Scenario();

        var r = s.Plan();

        Assert.Equal(1, r.Excluded[Reasons.Exclusion(Eligibility.NameSizeOnly)]);
        Assert.Equal(1, r.Excluded[Reasons.Exclusion(Eligibility.Conflict)]);
        Assert.Equal(1, r.Excluded[Reasons.Exclusion(Eligibility.NotInCandidateSet)]);
        Assert.Equal(1, r.Excluded[Reasons.Exclusion(Eligibility.PairedNoAsset)]);
        Assert.Equal(2, r.Excluded[Reasons.Exclusion(Eligibility.FewerRecordsInKey)]);
        Assert.Equal(1, r.Excluded[Reasons.Exclusion(Eligibility.MoreRecordsInKey)]);
        Assert.Equal(1, r.Excluded[Reasons.Exclusion(Eligibility.NoKeyID)]);
        Assert.Equal(1, r.Excluded[Reasons.AlreadyResolved]);
        Assert.Equal(9, r.Excluded.Total);

        Assert.Equal(Scenario.ExpectedAttachments, r.Attachments);
        Assert.Equal(r.Attachments, r.Rows + r.Excluded.Total);
    }

    [Fact]
    public void Plan_name_size_only_row_is_excluded_even_though_the_census_counts_it()
    {
        using var s = new Scenario();

        var r = s.Plan();
        var manifest = Manifest.Parse(File.ReadAllBytes(s.ManifestPath));

        Assert.Equal(1, r.HeadlineByCategory[MatchCategory.NameSizeOnly]);
        Assert.DoesNotContain(manifest.Rows, row => row.AttachmentID == s.NameSizeOnly);
        Assert.DoesNotContain(manifest.Rows, row => row.AttachmentID == s.Conflict);
        Assert.DoesNotContain(manifest.Rows, row => row.AttachmentID == s.Resolved);
    }

    [Fact]
    public void Plan_busy_minute_pairs_by_sort_id_and_directions_pair_separately()
    {
        using var s = new Scenario();

        s.Plan();
        var rows = Manifest.Parse(File.ReadAllBytes(s.ManifestPath)).Rows.ToDictionary(r => r.AttachmentID);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(s.BusyAssets[i].ID, rows[s.Busy[i]].MediaAssetID);
        }

        Assert.Equal("SYNTHKEYBUSY1", rows[s.Busy[0]].KeyID);
        Assert.Equal(s.DirectionAssets[0].ID, rows[s.Direction[0]].MediaAssetID);
        Assert.Equal(s.DirectionAssets[1].ID, rows[s.Direction[1]].MediaAssetID);
    }

    [Fact]
    public void Plan_excludes_an_asset_disproven_for_the_attachment()
    {
        using var s = new Scenario();

        s.Data.Disprove(s.Both, s.BothAsset.ID);
        s.Data.CloseAll();

        var r = s.Plan();
        var manifest = Manifest.Parse(File.ReadAllBytes(s.ManifestPath));

        Assert.DoesNotContain(manifest.Rows, row => row.AttachmentID == s.Both);
        Assert.Equal(1, r.Excluded[Reasons.Exclusion(Eligibility.Disproven)]);
        Assert.Equal(Scenario.ExpectedRows - 1, r.Rows);
        Assert.Equal(r.Attachments, r.Rows + r.Excluded.Total);
    }

    [Fact]
    public void Plan_excludes_a_disproof_recorded_on_an_older_run()
    {
        using var s = new Scenario();

        // Disproven against run 1's candidate row; then a newer run supersedes run 1. The disproof
        // stays on run 1 only (dispositions are scoped to one run's candidate rows).
        var firstRun = s.Data.Count("SELECT MAX(RunID) FROM Phase7Run;");
        s.Data.Disprove(s.HashOnly, s.HashAsset.ID, firstRun);
        s.Data.RunPhase7();
        Assert.True(s.Data.Count("SELECT MAX(RunID) FROM Phase7Run;") > firstRun);
        Assert.Equal(0, s.Data.Count(
            $"""
            SELECT COUNT(*) FROM AttachmentCandidateDispositionEvent AS d
            INNER JOIN AttachmentCandidate AS c ON c.AttachmentCandidateID = d.AttachmentCandidateID
            WHERE c.RunID <> {firstRun};
            """));
        s.Data.CloseAll();

        var r = s.Plan();

        Assert.DoesNotContain(Manifest.Parse(File.ReadAllBytes(s.ManifestPath)).Rows, row => row.AttachmentID == s.HashOnly);
        Assert.Equal(1, r.Excluded[Reasons.Exclusion(Eligibility.Disproven)]);
    }

    [Fact]
    public void Plan_a_disproof_of_a_different_asset_does_not_exclude_the_row()
    {
        using var s = new Scenario();

        // Another candidate of the same attachment (same date, so in its candidate set).
        s.Data.Disprove(s.Both, s.HashAsset.ID);
        s.Data.CloseAll();

        var r = s.Plan();

        Assert.Contains(Manifest.Parse(File.ReadAllBytes(s.ManifestPath)).Rows,
            row => row.AttachmentID == s.Both && row.MediaAssetID == s.BothAsset.ID);
        Assert.Equal(0, r.Excluded[Reasons.Exclusion(Eligibility.Disproven)]);
        Assert.Equal(Scenario.ExpectedRows, r.Rows);
    }

    [Fact]
    public void Plan_refuses_a_manifest_inside_any_repository_by_its_own_location()
    {
        using var s = new Scenario();

        var repo = Path.Combine(s.Data.DirectoryPath, "some-clone");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        Directory.CreateDirectory(Path.Combine(repo, "sub"));
        var inside = Path.Combine(repo, "sub", "manifest.tsv");

        var (exit, _, stderr) = Scenario.Cli(s.PlanArgs(inside));

        Assert.Equal(2, exit);
        Assert.Contains("inside a repository", stderr);
        Assert.False(File.Exists(inside));
    }

    [Fact]
    public void Unrecognised_argument_is_not_echoed()
    {
        var (exit, _, stderr) = Scenario.Cli(["plan", "--workspace", "x", "C:\\private\\secret-name.db"]);

        Assert.Equal(2, exit);
        Assert.Contains("Argument 4 is not a recognised option.", stderr);
        Assert.DoesNotContain("secret-name", stderr);
    }

    [Fact]
    public void Plan_is_read_only_on_both_databases()
    {
        using var s = new Scenario();

        var workspaceBefore = Sha(s.Data.WorkspacePath);
        var msgstoreBefore = Sha(s.Data.MsgstorePath);
        var filesBefore = Directory.GetFiles(s.Data.DirectoryPath).Order().ToList();

        s.Plan();

        Assert.Equal(workspaceBefore, Sha(s.Data.WorkspacePath));
        Assert.Equal(msgstoreBefore, Sha(s.Data.MsgstorePath));
        Assert.Equal(
            filesBefore.Append(s.ManifestPath).Order(),
            Directory.GetFiles(s.Data.DirectoryPath).Order());
    }

    [Fact]
    public void Plan_stdout_carries_counts_only()
    {
        using var s = new Scenario();

        var (exit, stdout, stderr) = Scenario.Cli(s.PlanArgs());

        Assert.Equal(0, exit);
        Assert.Equal("", stderr);

        var manifestText = File.ReadAllText(s.ManifestPath);
        var sha = Manifest.Sha256Hex(File.ReadAllBytes(s.ManifestPath));

        Assert.Contains("manifest SHA-256: " + sha, stdout);
        Assert.DoesNotContain(s.Data.DirectoryPath, stdout);
        Assert.DoesNotContain("IMG-", stdout);
        Assert.DoesNotContain(".jpg", stdout);

        foreach (var key in s.KeyIDs)
        {
            Assert.DoesNotContain(key, stdout);
        }

        // The manifest, by contrast, holds the identifiers.
        Assert.Contains("SYNTHKEYBOTH", manifestText);
    }

    [Fact]
    public void Plan_refuses_an_existing_manifest_and_leaves_it_alone()
    {
        using var s = new Scenario();

        File.WriteAllText(s.ManifestPath, "already here");

        var (exit, _, stderr) = Scenario.Cli(s.PlanArgs());

        Assert.Equal(2, exit);
        Assert.Contains("never overwritten", stderr);
        Assert.Equal("already here", File.ReadAllText(s.ManifestPath));
    }

    [Fact]
    public void Plan_refuses_a_manifest_inside_the_repository()
    {
        using var s = new Scenario();

        var inside = Path.Combine(AppContext.BaseDirectory, "manifest-" + Guid.NewGuid().ToString("N") + ".tsv");
        var (exit, _, stderr) = Scenario.Cli(s.PlanArgs(inside));

        Assert.Equal(2, exit);
        Assert.Contains("inside the repository", stderr);
        Assert.False(File.Exists(inside));
    }

    [Fact]
    public void Plan_refuses_a_msgstore_without_key_id()
    {
        using var s = new Scenario();

        s.Data.Msgstore("ALTER TABLE message DROP COLUMN key_id;");
        s.Data.CloseAll();

        var (exit, _, stderr) = Scenario.Cli(s.PlanArgs());

        Assert.Equal(2, exit);
        Assert.Contains("key_id", stderr);
        Assert.False(File.Exists(s.ManifestPath));
    }

    // -------------------------------------------------------------------------------------------

    internal static string Sha(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public void Synthetic_workspace_is_at_the_current_schema_version()
    {
        using var s = new Scenario();
        using var connection = WorkspaceDatabase.OpenReadOnlyConnection(s.Data.WorkspacePath);

        Assert.Equal(WorkspaceDatabase.SchemaVersion, WorkspaceDatabase.ReadSchemaVersion(connection));
    }
}
