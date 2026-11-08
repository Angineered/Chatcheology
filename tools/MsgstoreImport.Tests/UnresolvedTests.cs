using System.Globalization;

namespace MsgstoreImport.Tests;

public sealed class UnresolvedTests
{
    private static readonly DateOnly A = SyntheticData.Day, B = A.AddDays(1);

    private sealed class Fixture : IDisposable
    {
        internal SyntheticData Data { get; } = new();
        internal string ListPath => Path.Combine(Data.DirectoryPath, "unresolved-list.md");
        internal long Confirmed;
        internal List<long> Unresolved = [];

        internal Fixture()
        {
            var d = Data;

            long Slot(string time, string? file, long? size, string? hash, string key, DateOnly? date = null, int type = 1, string? recordTime = null)
            {
                var slot = d.AddAttachment(time, false, date);
                d.AddRecord(recordTime ?? time, false, file, size, hash, key, date: date, messageType: type);
                Unresolved.Add(slot);

                return slot;
            }

            Slot("09:00:00", null, null, null, "UKEYNOFILE");                                    // WhatsApp kept no file
            Slot("09:05:00", "never-recovered.jpg", 77, d.UnknownHash(), "UKEYNOTRECOVERED");   // not in any copy

            var sized = d.AddAsset(A);
            Slot("09:10:00", sized.FileName, sized.Size + 1, null, "UKEYSIZE");                 // same name, other size

            var later = d.AddAsset(A.AddDays(3));
            Slot("09:15:00", later.FileName, later.Size, later.Base64Hash, "UKEYLATER");        // dated after

            var nameSize = d.AddAsset(A);
            Slot("09:20:00", nameSize.FileName, nameSize.Size, null, "UKEYNAMESIZE");           // name+size only

            var disproven = d.AddAsset(A);
            var disprovenSlot = Slot("09:25:00", disproven.FileName, disproven.Size, disproven.Base64Hash, "UKEYDISPROVEN");

            var undated = d.AddAsset(fileName: "photo-nodate.jpg");
            Slot("09:30:00", undated.FileName, undated.Size, undated.Base64Hash, "UKEYNODATE"); // no date, not a document

            var empty = d.AddAsset(A.AddDays(-4), size: 0);
            Slot("09:35:00", empty.FileName, 0, empty.Base64Hash, "UKEYEMPTY");                 // empty file

            var confirmedAsset = d.AddAsset(A);
            Confirmed = d.AddAttachment("09:40:00", false);
            d.AddRecordFor(confirmedAsset, "09:40:00", false, "UKEYCONFIRMED");

            // Day B: one more workspace message, and the slot's record a minute later: not paired.
            Slot("10:00:00", "b.jpg", 5, d.UnknownHash(), "UKEYNOTPAIRED", B, recordTime: "10:01:00");
            d.AddWorkspaceMessage("10:05:00", false, B);

            d.RunPhase7();
            d.Disprove(disprovenSlot, disproven.ID);
            d.ConfirmByHand(Confirmed, confirmedAsset.ID);
            d.CloseAll();
        }

        internal string[] Args(string? list = null) =>
        [
            "list-unresolved", "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath, "--conversation", "1",
            "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture), "--utc-offset", SyntheticData.OffsetText,
            "--local-participant", "1", "--list-out", list ?? ListPath,
        ];

        public void Dispose() => Data.Dispose();
    }

    [Fact]
    public void Population_is_exactly_the_unresolved_slots_and_each_reason_appears()
    {
        using var f = new Fixture();

        var rows = MsgstoreImport.Unresolved.Compute(Options.Parse(f.Args()));
        var reasons = rows.ToDictionary(r => r.Attachment.AttachmentID, r => r.Reason);

        Assert.Equal(f.Unresolved.Order(), reasons.Keys.Order());
        Assert.DoesNotContain(f.Confirmed, reasons.Keys);
        Assert.Equal(f.Data.Count("SELECT COUNT(*) FROM Attachment WHERE ResolutionStatus = 'Unresolved';"), rows.Count);

        string[] expected =
        [
            MsgstoreImport.Unresolved.NoFile,
            MsgstoreImport.Unresolved.NotRecovered,
            MsgstoreImport.Unresolved.SizeDiffers,
            MsgstoreImport.Unresolved.DatedAfter,
            MsgstoreImport.Unresolved.NameSizeNotReviewed,
            MsgstoreImport.Unresolved.Disproven,
            MsgstoreImport.Unresolved.NoDateNotDocument,
            MsgstoreImport.Unresolved.Empty,
            MsgstoreImport.Unresolved.NotPaired(DayVerdict.CountMismatch),
        ];

        Assert.Equal(expected, f.Unresolved.Select(id => reasons[id]));
        Assert.Equal("not paired", rows.Single(r => r.Record is null).PairedBy);
        Assert.All(rows.Where(r => r.Record is not null), r => Assert.Equal("minute", r.PairedBy));
    }

    [Fact]
    public void The_private_list_has_one_row_per_slot_in_date_order()
    {
        using var f = new Fixture();

        Assert.Equal(0, Scenario.Cli(f.Args()).Exit);

        var lines = File.ReadAllText(f.ListPath).Split('\n');
        var rows = lines.Where(l => l.Length > 2 && char.IsDigit(l[2])).ToList();

        Assert.StartsWith("Every attachment still unresolved", lines[0]);
        Assert.Equal(f.Unresolved.Count, rows.Count);
        Assert.Equal(f.Unresolved, rows.Select(l => long.Parse(l.Split('|')[1].Trim(), CultureInfo.InvariantCulture)));
        Assert.Contains($"| {f.Unresolved[0]} | 2026-03-02 | 09:00 | received | 1 of 1 | minute | - (WhatsApp kept no file) | - | image/jpeg | 1 | - | WhatsApp kept no file |  |", rows[0]);
        Assert.Contains("| not paired | - | - | - | - | - | not paired (minute counts differ; day: message counts differ) |  |", rows[^1]);
        Assert.EndsWith("| found on a phone? (which phone / no) |", lines.Single(l => l.StartsWith("| AttachmentID", StringComparison.Ordinal)));
    }

    [Fact]
    public void Stdout_is_counts_only()
    {
        using var f = new Fixture();

        var (exit, stdout, stderr) = Scenario.Cli(f.Args());

        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.Matches(@"unresolved attachments\s+9", stdout);
        Assert.Matches(@"received\s+9", stdout);
        Assert.Matches(@"2026\s+9", stdout);
        Assert.Matches(@"not paired\s+1", stdout);
        Assert.Contains("WhatsApp kept no file", stdout);
        Assert.DoesNotContain("UKEY", stdout);
        Assert.DoesNotContain(".jpg", stdout);
        Assert.DoesNotContain(f.Data.DirectoryPath, stdout);
    }

    [Fact]
    public void It_is_read_only_and_guards_its_output()
    {
        using var f = new Fixture();

        var workspace = PlanTests.Sha(f.Data.WorkspacePath);
        var msgstore = PlanTests.Sha(f.Data.MsgstorePath);
        var files = Directory.GetFiles(f.Data.DirectoryPath).Order().ToList();

        Assert.Equal(0, Scenario.Cli(f.Args()).Exit);

        Assert.Equal(workspace, PlanTests.Sha(f.Data.WorkspacePath));
        Assert.Equal(msgstore, PlanTests.Sha(f.Data.MsgstorePath));
        Assert.Equal(files.Append(f.ListPath).Order(), Directory.GetFiles(f.Data.DirectoryPath).Order());

        var (exit, _, stderr) = Scenario.Cli(f.Args());
        Assert.Equal(2, exit);
        Assert.Contains("never overwritten", stderr);

        var repo = Path.Combine(f.Data.DirectoryPath, "clone");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        (exit, _, stderr) = Scenario.Cli(f.Args(Path.Combine(repo, "u.md")));
        Assert.Equal(2, exit);
        Assert.Contains("--list-out path is inside a repository", stderr);
    }
}
