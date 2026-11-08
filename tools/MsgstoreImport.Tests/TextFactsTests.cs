using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Workspace;

namespace MsgstoreImport.Tests;

/// <summary><c>plan --rule text-facts</c>, apply and undo-batch (CH-22) on synthetic data only.</summary>
public sealed partial class TextFactsTests
{
    /// <summary>In every synthetic text, so output can be checked for leaked text.</summary>
    private const string Token = "TFTOKEN";

    private enum Shape
    {
        Modern,
        Legacy,
        Both,
        Neither,
    }

    /// <summary>
    /// Day <see cref="SyntheticData.Day"/>, offset +02:00, local participant 1.
    /// Paired: Fwd7 (10:00, forwarded, score 7), Plain (10:01, not forwarded), Edited (10:04, the
    /// workspace text ends in the edited marker; forwarded, score 2), Nfc (10:05, direction marks, CRLF and
    /// a decomposed accent on the msgstore side; forwarded, no score), BadScore (10:09, forwarded, score
    /// not a number). Ambiguous: Twice (10:02, two msgstore candidates), Shared1/Shared2 (10:03, one
    /// shared candidate). Unmatched: Lonely (10:06), Direction (10:07, msgstore other direction).
    /// Excluded: an empty message, a caption (with a msgstore caption in its minute), an attachment, a
    /// system message.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        internal SyntheticData Data { get; } = new();
        internal string ManifestPath => Path.Combine(Data.DirectoryPath, "text-facts-manifest.tsv");

        internal long Fwd7, Plain, Edited, Nfc, BadScore, Twice, Shared1, Shared2, Lonely, Direction, Empty, Caption;
        internal long MsFwd7, MsPlain, MsEdited, MsNfc, MsBadScore;

        internal Fixture(Shape shape = Shape.Modern)
        {
            var d = Data;

            if (shape is Shape.Modern or Shape.Both)
            {
                d.Msgstore("CREATE TABLE message_forwarded (message_row_id INTEGER PRIMARY KEY, forward_score INTEGER, forward_origin INTEGER);");
            }

            if (shape is Shape.Legacy or Shape.Both)
            {
                d.Msgstore("ALTER TABLE message ADD COLUMN forwarded INTEGER;");
            }

            Fwd7 = Ws("10:00:00", false, Token + " forwarded seven");
            MsFwd7 = d.AddNonMediaMessage("10:00:20", false, 0, Token + " forwarded seven");

            Plain = Ws("10:01:00", true, Token + " plain");
            MsPlain = d.AddNonMediaMessage("10:01:20", true, 0, Token + " plain");

            Twice = Ws("10:02:00", true, Token + " twice");
            d.AddNonMediaMessage("10:02:10", true, 0, Token + " twice");
            d.AddNonMediaMessage("10:02:20", true, 0, Token + " twice");

            Shared1 = Ws("10:03:00", true, Token + " shared");
            Shared2 = Ws("10:03:00", true, Token + " shared");
            d.AddNonMediaMessage("10:03:10", true, 0, Token + " shared");

            Edited = Ws("10:04:00", false, Token + " edited text  " + TextProber.EditedMarker);
            MsEdited = d.AddNonMediaMessage("10:04:30", false, 0, Token + " edited text");

            Nfc = Ws("10:05:00", false, Token + " café\nline two");
            MsNfc = d.AddNonMediaMessage("10:05:05", false, 0, "‎  " + Token + " café\r\nline two‏\r\n");

            Lonely = Ws("10:06:00", false, Token + " lonely");

            Direction = Ws("10:07:00", false, Token + " direction");
            d.AddNonMediaMessage("10:07:05", true, 0, Token + " direction");

            Empty = Ws("10:08:00", false, "‎ ‏");

            BadScore = Ws("10:09:00", true, Token + " bad score");
            MsBadScore = d.AddNonMediaMessage("10:09:05", true, 0, Token + " bad score");

            Caption = Ws("10:10:00", false, "<Media omitted>\n" + Token + " caption");
            var msCaption = d.AddRecord("10:10:05", false, "TFFILE-a.jpg", 10, d.UnknownHash(), "TFKEYA");
            d.Msgstore($"UPDATE message SET text_data = '{Token} caption' WHERE _id = {Id(msCaption)};");

            d.AddAttachment("10:11:00", false);
            d.Workspace(
                """
                INSERT INTO Message (
                    ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                    SenderParticipantID, MessageType, MessageContent, RawContent, SourceLineStart, SourceLineEnd)
                VALUES (1, 1, 1000, '2026-03-02T10:12:00', NULL, 'System', 'TFTOKEN system', 'raw', 1000, 1000);
                """);

            if (shape is Shape.Modern or Shape.Both)
            {
                d.Msgstore(
                    $"""
                    INSERT INTO message_forwarded (message_row_id, forward_score) VALUES
                        ({Id(MsFwd7)}, 7), ({Id(MsEdited)}, 2), ({Id(MsNfc)}, NULL), ({Id(MsBadScore)}, 'x'), ({Id(msCaption)}, 9);
                    """);
            }

            if (shape is Shape.Legacy or Shape.Both)
            {
                d.Msgstore($"UPDATE message SET forwarded = 0; UPDATE message SET forwarded = 1 WHERE _id IN ({Id(MsFwd7)}, {Id(MsPlain)});");
            }

            d.CloseAll();
        }

        private long Ws(string time, bool outgoing, string content)
        {
            var id = Data.AddWorkspaceMessage(time, outgoing);
            Data.Workspace($"UPDATE Message SET MessageContent = '{content.Replace("'", "''", StringComparison.Ordinal)}' WHERE MessageID = {Id(id)};");
            return id;
        }

        internal string[] PlanArgs(string? manifestOut = null) =>
        [
            "plan", "--rule", "text-facts", "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath,
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

        /// <summary>Every stored text fact, by message: (forwarded, score, batch).</summary>
        internal Dictionary<long, (bool Forwarded, string Score, string Batch)> Facts() =>
            Data.Rows("SELECT MessageID, IsForwarded, ForwardScore, SourceBatch FROM MessageMsgstoreFact;")
                .Select(r => r.Split(','))
                .ToDictionary(p => long.Parse(p[0], CultureInfo.InvariantCulture), p => (p[1] == "1", p[2], p[3]));

        internal Dictionary<long, TextFactRow> ManifestRows() =>
            TextFactsManifest.Parse(File.ReadAllBytes(ManifestPath)).Rows.ToDictionary(r => r.MessageID);

        public void Dispose() => Data.Dispose();
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("manifest SHA-256: ([0-9a-f]{64})")]
    private static partial Regex ShaLine();

    private static string Sha(string stdout) => ShaLine().Match(stdout).Groups[1].Value;

    private static string Line(string label, int value) => Regex.Escape(label) + @"\s+" + value.ToString(CultureInfo.InvariantCulture) + @"\r?\n";

    private static void NoSyntheticTokens(Fixture f, string text)
    {
        foreach (var token in new[] { Token, "TFKEY", "TFFILE", "SYNTHTEXT", "caf", "Alex", "Sam", "Media/WhatsApp", f.Data.DirectoryPath })
        {
            Assert.DoesNotContain(token, text);
        }
    }

    private static string Plan(Fixture f)
    {
        var (exit, stdout, stderr) = Scenario.Cli(f.PlanArgs());

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);

        return stdout;
    }

    [Fact]
    public void Plan_counts_unique_ambiguous_and_unmatched_and_reconciles()
    {
        using var f = new Fixture();
        var stdout = Plan(f);

        Assert.Matches(Line("workspace messages in the conversation", 14), stdout);
        Assert.Matches(Line("  text messages", 10), stdout);
        Assert.Matches(Line("text messages paired uniquely", 5), stdout);
        Assert.Matches(Line("text messages ambiguous (no row)", 3), stdout);
        Assert.Matches(Line("text messages unmatched (no row)", 2), stdout);
        Assert.Matches(Line("text messages with no readable time (no row)", 0), stdout);
        Assert.Matches(Line("manifest rows (one per paired text message)", 5), stdout);
        Assert.Matches(Line("rows + left alone + ambiguous + unmatched + no time", 10), stdout);
        Assert.Matches(@"reconciliation \(= text messages\)\s+OK", stdout);
        Assert.Matches("batch tag:        [0-9a-f]{12}", stdout);

        var rows = f.ManifestRows();
        Assert.Equal([f.Fwd7, f.Plain, f.Edited, f.Nfc, f.BadScore], rows.Keys.Order());
        Assert.Equal(f.MsFwd7, rows[f.Fwd7].MsgstoreMessageID);
        Assert.Equal(f.MsPlain, rows[f.Plain].MsgstoreMessageID);

        foreach (var id in new[] { f.Twice, f.Shared1, f.Shared2, f.Lonely, f.Direction })
        {
            Assert.False(rows.ContainsKey(id));
        }

        // Nothing written to the workspace.
        Assert.Equal(0, f.Data.Count("SELECT COUNT(*) FROM MessageMsgstoreFact;"));
    }

    [Fact]
    public void Edited_marker_strip_is_counted_as_a_gain()
    {
        Assert.Equal(Token + " x", TextProber.WithoutEditedMarker(TextProber.Normalise(Token + " x \r\n " + TextProber.EditedMarker)));

        using var f = new Fixture();
        var stdout = Plan(f);

        Assert.Matches(Line("  gained by the edited-marker strip", 1), stdout);
        Assert.Matches(Line("  lost by the edited-marker strip (unique only without it)", 0), stdout);
        Assert.Matches(Line("text messages ending in the export's edited marker", 1), stdout);
        Assert.Equal(f.MsEdited, f.ManifestRows()[f.Edited].MsgstoreMessageID);
    }

    [Fact]
    public void Edited_marker_strip_that_makes_a_pair_ambiguous_is_counted_as_lost()
    {
        using var d = new SyntheticData();
        d.Msgstore("CREATE TABLE message_forwarded (message_row_id INTEGER PRIMARY KEY, forward_score INTEGER);");

        long Ws(string time, string content)
        {
            var id = d.AddWorkspaceMessage(time, false);
            d.Workspace($"UPDATE Message SET MessageContent = '{content}' WHERE MessageID = {Id(id)};");
            return id;
        }

        Ws("10:00:00", Token + " same");
        Ws("10:00:00", Token + " same " + TextProber.EditedMarker);
        d.AddNonMediaMessage("10:00:10", false, 0, Token + " same");
        d.CloseAll();

        var manifest = Path.Combine(d.DirectoryPath, "lost.tsv");
        var (exit, stdout, stderr) = Scenario.Cli(
        [
            "plan", "--rule", "text-facts", "--msgstore", d.MsgstorePath, "--workspace", d.WorkspacePath,
            "--conversation", "1", "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture),
            "--utc-offset", SyntheticData.OffsetText, "--local-participant", "1", "--manifest-out", manifest,
        ]);

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);
        Assert.Matches(Line("text messages paired uniquely", 0), stdout);
        Assert.Matches(Line("  lost by the edited-marker strip (unique only without it)", 1), stdout);
        Assert.Matches(Line("text messages ambiguous (no row)", 2), stdout);
        Assert.Matches(@"reconciliation \(= text messages\)\s+OK", stdout);
    }

    [Fact]
    public void Normalisation_pairs_direction_marks_line_endings_and_nfc()
    {
        Assert.Equal(TextProber.Normalise(Token + " café\nx"), TextProber.Normalise("‎ " + Token + " café\r\nx‏\r\n"));

        using var f = new Fixture();
        Plan(f);

        Assert.Equal(f.MsNfc, f.ManifestRows()[f.Nfc].MsgstoreMessageID);
    }

    [Fact]
    public void Empty_and_caption_messages_are_excluded_and_get_no_row()
    {
        using var f = new Fixture();
        var stdout = Plan(f);

        Assert.Matches(Line("  system messages (excluded)", 1), stdout);
        Assert.Matches(Line("  attachment messages (excluded)", 1), stdout);
        Assert.Matches(Line("  captions (excluded; the attachment carries the fact)", 1), stdout);
        Assert.Matches(Line("  empty after normalisation (excluded)", 1), stdout);

        var rows = f.ManifestRows();
        Assert.False(rows.ContainsKey(f.Empty));
        Assert.False(rows.ContainsKey(f.Caption));
    }

    [Fact]
    public void Forwarded_and_score_are_copied_and_an_unreadable_score_is_none()
    {
        using var f = new Fixture();
        var stdout = Plan(f);

        Assert.Matches(@"forwarded from\s+message_forwarded \(with forward_score\)", stdout);
        Assert.Matches(Line("  forwarded", 4), stdout);
        Assert.Matches(Line("    forwarded many times (score >= 5)", 1), stdout);
        Assert.Matches(Line("    forward score recorded", 2), stdout);
        Assert.Matches(Line("    forward score unreadable (recorded as none)", 1), stdout);
        Assert.Matches(Line("  not forwarded", 1), stdout);

        var rows = f.ManifestRows();
        Assert.Equal((true, (int?)7), (rows[f.Fwd7].IsForwarded, rows[f.Fwd7].ForwardScore));
        Assert.Equal((false, (int?)null), (rows[f.Plain].IsForwarded, rows[f.Plain].ForwardScore));
        Assert.Equal((true, (int?)2), (rows[f.Edited].IsForwarded, rows[f.Edited].ForwardScore));
        Assert.Equal((true, (int?)null), (rows[f.Nfc].IsForwarded, rows[f.Nfc].ForwardScore));
        Assert.Equal((true, (int?)null), (rows[f.BadScore].IsForwarded, rows[f.BadScore].ForwardScore));
    }

    [Fact]
    public void Legacy_forwarded_column_is_used_only_when_message_forwarded_is_absent()
    {
        using (var legacy = new Fixture(Shape.Legacy))
        {
            var stdout = Plan(legacy);
            var rows = legacy.ManifestRows();

            Assert.Matches(@"forwarded from\s+legacy message.forwarded \(no score\)", stdout);
            Assert.Matches(Line("  forwarded", 2), stdout);
            Assert.Equal((true, (int?)null), (rows[legacy.Fwd7].IsForwarded, rows[legacy.Fwd7].ForwardScore));
            Assert.True(rows[legacy.Plain].IsForwarded);
            Assert.False(rows[legacy.Edited].IsForwarded);
        }

        using var both = new Fixture(Shape.Both);
        var bothOut = Plan(both);
        var bothRows = both.ManifestRows();

        // The table wins: Plain is forwarded only in the legacy column, so it is not forwarded here.
        Assert.Matches(Line("  forwarded", 4), bothOut);
        Assert.False(bothRows[both.Plain].IsForwarded);
        Assert.Equal(7, bothRows[both.Fwd7].ForwardScore);
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
    public void Apply_records_the_rows_and_a_rerun_is_already_done()
    {
        using var f = new Fixture();
        var sha = Sha(Plan(f));
        var tag = sha[..12];
        var otherTables = f.Data.DumpExcept("MessageMsgstoreFact");

        var (exit, stdout, stderr) = Scenario.Cli(f.ApplyArgs(sha));

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);
        Assert.Contains("# Msgstore import - apply text-facts", stdout);
        Assert.Matches(Line("manifest rows", 5), stdout);
        Assert.Matches(Line("recorded", 5), stdout);
        Assert.Matches(Line("already done (this batch, same facts)", 0), stdout);
        Assert.Matches(@"reconciliation against manifest rows\s+OK", stdout);

        var facts = f.Facts();
        Assert.Equal(5, facts.Count);
        Assert.All(facts.Values, v => Assert.Equal(tag, v.Batch));
        Assert.Equal((true, "7"), (facts[f.Fwd7].Forwarded, facts[f.Fwd7].Score));
        Assert.Equal((false, "<null>"), (facts[f.Plain].Forwarded, facts[f.Plain].Score));
        Assert.Equal((true, "<null>"), (facts[f.BadScore].Forwarded, facts[f.BadScore].Score));

        // Only the text facts table changed.
        var after = f.Data.DumpExcept("MessageMsgstoreFact");
        foreach (var (table, rows) in otherTables)
        {
            Assert.Equal(rows, after[table]);
        }

        var (again, againOut, againErr) = Scenario.Cli(f.ApplyArgs(sha));

        Assert.Equal("", againErr);
        Assert.Equal(0, again);
        Assert.Matches(Line("recorded", 0), againOut);
        Assert.Matches(Line("already done (this batch, same facts)", 5), againOut);
        Assert.Equal(5, f.Facts().Count);

        // A new plan leaves every recorded message alone.
        var second = Path.Combine(f.Data.DirectoryPath, "text-facts-2.tsv");
        var (planExit, planOut, _) = Scenario.Cli(f.PlanArgs(second));

        Assert.Equal(0, planExit);
        Assert.Matches(Line("manifest rows (one per paired text message)", 0), planOut);
        Assert.Matches(Line("paired, " + TextFactsPlanner.LeftAloneReason, 5), planOut);
        Assert.Matches(@"reconciliation \(= text messages\)\s+OK", planOut);
    }

    [Fact]
    public void A_fact_from_another_batch_is_left_alone_and_survives_undo()
    {
        using var f = new Fixture();
        MessageMsgstoreFactWriter.InsertBatch(f.Data.WorkspacePath, "otherbatch01", [new MessageMsgstoreFactRow(f.Plain, true, 1)], DateTime.UtcNow);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var stdout = Plan(f);
        var sha = Sha(stdout);

        Assert.Matches(Line("text messages paired uniquely", 5), stdout);
        Assert.Matches(Line("manifest rows (one per paired text message)", 4), stdout);
        Assert.Matches(Line("paired, " + TextFactsPlanner.LeftAloneReason, 1), stdout);
        Assert.Matches(@"reconciliation \(= text messages\)\s+OK", stdout);
        Assert.False(f.ManifestRows().ContainsKey(f.Plain));

        Assert.Equal(0, Scenario.Cli(f.ApplyArgs(sha)).Exit);
        Assert.Equal(5, f.Facts().Count);

        var (exit, undoOut, _) = Scenario.Cli(f.UndoArgs(sha));

        Assert.Equal(0, exit);
        Assert.Matches(Line("removed", 4), undoOut);

        var left = Assert.Single(f.Facts());
        Assert.Equal((f.Plain, "otherbatch01"), (left.Key, left.Value.Batch));
    }

    [Fact]
    public void Undo_removes_the_batch_and_a_rerun_removes_nothing()
    {
        using var f = new Fixture();
        var sha = Sha(Plan(f));
        Assert.Equal(0, Scenario.Cli(f.ApplyArgs(sha)).Exit);

        var (exit, stdout, stderr) = Scenario.Cli(f.UndoArgs(sha));

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);
        Assert.Contains("# Msgstore import - undo-batch text-facts", stdout);
        Assert.Matches(Line("removed", 5), stdout);
        Assert.Matches(Line("not present (never applied or already removed)", 0), stdout);
        Assert.Empty(f.Facts());

        var (again, againOut, _) = Scenario.Cli(f.UndoArgs(sha));

        Assert.Equal(0, again);
        Assert.Matches(Line("removed", 0), againOut);
        Assert.Matches(Line("not present (never applied or already removed)", 5), againOut);

        // Apply works again after the undo.
        Assert.Equal(0, Scenario.Cli(f.ApplyArgs(sha)).Exit);
        Assert.Equal(5, f.Facts().Count);
    }

    [Fact]
    public void Digest_mismatch_and_tampering_are_refused()
    {
        using var f = new Fixture();
        var sha = Sha(Plan(f));
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
    public void A_change_since_the_plan_refuses_the_whole_batch()
    {
        using var f = new Fixture();
        var sha = Sha(Plan(f));

        f.Data.Msgstore("UPDATE message_forwarded SET forward_score = 6 WHERE forward_score = 7;");
        f.Data.CloseAll();

        var (exit, stdout, stderr) = Scenario.Cli(f.ApplyArgs(sha));

        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.Contains("1 manifest rows no longer match", stderr);
        NoSyntheticTokens(f, stderr);
        Assert.Empty(f.Facts());

        // A new pair the manifest lacks is refused too.
        f.Data.Msgstore("UPDATE message_forwarded SET forward_score = 7 WHERE forward_score = 6;");
        f.Data.AddNonMediaMessage("10:06:30", false, 0, Token + " lonely");
        f.Data.CloseAll();

        var (exit2, _, stderr2) = Scenario.Cli(f.ApplyArgs(sha));

        Assert.Equal(2, exit2);
        Assert.Contains("0 manifest rows no longer match the msgstore and workspace, and 1 text messages would now get a row", stderr2);
        Assert.Empty(f.Facts());
    }

    [Fact]
    public void A_claimed_workspace_is_refused()
    {
        using var f = new Fixture();
        var sha = Sha(Plan(f));

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
        var sha = Sha(Plan(f));
        MigrateTests.MakeVersionFive(f.Data);
        var before = MigrateTests.Hash(f.Data.WorkspacePath);

        var second = Path.Combine(f.Data.DirectoryPath, "text-facts-v5.tsv");

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

        // After migrate, the same manifest applies.
        Assert.Equal(0, Scenario.Cli(["migrate", "--workspace", f.Data.WorkspacePath, "--apply"]).Exit);
        Assert.Equal(0, Scenario.Cli(f.ApplyArgs(sha)).Exit);
        Assert.Equal(5, f.Facts().Count);
    }

    [Fact]
    public void Output_contains_no_text_names_keys_ids_or_paths()
    {
        using var f = new Fixture();
        var plan = Plan(f);
        var sha = Sha(plan);
        var (_, apply, applyErr) = Scenario.Cli(f.ApplyArgs(sha));
        var (_, undo, undoErr) = Scenario.Cli(f.UndoArgs(sha));

        foreach (var text in new[] { plan, apply, applyErr, undo, undoErr })
        {
            NoSyntheticTokens(f, text);
        }
    }

    [Fact]
    public void Text_facts_manifest_round_trips_and_rejects_bad_rows()
    {
        var manifest = new TextFactsManifest
        {
            Parameters = new PairingParameters(1, 7, TimeSpan.FromHours(2), 1),
            Rows = [new TextFactRow(3, 101, true, null), new TextFactRow(4, 102, true, 7), new TextFactRow(5, 103, false, null)],
        };

        var bytes = manifest.ToBytes();
        Assert.Equal(manifest.Rows, TextFactsManifest.Parse(bytes).Rows);

        var text = Encoding.UTF8.GetString(bytes);

        byte[] With(string from, string to) => Encoding.UTF8.GetBytes(text.Replace(from, to, StringComparison.Ordinal));

        Assert.Throws<FormatException>(() => TextFactsManifest.Parse(With("5\t103\t0\t-", "5\t103\t0\t2")));   // score without forwarded
        Assert.Throws<FormatException>(() => TextFactsManifest.Parse(With("5\t103\t0\t-", "5\t102\t0\t-")));   // msgstore message twice
        Assert.Throws<FormatException>(() => TextFactsManifest.Parse(With("5\t103\t0\t-", "4\t103\t0\t-")));   // message twice
        Assert.Throws<FormatException>(() => TextFactsManifest.Parse(With("\t1\t7", "\t2\t7")));

        // Neither the facts nor the import parser takes a text-facts manifest, and the reverse.
        Assert.Throws<FormatException>(() => FactsManifest.Parse(bytes));
        Assert.Throws<FormatException>(() => Manifest.Parse(bytes));
        Assert.Throws<FormatException>(() => TextFactsManifest.Parse(new FactsManifest { Parameters = manifest.Parameters, Rows = [] }.ToBytes()));
    }

    [Fact]
    public void Text_facts_rule_takes_no_sample_and_parses()
    {
        using var f = new Fixture();
        var (exit, _, stderr) = Scenario.Cli([.. f.PlanArgs(), "--sample-out", "x.md", "--seed", "1"]);

        Assert.Equal(2, exit);
        Assert.Contains("--sample-out applies only to", stderr);
        Assert.Equal(PairingRule.TextFacts, Options.Parse(f.PlanArgs()).Rule);
    }
}
