using System.Globalization;

namespace MsgstoreImport.Tests;

/// <summary>Small one-day fixtures for the stricter rule 4 and the day-shape edge cases.</summary>
public sealed class DaySequenceHardeningTests
{
    private static readonly DateOnly Day = SyntheticData.Day;

    private static DayPlanResult Plan(SyntheticData d)
    {
        d.RunPhase7();
        d.CloseAll();

        return DayPlanner.Plan(Options.Parse(
        [
            "plan", "--rule", "day-sequence", "--msgstore", d.MsgstorePath, "--workspace", d.WorkspacePath,
            "--conversation", "1", "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture),
            "--utc-offset", SyntheticData.OffsetText, "--local-participant", "1",
            "--manifest-out", Path.Combine(d.DirectoryPath, "dayseq.tsv"),
        ]));
    }

    private static List<ManifestRow> Rows(SyntheticData d) =>
        [.. Manifest.Parse(File.ReadAllBytes(Path.Combine(d.DirectoryPath, "dayseq.tsv"))).Rows];

    [Fact]
    public void An_unconfirmed_minute_pair_that_disagrees_with_its_day_position_skips_the_day()
    {
        using var d = new SyntheticData();

        // Workspace: photo P (16:00), text (16:05). Msgstore: a text at 16:00:00, then P's media at 16:00:30.
        // The minute rule pairs P with the media record (one each in key 16:00 received); the day
        // sequence puts P opposite the text. P's asset is dated another day, so the minute batch
        // would never confirm it: the pair is unconfirmed.
        var p = d.AddAttachment("16:00:00", false);
        d.AddWorkspaceMessage("16:05:00", false);
        d.AddNonMediaMessage("16:00:00", false, 0, "text");
        d.AddRecordFor(d.AddAsset(Day.AddDays(4)), "16:00:30", false, "HKEYP");

        // A drifted slot on the same day that would otherwise be a new row.
        var q = d.AddAttachment("16:10:00", false);
        d.AddWorkspaceMessage("16:15:00", false);
        d.AddRecordFor(d.AddAsset(), "16:11:00", false, "HKEYQ");
        d.AddNonMediaMessage("16:15:00", false, 0, "text");

        var r = Plan(d);

        Assert.Equal(1, r.Alignment.Days[DayVerdict.MinutePairingDisagreement]);
        Assert.Equal(1, r.Alignment.MinuteDisagree);
        Assert.Equal(0, r.Rows);
        Assert.Equal(2, r.Excluded[DaySequence.SlotReason(DayEligibility.DaySkipped, DayVerdict.MinutePairingDisagreement)]);
        Assert.Contains("ATTENTION: 1 minute-rule pair(s) face a DIFFERENT msgstore record", DayPlanner.Report(r));
        _ = (p, q);
    }

    [Fact]
    public void A_message_with_two_attachments_skips_the_day()
    {
        using var d = new SyntheticData();

        var first = d.AddAttachment("09:00:00", true);
        d.Workspace("INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus) VALUES ((SELECT MAX(MessageID) FROM Message), 2, 'Unresolved');");
        d.AddRecordFor(d.AddAsset(), "09:01:00", true, "MKEYONE");

        var r = Plan(d);

        Assert.Equal(1, r.Alignment.Days[DayVerdict.MultiAttachmentMessage]);
        Assert.Equal(2, r.Excluded[DaySequence.SlotReason(DayEligibility.DaySkipped, DayVerdict.MultiAttachmentMessage)]);
        Assert.Equal(0, r.Rows);
        _ = first;
    }

    [Fact]
    public void Day_order_follows_sort_id_not_id_and_media_facing_text_is_counted_not_skipped()
    {
        using var d = new SyntheticData();

        var a = d.AddAttachment("17:00:00", true);
        var b = d.AddAttachment("17:05:00", true);
        d.AddWorkspaceMessage("17:10:00", true);

        var assetA = d.AddAsset();
        var assetB = d.AddAsset();

        // Inserted B first, so _id order is B, A; sort_id order is A, B.
        d.AddRecordFor(assetB, "17:06:00", true, "SKEYB", sortID: 2000);
        d.AddRecordFor(assetA, "17:01:00", true, "SKEYA", sortID: 1000);

        // A media row opposite the workspace text line (e.g. a sticker the export shows as text).
        d.AddRecord("17:10:00", true, "sticker.webp", 10, d.UnknownHash(), "SKEYSTICKER", sortID: 3000);

        var r = Plan(d);
        var rows = Rows(d).ToDictionary(x => x.AttachmentID);

        Assert.Equal(1, r.Alignment.Days[DayVerdict.Usable]);
        Assert.Equal(assetA.ID, rows[a].MediaAssetID);
        Assert.Equal(assetB.ID, rows[b].MediaAssetID);
        Assert.Equal(1, r.Alignment.MediaFacingText);
        Assert.Matches(@"msgstore media rows facing a workspace text line \(not a skip\)\s+1", DayPlanner.Report(r));
        Assert.Matches(@"minute-rule pairs agreeing with day position\s+0", DayPlanner.Report(r));
    }
}
