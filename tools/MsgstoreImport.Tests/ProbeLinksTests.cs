using System.Globalization;

namespace MsgstoreImport.Tests;

/// <summary>
/// <c>probe-links</c> (CH-8, CH-10) on three synthetic msgstores: one with <c>message_forwarded</c> and
/// <c>message_association</c>, one with only a legacy <c>message.forwarded</c> column, one with neither.
/// </summary>
public sealed class ProbeLinksTests
{
    private const string Secret = "SECRET-FORWARDED-TEXT";
    private const string KeyToken = "LINKKEY";
    private const string FileToken = "SYNTHFILE";

    private enum Shape
    {
        Modern,
        Legacy,
        Neither,
    }

    /// <summary>
    /// 11:13 received: four attachments, four records (two large, two small copies), paired by the
    /// minute rule; type-1 links large to small, once with the smaller side as child and once as
    /// parent. 11:20 sent: one attachment, two equal-size records (not paired), one type-2 link.
    /// One type-3 link to a message in another chat. A forwarded text message carries text the probe
    /// must never read.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        internal SyntheticData Data { get; } = new();

        internal Fixture(Shape shape, bool unreadableVirtualTable = false)
        {
            var d = Data;

            if (shape == Shape.Modern)
            {
                d.Msgstore(
                    """
                    CREATE TABLE message_forwarded (message_row_id INTEGER PRIMARY KEY, forward_score INTEGER);
                    CREATE TABLE message_association (
                        _id INTEGER PRIMARY KEY, child_message_row_id INTEGER, parent_message_row_id INTEGER,
                        association_type INTEGER);
                    """);
            }
            else if (shape == Shape.Legacy)
            {
                d.Msgstore("ALTER TABLE message ADD COLUMN forwarded INTEGER;");
            }
            else
            {
                d.Msgstore("CREATE TABLE message_thumbnail (key_id TEXT, thumbnail BLOB); INSERT INTO message_thumbnail VALUES ('" + KeyToken + "X', NULL);");
            }

            _ = new[] { d.AddAttachment("11:13:00", false), d.AddAttachment("11:13:00", false),
                        d.AddAttachment("11:13:00", false), d.AddAttachment("11:13:00", false) };
            var large1 = d.AddAsset(size: 4096);
            var small1 = d.AddAsset(size: 1024);
            var large2 = d.AddAsset(size: 4096);
            var small2 = d.AddAsset(size: 1024);

            var l1 = d.AddRecordFor(large1, "11:13:01", false, KeyToken + "L1");
            var s1 = d.AddRecordFor(small1, "11:13:02", false, KeyToken + "S1");
            var l2 = d.AddRecordFor(large2, "11:13:05", false, KeyToken + "L2");
            var s2 = d.AddRecordFor(small2, "11:13:06", false, KeyToken + "S2");

            d.AddAttachment("11:20:00", true);
            var a = d.AddRecord("11:20:01", true, FileToken + "-a.jpg", 500, d.UnknownHash(), KeyToken + "A");
            var b = d.AddRecord("11:20:02", true, FileToken + "-b.jpg", 500, d.UnknownHash(), KeyToken + "B");

            var text = d.AddNonMediaMessage("11:21:00", true, 0, Secret);
            var elsewhere = d.AddRecord("11:22:00", false, FileToken + "-c.jpg", 10, d.UnknownHash(), KeyToken + "C", chat: 8);

            string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

            if (shape == Shape.Modern)
            {
                d.Msgstore(
                    $"""
                    INSERT INTO message_forwarded VALUES ({Id(l1)}, 3), ({Id(a)}, 12), ({Id(text)}, 1);
                    INSERT INTO message_association (child_message_row_id, parent_message_row_id, association_type) VALUES
                        ({Id(s1)}, {Id(l1)}, 1), ({Id(l2)}, {Id(s2)}, 1), ({Id(b)}, {Id(a)}, 2), ({Id(elsewhere)}, {Id(l1)}, 3);
                    """);
            }
            else if (shape == Shape.Legacy)
            {
                d.Msgstore($"UPDATE message SET forwarded = 0; UPDATE message SET forwarded = 1 WHERE _id IN ({Id(l1)}, {Id(text)});");
            }

            if (unreadableVirtualTable)
            {
                // A virtual table whose module this SQLite build lacks, as a custom-tokenizer FTS table
                // would be. CREATE VIRTUAL TABLE would refuse, so the schema row is written directly.
                d.Msgstore(
                    """
                    PRAGMA writable_schema = ON;
                    INSERT INTO sqlite_master (type, name, tbl_name, rootpage, sql) VALUES
                        ('table', 'message_edit_fts', 'message_edit_fts', 0,
                         'CREATE VIRTUAL TABLE message_edit_fts USING nosuchmodule(content)');
                    PRAGMA writable_schema = OFF;
                    """);
            }

            d.RunPhase7();
            d.CloseAll();
        }

        internal (int Exit, string Out, string Err) Run(string? minute = null)
        {
            List<string> args =
            [
                "probe-links", "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath,
                "--conversation", "1", "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture),
                "--utc-offset", SyntheticData.OffsetText, "--local-participant", "1",
            ];

            if (minute is not null)
            {
                args.AddRange(["--minute", minute]);
            }

            return Scenario.Cli([.. args]);
        }

        public void Dispose() => Data.Dispose();
    }

    private static string Section(string text, string start, string? end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "missing section " + start);
        var to = end is null ? -1 : text.IndexOf(end, from, StringComparison.Ordinal);

        return to < 0 ? text[from..] : text[from..to];
    }

    private static void NoIdentifiers(Fixture f, string stdout)
    {
        Assert.DoesNotContain(KeyToken, stdout);
        Assert.DoesNotContain(FileToken, stdout);
        Assert.DoesNotContain("IMG-", stdout);
        Assert.DoesNotContain(Secret, stdout);
        Assert.DoesNotContain(f.Data.DirectoryPath, stdout);
    }

    [Fact]
    public void Modern_msgstore_reports_forwarded_and_association_counts()
    {
        using var f = new Fixture(Shape.Modern);
        var (exit, stdout, stderr) = f.Run();

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);
        NoIdentifiers(f, stdout);

        var scan = Section(stdout, "## 1.", "## 2.");
        Assert.Matches(@"message_association rows\s+4", scan);
        Assert.Matches(@"message_forwarded rows\s+3", scan);
        Assert.Contains("message_association.parent_message_row_id INTEGER", scan);
        Assert.Contains("message_forwarded.forward_score INTEGER", scan);

        var forwarded = Section(stdout, "## 2.", "## 3.");
        Assert.Contains("### Table message_forwarded, keyed by message_row_id", forwarded);
        Assert.Matches(@"messages of the chat marked forwarded\s+3", forwarded);
        Assert.Matches(@"with a message_media row \(media\)\s+2", forwarded);
        Assert.Matches(@"without one \(text and other\)\s+1", forwarded);
        Assert.Matches(@"from_me = 1 \(sent\)\s+2", forwarded);
        Assert.Matches(@"from_me = 0 \(received\)\s+1", forwarded);
        Assert.Matches(@"\n    0\s+1", forwarded);
        Assert.Matches(@"\n    1\s+2", forwarded);
        Assert.Matches(@"forwarded media paired to a workspace attachment\s+1", forwarded);
        Assert.Matches(@"by the minute rule\s+1", forwarded);
        Assert.Matches(@"  1\s+1\r?\n  2-4\s+1\r?\n  10-99\s+1", forwarded);

        var linked = Section(stdout, "## 3.", "## 4.");
        Assert.Contains("parent parent_message_row_id, child child_message_row_id, type association_type", linked);
        var type1 = Section(linked, "type 1: links", "type 2: links");
        Assert.Matches(@"type 1: links\s+2", type1);
        Assert.Matches(@"both sides media\s+2", type1);
        Assert.Matches(@"both sides paired to attachments\s+2", type1);
        Assert.Matches(@"both sides in the same local minute\s+2", type1);
        Assert.Matches(@"child file_size smaller\s+1", type1);
        Assert.Matches(@"parent file_size smaller\s+1", type1);
        Assert.Matches(@"sizes equal\s+0", type1);

        var type2 = Section(linked, "type 2: links", "type 3: links");
        Assert.Matches(@"both sides media\s+1", type2);
        Assert.Matches(@"both sides paired to attachments\s+0", type2);
        Assert.Matches(@"sizes equal\s+1", type2);

        var type3 = Section(linked, "type 3: links", null);
        Assert.Matches(@"type 3: links\s+1", type3);
        Assert.Matches(@"one side outside the chat or missing\s+1", type3);

        Assert.DoesNotContain("## 3a.", stdout);
        Assert.Contains("Forwarding structure found: yes. Linked-copy structure found: yes.", stdout);
    }

    [Fact]
    public void Minute_filter_restricts_links_and_counts_what_hiding_one_side_leaves()
    {
        using var f = new Fixture(Shape.Modern);

        var (exit, stdout, _) = f.Run("2026-03-02T11:13");        Assert.Equal(0, exit);
        NoIdentifiers(f, stdout);

        var minute = Section(stdout, "## 3a.", "## 4.");
        Assert.Contains("2026-03-02 11:13", minute);
        Assert.Matches(@"msgstore media records in the minute \(both directions\)\s+4", minute);
        Assert.Matches(@"workspace attachments in the minute \(both directions\)\s+4", minute);
        Assert.Matches(@"media records in the minute paired to attachments\s+4", minute);
        Assert.Matches(@"type \(all tables and types\): links\s+3", minute); // two type-1 links and the type-3 link
        Assert.Matches(@"message_association type 1: links\s+2", minute);
        Assert.DoesNotContain("type 2: links", minute);
        var type1 = Section(minute, "type 1: links", "type 3: links");
        Assert.Matches(@"left in the minute, child side hidden\s+2", type1);
        Assert.Matches(@"left in the minute, parent side hidden\s+2", type1);

        (exit, stdout, _) = f.Run("2026-03-02T11:20");
        Assert.Equal(0, exit);
        minute = Section(stdout, "## 3a.", "## 4.");
        Assert.Matches(@"msgstore media records in the minute \(both directions\)\s+2", minute);
        Assert.Matches(@"workspace attachments in the minute \(both directions\)\s+1", minute);
        Assert.Matches(@"media records in the minute paired to attachments\s+0", minute);
        Assert.DoesNotContain("type 1: links", minute);
        var type2 = Section(minute, "type 2: links", null);
        Assert.Matches(@"left in the minute, child side hidden\s+1", type2);
        Assert.Matches(@"left in the minute, parent side hidden\s+1", type2);
    }

    [Fact]
    public void Legacy_forwarded_column_only()
    {
        using var f = new Fixture(Shape.Legacy);
        var (exit, stdout, stderr) = f.Run();

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);
        NoIdentifiers(f, stdout);

        Assert.Contains("message.forwarded INTEGER", Section(stdout, "## 1.", "## 2."));

        var forwarded = Section(stdout, "## 2.", "## 3.");
        Assert.Contains("### Legacy column message.forwarded", forwarded);
        Assert.Matches(@"messages of the chat marked forwarded\s+2", forwarded);
        Assert.Matches(@"with a message_media row \(media\)\s+1", forwarded);
        Assert.Matches(@"forwarded media paired to a workspace attachment\s+1", forwarded);
        Assert.Matches(@"  0\s+5\r?\n  1\s+2", forwarded); // seven messages in chat 7

        Assert.Contains("Not present: no table whose name contains 'assoc'", Section(stdout, "## 3.", "## 4."));
        Assert.Contains("Forwarding structure found: yes. Linked-copy structure found: no.", stdout);
    }

    [Fact]
    public void Neither_structure_says_so_and_the_scan_still_lists_matching_tables()
    {
        using var f = new Fixture(Shape.Neither);
        var (exit, stdout, stderr) = f.Run("2026-03-02T11:13");

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);
        NoIdentifiers(f, stdout);

        var scan = Section(stdout, "## 1.", "## 2.");
        Assert.Matches(@"message_thumbnail rows\s+1", scan);
        Assert.Contains("message_thumbnail.thumbnail BLOB", scan);

        Assert.Contains("Not present: no table whose name contains 'forward'", stdout);
        Assert.Contains("Not present: no table whose name contains 'assoc'", stdout);
        Assert.Matches(@"msgstore media records in the minute \(both directions\)\s+4", stdout);
        Assert.Contains("None of the expected structures exist", stdout);
    }

    [Fact]
    public void Unreadable_virtual_table_is_reported_and_the_probe_completes()
    {
        using var f = new Fixture(Shape.Modern, unreadableVirtualTable: true);
        var (exit, stdout, stderr) = f.Run("2026-03-02T11:13");

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);
        NoIdentifiers(f, stdout);
        Assert.DoesNotContain("nosuchmodule", stdout);
        Assert.Contains("table message_edit_fts: schema not readable (SqliteException)", Section(stdout, "## 1.", "## 2."));
        Assert.Matches(@"message_association rows\s+4", stdout);
        Assert.Matches(@"messages of the chat marked forwarded\s+3", stdout);
        Assert.Matches(@"message_association type 1: links\s+2", Section(stdout, "## 3.", "## 3a."));
        Assert.Matches(@"msgstore media records in the minute \(both directions\)\s+4", stdout);
        Assert.Contains("Forwarding structure found: yes. Linked-copy structure found: yes.", stdout);
    }

    [Fact]
    public void Probe_links_is_read_only_and_writes_no_file()
    {
        using var f = new Fixture(Shape.Modern);

        var workspace = PlanTests.Sha(f.Data.WorkspacePath);
        var msgstore = PlanTests.Sha(f.Data.MsgstorePath);
        var files = Directory.GetFiles(f.Data.DirectoryPath).Order().ToList();

        var (exit, _, _) = f.Run("2026-03-02T11:13");

        Assert.Equal(0, exit);
        Assert.Equal(workspace, PlanTests.Sha(f.Data.WorkspacePath));
        Assert.Equal(msgstore, PlanTests.Sha(f.Data.MsgstorePath));
        Assert.Equal(files, Directory.GetFiles(f.Data.DirectoryPath).Order().ToList());
    }

    [Fact]
    public void Probe_links_options_are_checked()
    {
        var (exit, _, stderr) = Scenario.Cli(
            ["probe-links", "--msgstore", "m", "--workspace", "w", "--conversation", "1", "--chat", "7",
             "--utc-offset", "+02:00", "--local-participant", "1", "--minute", "2026-03-02 11:13"]);
        Assert.Equal(2, exit);
        Assert.Contains("--minute must look like", stderr);

        (exit, _, stderr) = Scenario.Cli(
            ["probe", "--msgstore", "m", "--workspace", "w", "--conversation", "1", "--chat", "7",
             "--utc-offset", "+02:00", "--local-participant", "1", "--minute", "2026-03-02T11:13"]);
        Assert.Equal(2, exit);
        Assert.Contains("--minute applies only to probe-links", stderr);

        (exit, _, stderr) = Scenario.Cli(
            ["probe-links", "--msgstore", "m", "--workspace", "w", "--conversation", "1", "--chat", "7",
             "--utc-offset", "+02:00", "--local-participant", "1", "--probe-out", "x"]);
        Assert.Equal(2, exit);
        Assert.Contains("probe-links takes only", stderr);

        (exit, _, stderr) = Scenario.Cli(["probe-links", "--workspace", "w"]);
        Assert.Equal(2, exit);
        Assert.Contains("probe-links needs", stderr);
    }
}
