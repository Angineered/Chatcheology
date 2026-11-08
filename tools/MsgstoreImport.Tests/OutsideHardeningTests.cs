using System.Globalization;

namespace MsgstoreImport.Tests;

/// <summary>Stricter checks for the outside-candidate-set rule, each on a small one-day fixture.</summary>
public sealed class OutsideHardeningTests
{
    private static readonly DateOnly Day = SyntheticData.Day;

    private static string ManifestPath(SyntheticData d) => Path.Combine(d.DirectoryPath, "outside.tsv");

    private static string[] PlanArgs(SyntheticData d) =>
    [
        "plan", "--rule", "outside-candidate-set", "--msgstore", d.MsgstorePath, "--workspace", d.WorkspacePath,
        "--conversation", "1", "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture),
        "--utc-offset", SyntheticData.OffsetText, "--local-participant", "1", "--manifest-out", ManifestPath(d),
    ];

    private static OutsidePlanResult Plan(SyntheticData d)
    {
        d.CloseAll();

        return OutsidePlanner.Plan(Options.Parse(PlanArgs(d)));
    }

    private static string Reason(OutsideEligibility e) =>
        OutsideCandidateSet.Reason(new OutsideAssessment(0, e, null, false, false, null, null, null, null, null, null, null, null, null, null, 0, 0));

    private static List<ManifestRow> Rows(SyntheticData d) => [.. Manifest.Parse(File.ReadAllBytes(ManifestPath(d))).Rows];

    [Fact]
    public void A_zero_byte_asset_is_excluded_under_its_own_reason()
    {
        using var d = new SyntheticData();

        var empty = d.AddAttachment("09:00:00", false);
        d.AddRecordFor(d.AddAsset(Day.AddDays(-5), size: 0), "09:00:00", false, "ZKEYEMPTY");

        var full = d.AddAttachment("09:10:00", false);
        d.AddRecordFor(d.AddAsset(Day.AddDays(-5)), "09:10:00", false, "ZKEYFULL");

        d.RunPhase7();
        var r = Plan(d);

        Assert.Equal(1, r.Excluded[Reason(OutsideEligibility.EmptyAsset)]);
        Assert.Equal([full], Rows(d).Select(x => x.AttachmentID));
        Assert.DoesNotContain(Rows(d), x => x.AttachmentID == empty);
        Assert.Contains("asset is empty (zero bytes)", OutsidePlanner.Report(r));
    }

    [Fact]
    public void Gap_zero_is_counted_on_its_own_line()
    {
        using var d = new SyntheticData();

        // A same-day file that is still outside the set: the asset's copy dates change after the
        // Phase 7 run, so the run's candidate set does not hold it. Gap 0 is "not explained by date".
        d.AddAttachment("09:00:00", false);
        d.AddRecordFor(d.AddAsset(Day.AddDays(-3)), "09:00:00", false, "GKEYOLD");

        var same = d.AddAttachment("09:10:00", false);
        var sameAsset = d.AddAsset(Day.AddDays(-30));
        d.AddRecordFor(sameAsset, "09:10:00", false, "GKEYSAME");

        d.RunPhase7();

        // After the run, the asset's copies move to the message's own day.
        d.Workspace($"UPDATE MediaFile SET FileDate = '{Day:yyyy-MM-dd}' WHERE MediaFileID IN (SELECT MediaFileID FROM MediaAssetFile WHERE MediaAssetID = {sameAsset.ID});");

        var r = Plan(d);

        Assert.Equal(1, r.SameDay);
        Assert.Equal(0, r.PlusMinusOne);
        Assert.Equal(1, r.Older2To30);
        Assert.Contains(Rows(d), x => x.AttachmentID == same && x.FileDate == Day);
        Assert.Matches(@"same day \(gap 0\) - not explained by date \(rows\)\s+1", OutsidePlanner.Report(r));
        Assert.Matches(@"assets named by 2\+ manifest rows \(informational\)\s+0", OutsidePlanner.Report(r));
    }

    [Fact]
    public void Disproven_and_name_size_only_slots_are_excluded()
    {
        using var d = new SyntheticData();

        // Disproven while it was a candidate (same-day file), then its copy turns out older and a new
        // run leaves it outside the set: the disproof on the older run still excludes it.
        var disproven = d.AddAttachment("09:00:00", false);
        var disprovenAsset = d.AddAsset(Day);
        d.AddRecordFor(disprovenAsset, "09:00:00", false, "XKEYDISPROVEN");

        // No hash in the record: name+size only.
        var nameSize = d.AddAttachment("09:10:00", false);
        var nameSizeAsset = d.AddAsset(Day.AddDays(-4));
        d.AddRecord("09:10:00", false, nameSizeAsset.FileName, nameSizeAsset.Size, null, "XKEYNAMESIZE");

        d.RunPhase7();
        d.Disprove(disproven, disprovenAsset.ID);
        var older = Day.AddDays(-6);
        d.Workspace(
            $"""
            UPDATE MediaFile
            SET FileDate = '{older:yyyy-MM-dd}',
                FileName = replace(FileName, '{Day:yyyyMMdd}', '{older:yyyyMMdd}'),
                RelativePath = replace(RelativePath, '{Day:yyyyMMdd}', '{older:yyyyMMdd}')
            WHERE MediaFileID IN (SELECT MediaFileID FROM MediaAssetFile WHERE MediaAssetID = {disprovenAsset.ID});
            """);
        d.RunPhase7();

        var r = Plan(d);

        Assert.Equal(1, r.Excluded[Reason(OutsideEligibility.Disproven)]);
        Assert.Equal(1, r.Excluded[Reason(OutsideEligibility.NameSizeOnly)]);
        Assert.Empty(Rows(d));
        _ = nameSize;
    }

    [Fact]
    public void Apply_skips_as_changed_when_an_earlier_copy_appears_after_the_plan()
    {
        using var d = new SyntheticData();

        var slot = d.AddAttachment("09:00:00", false);
        var asset = d.AddAsset(Day.AddDays(-10));
        d.AddRecordFor(asset, "09:00:00", false, "CKEYCOPY");

        d.RunPhase7();
        var sha = Plan(d).Sha256;

        d.AddCopy(asset, Day.AddDays(-50));
        d.CloseAll();

        var r = Applier.Apply(Options.Parse(
        [
            "apply", "--msgstore", d.MsgstorePath, "--workspace", d.WorkspacePath, "--manifest", ManifestPath(d),
            "--manifest-sha256", sha, "--i-have-a-backup",
        ]));

        Assert.Equal(1, r.Other[Reasons.Changed]);
        Assert.Equal(0, r.Done);
        Assert.True(r.Reconciled);
        Assert.Equal("Unresolved", d.Rows($"SELECT ResolutionStatus FROM Attachment WHERE AttachmentID = {slot};").Single());
    }

    [Fact]
    public void Record_name_date_is_parsed_from_the_file_name_only()
    {
        Assert.Equal(new DateOnly(2024, 1, 31), OutsideCandidateSet.NameDate("IMG-20240131-WA0007.jpg"));
        Assert.Null(OutsideCandidateSet.NameDate("IMG-20251332-WA0007.jpg"));
        Assert.Null(OutsideCandidateSet.NameDate("photo-20240131.jpg"));
        Assert.Null(OutsideCandidateSet.NameDate(null));
    }
}
