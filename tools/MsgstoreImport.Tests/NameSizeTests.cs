using System.Globalization;
using System.Text;

namespace MsgstoreImport.Tests;

/// <summary>Synthetic data for the name+size review: day A usable, day B not.</summary>
internal sealed class NameSizeScenario : IDisposable
{
    internal static readonly DateOnly A = SyntheticData.Day, B = A.AddDays(1);

    internal SyntheticData Data { get; } = new();

    internal long InSet, OutOfSet, Empty, NoKey, Hash, Resolved, Disproven, Ambiguous, Unusable;
    internal SyntheticData.Asset InSetAsset = null!, OutOfSetAsset = null!, NoKeyAsset = null!;

    internal string ListPath => Path.Combine(Data.DirectoryPath, "namesize-review-list.md");
    internal string Manifest => Path.Combine(Data.DirectoryPath, "namesize.tsv");

    internal NameSizeScenario()
    {
        var d = Data;

        long NameSizeSlot(string slotTime, string recordTime, SyntheticData.Asset asset, string? key, DateOnly? date = null)
        {
            var slot = d.AddAttachment(slotTime, false, date);
            d.AddRecord(recordTime, false, asset.FileName, asset.Size, null, key, date: date);

            return slot;
        }

        InSetAsset = d.AddAsset(A);
        InSet = NameSizeSlot("09:00:00", "09:00:10", InSetAsset, "NSKEYINSET");

        OutOfSetAsset = d.AddAsset(A.AddDays(-5));
        OutOfSet = NameSizeSlot("09:10:00", "09:11:00", OutOfSetAsset, "NSKEYDAY");

        Empty = NameSizeSlot("09:20:00", "09:20:00", d.AddAsset(A, size: 0), "NSKEYEMPTY");
        NoKeyAsset = d.AddAsset(A);
        NoKey = NameSizeSlot("09:30:00", "09:30:00", NoKeyAsset, null);

        Hash = d.AddAttachment("09:40:00", false);
        d.AddRecordFor(d.AddAsset(A), "09:40:00", false, "NSKEYHASH");

        var resolvedAsset = d.AddAsset(A);
        Resolved = NameSizeSlot("09:50:00", "09:50:00", resolvedAsset, "NSKEYRESOLVED");

        var disprovenAsset = d.AddAsset(A);
        Disproven = NameSizeSlot("10:00:00", "10:00:00", disprovenAsset, "NSKEYDISPROVEN");

        // Two assets with the same name and size: no single asset, so not this population.
        var first = d.AddAsset(A, fileName: "same-name.jpg");
        d.AddAsset(A, fileName: "same-name.jpg");
        Ambiguous = NameSizeSlot("10:10:00", "10:10:00", first, "NSKEYAMBIGUOUS");

        Unusable = NameSizeSlot("11:00:00", "11:00:00", d.AddAsset(B), "NSKEYUNUSABLE", B);
        d.AddWorkspaceMessage("11:05:00", false, B);

        d.RunPhase7();
        d.ConfirmByHand(Resolved, resolvedAsset.ID);
        d.Disprove(Disproven, disprovenAsset.ID);
        d.CloseAll();
    }

    internal string[] Inputs(params string[] extra) =>
    [
        "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath, "--conversation", "1",
        "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture), "--utc-offset", SyntheticData.OffsetText,
        "--local-participant", "1", .. extra,
    ];

    internal string[] ListArgs() => ["review-list", "--rule", "namesize", .. Inputs("--review-out", ListPath)];

    /// <summary>The SHA-256 review-list printed, set by <see cref="List"/>.</summary>
    internal string ListSha = "";

    internal string[] PlanArgs(string? listSha = null, string? reviewFile = null) =>
    [
        "plan", "--rule", "namesize-reviewed", "--review-file", reviewFile ?? ListPath, "--review-sha256", listSha ?? ListSha,
        .. Inputs("--manifest-out", Manifest),
    ];

    internal string[] ApplyArgs(string sha, bool withReview = true, string? reviewFile = null) =>
    [
        "apply", "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath, "--manifest", Manifest,
        "--manifest-sha256", sha, "--i-have-a-backup", .. withReview ? new[] { "--review-file", reviewFile ?? ListPath } : [],
    ];

    internal ReviewListResult List()
    {
        var result = ReviewLister.Run(Options.Parse(ListArgs()));
        ListSha = result.Sha256;

        return result;
    }

    internal NameSizePlanResult Plan() => NameSizePlanner.Plan(Options.Parse(PlanArgs()));

    internal WriteResult Apply(string sha) => Applier.Apply(Options.Parse(ApplyArgs(sha)));

    /// <summary>Marks one row as Angie would: in the last cell, or typed after the closing pipe.</summary>
    internal void Mark(long attachment, string mark, bool afterPipe = false)
    {
        var lines = File.ReadAllText(ListPath).Split('\n');
        var prefix = $"| {attachment.ToString(CultureInfo.InvariantCulture)} |";
        var i = Array.FindIndex(lines, l => l.StartsWith(prefix, StringComparison.Ordinal));

        Assert.True(i >= 0, "row not found");
        Assert.EndsWith("|  |", lines[i]);

        lines[i] = afterPipe ? lines[i] + " " + mark : lines[i][..^4] + $"| {mark} |";
        File.WriteAllText(ListPath, string.Join('\n', lines), new UTF8Encoding(false));
    }

    public void Dispose() => Data.Dispose();
}

public sealed class NameSizeTests
{
    private static string Outside(OutsideEligibility e, DayVerdict? day = null) =>
        OutsideCandidateSet.Reason(new OutsideAssessment(0, e, day, false, false, null, null, null, null, null, null, null, null, null, null, 0, 0));

    [Fact]
    public void Review_list_holds_exactly_the_population_and_counts_the_rest()
    {
        using var s = new NameSizeScenario();

        var r = s.List();
        var text = File.ReadAllText(s.ListPath);
        var lines = text.Split('\n');

        Assert.Equal(NameSize.Instructions, lines[0]);
        Assert.Equal(2, r.Rows);
        Assert.Equal(1, r.InCandidateSet);

        Assert.Equal(1, r.Excluded[Outside(OutsideEligibility.EmptyAsset)]);
        Assert.Equal(1, r.Excluded[Outside(OutsideEligibility.NoKeyID)]);
        Assert.Equal(1, r.Excluded[NameSize.NotNameSize]);
        Assert.Equal(1, r.Excluded[Reasons.AlreadyResolved]);
        Assert.Equal(1, r.Excluded[Outside(OutsideEligibility.Disproven)]);
        Assert.Equal(1, r.Excluded[NameSize.Ambiguous]);
        Assert.Equal(1, r.Excluded[Outside(OutsideEligibility.DayNotUsable, DayVerdict.CountMismatch)]);
        Assert.Equal(r.Attachments, r.Rows + r.Excluded.Total);

        var inSet = lines.Single(l => l.StartsWith($"| {s.InSet} |", StringComparison.Ordinal));
        Assert.Contains($"| {s.InSetAsset.ID} | {s.InSetAsset.FileName} | 2048 | 2026-03-02 | yes | ", inSet);
        Assert.Contains(Path.Combine("MediaRoot/Synthetic", "folder"), inSet);
        Assert.EndsWith("| NSKEYINSET |  |", inSet);

        var outOfSet = lines.Single(l => l.StartsWith($"| {s.OutOfSet} |", StringComparison.Ordinal));
        Assert.Contains($"| {s.OutOfSetAsset.ID} | {s.OutOfSetAsset.FileName} | 2048 | 2026-02-25 | no | ", outOfSet);

        Assert.Equal(r.Sha256, Manifest.Sha256Hex(File.ReadAllBytes(s.ListPath)));
    }

    [Fact]
    public void Review_list_and_plan_stdout_are_counts_only()
    {
        using var s = new NameSizeScenario();

        var (exit, stdout, stderr) = Scenario.Cli(s.ListArgs());

        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.Matches(@"rows in the review list\s+2", stdout);
        s.ListSha = Manifest.Sha256Hex(File.ReadAllBytes(s.ListPath));
        Assert.Contains("review list SHA-256: " + s.ListSha, stdout);

        s.Mark(s.InSet, "R");
        (exit, var planOut, stderr) = Scenario.Cli(s.PlanArgs());
        Assert.Equal(0, exit);
        Assert.Equal("", stderr);

        foreach (var output in new[] { stdout, planOut })
        {
            Assert.DoesNotContain("NSKEY", output);
            Assert.DoesNotContain(".jpg", output);
            Assert.DoesNotContain("MediaRoot", output);
            Assert.DoesNotContain(s.Data.DirectoryPath, output);
        }
    }

    [Fact]
    public void Marks_parse_in_the_cell_or_after_the_last_pipe_and_the_list_digest_ignores_them()
    {
        var rows = Enumerable.Range(1, 8).Select(i => new NameSizeAssessment(
            new OutsideAssessment(i, OutsideEligibility.NameSizeOnly, DayVerdict.Usable, false, false, new DateTime(2026, 3, 2, 9, i, 0),
                PairingRule.Minute, 100 + i, "K" + i.ToString(CultureInfo.InvariantCulture), 200 + i, MatchCategory.NameSizeOnly,
                null, null, "f.jpg", 10, 1, 1),
            true, null, false, null, 10, null, ["root/f.jpg"])).ToList();

        var written = NameSize.List(rows);
        var lines = written.Split('\n');

        string Edit(string line, string tail) => line[..^4] + tail;

        lines[6] = Edit(lines[6], "| R |");
        lines[7] = Edit(lines[7], "|  r  |");
        lines[8] = Edit(lines[8], "| W |");
        lines[9] = Edit(lines[9], "| ? |");
        lines[10] = Edit(lines[10], "|  |");
        lines[11] = Edit(lines[11], "|  | R");          // typed after the closing pipe
        lines[12] = Edit(lines[12], "| R");             // closing pipe dropped
        lines[13] = Edit(lines[13], "| yes |");

        var marked = string.Join("\r\n", lines);        // and saved with CRLF line endings
        var (parsed, sha) = NameSize.Parse(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(marked)).ToArray());

        Assert.Equal(
            [ReviewMark.Right, ReviewMark.Right, ReviewMark.Wrong, ReviewMark.Unsure, ReviewMark.Blank, ReviewMark.Right, ReviewMark.Right, ReviewMark.Other],
            parsed.Select(r => r.Mark));
        Assert.Equal(Manifest.Sha256Hex(Encoding.UTF8.GetBytes(written)), sha);
        Assert.Equal(205, parsed[4].MediaAssetID);
        Assert.Equal("K5", parsed[4].KeyID);
    }

    [Fact]
    public void Plan_takes_only_R_rows_and_counts_marks()
    {
        using var s = new NameSizeScenario();

        var list = s.List();
        s.Mark(s.InSet, "R");
        s.Mark(s.OutOfSet, "w");

        var r = s.Plan();
        var rows = Manifest.Parse(File.ReadAllBytes(s.Manifest));

        Assert.Equal(PairingRule.NameSizeReviewed, rows.Rule);
        Assert.Equal([s.InSet], rows.Rows.Select(x => x.AttachmentID));
        Assert.Equal(MatchKind.NameSizeOnly, rows.Rows[0].Kind);
        Assert.Equal(list.Sha256[..12], rows.Rows[0].ReviewList);
        Assert.Equal(1, r.ByMark[NameSize.MarkLabel(ReviewMark.Right)]);
        Assert.Equal(1, r.ByMark[NameSize.MarkLabel(ReviewMark.Wrong)]);
        Assert.Equal(r.ListRows, r.Rows + r.Excluded.Total);
        Assert.Equal(list.Sha256, r.ListSha256);
    }

    [Fact]
    public void Plan_recheck_excludes_a_changed_row_and_one_no_longer_eligible()
    {
        using var s = new NameSizeScenario();

        s.List();
        s.Mark(s.InSet, "R");
        s.Mark(s.OutOfSet, "R");

        // The out-of-set record now names another file of the same size: same slot, different asset.
        s.Data.Msgstore(
            $"UPDATE message_media SET file_path = 'Media/WhatsApp Images/{s.NoKeyAsset.FileName}' " +
            "WHERE message_row_id = (SELECT _id FROM message WHERE key_id = 'NSKEYDAY');");

        // The in-set row's asset is disproven after the list was written.
        s.Data.Disprove(s.InSet, s.InSetAsset.ID);
        s.Data.CloseAll();

        var r = s.Plan();

        Assert.Equal(0, r.Rows);
        Assert.Equal(1, r.Excluded[NameSizePlanner.Changed]);
        Assert.Equal(1, r.Excluded[NameSizePlanner.NoLongerEligible]);
    }

    [Fact]
    public void Plan_refuses_a_list_edited_outside_the_mark_column_when_its_digest_is_given()
    {
        using var s = new NameSizeScenario();

        var list = s.List();
        File.WriteAllText(s.ListPath, File.ReadAllText(s.ListPath).Replace("| yes |", "| no |"), new UTF8Encoding(false));

        var (exit, _, stderr) = Scenario.Cli(s.PlanArgs(list.Sha256));

        Assert.Equal(2, exit);
        Assert.Contains("does not match --review-sha256", stderr);
        Assert.False(File.Exists(s.Manifest));
    }

    [Fact]
    public void Apply_uses_human_inspection_with_the_review_text_and_writes_only_the_resolution_tables()
    {
        using var s = new NameSizeScenario();

        var list = s.List();
        s.Mark(s.InSet, "R");
        s.Mark(s.OutOfSet, "R", afterPipe: true);
        var sha = s.Plan().Sha256;
        var tag = sha[..12];
        var others = s.Data.DumpExcept("AttachmentResolutionEvent", "Attachment");


        var r = s.Apply(sha);

        Assert.Equal(2, r.Done);
        Assert.True(r.Reconciled);
        Assert.Equal(others, s.Data.DumpExcept("AttachmentResolutionEvent", "Attachment"));

        Assert.Equal(
            $"HumanContentOrContextInspection,Angie inspected the file and confirmed it (review list {list.Sha256[..12]}); " +
            $"WhatsApp msgstore record (message key_id NSKEYINSET), paired by minute, file matched by name+size only; import batch {tag}," +
            "Angie (reviewed name+size list),<null>",
            s.Data.Rows($"SELECT IndependentBasisType, IndependentBasisDescription, ActorLabel, SourceRunID FROM AttachmentResolutionEvent WHERE AttachmentID = {s.InSet} ORDER BY AttachmentResolutionEventID DESC LIMIT 1;").Single());

        Assert.EndsWith(
            "paired by day message sequence, file matched by name+size only; import batch " + tag + ",<null>",
            s.Data.Rows($"SELECT IndependentBasisDescription, SourceRunID FROM AttachmentResolutionEvent WHERE AttachmentID = {s.OutOfSet};").Single());

        // Rerun: no-op.
        Assert.Equal(2, s.Apply(sha).AlreadyDone);

        // Undo per tag.
        var undo = Undoer.Undo(Options.Parse(["undo-batch", "--workspace", s.Data.WorkspacePath, "--manifest", s.Manifest, "--manifest-sha256", sha, "--i-have-a-backup"]));
        Assert.Equal(2, undo.Done);
        Assert.Equal("Resolved", s.Data.Rows($"SELECT ResolutionStatus FROM Attachment WHERE AttachmentID = {s.Resolved};").Single());
    }

    [Fact]
    public void Apply_needs_the_review_file_and_skips_a_row_no_longer_marked_R()
    {
        using var s = new NameSizeScenario();

        s.List();
        s.Mark(s.InSet, "R");
        s.Mark(s.OutOfSet, "R");
        var sha = s.Plan().Sha256;

        var (exit, _, stderr) = Scenario.Cli(s.ApplyArgs(sha, withReview: false));
        Assert.Equal(2, exit);
        Assert.Contains("needs --review-file", stderr);

        File.WriteAllText(s.ListPath, File.ReadAllText(s.ListPath).Replace("| NSKEYDAY | R |", "| NSKEYDAY | W |"), new UTF8Encoding(false));

        var r = s.Apply(sha);

        Assert.Equal(1, r.Done);
        Assert.Equal(1, r.Other[Reasons.MarkGone]);
        Assert.True(r.Reconciled);
    }

    [Fact]
    public void Day_sequence_still_finds_these_confirmations_and_earlier_rules_are_unaffected()
    {
        using var s = new NameSizeScenario();

        s.List();
        s.Mark(s.InSet, "R");
        s.Mark(s.OutOfSet, "R");
        s.Apply(s.Plan().Sha256);

        var day = DayPlanner.Plan(Options.Parse(["plan", "--rule", "day-sequence", .. s.Inputs("--manifest-out", Path.Combine(s.Data.DirectoryPath, "day.tsv"))]));

        Assert.Equal(2, day.Alignment.PriorAgree);
        Assert.Equal(0, day.Alignment.PriorUnreadable);
        Assert.Equal(0, day.Alignment.PriorDisagree);

        // The minute rule still never imports a name+size-only match.
        var minute = Planner.Plan(Options.Parse(["plan", .. s.Inputs("--manifest-out", Path.Combine(s.Data.DirectoryPath, "minute.tsv"))]));
        Assert.StartsWith("chatcheology-msgstore-import-manifest\t1\n", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(s.Data.DirectoryPath, "minute.tsv"))));
        Assert.Equal(1, minute.Rows);
    }

    [Fact]
    public void Plan_requires_the_list_digest()
    {
        var (exit, _, stderr) = Scenario.Cli(
            ["plan", "--rule", "namesize-reviewed", "--review-file", "list.md", "--msgstore", "m", "--workspace", "w",
             "--conversation", "1", "--chat", "7", "--utc-offset", "+02:00", "--local-participant", "1", "--manifest-out", "x"]);

        Assert.Equal(2, exit);
        Assert.Contains("--review-sha256", stderr);
    }

    [Fact]
    public void Apply_refuses_a_different_list()
    {
        using var s = new NameSizeScenario();

        s.List();
        s.Mark(s.InSet, "R");
        var sha = s.Plan().Sha256;

        var other = Path.Combine(s.Data.DirectoryPath, "other-list.md");
        File.WriteAllText(other, File.ReadAllText(s.ListPath).Replace("| yes |", "| no |"), new UTF8Encoding(false));

        var (exit, _, stderr) = Scenario.Cli(s.ApplyArgs(sha, reviewFile: other));

        Assert.Equal(2, exit);
        Assert.Contains("not the list this manifest was planned from", stderr);
        Assert.Equal(0, s.Data.Count($"SELECT COUNT(*) FROM AttachmentResolutionEvent WHERE AttachmentID = {s.InSet};"));
    }

    [Fact]
    public void A_conflicting_mark_is_excluded()
    {
        using var s = new NameSizeScenario();

        s.List();
        s.Mark(s.InSet, "R");
        s.Mark(s.OutOfSet, "R");

        // R in the cell, W typed after the pipe.
        File.WriteAllText(s.ListPath, File.ReadAllText(s.ListPath).Replace("| NSKEYDAY | R |", "| NSKEYDAY | R | W"), new UTF8Encoding(false));

        var r = s.Plan();

        Assert.Equal(1, r.Rows);
        Assert.Equal(1, r.ByMark[NameSize.MarkLabel(ReviewMark.Other)]);
        Assert.Equal(1, r.Excluded[NameSize.MarkLabel(ReviewMark.Other)]);
    }

    [Fact]
    public void A_modified_header_line_is_refused()
    {
        using var s = new NameSizeScenario();

        s.List();
        s.Mark(s.InSet, "R");
        File.WriteAllText(s.ListPath, File.ReadAllText(s.ListPath).Replace("| proposed file name |", "| file |"), new UTF8Encoding(false));

        var (exit, _, stderr) = Scenario.Cli(s.PlanArgs());

        Assert.Equal(2, exit);
        Assert.Contains("not a name+size review list", stderr);
        Assert.False(File.Exists(s.Manifest));
    }

    [Fact]
    public void Review_out_inside_a_repository_is_refused()
    {
        using var s = new NameSizeScenario();

        var repo = Path.Combine(s.Data.DirectoryPath, "clone");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var inside = Path.Combine(repo, "list.md");

        var (exit, _, stderr) = Scenario.Cli(["review-list", "--rule", "namesize", .. s.Inputs("--review-out", inside)]);

        Assert.Equal(2, exit);
        Assert.Contains("--review-out path is inside a repository", stderr);
        Assert.False(File.Exists(inside));
    }

    [Fact]
    public void A_key_id_with_a_pipe_is_excluded_from_the_list()
    {
        using var s = new NameSizeScenario();

        s.Data.Msgstore("UPDATE message SET key_id = 'NS|PIPE' WHERE key_id = 'NSKEYINSET';");
        s.Data.CloseAll();

        var r = s.List();

        Assert.Equal(1, r.Rows);
        Assert.Equal(2, r.Excluded[Outside(OutsideEligibility.NoKeyID)]);
        Assert.DoesNotContain("NS|PIPE", File.ReadAllText(s.ListPath));
    }

    [Theory]
    [InlineData("WhatsApp msgstore record (message key_id REAL1), file matched by hash only; import batch 0123456789ab", "REAL1")]
    [InlineData("WhatsApp msgstore record (message key_id REAL2), paired by day message sequence, file matched by hash only; WhatsApp msgstore record (message key_id DECOY), x; import batch 0123456789ab", "REAL2")]
    [InlineData("Angie inspected the file and confirmed it (review list 0123456789ab); WhatsApp msgstore record (message key_id REAL3), paired by minute, file matched by name+size only; import batch 0123456789ab", "REAL3")]
    [InlineData("Note: WhatsApp msgstore record (message key_id DECOY), then Angie inspected the file and confirmed it (review list 0123456789ab); WhatsApp msgstore record (message key_id REAL4), x; import batch 0123456789ab", null)]
    [InlineData("Angie inspected the file and confirmed it (review list notHex123456); WhatsApp msgstore record (message key_id DECOY), x; import batch 0123456789ab", null)]
    public void Prior_key_is_the_one_right_after_a_known_prefix(string description, string? expected)
    {
        Assert.Equal(expected, DaySequence.ParsePriorKey(description));
    }
}
