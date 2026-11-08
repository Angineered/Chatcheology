using System.Globalization;

namespace MsgstoreImport.Tests;

/// <summary>
/// CH-9 synthetic data: six days, each exercising one day-sequence rule, with a minute-rule batch
/// applied first so prior confirmations exist.
/// </summary>
/// <remarks>
/// Day A (usable): a received text, a minute-paired received photo (confirmed by the minute batch),
/// the drifted 1/2/4/0 against 0/3/3/1 run of seven sent photos, a sent text.
/// Day B: one more message in the workspace (count mismatch). Day C: a text's direction differs.
/// Day D: the slot lines up with a non-media message. Day E: a prior import confirmation names a
/// different record. Day F (usable): one slot per new-slot guard, and one eligible slot.
/// </remarks>
internal sealed class DayScenario : IDisposable
{
    internal static readonly DateOnly A = SyntheticData.Day, B = A.AddDays(1), C = A.AddDays(2),
        D = A.AddDays(3), E = A.AddDays(4), F = A.AddDays(5);

    internal SyntheticData Data { get; } = new();

    internal long MinutePaired;
    internal long[] Drift = new long[7];
    internal SyntheticData.Asset[] DriftAssets = new SyntheticData.Asset[7];
    internal long CountSlot, DirectionSlot, MediaSlot, PriorSlot, PriorDaySlot;
    internal long Resolved, Gap, NameSize, NoHash, NotInSet, Disproven, NoKey, Eligible;
    internal SyntheticData.Asset EligibleAsset = null!;
    internal string MinuteSha = "";

    internal string Manifest => Path.Combine(Data.DirectoryPath, "dayseq.tsv");
    internal string MinuteManifest => Path.Combine(Data.DirectoryPath, "minute.tsv");
    internal string SamplePath => Path.Combine(Data.DirectoryPath, "sample.md");

    internal DayScenario()
    {
        var d = Data;

        // ---- Day A ----
        d.AddWorkspaceMessage("08:30:00", false, A);
        d.AddNonMediaMessage("08:30:10", false, 0, "text A1", A);

        MinutePaired = d.AddAttachment("08:31:00", false, A);
        var paired = d.AddAsset(A);
        d.AddRecordFor(paired, "08:31:20", false, "DKEYPAIRED", date: A);

        string[] wsMinutes = ["08:39:00", "08:40:00", "08:40:00", "08:41:00", "08:41:00", "08:41:00", "08:41:00"];
        string[] msMinutes = ["08:40:05", "08:40:25", "08:40:45", "08:41:10", "08:41:30", "08:41:50", "08:42:10"];

        for (var i = 0; i < 7; i++)
        {
            Drift[i] = d.AddAttachment(wsMinutes[i], true, A);
            DriftAssets[i] = d.AddAsset(A);
        }

        for (var i = 0; i < 7; i++)
        {
            d.AddRecordFor(DriftAssets[i], msMinutes[i], true, "DKEYDRIFT" + i.ToString(CultureInfo.InvariantCulture),
                nameAndSize: i % 2 == 0, date: A);
        }

        d.AddWorkspaceMessage("08:50:00", true, A);
        d.AddNonMediaMessage("08:50:00", true, 0, "text A2", A);

        // ---- Day B: count mismatch (an extra workspace text) ----
        CountSlot = d.AddAttachment("10:00:00", true, B);
        d.AddWorkspaceMessage("10:05:00", true, B);
        d.AddRecordFor(d.AddAsset(B), "10:01:00", true, "DKEYCOUNT", date: B);

        // ---- Day C: direction differs on the text ----
        DirectionSlot = d.AddAttachment("11:00:00", true, C);
        d.AddWorkspaceMessage("11:05:00", true, C);
        d.AddRecordFor(d.AddAsset(C), "11:01:00", true, "DKEYDIRECTION", date: C);
        d.AddNonMediaMessage("11:05:00", false, 0, "text C", C);

        // ---- Day D: the slot lines up with a non-media message ----
        MediaSlot = d.AddAttachment("12:00:00", true, D);
        d.AddWorkspaceMessage("12:01:00", true, D);
        d.AddNonMediaMessage("12:00:00", true, 0, "text D", D);
        d.AddRecordFor(d.AddAsset(D), "12:01:00", true, "DKEYMEDIA", date: D);

        // ---- Day E: a prior import confirmation that names another record ----
        PriorSlot = d.AddAttachment("13:00:00", false, E);
        var priorAsset = d.AddAsset(E);
        d.AddRecordFor(priorAsset, "13:00:10", false, "DKEYPRIORREAL", date: E);
        PriorDaySlot = d.AddAttachment("13:20:00", false, E);
        d.AddRecordFor(d.AddAsset(E), "13:21:00", false, "DKEYPRIORDAY", date: E);

        // ---- Day F: one slot per guard, each drifted one minute so the minute rule pairs none ----
        Resolved = d.AddAttachment("14:00:00", true, F);
        var resolvedAsset = d.AddAsset(F);
        d.AddRecordFor(resolvedAsset, "14:01:00", true, "DKEYRESOLVED", date: F);

        Gap = d.AddAttachment("14:10:00", true, F);
        d.AddRecordFor(d.AddAsset(F), "14:15:00", true, "DKEYGAP", date: F);

        NameSize = d.AddAttachment("14:20:00", true, F);
        var nameSizeAsset = d.AddAsset(F);
        d.AddRecord("14:21:00", true, nameSizeAsset.FileName, nameSizeAsset.Size, null, "DKEYNAMESIZE", date: F);

        NoHash = d.AddAttachment("14:30:00", true, F);
        d.AddRecord("14:31:00", true, "never-recovered.jpg", 5, d.UnknownHash(), "DKEYNOHASH", date: F);

        NotInSet = d.AddAttachment("14:40:00", true, F);
        d.AddRecordFor(d.AddAsset(F.AddDays(3)), "14:41:00", true, "DKEYNOTINSET", date: F);

        Disproven = d.AddAttachment("14:50:00", true, F);
        var disprovenAsset = d.AddAsset(F);
        d.AddRecordFor(disprovenAsset, "14:51:00", true, "DKEYDISPROVEN", date: F);

        NoKey = d.AddAttachment("15:00:00", true, F);
        var noKeyAsset = d.AddAsset(F);
        d.AddRecord("15:01:00", true, noKeyAsset.FileName, noKeyAsset.Size, noKeyAsset.Base64Hash, null, date: F);

        Eligible = d.AddAttachment("15:10:00", true, F);
        EligibleAsset = d.AddAsset(F);
        d.AddRecordFor(EligibleAsset, "15:11:00", true, "DKEYELIGIBLE", date: F);

        d.RunPhase7();
        d.ConfirmByHand(Resolved, resolvedAsset.ID);
        d.Disprove(Disproven, disprovenAsset.ID);
        d.ConfirmAsImport(PriorSlot, priorAsset.ID, "DKEYSOMETHINGELSE");
        d.CloseAll();

        // The first (minute-rule) batch.
        var minute = Planner.Plan(Options.Parse(Args("plan", "--manifest-out", MinuteManifest)));
        MinuteSha = minute.Sha256;
        Applier.Apply(Options.Parse(
        [
            "apply", "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath,
            "--manifest", MinuteManifest, "--manifest-sha256", MinuteSha, "--i-have-a-backup",
        ]));
    }

    internal string[] Args(string mode, params string[] extra) =>
    [
        mode, "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath, "--conversation", "1",
        "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture), "--utc-offset", SyntheticData.OffsetText,
        "--local-participant", "1", .. extra,
    ];

    internal string[] PlanArgs(params string[] extra) =>
        Args("plan", ["--rule", "day-sequence", "--manifest-out", Manifest, .. extra]);

    internal DayPlanResult Plan() => DayPlanner.Plan(Options.Parse(PlanArgs()));

    internal WriteResult Apply(string sha) => Applier.Apply(Options.Parse(
    [
        "apply", "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath,
        "--manifest", Manifest, "--manifest-sha256", sha, "--i-have-a-backup",
    ]));

    internal WriteResult Undo(string sha) => Undoer.Undo(Options.Parse(
    [
        "undo-batch", "--workspace", Data.WorkspacePath, "--manifest", Manifest, "--manifest-sha256", sha, "--i-have-a-backup",
    ]));

    public void Dispose() => Data.Dispose();
}

public sealed class DaySequenceTests
{
    private static string Reason(DayEligibility e, DayVerdict? v = null) => DaySequence.SlotReason(e, v);

    [Fact]
    public void Minute_batch_confirmed_only_the_minute_paired_photo()
    {
        using var s = new DayScenario();

        // Only the 08:31 photo pairs by minute; every other slot is drifted.
        Assert.Equal(1, s.Data.Count("SELECT COUNT(*) FROM AttachmentResolutionEvent WHERE IndependentBasisDescription LIKE '%DKEYPAIRED%';"));
        Assert.Equal(3, s.Data.Count("SELECT COUNT(*) FROM Attachment WHERE ResolutionStatus = 'Resolved';"));
    }

    [Fact]
    public void Drifted_run_pairs_by_day_position()
    {
        using var s = new DayScenario();

        var r = s.Plan();
        var rows = MsgstoreImport.Manifest.Parse(File.ReadAllBytes(s.Manifest)).Rows.ToDictionary(x => x.AttachmentID);

        for (var i = 0; i < 7; i++)
        {
            Assert.Equal(s.DriftAssets[i].ID, rows[s.Drift[i]].MediaAssetID);
            Assert.Equal("DKEYDRIFT" + i.ToString(CultureInfo.InvariantCulture), rows[s.Drift[i]].KeyID);
            Assert.Equal(i % 2 == 0 ? MatchKind.HashAndNameSize : MatchKind.HashOnly, rows[s.Drift[i]].Kind);
        }

        Assert.Equal(s.EligibleAsset.ID, rows[s.Eligible].MediaAssetID);
        Assert.Equal(8, r.Rows);
        Assert.Equal(PairingRule.DaySequence, MsgstoreImport.Manifest.Parse(File.ReadAllBytes(s.Manifest)).Rule);
    }

    [Fact]
    public void Each_day_rule_skips_its_day_and_is_counted()
    {
        using var s = new DayScenario();

        var r = s.Plan();
        var days = r.Alignment.Days;

        Assert.Equal(2, days[DayVerdict.Usable]);
        Assert.Equal(1, days[DayVerdict.CountMismatch]);
        Assert.Equal(1, days[DayVerdict.DirectionMismatch]);
        Assert.Equal(1, days[DayVerdict.SlotWithoutMedia]);
        Assert.Equal(1, days[DayVerdict.PriorDisagreement]);

        Assert.Equal(1, r.Excluded[Reason(DayEligibility.DaySkipped, DayVerdict.CountMismatch)]);
        Assert.Equal(1, r.Excluded[Reason(DayEligibility.DaySkipped, DayVerdict.DirectionMismatch)]);
        Assert.Equal(1, r.Excluded[Reason(DayEligibility.DaySkipped, DayVerdict.SlotWithoutMedia)]);
        Assert.Equal(2, r.Excluded[Reason(DayEligibility.DaySkipped, DayVerdict.PriorDisagreement)]);

        // The minute batch's confirmation agrees; the fake prior one does not.
        Assert.Equal(1, r.Alignment.PriorAgree);
        Assert.Equal(1, r.Alignment.PriorDisagree);

        // Minute pairs on lined-up days: 08:31 (Day A) and the 13:00 photo (Day E).
        Assert.Equal(2, r.Alignment.MinuteAgree);
        Assert.Equal(0, r.Alignment.MinuteDisagree);
        Assert.Equal(0, days[DayVerdict.MinutePairingDisagreement]);

        var manifest = MsgstoreImport.Manifest.Parse(File.ReadAllBytes(s.Manifest)).Rows.Select(x => x.AttachmentID).ToHashSet();
        Assert.DoesNotContain(s.CountSlot, manifest);
        Assert.DoesNotContain(s.DirectionSlot, manifest);
        Assert.DoesNotContain(s.MediaSlot, manifest);
        Assert.DoesNotContain(s.PriorDaySlot, manifest);
    }

    [Fact]
    public void Every_new_slot_guard_applies_and_counts_reconcile()
    {
        using var s = new DayScenario();

        var r = s.Plan();

        Assert.Equal(1, r.Excluded[Reason(DayEligibility.PairedByMinuteRule)]);
        Assert.Equal(1, r.Excluded[Reasons.AlreadyResolved]);
        Assert.Equal(1, r.Excluded[Reason(DayEligibility.MinuteGap)]);
        Assert.Equal(1, r.Excluded[Reason(DayEligibility.NameSizeOnly)]);
        Assert.Equal(1, r.Excluded[Reason(DayEligibility.NoHashAsset)]);
        Assert.Equal(1, r.Excluded[Reason(DayEligibility.NotInCandidateSet)]);
        Assert.Equal(1, r.Excluded[Reason(DayEligibility.Disproven)]);
        Assert.Equal(1, r.Excluded[Reason(DayEligibility.NoKeyID)]);

        Assert.Equal(r.Attachments, r.Rows + r.Excluded.Total);
        Assert.Equal(21, r.Attachments);
    }

    [Fact]
    public void Plan_stdout_is_counts_only_and_flags_disagreements()
    {
        using var s = new DayScenario();

        var (exit, stdout, stderr) = Scenario.Cli(s.PlanArgs());

        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.Contains("ATTENTION: 1 prior import confirmation(s)", stdout);
        Assert.Matches(@"DISAGREE \(different record\)\s+1", stdout);
        Assert.Matches(@"new manifest rows\s+8", stdout);
        Assert.DoesNotContain("DKEY", stdout);
        Assert.DoesNotContain("IMG-", stdout);
        Assert.DoesNotContain(s.Data.DirectoryPath, stdout);
    }

    [Fact]
    public void Plan_is_read_only()
    {
        using var s = new DayScenario();

        var workspace = PlanTests.Sha(s.Data.WorkspacePath);
        var msgstore = PlanTests.Sha(s.Data.MsgstorePath);

        s.Plan();

        Assert.Equal(workspace, PlanTests.Sha(s.Data.WorkspacePath));
        Assert.Equal(msgstore, PlanTests.Sha(s.Data.MsgstorePath));
    }

    [Fact]
    public void Sample_lists_rows_with_day_positions_and_honours_the_guards()
    {
        using var s = new DayScenario();

        var (exit, stdout, _) = Scenario.Cli(s.PlanArgs("--sample-out", s.SamplePath, "--seed", "7"));

        Assert.Equal(0, exit);
        Assert.Matches(@"private sample rows written\s+8", stdout);

        var text = File.ReadAllText(s.SamplePath);
        Assert.Contains("Seed 7, sample size 20, 8 of 8", text);
        Assert.Contains($"| {s.Drift[0]} | 2026-03-02 | 08:39 | sent | 1 of 7 | 08:40 | {s.DriftAssets[0].FileName} |", text);
        Assert.Contains($"| {s.Drift[6]} | 2026-03-02 | 08:41 | sent | 7 of 7 | 08:42 |", text);

        // Same seed, same sample.
        var again = Path.Combine(s.Data.DirectoryPath, "sample2.md");
        var manifest2 = Path.Combine(s.Data.DirectoryPath, "dayseq2.tsv");
        Scenario.Cli(s.Args("plan", "--rule", "day-sequence", "--manifest-out", manifest2, "--sample-out", again, "--seed", "7"));
        Assert.Equal(text, File.ReadAllText(again));

        // The repository guard and the existing-file guard apply to the sample too.
        var repo = Path.Combine(s.Data.DirectoryPath, "clone");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var (refused, _, stderr) = Scenario.Cli(s.Args("plan", "--rule", "day-sequence", "--manifest-out",
            Path.Combine(s.Data.DirectoryPath, "dayseq3.tsv"), "--sample-out", Path.Combine(repo, "s.md"), "--seed", "1"));
        Assert.Equal(2, refused);
        Assert.Contains("--sample-out path is inside a repository", stderr);
        Assert.False(File.Exists(Path.Combine(s.Data.DirectoryPath, "dayseq3.tsv")));
    }

    [Fact]
    public void Sample_needs_the_day_sequence_rule_and_a_seed()
    {
        var (exit, _, stderr) = Scenario.Cli(
            ["plan", "--msgstore", "m", "--workspace", "w", "--conversation", "1", "--chat", "7", "--utc-offset", "+02:00",
             "--local-participant", "1", "--manifest-out", "x", "--sample-out", "y", "--seed", "1"]);
        Assert.Equal(2, exit);
        Assert.Contains("only to plan --rule day-sequence", stderr);

        (exit, _, stderr) = Scenario.Cli(
            ["plan", "--msgstore", "m", "--workspace", "w", "--conversation", "1", "--chat", "7", "--utc-offset", "+02:00",
             "--local-participant", "1", "--manifest-out", "x", "--rule", "day-sequence", "--sample-out", "y"]);
        Assert.Equal(2, exit);
        Assert.Contains("--seed is required", stderr);
    }

    [Fact]
    public void Apply_confirms_with_the_day_sequence_basis_and_writes_only_the_resolution_tables()
    {
        using var s = new DayScenario();
        var sha = s.Plan().Sha256;
        var tag = sha[..12];
        var others = s.Data.DumpExcept("AttachmentResolutionEvent", "Attachment");

        var r = s.Apply(sha);

        Assert.Equal(8, r.Done);
        Assert.True(r.Reconciled);
        Assert.Equal(others, s.Data.DumpExcept("AttachmentResolutionEvent", "Attachment"));
        Assert.Equal(
            $"Confirm,ExternalProvenanceOrGroundTruth,WhatsApp msgstore record (message key_id DKEYDRIFT1), paired by day message sequence, " +
            $"file matched by hash only; import batch {tag},<null>,Angie (msgstore import)",
            s.Data.Rows($"SELECT EventType, IndependentBasisType, IndependentBasisDescription, SourceRunID, ActorLabel FROM AttachmentResolutionEvent WHERE AttachmentID = {s.Drift[1]};").Single());

        // Rerun: all already done.
        var again = s.Apply(sha);
        Assert.Equal(8, again.AlreadyDone);
        Assert.Equal(0, again.Done);
    }

    [Fact]
    public void Apply_catches_a_changed_alignment()
    {
        using var s = new DayScenario();
        var sha = s.Plan().Sha256;

        // A new msgstore message on Day A: the counts no longer agree, so the whole day falls away.
        s.Data.AddNonMediaMessage("08:55:00", true, 0, "late text", DayScenario.A);
        s.Data.CloseAll();

        var r = s.Apply(sha);

        Assert.Equal(7, r.Other[Reasons.DayNoLongerUsable]);
        Assert.Equal(1, r.Done);
        Assert.True(r.Reconciled);
        Assert.Equal("Unresolved", s.Data.Rows($"SELECT ResolutionStatus FROM Attachment WHERE AttachmentID = {s.Drift[0]};").Single());
    }

    [Fact]
    public void Undo_withdraws_only_the_day_sequence_batch()
    {
        using var s = new DayScenario();
        var sha = s.Plan().Sha256;

        s.Apply(sha);
        var r = s.Undo(sha);

        Assert.Equal(8, r.Done);
        Assert.True(r.Reconciled);
        Assert.Equal("Resolved", s.Data.Rows($"SELECT ResolutionStatus FROM Attachment WHERE AttachmentID = {s.MinutePaired};").Single());
        Assert.Equal("Resolved", s.Data.Rows($"SELECT ResolutionStatus FROM Attachment WHERE AttachmentID = {s.PriorSlot};").Single());
        Assert.Equal(3, s.Data.Count("SELECT COUNT(*) FROM Attachment WHERE ResolutionStatus = 'Resolved';"));
    }

    [Fact]
    public void Minute_manifest_keeps_format_one_and_its_undo_is_unaffected()
    {
        using var s = new DayScenario();

        var minuteBytes = File.ReadAllBytes(s.MinuteManifest);
        Assert.StartsWith("chatcheology-msgstore-import-manifest\t1\n# PRIVATE", System.Text.Encoding.UTF8.GetString(minuteBytes));
        Assert.Equal(PairingRule.Minute, MsgstoreImport.Manifest.Parse(minuteBytes).Rule);

        var sha = s.Plan().Sha256;
        s.Apply(sha);

        var undo = Undoer.Undo(Options.Parse(
        [
            "undo-batch", "--workspace", s.Data.WorkspacePath, "--manifest", s.MinuteManifest,
            "--manifest-sha256", s.MinuteSha, "--i-have-a-backup",
        ]));

        Assert.Equal(1, undo.Done);
        Assert.Equal("Unresolved", s.Data.Rows($"SELECT ResolutionStatus FROM Attachment WHERE AttachmentID = {s.MinutePaired};").Single());
        Assert.Equal("Resolved", s.Data.Rows($"SELECT ResolutionStatus FROM Attachment WHERE AttachmentID = {s.Drift[0]};").Single());
    }
}
