using System.Globalization;
using System.Text;
using Chatcheology.Archive;
using Chatcheology.Data.ArchiveExport;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Psn;
using Chatcheology.Data.NewerMessages;
using Microsoft.Data.Sqlite;
using Msg = MsgstoreImport;
using static ArchiveRefresh.RefreshPlanner;

namespace ArchiveRefresh;

/// <summary>Test seams. The tool itself uses the defaults.</summary>
internal sealed class RefreshHooks
{
    /// <summary>The clock that names the backup folder (local time).</summary>
    internal Func<DateTime> Now { get; init; } = () => DateTime.Now;

    /// <summary>Runs just before a step begins (after the backup). Never set outside tests.</summary>
    internal Action<string>? BeforeStep { get; init; }

    /// <summary>Runs just after a step's write, before it is recorded as done. Never set outside tests.</summary>
    internal Action<string>? AfterStep { get; init; }
}

/// <summary>
/// <c>apply</c> (CH-71 step 5, D9 steps 3 to 6): checks the plan is unchanged, backs up every file it may write, applies
/// each step in order (newer messages, caption facts, PSN import and enrich), exports the version 4 archive, then runs the
/// validator and the comparer. Stops at the first refusal with a counts-only reason and what was already applied.
/// </summary>
/// <remarks>
/// Every step is one transaction and safe to repeat; the progress file in the plan folder records each step begun and
/// done with the state of the files, so a rerun after a stop skips the finished steps and checks that nothing else
/// changed the files in between.
/// </remarks>
internal static class RefreshApplier
{
    internal const string StepNewer = "newer-messages";
    internal const string StepCaptions = "caption-facts";
    internal const string StepPsnImport = "psn-import";
    internal const string StepPsnEnrich = "psn-enrich";
    internal const string StepExport = "export";

    internal const string BackupContentsFile = "backup-contents.tsv";

    /// <summary>The file each step writes, by step.</summary>
    internal static string WrittenBy(string step) => step switch
    {
        StepNewer => NewerMessagesDatabase.FileName,
        StepCaptions => RefreshFiles.Workspace,
        StepPsnImport or StepPsnEnrich => MemoriesDatabase.FileName,
        StepExport => ArchiveIdFile.FileName,
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    };

    internal static IReadOnlyList<string> Steps(bool psn) =>
        psn ? [StepNewer, StepCaptions, StepPsnImport, StepPsnEnrich, StepExport] : [StepNewer, StepCaptions, StepExport];

    internal sealed record ApplyResult(bool Passed, string Report);

    /// <exception cref="RefreshRefusedException">Stopped; the message says what was already applied.</exception>
    internal static ApplyResult Apply(RefreshOptions options, RefreshHooks hooks)
    {
        var report = new StringBuilder();
        var planFolder = Path.GetFullPath(options.PlanFolder);
        var (plan, planSha) = LoadPlan(options, planFolder);
        var steps = Steps(plan.Psn);

        report.AppendLine("# Archive refresh - apply (CH-71 step 5)");
        report.AppendLine();
        report.AppendLine("Counts only: no text, name, file name, id or path.");
        report.AppendLine();
        report.AppendLine("```text");
        report.AppendLine(Pad("plan") + "unchanged (SHA-256 matches; every sub-manifest matches)");

        RefreshJournal journal;

        try
        {
            journal = RefreshJournal.OpenOrCreate(planFolder, planSha);
        }
        catch (FormatException exception)
        {
            throw new RefreshRefusedException(exception.Message + " Nothing was written.");
        }

        var done = journal.Done.ToHashSet(StringComparer.Ordinal);
        var interrupted = journal.Interrupted;

        RequireInputsAsPlanned(options, plan, journal, interrupted);
        report.AppendLine(Pad("inputs") + (journal.LastState is null
            ? "exactly as planned"
            : "as planned, and the workspace folder as the last apply left it"));

        if (done.Count > 0)
        {
            report.AppendLine(Pad("steps finished by an earlier apply (skipped)") + string.Join(", ", steps.Where(done.Contains)));
        }

        var remaining = steps.Where(s => !done.Contains(s)).ToList();
        var archiveOut = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.ArchiveOut!));
        var exportMade = done.Contains(StepExport) || (interrupted == StepExport && Directory.Exists(archiveOut));

        if (exportMade)
        {
            remaining.Remove(StepExport);

            if (!Directory.Exists(archiveOut))
            {
                throw new RefreshRefusedException(
                    "The archive was exported by an earlier apply, but there is no folder at --archive-out: give the same " +
                    "--archive-out as then. Nothing was written.");
            }
        }
        else if (Directory.Exists(archiveOut + ArchiveExporter.PartialSuffix) || File.Exists(archiveOut + ArchiveExporter.PartialSuffix))
        {
            throw new RefreshRefusedException(PartialAdvice + " Nothing was written.");
        }
        else if (Msg.Planner.ManifestOutRefusal(archiveOut, "--archive-out") is { } refusal)
        {
            throw new RefreshRefusedException(refusal + " Nothing was written.");
        }

        if (remaining.Count > 0)
        {
            var rerun = journal.FirstBackup is not null;
            var backup = Backup(options, hooks, journal);
            report.AppendLine(Pad(rerun ? "backup taken before this rerun" : "backup before the first write") +
                              $"{N(backup.Files)} file(s) copied and verified (size and SHA-256), folder {backup.Name}");
            report.AppendLine(Pad("  not there, so not copied") + N(RefreshFiles.Writable.Length - backup.Files));
        }

        var firstBackup = journal.FirstBackup;

        if (firstBackup is not null)
        {
            report.AppendLine(Pad("files as they were before the refresh") + "in backup folder " + firstBackup);
        }

        report.AppendLine("```");

        var applied = steps.Where(done.Contains).ToList();

        foreach (var step in remaining)
        {
            hooks.BeforeStep?.Invoke(step);
            journal.Record("begin", step, RefreshFiles.StateText(RefreshFiles.StateOf(options.WorkspacePath)));

            try
            {
                report.AppendLine();
                report.Append(RunStep(step, options, plan, planFolder, archiveOut));
            }
            catch (RefreshRefusedException exception)
            {
                throw new RefreshRefusedException(StopMessage(step, exception.Message, applied, firstBackup));
            }
            catch (Exception exception) when (exception is not RefreshRefusedException)
            {
                // The exception's own message may carry a path, so only its type is shown.
                throw new RefreshRefusedException(StopMessage(step, $"it could not complete ({exception.GetType().Name})", applied, firstBackup));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }

            hooks.AfterStep?.Invoke(step);
            journal.Record("done", step, RefreshFiles.StateText(RefreshFiles.StateOf(options.WorkspacePath)));
            applied.Add(step);
        }

        var passed = Check(options, plan, archiveOut, report);

        report.AppendLine();

        if (passed)
        {
            report.AppendLine("Done. The archive passed the validator and the comparison with the workspace.");
            report.AppendLine();
            report.AppendLine("Copy it to the phone (one command, from the repository folder):");
            report.AppendLine();
            report.AppendLine("  powershell -ExecutionPolicy Bypass -File .\\tools\\Copy-ArchiveToPhone.ps1 -ArchiveFolder '<the --archive-out folder>'");
            report.AppendLine();
            report.AppendLine("Then, on the phone: Import archive..., and pick the copied folder. The phone recognises the archive id and keeps");
            report.AppendLine("stars, position and settings.");
        }
        else
        {
            report.AppendLine("The archive did NOT pass (see the counts above). Do not copy it to the phone. The steps applied stay");
            report.AppendLine("applied. To undo the whole refresh, restore the files of the first backup folder (" + (firstBackup ?? "none made") + "):");
            report.AppendLine("it holds them as they were before the refresh; later backups were taken before a rerun.");
        }

        if (plan.Psn)
        {
            report.AppendLine();
            report.AppendLine("PSN game images and trophy icons are fetched separately (it needs the internet):");
            report.AppendLine("  .\\tools\\ArchiveRefresh\\Run-Refresh.ps1 -Step Images ... (see the README); they reach the phone with the next archive.");
        }

        return new ApplyResult(passed, report.ToString());
    }

    private const string PartialAdvice =
        "A folder named like --archive-out with .partial on the end is there: an export that stopped part way. Delete that " +
        ".partial folder (only it, beside the --archive-out you gave), or give a new --archive-out, then run the same apply again.";

    private static string StopMessage(string step, string reason, List<string> applied, string? firstBackup) =>
        $"Stopped at step {step}: {reason}" + Environment.NewLine +
        $"Finished before the stop (in this or an earlier apply): {(applied.Count == 0 ? "none" : string.Join(", ", applied))}. " +
        $"Step {step} may have written part of its work (an export may already have made the archive id, a caption batch " +
        "may be recorded); a rerun of the same apply continues safely and skips finished steps. If an input changed, plan again. " +
        $"To undo the whole refresh, restore the files of the first backup folder ({firstBackup ?? "none made"}), which holds " +
        "them as they were before the refresh." +
        (step == StepExport ? " " + PartialAdvice.Replace(" is there:", " may be there:", StringComparison.Ordinal) : "");

    private static (RefreshPlanFile Plan, string Sha) LoadPlan(RefreshOptions options, string planFolder)
    {
        var planPath = Path.Combine(planFolder, RefreshPlanFile.FileName);

        if (!File.Exists(planPath))
        {
            throw new RefreshRefusedException("There is no plan in the --plan-folder. Run plan first. Nothing was written.");
        }

        var bytes = ReadInput(planPath, "plan file");
        var sha = RefreshFiles.Sha256Hex(bytes);

        if (!string.Equals(sha, options.PlanSha256, StringComparison.Ordinal))
        {
            throw new RefreshRefusedException(
                "The plan's SHA-256 does not match --plan-sha256: the plan changed since it was approved, or another plan or " +
                "digest was given. Nothing was opened or written.");
        }

        RefreshPlanFile plan;

        try
        {
            plan = RefreshPlanFile.Parse(bytes);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
        {
            throw new RefreshRefusedException("The plan file is malformed. Nothing was written.");
        }

        var binding = RefreshBinding.Of(options);

        if (plan.Psn != (options.Psn is not null))
        {
            throw new RefreshRefusedException(
                plan.Psn
                    ? "The plan includes PSN: give the same PSN options to apply. Nothing was written."
                    : "The plan leaves PSN out: give no PSN options to apply. Nothing was written.");
        }

        if (!plan.Paths.SequenceEqual(binding.Paths) || !plan.Values.SequenceEqual(binding.Values))
        {
            throw new RefreshRefusedException(
                "A path or PSN name differs from the plan's (the same workspace, msgstore, media root and PSN files and names " +
                "must be given). Nothing was written.");
        }

        foreach (var (_, (file, manifestSha)) in plan.Manifests)
        {
            var path = Path.Combine(planFolder, file);

            if (!File.Exists(path) || RefreshFiles.Sha256Hex(ReadInput(path, "sub-manifest")) != manifestSha)
            {
                throw new RefreshRefusedException("A sub-manifest in the plan folder is missing or changed since the plan. Plan again. Nothing was written.");
            }
        }

        return (plan, sha);
    }

    /// <summary>
    /// The read-only inputs exactly as planned; the workspace folder's files as planned, or as the last apply left them
    /// (a step the last apply stopped inside may have written its own file, which that step rechecks when it runs again).
    /// </summary>
    private static void RequireInputsAsPlanned(RefreshOptions options, RefreshPlanFile plan, RefreshJournal journal, string? interrupted)
    {
        if (!File.Exists(options.WorkspacePath))
        {
            throw new RefreshRefusedException("There is no file at the --workspace path. Nothing was written.");
        }

        RefreshFiles.RequireNoCompanions(options.WorkspacePath);

        try
        {
            Msg.Inputs.RequireMsgstore(options.MsgstorePath);
        }
        catch (Msg.RefusalException exception)
        {
            throw new RefreshRefusedException(exception.Message);
        }

        if (!Directory.Exists(options.MediaRoot))
        {
            throw new RefreshRefusedException("There is no folder at the --media-root path. Nothing was written.");
        }

        var readOnly = RefreshBinding.Of(options).ReadOnlyState();
        var changedReadOnly = readOnly.Count(p => !plan.Inputs.TryGetValue(p.Key, out var planned) || planned != p.Value);

        if (changedReadOnly > 0)
        {
            throw new RefreshRefusedException(
                $"{N(changedReadOnly)} read-only input(s) (the msgstore or a PSN file) differ from the plan. Plan again. Nothing was written.");
        }

        var expected = journal.LastState is { } last
            ? RefreshFiles.ParseState(last)
            : new SortedDictionary<string, FileFingerprint>(
                RefreshFiles.Writable.ToDictionary(l => l, l => plan.Inputs.TryGetValue(l, out var f) ? f : FileFingerprint.Absent),
                StringComparer.Ordinal);

        var mayDiffer = interrupted is null ? null : WrittenBy(interrupted);
        var now = RefreshFiles.StateOf(options.WorkspacePath);
        var changed = RefreshFiles.Writable.Count(l => l != mayDiffer && now[l] != expected[l]);

        if (changed > 0)
        {
            throw new RefreshRefusedException(
                $"{N(changed)} file(s) beside the workspace (workspace, older-exports, newer-messages, memories or archive id) " +
                (journal.LastState is null ? "changed since the plan" : "changed since the last apply stopped") +
                ". Plan again. Nothing was written.");
        }
    }

    private sealed record BackupResult(string Name, int Files);

    /// <summary>A new timestamped backup folder with every file the refresh may write, each verified by size and SHA-256.</summary>
    private static BackupResult Backup(RefreshOptions options, RefreshHooks hooks, RefreshJournal journal)
    {
        if (!Directory.Exists(options.BackupRoot))
        {
            throw new RefreshRefusedException("There is no folder at the --backup-root path. Nothing was written.");
        }

        var name = "refresh-backup-" + hooks.Now().ToString("yyyyMMdd'-'HHmmss", CultureInfo.InvariantCulture);
        var folder = Path.Combine(Path.GetFullPath(options.BackupRoot!), name);

        if (Msg.Planner.ManifestOutRefusal(folder, "backup folder") is { } refusal)
        {
            throw new RefreshRefusedException(refusal + " Nothing was written.");
        }

        var before = RefreshFiles.StateOf(options.WorkspacePath);
        Directory.CreateDirectory(folder);

        var contents = new StringBuilder("chatcheology-archive-refresh-backup\t1\n");
        var copied = 0;

        foreach (var label in RefreshFiles.Writable)
        {
            if (!before[label].Exists)
            {
                contents.Append(label).Append("\tabsent\n");
                continue;
            }

            var source = RefreshFiles.PathOf(options.WorkspacePath, label);
            var target = Path.Combine(folder, label == RefreshFiles.Workspace ? Path.GetFileName(source) : label);

            try
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new RefreshRefusedException(
                    $"The backup could not be written ({exception.GetType().Name}). Nothing was applied; the backup folder is incomplete, " +
                    "delete it and run apply again.");
            }

            var copy = FileFingerprint.Of(target, "backup copy");

            if (copy != before[label] || FileFingerprint.Of(source, "file being backed up") != before[label])
            {
                throw new RefreshRefusedException(
                    "A backup copy does not match its file by size and SHA-256 (or the file changed while it was copied). Nothing " +
                    "was applied; delete the backup folder and run apply again.");
            }

            contents.Append(label).Append('\t').Append(copy.Text).Append('\n');
            copied++;
        }

        File.WriteAllText(Path.Combine(folder, BackupContentsFile), contents.ToString(), new UTF8Encoding(false));
        journal.Record("backup", name, RefreshFiles.StateText(before));

        return new BackupResult(name, copied);
    }

    private static string RunStep(string step, RefreshOptions options, RefreshPlanFile plan, string planFolder, string archiveOut)
    {
        var text = new StringBuilder();

        switch (step)
        {
            case StepNewer:
            {
                text.AppendLine("## 1. Newer messages");
                text.AppendLine();
                var (_, sha) = plan.Manifests[RefreshPlanFile.StepNewer];

                try
                {
                    var result = Msg.NewerTool.Apply(new Msg.NewerOptions
                    {
                        Apply = true,
                        MsgstorePath = options.MsgstorePath,
                        WorkspacePath = options.WorkspacePath,
                        MediaRoot = options.MediaRoot,
                        ManifestPath = Path.Combine(planFolder, RefreshPlanFile.NewerManifest),
                        ManifestSha256 = sha,
                    });

                    text.Append(Msg.NewerTool.ApplyReport(result));
                }
                catch (Msg.RefusalException exception)
                {
                    throw new RefreshRefusedException(exception.Message);
                }

                break;
            }

            case StepCaptions:
            {
                text.AppendLine("## 2. Picture captions");
                text.AppendLine();
                var (_, sha) = plan.Manifests[RefreshPlanFile.StepCaptions];

                try
                {
                    var result = Msg.CaptionFactsBatch.Apply(Msg.Options.Parse(
                    [
                        "apply", "--msgstore", options.MsgstorePath, "--workspace", options.WorkspacePath,
                        "--manifest", Path.Combine(planFolder, RefreshPlanFile.CaptionsManifest), "--manifest-sha256", sha,
                        "--i-have-a-backup",
                    ]));

                    text.Append(Msg.FactsBatch.Report(result));

                    if (!result.Reconciled)
                    {
                        throw new RefreshRefusedException("the caption batch does not reconcile against its manifest");
                    }
                }
                catch (Msg.RefusalException exception)
                {
                    throw new RefreshRefusedException(exception.Message);
                }
                catch (Msg.OptionsException exception)
                {
                    throw new RefreshRefusedException(exception.Message);
                }

                break;
            }

            case StepPsnImport:
            {
                var psn = options.Psn!;
                var activity = Psn(() => PsnSharedActivityReader.Parse(
                    ReadInput(Path.Combine(planFolder, RefreshPlanFile.PsnBuilt), "built PSN file"), psn.Local, psn.Other, psn.AccountA));
                var result = Psn(() => new PsnImporter().Import(
                    RefreshFiles.PathOf(options.WorkspacePath, MemoriesDatabase.FileName), activity, psn.Collection));

                text.AppendLine("## 3. PSN import");
                text.AppendLine();
                text.AppendLine("```text");
                text.AppendLine(Pad("collection created") + (result.CollectionCreated ? "yes" : "no"));
                text.AppendLine(Line("records added", result.Added));
                text.AppendLine(Line("records updated", result.Updated));
                text.AppendLine(Line("records unchanged", result.Unchanged));
                text.AppendLine(Line("entries no longer in the file (kept)", result.MissingFromFile));
                text.AppendLine(Line("changes", result.Changes));
                text.AppendLine("```");
                break;
            }

            case StepPsnEnrich:
            {
                var definitions = Psn(() => PsnTrophyDefinitionsReader.Parse(ReadInput(options.Psn!.Definitions, "trophy definitions file")));
                var result = Psn(() => PsnTrophyEnricher.Enrich(RefreshFiles.PathOf(options.WorkspacePath, MemoriesDatabase.FileName), definitions));

                text.AppendLine("## 4. PSN trophy names and icons (enrich)");
                text.AppendLine();
                text.AppendLine("```text");
                text.AppendLine(Line("definitions", result.Definitions));
                text.AppendLine(Line("items with a definition", result.ItemsWithDefinition));
                text.AppendLine(Line("items without a definition", result.ItemsWithoutDefinition));
                text.AppendLine(Line("definitions added", result.Added));
                text.AppendLine(Line("definitions updated", result.Updated));
                text.AppendLine(Line("definitions unchanged", result.Unchanged));
                text.AppendLine(Line("definitions not stored (type mismatch)", result.TypeMismatches));
                text.AppendLine(Line("changes", result.Changes));
                text.AppendLine("```");
                break;
            }

            case StepExport:
            {
                ArchiveExportResult result;

                try
                {
                    result = new ArchiveExporter().Export(new ArchiveExportRequest
                    {
                        WorkspacePath = options.WorkspacePath,
                        ConversationID = plan.ConversationID,
                        LocalParticipantID = plan.LocalParticipantID,
                        OutputPath = archiveOut,
                        ExpectResolved = plan.ExpectRestored,
                        ExpectMissingReviewed = plan.ExpectMissing,
                    });
                }
                catch (Exception exception) when (exception is ArchiveExportRefusedException or ArchiveExportStoppedException)
                {
                    // The exporter's messages are fixed phrases with counts, never a path.
                    throw new RefreshRefusedException(exception.Message);
                }

                var c = result.Counts;
                text.AppendLine("## 5. Archive export (format version 4)");
                text.AppendLine();
                text.AppendLine("```text");
                text.AppendLine(Line("messages", c.Messages));
                text.AppendLine(Line("  from older exports", c.ExportMessages));
                text.AppendLine(Line("  from the workspace", c.WorkspaceMessages));
                text.AppendLine(Line("  newer messages (msgstore)", c.MsgstoreMessages));
                text.AppendLine(Line("attachments", c.Attachments));
                text.AppendLine(Line("  Restored", c.Restored));
                text.AppendLine(Line("  Missing", c.Missing));
                text.AppendLine(Line("  Unmatched", c.Unmatched));
                text.AppendLine(Line("  with a caption", c.Captions));
                text.AppendLine(Line("media files", c.MediaFiles));
                text.AppendLine(Line("memory collections", c.Collections.Count));
                text.AppendLine(Line("  entries", c.Collections.Sum(k => k.Entries)));
                text.AppendLine(Line("media copies skipped (bytes differ from the recorded hash)", result.DifferentCopies));
                text.AppendLine(Line("media copies skipped (not there or unreadable)", result.UnreadableCopies));
                text.AppendLine(Pad("archive id") + "fixed (kept from earlier exports, or made by this first one)");
                text.AppendLine("```");
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(step));
        }

        return text.ToString();
    }

    /// <summary>The PSN library's refusals carry counts, indexes and reasons only (MemoryImport's own rule).</summary>
    private static T Psn<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            throw new RefreshRefusedException(exception.Message);
        }
    }

    /// <summary>The validator and the comparer on the new archive. Read-only.</summary>
    private static bool Check(RefreshOptions options, RefreshPlanFile plan, string archiveOut, StringBuilder report)
    {
        report.AppendLine();
        report.AppendLine("## 6. Checks on the new archive");
        report.AppendLine();
        report.AppendLine("```text");

        ArchiveValidationResult validation;

        try
        {
            validation = ArchiveValidator.Validate(archiveOut);
        }
        catch (ArgumentException)
        {
            report.AppendLine(Pad("validator") + "could not run: there is no archive folder at --archive-out");
            report.AppendLine("```");
            return false;
        }

        foreach (var check in validation.Checks)
        {
            report.AppendLine(Pad("validator: " + check.Check) +
                              (check.Passed ? "passed" : check.Ran ? "FAILED" : "NOT RUN") +
                              $" ({N(check.Examined)} examined, {N(check.Problems)} problem(s))");
        }

        ArchiveComparisonResult? comparison = null;

        try
        {
            comparison = new ArchiveWorkspaceComparer().Compare(new ArchiveComparisonRequest
            {
                WorkspacePath = options.WorkspacePath,
                ConversationID = plan.ConversationID,
                LocalParticipantID = plan.LocalParticipantID,
                ArchivePath = archiveOut,
            });
        }
        catch (ArchiveComparisonRefusedException exception)
        {
            report.AppendLine(Pad("comparison with the workspace") + "could not be made. " + exception.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        if (comparison is not null)
        {
            report.AppendLine(Pad("comparison with the workspace") + (comparison.Passed ? "passed" : "FAILED"));
            report.AppendLine(Line("  messages compared", comparison.MessagesCompared));
            report.AppendLine(Line("    newer messages", comparison.MsgstoreMessages));
            report.AppendLine(Line("  attachments compared", comparison.AttachmentsCompared));
            report.AppendLine(Line("  captions", comparison.Captions));
            report.AppendLine(Line("  mismatches", comparison.MismatchCounts.Values.Sum()));
        }

        report.AppendLine("```");

        return validation.Passed && comparison?.Passed == true;
    }
}
