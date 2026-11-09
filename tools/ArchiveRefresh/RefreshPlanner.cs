using System.Globalization;
using System.Text;
using Chatcheology.Data.ArchiveExport;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Psn;
using Chatcheology.Data.NewerMessages;
using Chatcheology.Data.Workspace;
using Chatcheology.Tools.MemoryImport;
using Microsoft.Data.Sqlite;
using Msg = MsgstoreImport;

namespace ArchiveRefresh;

/// <summary>What ties a plan to its command line: the path digests, value digests and read-only inputs.</summary>
internal sealed record RefreshBinding(
    SortedDictionary<string, string> Paths,
    SortedDictionary<string, string> Values,
    SortedDictionary<string, string> ReadOnlyInputs)
{
    internal const string Msgstore = "msgstore";
    internal const string MediaRoot = "media-root";
    internal const string PsnManifest = "psn-manifest";
    internal const string PsnCurrent = "psn-current";
    internal const string PsnDefinitions = "psn-definitions";

    internal static string PsnTimestamps(int index) => "psn-timestamps-" + (index + 1).ToString(CultureInfo.InvariantCulture);

    internal static RefreshBinding Of(RefreshOptions options)
    {
        var paths = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [RefreshFiles.Workspace] = RefreshFiles.PathDigest(options.WorkspacePath),
            [Msgstore] = RefreshFiles.PathDigest(options.MsgstorePath),
            [MediaRoot] = RefreshFiles.PathDigest(options.MediaRoot),
        };

        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var readOnly = new SortedDictionary<string, string>(StringComparer.Ordinal) { [Msgstore] = options.MsgstorePath };

        if (options.Psn is { } psn)
        {
            for (var i = 0; i < psn.Timestamps.Count; i++)
            {
                paths[PsnTimestamps(i)] = RefreshFiles.PathDigest(psn.Timestamps[i]);
                readOnly[PsnTimestamps(i)] = psn.Timestamps[i];
            }

            paths[PsnManifest] = RefreshFiles.PathDigest(psn.Manifest);
            paths[PsnCurrent] = RefreshFiles.PathDigest(psn.Current);
            paths[PsnDefinitions] = RefreshFiles.PathDigest(psn.Definitions);
            readOnly[PsnManifest] = psn.Manifest;
            readOnly[PsnCurrent] = psn.Current;
            readOnly[PsnDefinitions] = psn.Definitions;

            // Case-insensitive, as the PSN reader matches field names.
            values["psn-account-a"] = RefreshFiles.ValueDigest(psn.AccountA.ToUpperInvariant());
            values["psn-account-b"] = RefreshFiles.ValueDigest(psn.AccountB.ToUpperInvariant());
            values["psn-local"] = RefreshFiles.ValueDigest(psn.Local.ToUpperInvariant());
            values["psn-collection"] = RefreshFiles.ValueDigest(psn.Collection);
        }

        return new RefreshBinding(paths, values, readOnly);
    }

    /// <summary>Every read-only input's fingerprint now, by label.</summary>
    internal SortedDictionary<string, FileFingerprint> ReadOnlyState() =>
        new(ReadOnlyInputs.ToDictionary(p => p.Key, p => FileFingerprint.Of(p.Value, Describe(p.Key))), StringComparer.Ordinal);

    internal static string Describe(string label) => label switch
    {
        Msgstore => "msgstore",
        PsnManifest => "PSN game list",
        PsnCurrent => "current PSN shared-activity file",
        PsnDefinitions => "trophy definitions file",
        _ when label.StartsWith("psn-timestamps-", StringComparison.Ordinal) => "PSN timestamps file",
        _ => label,
    };
}

/// <summary>
/// <c>plan</c> (CH-71 step 5, D9 steps 1, 2, 4 and 5 read-only): the preflight, the newer-messages plan, the caption-facts
/// plan and, with PSN, the PSN build, compare and import dry run. Read-only on every input; writes only the new plan folder.
/// </summary>
internal static class RefreshPlanner
{
    internal sealed record PlanResult(string Sha256, string Report);

    /// <exception cref="RefreshRefusedException">Nothing was written (the plan folder is removed again).</exception>
    internal static PlanResult Plan(RefreshOptions options)
    {
        if (Msg.Planner.ManifestOutRefusal(options.PlanFolder, "--plan-folder") is { } refusal)
        {
            throw new RefreshRefusedException(refusal + " Nothing was written.");
        }

        var report = new StringBuilder();
        var binding = RefreshBinding.Of(options);

        // ---- Preflight: read-only; nothing is created until it has passed. ----
        Preflight(options, report);

        var planFolder = Path.GetFullPath(options.PlanFolder);
        Directory.CreateDirectory(planFolder);

        try
        {
            var plan = Compute(options, binding, planFolder, report);
            var bytes = plan.ToBytes();
            var sha = RefreshFiles.Sha256Hex(bytes);

            using (var stream = new FileStream(Path.Combine(planFolder, RefreshPlanFile.FileName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            report.AppendLine();
            report.AppendLine("The plan SHA-256 covers every sub-manifest above and the size and SHA-256 of every input. Approving it");
            report.AppendLine("approves exactly this refresh; apply refuses if anything differs.");
            report.AppendLine();
            report.AppendLine("PLAN SHA-256: " + sha);

            return new PlanResult(sha, report.ToString());
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            TryRemove(planFolder);
            throw;
        }
    }

    private static void TryRemove(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The plan folder holds only this run's private files; a leftover one is never approved by anything.
        }
    }

    /// <summary>D9 step 1: every input there, at the current versions, and no program holding the databases.</summary>
    private static void Preflight(RefreshOptions options, StringBuilder report)
    {
        if (!File.Exists(options.WorkspacePath))
        {
            throw new RefreshRefusedException("There is no file at the --workspace path. Nothing was written.");
        }

        RefreshFiles.RequireNoCompanions(options.WorkspacePath);

        try
        {
            Msg.Inputs.RequireMsgstore(options.MsgstorePath);
            Msg.WorkspaceMigrator.RequireCurrent(options.WorkspacePath);
        }
        catch (Msg.RefusalException exception)
        {
            throw new RefreshRefusedException(exception.Message);
        }

        if (!Directory.Exists(options.MediaRoot))
        {
            throw new RefreshRefusedException("There is no folder at the --media-root path. Nothing was written.");
        }

        if (options.Psn is { } psn)
        {
            if (psn.Timestamps.Append(psn.Manifest).Append(psn.Current).Append(psn.Definitions).Any(p => !File.Exists(p)))
            {
                throw new RefreshRefusedException(
                    "A PSN input file (timestamps, game list, current shared-activity file or definitions) is not there. Nothing was written.");
            }

            if (!File.Exists(RefreshFiles.PathOf(options.WorkspacePath, MemoriesDatabase.FileName)))
            {
                throw new RefreshRefusedException(
                    "There is no memories.db beside the workspace. Create it once with MemoryImport psn-import --create (see its README). " +
                    "Nothing was written.");
            }
        }

        // The archive id file and the older-exports, newer-messages and memories files are read exactly as the export
        // reads them: anything the export would refuse (a damaged file, an older version) refuses here.
        string? archiveId;
        ArchiveExportPreview preview;

        try
        {
            archiveId = ArchiveIdFile.Read(options.WorkspacePath, options.ConversationID);
            preview = new ArchiveExporter().Preview(options.WorkspacePath, options.ConversationID, options.LocalParticipantID);
        }
        catch (ArchiveExportRefusedException exception)
        {
            throw new RefreshRefusedException(exception.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        if (preview.Restored != options.ExpectRestored || preview.Missing != options.ExpectMissing)
        {
            throw new RefreshRefusedException(
                $"The workspace has {N(preview.Restored)} restored and {N(preview.Missing)} reviewed-missing attachments; " +
                $"--expect-restored is {N(options.ExpectRestored)} and --expect-missing is {N(options.ExpectMissing)}. Nothing was written.");
        }

        string Present(string label) =>
            File.Exists(RefreshFiles.PathOf(options.WorkspacePath, label)) ? "present" : "absent";

        report.AppendLine("# Archive refresh - plan (CH-71 step 5)");
        report.AppendLine();
        report.AppendLine("Read-only on every input. Counts only: no text, name, file name, id or path. Identifiers and digests are");
        report.AppendLine("in the private plan folder.");
        report.AppendLine();
        report.AppendLine("## Preflight");
        report.AppendLine();
        report.AppendLine("```text");
        report.AppendLine(Pad("workspace schema version") + WorkspaceDatabase.SchemaVersion.ToString(CultureInfo.InvariantCulture) + " (current)");
        report.AppendLine(Pad("older-exports.db") + Present(Chatcheology.Data.OlderExports.OlderExportsDatabase.FileName));
        report.AppendLine(Pad("newer-messages.db") + (File.Exists(NewerMessagesDatabase.PathBeside(options.WorkspacePath))
            ? "present" : "absent (the first apply creates it)"));
        report.AppendLine(Pad("memories.db") + Present(MemoriesDatabase.FileName));
        report.AppendLine(Pad("archive-id.json") + (archiveId is not null ? "present" : "absent (the first export creates it)"));
        report.AppendLine(Pad("msgstore") + "no -wal or -journal beside it; opened read-only");
        report.AppendLine(Pad("media root folder") + "present");
        report.AppendLine(Line("workspace messages", preview.Messages));
        report.AppendLine(Line("  attachments restored (as expected)", preview.Restored));
        report.AppendLine(Line("  attachments reviewed missing (as expected)", preview.Missing));
        report.AppendLine(Line("  attachments unmatched", preview.Unmatched));
        report.AppendLine(Line("newer messages already in newer-messages.db", preview.NewerMessages));
        report.AppendLine("```");
    }

    private static RefreshPlanFile Compute(RefreshOptions options, RefreshBinding binding, string planFolder, StringBuilder report)
    {
        // Fingerprints first and again at the end: a file that changes while the plan reads it refuses the plan.
        var before = RefreshFiles.StateOf(options.WorkspacePath);
        var readOnlyBefore = binding.ReadOnlyState();
        var manifests = new SortedDictionary<string, (string, string)>(StringComparer.Ordinal);

        // ---- Newer messages (D9 step 2, the step 3a plan). ----
        var newerPath = Path.Combine(planFolder, RefreshPlanFile.NewerManifest);

        try
        {
            var newer = Msg.NewerTool.Plan(new Msg.NewerOptions
            {
                Apply = false,
                MsgstorePath = options.MsgstorePath,
                WorkspacePath = options.WorkspacePath,
                MediaRoot = options.MediaRoot,
                ConversationID = options.ConversationID,
                ChatID = options.ChatID,
                LocalParticipantID = options.LocalParticipantID,
                ManifestOut = newerPath,
            });

            manifests[RefreshPlanFile.StepNewer] = (RefreshPlanFile.NewerManifest, newer.Sha256);
            report.AppendLine();
            report.AppendLine("## 1. Newer messages (into newer-messages.db)");
            report.AppendLine();
            report.Append(WithoutSubManifestLines(Msg.NewerTool.PlanReport(newer)));

            // ---- Caption facts for workspace attachments not yet captioned (the step 1a plan). ----
            var captionsPath = Path.Combine(planFolder, RefreshPlanFile.CaptionsManifest);
            var captions = Msg.CaptionFactsPlanner.Plan(Msg.Options.Parse(
            [
                "plan", "--rule", "caption-facts", "--msgstore", options.MsgstorePath, "--workspace", options.WorkspacePath,
                "--conversation", Id(options.ConversationID), "--chat", Id(options.ChatID),
                "--utc-offset", OffsetText(NewerMessagesTimeZone.Offset), "--local-participant", Id(options.LocalParticipantID),
                "--manifest-out", captionsPath,
            ]));

            if (!Msg.CaptionFactsPlanner.Reconciled(captions.Captions))
            {
                throw new RefreshRefusedException("The caption-facts plan does not reconcile (see its counts). Nothing was written.");
            }

            manifests[RefreshPlanFile.StepCaptions] = (RefreshPlanFile.CaptionsManifest, captions.Sha256);
            report.AppendLine();
            report.AppendLine("## 2. Picture captions for workspace attachments not yet captioned");
            report.AppendLine();
            report.Append(WithoutSubManifestLines(Msg.CaptionFactsPlanner.Report(captions)));
        }
        catch (Msg.RefusalException exception)
        {
            throw new RefreshRefusedException(exception.Message);
        }
        catch (Msg.OptionsException exception)
        {
            throw new RefreshRefusedException("The caption-facts plan could not be set up: " + exception.Message);
        }

        // ---- PSN (D8): build into the plan folder, compare with the current file, import dry run. ----
        if (options.Psn is { } psn)
        {
            var built = PlanPsn(psn, planFolder, report);
            manifests[RefreshPlanFile.StepPsn] = (RefreshPlanFile.PsnBuilt, built);
        }
        else
        {
            report.AppendLine();
            report.AppendLine("## 3. PSN");
            report.AppendLine();
            report.AppendLine("Left out of this refresh (no PSN options given).");
        }

        var after = RefreshFiles.StateOf(options.WorkspacePath);
        var readOnlyAfter = binding.ReadOnlyState();

        if (RefreshFiles.StateText(before) != RefreshFiles.StateText(after)
            || !readOnlyBefore.SequenceEqual(readOnlyAfter))
        {
            throw new RefreshRefusedException("An input changed while the plan read it. Close every other program and plan again. Nothing was written.");
        }

        var inputs = new SortedDictionary<string, FileFingerprint>(StringComparer.Ordinal);

        foreach (var (label, fingerprint) in after.Concat(readOnlyAfter))
        {
            inputs.Add(label, fingerprint);
        }

        return new RefreshPlanFile
        {
            ConversationID = options.ConversationID,
            ChatID = options.ChatID,
            LocalParticipantID = options.LocalParticipantID,
            ExpectRestored = options.ExpectRestored,
            ExpectMissing = options.ExpectMissing,
            Psn = options.Psn is not null,
            Paths = binding.Paths,
            Values = binding.Values,
            Inputs = inputs,
            Manifests = manifests,
        };
    }

    /// <returns>The built file's SHA-256.</returns>
    private static string PlanPsn(PsnInputs psn, string planFolder, StringBuilder report)
    {
        byte[] current, built;
        PsnActivity newActivity;
        PsnCompareResult compare;
        PsnBuildCounts counts;

        try
        {
            var timestamps = psn.Timestamps.Select(p => (ReadOnlyMemory<byte>)ReadInput(p, "PSN timestamps file")).ToList();
            var gameList = ReadInput(psn.Manifest, "PSN game list");
            current = ReadInput(psn.Current, "current PSN shared-activity file");

            var result = PsnSharedActivityBuilder.Build(timestamps, gameList, psn.AccountA, psn.AccountB, current);
            built = result.Json;
            counts = result.Counts;

            var oldActivity = PsnSharedActivityReader.Parse(current, psn.Local, psn.Other, psn.AccountA);
            newActivity = PsnSharedActivityReader.Parse(built, psn.Local, psn.Other, psn.AccountA);
            compare = PsnSharedActivityComparer.Compare(oldActivity, current, newActivity, built);

            // The definitions file must read before anything is approved; enrich itself runs in apply.
            PsnTrophyDefinitionsReader.Parse(ReadInput(psn.Definitions, "trophy definitions file"));
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            // The PSN library's refusals carry counts, indexes and reasons only (MemoryImport's own rule).
            throw new RefreshRefusedException("PSN: " + exception.Message + " Nothing was written.");
        }

        using (var stream = new FileStream(Path.Combine(planFolder, RefreshPlanFile.PsnBuilt), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(built);
            stream.Flush(flushToDisk: true);
        }

        var summary = PsnActivitySummary.From(newActivity);

        report.AppendLine();
        report.AppendLine("## 3. PSN shared activity (built from the fetch output and the game list)");
        report.AppendLine();
        report.AppendLine("```text");
        report.AppendLine(Line("games written", counts.GamesBuilt));
        report.AppendLine(Line("events", counts.Events));
        report.AppendLine(Line("  strong", counts.Strong));
        report.AppendLine(Line("  supporting", counts.Supporting));
        report.AppendLine(Line("  same-day", counts.SameDay));
        report.AppendLine(Line("matching pairs", counts.MatchingPairs));
        report.AppendLine(Line("skipped: game-list games with no timestamps", counts.ManifestGamesWithoutTimestamps));
        report.AppendLine(Line("skipped: timestamp games not in the game list", counts.TimestampGamesNotInManifest));
        report.AppendLine(Line("game icons from the game list", counts.IconsFromManifest));
        report.AppendLine(Line("game icons from the current file", counts.IconsFromExistingFile));
        report.AppendLine();
        report.AppendLine("compared with the current shared-activity file:");
        report.AppendLine(Line("  games: current / built", compare.OldGames) + " / " + N(compare.NewGames));
        report.AppendLine(Line("  events: current / built", compare.OldEvents) + " / " + N(compare.NewEvents));
        report.AppendLine(Line("  events only in the built file (new)", compare.OnlyNewTotal));
        report.AppendLine(Line("  games only in the built file (new)", compare.GamesOnlyNew));
        report.AppendLine(Line("  events in both, same level", compare.SameLevelTotal));
        report.AppendLine(Line("  events in both, different level", compare.DifferentLevelTotal));
        report.AppendLine(Line("  events only in the current file", compare.OnlyOldTotal));
        report.AppendLine(Line("  games only in the current file", compare.GamesOnlyOld));
        report.AppendLine(Line("  games in both, different title or icon", compare.GamesDifferentTitle + compare.GamesDifferentIcon));
        report.AppendLine(Line("  same level, different trophies, pairs or periods",
            compare.SameLevelDifferentTrophies + compare.SameLevelDifferentPairs + compare.SameLevelDifferentPeriods));
        report.AppendLine();
        report.AppendLine("import dry run (the built file as psn-import reads it):");
        report.AppendLine(Line("  records", summary.RecordCount));
        report.AppendLine(Line("  items", summary.ItemCount));
        report.AppendLine(Line("  periods", summary.PeriodCount));
        report.AppendLine(Line("  timeline cards (periods with both accounts)", summary.TimelineCards));
        report.AppendLine(Line("  matching pairs not located", summary.PairsNotLocated));
        report.AppendLine(Line("  matching pairs split across periods", summary.PairsSplit));
        report.AppendLine("```");

        var changed = compare.DifferentLevelTotal + compare.OnlyOldTotal + compare.GamesOnlyOld + compare.GamesDifferentTitle
                      + compare.GamesDifferentIcon + compare.SameLevelDifferentTrophies + compare.SameLevelDifferentPairs
                      + compare.SameLevelDifferentPeriods;

        if (changed > 0)
        {
            report.AppendLine();
            report.AppendLine("ATTENTION: the built file changes or drops something the current file holds (not only new games and");
            report.AppendLine("events). Import updates changed entries in place and keeps dropped ones. Check before approving.");
        }

        return RefreshFiles.Sha256Hex(built);
    }

    internal static byte[] ReadInput(string path, string what)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RefreshRefusedException($"The {what} could not be read ({exception.GetType().Name}). Nothing was written.");
        }
    }

    /// <summary>A sub-report without its own SHA-256 and batch tag lines: only the plan SHA-256 is approved.</summary>
    private static string WithoutSubManifestLines(string report) =>
        string.Join('\n', report.Split('\n').Where(l =>
            !l.StartsWith("manifest SHA-256:", StringComparison.Ordinal) && !l.StartsWith("batch tag:", StringComparison.Ordinal)));

    internal static string OffsetText(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString("hh':'mm", CultureInfo.InvariantCulture);

    private static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    internal static string Pad(string label) => label.Length >= 58 ? label + "  " : label.PadRight(58);

    internal static string Line(string label, long value) => Pad(label) + N(value);
}
