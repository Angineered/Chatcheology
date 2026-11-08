using System.Globalization;
using System.Text;

namespace MsgstoreImport.Tests;

/// <summary>Synthetic data for the documents rule: day A usable, day B not.</summary>
internal sealed class DocumentsScenario : IDisposable
{
    internal static readonly DateOnly A = SyntheticData.Day, B = A.AddDays(1);

    internal SyntheticData Data { get; } = new();

    internal long DocMinute, DocDay, PhotoNoDate, DatedDoc, DocNameSize, DocEmpty, DocDisproven, DocResolved, DocGap, DocUnusable;
    internal SyntheticData.Asset DocMinuteAsset = null!, DocDayAsset = null!;

    internal string Manifest => Path.Combine(Data.DirectoryPath, "documents.tsv");
    internal string SamplePath => Path.Combine(Data.DirectoryPath, "documents-sample.md");

    internal DocumentsScenario()
    {
        var d = Data;
        const string pdf = "application/pdf";

        long Doc(string slotTime, string recordTime, SyntheticData.Asset asset, string key, bool hash = true, bool nameSize = true,
            int type = 9, string mime = pdf, string? mediaName = null, DateOnly? date = null)
        {
            var slot = d.AddAttachment(slotTime, false, date);
            d.AddRecord(recordTime, false, nameSize ? asset.FileName : "renamed-" + key + ".pdf", nameSize ? asset.Size : 1,
                hash ? asset.Base64Hash : null, key, date: date, messageType: type, mime: mime, mediaName: mediaName);

            return slot;
        }

        DocMinuteAsset = d.AddAsset(fileName: "report.pdf");
        DocMinute = Doc("09:00:00", "09:00:10", DocMinuteAsset, "DOCKEYMINUTE", mediaName: "Quarterly report.pdf");

        DocDayAsset = d.AddAsset(fileName: "scan.pdf");
        DocDay = Doc("09:10:00", "09:11:00", DocDayAsset, "DOCKEYDAY", nameSize: false);

        PhotoNoDate = Doc("09:20:00", "09:20:00", d.AddAsset(fileName: "photo-x.jpg"), "DOCKEYPHOTO", type: 1, mime: "image/jpeg");

        DatedDoc = Doc("09:30:00", "09:30:00", d.AddAsset(A.AddDays(-8)), "DOCKEYDATED");

        DocNameSize = Doc("09:35:00", "09:35:00", d.AddAsset(fileName: "minutes.docx"), "DOCKEYNAMESIZE", hash: false);

        DocGap = Doc("09:40:00", "09:45:00", d.AddAsset(fileName: "late.pdf"), "DOCKEYGAP");

        DocEmpty = Doc("09:50:00", "09:50:00", d.AddAsset(fileName: "empty.pdf", size: 0), "DOCKEYEMPTY");

        // Disproven while it was a same-day candidate; later its copy turns out to be an undated document.
        var disprovenAsset = d.AddAsset(A);
        DocDisproven = Doc("10:00:00", "10:00:00", disprovenAsset, "DOCKEYDISPROVEN");

        var resolvedAsset = d.AddAsset(fileName: "contract.pdf");
        DocResolved = Doc("10:10:00", "10:10:00", resolvedAsset, "DOCKEYRESOLVED");

        // Day B: one more workspace message than msgstore messages.
        DocUnusable = Doc("11:00:00", "11:00:00", d.AddAsset(fileName: "other.pdf"), "DOCKEYUNUSABLE", date: B);
        d.AddWorkspaceMessage("11:05:00", false, B);

        d.RunPhase7();
        d.Disprove(DocDisproven, disprovenAsset.ID);
        d.Workspace(
            $"""
            UPDATE MediaFile SET FileDate = NULL, FileName = 'letter.docx', RelativePath = RelativePath || '-letter.docx'
            WHERE MediaFileID IN (SELECT MediaFileID FROM MediaAssetFile WHERE MediaAssetID = {disprovenAsset.ID});
            """);
        d.RunPhase7();
        d.ConfirmByHand(DocResolved, resolvedAsset.ID);
        d.CloseAll();
    }

    internal string[] Args(string mode, params string[] extra) =>
    [
        mode, "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath, "--conversation", "1",
        "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture), "--utc-offset", SyntheticData.OffsetText,
        "--local-participant", "1", .. extra,
    ];

    internal string[] PlanArgs(params string[] extra) => Args("plan", ["--rule", "documents", "--manifest-out", Manifest, .. extra]);

    internal string[] ApplyArgs(string manifest, string sha) =>
    [
        "apply", "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath,
        "--manifest", manifest, "--manifest-sha256", sha, "--i-have-a-backup",
    ];

    internal string[] UndoArgs(string manifest, string sha) =>
        ["undo-batch", "--workspace", Data.WorkspacePath, "--manifest", manifest, "--manifest-sha256", sha, "--i-have-a-backup"];

    internal DocumentsPlanResult Plan() => DocumentsPlanner.Plan(Options.Parse(PlanArgs()));

    internal WriteResult Apply(string sha) => Applier.Apply(Options.Parse(ApplyArgs(Manifest, sha)));

    public void Dispose() => Data.Dispose();
}

public sealed class DocumentsTests
{
    private static string Outside(OutsideEligibility e, DayVerdict? day = null) =>
        OutsideCandidateSet.Reason(new OutsideAssessment(0, e, day, false, false, null, null, null, null, null, null, null, null, null, null, 0, 0));

    [Fact]
    public void Type_nine_no_date_slots_are_included_and_everything_else_is_excluded_by_reason()
    {
        using var s = new DocumentsScenario();

        var r = s.Plan();
        var rows = Manifest.Parse(File.ReadAllBytes(s.Manifest)).Rows.ToDictionary(x => x.AttachmentID);

        Assert.Equal([s.DocMinute, s.DocDay], rows.Keys.Order());
        Assert.Equal((PairingRule?)PairingRule.Minute, rows[s.DocMinute].PairedBy);
        Assert.Equal(MatchKind.HashAndNameSize, rows[s.DocMinute].Kind);
        Assert.Equal((PairingRule?)PairingRule.DaySequence, rows[s.DocDay].PairedBy);
        Assert.Equal(MatchKind.HashOnly, rows[s.DocDay].Kind);

        Assert.Equal(1, r.Excluded[Documents.NotDocument]);
        Assert.Equal(1, r.Excluded[Documents.Dated]);
        Assert.Equal(1, r.Excluded[Outside(OutsideEligibility.NameSizeOnly)]);
        Assert.Equal(1, r.Excluded[Outside(OutsideEligibility.MinuteGap)]);
        Assert.Equal(1, r.Excluded[Outside(OutsideEligibility.EmptyAsset)]);
        Assert.Equal(1, r.Excluded[Outside(OutsideEligibility.Disproven)]);
        Assert.Equal(1, r.Excluded[Reasons.AlreadyResolved]);
        Assert.Equal(1, r.Excluded[Outside(OutsideEligibility.DayNotUsable, DayVerdict.CountMismatch)]);
        Assert.Equal(10, r.Attachments);
        Assert.Equal(r.Attachments, r.Rows + r.Excluded.Total);
        Assert.Equal(2, r.ByMime["application"]);
    }

    [Fact]
    public void A_dated_document_follows_the_outside_rule_not_this_one()
    {
        using var s = new DocumentsScenario();

        var outside = OutsidePlanner.Plan(Options.Parse(s.Args(
            "plan", "--rule", "outside-candidate-set", "--manifest-out", Path.Combine(s.Data.DirectoryPath, "outside.tsv"))));

        Assert.Equal([s.DatedDoc], Manifest.Parse(File.ReadAllBytes(Path.Combine(s.Data.DirectoryPath, "outside.tsv"))).Rows.Select(x => x.AttachmentID));
        Assert.Equal(1, outside.Rows);

        s.Plan();
        Assert.DoesNotContain(Manifest.Parse(File.ReadAllBytes(s.Manifest)).Rows, x => x.AttachmentID == s.DatedDoc);
    }

    [Fact]
    public void Stdout_is_counts_only_and_the_sample_shows_document_names()
    {
        using var s = new DocumentsScenario();

        var (exit, stdout, stderr) = Scenario.Cli(s.PlanArgs("--sample-out", s.SamplePath, "--seed", "7"));

        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.Matches(@"new manifest rows\s+2", stdout);
        Assert.Matches(@"private sample rows written\s+2", stdout);
        Assert.DoesNotContain(".pdf", stdout);
        Assert.DoesNotContain("DOCKEY", stdout);
        Assert.DoesNotContain(s.Data.DirectoryPath, stdout);

        var text = File.ReadAllText(s.SamplePath);
        Assert.Contains($"| {s.DocMinute} | 2026-03-02 | 09:00 | received | 1 of 9 | report.pdf | Quarterly report.pdf | 2048 | application/pdf | hash and name+size |  |", text);
        Assert.Contains($"| {s.DocDay} | 2026-03-02 | 09:10 | received | 2 of 9 | renamed-DOCKEYDAY.pdf | - | 1 | application/pdf | hash only |  |", text);
    }

    [Fact]
    public void Apply_uses_the_documents_basis_writes_only_the_resolution_tables_and_is_read_by_day_sequence()
    {
        using var s = new DocumentsScenario();
        var sha = s.Plan().Sha256;
        var tag = sha[..12];
        var others = s.Data.DumpExcept("AttachmentResolutionEvent", "Attachment");

        var r = s.Apply(sha);

        Assert.Equal(2, r.Done);
        Assert.True(r.Reconciled);
        Assert.Equal(others, s.Data.DumpExcept("AttachmentResolutionEvent", "Attachment"));
        Assert.Equal(
            "WhatsApp msgstore record (message key_id DOCKEYMINUTE), paired by minute, file matched by hash and name+size, " +
            $"document (original name, no date check); import batch {tag},ExternalProvenanceOrGroundTruth,Angie (msgstore import),<null>",
            s.Data.Rows($"SELECT IndependentBasisDescription, IndependentBasisType, ActorLabel, SourceRunID FROM AttachmentResolutionEvent WHERE AttachmentID = {s.DocMinute};").Single());
        Assert.Contains("paired by day message sequence, file matched by hash only, document (original name, no date check)",
            s.Data.Rows($"SELECT IndependentBasisDescription FROM AttachmentResolutionEvent WHERE AttachmentID = {s.DocDay};").Single());

        // Rerun: no-op.
        var again = s.Apply(sha);
        Assert.Equal(2, again.AlreadyDone);

        // The day-sequence prior check parses the new basis text and agrees.
        var day = DayPlanner.Plan(Options.Parse(s.Args("plan", "--rule", "day-sequence", "--manifest-out", Path.Combine(s.Data.DirectoryPath, "dayseq.tsv"))));
        Assert.Equal(0, day.Alignment.PriorUnreadable);
        Assert.Equal(0, day.Alignment.PriorDisagree);
        Assert.Equal(2, day.Alignment.PriorAgree);
    }

    [Fact]
    public void Apply_recheck_skips_a_slot_that_is_no_longer_a_document()
    {
        using var s = new DocumentsScenario();
        var sha = s.Plan().Sha256;

        // The minute-paired document's record turns out not to be a document.
        s.Data.Msgstore("UPDATE message SET message_type = 1 WHERE key_id = 'DOCKEYMINUTE';");
        s.Data.CloseAll();

        var first = s.Apply(sha);
        Assert.Equal(1, first.Other[Reasons.NoLongerEligible]);
        Assert.Equal(1, first.Done);
        Assert.True(first.Reconciled);
    }

    [Fact]
    public void Apply_recheck_skips_when_the_day_no_longer_lines_up()
    {
        using var s = new DocumentsScenario();
        var sha = s.Plan().Sha256;

        s.Data.AddNonMediaMessage("12:00:00", false, 0, "late", DocumentsScenario.A);
        s.Data.CloseAll();

        var r = s.Apply(sha);

        Assert.Equal(2, r.Other[Reasons.DayNoLongerUsable]);
        Assert.Equal(0, r.Done);
        Assert.True(r.Reconciled);
    }

    [Fact]
    public void Earlier_rules_manifests_and_undo_are_unaffected()
    {
        using var s = new DocumentsScenario();

        var outsidePath = Path.Combine(s.Data.DirectoryPath, "outside.tsv");
        var outsideSha = OutsidePlanner.Plan(Options.Parse(s.Args("plan", "--rule", "outside-candidate-set", "--manifest-out", outsidePath))).Sha256;
        var outsideLines = Encoding.UTF8.GetString(File.ReadAllBytes(outsidePath)).Split('\n');
        Assert.Equal("rule\toutside-candidate-set", outsideLines[2]);
        Assert.Equal(MsgstoreImport.Manifest.OutsideHeader, outsideLines[8]);

        Applier.Apply(Options.Parse(s.ApplyArgs(outsidePath, outsideSha)));

        var sha = s.Plan().Sha256;
        var lines = Encoding.UTF8.GetString(File.ReadAllBytes(s.Manifest)).Split('\n');
        Assert.Equal("rule\tdocuments", lines[2]);
        Assert.Equal(MsgstoreImport.Manifest.DocumentsHeader, lines[8]);
        s.Apply(sha);

        var undo = Undoer.Undo(Options.Parse(s.UndoArgs(s.Manifest, sha)));
        Assert.Equal(2, undo.Done);
        Assert.Equal("Resolved", s.Data.Rows($"SELECT ResolutionStatus FROM Attachment WHERE AttachmentID = {s.DatedDoc};").Single());

        var outsideUndo = Undoer.Undo(Options.Parse(s.UndoArgs(outsidePath, outsideSha)));
        Assert.Equal(1, outsideUndo.Done);
        Assert.Equal("Resolved", s.Data.Rows($"SELECT ResolutionStatus FROM Attachment WHERE AttachmentID = {s.DocResolved};").Single());
        Assert.Equal(1, s.Data.Count("SELECT COUNT(*) FROM Attachment WHERE ResolutionStatus = 'Resolved';"));
    }
}
