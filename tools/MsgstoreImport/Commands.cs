using System.Text;
using Chatcheology.Data.Phase7;

namespace MsgstoreImport;

/// <summary>The labels every count is printed under. Aggregates only; never an identifier.</summary>
internal static class Reasons
{
    internal static string Exclusion(Eligibility eligibility) => eligibility switch
    {
        Eligibility.NoTime => "attachment message time unreadable (not keyed)",
        Eligibility.MoreRecordsInKey => "not paired: key has MORE msgstore records",
        Eligibility.FewerRecordsInKey => "not paired: key has FEWER msgstore records",
        Eligibility.PairedNoAsset => "paired, record matched no single asset",
        Eligibility.NoCompletedRun => "paired, attachment has no completed Phase 7 run",
        Eligibility.NotInCandidateSet => "paired, asset NOT in current candidate set",
        Eligibility.NameSizeOnly => "matched by name+size only (excluded by decision)",
        Eligibility.Conflict => "hash and name+size named DIFFERENT assets",
        Eligibility.Disproven => "asset disproven by Angie for this attachment",
        Eligibility.NoKeyID => "msgstore record has no usable key_id",
        _ => throw new ArgumentOutOfRangeException(nameof(eligibility)),
    };

    internal const string AlreadyResolved = "already resolved (left alone)";

    internal const string ResolvedOtherwise = "skipped: attachment resolved otherwise";
    internal const string Missing = "skipped: attachment no longer exists";
    internal const string Changed = "skipped: pairing or match changed since plan";
    internal const string NoLongerEligible = "skipped: no longer eligible";
    internal const string ConfirmRefused = "skipped: Confirm refused the row";
    internal const string SkipDisproven = "skipped: asset disproven by Angie since plan";
    internal const string DayNoLongerUsable = "skipped: day no longer lines up by message sequence";
    internal const string MarkGone = "skipped: no longer marked R in the review file";
    internal const string NotAttempted = "not attempted (stopped, workspace claimed or busy)";
    internal const string WorkspaceGone = "not attempted (stopped, workspace file not found)";

    internal const string NotResolved = "left alone: attachment not resolved";
    internal const string NotThisBatch = "left alone: current event is not this batch's confirmation";
    internal const string RevokeRefused = "left alone: Revoke refused the row";
}

/// <summary>Counts by reason, kept in first-seen order for stable printing.</summary>
internal sealed class Tally
{
    private readonly List<string> _order = [];
    private readonly Dictionary<string, int> _counts = [];

    internal void Add(string reason, int n = 1)
    {
        if (!_counts.ContainsKey(reason))
        {
            _order.Add(reason);
            _counts[reason] = 0;
        }

        _counts[reason] += n;
    }

    internal int this[string reason] => _counts.GetValueOrDefault(reason);

    internal int Total => _counts.Values.Sum();

    internal IEnumerable<(string Reason, int Count)> Items => _order.Select(r => (r, _counts[r]));
}

internal sealed class PlanResult
{
    internal required int Attachments { get; init; }
    internal required int HashAndNameSize { get; init; }
    internal required int HashOnly { get; init; }
    internal required Tally Excluded { get; init; }
    internal required Dictionary<MatchCategory, int> HeadlineByCategory { get; init; }
    internal required int Headline { get; init; }
    internal required int AssetsOnTwoOrMoreRows { get; init; }
    internal required string Sha256 { get; init; }
    internal int Rows => HashAndNameSize + HashOnly;
}

internal static class Planner
{
    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static PlanResult Plan(Options options)
    {
        if (ManifestOutRefusal(options.ManifestOut!) is { } refusal)
        {
            throw new RefusalException(refusal);
        }

        var parameters = options.Parameters!;
        var pairing = Inputs.ComputePairing(options.MsgstorePath!, options.WorkspacePath, parameters);

        var rows = new List<ManifestRow>();
        var excluded = new Tally();

        foreach (var assessment in pairing.Values)
        {
            if (assessment.Eligibility != Eligibility.Eligible)
            {
                excluded.Add(Reasons.Exclusion(assessment.Eligibility));
            }
            else if (assessment.Resolved)
            {
                excluded.Add(Reasons.AlreadyResolved);
            }
            else
            {
                rows.Add(new ManifestRow(
                    assessment.AttachmentID, assessment.Asset!.Value, assessment.MessageID!.Value,
                    assessment.KeyID!, assessment.Kind!.Value));
            }
        }

        var manifest = new Manifest { Parameters = parameters, Rows = rows };
        var bytes = manifest.ToBytes();

        // CreateNew: never overwrite, even if something appeared since the path was checked.
        using (var stream = new FileStream(options.ManifestOut!, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
        }

        var headline = pairing.Values.Where(a => a.InHeadline).ToList();

        return new PlanResult
        {
            Attachments = pairing.Count,
            HashAndNameSize = rows.Count(r => r.Kind == MatchKind.HashAndNameSize),
            HashOnly = rows.Count(r => r.Kind == MatchKind.HashOnly),
            Excluded = excluded,
            Headline = headline.Count,
            HeadlineByCategory = Enum.GetValues<MatchCategory>()
                .ToDictionary(c => c, c => headline.Count(a => a.Category == c)),
            AssetsOnTwoOrMoreRows = rows.GroupBy(r => r.MediaAssetID).Count(g => g.Count() >= 2),
            Sha256 = Manifest.Sha256Hex(bytes),
        };
    }

    internal static string Report(PlanResult r)
    {
        var text = new StringBuilder();

        text.AppendLine("# Msgstore import - plan");
        text.AppendLine();
        text.AppendLine("Read-only on both databases. Counts only; identifiers are in the private manifest.");
        text.AppendLine();
        text.AppendLine("```text");
        text.AppendLine(Text.Line("attachments in the conversation", r.Attachments));
        text.AppendLine();
        text.AppendLine(Text.Line("manifest rows", r.Rows));
        text.AppendLine(Text.Line("  matched by hash and name+size (same asset)", r.HashAndNameSize));
        text.AppendLine(Text.Line("  matched by hash only", r.HashOnly));
        text.AppendLine();
        text.AppendLine(Text.Line("excluded", r.Excluded.Total));

        foreach (var (reason, count) in r.Excluded.Items)
        {
            text.AppendLine(Text.Line("  " + reason, count));
        }

        text.AppendLine();
        text.AppendLine(Text.Line("rows + excluded = attachments", r.Rows + r.Excluded.Total));
        text.AppendLine(r.Rows + r.Excluded.Total == r.Attachments ? "reconciliation                                            OK" : "reconciliation                                            MISMATCH");
        text.AppendLine();
        text.AppendLine(Text.Line("census headline (for comparison)", r.Headline));
        text.AppendLine(Text.Line("  hash and name+size, same asset", r.HeadlineByCategory[MatchCategory.BothSameAsset]));
        text.AppendLine(Text.Line("  hash only", r.HeadlineByCategory[MatchCategory.HashOnly]));
        text.AppendLine(Text.Line("  name+size only", r.HeadlineByCategory[MatchCategory.NameSizeOnly]));
        text.AppendLine(Text.Line("  hash/name+size conflict", r.HeadlineByCategory[MatchCategory.Conflict]));
        text.AppendLine(Text.Line("assets named by 2+ manifest rows (informational)", r.AssetsOnTwoOrMoreRows));
        text.AppendLine("```");
        text.AppendLine();
        text.AppendLine("manifest SHA-256: " + r.Sha256);
        text.AppendLine("batch tag:        " + Manifest.Tag(r.Sha256));

        return text.ToString();
    }

    /// <summary>Why the manifest may not be written at <paramref name="path"/>, or null.</summary>
    /// <remarks>
    /// The census's sample-path rule: never inside the repository (found by walking up from this
    /// executable and the working directory to <c>Chatcheology.slnx</c> or <c>.git</c>), never over
    /// an existing file, and only into a folder that exists.
    /// </remarks>
    internal static string? ManifestOutRefusal(string path, string option = "--manifest-out")
    {
        string full;

        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"The {option} path is not a usable path.";
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (!File.Exists(Path.Combine(directory.FullName, "Chatcheology.slnx"))
                    && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
                {
                    continue;
                }

                var root = directory.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return $"The {option} path is inside the repository. This file holds real " +
                           "identifiers and must be written outside it.";
                }
            }
        }

        // Also walk up from the manifest's own location, so a repository (this one or any clone of
        // it) is caught wherever the tool happens to run from.
        for (var directory = Path.GetDirectoryName(full) is { } folder ? new DirectoryInfo(folder) : null;
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Chatcheology.slnx"))
                || Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return $"The {option} path is inside a repository. This file holds real " +
                       "identifiers and must be written outside it.";
            }
        }

        if (File.Exists(full) || Directory.Exists(full))
        {
            return $"Something already exists at the {option} path; it is never overwritten.";
        }

        if (Path.GetDirectoryName(full) is not { } parent || !Directory.Exists(parent))
        {
            return $"The folder for the {option} path does not exist.";
        }

        return null;
    }
}

/// <summary>Reading and verifying an approved manifest.</summary>
internal static class ApprovedManifest
{
    /// <exception cref="RefusalException">Missing, tampered or malformed. Nothing was opened.</exception>
    internal static (Manifest Manifest, string Tag) Load(string path, string expectedSha256)
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
            return (Manifest.Parse(bytes), Manifest.Tag(actual));
        }
        catch (FormatException exception)
        {
            throw new RefusalException("The manifest is malformed: " + exception.Message + " Nothing was written.");
        }
    }
}

internal sealed class WriteResult
{
    internal required string Mode { get; init; }
    internal required string Tag { get; init; }
    internal required int Rows { get; init; }
    internal int Done { get; set; }
    internal int AlreadyDone { get; set; }
    internal Tally Other { get; } = new();
    internal bool Stopped { get; set; }
    internal string? StopReason { get; set; }

    internal bool Reconciled => Done + AlreadyDone + Other.Total == Rows;
}

/// <summary>The per-row plumbing apply and undo-batch share: reading state, and stopping cleanly.</summary>
internal static class RowLoop
{
    /// <summary>Test seam: run before each row's state is read. Never set outside tests.</summary>
    internal static Action<int>? BeforeRowForTests { get; set; }

    /// <summary>
    /// Reads a row's state. On a busy/claimed or vanished workspace, stops the run with every
    /// remaining row counted as not attempted, so the counts still reconcile.
    /// </summary>
    internal static bool TryReadState(
        string workspacePath, long attachmentID, int index, WriteResult result, out Inputs.AttachmentState state)
    {
        try
        {
            BeforeRowForTests?.Invoke(index);
            state = Inputs.ReadState(workspacePath, attachmentID);

            return true;
        }
        catch (Exception exception) when (StopReason(exception) is { } stop)
        {
            Stop(result, stop, index);
            state = null!;

            return false;
        }
    }

    /// <summary>Why this failure must stop the run, or null when it is a per-row refusal.</summary>
    internal static string? StopReason(Exception exception) =>
        Inputs.IsClaimFailure(exception) ? Reasons.NotAttempted
        : exception is FileNotFoundException ? Reasons.WorkspaceGone
        : null;

    internal static void Stop(WriteResult result, string reason, int index)
    {
        result.Stopped = true;
        result.StopReason = reason;
        result.Other.Add(reason, result.Rows - index);
    }
}

internal static class Applier
{
    internal const string ActorLabel = "Angie (msgstore import)";

    internal static string Marker(string tag) => "import batch " + tag;

    internal static string Description(ManifestRow row, string tag, PairingRule rule = PairingRule.Minute) => rule switch
    {
        PairingRule.NameSizeReviewed =>
            $"Angie inspected the file and confirmed it (review list {row.ReviewList}); " +
            $"WhatsApp msgstore record (message key_id {row.KeyID}), " +
            $"paired by {(row.PairedBy == PairingRule.DaySequence ? "day message sequence" : "minute")}, " +
            $"file matched by name+size only; {Marker(tag)}",
        PairingRule.Documents =>
            $"WhatsApp msgstore record (message key_id {row.KeyID}), " +
            $"paired by {(row.PairedBy == PairingRule.DaySequence ? "day message sequence" : "minute")}, " +
            $"file matched by {Manifest.KindDescription(row.Kind)}, document (original name, no date check); {Marker(tag)}",
        PairingRule.OutsideCandidateSet =>
            $"WhatsApp msgstore record (message key_id {row.KeyID}), " +
            $"paired by {(row.PairedBy == PairingRule.DaySequence ? "day message sequence" : "minute")}, " +
            $"file matched by {Manifest.KindDescription(row.Kind)}, file outside the date candidate set " +
            $"(file dated {row.FileDate!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}); {Marker(tag)}",
        _ =>
            $"WhatsApp msgstore record (message key_id {row.KeyID}), " +
            (rule == PairingRule.DaySequence ? "paired by day message sequence, " : "") +
            $"file matched by {Manifest.KindDescription(row.Kind)}; {Marker(tag)}",
    };

    /// <summary>What apply re-checks a row against, whichever rule produced it.</summary>
    internal sealed record Recheck(
        bool Eligible, bool Disproven, bool DaySkipped, long? MessageID, string? KeyID, long? Asset, MatchKind? Kind,
        PairingRule? PairedBy = null, DateOnly? FileDate = null, long? SourceRunID = null);

    /// <summary>Recomputed from the msgstore and the workspace, never trusted from the manifest.</summary>
    private static Dictionary<long, Recheck> Recompute(Options options, Manifest manifest)
    {
        if (manifest.Rule == PairingRule.NameSizeReviewed)
        {
            return Inputs.ComputeNameSize(options.MsgstorePath!, options.WorkspacePath, manifest.Parameters)
                .Values.ToDictionary(
                    a => a.Slot.AttachmentID,
                    a => new Recheck(
                        a.Eligible, a.Disproven, a.DaySkipped, a.Slot.MessageID, a.Slot.KeyID, a.Slot.Asset,
                        a.Eligible ? MatchKind.NameSizeOnly : null, a.Slot.PairedBy));
        }

        if (manifest.Rule == PairingRule.Documents)
        {
            return Inputs.ComputeDocuments(options.MsgstorePath!, options.WorkspacePath, manifest.Parameters)
                .Values.ToDictionary(
                    a => a.Slot.AttachmentID,
                    a => new Recheck(
                        a.Eligible, a.Disproven, a.DaySkipped, a.Slot.MessageID, a.Slot.KeyID, a.Slot.Asset, a.Slot.Kind,
                        a.Slot.PairedBy));
        }

        if (manifest.Rule == PairingRule.OutsideCandidateSet)
        {
            return Inputs.ComputeOutside(options.MsgstorePath!, options.WorkspacePath, manifest.Parameters)
                .Values.ToDictionary(
                    a => a.AttachmentID,
                    a => new Recheck(
                        a.Eligibility == OutsideEligibility.Eligible, a.Eligibility == OutsideEligibility.Disproven,
                        a.Eligibility == OutsideEligibility.DayNotUsable, a.MessageID, a.KeyID, a.Asset, a.Kind,
                        a.PairedBy, a.FileDate));
        }

        if (manifest.Rule == PairingRule.DaySequence)
        {
            return Inputs.ComputeDaySequence(options.MsgstorePath!, options.WorkspacePath, manifest.Parameters)
                .ByAttachment.Values.ToDictionary(
                    a => a.AttachmentID,
                    a => new Recheck(
                        a.Eligibility == DayEligibility.Eligible, a.Eligibility == DayEligibility.Disproven,
                        a.Eligibility == DayEligibility.DaySkipped, a.MessageID, a.KeyID, a.Asset, a.Kind));
        }

        return Inputs.ComputePairing(options.MsgstorePath!, options.WorkspacePath, manifest.Parameters)
            .Values.ToDictionary(
                a => a.AttachmentID,
                a => new Recheck(
                    a.Eligibility == Eligibility.Eligible, a.Eligibility == Eligibility.Disproven, false,
                    a.MessageID, a.KeyID, a.Asset, a.Kind));
    }

    /// <summary>True when the event is a confirmation this batch wrote for this asset.</summary>
    internal static bool IsBatchConfirm(Phase7ResolutionHistoryEntry? entry, string tag) =>
        entry is { EventType: Phase7ResolutionEventType.Confirm, IndependentBasisDescription: { } description }
        && description.Contains(Marker(tag), StringComparison.Ordinal);

    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static WriteResult Apply(Options options)
    {
        var (manifest, tag) = ApprovedManifest.Load(options.ManifestPath!, options.ManifestSha256!);

        Inputs.RequireMsgstore(options.MsgstorePath!);
        Inputs.RequireWorkspace(options.WorkspacePath);
        Inputs.RequireUnclaimed(options.WorkspacePath);

        // The reviewed rule also needs the marked list: each row must still be marked R there.
        Dictionary<long, ReviewRow>? marks = null;

        if (manifest.Rule == PairingRule.NameSizeReviewed)
        {
            if (options.ReviewFile is null)
            {
                throw new RefusalException("This manifest is from a reviewed list; apply needs --review-file (the marked list).");
            }

            var (rows, listSha) = NameSizePlanner.ReadReviewFile(options.ReviewFile, null);

            if (manifest.Rows.Any(r => r.ReviewList != listSha[..12]))
            {
                throw new RefusalException("The review file is not the list this manifest was planned from. Nothing was written.");
            }

            marks = rows.ToDictionary(r => r.AttachmentID);
        }
        else if (options.ReviewFile is not null)
        {
            throw new RefusalException("--review-file applies only to a manifest of rule namesize-reviewed.");
        }

        var current = Recompute(options, manifest);

        var result = new WriteResult { Mode = "apply", Tag = tag, Rows = manifest.Rows.Count };
        var service = new Phase7ResolutionService();

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

            if (state.Resolved)
            {
                if (state.ResolvedAsset == row.MediaAssetID
                    && state.CurrentEffective?.MediaAssetID == row.MediaAssetID
                    && IsBatchConfirm(state.CurrentEffective, tag))
                {
                    result.AlreadyDone++;
                }
                else
                {
                    result.Other.Add(Reasons.ResolvedOtherwise);
                }

                continue;
            }

            if (!current.TryGetValue(row.AttachmentID, out var now))
            {
                result.Other.Add(Reasons.Missing);
                continue;
            }

            if (now.Disproven)
            {
                result.Other.Add(Reasons.SkipDisproven);
                continue;
            }

            if (now.DaySkipped)
            {
                result.Other.Add(Reasons.DayNoLongerUsable);
                continue;
            }

            if (!now.Eligible)
            {
                result.Other.Add(Reasons.NoLongerEligible);
                continue;
            }

            if (now.MessageID != row.MessageID || now.KeyID != row.KeyID || now.Asset != row.MediaAssetID
                || now.Kind != row.Kind
                || (manifest.Rule == PairingRule.OutsideCandidateSet && (now.PairedBy != row.PairedBy || now.FileDate != row.FileDate))
                || (manifest.Rule is PairingRule.Documents or PairingRule.NameSizeReviewed && now.PairedBy != row.PairedBy))
            {
                result.Other.Add(Reasons.Changed);
                continue;
            }

            if (marks is not null
                && (!marks.TryGetValue(row.AttachmentID, out var mark) || mark.Mark != ReviewMark.Right
                    || mark.MediaAssetID != row.MediaAssetID || mark.KeyID != row.KeyID))
            {
                result.Other.Add(Reasons.MarkGone);
                continue;
            }

            var reviewed = manifest.Rule == PairingRule.NameSizeReviewed;

            try
            {
                service.Confirm(new Phase7ConfirmRequest
                {
                    DatabasePath = options.WorkspacePath,
                    AttachmentID = row.AttachmentID,
                    MediaAssetID = row.MediaAssetID,
                    IndependentBasisType = reviewed
                        ? Phase7IndependentBasisType.HumanContentOrContextInspection
                        : Phase7IndependentBasisType.ExternalProvenanceOrGroundTruth,
                    IndependentBasisDescription = Description(row, tag, manifest.Rule),
                    // No snapshot is cited, for any rule: the reviewed rule's inspection happened from the list.
                    SourceRunID = null,
                    ActorLabel = reviewed ? NameSize.ReviewedActorLabel : ActorLabel,
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
                result.Other.Add(Reasons.ConfirmRefused);
            }
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        return result;
    }
}

internal static class Undoer
{
    internal static string Reason(string tag) => $"msgstore import batch {tag} withdrawn";

    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static WriteResult Undo(Options options)
    {
        var (manifest, tag) = ApprovedManifest.Load(options.ManifestPath!, options.ManifestSha256!);

        Inputs.RequireWorkspace(options.WorkspacePath);

        using (var connection = Chatcheology.Data.Workspace.WorkspaceDatabase.OpenReadOnlyConnection(options.WorkspacePath))
        {
            Inputs.RequireSchemaVersion(connection);
        }

        Inputs.RequireUnclaimed(options.WorkspacePath);

        var result = new WriteResult { Mode = "undo-batch", Tag = tag, Rows = manifest.Rows.Count };
        var service = new Phase7ResolutionService();

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

            if (!state.Resolved)
            {
                result.Other.Add(Reasons.NotResolved);
                continue;
            }

            if (!IsCurrentBatchConfirm(state, tag))
            {
                result.Other.Add(Reasons.NotThisBatch);
                continue;
            }

            try
            {
                service.Revoke(new Phase7RevokeRequest
                {
                    DatabasePath = options.WorkspacePath,
                    AttachmentID = row.AttachmentID,
                    SupersedesEventID = state.CurrentEffective!.AttachmentResolutionEventID,
                    RevocationReason = Reason(tag),
                    SourceRunID = null,
                    ActorLabel = Applier.ActorLabel,
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
                result.Other.Add(Reasons.RevokeRefused);
            }
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        return result;
    }

    private static bool IsCurrentBatchConfirm(Inputs.AttachmentState state, string tag) =>
        Applier.IsBatchConfirm(state.CurrentEffective, tag)
        && state.CurrentEffective!.MediaAssetID == state.ResolvedAsset;
}

internal static class WriteReport
{
    internal static string Of(WriteResult r)
    {
        var text = new StringBuilder();
        var apply = r.Mode == "apply";

        text.AppendLine($"# Msgstore import - {r.Mode}");
        text.AppendLine();
        text.AppendLine("Counts only.");
        text.AppendLine();
        text.AppendLine("```text");
        text.AppendLine($"{"batch tag",-58}{r.Tag}");
        text.AppendLine(Text.Line("manifest rows", r.Rows));
        text.AppendLine(Text.Line(apply ? "confirmed" : "revoked", r.Done));

        if (apply)
        {
            text.AppendLine(Text.Line("already done (this batch, same asset)", r.AlreadyDone));
        }

        text.AppendLine(Text.Line(apply ? "skipped" : "left alone", r.Other.Total));

        foreach (var (reason, count) in r.Other.Items)
        {
            text.AppendLine(Text.Line("  " + reason, count));
        }

        text.AppendLine();
        text.AppendLine(apply
            ? Text.Line("confirmed + already done + skipped", r.Done + r.AlreadyDone + r.Other.Total)
            : Text.Line("revoked + left alone", r.Done + r.Other.Total));
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
}
