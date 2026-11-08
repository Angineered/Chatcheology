using System.Text;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport.Tests;

public sealed class ApplyTests
{
    private const string EventColumns =
        "AttachmentID, EventType, MediaAssetID, IndependentBasisType, IndependentBasisDescription, " +
        "RevocationReason, SupersedesEventID, SourceRunID, ActorLabel";

    [Fact]
    public void Apply_confirms_every_row_through_the_public_service_with_the_stated_basis()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;
        var tag = sha[..12];

        var r = s.Apply(sha);

        Assert.Equal(Scenario.ExpectedRows, r.Done);
        Assert.Equal(0, r.AlreadyDone);
        Assert.Equal(0, r.Other.Total);
        Assert.True(r.Reconciled);
        Assert.False(r.Stopped);

        var events = s.Data.Rows($"SELECT {EventColumns} FROM AttachmentResolutionEvent WHERE ActorLabel = 'Angie (msgstore import)' ORDER BY AttachmentID;");
        Assert.Equal(Scenario.ExpectedRows, events.Count);

        var both = $"{s.Both},Confirm,{s.BothAsset.ID},ExternalProvenanceOrGroundTruth," +
                   $"WhatsApp msgstore record (message key_id SYNTHKEYBOTH), file matched by hash and name+size; import batch {tag}," +
                   "<null>,<null>,<null>,Angie (msgstore import)";
        Assert.Contains(both, events);

        var hash = $"{s.HashOnly},Confirm,{s.HashAsset.ID},ExternalProvenanceOrGroundTruth," +
                   $"WhatsApp msgstore record (message key_id SYNTHKEYHASH), file matched by hash only; import batch {tag}," +
                   "<null>,<null>,<null>,Angie (msgstore import)";
        Assert.Contains(hash, events);

        foreach (var (attachment, asset, _) in s.ExpectedManifest())
        {
            Assert.Equal($"Resolved,{asset}", s.Data.Rows($"SELECT ResolutionStatus, ResolvedMediaAssetID FROM Attachment WHERE AttachmentID = {attachment};").Single());
        }

        // Nothing outside the manifest moved.
        Assert.Equal(Scenario.ExpectedRows + 1, s.Data.Count("SELECT COUNT(*) FROM Attachment WHERE ResolutionStatus = 'Resolved';"));
    }

    [Fact]
    public void Apply_cli_prints_counts_that_reconcile()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;

        var (exit, stdout, stderr) = Scenario.Cli(s.ApplyArgs(sha));

        Assert.Equal(0, exit);
        Assert.Equal("", stderr);
        Assert.Matches(@"confirmed\s+7", stdout);
        Assert.Matches(@"reconciliation against manifest rows\s+OK", stdout);
        Assert.DoesNotContain("SYNTHKEY", stdout);
        Assert.DoesNotContain(s.Data.DirectoryPath, stdout);
    }

    [Fact]
    public void Apply_refuses_a_tampered_manifest_and_writes_nothing()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;

        var bytes = File.ReadAllBytes(s.ManifestPath);
        var text = Encoding.UTF8.GetString(bytes).Replace("hash-only", "hash-and-name-size");
        File.WriteAllText(s.ManifestPath, text, new UTF8Encoding(false));

        var before = PlanTests.Sha(s.Data.WorkspacePath);
        var (exit, stdout, stderr) = Scenario.Cli(s.ApplyArgs(sha));

        Assert.Equal(2, exit);
        Assert.Equal("", stdout);
        Assert.Contains("SHA-256 does not match", stderr);
        Assert.Equal(before, PlanTests.Sha(s.Data.WorkspacePath));
    }

    [Fact]
    public void Apply_refuses_a_wrong_digest_for_an_untouched_manifest()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;
        var wrong = (sha[0] == '0' ? "1" : "0") + sha[1..];

        var (exit, _, stderr) = Scenario.Cli(s.ApplyArgs(wrong));

        Assert.Equal(2, exit);
        Assert.Contains("SHA-256 does not match", stderr);
        Assert.Equal(1, s.Data.Count("SELECT COUNT(*) FROM AttachmentResolutionEvent;"));
    }

    [Fact]
    public void Apply_requires_the_backup_flag()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;

        var (exit, _, stderr) = Scenario.Cli(s.ApplyArgs(sha, backup: false));

        Assert.Equal(2, exit);
        Assert.Contains("--i-have-a-backup", stderr);
        Assert.Equal(1, s.Data.Count("SELECT COUNT(*) FROM AttachmentResolutionEvent;"));
    }

    [Fact]
    public void Apply_recheck_skips_rows_whose_pairing_changed_since_the_plan()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;

        // Swap the order of the first two busy-minute records: those two attachments now pair to
        // each other's record and asset.
        s.Data.Msgstore(
            """
            UPDATE message SET sort_id = CASE sort_id WHEN 1000 THEN 2000 WHEN 2000 THEN 1000 ELSE sort_id END
            WHERE sort_id IN (1000, 2000);
            """);
        s.Data.CloseAll();

        var r = s.Apply(sha);

        Assert.Equal(2, r.Other[Reasons.Changed]);
        Assert.Equal(Scenario.ExpectedRows - 2, r.Done);
        Assert.True(r.Reconciled);
        Assert.Equal("Unresolved", s.Data.Rows($"SELECT ResolutionStatus FROM Attachment WHERE AttachmentID = {s.Busy[0]};").Single());
        Assert.Equal("Unresolved", s.Data.Rows($"SELECT ResolutionStatus FROM Attachment WHERE AttachmentID = {s.Busy[1]};").Single());
    }

    [Fact]
    public void Apply_recheck_skips_a_row_that_is_no_longer_eligible()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;

        // The hash disappears from the record: the attachment is now name+size only.
        s.Data.Msgstore("UPDATE message_media SET file_hash = NULL WHERE message_row_id = (SELECT _id FROM message WHERE key_id = 'SYNTHKEYBOTH' AND chat_row_id = 7);");
        s.Data.CloseAll();

        var r = s.Apply(sha);

        Assert.Equal(1, r.Other[Reasons.NoLongerEligible]);
        Assert.Equal(Scenario.ExpectedRows - 1, r.Done);
        Assert.True(r.Reconciled);
    }

    [Fact]
    public void Apply_recheck_leaves_an_attachment_resolved_in_the_meantime_alone()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;

        var manual = s.Data.ConfirmByHand(s.Both, s.BothAsset.ID);
        s.Data.CloseAll();

        var r = s.Apply(sha);

        Assert.Equal(1, r.Other[Reasons.ResolvedOtherwise]);
        Assert.Equal(0, r.AlreadyDone);
        Assert.Equal(Scenario.ExpectedRows - 1, r.Done);
        Assert.True(r.Reconciled);

        var history = s.Data.Rows($"SELECT AttachmentResolutionEventID FROM AttachmentResolutionEvent WHERE AttachmentID = {s.Both};");
        Assert.Equal([manual.ToString(System.Globalization.CultureInfo.InvariantCulture)], history);
    }

    [Fact]
    public void Apply_recheck_skips_a_row_whose_asset_was_disproven_after_the_plan()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;

        s.Data.Disprove(s.Busy[0], s.BusyAssets[0].ID);
        s.Data.CloseAll();

        var r = s.Apply(sha);

        Assert.Equal(1, r.Other[Reasons.SkipDisproven]);
        Assert.Equal(Scenario.ExpectedRows - 1, r.Done);
        Assert.True(r.Reconciled);
        Assert.Equal("Unresolved", s.Data.Rows($"SELECT ResolutionStatus FROM Attachment WHERE AttachmentID = {s.Busy[0]};").Single());
    }

    [Fact]
    public void Apply_stops_cleanly_when_the_workspace_turns_busy_while_reading_a_row()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;

        SqliteConnection? blocker = null;
        WriteResult r;

        // Before the third row's state is read, another connection takes an EXCLUSIVE lock, so the
        // read itself gets SQLITE_BUSY (after the provider's busy timeout).
        RowLoop.BeforeRowForTests = index =>
        {
            if (index != 2)
            {
                return;
            }

            blocker = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = s.Data.WorkspacePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            blocker.Open();

            using var command = blocker.CreateCommand();
            command.CommandText = "BEGIN EXCLUSIVE;";
            command.ExecuteNonQuery();
        };

        try
        {
            r = s.Apply(sha);
        }
        finally
        {
            RowLoop.BeforeRowForTests = null;

            if (blocker is not null)
            {
                using (var command = blocker.CreateCommand())
                {
                    command.CommandText = "ROLLBACK;";
                    command.ExecuteNonQuery();
                }

                blocker.Dispose();
            }
        }

        Assert.True(r.Stopped);
        Assert.Equal(2, r.Done);
        Assert.Equal(Scenario.ExpectedRows - 2, r.Other[Reasons.NotAttempted]);
        Assert.True(r.Reconciled);
        Assert.Contains("STOPPED", WriteReport.Of(r));
        Assert.Equal(3, s.Data.Count("SELECT COUNT(*) FROM AttachmentResolutionEvent;"));
    }

    [Fact]
    public void Apply_rerun_is_a_no_op()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;

        s.Apply(sha);
        var eventsAfterFirst = s.Data.Rows("SELECT * FROM AttachmentResolutionEvent ORDER BY AttachmentResolutionEventID;");
        var workspaceAfterFirst = s.Data.DumpExcept();

        var second = s.Apply(sha);

        Assert.Equal(0, second.Done);
        Assert.Equal(Scenario.ExpectedRows, second.AlreadyDone);
        Assert.Equal(0, second.Other.Total);
        Assert.True(second.Reconciled);
        Assert.Equal(eventsAfterFirst, s.Data.Rows("SELECT * FROM AttachmentResolutionEvent ORDER BY AttachmentResolutionEventID;"));
        Assert.Equal(workspaceAfterFirst, s.Data.DumpExcept());
    }

    [Fact]
    public void Apply_writes_only_the_resolution_history_and_the_attachment_projection()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;

        var others = s.Data.DumpExcept("AttachmentResolutionEvent", "Attachment");
        var attachmentFacts = s.Data.Rows("SELECT AttachmentID, MessageID, Ordinal FROM Attachment ORDER BY AttachmentID;");
        var priorEvents = s.Data.Rows("SELECT * FROM AttachmentResolutionEvent ORDER BY AttachmentResolutionEventID;");
        var msgstore = PlanTests.Sha(s.Data.MsgstorePath);

        s.Apply(sha);

        Assert.Equal(others, s.Data.DumpExcept("AttachmentResolutionEvent", "Attachment"));
        Assert.Equal(attachmentFacts, s.Data.Rows("SELECT AttachmentID, MessageID, Ordinal FROM Attachment ORDER BY AttachmentID;"));
        Assert.Equal(priorEvents, s.Data.Rows("SELECT * FROM AttachmentResolutionEvent ORDER BY AttachmentResolutionEventID;").Take(priorEvents.Count));
        Assert.Equal(msgstore, PlanTests.Sha(s.Data.MsgstorePath));
        Assert.False(File.Exists(s.Data.MsgstorePath + "-journal"));
        Assert.False(File.Exists(s.Data.MsgstorePath + "-wal"));
    }

    [Fact]
    public void Apply_refuses_up_front_when_the_workspace_is_claimed()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;

        int exit;
        string stderr;

        using (WorkspaceExclusiveLock.Acquire(s.Data.WorkspacePath))
        {
            (exit, _, stderr) = Scenario.Cli(s.ApplyArgs(sha));
        }

        Assert.Equal(2, exit);
        Assert.Contains("claimed by another operation", stderr);
        Assert.DoesNotContain(s.Data.DirectoryPath, stderr);
        Assert.Equal(1, s.Data.Count("SELECT COUNT(*) FROM AttachmentResolutionEvent;"));
    }

    [Fact]
    public void Apply_stops_on_the_first_claim_failure_and_still_reconciles()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;

        WriteResult r;

        // A competing SQLite writer holding the reservation: the claim file is free, so the up-front
        // probe passes, and the first Confirm is refused with SQLITE_BUSY after its busy timeout.
        using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = s.Data.WorkspacePath,
                   Mode = SqliteOpenMode.ReadWrite,
                   Pooling = false,
               }.ToString()))
        {
            writer.Open();
            using var transaction = writer.BeginTransaction(deferred: false);

            r = s.Apply(sha);

            transaction.Rollback();
        }

        Assert.True(r.Stopped);
        Assert.Equal(0, r.Done);
        Assert.Equal(Scenario.ExpectedRows, r.Other[Reasons.NotAttempted]);
        Assert.True(r.Reconciled);
        Assert.Equal(1, s.Data.Count("SELECT COUNT(*) FROM AttachmentResolutionEvent;"));
    }
}

public sealed class UndoTests
{
    [Fact]
    public void Undo_revokes_only_this_batchs_current_confirmations()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;
        var tag = sha[..12];

        s.Apply(sha);

        // Replaced since: the batch confirmation of HashOnly is superseded by a replacement.
        var hashEvent = s.Data.Count($"SELECT MAX(AttachmentResolutionEventID) FROM AttachmentResolutionEvent WHERE AttachmentID = {s.HashOnly};");
        s.Data.CloseAll();
        new Phase7ResolutionService().ConfirmReplacement(new Phase7ConfirmReplacementRequest
        {
            DatabasePath = s.Data.WorkspacePath,
            AttachmentID = s.HashOnly,
            MediaAssetID = s.ConflictNameAsset.ID,
            SupersedesEventID = hashEvent,
            IndependentBasisType = Phase7IndependentBasisType.HumanContentOrContextInspection,
            IndependentBasisDescription = "Replaced by eye (synthetic).",
        });

        // Revoked by hand, then confirmed by hand to the same asset: a manual confirmation.
        var bothEvent = s.Data.Count($"SELECT MAX(AttachmentResolutionEventID) FROM AttachmentResolutionEvent WHERE AttachmentID = {s.Both};");
        s.Data.CloseAll();
        new Phase7ResolutionService().Revoke(new Phase7RevokeRequest
        {
            DatabasePath = s.Data.WorkspacePath,
            AttachmentID = s.Both,
            SupersedesEventID = bothEvent,
            RevocationReason = "Withdrawn by hand (synthetic).",
        });
        s.Data.ConfirmByHand(s.Both, s.BothAsset.ID);
        s.Data.CloseAll();

        var untouched = s.Data.Rows(
            $"SELECT * FROM AttachmentResolutionEvent WHERE AttachmentID IN ({s.Both}, {s.HashOnly}, {s.Resolved}) ORDER BY AttachmentResolutionEventID;");

        var r = s.Undo(sha);

        Assert.Equal(Scenario.ExpectedRows - 2, r.Done);
        Assert.Equal(2, r.Other[Reasons.NotThisBatch]);
        Assert.True(r.Reconciled);

        // The manual, replaced and hand-resolved attachments kept their histories and assets.
        Assert.Equal(untouched, s.Data.Rows(
            $"SELECT * FROM AttachmentResolutionEvent WHERE AttachmentID IN ({s.Both}, {s.HashOnly}, {s.Resolved}) ORDER BY AttachmentResolutionEventID;"));
        Assert.Equal($"Resolved,{s.BothAsset.ID}", s.Data.Rows($"SELECT ResolutionStatus, ResolvedMediaAssetID FROM Attachment WHERE AttachmentID = {s.Both};").Single());
        Assert.Equal($"Resolved,{s.ConflictNameAsset.ID}", s.Data.Rows($"SELECT ResolutionStatus, ResolvedMediaAssetID FROM Attachment WHERE AttachmentID = {s.HashOnly};").Single());

        // Every other batch attachment is back to unresolved, by a revocation naming the batch.
        foreach (var attachment in s.Busy.Concat(s.Direction))
        {
            Assert.Equal("Unresolved,<null>", s.Data.Rows($"SELECT ResolutionStatus, ResolvedMediaAssetID FROM Attachment WHERE AttachmentID = {attachment};").Single());
            Assert.Equal(
                $"Revoke,msgstore import batch {tag} withdrawn,Angie (msgstore import)",
                s.Data.Rows($"SELECT EventType, RevocationReason, ActorLabel FROM AttachmentResolutionEvent WHERE AttachmentID = {attachment} ORDER BY AttachmentResolutionEventID DESC LIMIT 1;").Single());
        }
    }

    [Fact]
    public void Undo_rerun_revokes_nothing_more()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;

        s.Apply(sha);
        s.Undo(sha);
        var events = s.Data.Count("SELECT COUNT(*) FROM AttachmentResolutionEvent;");

        var again = s.Undo(sha);

        Assert.Equal(0, again.Done);
        Assert.Equal(Scenario.ExpectedRows, again.Other[Reasons.NotResolved]);
        Assert.True(again.Reconciled);
        Assert.Equal(events, s.Data.Count("SELECT COUNT(*) FROM AttachmentResolutionEvent;"));
    }

    [Fact]
    public void Undo_refuses_a_tampered_manifest()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;

        s.Apply(sha);
        File.AppendAllText(s.ManifestPath, "\n");

        var (exit, _, stderr) = Scenario.Cli(s.UndoArgs(sha));

        Assert.Equal(2, exit);
        Assert.Contains("SHA-256 does not match", stderr);
        Assert.Equal(0, s.Data.Count("SELECT COUNT(*) FROM AttachmentResolutionEvent WHERE EventType = 'Revoke';"));
    }

    [Fact]
    public void Undo_writes_only_the_resolution_history_and_the_attachment_projection()
    {
        using var s = new Scenario();
        var sha = s.Plan().Sha256;

        s.Apply(sha);
        var others = s.Data.DumpExcept("AttachmentResolutionEvent", "Attachment");

        var (exit, stdout, _) = Scenario.Cli(s.UndoArgs(sha));

        Assert.Equal(0, exit);
        Assert.Matches(@"revoked\s+7", stdout);
        Assert.Equal(others, s.Data.DumpExcept("AttachmentResolutionEvent", "Attachment"));
    }
}
