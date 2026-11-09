using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Chatcheology.Data.NewerMessages;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport.Tests;

/// <summary>
/// <c>newer plan</c> / <c>newer apply</c> (CH-71 step 3a) on synthetic data only: the hand-off check, placeholders,
/// media Restored/Unmatched, low-quality links, idempotent apply, a second run, the time zone and counts-only output.
/// </summary>
public sealed class NewerTests : IDisposable
{
    private static readonly DateOnly Earlier = SyntheticData.Day.AddDays(-1);
    private static readonly DateOnly Next = SyntheticData.Day.AddDays(1);

    private readonly SyntheticData _d = new();
    private readonly string _root;
    private readonly List<string> _secrets = [];
    private readonly List<string> _keys = [];
    private int _plans;

    public NewerTests()
    {
        _root = Path.Combine(_d.DirectoryPath, "phone-copy", "WhatsApp");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => _d.Dispose();

    private string NewerPath => NewerMessagesDatabase.PathBeside(_d.WorkspacePath);

    // -------------------------------------------------------------------------------------------
    // Synthetic building blocks.
    // -------------------------------------------------------------------------------------------

    /// <summary>The past both sides share: an earlier day and the workspace's last day, lined up exactly.</summary>
    private void SharedPast(string lastDayInText = "Bye now", bool lastDayOutgoingFirst = true)
    {
        _d.AddWorkspaceMessage("10:00:00", outgoing: true, Earlier, content: "Earlier one");
        _d.AddWorkspaceMessage("10:01:00", outgoing: false, Earlier, content: "Earlier two");
        Text("10:00:05", true, "Earlier one", Earlier);
        Text("10:01:05", false, "Earlier two", Earlier);

        _d.AddWorkspaceMessage("09:00:00", outgoing: lastDayOutgoingFirst, content: "Hello there");
        _d.AddAttachment("09:05:00", outgoing: false);
        _d.AddWorkspaceMessage("09:10:00", outgoing: false, content: "Bye now <This message was edited>");

        Text("09:00:10", true, "Hello there");
        _d.AddRecord("09:05:00", false, "IMG-shared.jpg", 10, _d.UnknownHash(), Key("shared"));
        Text("09:10:00", false, "\u200E" + lastDayInText + "  ");
    }

    private long Text(string time, bool fromMe, string text, DateOnly? date = null, int type = 0)
    {
        _secrets.Add(text.Trim('\u200E', ' '));
        var id = _d.AddNonMediaMessage(time, fromMe, type, text, date);
        _keys.Add("SYNTHTEXT" + id.ToString(CultureInfo.InvariantCulture));

        return id;
    }

    private string Key(string name)
    {
        var key = "SYNTHNEWER" + name.ToUpperInvariant();
        _keys.Add(key);

        return key;
    }

    /// <summary>A media message whose file is written under the media root with the given bytes.</summary>
    private long Media(string time, bool fromMe, string name, byte[]? fileBytes, string? hash, DateOnly? date = null,
        string? caption = null, int type = 1, long? size = null)
    {
        _secrets.Add(name);
        var id = _d.AddRecord(time, fromMe, name, size ?? fileBytes?.Length ?? 10, hash, Key(name), date: date, messageType: type);

        if (fileBytes is not null)
        {
            var path = Path.Combine(_root, "Media", "WhatsApp Images", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, fileBytes);
        }

        if (caption is not null)
        {
            _secrets.Add(caption);
            _d.Msgstore($"UPDATE message SET text_data = '{caption}' WHERE _id = {id};");
        }

        return id;
    }

    private static string HashOf(byte[] bytes) => Convert.ToBase64String(SHA256.HashData(bytes));

    private static byte[] Bytes(string seed) => Encoding.UTF8.GetBytes("synthetic file " + seed);

    // -------------------------------------------------------------------------------------------
    // Driving the tool.
    // -------------------------------------------------------------------------------------------

    private string ManifestPath(int n) => Path.Combine(_d.DirectoryPath, $"newer-manifest-{n.ToString(CultureInfo.InvariantCulture)}.tsv");

    private (int Exit, string Out, string Err, string Sha, string Manifest) Plan()
    {
        _d.CloseAll();
        var manifest = ManifestPath(++_plans);

        var (exit, stdout, stderr) = Scenario.Cli(
        [
            "newer", "plan", "--msgstore", _d.MsgstorePath, "--workspace", _d.WorkspacePath,
            "--conversation", "1", "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture),
            "--local-participant", "1", "--media-root", _root, "--manifest-out", manifest,
        ]);

        var sha = stdout.Split('\n').FirstOrDefault(l => l.StartsWith("manifest SHA-256: ", StringComparison.Ordinal))?
            ["manifest SHA-256: ".Length..].Trim() ?? "";

        return (exit, stdout, stderr, sha, manifest);
    }

    private (int Exit, string Out, string Err) Apply(string manifest, string sha, string? root = null)
    {
        _d.CloseAll();

        return Scenario.Cli(
        [
            "newer", "apply", "--msgstore", _d.MsgstorePath, "--workspace", _d.WorkspacePath, "--media-root", root ?? _root,
            "--manifest", manifest, "--manifest-sha256", sha, "--i-have-a-backup",
        ]);
    }

    private NewerMessagesContents PlanAndApply()
    {
        var plan = Plan();
        Assert.True(plan.Exit == 0, plan.Err);

        var apply = Apply(plan.Manifest, plan.Sha);
        Assert.True(apply.Exit == 0, apply.Err);
        AssertCountsOnly(plan.Out + plan.Err + apply.Out + apply.Err);

        return NewerMessagesReader.Read(NewerPath);
    }

    private void AssertCountsOnly(string output)
    {
        foreach (var secret in _secrets.Concat(_keys).Where(s => s.Length > 0))
        {
            Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(_root, output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_d.DirectoryPath, output, StringComparison.OrdinalIgnoreCase);
    }

    private static string Line(string report, string label) =>
        report.Split('\n').First(l => l.TrimStart().StartsWith(label, StringComparison.Ordinal)).Trim();

    // -------------------------------------------------------------------------------------------
    // Tests.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void First_run_hands_off_after_the_workspace_and_imports_only_newer_messages()
    {
        SharedPast();
        Text("09:20:00", false, "A newer text");
        var restored = Bytes("restored");
        Media("08:00:00", true, "IMG-new-restored.jpg", restored, HashOf(restored), Next, caption: "Sunset caption");
        Media("08:01:00", false, "IMG-new-wronghash.jpg", Bytes("wrong"), HashOf(Bytes("something else")), Next);
        Media("08:02:00", false, "IMG-new-nofile.jpg", null, HashOf(Bytes("absent")), Next);
        Media("08:03:00", false, "IMG-new-emptyhash.jpg", Bytes("empty"), "", Next);
        Text("08:04:00", true, "Poll question secret", Next, type: 66);
        Text("08:05:00", false, "Location label secret", Next, type: 5);
        Text("08:06:00", false, "System row secret", Next, type: 7);
        Text("08:07:00", false, "Mystery content secret", Next, type: 99);
        var deletedID = _d.AddNonMediaMessage("08:08:00", true, 15, null, Next);
        _keys.Add("SYNTHTEXT" + deletedID.ToString(CultureInfo.InvariantCulture));

        var plan = Plan();
        Assert.True(plan.Exit == 0, plan.Err);
        AssertCountsOnly(plan.Out + plan.Err);

        Assert.EndsWith("1", Line(plan.Out, "earlier days compared"));
        Assert.EndsWith("3", Line(plan.Out, "workspace messages on its last day"));
        Assert.EndsWith("2", Line(plan.Out, "same normalised text"));
        Assert.EndsWith("1", Line(plan.Out, "media facing media"));
        Assert.EndsWith("10", Line(plan.Out, "new messages"));
        Assert.EndsWith("4", Line(plan.Out, "user media"));
        Assert.EndsWith("1", Line(plan.Out, "user text"));
        Assert.EndsWith("4", Line(plan.Out, "placeholders (fixed wording)"));
        Assert.EndsWith("1", Line(plan.Out, "Restored (SHA-256"));
        Assert.EndsWith("3", Line(plan.Out, "Unmatched"));
        Assert.EndsWith("1", Line(plan.Out, NewerPlanner.HashDiffers));
        Assert.EndsWith("1", Line(plan.Out, NewerPlanner.NoFile));
        Assert.EndsWith("1", Line(plan.Out, NewerPlanner.HashEmpty));
        Assert.EndsWith("1", Line(plan.Out, "type 99 (placeholder Unknown)"));
        Assert.EndsWith("1", Line(plan.Out, "type 66 (placeholder Poll)"));
        Assert.EndsWith("1", Line(plan.Out, "with a caption"));
        Assert.EndsWith("10", Line(plan.Out, $"{SyntheticData.Day:yyyy-MM}"));
        Assert.False(File.Exists(NewerPath), "plan must not create newer-messages.db");

        var apply = Apply(plan.Manifest, plan.Sha);
        Assert.True(apply.Exit == 0, apply.Err);
        AssertCountsOnly(apply.Out + apply.Err);

        var contents = NewerMessagesReader.Read(NewerPath);
        var m = contents.Messages;

        Assert.Equal(10, m.Count);
        Assert.Single(contents.Runs);
        Assert.Equal(NewerMessagesDatabase.HandOffWorkspace, contents.Runs[0].HandOff);
        Assert.Equal(m[^1].KeyID, contents.LastKeyID);

        // The text exactly as the msgstore holds it; the sender from from_me.
        Assert.Equal("A newer text", m[0].Text);
        Assert.Equal(NewerMessagesDatabase.KindUser, m[0].Kind);
        Assert.Equal(SyntheticData.OtherParticipantID, m[0].SenderParticipantID);
        Assert.Equal($"{SyntheticData.Day:yyyy-MM-dd}T09:20:00", m[0].DateTimeLocal);

        var media = m[1];
        Assert.Null(media.Text);
        Assert.Equal("Sunset caption", media.Caption);
        Assert.Equal(SyntheticData.LocalParticipantID, media.SenderParticipantID);
        Assert.Equal(NewerMessagesDatabase.StateRestored, media.Attachment!.State);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(restored)).ToLowerInvariant(), media.Attachment.Sha256);
        Assert.Equal("Media/WhatsApp Images/IMG-new-restored.jpg", media.Attachment.RelativePath);
        Assert.Equal(Path.GetFullPath(_root), media.Attachment.MediaRootPath);

        Assert.All(m.Skip(2).Take(3), x => Assert.Equal(NewerMessagesDatabase.StateUnmatched, x.Attachment!.State));
        Assert.All(m.Skip(2).Take(3), x => Assert.Null(x.Attachment!.Sha256));

        // Placeholders: fixed wording only, never the item's content.
        Assert.Equal(("Placeholder", "Poll", "Poll (not shown in this archive)"), (m[5].Kind, m[5].PlaceholderKind, m[5].Text));
        Assert.Equal(("Placeholder", "Location", "Location (not shown in this archive)"), (m[6].Kind, m[6].PlaceholderKind, m[6].Text));
        Assert.Equal(("System", (string?)null, "System message (not shown in this archive)"), (m[7].Kind, m[7].PlaceholderKind, m[7].Text));
        Assert.Null(m[7].SenderParticipantID);
        Assert.Equal(("Placeholder", "Unknown", "Message type not shown in this archive"), (m[8].Kind, m[8].PlaceholderKind, m[8].Text));
        Assert.Equal(("Placeholder", "Deleted"), (m[9].Kind, m[9].PlaceholderKind));
        Assert.DoesNotContain(m, x => x.Text?.Contains("secret", StringComparison.Ordinal) == true);

        // The manifest holds ids and digests, never text, captions or file names.
        var manifest = File.ReadAllText(plan.Manifest);
        Assert.DoesNotContain("Sunset", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("newer text", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("IMG-new", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void An_earlier_day_with_a_different_count_refuses_with_counts_only()
    {
        SharedPast();
        Text("11:00:00", true, "Only on the phone", Earlier);
        Text("09:20:00", false, "A newer text");

        var plan = Plan();

        Assert.Equal(2, plan.Exit);
        Assert.Contains("Hand-off refused: 1 of 1 days before the workspace's last day", plan.Err, StringComparison.Ordinal);
        Assert.Contains("1 in both with different counts", plan.Err, StringComparison.Ordinal);
        Assert.False(File.Exists(plan.Manifest));
        AssertCountsOnly(plan.Out + plan.Err);
    }

    [Fact]
    public void A_last_day_direction_mismatch_refuses()
    {
        SharedPast(lastDayOutgoingFirst: false);

        var plan = Plan();

        Assert.Equal(2, plan.Exit);
        Assert.Contains("1 differ in direction, 0 in text", plan.Err, StringComparison.Ordinal);
        AssertCountsOnly(plan.Out + plan.Err);
    }

    [Fact]
    public void A_last_day_text_mismatch_refuses()
    {
        SharedPast(lastDayInText: "Bye later");

        var plan = Plan();

        Assert.Equal(2, plan.Exit);
        Assert.Contains("0 differ in direction, 1 in text, 0 media against text", plan.Err, StringComparison.Ordinal);
        AssertCountsOnly(plan.Out + plan.Err);
    }

    [Fact]
    public void Media_facing_a_workspace_text_message_refuses()
    {
        _d.AddWorkspaceMessage("09:00:00", outgoing: true, content: "Hello there");
        Media("09:00:10", true, "IMG-instead.jpg", null, _d.UnknownHash());

        var plan = Plan();

        Assert.Equal(2, plan.Exit);
        Assert.Contains("1 media against text", plan.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Fewer_msgstore_messages_on_the_last_day_refuses()
    {
        _d.AddWorkspaceMessage("09:00:00", outgoing: true, content: "Hello there");
        _d.AddWorkspaceMessage("09:01:00", outgoing: true, content: "Second");
        Text("09:00:10", true, "Hello there");

        var plan = Plan();

        Assert.Equal(2, plan.Exit);
        Assert.Contains("the msgstore has 1 messages, fewer than the workspace's 2", plan.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_twice_writes_nothing_the_second_time()
    {
        SharedPast();
        Text("09:20:00", false, "A newer text");

        var plan = Plan();
        Assert.Equal(0, Apply(plan.Manifest, plan.Sha).Exit);
        var before = File.ReadAllBytes(NewerPath);

        var again = Apply(plan.Manifest, plan.Sha);

        Assert.Equal(0, again.Exit);
        Assert.Contains("this manifest was already applied", again.Out, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(NewerPath));
        Assert.Single(NewerMessagesReader.Read(NewerPath).Runs);
    }

    [Fact]
    public void Apply_refuses_when_the_msgstore_no_longer_gives_the_plan()
    {
        SharedPast();
        Text("09:20:00", false, "A newer text");

        var plan = Plan();
        Text("09:30:00", true, "Arrived after the plan");

        var apply = Apply(plan.Manifest, plan.Sha);

        Assert.Equal(2, apply.Exit);
        Assert.Contains("Plan again. Nothing was written.", apply.Err, StringComparison.Ordinal);
        Assert.False(File.Exists(NewerPath));
    }

    [Fact]
    public void Apply_refuses_another_media_root()
    {
        SharedPast();
        Text("09:20:00", false, "A newer text");

        var plan = Plan();
        var other = Path.Combine(_d.DirectoryPath, "other-root");
        Directory.CreateDirectory(other);

        Assert.Equal(2, Apply(plan.Manifest, plan.Sha, other).Exit);
        Assert.False(File.Exists(NewerPath));
    }

    [Fact]
    public void A_second_run_appends_only_the_messages_after_the_last_imported_one()
    {
        SharedPast();
        Text("09:20:00", false, "A newer text");
        var first = PlanAndApply();
        Assert.Single(first.Messages);

        var bytes = Bytes("second run");
        Text("07:00:00", true, "Second run text", Next);
        Media("07:01:00", false, "IMG-second.jpg", bytes, HashOf(bytes), Next);

        var plan = Plan();
        Assert.True(plan.Exit == 0, plan.Err);
        Assert.Contains("later run, after the last imported message", plan.Out, StringComparison.Ordinal);
        Assert.EndsWith("2", Line(plan.Out, "new messages"));

        Assert.Equal(0, Apply(plan.Manifest, plan.Sha).Exit);

        var second = NewerMessagesReader.Read(NewerPath);
        Assert.Equal(3, second.Messages.Count);
        Assert.Equal(2, second.Runs.Count);
        Assert.Equal(NewerMessagesDatabase.HandOffPreviousRun, second.Runs[1].HandOff);
        Assert.Equal(first.Messages[0].KeyID, second.Runs[1].HandOffKeyID);

        // Earlier rows keep their ids; new ones follow in msgstore order.
        Assert.Equal(first.Messages[0].NewerMessageID, second.Messages[0].NewerMessageID);
        Assert.Equal([1L, 2L, 3L], second.Messages.Select(x => x.NewerMessageID));
        Assert.Equal(NewerMessagesDatabase.StateRestored, second.Messages[2].Attachment!.State);

        // A third plan with nothing new imports nothing.
        var third = Plan();
        Assert.Equal(0, third.Exit);
        Assert.EndsWith("0", Line(third.Out, "new messages"));
    }

    [Fact]
    public void A_later_run_refuses_when_the_last_imported_key_is_missing()
    {
        SharedPast();
        var last = Text("09:20:00", false, "A newer text");
        PlanAndApply();

        _d.Msgstore($"DELETE FROM message WHERE _id = {last};");
        Text("07:00:00", true, "Second run text", Next);

        var plan = Plan();

        Assert.Equal(2, plan.Exit);
        Assert.Contains("the last imported message's key_id is found 0 times", plan.Err, StringComparison.Ordinal);
        AssertCountsOnly(plan.Out + plan.Err);
    }

    [Fact]
    public void A_later_run_refuses_a_workspace_era_message_now_sorted_after_the_hand_off()
    {
        SharedPast();
        Text("09:20:00", false, "A newer text");
        PlanAndApply();

        // A renumbered msgstore (restore or migration): a message of the earlier day now sorts last.
        _d.Msgstore("UPDATE message SET sort_id = 900000 WHERE text_data = 'Earlier one';");
        Text("07:00:00", true, "Second run text", Next);

        var plan = Plan();

        Assert.Equal(2, plan.Exit);
        Assert.Contains("1 messages after the hand-off are on or before the workspace's last day, 0 messages before",
            plan.Err, StringComparison.Ordinal);
        AssertCountsOnly(plan.Out + plan.Err);
    }

    [Fact]
    public void A_later_run_refuses_a_never_imported_message_now_sorted_before_the_hand_off()
    {
        SharedPast();
        Text("09:20:00", false, "A newer text");
        PlanAndApply();

        var skipped = Text("07:00:00", true, "Sorted too early", Next);
        _d.Msgstore($"UPDATE message SET sort_id = 1 WHERE _id = {skipped};");

        var plan = Plan();

        Assert.Equal(2, plan.Exit);
        Assert.Contains("0 messages after the hand-off are on or before the workspace's last day, 1 messages before",
            plan.Err, StringComparison.Ordinal);
        AssertCountsOnly(plan.Out + plan.Err);
    }

    [Fact]
    public void The_same_key_id_in_both_directions_is_two_messages()
    {
        SharedPast();
        var sent = Text("09:20:00", true, "Sent with a shared key");
        var received = Text("09:21:00", false, "Received with the same key");
        _d.Msgstore($"UPDATE message SET key_id = 'SYNTHTEXT{sent}' WHERE _id = {received};");

        var rows = PlanAndApply().Messages;

        Assert.Equal([(true, "SYNTHTEXT" + sent), (false, "SYNTHTEXT" + sent)], rows.Select(r => (r.FromMe, r.KeyID)));

        // The later run finds the last message by direction and key_id together.
        Text("07:00:00", true, "Second run text", Next);
        var plan = Plan();

        Assert.True(plan.Exit == 0, plan.Err);
        Assert.EndsWith("1", Line(plan.Out, "new messages"));
    }

    [Fact]
    public void Edits_and_deletes_of_imported_messages_are_counted_and_the_archived_version_kept()
    {
        SharedPast();
        var edited = Text("09:20:00", false, "A newer text");
        var deleted = Text("09:21:00", true, "Soon deleted");
        Text("09:22:00", true, "Stays");
        PlanAndApply();

        _d.Msgstore($"UPDATE message SET text_data = 'A newer text, edited' WHERE _id = {edited};");
        _d.Msgstore($"UPDATE message SET message_type = 15, text_data = NULL WHERE _id = {deleted};");

        var plan = Plan();

        Assert.Equal(0, plan.Exit);
        Assert.EndsWith("1", Line(plan.Out, "text or caption edited since"));
        Assert.EndsWith("1", Line(plan.Out, "deleted for everyone since"));
        Assert.Equal("A newer text", NewerMessagesReader.Read(NewerPath).Messages[0].Text);
    }

    [Fact]
    public void A_low_quality_copy_link_is_recorded_on_the_smaller_copy()
    {
        SharedPast();
        var small = Bytes("small");
        var big = Bytes("the full copy, which is larger");
        var parent = Media("08:00:10", true, "IMG-low.jpg", small, HashOf(small), Next);
        var child = Media("08:00:20", true, "IMG-full.jpg", big, HashOf(big), Next);
        var other = Media("08:05:00", true, "IMG-elsewhere.jpg", big, HashOf(big), Next, size: 999);

        _d.Msgstore(
            $"""
            CREATE TABLE message_association (parent_message_row_id INTEGER, child_message_row_id INTEGER, association_type INTEGER);
            INSERT INTO message_association VALUES ({parent}, {child}, 12);
            INSERT INTO message_association VALUES ({other}, {child}, 12);
            INSERT INTO message_association VALUES ({parent}, {other}, 2);
            """);

        var plan = Plan();
        Assert.True(plan.Exit == 0, plan.Err);
        Assert.EndsWith("1", Line(plan.Out, "low-quality copy links recorded"));
        Assert.EndsWith("1", Line(plan.Out, NewerPlanner.LinkMinute));

        Assert.Equal(0, Apply(plan.Manifest, plan.Sha).Exit);

        var rows = NewerMessagesReader.Read(NewerPath).Messages;
        Assert.Equal(rows[1].Attachment!.NewerAttachmentID, rows[0].Attachment!.LowQualityCopyOfAttachmentID);
        Assert.Equal(rows[1].KeyID, rows[0].Attachment!.LowQualityCopyOfKeyID);
        Assert.Null(rows[1].Attachment!.LowQualityCopyOfAttachmentID);
        Assert.Null(rows[2].Attachment!.LowQualityCopyOfAttachmentID);
    }

    [Fact]
    public void Forwarded_facts_come_from_message_forwarded()
    {
        SharedPast();
        var forwarded = Text("09:20:00", false, "Forwarded text");
        Text("09:21:00", false, "Not forwarded");
        _d.Msgstore(
            $"""
            CREATE TABLE message_forwarded (message_row_id INTEGER PRIMARY KEY, forward_score INTEGER, forward_origin INTEGER);
            INSERT INTO message_forwarded VALUES ({forwarded}, 6, 0);
            """);

        var rows = PlanAndApply().Messages;

        Assert.Equal((true, (int?)6), (rows[0].IsForwarded, rows[0].ForwardScore));
        Assert.Equal((false, (int?)null), (rows[1].IsForwarded, rows[1].ForwardScore));
    }

    [Fact]
    public void Without_a_forwarding_record_forwarded_is_not_known()
    {
        SharedPast();
        Text("09:20:00", false, "A newer text");

        Assert.Null(PlanAndApply().Messages[0].IsForwarded);
    }

    [Fact]
    public void Local_time_is_Africa_Johannesburg()
    {
        SharedPast();
        var id = Text("09:20:00", false, "Late evening UTC");
        var utc = new DateTimeOffset(2026, 3, 2, 22, 30, 15, TimeSpan.Zero).ToUnixTimeMilliseconds();
        _d.Msgstore($"UPDATE message SET timestamp = {utc} WHERE _id = {id};");

        var row = PlanAndApply().Messages.Single();

        Assert.Equal(utc, row.TimestampUtcMs);
        Assert.Equal("2026-03-03T00:30:15", row.DateTimeLocal);
    }

    [Fact]
    public void The_written_rule_agrees_with_the_system_zone_where_there_is_one()
    {
        TimeZoneInfo zone;

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "South Africa Standard Time" : NewerMessagesTimeZone.Name);
        }
        catch (TimeZoneNotFoundException)
        {
            return; // No system data to compare with; the written rule stands.
        }

        for (var year = 1950; year <= 2030; year += 4)
        {
            foreach (var month in new[] { 1, 4, 7, 10 })
            {
                var instant = new DateTimeOffset(year, month, 15, 23, 45, 0, TimeSpan.Zero);
                Assert.Equal(TimeZoneInfo.ConvertTime(instant, zone).DateTime, NewerMessagesTimeZone.ToLocal(instant.ToUnixTimeMilliseconds()));
            }
        }
    }

    [Fact]
    public void A_message_after_the_hand_off_without_a_key_id_refuses()
    {
        SharedPast();
        var id = Text("09:20:00", false, "No key");
        _d.Msgstore($"UPDATE message SET key_id = NULL WHERE _id = {id};");

        var plan = Plan();

        Assert.Equal(2, plan.Exit);
        Assert.Contains("1 no usable key_id", plan.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_refuses_a_missing_media_root_without_printing_its_path()
    {
        SharedPast();
        _d.CloseAll();

        var (exit, _, stderr) = Scenario.Cli(
        [
            "newer", "plan", "--msgstore", _d.MsgstorePath, "--workspace", _d.WorkspacePath, "--conversation", "1",
            "--chat", SyntheticData.ChatID.ToString(CultureInfo.InvariantCulture), "--local-participant", "1",
            "--media-root", Path.Combine(_d.DirectoryPath, "absent"), "--manifest-out", ManifestPath(99),
        ]);

        Assert.Equal(2, exit);
        Assert.Contains("There is no folder at the --media-root path.", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(_d.DirectoryPath, stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_needs_the_backup_flag_and_takes_no_pairing_parameters()
    {
        var (exit, _, stderr) = Scenario.Cli(
        [
            "newer", "apply", "--msgstore", "m", "--workspace", "w", "--media-root", "r", "--manifest", "x",
            "--manifest-sha256", new string('a', 64),
        ]);

        Assert.Equal(2, exit);
        Assert.Contains("--i-have-a-backup is required", stderr, StringComparison.Ordinal);

        (exit, _, stderr) = Scenario.Cli(
        [
            "newer", "apply", "--msgstore", "m", "--workspace", "w", "--media-root", "r", "--manifest", "x",
            "--manifest-sha256", new string('a', 64), "--i-have-a-backup", "--chat", "7",
        ]);

        Assert.Equal(2, exit);
        Assert.Contains("from the manifest", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Media/WhatsApp Images/a.jpg", "Media/WhatsApp Images/a.jpg")]
    [InlineData("Media\\WhatsApp Images\\a.jpg", "Media/WhatsApp Images/a.jpg")]
    [InlineData("/storage/emulated/0/WhatsApp/Media/a.jpg", null)]
    [InlineData("C:/x/a.jpg", null)]
    [InlineData("Media/../../a.jpg", null)]
    [InlineData("Media//a.jpg", null)]
    [InlineData("", null)]
    public void Only_a_plain_relative_file_path_is_used(string input, string? expected) =>
        Assert.Equal(expected, NewerPlanner.UsableRelativePath(input));
}
