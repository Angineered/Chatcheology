using System.Globalization;
using System.Security.Cryptography;

namespace MsgstoreImport.Tests;

/// <summary>
/// CH-11 synthetic case: an unresolved SENT slot paired by the minute rule, whose msgstore record
/// carries a content hash that matches no inventoried file, until a new one-file folder (no Sent
/// folder, a -YYYYMMDD-WA name dated the slot's day) is added with add-source. The Phase 7 run and
/// the minute batch happen first, as on the real workspace.
/// </summary>
internal sealed class ExtraSourceScenario : IDisposable
{
    internal static readonly DateOnly A = SyntheticData.Day, B = A.AddDays(1);

    internal SyntheticData Data { get; } = new();

    internal long Slot, InSet, DayPaired;
    internal string SlotKey = "XKEYSLOT";
    internal string ExtraRoot => Path.Combine(Data.DirectoryPath, "extra-folder");
    internal string ExtraFileName { get; }
    internal byte[] ExtraBytes { get; }

    /// <param name="slotDay">The slot's day: <see cref="A"/> is usable; <see cref="B"/> has one extra workspace message.</param>
    internal ExtraSourceScenario(DateOnly slotDay)
    {
        var d = Data;

        // Day A, minute batch row: asset in the candidate set.
        InSet = d.AddAttachment("08:00:00", false, A);
        d.AddRecordFor(d.AddAsset(A), "08:00:00", false, "XKEYINSET", date: A);

        // Day A, outside row already there before the new source: day-paired, file 40 days older.
        DayPaired = d.AddAttachment("08:10:00", true, A);
        d.AddRecordFor(d.AddAsset(A.AddDays(-40)), "08:11:00", true, "XKEYDAYPAIRED", nameAndSize: false, date: A);

        // Day B is unusable: one workspace message with no msgstore counterpart.
        d.AddWorkspaceMessage("12:00:00", false, B);

        // The 1164-like slot: sent, minute-paired, record hash names a file no source holds yet.
        ExtraBytes = SyntheticBytes(30_971);
        ExtraFileName = $"IMG-{slotDay:yyyyMMdd}-WA0042.jpg";
        Slot = d.AddAttachment("09:00:00", outgoing: true, slotDay);
        d.AddRecord("09:00:30", fromMe: true, ExtraFileName, ExtraBytes.Length,
            Convert.ToBase64String(SHA256.HashData(ExtraBytes)), SlotKey, date: slotDay);

        d.RunPhase7();
        d.CloseAll();

        var minuteManifest = Path.Combine(d.DirectoryPath, "minute-batch.tsv");
        var sha = Planner.Plan(Options.Parse(Args("plan", "--manifest-out", minuteManifest))).Sha256;
        Applier.Apply(Options.Parse(
        [
            "apply", "--msgstore", d.MsgstorePath, "--workspace", d.WorkspacePath,
            "--manifest", minuteManifest, "--manifest-sha256", sha, "--i-have-a-backup",
        ]));

        // The new folder: one file directly in the root, no Sent folder anywhere.
        Directory.CreateDirectory(ExtraRoot);
        File.WriteAllBytes(Path.Combine(ExtraRoot, ExtraFileName), ExtraBytes);
    }

    internal static byte[] SyntheticBytes(int length, int seed = 11)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);

        return bytes;
    }

    internal string[] Args(string mode, params string[] extra) =>
    [
        mode, "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath, "--conversation", "1",
        "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture), "--utc-offset", SyntheticData.OffsetText,
        "--local-participant", "1", .. extra,
    ];

    internal string[] AddSourceArgs(string root, bool apply, string displayName = "Synthetic extra folder") =>
    [
        "add-source", "--workspace", Data.WorkspacePath, "--root", root, "--display-name", displayName,
        "--device", "Synthetic phone", .. apply ? new[] { "--apply" } : [],
    ];

    private int _plans;

    private string NextPath(string stem) =>
        Path.Combine(Data.DirectoryPath, $"{stem}-{(++_plans).ToString(CultureInfo.InvariantCulture)}.tsv");

    internal (string Minute, string Day, string Documents) EarlierShas() =>
    (
        Planner.Plan(Options.Parse(Args("plan", "--manifest-out", NextPath("minute")))).Sha256,
        DayPlanner.Plan(Options.Parse(Args("plan", "--rule", "day-sequence", "--manifest-out", NextPath("day")))).Sha256,
        DocumentsPlanner.Plan(Options.Parse(Args("plan", "--rule", "documents", "--manifest-out", NextPath("docs")))).Sha256
    );

    internal (OutsidePlanResult Result, List<ManifestRow> Rows, string Path) Outside()
    {
        var path = NextPath("outside");
        var result = OutsidePlanner.Plan(Options.Parse(Args("plan", "--rule", "outside-candidate-set", "--manifest-out", path)));

        return (result, MsgstoreImport.Manifest.Parse(File.ReadAllBytes(path)).Rows.ToList(), path);
    }

    internal static OutsideEligibility Eligibility(ExtraSourceScenario s, long attachment) =>
        Inputs.ComputeOutside(s.Data.MsgstorePath, s.Data.WorkspacePath, new PairingParameters(
            1, SyntheticData.ChatID, SyntheticData.Offset, 1))[attachment].Eligibility;

    public void Dispose() => Data.Dispose();
}

public sealed class AddSourceTests
{
    private static string Reason(OutsideEligibility e, DayVerdict? day = null) =>
        OutsideCandidateSet.Reason(new OutsideAssessment(0, e, day, false, false, null, null, null, null, null, null, null, null, null, null, 0, 0));

    [Fact]
    public void Dry_run_prints_counts_and_writes_nothing()
    {
        using var s = new ExtraSourceScenario(ExtraSourceScenario.A);
        Directory.CreateDirectory(Path.Combine(s.ExtraRoot, "Sent"));
        File.WriteAllBytes(Path.Combine(s.ExtraRoot, "Sent", "IMG-20260302-WA0099.jpg"), ExtraSourceScenario.SyntheticBytes(10, 3));
        File.WriteAllBytes(Path.Combine(s.ExtraRoot, "notes-undated.jpg"), ExtraSourceScenario.SyntheticBytes(20, 4));

        var before = File.ReadAllBytes(s.Data.WorkspacePath);
        var siblings = Directory.GetFiles(s.Data.DirectoryPath).Order().ToList();

        var (exit, stdout, stderr) = Scenario.Cli(s.AddSourceArgs(s.ExtraRoot, apply: false));

        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.Contains("dry run (nothing written", stdout);
        Assert.Matches(@"files found\s+3\r?\n", stdout);
        Assert.Matches(@"total bytes\s+31,001\r?\n", stdout);
        Assert.Matches(@"with a naming-derived date \(-YYYYMMDD-WA\)\s+2\r?\n", stdout);
        Assert.Matches(@"direction: sent \(under a Sent folder\)\s+1\r?\n", stdout);
        Assert.Matches(@"direction: received\s+2\r?\n", stdout);
        Assert.DoesNotContain("IMG-", stdout);
        Assert.DoesNotContain(s.Data.DirectoryPath, stdout);
        Assert.DoesNotContain("Synthetic extra folder", stdout);

        Assert.Equal(before, File.ReadAllBytes(s.Data.WorkspacePath));
        Assert.Equal(siblings, Directory.GetFiles(s.Data.DirectoryPath).Order().ToList());
        Assert.Equal(1, s.Data.Count("SELECT COUNT(*) FROM MediaSource;"));
    }

    [Fact]
    public void Apply_adds_one_source_with_its_files_and_hashes_them()
    {
        using var s = new ExtraSourceScenario(ExtraSourceScenario.A);
        var assets = s.Data.Count("SELECT COUNT(*) FROM MediaAsset;");

        var (exit, stdout, stderr) = Scenario.Cli(s.AddSourceArgs(s.ExtraRoot, apply: true));

        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.Contains("APPLIED", stdout);
        Assert.Matches(@"new media source id\s+2\r?\n", stdout);
        Assert.Matches(@"files added\s+1\r?\n", stdout);
        Assert.Matches(@"files hashed\s+1\r?\n", stdout);
        Assert.Matches(@"new assets\s+1\r?\n", stdout);
        Assert.Contains("COMPLETE", stdout);
        Assert.DoesNotContain(s.ExtraFileName, stdout);
        Assert.DoesNotContain(s.Data.DirectoryPath, stdout);
        Assert.DoesNotContain("Synthetic extra folder", stdout);
        Assert.DoesNotContain("Synthetic phone", stdout);

        Assert.Equal(
            "Synthetic extra folder,WhatsAppMediaDirectory,Synthetic phone",
            s.Data.Rows("SELECT DisplayName, SourceType, DeviceDescription FROM MediaSource WHERE MediaSourceID = 2;").Single());

        // One file, its naming-derived date read, no direction recorded (no Sent folder in the tree),
        // hashed to the bytes on disk and linked to one new asset.
        var hex = Convert.ToHexString(SHA256.HashData(s.ExtraBytes));
        Assert.Equal(
            $"{s.ExtraFileName},30971,2026-03-02,<null>,{hex}",
            s.Data.Rows("SELECT FileName, SizeBytes, FileDate, IsSent, SHA256 FROM MediaFile WHERE MediaSourceID = 2;").Single());
        Assert.Equal(assets + 1, s.Data.Count("SELECT COUNT(*) FROM MediaAsset;"));
        Assert.Equal(1, s.Data.Count(
            "SELECT COUNT(*) FROM MediaAssetFile AS af INNER JOIN MediaFile AS f ON f.MediaFileID = af.MediaFileID WHERE f.MediaSourceID = 2;"));
    }

    [Fact]
    public void Apply_refuses_an_overlapping_root_and_writes_nothing()
    {
        using var s = new ExtraSourceScenario(ExtraSourceScenario.A);
        Directory.CreateDirectory(Path.Combine(s.ExtraRoot, "inner"));
        File.WriteAllBytes(Path.Combine(s.ExtraRoot, "inner", "IMG-20260302-WA0100.jpg"), ExtraSourceScenario.SyntheticBytes(10, 5));

        Assert.Equal(0, Scenario.Cli(s.AddSourceArgs(s.ExtraRoot, apply: true)).Exit);
        var before = s.Data.DumpExcept();

        foreach (var root in new[] { s.ExtraRoot, Path.Combine(s.ExtraRoot, "inner"), s.Data.DirectoryPath })
        {
            var (exit, stdout, stderr) = Scenario.Cli(s.AddSourceArgs(root, apply: true, displayName: "Again"));

            Assert.Equal(2, exit);
            Assert.Equal("", stdout);
            Assert.StartsWith("Refused: ", stderr);
            Assert.Contains("overlaps MediaSource 2", stderr);
            Assert.DoesNotContain(s.Data.DirectoryPath, stderr);
        }

        Assert.Equal(before, s.Data.DumpExcept());
    }

    [Fact]
    public void An_empty_folder_is_refused_in_the_dry_run_and_on_apply()
    {
        using var s = new ExtraSourceScenario(ExtraSourceScenario.A);
        var empty = Path.Combine(s.Data.DirectoryPath, "empty-folder");
        Directory.CreateDirectory(Path.Combine(empty, "Sent"));
        var before = s.Data.DumpExcept();

        foreach (var apply in new[] { false, true })
        {
            var (exit, stdout, stderr) = Scenario.Cli(s.AddSourceArgs(empty, apply));

            Assert.Equal(2, exit);
            Assert.Equal("", stdout);
            Assert.StartsWith("Refused: ", stderr);
            Assert.Contains("no files", stderr);
        }

        var missing = Scenario.Cli(s.AddSourceArgs(Path.Combine(s.Data.DirectoryPath, "not-there"), apply: true));
        Assert.Equal(2, missing.Exit);
        Assert.Equal(before, s.Data.DumpExcept());
    }

    [Fact]
    public void Options_keep_add_source_separate_from_the_other_modes()
    {
        Assert.Throws<OptionsException>(() => Options.Parse(["add-source", "--workspace", "w.db", "--root", "r"]));
        Assert.Throws<OptionsException>(() => Options.Parse(["add-source", "--workspace", "w.db", "--display-name", "n"]));
        Assert.Throws<OptionsException>(() => Options.Parse(
            ["add-source", "--workspace", "w.db", "--root", "r", "--display-name", "n", "--msgstore", "m.db"]));
        Assert.Throws<OptionsException>(() => Options.Parse(
            ["add-source", "--workspace", "w.db", "--root", "r", "--display-name", "n", "--i-have-a-backup"]));
        Assert.Throws<OptionsException>(() => Options.Parse(
            ["plan", "--msgstore", "m", "--workspace", "w", "--conversation", "1", "--chat", "7", "--utc-offset", "+02:00",
             "--local-participant", "1", "--manifest-out", "x", "--apply"]));

        var o = Options.Parse(["add-source", "--workspace", "w.db", "--root", "r", "--display-name", "n"]);
        Assert.Equal(Mode.AddSource, o.Mode);
        Assert.False(o.ApplySource);
        Assert.Null(o.SourceDevice);
    }

    /// <summary>Open point 2a: the asset's direction is not read by the outside rule or its pairing.</summary>
    [Fact]
    public void A_sent_slot_whose_file_only_exists_in_the_new_undirected_source_comes_out_in_the_outside_plan()
    {
        using var s = new ExtraSourceScenario(ExtraSourceScenario.A);

        var earlier = s.EarlierShas();
        var (beforeResult, beforeRows, _) = s.Outside();
        Assert.Equal([s.DayPaired], beforeRows.Select(r => r.AttachmentID));
        Assert.Equal(OutsideEligibility.NoHashAsset, ExtraSourceScenario.Eligibility(s, s.Slot));

        Assert.Equal(0, Scenario.Cli(s.AddSourceArgs(s.ExtraRoot, apply: true)).Exit);

        // Earlier rules: the same manifests, byte for byte.
        Assert.Equal(earlier, s.EarlierShas());

        // Outside rule: the earlier row plus exactly this slot, paired by minute, same-day file.
        var (after, rows, path) = s.Outside();
        Assert.Equal([s.DayPaired, s.Slot], rows.Select(r => r.AttachmentID).Order());
        var row = rows.Single(r => r.AttachmentID == s.Slot);
        Assert.Equal((PairingRule?)PairingRule.Minute, row.PairedBy);
        Assert.Equal(MatchKind.HashAndNameSize, row.Kind);
        Assert.Equal(s.SlotKey, row.KeyID);
        Assert.Equal(ExtraSourceScenario.A, row.FileDate);
        Assert.Equal(beforeResult.Rows + 1, after.Rows);
        Assert.Equal(beforeResult.SameDay + 1, after.SameDay);
        Assert.Equal(after.Attachments, after.Rows + after.Excluded.Total);

        // The attachment is outgoing; the new file has no recorded direction.
        Assert.Equal("<null>", s.Data.Rows("SELECT IsSent FROM MediaFile WHERE MediaSourceID = 2;").Single());

        // And it applies through the public decision service.
        var sha = after.Sha256;
        var write = Applier.Apply(Options.Parse(
        [
            "apply", "--msgstore", s.Data.MsgstorePath, "--workspace", s.Data.WorkspacePath,
            "--manifest", path, "--manifest-sha256", sha, "--i-have-a-backup",
        ]));
        Assert.Equal(2, write.Done);
        Assert.True(write.Reconciled);
        Assert.Equal("Resolved", s.Data.Rows($"SELECT ResolutionStatus FROM Attachment WHERE AttachmentID = {s.Slot};").Single());
    }

    /// <summary>Open point 2b: the same slot on a day that fails the day-sequence checks.</summary>
    [Fact]
    public void The_same_slot_on_an_unusable_day_is_excluded_by_the_day_guard()
    {
        using var s = new ExtraSourceScenario(ExtraSourceScenario.B);

        Assert.Equal(0, Scenario.Cli(s.AddSourceArgs(s.ExtraRoot, apply: true)).Exit);

        var (result, rows, _) = s.Outside();

        Assert.Equal([s.DayPaired], rows.Select(r => r.AttachmentID));
        Assert.Equal(OutsideEligibility.DayNotUsable, ExtraSourceScenario.Eligibility(s, s.Slot));
        Assert.Equal(1, result.Excluded[Reason(OutsideEligibility.DayNotUsable, DayVerdict.CountMismatch)]);
    }
}
