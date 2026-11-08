using System.Globalization;

namespace MsgstoreImport.Tests;

/// <summary>
/// A busy received minute shaped like the one being investigated: four attachments, four media
/// records, the last two sharing one hash (so two slots resolve to the same asset after import),
/// interleaved with non-media rows carrying text the probe must never read.
/// </summary>
public sealed class ProbeTests
{
    private const string Secret = "SECRET-MESSAGE-TEXT";

    private sealed class BusyMinute : IDisposable
    {
        internal SyntheticData Data { get; } = new();
        internal long[] Slots;
        internal SyntheticData.Asset[] Assets;
        internal string Probe => Path.Combine(Data.DirectoryPath, "probe.md");

        internal BusyMinute()
        {
            var d = Data;

            // Minute before: one sent attachment, one record.
            var before = d.AddAttachment("14:04:00", outgoing: true);
            var beforeAsset = d.AddAsset();
            d.AddRecordFor(beforeAsset, "14:04:30", true, "PKEYBEFORE");

            // Target minute, received: four slots.
            Slots = [d.AddAttachment("14:05:00", false), d.AddAttachment("14:05:00", false),
                     d.AddAttachment("14:05:00", false), d.AddAttachment("14:05:00", false)];
            Assets = [d.AddAsset(), d.AddAsset(), d.AddAsset()];

            d.AddRecordFor(Assets[0], "14:05:01", false, "PKEYONE");
            d.AddNonMediaMessage("14:05:02", false, 0, Secret);
            d.AddRecordFor(Assets[1], "14:05:05", false, "PKEYTWO");
            d.AddNonMediaMessage("14:05:06", false, 15, null); // e.g. a revoked item
            d.AddRecordFor(Assets[2], "14:05:09", false, "PKEYTHREE");
            d.AddRecordFor(Assets[2], "14:05:10", false, "PKEYFOUR", nameAndSize: false); // same hash again

            // Minute after: nothing in either direction.
            _ = before;

            d.RunPhase7();
            d.CloseAll();
        }

        internal string[] Args(string? output = null) =>
        [
            "probe", "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath,
            "--conversation", "1", "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture),
            "--utc-offset", SyntheticData.OffsetText, "--local-participant", "1",
            "--date", "2026-03-02", "--time", "14:05", "--probe-out", output ?? Probe,
        ];

        public void Dispose() => Data.Dispose();
    }

    /// <summary>Imports the minute through plan and apply, so the probe shows the batch's resolutions.</summary>
    private static string Import(BusyMinute m)
    {
        var manifest = Path.Combine(m.Data.DirectoryPath, "manifest.tsv");
        var plan = Planner.Plan(Options.Parse(
        [
            "plan", "--msgstore", m.Data.MsgstorePath, "--workspace", m.Data.WorkspacePath, "--conversation", "1",
            "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture), "--utc-offset", SyntheticData.OffsetText,
            "--local-participant", "1", "--manifest-out", manifest,
        ]));

        Applier.Apply(Options.Parse(
        [
            "apply", "--msgstore", m.Data.MsgstorePath, "--workspace", m.Data.WorkspacePath, "--manifest", manifest,
            "--manifest-sha256", plan.Sha256, "--i-have-a-backup",
        ]));

        return plan.Sha256[..12];
    }

    [Fact]
    public void Probe_stdout_carries_counts_only_and_shows_the_shared_hash()
    {
        using var m = new BusyMinute();
        Import(m);

        var (exit, stdout, stderr) = Scenario.Cli(m.Args());

        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.Matches(@"TARGET minute, received\s+attachments\s+4\s+media records\s+4\s+paired", stdout);
        Assert.Matches(@"minute before, sent\s+attachments\s+1\s+media records\s+1\s+paired", stdout);
        Assert.Matches(@"most records sharing one hash\s+2", stdout);
        Assert.Matches(@"most slots sharing one asset\s+2", stdout);
        Assert.Matches(@"distinct resolved assets\s+3", stdout);
        Assert.Matches(@"target minute: msgstore message rows\s+6", stdout);
        Assert.Matches(@"without a media row\s+2", stdout);

        Assert.DoesNotContain("PKEY", stdout);
        Assert.DoesNotContain("IMG-", stdout);
        Assert.DoesNotContain(m.Data.DirectoryPath, stdout);
        Assert.DoesNotContain(Secret, stdout);
    }

    [Fact]
    public void Probe_file_lists_both_tables_in_key_order_and_never_message_text()
    {
        using var m = new BusyMinute();
        var tag = Import(m);

        Scenario.Cli(m.Args());
        var text = File.ReadAllText(m.Probe);
        var lines = text.Split('\n');

        Assert.Contains("## TARGET KEY: 2026-03-02 14:05 received", text);
        Assert.Contains("## 2026-03-02 14:04 sent", text);
        Assert.Contains("## 2026-03-02 14:06 received", text);
        Assert.DoesNotContain(Secret, text);

        // Attachment table: the last two slots share one asset, resolved by this batch.
        var slotRows = lines.Where(l => l.StartsWith("| 3 of 4 |", StringComparison.Ordinal) || l.StartsWith("| 4 of 4 |", StringComparison.Ordinal))
            .Where(l => l.Contains("Resolved", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, slotRows.Count);
        Assert.All(slotRows, l =>
        {
            Assert.Contains($"| {m.Assets[2].ID} | Angie (msgstore import) | {tag} | {m.Assets[2].FileName} | 2048 | 2 |", l);
        });

        // Record table, in sort_id order: the last two share one hash.
        var three = lines.Single(l => l.Contains("| PKEYTHREE |", StringComparison.Ordinal));
        var four = lines.Single(l => l.Contains("| PKEYFOUR |", StringComparison.Ordinal));
        Assert.StartsWith("| 3 of 4 |", three);
        Assert.StartsWith("| 4 of 4 |", four);
        Assert.EndsWith("| hash and name+size | 2 |", three.TrimEnd());
        Assert.EndsWith("| hash only | 2 |", four.TrimEnd());
        Assert.Contains($"| {m.Assets[2].ID} |", four);

        // Every message row in the minute, media or not, in sort order with its type.
        var minute = text[text.IndexOf("## Every msgstore message row", StringComparison.Ordinal)..];
        Assert.Matches(@"\| 2 \| \d+ \| \d+ \| 0 \| 0 \| no \|", minute);
        Assert.Matches(@"\| 4 \| \d+ \| \d+ \| 0 \| 15 \| no \|", minute);
        Assert.Matches(@"\| 6 \| \d+ \| \d+ \| 0 \| 1 \| yes \|", minute);
    }

    [Fact]
    public void Probe_is_read_only_on_both_databases()
    {
        using var m = new BusyMinute();

        var workspace = PlanTests.Sha(m.Data.WorkspacePath);
        var msgstore = PlanTests.Sha(m.Data.MsgstorePath);
        var files = Directory.GetFiles(m.Data.DirectoryPath).Order().ToList();

        var (exit, _, _) = Scenario.Cli(m.Args());

        Assert.Equal(0, exit);
        Assert.Equal(workspace, PlanTests.Sha(m.Data.WorkspacePath));
        Assert.Equal(msgstore, PlanTests.Sha(m.Data.MsgstorePath));
        Assert.Equal(files.Append(m.Probe).Order(), Directory.GetFiles(m.Data.DirectoryPath).Order());
    }

    [Fact]
    public void Probe_refuses_an_existing_file_or_one_inside_a_repository()
    {
        using var m = new BusyMinute();

        File.WriteAllText(m.Probe, "keep");
        var (exit, _, stderr) = Scenario.Cli(m.Args());
        Assert.Equal(2, exit);
        Assert.Contains("never overwritten", stderr);
        Assert.Equal("keep", File.ReadAllText(m.Probe));

        var repo = Path.Combine(m.Data.DirectoryPath, "clone");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var inside = Path.Combine(repo, "probe.md");
        (exit, _, stderr) = Scenario.Cli(m.Args(inside));
        Assert.Equal(2, exit);
        Assert.Contains("inside a repository", stderr);
        Assert.False(File.Exists(inside));
    }

    [Fact]
    public void Probe_requires_date_time_and_output()
    {
        var (exit, _, stderr) = Scenario.Cli(
            ["probe", "--msgstore", "m", "--workspace", "w", "--conversation", "1", "--chat", "7",
             "--utc-offset", "+02:00", "--local-participant", "1", "--date", "2026-03-02"]);

        Assert.Equal(2, exit);
        Assert.Contains("probe needs", stderr);
    }
}
