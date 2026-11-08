using System.Globalization;

namespace MsgstoreImport.Tests;

/// <summary>
/// <c>probe-text</c> (CH-22) on one synthetic conversation covering each pairing outcome, with and
/// without the msgstore's message_forwarded and message_edit_info tables.
/// </summary>
public sealed class ProbeTextTests
{
    /// <summary>In every synthetic text, so the output can be checked for leaked text.</summary>
    private const string Token = "TXTTOKEN";

    /// <summary>
    /// Day <see cref="SyntheticData.Day"/>, offset +02:00, local participant 1.
    /// 10:00 received: unique. 10:01 sent: same text twice on both sides (2+ candidates).
    /// 10:02 sent: two workspace messages, one msgstore message (shared candidate). 10:03 sent: edited
    /// (different text, msgstore side in message_edit_info). 10:04: workspace received, msgstore sent.
    /// 10:05 received: NFC and CRLF differences only. 10:06 workspace / 10:07 msgstore: one minute
    /// apart. 10:08 received: only a media record in the msgstore minute. Excluded on the workspace
    /// side: an attachment, a caption under the media placeholder, a system message. msgstore only: a
    /// text at 10:20, a type-15 message with no text, a forwarded media record.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        internal SyntheticData Data { get; } = new();

        internal Fixture(bool withTables = true)
        {
            var d = Data;

            long Ws(string time, bool outgoing, string content)
            {
                var id = d.AddWorkspaceMessage(time, outgoing);
                d.Workspace($"UPDATE Message SET MessageContent = '{content.Replace("'", "''", StringComparison.Ordinal)}' WHERE MessageID = {Id(id)};");
                return id;
            }

            Ws("10:00:00", false, Token + " hello one");
            var hello = d.AddNonMediaMessage("10:00:30", false, 0, Token + " hello one");

            Ws("10:01:00", true, Token + " twice");
            Ws("10:01:00", true, Token + " twice");
            var twice = d.AddNonMediaMessage("10:01:10", true, 0, Token + " twice");
            d.AddNonMediaMessage("10:01:20", true, 0, Token + " twice");

            Ws("10:02:00", true, Token + " shared");
            Ws("10:02:00", true, Token + " shared");
            d.AddNonMediaMessage("10:02:10", true, 0, Token + " shared");

            Ws("10:03:00", true, Token + " edited new " + TextProber.EditedMarker);
            var edited = d.AddNonMediaMessage("10:03:05", true, 0, Token + " edited original");

            Ws("10:04:00", false, Token + " direction");
            d.AddNonMediaMessage("10:04:05", true, 0, Token + " direction");

            Ws("10:05:00", false, Token + " café\nline two");
            var nfc = d.AddNonMediaMessage("10:05:05", false, 0, "  " + Token + " café\r\nline two\r\n");

            Ws("10:06:00", false, Token + " late");
            var late = d.AddNonMediaMessage("10:07:10", false, 0, Token + " late");

            Ws("10:08:00", false, Token + " lonely");
            d.AddRecord("10:08:20", false, "SYNTHFILE-a.jpg", 10, d.UnknownHash(), "SYNTHKEYA");

            d.AddAttachment("10:09:00", false);
            Ws("10:10:00", false, "<Media omitted>\n" + Token + " caption");
            d.Workspace(
                """
                INSERT INTO Message (
                    ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                    SenderParticipantID, MessageType, MessageContent, RawContent, SourceLineStart, SourceLineEnd)
                VALUES (1, 1, 1000, '2026-03-02T10:11:00', NULL, 'System', 'TXTTOKEN system', 'raw', 1000, 1000);
                """);

            var lone = d.AddNonMediaMessage("10:20:00", false, 0, Token + " msgstore only");
            var noText = d.AddNonMediaMessage("10:21:00", false, 15, null);
            var media = d.AddRecord("10:30:00", false, "SYNTHFILE-b.jpg", 10, d.UnknownHash(), "SYNTHKEYB");

            if (withTables)
            {
                d.Msgstore(
                    $"""
                    CREATE TABLE message_forwarded (message_row_id INTEGER PRIMARY KEY, forward_score INTEGER);
                    CREATE TABLE message_edit_info (message_row_id INTEGER PRIMARY KEY, edited_timestamp INTEGER);
                    INSERT INTO message_forwarded (message_row_id) VALUES
                        ({Id(hello)}), ({Id(nfc)}), ({Id(twice)}), ({Id(lone)}), ({Id(late)}), ({Id(noText)}), ({Id(media)});
                    INSERT INTO message_edit_info VALUES ({Id(edited)}, 1);
                    """);
            }

            d.CloseAll();
        }

        internal (int Exit, string Out, string Err) Run() =>
            Scenario.Cli(
            [
                "probe-text", "--msgstore", Data.MsgstorePath, "--workspace", Data.WorkspacePath,
                "--conversation", "1", "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture),
                "--utc-offset", SyntheticData.OffsetText, "--local-participant", "1",
            ]);

        public void Dispose() => Data.Dispose();
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static string Section(string text, string start, string? end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "missing section " + start);
        var to = end is null ? -1 : text.IndexOf(end, from + start.Length, StringComparison.Ordinal);

        return to < 0 ? text[from..] : text[from..to];
    }

    private static string Run(Fixture f)
    {
        var (exit, stdout, stderr) = f.Run();

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);

        return stdout;
    }

    [Fact]
    public void Messages_are_classified_and_the_text_column_is_reported()
    {
        using var f = new Fixture();
        var section = Section(Run(f), "## 1.", "## 2.");

        Assert.Contains("msgstore text column used: message.text_data", section);
        Assert.Matches(@"workspace messages in the conversation\s+13", section);
        Assert.Matches(@"system messages \(excluded\)\s+1", section);
        Assert.Matches(@"attachment messages \(excluded\)\s+1", section);
        Assert.Matches(@"placeholder with a caption \(excluded\)\s+1", section);
        Assert.Matches(@"  text messages\s+10", section);
        Assert.Matches(@"sent \(local participant\)\s+5", section);
        Assert.Matches(@"ending in the export's edited marker\s+1", section);
        Assert.Matches(@"msgstore messages in the chat\s+12", section);
        Assert.Matches(@"  text-bearing\s+9", section);
        Assert.Matches(@"with one \(captions\)\s+0", section);
    }

    [Fact]
    public void Unique_ambiguous_and_unmatched_are_counted_by_the_rule()
    {
        using var f = new Fixture();
        var rule = Section(Run(f), "normalised text, same minute (the rule):", "exact text without normalisation");

        // Unique: 10:00 and the NFC pair at 10:05.
        Assert.Matches(@"matched uniquely\s+2", rule);
        // Same text twice in a minute (2 + 2 candidates) and a shared single candidate (2).
        Assert.Matches(@"  ambiguous\s+4", rule);
        Assert.Matches(@"2\+ msgstore candidates\s+2", rule);
        Assert.Matches(@"one candidate, shared with another workspace message\s+2", rule);
        Assert.Matches(@"unmatched \(no candidate\)\s+4", rule);
    }

    [Fact]
    public void Edited_text_is_unmatched_with_a_candidate_and_direction_mismatch_is_split_out()
    {
        using var f = new Fixture();
        var stdout = Run(f);
        var rule = Section(stdout, "normalised text, same minute (the rule):", "exact text without normalisation");

        Assert.Matches(@"same direction text-bearing, different text\s+1", rule);
        Assert.Matches(@"of which ending in the export's edited marker\s+1", rule);
        Assert.Matches(@"would match with that marker removed \(reported only\)\s+0", rule);
        Assert.Matches(@"identical text, other direction only\s+1", rule);
        Assert.Matches(@"other msgstore messages only \(no text or other direction\)\s+1", rule);
        Assert.Matches(@"no msgstore message in that minute at all\s+1", rule);

        var edits = Section(stdout, "## 4.", "## 5.");
        Assert.Matches(@"msgstore messages of the chat in message_edit_info\s+1", edits);
        Assert.Matches(@"unmatched \(no workspace candidate\)\s+1", edits);
        Assert.Matches(@"a same-minute candidate is in message_edit_info\s+1", edits);
    }

    [Fact]
    public void Nfc_and_line_endings_match_only_after_normalisation()
    {
        Assert.Equal(TextProber.Normalise("café\nx"), TextProber.Normalise(" café\r\nx\r\n"));

        using var f = new Fixture();
        var pairing = Section(Run(f), "## 2.", "## 3.");
        var raw = Section(pairing, "exact text without normalisation, same minute:", "+-1 minute variant");

        Assert.Matches(@"matched uniquely\s+1", raw);
        Assert.Matches(@"unique by the rule but not without normalisation\s+1", raw);
    }

    [Fact]
    public void One_minute_window_is_reported_only()
    {
        using var f = new Fixture();
        var wide = Section(Run(f), "+-1 minute variant", "## 3.");

        Assert.Matches(@"matched uniquely\s+3", wide);
        Assert.Matches(@"would be gained\)\s+1", wide);
        Assert.Matches(@"would be lost\)\s+0", wide);
    }

    [Fact]
    public void Forwarded_messages_without_media_are_counted_by_type_and_pairing()
    {
        using var f = new Fixture();
        var stdout = Run(f);
        var forwarded = Section(stdout, "## 3.", "## 4.");

        Assert.Contains("Forwarded = a message_forwarded row (message_row_id).", forwarded);
        Assert.Matches(@"forwarded messages of the chat\s+7", forwarded);
        Assert.Matches(@"  without a message_media row\s+6", forwarded);
        Assert.Matches(@"from_me = 1 \(sent\)\s+1", forwarded);
        Assert.Matches(@"\n      0\s+5\r?\n        paired uniquely\s+2", forwarded);
        Assert.Matches(@"\n      15\s+1\r?\n        paired uniquely\s+0", forwarded);
        Assert.Matches(@"no text \(not text-bearing\)\s+1", forwarded);
        Assert.Matches(@"    text-bearing\s+5", forwarded);
        Assert.Matches(@"paired uniquely to a workspace text message\s+2", forwarded);
        Assert.Matches(@"      ambiguous\s+1", forwarded);
        Assert.Matches(@"unmatched \(no workspace candidate\)\s+2", forwarded);
        Assert.Matches(@"unique with \+-1 minute but not by the rule \(reported only\)\s+1", forwarded);

        Assert.Contains(
            "2 of 10 workspace text messages (20.0%) pair uniquely by minute, direction and normalised text; " +
            "2 of 6 forwarded messages without a media row (33.3%) do.", stdout);
    }

    private static string RunSmall(Action<SyntheticData, Func<string, bool, string, long>> build)
    {
        using var d = new SyntheticData();

        long Ws(string time, bool outgoing, string content)
        {
            var id = d.AddWorkspaceMessage(time, outgoing);
            d.Workspace($"UPDATE Message SET MessageContent = '{content.Replace("'", "''", StringComparison.Ordinal)}' WHERE MessageID = {Id(id)};");
            return id;
        }

        build(d, Ws);
        d.CloseAll();

        var (exit, stdout, stderr) = Scenario.Cli(
        [
            "probe-text", "--msgstore", d.MsgstorePath, "--workspace", d.WorkspacePath,
            "--conversation", "1", "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture),
            "--utc-offset", SyntheticData.OffsetText, "--local-participant", "1",
        ]);

        Assert.Equal("", stderr);
        Assert.Equal(0, exit);
        Assert.DoesNotContain(Token, stdout);

        return stdout;
    }

    [Fact]
    public void Direction_marks_in_msgstore_text_are_removed_by_normalisation_only()
    {
        Assert.Equal(TextProber.Normalise(Token + " marks"), TextProber.Normalise("\u200E" + Token + "\u200F marks"));

        var stdout = RunSmall((d, ws) =>
        {
            ws("10:00:00", false, Token + " marks");
            d.AddNonMediaMessage("10:00:10", false, 0, "\u200E" + Token + "\u200F marks");
        });

        var rule = Section(stdout, "normalised text, same minute (the rule):", "exact text without normalisation");
        var raw = Section(stdout, "exact text without normalisation, same minute:", "+-1 minute variant");

        Assert.Matches(@"matched uniquely\s+1", rule);
        Assert.Matches(@"matched uniquely\s+0", raw);
        Assert.Matches(@"unmatched \(no candidate\)\s+1", raw);
    }

    [Fact]
    public void Removing_the_edited_marker_is_counted_but_not_adopted()
    {
        Assert.Equal(Token + " fixed", TextProber.WithoutEditedMarker(Token + " fixed \n " + TextProber.EditedMarker));

        var stdout = RunSmall((d, ws) =>
        {
            ws("10:00:00", true, Token + " fixed  " + TextProber.EditedMarker);
            d.AddNonMediaMessage("10:00:10", true, 0, Token + " fixed");
            ws("10:01:00", true, Token + " other " + TextProber.EditedMarker);
            d.AddNonMediaMessage("10:01:10", true, 0, Token + " different");
        });

        var rule = Section(stdout, "normalised text, same minute (the rule):", "exact text without normalisation");

        Assert.Matches(@"matched uniquely\s+0", rule);
        Assert.Matches(@"same direction text-bearing, different text\s+2", rule);
        Assert.Matches(@"of which ending in the export's edited marker\s+2", rule);
        Assert.Matches(@"would match with that marker removed \(reported only\)\s+1", rule);
    }

    [Fact]
    public void Missing_forward_and_edit_tables_are_reported_as_not_present()
    {
        using var f = new Fixture(withTables: false);
        var stdout = Run(f);

        Assert.Contains("Not present: no message_forwarded table with message_row_id and no message.forwarded column.", stdout);
        Assert.Contains("Not present: no message_edit_info table with message_row_id.", stdout);
        Assert.Matches(@"matched uniquely\s+2", stdout);
    }

    [Fact]
    public void Output_contains_no_text_names_keys_or_paths()
    {
        using var f = new Fixture();
        var stdout = Run(f);

        Assert.DoesNotContain(Token, stdout);
        Assert.DoesNotContain("hello", stdout);
        Assert.DoesNotContain("caf", stdout);
        Assert.DoesNotContain("SYNTHTEXT", stdout);
        Assert.DoesNotContain("SYNTHKEY", stdout);
        Assert.DoesNotContain("SYNTHFILE", stdout);
        Assert.DoesNotContain("Alex", stdout);
        Assert.DoesNotContain("Sam", stdout);
        Assert.DoesNotContain(f.Data.DirectoryPath, stdout);
    }

    [Fact]
    public void Probe_text_is_read_only_and_writes_no_file()
    {
        using var f = new Fixture();

        var workspace = PlanTests.Sha(f.Data.WorkspacePath);
        var msgstore = PlanTests.Sha(f.Data.MsgstorePath);
        var files = Directory.GetFiles(f.Data.DirectoryPath).Order().ToList();

        Run(f);

        Assert.Equal(workspace, PlanTests.Sha(f.Data.WorkspacePath));
        Assert.Equal(msgstore, PlanTests.Sha(f.Data.MsgstorePath));
        Assert.Equal(files, Directory.GetFiles(f.Data.DirectoryPath).Order().ToList());
    }

    [Fact]
    public void Probe_text_options_are_checked()
    {
        var (exit, _, stderr) = Scenario.Cli(
            ["probe-text", "--msgstore", "m", "--workspace", "w", "--conversation", "1", "--chat", "7",
             "--utc-offset", "+02:00", "--local-participant", "1", "--manifest-out", "x"]);
        Assert.Equal(2, exit);
        Assert.Contains("probe-text takes only the plan inputs", stderr);

        (exit, _, stderr) = Scenario.Cli(["probe-text", "--workspace", "w", "--chat", "7"]);
        Assert.Equal(2, exit);
        Assert.Contains("probe-text needs", stderr);

        (exit, _, stderr) = Scenario.Cli(
            ["probe-text", "--msgstore", "m", "--workspace", "w", "--conversation", "1", "--chat", "7",
             "--utc-offset", "+02:00", "--local-participant", "1", "--minute", "2026-03-02T10:00"]);
        Assert.Equal(2, exit);
        Assert.Contains("--minute applies only to probe-links", stderr);
    }
}
