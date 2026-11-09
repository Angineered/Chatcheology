using System.Security.Cryptography;
using Chatcheology.Archive;
using Chatcheology.Data.ArchiveExport;
using Chatcheology.Data.Memories;
using Chatcheology.Data.NewerMessages;
using Microsoft.Data.Sqlite;
using MsgstoreImport.Tests;

namespace ArchiveRefresh.Tests;

/// <summary>
/// The archive refresh (CH-71 step 5) on synthetic data only: plan read-only, the combined SHA-256, apply refusing a stale
/// plan, the backup before the first write, stop and rerun, and the whole month end to end into a validated archive.
/// </summary>
public sealed class RefreshTests : IDisposable
{
    private readonly RefreshFixture _f = new();

    public void Dispose() => _f.Dispose();

    private static string Line(string report, string label) =>
        report.Split('\n').First(l => l.TrimStart().StartsWith(label, StringComparison.Ordinal)).Trim();

    private string NewerPath => NewerMessagesDatabase.PathBeside(_f.D.WorkspacePath);

    private static ArchiveCounts Counts(string archive) =>
        ArchiveManifest.Parse(File.ReadAllBytes(Path.Combine(archive, ArchiveFormat.ManifestFileName))).Counts;

    // -------------------------------------------------------------------------------------------
    // Plan.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Plan_changes_no_input_and_prints_counts_only()
    {
        var before = _f.Snapshot();

        var plan = _f.Plan();

        Assert.True(plan.Exit == 0, plan.Err);
        Assert.Matches("^[0-9a-f]{64}$", plan.Sha);
        Assert.Equal(before, _f.Snapshot());
        Assert.False(File.Exists(NewerPath), "plan must not create newer-messages.db");
        Assert.False(File.Exists(ArchiveIdFile.PathBeside(_f.D.WorkspacePath)), "plan must not create the archive id");

        // The plan folder holds the plan file and every sub-manifest; only the plan SHA-256 is printed.
        Assert.True(File.Exists(Path.Combine(plan.Folder, RefreshPlanFile.FileName)));
        Assert.True(File.Exists(Path.Combine(plan.Folder, RefreshPlanFile.NewerManifest)));
        Assert.True(File.Exists(Path.Combine(plan.Folder, RefreshPlanFile.CaptionsManifest)));
        Assert.True(File.Exists(Path.Combine(plan.Folder, RefreshPlanFile.PsnBuilt)));
        Assert.DoesNotContain("manifest SHA-256:", plan.Out, StringComparison.Ordinal);
        Assert.Equal(plan.Sha, RefreshFiles.Sha256Hex(File.ReadAllBytes(Path.Combine(plan.Folder, RefreshPlanFile.FileName))));

        Assert.EndsWith("3", Line(plan.Out, "new messages"));
        Assert.EndsWith("1", Line(plan.Out, "Restored (SHA-256"));
        Assert.EndsWith("1", Line(plan.Out, "paired attachments with a caption (manifest rows)"));
        Assert.EndsWith("1", Line(plan.Out, "events only in the built file (new)"));
        Assert.EndsWith("0", Line(plan.Out, "events only in the current file"));
        Assert.DoesNotContain("ATTENTION", plan.Out, StringComparison.Ordinal);

        // The plan file holds ids and digests only.
        var planText = File.ReadAllText(Path.Combine(plan.Folder, RefreshPlanFile.FileName));
        Assert.DoesNotContain(_f.D.DirectoryPath, planText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alpha", planText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SYNTH", planText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_inputs_give_the_same_plan_SHA_and_any_changed_input_another()
    {
        var first = _f.Plan();
        var again = _f.Plan();
        Assert.True(first.Exit == 0, first.Err);
        Assert.Equal(first.Sha, again.Sha);

        var seen = new HashSet<string> { first.Sha };

        void Changed(string what, Action change)
        {
            change();
            SqliteConnection.ClearAllPools();
            var plan = _f.Plan();
            Assert.True(plan.Exit == 0, what + ": " + plan.Err);
            Assert.True(seen.Add(plan.Sha), what + " did not change the plan SHA-256");
        }

        // A read-only input whose content changes without changing any sub-manifest still changes the SHA.
        Changed("definitions file", () => File.AppendAllText(_f.Definitions!, " "));
        Changed("PSN timestamps", () => File.AppendAllText(_f.Timestamps!, " "));
        Changed("PSN game list", () => File.AppendAllText(_f.GameList!, " "));
        Changed("current PSN file", () => File.AppendAllText(_f.Current!, " "));
        Changed("msgstore", () => _f.D.Msgstore("CREATE TABLE invented_unused (x INTEGER);"));
        Changed("workspace", () => _f.D.Workspace("UPDATE ImportSource SET DisplayName = 'Synthetic export, renamed';"));
        Changed("memories.db", () => new Chatcheology.Data.Memories.Psn.PsnImporter().Import(
            _f.MemoriesPath,
            Chatcheology.Data.Memories.Psn.PsnSharedActivityReader.Read(
                _f.Current!, Chatcheology.Tools.MemoryImport.Tests.SyntheticTrophyBatch.AccountA,
                Chatcheology.Tools.MemoryImport.Tests.SyntheticTrophyBatch.AccountB,
                Chatcheology.Tools.MemoryImport.Tests.SyntheticTrophyBatch.AccountA),
            "Invented second collection"));
        Changed("archive id file", () => ArchiveIdFile.ReadOrCreate(_f.D.WorkspacePath, SyntheticData.ConversationID));

        Changed("a media file", () => File.WriteAllText(
            Path.Combine(_f.MediaRoot, "Media", "WhatsApp Images", "IMG-SYNTH-newer.jpg"), "invented other bytes"));
    }

    [Fact]
    public void Plan_refuses_unexpected_restored_counts_and_leaves_no_plan_folder()
    {
        var plan = _f.Plan(expectRestored: 3);

        Assert.Equal(2, plan.Exit);
        Assert.Contains("--expect-restored is 3", plan.Err, StringComparison.Ordinal);
        Assert.False(Directory.Exists(plan.Folder));
    }

    // -------------------------------------------------------------------------------------------
    // Apply refusals.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Apply_refuses_a_stale_or_wrong_SHA_and_a_changed_input_and_writes_nothing()
    {
        var plan = _f.Plan();

        var wrong = _f.Apply(plan.Folder, new string('0', 64), _f.NewArchiveOut());
        Assert.Equal(2, wrong.Exit);
        Assert.Contains("does not match --plan-sha256", wrong.Err, StringComparison.Ordinal);

        // The plan file edited after approval: its SHA-256 no longer matches.
        File.AppendAllText(Path.Combine(plan.Folder, RefreshPlanFile.FileName), "# edited\n");
        var edited = _f.Apply(plan.Folder, plan.Sha, _f.NewArchiveOut());
        Assert.Equal(2, edited.Exit);
        Assert.Contains("does not match --plan-sha256", edited.Err, StringComparison.Ordinal);

        // An input changed after the plan: the plan is stale.
        var fresh = _f.Plan();
        _f.D.Msgstore("CREATE TABLE invented_unused (x INTEGER);");
        var stale = _f.Apply(fresh.Folder, fresh.Sha, _f.NewArchiveOut());
        Assert.Equal(2, stale.Exit);
        Assert.Contains("differ from the plan. Plan again", stale.Err, StringComparison.Ordinal);

        var workspaceChanged = _f.Plan();
        _f.D.Workspace("UPDATE ImportSource SET DisplayName = 'Synthetic export, renamed';");
        var changed = _f.Apply(workspaceChanged.Folder, workspaceChanged.Sha, _f.NewArchiveOut());
        Assert.Equal(2, changed.Exit);
        Assert.Contains("changed since the plan", changed.Err, StringComparison.Ordinal);

        // A sub-manifest changed in the plan folder.
        var tampered = _f.Plan();
        File.AppendAllText(Path.Combine(tampered.Folder, RefreshPlanFile.NewerManifest), "\n");
        var subManifest = _f.Apply(tampered.Folder, tampered.Sha, _f.NewArchiveOut());
        Assert.Equal(2, subManifest.Exit);
        Assert.Contains("sub-manifest", subManifest.Err, StringComparison.Ordinal);

        Assert.False(File.Exists(NewerPath));
        Assert.Empty(Directory.GetDirectories(_f.BackupRoot));
        Assert.Empty(Directory.GetDirectories(_f.Private, "archive-*"));
    }

    [Fact]
    public void Apply_refuses_other_paths_than_the_plan_s()
    {
        var plan = _f.Plan();
        var otherRoot = Path.Combine(_f.D.DirectoryPath, "other-copy");
        Directory.CreateDirectory(otherRoot);

        var args = new List<string> { "apply" };
        args.AddRange(_f.InputArgs());
        args[args.IndexOf("--media-root") + 1] = otherRoot;
        args.AddRange(["--plan-folder", plan.Folder, "--plan-sha256", plan.Sha, "--backup-root", _f.BackupRoot, "--archive-out", _f.NewArchiveOut()]);

        var result = _f.Run(args);

        Assert.Equal(2, result.Exit);
        Assert.Contains("differs from the plan's", result.Err, StringComparison.Ordinal);
        Assert.False(File.Exists(NewerPath));
    }

    // -------------------------------------------------------------------------------------------
    // Backup, stop and rerun.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void The_backup_is_made_and_verified_before_the_first_write()
    {
        var originals = new Dictionary<string, string>
        {
            ["workspace.db"] = Sha(_f.D.WorkspacePath),
            [MemoriesDatabase.FileName] = Sha(_f.MemoriesPath),
        };

        var plan = _f.Plan();
        var checkedBackup = false;

        var hooks = _f.Hooks(before: step =>
        {
            if (step != RefreshApplier.StepNewer)
            {
                return;
            }

            // Before the first write: one backup folder, holding exactly the files there were, byte for byte.
            var folder = Assert.Single(Directory.GetDirectories(_f.BackupRoot));
            Assert.StartsWith("refresh-backup-", Path.GetFileName(folder), StringComparison.Ordinal);

            foreach (var (name, sha) in originals)
            {
                Assert.Equal(sha, Sha(Path.Combine(folder, name)));
            }

            Assert.False(File.Exists(Path.Combine(folder, NewerMessagesDatabase.FileName)));
            Assert.False(File.Exists(NewerPath), "nothing may be written before the backup");

            var contents = File.ReadAllText(Path.Combine(folder, RefreshApplier.BackupContentsFile));
            Assert.Contains(NewerMessagesDatabase.FileName + "\tabsent", contents, StringComparison.Ordinal);
            Assert.Contains(ArchiveIdFile.FileName + "\tabsent", contents, StringComparison.Ordinal);
            Assert.Contains(MemoriesDatabase.FileName + "\t", contents, StringComparison.Ordinal);
            checkedBackup = true;
        });

        var apply = _f.Apply(plan.Folder, plan.Sha, _f.NewArchiveOut(), hooks);

        Assert.True(apply.Exit == 0, apply.Err + apply.Out);
        Assert.True(checkedBackup);
        Assert.Contains("2 file(s) copied and verified (size and SHA-256)", apply.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_backup_folder_that_exists_already_is_refused_before_anything_is_written()
    {
        var plan = _f.Plan();
        Directory.CreateDirectory(Path.Combine(_f.BackupRoot, "refresh-backup-20261109-100000"));

        var apply = _f.Apply(plan.Folder, plan.Sha, _f.NewArchiveOut(), _f.Hooks());

        Assert.Equal(2, apply.Exit);
        Assert.Contains("already exists", apply.Err, StringComparison.Ordinal);
        Assert.False(File.Exists(NewerPath));
    }

    [Fact]
    public void A_stop_between_steps_and_a_stop_inside_a_step_both_continue_safely_on_a_rerun()
    {
        var plan = _f.Plan();
        var archive = _f.NewArchiveOut();

        // 1. A refusal before the caption step: the newer messages are in, nothing else.
        var first = _f.Apply(plan.Folder, plan.Sha, archive, _f.Hooks(before: step =>
        {
            if (step == RefreshApplier.StepCaptions)
            {
                throw new RefreshRefusedException("invented stop for the test");
            }
        }));

        Assert.Equal(2, first.Exit);
        Assert.True(File.Exists(NewerPath));
        Assert.Equal(3, NewerMessagesReader.Read(NewerPath).Messages.Count);
        SqliteConnection.ClearAllPools();
        Assert.False(Directory.Exists(archive));

        // 2. A stop inside the PSN import, after its write but before it is recorded as done.
        var second = _f.Apply(plan.Folder, plan.Sha, archive, _f.Hooks(after: step =>
        {
            if (step == RefreshApplier.StepPsnImport)
            {
                throw new IOException("invented crash for the test");
            }
        }));

        Assert.Equal(1, second.Exit);
        Assert.False(Directory.Exists(archive));

        // 3. The rerun: newer messages and captions skipped, the PSN import rerun (already there: no change), then the rest.
        var third = _f.Apply(plan.Folder, plan.Sha, archive);

        Assert.True(third.Exit == 0, third.Err + third.Out);
        Assert.Contains("steps finished by an earlier apply (skipped)", third.Out, StringComparison.Ordinal);
        Assert.Contains(RefreshApplier.StepNewer + ", " + RefreshApplier.StepCaptions, third.Out, StringComparison.Ordinal);
        Assert.EndsWith("0", Line(third.Out, "records added"));
        Assert.Equal(3, NewerMessagesReader.Read(NewerPath).Messages.Count);
        SqliteConnection.ClearAllPools();
        Assert.Equal(3, Directory.GetDirectories(_f.BackupRoot).Length);

        var counts = Counts(archive);
        Assert.Equal(3, counts.MsgstoreMessages);
        Assert.Equal(2, counts.Captions);

        // 4. Once more after it all finished: nothing written, the same archive checked again.
        var fourth = _f.Apply(plan.Folder, plan.Sha, archive);
        Assert.True(fourth.Exit == 0, fourth.Err + fourth.Out);
        Assert.Equal(3, Directory.GetDirectories(_f.BackupRoot).Length);
    }

    [Fact]
    public void Something_else_changing_the_files_between_a_stop_and_the_rerun_is_refused()
    {
        var plan = _f.Plan();
        var archive = _f.NewArchiveOut();

        _f.Apply(plan.Folder, plan.Sha, archive, _f.Hooks(before: step =>
        {
            if (step == RefreshApplier.StepCaptions)
            {
                throw new RefreshRefusedException("invented stop for the test");
            }
        }));

        // The workspace changed by something other than this apply.
        _f.D.Workspace("UPDATE ImportSource SET DisplayName = 'Synthetic export, renamed';");

        var rerun = _f.Apply(plan.Folder, plan.Sha, archive);

        Assert.Equal(2, rerun.Exit);
        Assert.Contains("changed since the last apply stopped", rerun.Err, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------
    // End to end.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Plan_then_apply_gives_a_validated_version_4_archive_with_newer_messages_captions_and_PSN()
    {
        var plan = _f.Plan();
        Assert.True(plan.Exit == 0, plan.Err);

        var archive = _f.NewArchiveOut();
        var apply = _f.Apply(plan.Folder, plan.Sha, archive);

        Assert.True(apply.Exit == 0, apply.Err + apply.Out);
        Assert.Contains("The archive passed the validator and the comparison with the workspace", apply.Out, StringComparison.Ordinal);
        Assert.Contains("Copy-ArchiveToPhone.ps1 -ArchiveFolder '<the --archive-out folder>'", apply.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("FAILED", apply.Out, StringComparison.Ordinal);

        // The archive, checked again here independently.
        Assert.True(ArchiveValidator.Validate(archive).Passed);
        var comparison = new ArchiveWorkspaceComparer().Compare(new ArchiveComparisonRequest
        {
            WorkspacePath = _f.D.WorkspacePath,
            ConversationID = SyntheticData.ConversationID,
            LocalParticipantID = SyntheticData.LocalParticipantID,
            ArchivePath = archive,
        });
        Assert.True(comparison.Passed);
        SqliteConnection.ClearAllPools();

        var counts = Counts(archive);
        Assert.Equal(5, counts.WorkspaceMessages);
        Assert.Equal(3, counts.MsgstoreMessages);
        Assert.Equal(8, counts.Messages);
        Assert.Equal(2, counts.Captions);
        Assert.Equal(1, counts.Restored);
        Assert.Equal(2, Assert.Single(counts.Collections).Entries);

        using (var reader = ArchiveReader.Open(archive))
        {
            Assert.Equal(4, reader.Info.FormatVersion);
            Assert.Equal(ArchiveIdFile.Read(_f.D.WorkspacePath, SyntheticData.ConversationID), reader.Info.ArchiveId);

            var messages = reader.ReadAllPages().SelectMany(p => p.Messages).ToList();
            var newer = messages.Where(m => m.Source == ArchiveMessageSource.Msgstore).ToList();
            Assert.Equal(3, newer.Count);
            Assert.Contains(newer, m => m.Text == "Poll (not shown in this archive)");
            Assert.Contains(messages.SelectMany(m => m.Attachments), a => a.Caption == RefreshFixture.WorkspaceCaption);
            Assert.Contains(newer.SelectMany(m => m.Attachments), a => a.Caption == RefreshFixture.NewerCaption);
        }

        SqliteConnection.ClearAllPools();

        // Next month's plan from the same files: the hand-off continues after the last imported message, nothing new.
        var next = _f.Plan();
        Assert.True(next.Exit == 0, next.Err);
        Assert.Contains("later run, after the last imported message", next.Out, StringComparison.Ordinal);
        Assert.EndsWith("0", Line(next.Out, "new messages"));
        Assert.EndsWith("0", Line(next.Out, "paired attachments with a caption (manifest rows)"));
    }

    [Fact]
    public void Without_PSN_options_PSN_is_left_out_and_the_rest_runs()
    {
        using var f = new RefreshFixture(psn: false);
        var plan = f.Plan();
        Assert.True(plan.Exit == 0, plan.Err);
        Assert.Contains("Left out of this refresh", plan.Out, StringComparison.Ordinal);

        var archive = f.NewArchiveOut();
        var apply = f.Apply(plan.Folder, plan.Sha, archive);

        Assert.True(apply.Exit == 0, apply.Err + apply.Out);
        Assert.DoesNotContain("PSN import", apply.Out, StringComparison.Ordinal);
        Assert.Equal(3, Counts(archive).MsgstoreMessages);
    }

    private static string Sha(string path)
    {
        SqliteConnection.ClearAllPools();

        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }
}
