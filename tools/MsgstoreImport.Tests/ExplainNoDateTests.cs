using System.Globalization;

namespace MsgstoreImport.Tests;

public sealed class ExplainNoDateTests
{
    private sealed class Fixture : IDisposable
    {
        internal SyntheticData Data { get; } = new();
        internal string DetailPath => Path.Combine(Data.DirectoryPath, "nodate-detail.md");
        internal long Control;

        internal Fixture()
        {
            var d = Data;
            var minute = 0;

            long Slot(SyntheticData.Asset asset)
            {
                var time = $"09:{(minute++).ToString("D2", CultureInfo.InvariantCulture)}:00";
                var slot = d.AddAttachment(time, false);
                d.AddRecordFor(asset, time, false, "NKEY" + slot.ToString(CultureInfo.InvariantCulture));

                return slot;
            }

            Slot(d.AddAsset(fileName: "IMG_20260301_101112.jpg"));      // camera, -1
            Slot(d.AddAsset(fileName: "20260302_080000.jpg"));          // camera, 0
            Slot(d.AddAsset(fileName: "PXL_20260115_123456789.jpg"));   // camera, -46
            Slot(d.AddAsset(fileName: "Screenshot_2026-02-20-10-00-00.png")); // camera, -10
            Slot(d.AddAsset(fileName: "VID_20260310_101010.mp4"));      // camera, +8
            Slot(d.AddAsset(fileName: "holiday 20260225.jpg"));         // other date, -5
            Slot(d.AddAsset(fileName: "report.pdf"));                   // document
            Slot(d.AddAsset(fileName: "voice.opus"));                   // audio

            var plain = d.AddAsset(fileName: "photo-abc.jpg");          // no date, plus a copy in source 2
            Slot(plain);
            d.AddUndatedCopy(plain, "copy of photo.jpg", d.AddMediaSource("Second synthetic source"));

            var shared = d.AddAsset(fileName: "shared-pic.jpg");        // no date, two slots
            Slot(shared);
            Slot(shared);

            // Control: a dated older file is an outside-rule row, not part of this population.
            Control = Slot(d.AddAsset(SyntheticData.Day.AddDays(-12)));

            d.RunPhase7();
            d.CloseAll();
        }

        internal string[] Args(string? detail = null) =>
        [
            "explain-nodate", "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath, "--conversation", "1",
            "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture), "--utc-offset", SyntheticData.OffsetText,
            "--local-participant", "1", "--detail-out", detail ?? DetailPath,
        ];

        public void Dispose() => Data.Dispose();
    }

    [Theory]
    [InlineData("IMG_20240131_101112.jpg", "CameraDate", "2024-01-31")]
    [InlineData("VID_20240131_101112.mp4", "CameraDate", "2024-01-31")]
    [InlineData("20240131_101112.jpg", "CameraDate", "2024-01-31")]
    [InlineData("PXL_20240131_1011123.jpg", "CameraDate", "2024-01-31")]
    [InlineData("Screenshot_20240131-101112.png", "CameraDate", "2024-01-31")]
    [InlineData("Screenshot_2024-01-31-10-11-12.png", "CameraDate", "2024-01-31")]
    [InlineData("VID_20240131.mp4", "CameraDate", "2024-01-31")]
    [InlineData("party 20240131.jpg", "OtherDate", "2024-01-31")]
    [InlineData("scan 2024-01-31.jpg", "OtherDate", "2024-01-31")]
    [InlineData("invoice.PDF", "Document", null)]
    [InlineData("notes.docx", "Document", null)]
    [InlineData("voice.opus", "Audio", null)]
    [InlineData("song.mp3", "Audio", null)]
    [InlineData("photo-abc.jpg", "NoDate", null)]
    [InlineData("IMG_20251399_101112.jpg", "NoDate", null)]
    [InlineData("phone 0821234567.jpg", "NoDate", null)]
    public void Each_name_is_classified(string name, string pattern, string? date)
    {
        var (actual, embedded) = ExplainNoDate.Classify(name);

        Assert.Equal(Enum.Parse<NamePattern>(pattern), actual);
        Assert.Equal(date is null ? null : DateOnly.Parse(date, CultureInfo.InvariantCulture), embedded);
    }

    [Fact]
    public void Population_patterns_gaps_sharing_and_sources_are_counted()
    {
        using var f = new Fixture();

        var rows = ExplainNoDate.Compute(Options.Parse(f.Args()));

        Assert.Equal(11, rows.Count);
        Assert.DoesNotContain(rows, r => r.Slot.AttachmentID == f.Control);
        Assert.Equal(5, rows.Count(r => r.Pattern == NamePattern.CameraDate));
        Assert.Equal(1, rows.Count(r => r.Pattern == NamePattern.OtherDate));
        Assert.Equal(1, rows.Count(r => r.Pattern == NamePattern.Document));
        Assert.Equal(1, rows.Count(r => r.Pattern == NamePattern.Audio));
        Assert.Equal(3, rows.Count(r => r.Pattern == NamePattern.NoDate));

        var report = ExplainNoDate.Report(rows);

        Assert.Matches(@"slots\s+11", report);
        Assert.Matches(@"image / type 1\s+11", report);
        Assert.Matches(@"hash and name\+size\s+11", report);
        Assert.Matches(@"received\s+11", report);

        // Camera gaps: 0, -1, -10, -46, +8, one per bucket. Other date: -5.
        var camera = report[report.IndexOf("(camera/screenshot style with embedded date):", StringComparison.Ordinal)..];
        Assert.Matches(@"same day \(gap 0\)\s+1", camera);
        Assert.Matches(@"\+/-1 day\s+1", camera);
        Assert.Matches(@"older by 2-30 days\s+1", camera);
        Assert.Matches(@"older by more than 30 days\s+1", camera);
        Assert.Matches(@"newer by more than 1 day\s+1", camera);

        var other = report[report.IndexOf("(other name with a plausible date):", StringComparison.Ordinal)..];
        Assert.Matches(@"older by 2-30 days\s+1", other);
        Assert.Matches(@"same day \(gap 0\)\s+0", other);

        Assert.Matches(@"assets shared by 2\+ of these slots\s+1", report);
        Assert.Matches(@"source 1\s+10 / 11", report);
        Assert.Matches(@"source 2\s+1 / 1", report);
    }

    [Fact]
    public void Stdout_is_counts_only_and_the_detail_file_holds_the_names()
    {
        using var f = new Fixture();

        var (exit, stdout, stderr) = Scenario.Cli(f.Args());

        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.DoesNotContain(".jpg", stdout);
        Assert.DoesNotContain("IMG_", stdout);
        Assert.DoesNotContain("NKEY", stdout);
        Assert.DoesNotContain(f.Data.DirectoryPath, stdout);

        var detail = File.ReadAllText(f.DetailPath);
        Assert.Contains("| IMG_20260301_101112.jpg | IMG_20260301_101112.jpg (1) | 2026-03-01 | camera/screenshot style with embedded date | -1 |", detail);
        Assert.Contains("photo-abc.jpg (1); copy of photo.jpg (2)", detail);
        Assert.Contains("| report.pdf | report.pdf (1) | - | document-like, no date | - |", detail);
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
        Assert.Equal(files.Append(f.DetailPath).Order(), Directory.GetFiles(f.Data.DirectoryPath).Order());

        // Never overwrites; never inside a repository.
        var (exit, _, stderr) = Scenario.Cli(f.Args());
        Assert.Equal(2, exit);
        Assert.Contains("never overwritten", stderr);

        var repo = Path.Combine(f.Data.DirectoryPath, "clone");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        (exit, _, stderr) = Scenario.Cli(f.Args(Path.Combine(repo, "d.md")));
        Assert.Equal(2, exit);
        Assert.Contains("--detail-out path is inside a repository", stderr);
    }
}
