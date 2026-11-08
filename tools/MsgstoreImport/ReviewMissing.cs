using System.Globalization;
using System.Text;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport;

/// <summary>One attachment the review-missing manifest proposes to record as reviewed, missing.</summary>
internal sealed record MissingRow(long AttachmentID, long SourceRunID, string Reason);

/// <summary>
/// The CH-13 review-missing manifest. Same header lines as a format 2 import manifest, with the rule
/// <c>review-missing</c> and its own three columns. Tab-separated UTF-8 without a BOM, LF endings,
/// rows in AttachmentID order; the SHA-256 is over the bytes.
/// </summary>
internal sealed class MissingManifest
{
    internal const string Token = "review-missing";
    internal const string Header = "AttachmentID\tSourceRunID\tReason";

    internal required PairingParameters Parameters { get; init; }
    internal required IReadOnlyList<MissingRow> Rows { get; init; }

    /// <summary>True when the file at <paramref name="path"/> names the review-missing rule. Reads nothing else.</summary>
    internal static bool IsOne(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        var bytes = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512));
        var lines = text.Split('\n');

        return lines.Length >= 3
               && lines[0] == $"{Manifest.Magic}\t{Manifest.DaySequenceVersion}"
               && lines[2] == $"rule\t{Token}";
    }

    internal byte[] ToBytes()
    {
        var text = new StringBuilder();

        void Line(string line) => text.Append(line).Append('\n');

        Line($"{Manifest.Magic}\t{Manifest.DaySequenceVersion}");
        Line("# PRIVATE: real identifiers. Keep outside the repository. Any edit invalidates its SHA-256.");
        Line($"rule\t{Token}");
        Line($"conversation\t{Text.Id(Parameters.ConversationID)}");
        Line($"chat\t{Text.Id(Parameters.ChatID)}");
        Line($"utc-offset\t{Options.FormatOffset(Parameters.UtcOffset)}");
        Line($"local-participant\t{Text.Id(Parameters.LocalParticipantID)}");
        Line($"rows\t{Rows.Count.ToString(CultureInfo.InvariantCulture)}");
        Line(Header);

        foreach (var row in Rows)
        {
            Line($"{Text.Id(row.AttachmentID)}\t{Text.Id(row.SourceRunID)}\t{row.Reason}");
        }

        return new UTF8Encoding(false).GetBytes(text.ToString());
    }

    /// <exception cref="FormatException">The bytes are not a well-formed review-missing manifest.</exception>
    internal static MissingManifest Parse(byte[] bytes)
    {
        string content;

        try
        {
            content = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new FormatException("The manifest is not valid UTF-8.");
        }

        if (content.Contains('\r'))
        {
            throw new FormatException("The manifest has CR characters; it was not written by this tool.");
        }

        var lines = content.Split('\n');

        if (lines.Length < 10 || lines[^1].Length != 0)
        {
            throw new FormatException("The manifest is truncated.");
        }

        if (lines[0] != $"{Manifest.Magic}\t{Manifest.DaySequenceVersion}" || !lines[1].StartsWith('#')
            || lines[2] != $"rule\t{Token}")
        {
            throw new FormatException("The manifest's first lines are not a review-missing manifest header.");
        }

        long Value(int index, string key)
        {
            var parts = lines[index].Split('\t');

            return parts.Length == 2 && parts[0] == key
                   && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new FormatException($"The manifest's '{key}' line is malformed.");
        }

        var conversation = Value(3, "conversation");
        var chat = Value(4, "chat");
        var offsetParts = lines[5].Split('\t');

        if (offsetParts.Length != 2 || offsetParts[0] != "utc-offset" || !Options.TryParseOffset(offsetParts[1], out var offset))
        {
            throw new FormatException("The manifest's 'utc-offset' line is malformed.");
        }

        var local = Value(6, "local-participant");
        var count = Value(7, "rows");

        if (lines[8] != Header)
        {
            throw new FormatException("The manifest's column header is not the expected one.");
        }

        var body = lines[9..^1];

        if (body.Length != count)
        {
            throw new FormatException("The manifest's row count does not match its 'rows' line.");
        }

        var rows = new List<MissingRow>(body.Length);
        var seen = new HashSet<long>();

        foreach (var line in body)
        {
            var parts = line.Split('\t');

            if (parts.Length != 3
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var attachment)
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var run)
                || parts[2].Length == 0 || parts[2].Trim() != parts[2] || parts[2].Any(char.IsControl))
            {
                throw new FormatException("A manifest row is malformed.");
            }

            if (!seen.Add(attachment))
            {
                throw new FormatException("The manifest names an attachment twice.");
            }

            rows.Add(new MissingRow(attachment, run, parts[2]));
        }

        return new MissingManifest { Parameters = new PairingParameters(conversation, chat, offset, local), Rows = rows };
    }
}

internal sealed class MissingPlanResult
{
    internal required int Unresolved { get; init; }
    internal required int AlreadyReviewed { get; init; }
    internal required IReadOnlyList<MissingRow> Rows { get; init; }
    internal required string Sha256 { get; init; }
}

/// <summary>
/// CH-13 <c>review-missing</c>: records every unresolved, unreviewed attachment of the conversation as
/// <see cref="Phase7ReviewOutcome.ReviewedInsufficientEvidence"/> through the public
/// <see cref="Phase7ReviewService"/>. A review never resolves anything and never blocks a later Confirm.
/// </summary>
/// <remarks>
/// Reviews are append-only and Phase 7 has no way to withdraw one, so there is no undo-batch for this
/// rule: the backup taken before apply is the undo.
/// </remarks>
internal static class MissingReview
{
    internal const string ActorLabel = "Angie (accepted missing list)";

    internal const string NoFileInAnyCopy = "no file in any copy";
    internal const string WhatsAppKeptNoFile = "WhatsApp kept no file";
    internal const string SameNameDifferentSize = "same name different size";
    internal const string DatedAfterMessage = "dated after message";

    internal const string SkipResolved = "skipped: attachment resolved since plan";
    internal const string SkipReviewed = "skipped: already reviewed since plan (not by this batch)";
    internal const string SkipRunChanged = "skipped: evidence run changed since plan";
    internal const string SkipReasonChanged = "skipped: unresolved reason changed since plan";
    internal const string RecordRefused = "skipped: Record refused the row";

    /// <summary>The reason category for a list-unresolved reason: the four named ones, or "other: ...".</summary>
    internal static string Category(string unresolvedReason) => unresolvedReason switch
    {
        Unresolved.NotRecovered => NoFileInAnyCopy,
        Unresolved.NoFile => WhatsAppKeptNoFile,
        Unresolved.SizeDiffers => SameNameDifferentSize,
        Unresolved.DatedAfter => DatedAfterMessage,
        _ => "other: " + unresolvedReason.Replace('\t', ' '),
    };

    internal static string BatchSuffix(string tag) => "; batch " + tag;

    internal static string Notes(MissingRow row, string tag, DateOnly date) =>
        $"Accepted as missing {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} (CH-13): {row.Reason}{BatchSuffix(tag)}";

    /// <summary>Test seam for the date written into the notes. Never set outside tests.</summary>
    internal static Func<DateOnly>? TodayForTests { get; set; }

    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static MissingPlanResult Plan(Options options)
    {
        if (Planner.ManifestOutRefusal(options.ManifestOut!) is { } refusal)
        {
            throw new RefusalException(refusal);
        }

        var parameters = options.Parameters!;
        var unresolved = Unresolved.ComputeRows(options.MsgstorePath!, options.WorkspacePath, parameters);
        var states = ReadStates(options.WorkspacePath, unresolved.Select(r => r.Attachment.AttachmentID).ToList());

        var targets = unresolved
            .Where(r => states[r.Attachment.AttachmentID] is { Status: Phase7ResolutionStatus.Unresolved, HasCurrentReviewedInsufficientEvidence: false })
            .OrderBy(r => r.Attachment.AttachmentID)
            .ToList();

        var alreadyReviewed = unresolved.Count(r => states[r.Attachment.AttachmentID].HasCurrentReviewedInsufficientEvidence);

        if (targets.Count != options.ExpectedCount)
        {
            throw new RefusalException(
                $"{Text.Count(targets.Count)} unresolved, unreviewed attachments qualify; --expected-count is " +
                $"{Text.Count(options.ExpectedCount!.Value)}. Nothing was written.");
        }

        var noRun = targets.Count(r => states[r.Attachment.AttachmentID].EvidenceRunID is null);

        if (noRun > 0)
        {
            throw new RefusalException(
                $"{Text.Count(noRun)} of them have no completed Phase 7 run, and a review must name one. " +
                "Nothing was written.");
        }

        var rows = targets
            .Select(r => new MissingRow(r.Attachment.AttachmentID, states[r.Attachment.AttachmentID].EvidenceRunID!.Value, Category(r.Reason)))
            .ToList();

        var bytes = new MissingManifest { Parameters = parameters, Rows = rows }.ToBytes();

        using (var stream = new FileStream(options.ManifestOut!, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
        }

        return new MissingPlanResult
        {
            Unresolved = unresolved.Count,
            AlreadyReviewed = alreadyReviewed,
            Rows = rows,
            Sha256 = Manifest.Sha256Hex(bytes),
        };
    }

    internal static string Report(MissingPlanResult r)
    {
        var text = new StringBuilder();

        text.AppendLine("# Msgstore import - plan review-missing");
        text.AppendLine();
        text.AppendLine("Read-only on both databases. Counts only; identifiers are in the private manifest.");
        text.AppendLine();
        text.AppendLine("```text");
        text.AppendLine(Text.Line("unresolved attachments in the conversation", r.Unresolved));
        text.AppendLine(Text.Line("  already reviewed (excluded)", r.AlreadyReviewed));
        text.AppendLine();
        text.AppendLine(Text.Line("manifest rows (to record as missing)", r.Rows.Count));

        foreach (var g in r.Rows.GroupBy(x => x.Reason).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            text.AppendLine(Text.Line("  " + g.Key, g.Count()));
        }

        text.AppendLine();
        text.AppendLine(Text.Line("rows + already reviewed = unresolved", r.Rows.Count + r.AlreadyReviewed));
        text.AppendLine(r.Rows.Count + r.AlreadyReviewed == r.Unresolved
            ? "reconciliation                                            OK"
            : "reconciliation                                            MISMATCH");
        text.AppendLine("```");
        text.AppendLine();
        text.AppendLine("manifest SHA-256: " + r.Sha256);
        text.AppendLine("batch tag:        " + Manifest.Tag(r.Sha256));

        return text.ToString();
    }

    /// <exception cref="RefusalException">Missing, tampered or malformed manifest, or unusable inputs. Nothing was written.</exception>
    internal static WriteResult Apply(Options options)
    {
        var (manifest, tag) = Load(options.ManifestPath!, options.ManifestSha256!);

        Inputs.RequireMsgstore(options.MsgstorePath!);
        Inputs.RequireWorkspace(options.WorkspacePath);
        Inputs.RequireUnclaimed(options.WorkspacePath);

        // Each row's reason is recomputed, never trusted from the manifest. (This also checks the schema version.)
        var reasons = Unresolved.ComputeRows(options.MsgstorePath!, options.WorkspacePath, manifest.Parameters)
            .ToDictionary(r => r.Attachment.AttachmentID, r => Category(r.Reason));

        var date = TodayForTests?.Invoke() ?? DateOnly.FromDateTime(DateTime.Now);
        var result = new WriteResult { Mode = "apply review-missing", Tag = tag, Rows = manifest.Rows.Count };
        var service = new Phase7ReviewService();

        for (var index = 0; index < manifest.Rows.Count; index++)
        {
            var row = manifest.Rows[index];

            if (!RowLoop.TryReadState(options.WorkspacePath, row.AttachmentID, index, result, out var state))
            {
                break;
            }

            if (!state.Exists)
            {
                result.Other.Add(Reasons.Missing);
                continue;
            }

            Phase7ReconstructionState now;
            bool doneByThisBatch;

            try
            {
                (now, doneByThisBatch) = ReadReviewState(options.WorkspacePath, row.AttachmentID, tag);
            }
            catch (Exception exception) when (RowLoop.StopReason(exception) is { } stop)
            {
                RowLoop.Stop(result, stop, index);
                break;
            }

            if (doneByThisBatch)
            {
                result.AlreadyDone++;
                continue;
            }

            if (state.Resolved || now.Status == Phase7ResolutionStatus.Resolved)
            {
                result.Other.Add(SkipResolved);
                continue;
            }

            if (now.EvidenceRunID != row.SourceRunID)
            {
                result.Other.Add(SkipRunChanged);
                continue;
            }

            if (now.HasCurrentReviewedInsufficientEvidence)
            {
                result.Other.Add(SkipReviewed);
                continue;
            }

            if (!reasons.TryGetValue(row.AttachmentID, out var reason) || reason != row.Reason)
            {
                result.Other.Add(SkipReasonChanged);
                continue;
            }

            try
            {
                service.Record(new Phase7ReviewRequest
                {
                    DatabasePath = options.WorkspacePath,
                    AttachmentID = row.AttachmentID,
                    SourceRunID = row.SourceRunID,
                    ReviewNotes = Notes(row, tag, date),
                    ActorLabel = ActorLabel,
                });

                result.Done++;
            }
            catch (Exception exception) when (RowLoop.StopReason(exception) is { } stop)
            {
                RowLoop.Stop(result, stop, index);
                break;
            }
            catch (InvalidOperationException)
            {
                result.Other.Add(RecordRefused);
            }
        }

        SqliteConnection.ClearAllPools();

        return result;
    }

    internal static string WriteReport(WriteResult r)
    {
        var text = new StringBuilder();

        text.AppendLine("# Msgstore import - apply review-missing");
        text.AppendLine();
        text.AppendLine("Counts only.");
        text.AppendLine();
        text.AppendLine("```text");
        text.AppendLine($"{"batch tag",-58}{r.Tag}");
        text.AppendLine(Text.Line("manifest rows", r.Rows));
        text.AppendLine(Text.Line("reviewed (recorded as missing)", r.Done));
        text.AppendLine(Text.Line("already done (this batch)", r.AlreadyDone));
        text.AppendLine(Text.Line("skipped", r.Other.Total));

        foreach (var (reason, count) in r.Other.Items)
        {
            text.AppendLine(Text.Line("  " + reason, count));
        }

        text.AppendLine();
        text.AppendLine(Text.Line("reviewed + already done + skipped", r.Done + r.AlreadyDone + r.Other.Total));
        text.AppendLine($"{"reconciliation against manifest rows",-58}{(r.Reconciled ? "OK" : "MISMATCH")}");
        text.AppendLine("```");

        if (r.Stopped)
        {
            text.AppendLine();
            text.AppendLine(r.StopReason == Reasons.WorkspaceGone
                ? "STOPPED: the workspace file was no longer found. Rows already written stay written."
                : "STOPPED: the workspace was claimed or busy (is the viewer open?). Rows already " +
                  "written stay written; rerun the same command once it is closed.");
        }

        return text.ToString();
    }

    internal const string UndoRefusal =
        "This is a review-missing manifest. Phase 7 reviews are append-only and have no revoke, so " +
        "undo-batch cannot withdraw them. Restore the workspace backup taken before apply instead. " +
        "Nothing was opened or written.";

    /// <exception cref="RefusalException">Missing, tampered or malformed. Nothing was opened.</exception>
    private static (MissingManifest Manifest, string Tag) Load(string path, string expectedSha256)
    {
        if (!File.Exists(path))
        {
            throw new RefusalException("There is no file at the --manifest path.");
        }

        var bytes = File.ReadAllBytes(path);
        var actual = Manifest.Sha256Hex(bytes);

        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new RefusalException(
                "The manifest's SHA-256 does not match --manifest-sha256. The manifest has changed since " +
                "it was approved, or the wrong digest was given. Nothing was opened or written.");
        }

        try
        {
            return (MissingManifest.Parse(bytes), Manifest.Tag(actual));
        }
        catch (FormatException exception)
        {
            throw new RefusalException("The manifest is malformed: " + exception.Message + " Nothing was written.");
        }
    }

    /// <summary>Reconstruction states through the public reader, on a read-only connection.</summary>
    private static Dictionary<long, Phase7ReconstructionState> ReadStates(string workspacePath, IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        try
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(workspacePath);

            return new Phase7ReconstructionReader().Read(connection, ids).ToDictionary(s => s.AttachmentID);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new RefusalException(
                "The workspace is busy: another program holds a lock on it (is the viewer open?). Nothing was written.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>
    /// One attachment's reconstruction state (public reader), and whether this batch already recorded
    /// its review. Phase 7 has no public reader for review notes, so that one fact is a read-only
    /// SELECT on <c>AttachmentReviewEvent</c>, matched on this batch's actor and notes suffix.
    /// </summary>
    private static (Phase7ReconstructionState State, bool DoneByThisBatch) ReadReviewState(
        string workspacePath, long attachmentID, string tag)
    {
        using var connection = WorkspaceDatabase.OpenReadOnlyConnection(workspacePath);

        var state = new Phase7ReconstructionReader().Read(connection, attachmentID);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM AttachmentReviewEvent
            WHERE AttachmentID = $id AND ActorLabel = $actor
              AND ReviewNotes IS NOT NULL AND substr(ReviewNotes, -length($suffix)) = $suffix;
            """;
        command.Parameters.AddWithValue("$id", attachmentID);
        command.Parameters.AddWithValue("$actor", ActorLabel);
        command.Parameters.AddWithValue("$suffix", BatchSuffix(tag));

        var done = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;

        return (state, done);
    }
}
