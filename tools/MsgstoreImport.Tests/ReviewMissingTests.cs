using System.Globalization;
using System.Text;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport.Tests;

/// <summary>CH-13 review-missing: plan and apply on synthetic data only.</summary>
public sealed class ReviewMissingTests
{
    private static readonly DateOnly A = SyntheticData.Day;

    private const string Actor = "Angie (accepted missing list)";

    private sealed class Fixture : IDisposable
    {
        internal SyntheticData Data { get; } = new();
        internal string ManifestPath => Path.Combine(Data.DirectoryPath, "missing-manifest.tsv");

        internal long NoFile, NotRecovered, SizeDiffers, DatedAfter, NameSize, Confirmed, Reviewed;
        internal SyntheticData.Asset NameSizeAsset = null!;

        /// <summary>The expected manifest: attachment and reason category, in AttachmentID order.</summary>
        internal List<(long, string)> Expected = [];

        internal Fixture()
        {
            var d = Data;

            long Slot(string time, string? file, long? size, string? hash, string key)
            {
                var slot = d.AddAttachment(time, false);
                d.AddRecord(time, false, file, size, hash, key);

                return slot;
            }

            NoFile = Slot("09:00:00", null, null, null, "MKEYNOFILE");
            NotRecovered = Slot("09:05:00", "never-recovered.jpg", 77, d.UnknownHash(), "MKEYNOTRECOVERED");

            var sized = d.AddAsset(A);
            SizeDiffers = Slot("09:10:00", sized.FileName, sized.Size + 1, null, "MKEYSIZE");

            var later = d.AddAsset(A.AddDays(3));
            DatedAfter = Slot("09:15:00", later.FileName, later.Size, later.Base64Hash, "MKEYLATER");

            NameSizeAsset = d.AddAsset(A);
            NameSize = Slot("09:20:00", NameSizeAsset.FileName, NameSizeAsset.Size, null, "MKEYNAMESIZE");

            var confirmedAsset = d.AddAsset(A);
            Confirmed = d.AddAttachment("09:40:00", false);
            d.AddRecordFor(confirmedAsset, "09:40:00", false, "MKEYCONFIRMED");

            Reviewed = Slot("09:45:00", "gone-too.jpg", 12, d.UnknownHash(), "MKEYREVIEWED");

            d.RunPhase7();
            d.ConfirmByHand(Confirmed, confirmedAsset.ID);
            ReviewByOther(Reviewed);
            d.CloseAll();

            Expected =
            [
                (NoFile, MissingReview.WhatsAppKeptNoFile),
                (NotRecovered, MissingReview.NoFileInAnyCopy),
                (SizeDiffers, MissingReview.SameNameDifferentSize),
                (DatedAfter, MissingReview.DatedAfterMessage),
                (NameSize, "other: " + Unresolved.NameSizeNotReviewed),
            ];
        }

        internal long RunID => Data.Count("SELECT MAX(RunID) FROM Phase7Run;");

        /// <summary>A review recorded by hand in the viewer, not by this tool.</summary>
        internal void ReviewByOther(long attachment)
        {
            var run = RunID;
            SqliteConnection.ClearAllPools();

            new Phase7ReviewService().Record(new Phase7ReviewRequest
            {
                DatabasePath = Data.WorkspacePath,
                AttachmentID = attachment,
                SourceRunID = run,
                ReviewNotes = "Looked by eye (synthetic).",
                ActorLabel = "Synthetic tester",
            });

            SqliteConnection.ClearAllPools();
        }

        internal string[] PlanArgs(int expected, string? manifestOut = null) =>
        [
            "plan", "--rule", "review-missing", "--expected-count", expected.ToString(CultureInfo.InvariantCulture),
            "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath, "--conversation", "1",
            "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture), "--utc-offset", SyntheticData.OffsetText,
            "--local-participant", "1", "--manifest-out", manifestOut ?? ManifestPath,
        ];

        internal string[] ApplyArgs(string sha) =>
        [
            "apply", "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath,
            "--manifest", ManifestPath, "--manifest-sha256", sha, "--i-have-a-backup",
        ];

        internal string Plan() => MissingReview.Plan(Options.Parse(PlanArgs(Expected.Count))).Sha256;

        internal WriteResult Apply(string sha) => MissingReview.Apply(Options.Parse(ApplyArgs(sha)));

        internal List<string> ToolReviews() =>
            Data.Rows($"SELECT AttachmentID, SourceRunID, ReviewOutcome, ReviewNotes, ActorLabel FROM AttachmentReviewEvent WHERE ActorLabel = '{Actor}' ORDER BY AttachmentID;");

        internal Phase7ReconstructionState Reconstruct(long attachment)
        {
            SqliteConnection.ClearAllPools();

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(Data.WorkspacePath);
            var state = new Phase7ReconstructionReader().Read(connection, attachment);
            connection.Close();
            SqliteConnection.ClearAllPools();

            return state;
        }

        public void Dispose()
        {
            MissingReview.TodayForTests = null;
            Data.Dispose();
        }
    }

    [Fact]
    public void Plan_refuses_a_count_mismatch_and_writes_nothing()
    {
        using var f = new Fixture();

        var workspace = PlanTests.Sha(f.Data.WorkspacePath);
        var files = Directory.GetFiles(f.Data.DirectoryPath).Order().ToList();

        foreach (var wrong in new[] { f.Expected.Count - 1, f.Expected.Count + 1, 0 })
        {
            var (exit, stdout, stderr) = Scenario.Cli(f.PlanArgs(wrong));

            Assert.Equal(2, exit);
            Assert.Equal("", stdout);
            Assert.Contains("5 unresolved, unreviewed attachments qualify", stderr);
            Assert.Contains("Nothing was written", stderr);
        }

        Assert.False(File.Exists(f.ManifestPath));
        Assert.Equal(files, Directory.GetFiles(f.Data.DirectoryPath).Order());
        Assert.Equal(workspace, PlanTests.Sha(f.Data.WorkspacePath));
    }

    [Fact]
    public void Plan_takes_exactly_the_unresolved_unreviewed_slots_with_their_reason_category()
    {
        using var f = new Fixture();

        var workspace = PlanTests.Sha(f.Data.WorkspacePath);
        var (exit, stdout, stderr) = Scenario.Cli(f.PlanArgs(f.Expected.Count));

        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.Equal(workspace, PlanTests.Sha(f.Data.WorkspacePath));

        var manifest = MissingManifest.Parse(File.ReadAllBytes(f.ManifestPath));
        var run = f.RunID;

        Assert.Equal(f.Expected, manifest.Rows.Select(r => (r.AttachmentID, r.Reason)));
        Assert.All(manifest.Rows, r => Assert.Equal(run, r.SourceRunID));
        Assert.DoesNotContain(manifest.Rows, r => r.AttachmentID == f.Confirmed || r.AttachmentID == f.Reviewed);

        Assert.Matches(@"unresolved attachments in the conversation\s+6", stdout);
        Assert.Matches(@"already reviewed \(excluded\)\s+1", stdout);
        Assert.Matches(@"manifest rows \(to record as missing\)\s+5", stdout);
        Assert.Matches(@"no file in any copy\s+1", stdout);
        Assert.Matches(@"reconciliation\s+OK", stdout);
        Assert.Contains("manifest SHA-256: " + Manifest.Sha256Hex(File.ReadAllBytes(f.ManifestPath)), stdout);
        Assert.DoesNotContain("MKEY", stdout);
        Assert.DoesNotContain(".jpg", stdout);
        Assert.DoesNotContain(f.Data.DirectoryPath, stdout);
    }

    [Fact]
    public void Apply_records_exactly_one_review_per_row_and_the_reader_reports_it()
    {
        using var f = new Fixture();
        MissingReview.TodayForTests = () => new DateOnly(2026, 10, 15);

        var sha = f.Plan();
        var tag = sha[..12];
        var resolutionEvents = f.Data.Count("SELECT COUNT(*) FROM AttachmentResolutionEvent;");

        var (exit, stdout, stderr) = Scenario.Cli(f.ApplyArgs(sha));

        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.Matches(@"reviewed \(recorded as missing\)\s+5", stdout);
        Assert.Matches(@"reconciliation against manifest rows\s+OK", stdout);
        Assert.DoesNotContain("MKEY", stdout);

        var run = f.RunID;
        var reviews = f.ToolReviews();

        Assert.Equal(
            f.Expected.Select(e =>
                $"{e.Item1},{run},ReviewedInsufficientEvidence,Accepted as missing 2026-10-15 (CH-13): {e.Item2}; batch {tag},{Actor}"),
            reviews);

        foreach (var (attachment, _) in f.Expected)
        {
            var state = f.Reconstruct(attachment);

            Assert.Equal(Phase7ResolutionStatus.Unresolved, state.Status);
            Assert.Equal(Phase7UnresolvedPlaceholderReason.ReviewedInsufficientEvidence, state.PlaceholderReason);
        }

        // A review never resolves anything, and nothing outside the manifest was reviewed.
        Assert.Equal(resolutionEvents, f.Data.Count("SELECT COUNT(*) FROM AttachmentResolutionEvent;"));
        Assert.Equal(1, f.Data.Count("SELECT COUNT(*) FROM Attachment WHERE ResolutionStatus = 'Resolved';"));
        Assert.Equal(f.Expected.Count + 1, f.Data.Count("SELECT COUNT(*) FROM AttachmentReviewEvent;"));
    }

    [Fact]
    public void Apply_refuses_a_wrong_digest_or_a_tampered_manifest_and_writes_nothing()
    {
        using var f = new Fixture();
        var sha = f.Plan();
        var wrong = (sha[0] == '0' ? "1" : "0") + sha[1..];
        var before = PlanTests.Sha(f.Data.WorkspacePath);

        var (exit, stdout, stderr) = Scenario.Cli(f.ApplyArgs(wrong));

        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.Contains("SHA-256 does not match", stderr);

        var text = File.ReadAllText(f.ManifestPath).Replace(MissingReview.WhatsAppKeptNoFile, "edited reason");
        File.WriteAllText(f.ManifestPath, text, new UTF8Encoding(false));

        (exit, _, stderr) = Scenario.Cli(f.ApplyArgs(sha));

        Assert.Equal(2, exit);
        Assert.Contains("SHA-256 does not match", stderr);
        Assert.Equal(before, PlanTests.Sha(f.Data.WorkspacePath));
        Assert.Empty(f.ToolReviews());
    }

    [Fact]
    public void Rerun_counts_rows_already_done_and_writes_nothing_more()
    {
        using var f = new Fixture();
        var sha = f.Plan();

        Assert.Equal(f.Expected.Count, f.Apply(sha).Done);

        var r = f.Apply(sha);

        Assert.Equal(0, r.Done);
        Assert.Equal(f.Expected.Count, r.AlreadyDone);
        Assert.Equal(0, r.Other.Total);
        Assert.True(r.Reconciled);
        Assert.Equal(f.Expected.Count, f.ToolReviews().Count);
    }

    [Fact]
    public void Slots_confirmed_or_reviewed_after_the_plan_are_skipped_and_counted()
    {
        using var f = new Fixture();
        var sha = f.Plan();

        f.Data.ConfirmByHand(f.NameSize, f.NameSizeAsset.ID);
        f.ReviewByOther(f.NoFile);
        f.Data.CloseAll();

        var r = f.Apply(sha);

        Assert.Equal(f.Expected.Count - 2, r.Done);
        Assert.Equal(1, r.Other[MissingReview.SkipResolved]);
        Assert.Equal(1, r.Other[MissingReview.SkipReviewed]);
        Assert.True(r.Reconciled);
        Assert.DoesNotContain(f.ToolReviews(), row => row.StartsWith($"{f.NameSize},", StringComparison.Ordinal));
        Assert.DoesNotContain(f.ToolReviews(), row => row.StartsWith($"{f.NoFile},", StringComparison.Ordinal));
        Assert.Equal("Resolved", f.Data.Rows($"SELECT ResolutionStatus FROM Attachment WHERE AttachmentID = {f.NameSize};").Single());
    }

    [Fact]
    public void A_later_confirm_still_works_after_the_review()
    {
        using var f = new Fixture();
        f.Apply(f.Plan());

        f.Data.ConfirmByHand(f.NameSize, f.NameSizeAsset.ID);

        Assert.Equal(Phase7ResolutionStatus.Resolved, f.Reconstruct(f.NameSize).Status);
    }

    [Fact]
    public void Undo_batch_refuses_a_review_missing_manifest()
    {
        using var f = new Fixture();
        var sha = f.Plan();
        f.Apply(sha);
        var before = PlanTests.Sha(f.Data.WorkspacePath);

        var (exit, stdout, stderr) = Scenario.Cli(
            ["undo-batch", "--workspace", f.Data.WorkspacePath, "--manifest", f.ManifestPath, "--manifest-sha256", sha, "--i-have-a-backup"]);

        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.Contains("append-only", stderr);
        Assert.Equal(before, PlanTests.Sha(f.Data.WorkspacePath));
    }

    [Fact]
    public void Expected_count_is_required_for_review_missing_and_refused_elsewhere()
    {
        using var f = new Fixture();

        var without = f.PlanArgs(5).Where((_, i) => i is not (3 or 4)).ToArray();
        Assert.Throws<OptionsException>(() => Options.Parse(without));

        var minute = f.PlanArgs(5).Where((_, i) => i is not (1 or 2)).ToArray();
        Assert.Throws<OptionsException>(() => Options.Parse(minute));
    }

    [Fact]
    public void Earlier_import_manifests_still_apply_through_the_import_path()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;

        Assert.False(MissingManifest.IsOne(s.ManifestPath));

        var (exit, stdout, _) = Scenario.Cli(s.ApplyArgs(sha));

        Assert.Equal(0, exit);
        Assert.Matches(@"confirmed\s+7", stdout);
        Assert.Equal(0, s.Data.Count("SELECT COUNT(*) FROM AttachmentReviewEvent;"));
    }
}
