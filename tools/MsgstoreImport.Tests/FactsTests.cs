using System.Globalization;
using System.Text.RegularExpressions;
using Chatcheology.Data.Workspace;

namespace MsgstoreImport.Tests;

/// <summary><c>plan --rule facts</c>, apply and undo-batch (CH-8, CH-10) on synthetic data only.</summary>
public sealed partial class FactsTests
{
    private const string KeyToken = "FKEY";
    private const string FileToken = "FACTFILE";
    private const string Secret = "SECRET-FACTS-TEXT";

    private enum Shape
    {
        Modern,
        Legacy,
        Neither,
    }

    /// <summary>
    /// One received or sent minute per link guard on <see cref="SyntheticData.Day"/>, all paired by the
    /// minute rule. An extra msgstore text message keeps the day from lining up by sequence.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        internal SyntheticData Data { get; } = new();
        internal string ManifestPath => Path.Combine(Data.DirectoryPath, "facts-manifest.tsv");

        internal long A1, A2, B1, B2, C1, C2, D1, D2, E1, E2, F1, G1, G2, G3, H1, H2, H3, I1, I2;

        private int _n;

        internal Fixture(Shape shape = Shape.Modern)
        {
            var d = Data;

            if (shape == Shape.Modern)
            {
                d.Msgstore(
                    """
                    CREATE TABLE message_forwarded (message_row_id INTEGER PRIMARY KEY, forward_score INTEGER, forward_origin INTEGER);
                    CREATE TABLE message_association (
                        _id INTEGER PRIMARY KEY, child_message_row_id INTEGER, parent_message_row_id INTEGER,
                        association_type INTEGER);
                    """);
            }
            else if (shape == Shape.Legacy)
            {
                d.Msgstore("ALTER TABLE message ADD COLUMN forwarded INTEGER;");
            }

            long Rec(string time, long size, bool fromMe = false) =>
                d.AddRecord(time, fromMe, FileToken + "-" + (++_n).ToString(CultureInfo.InvariantCulture) + ".jpg",
                    size, d.UnknownHash(), KeyToken + _n.ToString(CultureInfo.InvariantCulture));

            // A: the smaller parent is the low-quality copy of the child. Also a type-2 link (ignored).
            A1 = d.AddAttachment("11:13:00", false);
            A2 = d.AddAttachment("11:13:00", false);
            var rA1 = Rec("11:13:01", 1000);
            var rA2 = Rec("11:13:05", 5000);

            // B: parent larger than child. C: equal sizes.
            B1 = d.AddAttachment("11:14:00", false);
            B2 = d.AddAttachment("11:14:00", false);
            var rB1 = Rec("11:14:01", 5000);
            var rB2 = Rec("11:14:02", 1000);
            C1 = d.AddAttachment("11:15:00", false);
            C2 = d.AddAttachment("11:15:00", false);
            var rC1 = Rec("11:15:01", 2000);
            var rC2 = Rec("11:15:02", 2000);

            // D: different minutes. E: different directions.
            D1 = d.AddAttachment("11:16:00", false);
            var rD1 = Rec("11:16:10", 1000);
            D2 = d.AddAttachment("11:17:00", false);
            var rD2 = Rec("11:17:10", 5000);
            E1 = d.AddAttachment("11:18:00", true);
            E2 = d.AddAttachment("11:18:00", false);
            var rE1 = Rec("11:18:01", 1000, fromMe: true);
            var rE2 = Rec("11:18:02", 5000);

            // F: one attachment, two records: not paired.
            F1 = d.AddAttachment("11:19:00", false);
            var rF1 = Rec("11:19:01", 1000);
            var rF2 = Rec("11:19:02", 5000);

            // G: one copy, two full copies. H: a chain.
            G1 = d.AddAttachment("11:20:00", false);
            G2 = d.AddAttachment("11:20:00", false);
            G3 = d.AddAttachment("11:20:00", false);
            var rG1 = Rec("11:20:01", 500);
            var rG2 = Rec("11:20:02", 1000);
            var rG3 = Rec("11:20:03", 2000);
            H1 = d.AddAttachment("11:21:00", false);
            H2 = d.AddAttachment("11:21:00", false);
            H3 = d.AddAttachment("11:21:00", false);
            var rH1 = Rec("11:21:01", 500);
            var rH2 = Rec("11:21:02", 1000);
            var rH3 = Rec("11:21:03", 2000);

            // I: a type-2 link only.
            I1 = d.AddAttachment("11:22:00", false);
            I2 = d.AddAttachment("11:22:00", false);
            var rI1 = Rec("11:22:01", 1000);
            var rI2 = Rec("11:22:02", 5000);

            var text = d.AddNonMediaMessage("11:30:00", true, 0, Secret);
            var elsewhere = d.AddRecord("11:13:30", false, FileToken + "-x.jpg", 10, d.UnknownHash(), KeyToken + "X", chat: 8);

            string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
            string Link(long parent, long child, int type) => $"({Id(child)}, {Id(parent)}, {type.ToString(CultureInfo.InvariantCulture)})";

            if (shape == Shape.Modern)
            {
                d.Msgstore(
                    $"""
                    INSERT INTO message_forwarded VALUES ({Id(rA2)}, 3, 0), ({Id(rB2)}, 12, 0), ({Id(rC2)}, NULL, 0),
                        ({Id(rD2)}, 5, 0), ({Id(text)}, 1, 0), ({Id(elsewhere)}, 100, 0);
                    INSERT INTO message_association (child_message_row_id, parent_message_row_id, association_type) VALUES
                        {Link(rA1, rA2, 12)}, {Link(rA1, rA2, 2)}, {Link(rB1, rB2, 12)}, {Link(rC1, rC2, 12)},
                        {Link(rD1, rD2, 12)}, {Link(rE1, rE2, 12)}, {Link(rF1, rF2, 12)},
                        {Link(rG1, rG2, 12)}, {Link(rG1, rG3, 12)}, {Link(rH1, rH2, 12)}, {Link(rH2, rH3, 12)},
                        {Link(rI1, rI2, 2)};
                    """);
            }
            else if (shape == Shape.Legacy)
            {
                d.Msgstore($"UPDATE message SET forwarded = 0; UPDATE message SET forwarded = 1 WHERE _id IN ({Id(rA2)}, {Id(text)});");
            }

            d.CloseAll();
        }

        internal string[] PlanArgs(string? manifestOut = null) =>
        [
            "plan", "--rule", "facts", "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath,
            "--conversation", "1", "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture),
            "--utc-offset", SyntheticData.OffsetText, "--local-participant", "1",
            "--manifest-out", manifestOut ?? ManifestPath,
        ];

        internal string[] ApplyArgs(string sha) =>
        [
            "apply", "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath,
            "--manifest", ManifestPath, "--manifest-sha256", sha, "--i-have-a-backup",
        ];

        internal string[] UndoArgs(string sha) =>
        [
            "undo-batch", "--workspace", Data.WorkspacePath, "--manifest", ManifestPath,
            "--manifest-sha256", sha, "--i-have-a-backup",
        ];

        /// <summary>Every stored fact, by attachment: (forwarded, score, copy of, batch).</summary>
        internal Dictionary<long, (bool Forwarded, string Score, string CopyOf, string Batch)> Facts() =>
            Data.Rows("SELECT AttachmentID, IsForwarded, ForwardScore, LowQualityCopyOfAttachmentID, SourceBatch FROM AttachmentMsgstoreFact;")
                .Select(r => r.Split(','))
                .ToDictionary(p => long.Parse(p[0], CultureInfo.InvariantCulture), p => (p[1] == "1", p[2], p[3], p[4]));

        internal string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

        public void Dispose() => Data.Dispose();
    }

    [GeneratedRegex("manifest SHA-256: ([0-9a-f]{64})")]
    private static partial Regex ShaLine();

    private static string Sha(string stdout) => ShaLine().Match(stdout).Groups[1].Value;

    private static void NoIdentifiers(Fixture f, string text)
    {
        Assert.DoesNotContain(KeyToken, text);
        Assert.DoesNotContain(FileToken, text);
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain(f.Data.DirectoryPath, text);
        Assert.DoesNotContain("Media/WhatsApp", text);
    }

    private static string Line(string label, int value) => Regex.Escape(label) + @"\s+" + value.ToString(CultureInfo.InvariantCulture) + @"\r?\n";

    [Fact]
    public void Plan_counts_forwarded_facts_and_each_link_guard()
    {
        using var f = new Fixture();
        var (exit, stdout, stderr) = Scenario.Cli(f.PlanArgs());

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);
        NoIdentifiers(f, stdout);

        Assert.Matches(Line("attachments in the conversation", 19), stdout);
        Assert.Matches(Line("  paired by the minute rule", 18), stdout);
        Assert.Matches(Line("  paired by the day-sequence rule", 0), stdout);
        Assert.Matches(Line("manifest rows (one per paired attachment)", 18), stdout);
        Assert.Matches(Line("  forwarded", 4), stdout);
        Assert.Matches(Line("    forwarded many times (score >= 5)", 2), stdout);
        Assert.Matches(Line("    forward score recorded", 3), stdout);
        Assert.Matches(Line("  low-quality copy links", 1), stdout);
        Assert.Matches(Line("attachments not paired (no row)", 1), stdout);
        Assert.Matches(Line("  " + FactsPlanner.NotPaired, 1), stdout);
        Assert.Matches(Line("type-12 links touching the chat", 10), stdout);
        Assert.Matches(Line("    " + FactsPlanner.LinkSideNotPaired, 1), stdout);
        Assert.Matches(Line("    " + FactsPlanner.LinkMinute, 1), stdout);
        Assert.Matches(Line("    " + FactsPlanner.LinkDirection, 1), stdout);
        Assert.Matches(Line("    " + FactsPlanner.LinkSize, 2), stdout);
        Assert.Matches(Line("    " + FactsPlanner.LinkTwoTargets, 1), stdout);
        Assert.Matches(Line("    " + FactsPlanner.LinkChain, 2), stdout);
        Assert.Matches(Line("links of other types (ignored)", 2), stdout);
        Assert.Matches(@"reconciliation\s+OK", stdout);

        // The manifest: the smaller parent (A1) is the copy of the child (A2); no other link.
        var manifest = FactsManifest.Parse(File.ReadAllBytes(f.ManifestPath));
        var rows = manifest.Rows.ToDictionary(r => r.AttachmentID);

        Assert.Equal(18, rows.Count);
        Assert.False(rows.ContainsKey(f.F1));
        Assert.Equal(f.A2, rows[f.A1].LowQualityCopyOf);
        Assert.Single(rows.Values, r => r.LowQualityCopyOf is not null);

        Assert.Equal((true, (int?)3), (rows[f.A2].IsForwarded, rows[f.A2].ForwardScore));
        Assert.Equal((true, (int?)12), (rows[f.B2].IsForwarded, rows[f.B2].ForwardScore));
        Assert.Equal((true, (int?)null), (rows[f.C2].IsForwarded, rows[f.C2].ForwardScore));
        Assert.Equal((true, (int?)5), (rows[f.D2].IsForwarded, rows[f.D2].ForwardScore));
        Assert.Equal((false, (int?)null), (rows[f.A1].IsForwarded, rows[f.A1].ForwardScore));

        // Each guard's links record nothing: B, C (size), D (minute), E (direction), G (two), H (chain), I (type 2).
        foreach (var id in new[] { f.B1, f.B2, f.C1, f.C2, f.D1, f.D2, f.E1, f.E2, f.G1, f.G2, f.G3, f.H1, f.H2, f.H3, f.I1, f.I2 })
        {
            Assert.Null(rows[id].LowQualityCopyOf);
        }

        // Nothing written to the workspace.
        Assert.Equal(0, f.Data.Count("SELECT COUNT(*) FROM AttachmentMsgstoreFact;"));
    }

    [Fact]
    public void Apply_records_the_facts_and_a_rerun_is_already_done()
    {
        using var f = new Fixture();
        var sha = Sha(Scenario.Cli(f.PlanArgs()).Out);
        var tag = sha[..12];
        var otherTables = f.Data.DumpExcept("AttachmentMsgstoreFact");

        var (exit, stdout, stderr) = Scenario.Cli(f.ApplyArgs(sha));

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);
        NoIdentifiers(f, stdout);
        Assert.Matches(Line("manifest rows", 18), stdout);
        Assert.Matches(Line("recorded", 18), stdout);
        Assert.Matches(Line("already done (this batch, same facts)", 0), stdout);
        Assert.Matches(@"reconciliation against manifest rows\s+OK", stdout);

        var facts = f.Facts();
        Assert.Equal(18, facts.Count);
        Assert.All(facts.Values, v => Assert.Equal(tag, v.Batch));
        Assert.Equal(f.Id(f.A2), facts[f.A1].CopyOf);
        Assert.Equal("<null>", facts[f.A2].CopyOf);
        Assert.Equal((true, "12"), (facts[f.B2].Forwarded, facts[f.B2].Score));
        Assert.Equal((true, "<null>"), (facts[f.C2].Forwarded, facts[f.C2].Score));
        Assert.Equal((false, "<null>"), (facts[f.G1].Forwarded, facts[f.G1].Score));

        // Only the facts table changed.
        var after = f.Data.DumpExcept("AttachmentMsgstoreFact");
        foreach (var (table, rows) in otherTables)
        {
            Assert.Equal(rows, after[table]);
        }

        var (again, againOut, againErr) = Scenario.Cli(f.ApplyArgs(sha));

        Assert.Equal("", againErr);
        Assert.Equal(0, again);
        Assert.Matches(Line("recorded", 0), againOut);
        Assert.Matches(Line("already done (this batch, same facts)", 18), againOut);
        Assert.Equal(18, f.Facts().Count);

        // A new plan leaves every recorded attachment alone.
        var second = Path.Combine(f.Data.DirectoryPath, "facts-manifest-2.tsv");
        var (planExit, planOut, _) = Scenario.Cli(f.PlanArgs(second));

        Assert.Equal(0, planExit);
        Assert.Matches(Line("manifest rows (one per paired attachment)", 0), planOut);
        Assert.Matches(Line(FactsPlanner.LeftAloneReason, 18), planOut);
        Assert.Matches(@"reconciliation\s+OK", planOut);
    }

    [Fact]
    public void Undo_removes_the_batch_and_a_rerun_removes_nothing()
    {
        using var f = new Fixture();
        var sha = Sha(Scenario.Cli(f.PlanArgs()).Out);
        Assert.Equal(0, Scenario.Cli(f.ApplyArgs(sha)).Exit);

        var (exit, stdout, stderr) = Scenario.Cli(f.UndoArgs(sha));

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);
        NoIdentifiers(f, stdout);
        Assert.Matches(Line("removed", 18), stdout);
        Assert.Matches(Line("not present (never applied or already removed)", 0), stdout);
        Assert.Empty(f.Facts());

        var (again, againOut, _) = Scenario.Cli(f.UndoArgs(sha));

        Assert.Equal(0, again);
        Assert.Matches(Line("removed", 0), againOut);
        Assert.Matches(Line("not present (never applied or already removed)", 18), againOut);

        // Apply works again after the undo.
        Assert.Equal(0, Scenario.Cli(f.ApplyArgs(sha)).Exit);
        Assert.Equal(18, f.Facts().Count);
    }

    [Fact]
    public void Digest_mismatch_and_tampering_are_refused()
    {
        using var f = new Fixture();
        var sha = Sha(Scenario.Cli(f.PlanArgs()).Out);
        var wrong = new string('0', 64);

        var (exit, stdout, stderr) = Scenario.Cli(f.ApplyArgs(wrong));

        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.Contains("SHA-256 does not match", stderr);
        Assert.Equal(2, Scenario.Cli(f.UndoArgs(wrong)).Exit);

        File.AppendAllText(f.ManifestPath, "x");
        Assert.Contains("SHA-256 does not match", Scenario.Cli(f.ApplyArgs(sha)).Err);

        Assert.Empty(f.Facts());
    }

    [Fact]
    public void A_change_in_the_msgstore_since_the_plan_refuses_the_whole_batch()
    {
        using var f = new Fixture();
        var sha = Sha(Scenario.Cli(f.PlanArgs()).Out);

        f.Data.Msgstore("UPDATE message_forwarded SET forward_score = 4 WHERE forward_score = 3;");
        f.Data.CloseAll();

        var (exit, stdout, stderr) = Scenario.Cli(f.ApplyArgs(sha));

        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.Contains("1 manifest rows no longer match", stderr);
        NoIdentifiers(f, stderr);
        Assert.Empty(f.Facts());
    }

    [Fact]
    public void A_claimed_workspace_is_refused()
    {
        using var f = new Fixture();
        var sha = Sha(Scenario.Cli(f.PlanArgs()).Out);

        using (WorkspaceExclusiveLock.Acquire(f.Data.WorkspacePath))
        {
            Assert.Equal(2, Scenario.Cli(f.ApplyArgs(sha)).Exit);
            Assert.Equal(2, Scenario.Cli(f.UndoArgs(sha)).Exit);
        }

        Assert.Empty(f.Facts());
    }

    [Fact]
    public void Version_five_workspace_is_refused_with_the_migrate_first_message()
    {
        using var f = new Fixture();
        var sha = Sha(Scenario.Cli(f.PlanArgs()).Out);
        MigrateTests.MakeVersionFive(f.Data);
        var before = MigrateTests.Hash(f.Data.WorkspacePath);

        var second = Path.Combine(f.Data.DirectoryPath, "facts-manifest-v5.tsv");

        foreach (var args in new[] { f.PlanArgs(second), f.ApplyArgs(sha), f.UndoArgs(sha) })
        {
            var (exit, stdout, stderr) = Scenario.Cli(args);

            Assert.Equal(2, exit);
            Assert.Equal("", stdout);
            Assert.Contains("schema version 5", stderr);
            Assert.Contains("'migrate --workspace <path> --apply' first", stderr);
        }

        Assert.False(File.Exists(second));
        Assert.Equal(before, MigrateTests.Hash(f.Data.WorkspacePath));
    }

    [Fact]
    public void Version_three_workspace_is_refused_without_offering_migrate()
    {
        using var f = new Fixture();
        var sha = Sha(Scenario.Cli(f.PlanArgs()).Out);
        MigrateTests.MakeVersionThree(f.Data);
        var before = MigrateTests.Hash(f.Data.WorkspacePath);

        var second = Path.Combine(f.Data.DirectoryPath, "facts-manifest-v3.tsv");

        foreach (var args in new[] { f.PlanArgs(second), f.ApplyArgs(sha), f.UndoArgs(sha) })
        {
            var (exit, stdout, stderr) = Scenario.Cli(args);

            Assert.Equal(2, exit);
            Assert.Equal("", stdout);
            Assert.Contains("schema version 3. migrate takes only a version 5 workspace", stderr);
        }

        Assert.False(File.Exists(second));
        Assert.Equal(before, MigrateTests.Hash(f.Data.WorkspacePath));
    }

    [Fact]
    public void Legacy_forwarded_column_marks_forwarded_with_no_score()
    {
        using var f = new Fixture(Shape.Legacy);
        var (exit, stdout, stderr) = Scenario.Cli(f.PlanArgs());

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);
        Assert.Matches(Line("  forwarded", 1), stdout);
        Assert.Matches(Line("  low-quality copy links", 0), stdout);
        Assert.Matches(@"message_association table\s+absent", stdout);

        var rows = FactsManifest.Parse(File.ReadAllBytes(f.ManifestPath)).Rows.ToDictionary(r => r.AttachmentID);
        Assert.Equal((true, (int?)null), (rows[f.A2].IsForwarded, rows[f.A2].ForwardScore));
        Assert.False(rows[f.A1].IsForwarded);
    }

    [Fact]
    public void Msgstore_with_no_forwarding_structure_is_refused()
    {
        using var f = new Fixture(Shape.Neither);
        var (exit, stdout, stderr) = Scenario.Cli(f.PlanArgs());

        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.Contains("forwarded status cannot be read", stderr);
        Assert.False(File.Exists(f.ManifestPath));
    }

    [Fact]
    public void Day_sequence_pairs_get_a_row_and_a_far_record_does_not()
    {
        using var d = new SyntheticData();
        d.Msgstore("CREATE TABLE message_forwarded (message_row_id INTEGER PRIMARY KEY, forward_score INTEGER);");

        var next = SyntheticData.Day.AddDays(1);
        var later = SyntheticData.Day.AddDays(2);

        var near = d.AddAttachment("10:00:00", false, next);
        var nearRecord = d.AddRecord("10:01:30", false, FileToken + "-n.jpg", 10, d.UnknownHash(), KeyToken + "N", date: next);
        var far = d.AddAttachment("10:00:00", false, later);
        d.AddRecord("10:05:00", false, FileToken + "-f.jpg", 10, d.UnknownHash(), KeyToken + "F", date: later);
        d.Msgstore($"INSERT INTO message_forwarded VALUES ({nearRecord.ToString(CultureInfo.InvariantCulture)}, 2);");
        d.CloseAll();

        var manifest = Path.Combine(d.DirectoryPath, "facts-day.tsv");
        var (exit, stdout, stderr) = Scenario.Cli(
        [
            "plan", "--rule", "facts", "--msgstore", d.MsgstorePath, "--workspace", d.WorkspacePath,
            "--conversation", "1", "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture),
            "--utc-offset", SyntheticData.OffsetText, "--local-participant", "1", "--manifest-out", manifest,
        ]);

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);
        Assert.Matches(Line("  paired by the minute rule", 0), stdout);
        Assert.Matches(Line("  paired by the day-sequence rule", 1), stdout);
        Assert.Matches(Line("  " + FactsPlanner.DayMinuteGap, 1), stdout);
        Assert.Matches(@"reconciliation\s+OK", stdout);

        var row = Assert.Single(FactsManifest.Parse(File.ReadAllBytes(manifest)).Rows);
        Assert.Equal(near, row.AttachmentID);
        Assert.NotEqual(far, row.AttachmentID);
        Assert.Equal((true, (int?)2), (row.IsForwarded, row.ForwardScore));
    }

    [Fact]
    public void Facts_manifest_round_trips_and_rejects_bad_rows()
    {
        var manifest = new FactsManifest
        {
            Parameters = new PairingParameters(1, 7, TimeSpan.FromHours(2), 1),
            Rows = [new FactRow(3, 101, true, null, null), new FactRow(4, 102, false, 7, 3)],
        };

        var bytes = manifest.ToBytes();
        var parsed = FactsManifest.Parse(bytes);

        Assert.Equal(manifest.Rows, parsed.Rows);

        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Throws<FormatException>(() => FactsManifest.Parse(System.Text.Encoding.UTF8.GetBytes(text.Replace("4\t102\t0\t7\t3", "4\t102\t0\t7\t4"))));
        Assert.Throws<FormatException>(() => FactsManifest.Parse(System.Text.Encoding.UTF8.GetBytes(text.Replace("\t0\t7\t", "\t2\t7\t"))));

        // An import manifest parser never takes a facts manifest.
        Assert.Throws<FormatException>(() => Manifest.Parse(bytes));
    }

    [Fact]
    public void Facts_rule_takes_no_sample_and_existing_rules_still_parse()
    {
        using var f = new Fixture();
        var (exit, _, stderr) = Scenario.Cli([.. f.PlanArgs(), "--sample-out", "x.md", "--seed", "1"]);

        Assert.Equal(2, exit);
        Assert.Contains("--sample-out applies only to", stderr);

        Assert.Equal(PairingRule.Facts, Options.Parse(f.PlanArgs()).Rule);
        Assert.Equal(PairingRule.Minute, Options.Parse([.. f.PlanArgs().Where(a => a is not ("--rule" or "facts"))]).Rule);
    }
}
