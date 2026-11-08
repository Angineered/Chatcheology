using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MsgstoreImport.Tests;

/// <summary><c>plan --rule caption-facts</c>, apply and undo-batch (CH-63, CH-71) on synthetic data only.</summary>
public sealed partial class CaptionFactsTests
{
    private const string KeyToken = "CKEY";
    private const string FileToken = "CAPFILE";
    private const string CaptionToken = "SYNTHCAPTION";

    // Every caption below is invented and carries CaptionToken, so a test can prove none is printed.
    private const string OwnCaption = "SYNTHCAPTION own, with trailing spaces  ";
    private const string HiddenCopyCaption = "SYNTHCAPTION on the hidden copy";
    private const string FirstOfPair = "SYNTHCAPTION first of a pair";
    private const string SecondOfPair = "SYNTHCAPTION second of a pair";
    private const string LegacyCaption = "SYNTHCAPTION legacy column";
    private const string TextWins = "SYNTHCAPTION text column";
    private const string StaleLegacy = "SYNTHCAPTION stale legacy";
    private const string DayCaption = "SYNTHCAPTION paired by day";

    /// <summary>
    /// Fourteen attachments, one per case. All incoming, on <see cref="SyntheticData.Day"/> (paired by
    /// the minute rule; extra msgstore messages keep that day from lining up by sequence), except L,
    /// alone on the next day (paired by the day-sequence rule).
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        internal SyntheticData Data { get; } = new();
        internal string ManifestPath => Path.Combine(Data.DirectoryPath, "caption-manifest.tsv");

        internal long A, B1, B2, C1, C2, D, E, F, G, H, I, J, K, L;
        internal long RecordA, RecordB1;

        private int _n;

        internal Fixture(bool mediaCaptionColumn = true)
        {
            var d = Data;

            d.Msgstore(
                """
                CREATE TABLE message_association (
                    _id INTEGER PRIMARY KEY, child_message_row_id INTEGER, parent_message_row_id INTEGER,
                    association_type INTEGER);
                """);

            if (mediaCaptionColumn)
            {
                d.Msgstore("ALTER TABLE message_media ADD COLUMN media_caption TEXT;");
            }

            long Rec(string time, long size = 1000, bool fromMe = false, long chat = SyntheticData.ChatID, DateOnly? date = null) =>
                d.AddRecord(time, fromMe, FileToken + "-" + (++_n).ToString(CultureInfo.InvariantCulture) + ".jpg",
                    size, d.UnknownHash(), KeyToken + _n.ToString(CultureInfo.InvariantCulture), chat: chat, date: date);

            void Caption(long message, string? text) => SetText(d, message, text);
            var links = new List<(long Parent, long Child)>();

            // A: its own caption, stored untrimmed.
            A = d.AddAttachment("10:00:00", false);
            RecordA = Rec("10:00:10");
            Caption(RecordA, OwnCaption);

            // B: the caption sits on the hidden low-quality copy (the smaller parent).
            B1 = d.AddAttachment("10:01:00", false);
            B2 = d.AddAttachment("10:01:00", false);
            RecordB1 = Rec("10:01:01", 500);
            var rB2 = Rec("10:01:02", 5000);
            Caption(RecordB1, HiddenCopyCaption);
            links.Add((RecordB1, rB2));

            // C: both sides captioned differently: each keeps its own.
            C1 = d.AddAttachment("10:02:00", false);
            C2 = d.AddAttachment("10:02:00", false);
            var rC1 = Rec("10:02:01", 500);
            var rC2 = Rec("10:02:02", 5000);
            Caption(rC1, FirstOfPair);
            Caption(rC2, SecondOfPair);
            links.Add((rC1, rC2));

            // D: no own caption, two partners with different captions: ambiguous.
            D = d.AddAttachment("10:03:00", false);
            var rD = Rec("10:03:10");
            links.Add((d.AddNonMediaMessage("10:03:20", false, 0, CaptionToken + " partner one"), rD));
            links.Add((d.AddNonMediaMessage("10:03:30", false, 0, CaptionToken + " partner two"), rD));

            // E: partner in the other direction. F: partner a minute later. G: partner in another chat.
            E = d.AddAttachment("10:04:00", false);
            links.Add((d.AddNonMediaMessage("10:04:20", true, 0, CaptionToken + " other direction"), Rec("10:04:10")));
            F = d.AddAttachment("10:05:00", false);
            links.Add((d.AddNonMediaMessage("10:06:20", false, 0, CaptionToken + " other minute"), Rec("10:05:10")));
            G = d.AddAttachment("10:07:00", false);
            var rG = Rec("10:07:10");
            var elsewhere = Rec("10:07:20", chat: 8);
            Caption(elsewhere, CaptionToken + " other chat");
            links.Add((elsewhere, rG));

            // H: only direction marks and spaces: no caption.
            H = d.AddAttachment("10:08:00", false);
            Caption(Rec("10:08:10"), " ‎‏  ");

            // I: text column empty, media_caption holds it. J: both; the text column wins.
            I = d.AddAttachment("10:09:00", false);
            var rI = Rec("10:09:10");
            J = d.AddAttachment("10:10:00", false);
            var rJ = Rec("10:10:10");
            Caption(rJ, TextWins);

            if (mediaCaptionColumn)
            {
                SetMediaCaption(d, rI, LegacyCaption);
                SetMediaCaption(d, rJ, StaleLegacy);
            }

            // K: one attachment, two records: not paired; its captions are never used.
            K = d.AddAttachment("10:11:00", false);
            Caption(Rec("10:11:10"), CaptionToken + " unpaired one");
            Caption(Rec("10:11:20"), CaptionToken + " unpaired two");

            // L: alone on the next day, a minute apart: the day-sequence rule pairs it.
            var nextDay = SyntheticData.Day.AddDays(1);
            L = d.AddAttachment("08:00:00", false, nextDay);
            Caption(Rec("08:01:00", date: nextDay), DayCaption);

            // An extra text message keeps the main day from lining up by sequence.
            d.AddNonMediaMessage("10:30:00", true, 0, CaptionToken + " plain text");

            foreach (var (parent, child) in links)
            {
                d.Msgstore(
                    $"INSERT INTO message_association (child_message_row_id, parent_message_row_id, association_type) " +
                    $"VALUES ({Id(child)}, {Id(parent)}, 12);");
            }

            // A type-2 link carrying a caption is ignored.
            d.Msgstore(
                $"INSERT INTO message_association (child_message_row_id, parent_message_row_id, association_type) " +
                $"VALUES ({Id(rI)}, {Id(RecordA)}, 2);");

            d.CloseAll();
        }

        internal string[] PlanArgs(string? manifestOut = null) =>
        [
            "plan", "--rule", "caption-facts", "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath,
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

        /// <summary>Every stored caption, by attachment: (text, from the low-quality copy, batch).</summary>
        internal Dictionary<long, (string Text, bool FromCopy, string Batch)> Captions()
        {
            var result = new Dictionary<long, (string, bool, string)>();

            using (var connection = Chatcheology.Data.Workspace.WorkspaceDatabase.OpenReadOnlyConnection(Data.WorkspacePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT AttachmentID, CaptionText, FromLowQualityCopy, SourceBatch FROM AttachmentCaptionFact;";

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    result[reader.GetInt64(0)] = (reader.GetString(1), reader.GetInt64(2) == 1, reader.GetString(3));
                }
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            return result;
        }

        public void Dispose() => Data.Dispose();
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static void SetText(SyntheticData d, long message, string? text)
    {
        d.Msgstore($"UPDATE message SET text_data = {Literal(text)} WHERE _id = {Id(message)};");
    }

    private static void SetMediaCaption(SyntheticData d, long message, string text)
    {
        d.Msgstore($"UPDATE message_media SET media_caption = {Literal(text)} WHERE message_row_id = {Id(message)};");
    }

    private static string Literal(string? text) => text is null ? "NULL" : "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    [GeneratedRegex("manifest SHA-256: ([0-9a-f]{64})")]
    private static partial Regex ShaLine();

    private static string Sha(string stdout) => ShaLine().Match(stdout).Groups[1].Value;

    private static string Line(string label, int value) =>
        Regex.Escape(label) + @"\s+" + value.ToString(CultureInfo.InvariantCulture) + @"\r?\n";

    private static void NoPrivateContent(Fixture f, string text)
    {
        Assert.DoesNotContain(CaptionToken, text);
        Assert.DoesNotContain(KeyToken, text);
        Assert.DoesNotContain(FileToken, text);
        Assert.DoesNotContain(f.Data.DirectoryPath, text);
        Assert.DoesNotContain("Media/WhatsApp", text);
    }

    [Fact]
    public void Plan_counts_each_case_and_writes_no_caption_anywhere()
    {
        using var f = new Fixture();
        var (exit, stdout, stderr) = Scenario.Cli(f.PlanArgs());

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);
        NoPrivateContent(f, stdout);

        Assert.Contains("message.text_data", stdout);
        Assert.Contains("message_media.media_caption", stdout);
        Assert.Matches(Line("attachments in the conversation", 14), stdout);
        Assert.Matches(Line("  paired", 13), stdout);
        Assert.Matches(Line("    by the minute rule", 12), stdout);
        Assert.Matches(Line("    by the day-sequence rule", 1), stdout);
        Assert.Matches(Line("  unpaired (no row)", 1), stdout);
        Assert.Matches(Line("  ambiguous pairing (no row)", 0), stdout);
        Assert.Matches(Line("paired attachments with a caption (manifest rows)", 8), stdout);
        Assert.Matches(Line("  from their own message", 7), stdout);
        Assert.Matches(Line("  from the low-quality partner", 1), stdout);
        Assert.Matches(Line("  read from media_caption (text column empty)", 1), stdout);
        Assert.Matches(Line("  own caption used, partner's differs", 2), stdout);
        Assert.Matches(Line("paired attachments with no caption (no row)", 4), stdout);
        Assert.Matches(Line("paired, ambiguous caption (no row)", 1), stdout);
        Assert.Matches(Line("paired, " + CaptionFactsPlanner.LeftAloneReason, 0), stdout);
        Assert.Matches(Line("type-12 partner links refused", 3), stdout);
        Assert.Matches(Line("  " + CaptionFactsPlanner.LinkOtherChat, 1), stdout);
        Assert.Matches(Line("  " + CaptionFactsPlanner.LinkDirection, 1), stdout);
        Assert.Matches(Line("  " + CaptionFactsPlanner.LinkMinute, 1), stdout);
        Assert.Matches(@"reconciliation \(paired, and attachments\)\s+OK", stdout);

        // The manifest names the rows and digests, never a caption.
        var bytes = File.ReadAllBytes(f.ManifestPath);
        Assert.DoesNotContain(CaptionToken, Encoding.UTF8.GetString(bytes));

        var rows = CaptionFactsManifest.Parse(bytes).Rows.ToDictionary(r => r.AttachmentID);

        Assert.Equal(new[] { f.A, f.B1, f.B2, f.C1, f.C2, f.I, f.J, f.L }.Order(), rows.Keys.Order());
        Assert.Equal(CaptionRow.Digest(OwnCaption), rows[f.A].CaptionSha256);
        Assert.Equal(f.RecordA, rows[f.A].CaptionMessageID);
        Assert.False(rows[f.B1].FromLowQualityCopy);
        Assert.True(rows[f.B2].FromLowQualityCopy);
        Assert.Equal(f.RecordB1, rows[f.B2].CaptionMessageID);
        Assert.Equal(CaptionRow.Digest(HiddenCopyCaption), rows[f.B2].CaptionSha256);
        Assert.Equal(CaptionRow.Digest(FirstOfPair), rows[f.C1].CaptionSha256);
        Assert.Equal(CaptionRow.Digest(SecondOfPair), rows[f.C2].CaptionSha256);
        Assert.Equal(CaptionRow.Digest(LegacyCaption), rows[f.I].CaptionSha256);
        Assert.Equal(CaptionRow.Digest(TextWins), rows[f.J].CaptionSha256);
        Assert.Equal(CaptionRow.Digest(DayCaption), rows[f.L].CaptionSha256);

        // Nothing written to the workspace.
        Assert.Equal(0, f.Data.Count("SELECT COUNT(*) FROM AttachmentCaptionFact;"));
    }

    [Fact]
    public void Apply_stores_the_exact_captions_and_a_rerun_changes_nothing()
    {
        using var f = new Fixture();
        var sha = Sha(Scenario.Cli(f.PlanArgs()).Out);
        var tag = sha[..12];
        var otherTables = f.Data.DumpExcept("AttachmentCaptionFact");

        var (exit, stdout, stderr) = Scenario.Cli(f.ApplyArgs(sha));

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);
        NoPrivateContent(f, stdout);
        Assert.Matches(Line("manifest rows", 8), stdout);
        Assert.Matches(Line("recorded", 8), stdout);
        Assert.Matches(Line("already done (this batch, same facts)", 0), stdout);
        Assert.Matches(@"reconciliation against manifest rows\s+OK", stdout);

        var captions = f.Captions();

        Assert.Equal(8, captions.Count);
        Assert.All(captions.Values, c => Assert.Equal(tag, c.Batch));
        Assert.Equal((OwnCaption, false), (captions[f.A].Text, captions[f.A].FromCopy));
        Assert.Equal((HiddenCopyCaption, false), (captions[f.B1].Text, captions[f.B1].FromCopy));
        Assert.Equal((HiddenCopyCaption, true), (captions[f.B2].Text, captions[f.B2].FromCopy));
        Assert.Equal(FirstOfPair, captions[f.C1].Text);
        Assert.Equal(SecondOfPair, captions[f.C2].Text);
        Assert.Equal(LegacyCaption, captions[f.I].Text);
        Assert.Equal(TextWins, captions[f.J].Text);
        Assert.Equal(DayCaption, captions[f.L].Text);

        // Only the caption table changed.
        var after = f.Data.DumpExcept("AttachmentCaptionFact");
        Assert.Equal(otherTables.Keys.Order(), after.Keys.Order());

        foreach (var (table, rows) in otherTables)
        {
            Assert.Equal(rows, after[table]);
        }

        // A rerun records nothing and says so.
        var (again, againOut, againErr) = Scenario.Cli(f.ApplyArgs(sha));

        Assert.Equal("", againErr);
        Assert.Equal(0, again);
        Assert.Matches(Line("recorded", 0), againOut);
        Assert.Matches(Line("already done (this batch, same facts)", 8), againOut);
        Assert.Equal(captions, f.Captions());
    }

    [Fact]
    public void Undo_removes_only_this_batch()
    {
        using var f = new Fixture();
        var sha = Sha(Scenario.Cli(f.PlanArgs()).Out);
        Assert.Equal(0, Scenario.Cli(f.ApplyArgs(sha)).Exit);

        // A caption from another batch on an attachment this batch does not touch.
        f.Data.Workspace(
            $"""
            INSERT INTO AttachmentCaptionFact (AttachmentID, CaptionText, FromLowQualityCopy, SourceBatch, RecordedDateTimeUtc)
            VALUES ({Id(f.K)}, 'kept', 0, 'otherbatch01', '2026-11-01T00:00:00.0000000Z');
            """);
        f.Data.CloseAll();

        var (exit, stdout, stderr) = Scenario.Cli(f.UndoArgs(sha));

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);
        Assert.Matches(Line("removed", 8), stdout);
        Assert.Matches(Line("not present (never applied or already removed)", 0), stdout);

        var left = f.Captions();
        Assert.Single(left);
        Assert.Equal("otherbatch01", left[f.K].Batch);

        var (again, againOut, _) = Scenario.Cli(f.UndoArgs(sha));
        Assert.Equal(0, again);
        Assert.Matches(Line("removed", 0), againOut);
        Assert.Matches(Line("not present (never applied or already removed)", 8), againOut);
    }

    [Fact]
    public void Apply_refuses_the_whole_batch_when_a_caption_changed_since_the_plan()
    {
        using var f = new Fixture();
        var sha = Sha(Scenario.Cli(f.PlanArgs()).Out);

        SetText(f.Data, f.RecordA, OwnCaption.TrimEnd());
        f.Data.CloseAll();

        var (exit, stdout, stderr) = Scenario.Cli(f.ApplyArgs(sha));

        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.Contains("1 manifest rows no longer match", stderr);
        NoPrivateContent(f, stderr);
        Assert.Empty(f.Captions());
    }

    [Fact]
    public void Apply_refuses_a_manifest_that_was_edited()
    {
        using var f = new Fixture();
        var sha = Sha(Scenario.Cli(f.PlanArgs()).Out);
        File.AppendAllText(f.ManifestPath, "x");

        var (exit, _, stderr) = Scenario.Cli(f.ApplyArgs(sha));

        Assert.Equal(2, exit);
        Assert.Contains("SHA-256 does not match", stderr);
        Assert.Empty(f.Captions());
    }

    [Fact]
    public void Captions_from_another_batch_are_left_alone_and_counted()
    {
        using var f = new Fixture();
        f.Data.Workspace(
            $"""
            INSERT INTO AttachmentCaptionFact (AttachmentID, CaptionText, FromLowQualityCopy, SourceBatch, RecordedDateTimeUtc)
            VALUES ({Id(f.A)}, 'earlier', 0, 'otherbatch01', '2026-11-01T00:00:00.0000000Z'),
                   ({Id(f.E)}, 'earlier too', 0, 'otherbatch01', '2026-11-01T00:00:00.0000000Z');
            """);
        f.Data.CloseAll();

        var (exit, stdout, _) = Scenario.Cli(f.PlanArgs());

        Assert.Equal(0, exit);
        Assert.Matches(Line("paired attachments with a caption (manifest rows)", 7), stdout);
        Assert.Matches(Line("paired attachments with no caption (no row)", 3), stdout);
        Assert.Matches(Line("paired, " + CaptionFactsPlanner.LeftAloneReason, 2), stdout);
        Assert.Matches(@"reconciliation[^\n]*OK", stdout);

        var rows = CaptionFactsManifest.Parse(File.ReadAllBytes(f.ManifestPath)).Rows;
        Assert.DoesNotContain(rows, r => r.AttachmentID == f.A || r.AttachmentID == f.E);
    }

    [Fact]
    public void Without_a_media_caption_column_only_the_text_column_is_read()
    {
        using var f = new Fixture(mediaCaptionColumn: false);
        var (exit, stdout, _) = Scenario.Cli(f.PlanArgs());

        Assert.Equal(0, exit);
        Assert.Matches(@"msgstore media_caption column \(fallback\)\s+absent", stdout);
        Assert.Matches(Line("paired attachments with a caption (manifest rows)", 7), stdout);
        Assert.Matches(Line("  read from media_caption (text column empty)", 0), stdout);
        Assert.Matches(Line("paired attachments with no caption (no row)", 5), stdout);

        var rows = CaptionFactsManifest.Parse(File.ReadAllBytes(f.ManifestPath)).Rows;
        Assert.DoesNotContain(rows, r => r.AttachmentID == f.I);
    }

    [Fact]
    public void Version_five_workspace_is_refused_with_the_migrate_first_message()
    {
        using var f = new Fixture();
        var sha = Sha(Scenario.Cli(f.PlanArgs()).Out);
        MigrateTests.MakeVersionFive(f.Data);
        var before = MigrateTests.Hash(f.Data.WorkspacePath);

        var second = Path.Combine(f.Data.DirectoryPath, "caption-manifest-v5.tsv");

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
    public void Manifest_parse_refuses_a_row_whose_source_disagrees_with_its_flag()
    {
        using var f = new Fixture();
        Assert.Equal(0, Scenario.Cli(f.PlanArgs()).Exit);

        var text = File.ReadAllText(f.ManifestPath);
        var row = text.Split('\n').First(l => l.StartsWith(Id(f.A) + "\t", StringComparison.Ordinal));
        var parts = row.Split('\t');
        parts[3] = "1";

        Assert.Throws<FormatException>(() =>
            CaptionFactsManifest.Parse(Encoding.UTF8.GetBytes(text.Replace(row, string.Join('\t', parts), StringComparison.Ordinal))));
    }

    // -------------------------------------------------------------------------------------------
    // Smoke: the tool builds and -h works.
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void Help_prints_the_usage_and_exits_zero(string flag)
    {
        var (exit, stdout, stderr) = Scenario.Cli([flag]);

        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.Contains("Usage:", stdout);
        Assert.Contains("--rule caption-facts", stdout);
        Assert.Contains("Migrate a schema version 5 workspace to version 6", stdout);
    }

    [Fact]
    public void The_built_tool_runs_and_prints_help()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "MsgstoreImport.dll");
        Assert.True(File.Exists(dll));

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add("-h");

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000));

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("--rule caption-facts", stdout);
    }
}
