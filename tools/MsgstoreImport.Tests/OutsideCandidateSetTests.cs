using System.Globalization;
using System.Text;

namespace MsgstoreImport.Tests;

/// <summary>
/// Synthetic data for the outside-candidate-set rule. Day A is usable; day B has one extra workspace
/// message (not usable). The minute batch runs first, as on the real data.
/// </summary>
internal sealed class OutsideScenario : IDisposable
{
    internal static readonly DateOnly A = SyntheticData.Day, B = A.AddDays(1);

    internal SyntheticData Data { get; } = new();

    internal long Forwarded, DayPaired, Newer, PlusOne, InSet, NoDate, Unusable;
    internal SyntheticData.Asset ForwardedAsset = null!, DayPairedAsset = null!, PlusOneAsset = null!;
    internal string MinuteSha = "";

    internal string MinuteManifest => Path.Combine(Data.DirectoryPath, "minute.tsv");
    internal string Manifest => Path.Combine(Data.DirectoryPath, "outside.tsv");

    internal OutsideScenario()
    {
        var d = Data;

        // Minute-paired, file 10 days older (a forwarded photo).
        Forwarded = d.AddAttachment("09:00:00", false, A);
        ForwardedAsset = d.AddAsset(A.AddDays(-10));
        d.AddRecordFor(ForwardedAsset, "09:00:20", false, "OKEYFORWARDED", date: A);

        // Day-paired (one minute of drift), file 40 days older, hash only.
        DayPaired = d.AddAttachment("09:10:00", true, A);
        DayPairedAsset = d.AddAsset(A.AddDays(-40));
        d.AddRecordFor(DayPairedAsset, "09:11:00", true, "OKEYDAYPAIRED", nameAndSize: false, date: A);

        // File dated three days after the message: excluded.
        Newer = d.AddAttachment("09:20:00", false, A);
        d.AddRecordFor(d.AddAsset(A.AddDays(3)), "09:20:00", false, "OKEYNEWER", date: A);

        // File dated the next day (saved just after midnight): included.
        PlusOne = d.AddAttachment("09:30:00", false, A);
        PlusOneAsset = d.AddAsset(A.AddDays(1));
        d.AddRecordFor(PlusOneAsset, "09:30:00", false, "OKEYPLUSONE", date: A);

        // Asset IS in the candidate set: the minute batch confirms it; not this rule's.
        InSet = d.AddAttachment("09:40:00", false, A);
        d.AddRecordFor(d.AddAsset(A), "09:40:00", false, "OKEYINSET", date: A);

        // No naming-derived date at all.
        NoDate = d.AddAttachment("09:50:00", false, A);
        d.AddRecordFor(d.AddAsset(dated: false), "09:50:00", false, "OKEYNODATE", date: A);

        // Day B: an older file, but the day's message counts differ.
        Unusable = d.AddAttachment("10:00:00", false, B);
        d.AddWorkspaceMessage("10:05:00", false, B);
        d.AddRecordFor(d.AddAsset(B.AddDays(-5)), "10:00:00", false, "OKEYUNUSABLE", date: B);

        d.RunPhase7();
        d.CloseAll();

        MinuteSha = Planner.Plan(Options.Parse(Args("plan", "--manifest-out", MinuteManifest))).Sha256;
        Applier.Apply(Options.Parse(ApplyArgs(MinuteManifest, MinuteSha)));
    }

    internal string[] Args(string mode, params string[] extra) =>
    [
        mode, "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath, "--conversation", "1",
        "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture), "--utc-offset", SyntheticData.OffsetText,
        "--local-participant", "1", .. extra,
    ];

    internal string[] PlanArgs(params string[] extra) =>
        Args("plan", ["--rule", "outside-candidate-set", "--manifest-out", Manifest, .. extra]);

    internal string[] ApplyArgs(string manifest, string sha) =>
    [
        "apply", "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath,
        "--manifest", manifest, "--manifest-sha256", sha, "--i-have-a-backup",
    ];

    internal string[] UndoArgs(string manifest, string sha) =>
        ["undo-batch", "--workspace", Data.WorkspacePath, "--manifest", manifest, "--manifest-sha256", sha, "--i-have-a-backup"];

    internal OutsidePlanResult Plan() => OutsidePlanner.Plan(Options.Parse(PlanArgs()));

    internal WriteResult Apply(string sha) => Applier.Apply(Options.Parse(ApplyArgs(Manifest, sha)));

    public void Dispose() => Data.Dispose();
}

public sealed class OutsideCandidateSetTests
{
    private static string Reason(OutsideEligibility e, DayVerdict? day = null) =>
        OutsideCandidateSet.Reason(new OutsideAssessment(0, e, day, false, false, null, null, null, null, null, null, null, null, null, null, 0, 0));

    [Fact]
    public void Older_files_are_included_and_a_file_dated_after_the_message_is_excluded()
    {
        using var s = new OutsideScenario();

        var r = s.Plan();
        var rows = Manifest.Parse(File.ReadAllBytes(s.Manifest)).Rows.ToDictionary(x => x.AttachmentID);

        Assert.Equal([s.Forwarded, s.DayPaired, s.PlusOne], rows.Keys.Order());
        Assert.Equal((PairingRule?)PairingRule.Minute, rows[s.Forwarded].PairedBy);
        Assert.Equal(OutsideScenario.A.AddDays(-10), rows[s.Forwarded].FileDate);
        Assert.Equal((PairingRule?)PairingRule.DaySequence, rows[s.DayPaired].PairedBy);
        Assert.Equal(MatchKind.HashOnly, rows[s.DayPaired].Kind);
        Assert.Equal(OutsideScenario.A.AddDays(1), rows[s.PlusOne].FileDate);

        Assert.Equal(1, r.PlusMinusOne);
        Assert.Equal(0, r.SameDay);
        Assert.Equal(1, r.Older2To30);
        Assert.Equal(1, r.OlderOver30);
        Assert.Equal(1, r.NewerOverOne);
        Assert.Equal(1, r.NoDate);
        Assert.Equal(1, r.Excluded[Reason(OutsideEligibility.FileDatedAfterMessage)]);
        Assert.Equal(1, r.Excluded[Reason(OutsideEligibility.NoFileDate)]);
        Assert.Equal(r.Attachments, r.Rows + r.Excluded.Total);
    }

    [Fact]
    public void A_slot_on_an_unusable_day_is_excluded()
    {
        using var s = new OutsideScenario();

        var r = s.Plan();

        Assert.DoesNotContain(Manifest.Parse(File.ReadAllBytes(s.Manifest)).Rows, x => x.AttachmentID == s.Unusable);
        Assert.Equal(1, r.Excluded[Reason(OutsideEligibility.DayNotUsable, DayVerdict.CountMismatch)]);
    }

    [Fact]
    public void A_slot_whose_asset_is_in_the_candidate_set_is_not_this_rules()
    {
        using var s = new OutsideScenario();

        var r = s.Plan();

        Assert.DoesNotContain(Manifest.Parse(File.ReadAllBytes(s.Manifest)).Rows, x => x.AttachmentID == s.InSet);
        Assert.Equal(1, r.Excluded[Reason(OutsideEligibility.InCandidateSet)]);
    }

    [Fact]
    public void Stdout_is_counts_only_and_the_sample_shows_dates_and_gaps()
    {
        using var s = new OutsideScenario();
        var sample = Path.Combine(s.Data.DirectoryPath, "sample.md");

        var (exit, stdout, stderr) = Scenario.Cli(s.PlanArgs("--sample-out", sample, "--seed", "7"));

        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.Matches(@"new manifest rows\s+3", stdout);
        Assert.Matches(@"older by more than 30 days \(rows\)\s+1", stdout);
        Assert.Matches(@"private sample rows written\s+3", stdout);
        Assert.DoesNotContain("OKEY", stdout);
        Assert.DoesNotContain("IMG-", stdout);
        Assert.DoesNotContain(s.Data.DirectoryPath, stdout);

        var text = File.ReadAllText(sample);
        Assert.Contains(
            $"| {s.Forwarded} | 2026-03-02 | 09:00 | received | 1 of 5 | minute | {s.ForwardedAsset.FileName} | 2026-02-20 | 2026-02-20 | -10 | 2048 | hash and name+size |  |",
            text);
        Assert.Contains($"| {s.DayPaired} | 2026-03-02 | 09:10 | sent | 1 of 1 | day sequence |", text);
    }

    [Fact]
    public void Apply_confirms_with_the_outside_basis_writes_only_the_resolution_tables_and_reruns_as_a_no_op()
    {
        using var s = new OutsideScenario();
        var sha = s.Plan().Sha256;
        var tag = sha[..12];
        var others = s.Data.DumpExcept("AttachmentResolutionEvent", "Attachment");

        var r = s.Apply(sha);

        Assert.Equal(3, r.Done);
        Assert.True(r.Reconciled);
        Assert.Equal(others, s.Data.DumpExcept("AttachmentResolutionEvent", "Attachment"));
        Assert.Equal(
            "WhatsApp msgstore record (message key_id OKEYDAYPAIRED), paired by day message sequence, file matched by hash only, " +
            $"file outside the date candidate set (file dated 2026-01-21); import batch {tag},Angie (msgstore import),<null>",
            s.Data.Rows($"SELECT IndependentBasisDescription, ActorLabel, SourceRunID FROM AttachmentResolutionEvent WHERE AttachmentID = {s.DayPaired};").Single());
        Assert.Contains(
            "paired by minute, file matched by hash and name+size, file outside the date candidate set (file dated 2026-02-20)",
            s.Data.Rows($"SELECT IndependentBasisDescription FROM AttachmentResolutionEvent WHERE AttachmentID = {s.Forwarded};").Single());

        var again = s.Apply(sha);
        Assert.Equal(3, again.AlreadyDone);
        Assert.Equal(0, again.Done);
    }

    [Fact]
    public void Apply_recheck_skips_rows_whose_day_no_longer_lines_up()
    {
        using var s = new OutsideScenario();
        var sha = s.Plan().Sha256;

        s.Data.AddNonMediaMessage("11:00:00", true, 0, "late text", OutsideScenario.A);
        s.Data.CloseAll();

        var r = s.Apply(sha);

        Assert.Equal(3, r.Other[Reasons.DayNoLongerUsable]);
        Assert.Equal(0, r.Done);
        Assert.True(r.Reconciled);
    }

    [Fact]
    public void Day_sequence_reads_the_new_basis_as_a_prior_confirmation()
    {
        using var s = new OutsideScenario();
        var sha = s.Plan().Sha256;
        s.Apply(sha);

        var day = DayPlanner.Plan(Options.Parse(s.Args(
            "plan", "--rule", "day-sequence", "--manifest-out", Path.Combine(s.Data.DirectoryPath, "dayseq.tsv"))));

        // Minute batch: InSet. Outside batch: Forwarded, DayPaired, PlusOne. All on usable day A.
        Assert.Equal(4, day.Alignment.PriorAgree);
        Assert.Equal(0, day.Alignment.PriorUnreadable);
        Assert.Equal(0, day.Alignment.PriorDisagree);
    }

    [Fact]
    public void Earlier_manifests_and_undo_are_unaffected()
    {
        using var s = new OutsideScenario();

        // The minute manifest is format 1; a day-sequence manifest keeps its five-column format 2.
        Assert.StartsWith("chatcheology-msgstore-import-manifest\t1\n", Encoding.UTF8.GetString(File.ReadAllBytes(s.MinuteManifest)));

        var dayPath = Path.Combine(s.Data.DirectoryPath, "dayseq.tsv");
        DayPlanner.Plan(Options.Parse(s.Args("plan", "--rule", "day-sequence", "--manifest-out", dayPath)));
        var dayLines = Encoding.UTF8.GetString(File.ReadAllBytes(dayPath)).Split('\n');
        Assert.Equal("rule\tday-sequence", dayLines[2]);
        Assert.Equal(MsgstoreImport.Manifest.Header, dayLines[8]);

        var sha = s.Plan().Sha256;
        Assert.Equal(PairingRule.OutsideCandidateSet, MsgstoreImport.Manifest.Parse(File.ReadAllBytes(s.Manifest)).Rule);
        s.Apply(sha);

        // Undo the outside batch: only its three rows.
        var undo = Undoer.Undo(Options.Parse(s.UndoArgs(s.Manifest, sha)));
        Assert.Equal(3, undo.Done);
        Assert.Equal("Resolved", s.Data.Rows($"SELECT ResolutionStatus FROM Attachment WHERE AttachmentID = {s.InSet};").Single());

        // Undo the minute batch: its one row.
        var minuteUndo = Undoer.Undo(Options.Parse(s.UndoArgs(s.MinuteManifest, s.MinuteSha)));
        Assert.Equal(1, minuteUndo.Done);
        Assert.Equal(0, s.Data.Count("SELECT COUNT(*) FROM Attachment WHERE ResolutionStatus = 'Resolved';"));
    }
}
