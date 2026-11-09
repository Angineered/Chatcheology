using System.Globalization;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Psn;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Tools.MemoryImport
{
    /// <summary>
    /// The memories import tool: <c>psn-dry-run</c> (the default), <c>psn-import</c>,
    /// <c>psn-enrich-dry-run</c>, <c>psn-enrich</c>, <c>fetch-images</c>, <c>psn-build</c>,
    /// <c>psn-compare</c> and <c>psn-fetch</c>. See the README beside this file for usage.
    /// </summary>
    /// <remarks>
    /// Privacy rule for output: counts and dates only. Never a game title, account prefix, URL,
    /// trophy or set ID, or path, in output or in error text. Error output never echoes an argument
    /// value and never passes on a framework message that may quote a path.
    /// </remarks>
    public static class MemoryImportCommand
    {
        /// <summary>Exit code: success.</summary>
        public const int Success = 0;

        /// <summary>Exit code: the operation was refused or failed.</summary>
        public const int Failed = 1;

        /// <summary>Exit code: the command line could not be understood.</summary>
        public const int UsageError = 2;

        /// <summary>Exit code of <c>psn-compare</c>: the two files differ.</summary>
        public const int Different = 3;

        private const string DateFormat = "yyyy'-'MM'-'dd";

        /// <summary>Runs the tool.</summary>
        /// <param name="args">The command line.</param>
        /// <param name="output">Where results go.</param>
        /// <param name="error">Where refusals and usage errors go.</param>
        /// <param name="utcNow">The clock, for the memories file's creation timestamp.</param>
        /// <param name="imageSource">Makes the image downloader; only called by <c>fetch-images</c>.</param>
        public static int Run(
            string[] args,
            TextWriter output,
            TextWriter error,
            Func<DateTime> utcNow,
            Func<IMemoryImageSource> imageSource) =>
            Run(args, output, error, utcNow, imageSource, PsnFetchEnvironment.Console);

        /// <summary>Runs the tool.</summary>
        /// <param name="args">The command line.</param>
        /// <param name="output">Where results go.</param>
        /// <param name="error">Where refusals and usage errors go.</param>
        /// <param name="utcNow">The clock, for the memories file's creation timestamp.</param>
        /// <param name="imageSource">Makes the image downloader; only called by <c>fetch-images</c>.</param>
        /// <param name="psnFetch">Makes the PlayStation connection and secret prompt; only called by <c>psn-fetch</c>.</param>
        public static int Run(
            string[] args,
            TextWriter output,
            TextWriter error,
            Func<DateTime> utcNow,
            Func<IMemoryImageSource> imageSource,
            Func<PsnFetchEnvironment> psnFetch)
        {
            ArgumentNullException.ThrowIfNull(psnFetch);
            ArgumentNullException.ThrowIfNull(args);
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(error);
            ArgumentNullException.ThrowIfNull(utcNow);
            ArgumentNullException.ThrowIfNull(imageSource);

            ParsedArguments parsed;

            try
            {
                parsed = ParsedArguments.Parse(args);
            }
            catch (UsageException exception)
            {
                error.WriteLine($"Usage error: {exception.Message}");
                error.WriteLine("Run with --help for usage.");
                return UsageError;
            }

            if (parsed.Help)
            {
                WriteHelp(output);
                return Success;
            }

            try
            {
                return parsed.Command switch
                {
                    "psn-import" => RunImport(parsed, output, utcNow),
                    "psn-enrich-dry-run" => RunEnrich(parsed, output, apply: false),
                    "psn-enrich" => RunEnrich(parsed, output, apply: true),
                    "fetch-images" => RunFetchImages(parsed, output, imageSource),
                    "psn-build" => RunBuild(parsed, output),
                    "psn-compare" => RunCompare(parsed, output),
                    "psn-fetch" => RunFetch(parsed, output, utcNow, psnFetch),
                    _ => RunDryRun(parsed, output),
                };
            }
            catch (UsageException exception)
            {
                error.WriteLine($"Usage error: {exception.Message}");
                return UsageError;
            }
            catch (Exception exception)
            {
                error.WriteLine($"Stopped: {DescribeStop(exception)}");
                return Failed;
            }
        }

        /// <summary>
        /// The library's deliberate refusals carry counts, indexes and reasons only, so their
        /// message is shown; anything else may carry a path or source detail, so only its type is.
        /// </summary>
        private static string DescribeStop(Exception exception) =>
            exception is FormatException or InvalidOperationException or ArgumentException
                ? exception.Message
                : $"{exception.GetType().Name} (details suppressed because they may contain a path).";

        private static PsnActivity ReadPsn(ParsedArguments parsed) => ReadPsn(parsed, parsed.PsnFile);

        private static PsnActivity ReadPsn(ParsedArguments parsed, string? path)
        {
            if (path is null)
            {
                throw new UsageException("a PSN file is needed.");
            }

            if (parsed.Local is null || parsed.Other is null || parsed.PairA is null)
            {
                throw new UsageException("--local, --other and --pair-a are all needed.");
            }

            return PsnSharedActivityReader.Read(path, parsed.Local, parsed.Other, parsed.PairA, parsed.GapMinutes);
        }

        private static byte[] ReadInput(string path, string what)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The framework message may quote the path.
                throw new InvalidOperationException($"The {what} could not be read ({exception.GetType().Name}).");
            }
        }

        private static int RunBuild(ParsedArguments parsed, TextWriter output)
        {
            var inputs = parsed.Timestamps.Append(parsed.Manifest!).ToList();

            if (parsed.IconsFrom is not null)
            {
                inputs.Add(parsed.IconsFrom);
            }

            if (parsed.Out is not null)
            {
                var outFull = Path.GetFullPath(parsed.Out);

                if (inputs.Any(input => string.Equals(Path.GetFullPath(input), outFull, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new UsageException("--out names one of the input files; the inputs are never written.");
                }
            }

            var timestamps = new List<ReadOnlyMemory<byte>>();

            for (var index = 0; index < parsed.Timestamps.Count; index++)
            {
                timestamps.Add(ReadInput(parsed.Timestamps[index], $"timestamp file {index + 1}"));
            }

            var manifest = ReadInput(parsed.Manifest!, "manifest");
            ReadOnlyMemory<byte>? existing = null;

            if (parsed.IconsFrom is not null)
            {
                existing = ReadInput(parsed.IconsFrom, "--icons-from file");
            }

            var result = PsnSharedActivityBuilder.Build(
                timestamps, manifest, parsed.AccountA!, parsed.AccountB!, existing, parsed.AllowNoIcon);

            // The built file must pass the same reader psn-import uses before anything is written.
            PsnSharedActivityReader.Parse(result.Json, parsed.AccountA!, parsed.AccountB!, parsed.AccountA!);

            if (!parsed.DryRun)
            {
                WriteOutput(parsed.Out!, result.Json, parsed.Overwrite);
            }

            output.WriteLine(parsed.DryRun ? "Dry run: nothing is written." : "Built.");
            var c = result.Counts;
            output.WriteLine($"Manifest games: {c.ManifestGames}");
            output.WriteLine($"Timestamp games: {c.TimestampGames}");
            output.WriteLine($"Games written: {c.GamesBuilt}");
            output.WriteLine($"  with different trophy sets (never matched): {c.GamesWithDistinctSets}");
            output.WriteLine($"Events: {c.Events}");
            output.WriteLine($"  strong: {c.Strong}");
            output.WriteLine($"  supporting: {c.Supporting}");
            output.WriteLine($"  same-day: {c.SameDay}");
            output.WriteLine($"Matching pairs: {c.MatchingPairs}");
            output.WriteLine($"Trophies in events: {c.TrophiesInEvents}");
            output.WriteLine("Skipped:");
            output.WriteLine($"  manifest games with no timestamps: {c.ManifestGamesWithoutTimestamps}");
            output.WriteLine($"  timestamp games not in the manifest: {c.TimestampGamesNotInManifest}");
            output.WriteLine($"  games with no trophies for either account: {c.GamesWithNoTrophies}");
            output.WriteLine($"  games with trophies for one account only: {c.GamesOneAccountOnly}");
            output.WriteLine($"  games with both accounts but never on the same date: {c.GamesNoCommonDate}");
            output.WriteLine($"  game dates with one account only: {c.OneAccountDates}");
            output.WriteLine("Game icons:");
            output.WriteLine($"  from the manifest: {c.IconsFromManifest}");
            output.WriteLine($"  from the existing file: {c.IconsFromExistingFile}");
            output.WriteLine($"  none: {c.IconsNone}");
            output.WriteLine($"  games with events and no icon (allowed by --allow-no-icon): {c.GamesWithEventsAndNoIcon}");
            return Success;
        }

        private static void WriteOutput(string path, byte[] json, bool overwrite)
        {
            if (!overwrite && File.Exists(path))
            {
                throw new InvalidOperationException("The output file already exists. Add --overwrite to replace it. Nothing has been written.");
            }

            try
            {
                // Write beside the target first so a failed write never leaves half a file. Without
                // --overwrite the move itself refuses a file that appeared in the meantime.
                var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");

                try
                {
                    File.WriteAllBytes(temp, json);
                    File.Move(temp, path, overwrite);
                }
                finally
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"The output file could not be written ({exception.GetType().Name}).");
            }
        }

        private static int RunFetch(ParsedArguments parsed, TextWriter output, Func<DateTime> utcNow, Func<PsnFetchEnvironment> psnFetch)
        {
            var environment = psnFetch();
            using var api = new PsnApiClient(environment.Handler, environment.Prompt, environment.Sleep, output);
            var options = new PsnFetchOptions(
                parsed.PreviousList!, parsed.DefinitionsFile!, parsed.DecisionsFile!, parsed.AccountA!, parsed.AccountB!, parsed.Local!, parsed.Out!);
            var result = PsnTrophyFetcher.Run(options, api, environment.Prompt, utcNow);
            var c = result.Counts;

            if (!result.Completed)
            {
                output.WriteLine("Stopped before fetching any trophies.");

                if (c.MissingSideA + c.MissingSideB > 0)
                {
                    output.WriteLine($"  listed games no longer returned by PlayStation: account a {c.MissingSideA}, account b {c.MissingSideB} (details: {PsnTrophyFetcher.MissingFile}).");
                }

                if (c.DecisionConflicts + c.ListedGamesNotTogether > 0)
                {
                    output.WriteLine($"  candidates matched by decisions rows with different statuses: {c.DecisionConflicts}");
                    output.WriteLine($"  previous-list games marked excluded or uncertain: {c.ListedGamesNotTogether}");
                    output.WriteLine($"  (details: {PsnTrophyFetcher.ConflictsFile})");
                }

                output.WriteLine("The details are in the out folder. Correct the decisions file or the game list, and run again.");
                return Failed;
            }

            output.WriteLine("PSN fetch done.");
            output.WriteLine("Game list:");
            output.WriteLine($"  on the previous list: {c.ListedBefore}");
            output.WriteLine($"  added from decisions (together): {c.Added}");
            output.WriteLine($"  not added, excluded: {c.NotAddedExcluded}");
            output.WriteLine($"  not added, uncertain: {c.NotAddedUncertain}");
            output.WriteLine($"  not added, awaiting review (no decisions row; see {PsnTrophyFetcher.ToReviewFile}): {c.AwaitingReview}");
            output.WriteLine($"  not added, together but a set would be in two games (to review): {c.TogetherBlocked}");
            output.WriteLine($"  together-row pairs not offered as candidates (to review): {c.NotOffered}");
            output.WriteLine($"  together rows with no pair returned on both accounts (see {PsnTrophyFetcher.TogetherNotReturnedFile}): {c.TogetherNotReturned}");
            output.WriteLine($"  ids in together rows not returned for their account (same file): {c.TogetherIdsNotReturned}");
            output.WriteLine($"  listed now: {c.ListedNow}");
            output.WriteLine($"  icons added (added games only): {c.IconsAdded}");
            output.WriteLine($"  added games with no icon: {c.AddedWithoutIcon}");
            output.WriteLine($"  listed games with no icon (previous-list games: carried over from -PsnCurrent at Plan): {c.IconsNone}");
            output.WriteLine("Trophy times:");
            output.WriteLine($"  games fetched in this run: {c.GamesFetched}");
            output.WriteLine($"  games saved by an earlier run (skipped): {c.GamesAlreadySaved}");
            output.WriteLine($"  trophies earned, account a: {c.TrophiesA}");
            output.WriteLine($"  trophies earned, account b: {c.TrophiesB}");
            output.WriteLine("Trophy definitions:");
            output.WriteLine($"  sets in the given file: {c.DefinitionSetsBefore}");
            output.WriteLine($"  sets added: {c.DefinitionSetsFetched}");
            output.WriteLine($"  sets refreshed for new trophies (DLC): {c.DefinitionSetsRefreshed}");
            output.WriteLine($"  sets kept because PlayStation returned fewer than held: {c.DefinitionSetsKeptLarger}");
            output.WriteLine($"  sets still short of their trophy total: {c.DefinitionSetsStillShort}");
            output.WriteLine($"  trophies fetched: {c.DefinitionsFetched}");
            output.WriteLine($"  sets in the new file: {c.DefinitionSetsNow}");
            output.WriteLine($"Calls: {api.Attempts}, retries: {api.Retries}, new tokens: {api.TokenRenewals}");
            output.WriteLine($"Check build (no -PsnCurrent): games {c.CheckGames}, events {c.CheckEvents}, games with events and no icon in the list {c.CheckGamesWithEventsAndNoIcon}");

            if (c.AwaitingReview > 0)
            {
                output.WriteLine(
                    $"ATTENTION: {c.AwaitingReview} candidate(s) await review; they are not in the game list. " +
                    $"Copy the rows you decide from {PsnTrophyFetcher.ToReviewFile} into your decisions file with a status.");
            }

            if (c.TogetherBlocked > 0)
            {
                output.WriteLine(
                    $"ATTENTION: {c.TogetherBlocked} together pair(s) not added: a trophy set may be in only one game. " +
                    $"See the reason column in {PsnTrophyFetcher.ToReviewFile} and correct the decisions rows.");
            }

            if (c.NotOffered > 0)
            {
                output.WriteLine(
                    $"ATTENTION: {c.NotOffered} together-row pair(s) were not offered as candidates and are not added; see {PsnTrophyFetcher.ToReviewFile}.");
            }

            if (c.TogetherNotReturned + c.TogetherIdsNotReturned > 0)
            {
                output.WriteLine($"ATTENTION: together rows or ids PlayStation does not return; see {PsnTrophyFetcher.TogetherNotReturnedFile}.");
            }

            if (c.DefinitionSetsStillShort > 0)
            {
                output.WriteLine($"ATTENTION: {c.DefinitionSetsStillShort} set(s) still have fewer definitions than trophies; those trophies stay unnamed.");
            }

            if (c.AddedWithoutIcon > 0)
            {
                output.WriteLine("ATTENTION: added games have no icon; the refresh plan refuses any of them with events (-PsnCurrent has none for new games).");
            }

            return Success;
        }

        private static int RunCompare(ParsedArguments parsed, TextWriter output)
        {
            if (parsed.Local is null || parsed.Other is null || parsed.PairA is null)
            {
                throw new UsageException("--local, --other and --pair-a are all needed.");
            }

            var oldJson = ReadInput(parsed.PsnFile!, "old file");
            var newJson = ReadInput(parsed.SecondFile!, "new file");
            var oldActivity = PsnSharedActivityReader.Parse(oldJson, parsed.Local, parsed.Other, parsed.PairA, parsed.GapMinutes);
            var newActivity = PsnSharedActivityReader.Parse(newJson, parsed.Local, parsed.Other, parsed.PairA, parsed.GapMinutes);
            var result = PsnSharedActivityComparer.Compare(oldActivity, oldJson, newActivity, newJson);

            output.WriteLine($"Old file: games {result.OldGames}, events {result.OldEvents}");
            output.WriteLine($"New file: games {result.NewGames}, events {result.NewEvents}");
            output.WriteLine($"Games only in old: {result.GamesOnlyOld}");
            output.WriteLine($"Games only in new: {result.GamesOnlyNew}");
            output.WriteLine($"Games in both, different title: {result.GamesDifferentTitle}");
            output.WriteLine($"Games in both, different icon: {result.GamesDifferentIcon}");
            output.WriteLine($"In both, same level: {result.SameLevelTotal}");
            WriteLevels(output, result.SameLevel);
            output.WriteLine($"In both, different level: {result.DifferentLevelTotal}");

            foreach (var from in MemoriesDatabase.DayLevels)
            {
                foreach (var to in MemoriesDatabase.DayLevels.Where(to => to != from))
                {
                    output.WriteLine($"  {from} -> {to}: {result.DifferentLevel.GetValueOrDefault((from, to))}");
                }
            }

            output.WriteLine($"Only in old: {result.OnlyOldTotal}");
            WriteLevels(output, result.OnlyOld);
            output.WriteLine($"Only in new: {result.OnlyNewTotal}");
            WriteLevels(output, result.OnlyNew);
            output.WriteLine($"In both, same level, different trophies: {result.SameLevelDifferentTrophies}");
            output.WriteLine($"In both, same level, different pairs: {result.SameLevelDifferentPairs}");
            output.WriteLine($"In both, same level, different periods: {result.SameLevelDifferentPeriods}");
            output.WriteLine(result.Same ? "Result: same." : "Result: different.");
            return result.Same ? Success : Different;
        }

        private static void WriteLevels(TextWriter output, Dictionary<string, int> perLevel)
        {
            foreach (var level in MemoriesDatabase.DayLevels)
            {
                output.WriteLine($"  {level}: {perLevel[level]}");
            }
        }

        private static int RunDryRun(ParsedArguments parsed, TextWriter output)
        {
            var activity = ReadPsn(parsed);
            output.WriteLine("Dry run: nothing is written.");
            WriteSummary(output, PsnActivitySummary.From(activity), activity.PeriodGapMinutes);
            return Success;
        }

        private static void WriteSummary(TextWriter output, PsnActivitySummary summary, int gapMinutes)
        {
            output.WriteLine($"Period gap (minutes): {gapMinutes}");
            output.WriteLine($"Games: {summary.GameCount}");
            output.WriteLine($"Records: {summary.RecordCount}");
            output.WriteLine($"Items: {summary.ItemCount}");
            output.WriteLine($"Periods: {summary.PeriodCount}");

            foreach (var label in PeriodLabels.All)
            {
                output.WriteLine($"  {label}: {summary.PeriodsPerLabel[label]}");
            }

            output.WriteLine($"Timeline cards (periods with both accounts): {summary.TimelineCards}");
            output.WriteLine($"Records with no shared period: {summary.RecordsWithNoSharedPeriod}");
            output.WriteLine($"Matching pairs: {summary.PairCount}");
            output.WriteLine($"  not located: {summary.PairsNotLocated}");
            output.WriteLine($"  split across periods: {summary.PairsSplit}");
            output.WriteLine(
                "Records per year: " +
                (summary.RecordsPerYear.Count == 0
                    ? "none"
                    : string.Join(", ", summary.RecordsPerYear.Select(pair =>
                        string.Create(CultureInfo.InvariantCulture, $"{pair.Key}={pair.Value}")))));
            output.WriteLine($"First date: {FormatDate(summary.FirstDate)}");
            output.WriteLine($"Last date: {FormatDate(summary.LastDate)}");
        }

        private static int RunImport(ParsedArguments parsed, TextWriter output, Func<DateTime> utcNow)
        {
            if (parsed.WorkspaceFolder is null)
            {
                throw new UsageException("psn-import needs --workspace-folder.");
            }

            // Read and check the whole file before the memories file is touched.
            var activity = ReadPsn(parsed);
            var databasePath = Path.Combine(parsed.WorkspaceFolder, MemoriesDatabase.FileName);
            var created = false;

            if (!File.Exists(databasePath))
            {
                if (!parsed.Create)
                {
                    throw new InvalidOperationException(
                        "There is no memories file in the workspace folder. Add --create to create it. " +
                        "Nothing has been written.");
                }

                MemoriesDatabase.Create(databasePath, utcNow());
                created = true;
            }

            PsnImportResult result;

            try
            {
                result = new PsnImporter().Import(databasePath, activity, parsed.CollectionName);
            }
            catch when (created)
            {
                // The import rolled back, so the file this run created holds only the schema.
                SqliteConnection.ClearAllPools();
                File.Delete(databasePath);
                throw;
            }

            if (created)
            {
                output.WriteLine("Created the memories file.");
            }

            output.WriteLine("Imported.");
            output.WriteLine($"  Collection created: {(result.CollectionCreated ? "yes" : "no")}");
            output.WriteLine($"  Collection period gap changed: {(result.CollectionUpdated ? "yes" : "no")}");
            output.WriteLine($"  Records added: {result.Added}");
            output.WriteLine($"  Records updated: {result.Updated}");
            output.WriteLine($"  Records unchanged: {result.Unchanged}");
            output.WriteLine($"  Entries missing from this file (kept): {result.MissingFromFile}");
            output.WriteLine($"  Changes: {result.Changes}");
            output.WriteLine();
            WriteSummary(output, PsnActivitySummary.From(activity), activity.PeriodGapMinutes);
            return Success;
        }

        private static int RunEnrich(ParsedArguments parsed, TextWriter output, bool apply)
        {
            if (parsed.WorkspaceFolder is null)
            {
                throw new UsageException($"{parsed.Command} needs --workspace-folder.");
            }

            if (parsed.PsnFile is null)
            {
                throw new UsageException("a definitions file is needed.");
            }

            // Read and check the whole file before the memories file is opened.
            var definitions = PsnTrophyDefinitionsReader.Read(parsed.PsnFile);
            var databasePath = Path.Combine(parsed.WorkspaceFolder, MemoriesDatabase.FileName);

            if (!File.Exists(databasePath))
            {
                throw new InvalidOperationException(
                    "There is no memories file in the workspace folder. Run psn-import first. Nothing has been written.");
            }

            var result = apply
                ? PsnTrophyEnricher.Enrich(databasePath, definitions)
                : PsnTrophyEnricher.DryRun(databasePath, definitions);

            output.WriteLine(apply ? "Enriched." : "Dry run: nothing is written.");

            if (result.Upgraded)
            {
                output.WriteLine(apply
                    ? $"Upgraded the memories file from format {result.FormatVersionBefore} to {MemoriesDatabase.CurrentFormatVersion}."
                    : $"The memories file is format {result.FormatVersionBefore}; psn-enrich upgrades it to {MemoriesDatabase.CurrentFormatVersion}.");
            }
            else
            {
                output.WriteLine($"Memories file format: {result.FormatVersionBefore}");
            }

            output.WriteLine($"Sets: {result.Sets}");
            output.WriteLine($"Definitions: {result.Definitions}");
            output.WriteLine($"  hidden: {result.Hidden}");
            output.WriteLine($"Items with a definition: {result.ItemsWithDefinition}");
            output.WriteLine($"Items without a definition: {result.ItemsWithoutDefinition}");
            output.WriteLine($"Items whose definition has another type: {result.ItemsTypeMismatch}");
            output.WriteLine($"Definitions not stored (type mismatch): {result.TypeMismatches}");
            output.WriteLine($"Definitions added: {result.Added}");
            output.WriteLine($"Definitions updated: {result.Updated}");
            output.WriteLine($"Definitions unchanged: {result.Unchanged}");
            output.WriteLine($"Stored definitions removed (type mismatch): {result.RemovedForMismatch}");
            output.WriteLine($"Changes: {result.Changes}");
            return Success;
        }

        private static int RunFetchImages(ParsedArguments parsed, TextWriter output, Func<IMemoryImageSource> imageSource)
        {
            if (parsed.WorkspaceFolder is null)
            {
                throw new UsageException("fetch-images needs --workspace-folder.");
            }

            var databasePath = Path.Combine(parsed.WorkspaceFolder, MemoriesDatabase.FileName);
            var source = imageSource();

            try
            {
                var result = MemoryImageFetcher.FetchMissing(databasePath, source, parsed.MaxBytes);
                WriteCounts(output, "Game images.", result.GameImages);
                WriteCounts(output, "Trophy icons.", result.TrophyIcons);
                return result.Failed == 0 ? Success : Failed;
            }
            finally
            {
                (source as IDisposable)?.Dispose();
            }
        }

        private static void WriteCounts(TextWriter output, string heading, MemoryImageCounts counts)
        {
            output.WriteLine(heading);
            output.WriteLine($"  Fetched: {counts.Fetched}");
            output.WriteLine($"  Already present: {counts.AlreadyPresent}");
            output.WriteLine($"  Failed: {counts.Failed}");
        }

        private static string FormatDate(DateOnly? value) =>
            value?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? "none";

        private static void WriteHelp(TextWriter output)
        {
            output.WriteLine("MemoryImport: PSN shared-activity build, compare and import into the memories file, trophy definitions, and image fetch.");
            output.WriteLine();
            output.WriteLine("  [psn-dry-run] <psn-file> --local <prefix> --other <prefix> --pair-a <prefix> [--gap <minutes>]");
            output.WriteLine("  psn-import <psn-file> --workspace-folder <folder> --local <prefix> --other <prefix> --pair-a <prefix>");
            output.WriteLine("             [--collection <name>] [--gap <minutes>] [--create]");
            output.WriteLine("  psn-enrich-dry-run --workspace-folder <folder> <definitions-file>");
            output.WriteLine("  psn-enrich --workspace-folder <folder> <definitions-file>");
            output.WriteLine("  fetch-images --workspace-folder <folder> [--max-bytes <n>]");
            output.WriteLine("  psn-build --timestamps <file> [--timestamps <file> ...] --manifest <file> --account-a <name> --account-b <name>");
            output.WriteLine("            (--out <file> [--overwrite] | --dry-run) [--icons-from <shared-activity-file>] [--allow-no-icon]");
            output.WriteLine("  psn-compare <old-file> <new-file> --local <prefix> --other <prefix> --pair-a <prefix>");
            output.WriteLine("  psn-fetch --previous-list <game list> --definitions <definitions file> --decisions <decisions csv>");
            output.WriteLine("            --account-a <name> --account-b <name>");
            output.WriteLine("            --local <a or b name> --out <new folder>");
            output.WriteLine();
            output.WriteLine("psn-build: --account-a is the account whose trophy set is first in every game key; the account names");
            output.WriteLine("are the account field names in the inputs and become the prefixes in the built file.");
            output.WriteLine("psn-compare exits 0 when the files are the same and 3 when they differ.");
            output.WriteLine("psn-fetch asks for the token and the other account's id with hidden input; neither is saved.");
            output.WriteLine("A rerun into the same --out folder continues an unfinished psn-fetch.");
            output.WriteLine("Prefixes are the account field prefixes in the PSN file (case-insensitive).");
            output.WriteLine("--pair-a names which of --local/--other is side a of the matching pairs (first set in gameKey).");
            output.WriteLine("Output is counts and dates only.");
        }

        /// <summary>A command line the tool cannot act on. Messages never quote an argument value.</summary>
        private sealed class UsageException(string message) : Exception(message);

        /// <summary>The parsed command line.</summary>
        private sealed class ParsedArguments
        {
            internal string Command { get; private set; } = "psn-dry-run";

            internal bool Help { get; private set; }

            internal string? PsnFile { get; private set; }

            internal string? WorkspaceFolder { get; private set; }

            internal string? Local { get; private set; }

            internal string? Other { get; private set; }

            internal string? PairA { get; private set; }

            internal string CollectionName { get; private set; } = PsnImporter.DefaultCollectionName;

            internal int GapMinutes { get; private set; } = PsnSharedActivityReader.DefaultPeriodGapMinutes;

            internal long MaxBytes { get; private set; } = MemoryImageFetcher.DefaultMaxBytes;

            internal bool Create { get; private set; }

            internal string? SecondFile { get; private set; }

            internal List<string> Timestamps { get; } = [];

            internal string? Manifest { get; private set; }

            internal string? AccountA { get; private set; }

            internal string? AccountB { get; private set; }

            internal string? Out { get; private set; }

            internal string? IconsFrom { get; private set; }

            internal bool Overwrite { get; private set; }

            internal bool DryRun { get; private set; }

            internal bool AllowNoIcon { get; private set; }

            internal string? PreviousList { get; private set; }

            internal string? DefinitionsFile { get; private set; }

            internal string? DecisionsFile { get; private set; }

            private bool BuildOptionGiven =>
                Timestamps.Count > 0 || Manifest is not null || AccountA is not null || AccountB is not null
                || Out is not null || IconsFrom is not null || Overwrite || DryRun || AllowNoIcon;

            internal static ParsedArguments Parse(string[] args)
            {
                var parsed = new ParsedArguments();
                var index = 0;
                var collectionGiven = false;
                var gapGiven = false;
                var maxBytesGiven = false;

                if (args.Length > 0 && args[0] is "psn-dry-run" or "psn-import" or "psn-enrich-dry-run" or "psn-enrich" or "fetch-images" or "psn-build" or "psn-compare" or "psn-fetch")
                {
                    parsed.Command = args[0];
                    index = 1;
                }

                string NextValue(string option)
                {
                    if (index + 1 >= args.Length)
                    {
                        throw new UsageException($"{option} needs a value.");
                    }

                    index++;
                    return args[index];
                }

                for (; index < args.Length; index++)
                {
                    var argument = args[index];

                    switch (argument)
                    {
                        case "--help" or "-h" or "/?":
                            parsed.Help = true;
                            break;

                        case "--workspace-folder":
                            parsed.WorkspaceFolder = NextValue(argument);
                            break;

                        case "--local":
                            parsed.Local = NextValue(argument);
                            break;

                        case "--other":
                            parsed.Other = NextValue(argument);
                            break;

                        case "--pair-a":
                            parsed.PairA = NextValue(argument);
                            break;

                        case "--collection":
                            var name = NextValue(argument);

                            if (string.IsNullOrWhiteSpace(name))
                            {
                                throw new UsageException("--collection needs a non-empty name.");
                            }

                            parsed.CollectionName = name;
                            collectionGiven = true;
                            break;

                        case "--gap":
                            if (!int.TryParse(NextValue(argument), NumberStyles.None, CultureInfo.InvariantCulture, out var gap) || gap < 1)
                            {
                                throw new UsageException("--gap needs a whole number of minutes, 1 or more.");
                            }

                            parsed.GapMinutes = gap;
                            gapGiven = true;
                            break;

                        case "--max-bytes":
                            if (!long.TryParse(NextValue(argument), NumberStyles.None, CultureInfo.InvariantCulture, out var max) || max < 1)
                            {
                                throw new UsageException("--max-bytes needs a whole number, 1 or more.");
                            }

                            parsed.MaxBytes = max;
                            maxBytesGiven = true;
                            break;

                        case "--create":
                            parsed.Create = true;
                            break;

                        case "--timestamps":
                            parsed.Timestamps.Add(NextValue(argument));
                            break;

                        case "--manifest":
                            parsed.Manifest = NextValue(argument);
                            break;

                        case "--account-a":
                            parsed.AccountA = NextValue(argument);
                            break;

                        case "--account-b":
                            parsed.AccountB = NextValue(argument);
                            break;

                        case "--out":
                            parsed.Out = NextValue(argument);
                            break;

                        case "--icons-from":
                            parsed.IconsFrom = NextValue(argument);
                            break;

                        case "--overwrite":
                            parsed.Overwrite = true;
                            break;

                        case "--dry-run":
                            parsed.DryRun = true;
                            break;

                        case "--allow-no-icon":
                            parsed.AllowNoIcon = true;
                            break;

                        case "--previous-list":
                            parsed.PreviousList = NextValue(argument);
                            break;

                        case "--definitions":
                            parsed.DefinitionsFile = NextValue(argument);
                            break;

                        case "--decisions":
                            parsed.DecisionsFile = NextValue(argument);
                            break;

                        default:
                            if (argument.StartsWith("--", StringComparison.Ordinal))
                            {
                                throw new UsageException($"unknown option at argument {index + 1}.");
                            }

                            if (parsed.PsnFile is null)
                            {
                                parsed.PsnFile = argument;
                            }
                            else if (parsed.Command == "psn-compare" && parsed.SecondFile is null)
                            {
                                parsed.SecondFile = argument;
                            }
                            else
                            {
                                throw new UsageException($"too many input files (argument {index + 1}).");
                            }

                            break;
                    }
                }

                if (parsed.Help)
                {
                    return parsed;
                }

                if (parsed.Command == "psn-fetch")
                {
                    if (parsed.PsnFile is not null || parsed.WorkspaceFolder is not null || parsed.Other is not null
                        || parsed.PairA is not null || collectionGiven || gapGiven || parsed.Create || maxBytesGiven
                        || parsed.Timestamps.Count > 0 || parsed.Manifest is not null || parsed.IconsFrom is not null
                        || parsed.Overwrite || parsed.DryRun || parsed.AllowNoIcon)
                    {
                        throw new UsageException(
                            "psn-fetch takes --previous-list, --definitions, --decisions, --account-a, --account-b, --local and --out only.");
                    }

                    if (parsed.PreviousList is null || parsed.DefinitionsFile is null || parsed.DecisionsFile is null || parsed.AccountA is null
                        || parsed.AccountB is null || parsed.Local is null || parsed.Out is null)
                    {
                        throw new UsageException("psn-fetch needs --previous-list, --definitions, --decisions, --account-a, --account-b, --local and --out.");
                    }

                    return parsed;
                }

                if (parsed.PreviousList is not null || parsed.DefinitionsFile is not null || parsed.DecisionsFile is not null)
                {
                    throw new UsageException("--previous-list, --definitions and --decisions belong to psn-fetch only.");
                }

                if (parsed.Command == "psn-build")
                {
                    if (parsed.PsnFile is not null || parsed.WorkspaceFolder is not null || parsed.Local is not null
                        || parsed.Other is not null || parsed.PairA is not null || collectionGiven || gapGiven
                        || parsed.Create || maxBytesGiven)
                    {
                        throw new UsageException(
                            "psn-build takes --timestamps, --manifest, --account-a, --account-b, --out, --overwrite, --dry-run, --icons-from and --allow-no-icon only.");
                    }

                    if (parsed.Timestamps.Count == 0 || parsed.Manifest is null || parsed.AccountA is null || parsed.AccountB is null)
                    {
                        throw new UsageException("psn-build needs --timestamps, --manifest, --account-a and --account-b.");
                    }

                    if (parsed.DryRun == (parsed.Out is not null))
                    {
                        throw new UsageException("psn-build needs either --out or --dry-run (not both).");
                    }

                    if (parsed.Overwrite && parsed.Out is null)
                    {
                        throw new UsageException("--overwrite needs --out.");
                    }

                    return parsed;
                }

                if (parsed.BuildOptionGiven)
                {
                    throw new UsageException(
                        "--timestamps, --manifest, --account-a, --account-b, --out, --overwrite, --dry-run, --icons-from and --allow-no-icon belong to psn-build only.");
                }

                if (parsed.Command == "psn-compare")
                {
                    if (parsed.WorkspaceFolder is not null || collectionGiven || gapGiven || parsed.Create || maxBytesGiven)
                    {
                        throw new UsageException("psn-compare takes two files, --local, --other and --pair-a only.");
                    }

                    if (parsed.SecondFile is null)
                    {
                        throw new UsageException("psn-compare needs two files: the old one, then the new one.");
                    }

                    return parsed;
                }

                if (parsed.Command is "psn-enrich-dry-run" or "psn-enrich")
                {
                    if (parsed.Local is not null || parsed.Other is not null || parsed.PairA is not null
                        || collectionGiven || gapGiven || parsed.Create || maxBytesGiven)
                    {
                        throw new UsageException($"{parsed.Command} takes --workspace-folder and a definitions file only.");
                    }
                }
                else if (parsed.Command == "fetch-images")
                {
                    if (parsed.PsnFile is not null || parsed.Local is not null || parsed.Other is not null || parsed.PairA is not null
                        || collectionGiven || gapGiven || parsed.Create)
                    {
                        throw new UsageException("fetch-images takes --workspace-folder and --max-bytes only.");
                    }
                }
                else
                {
                    if (maxBytesGiven)
                    {
                        throw new UsageException("--max-bytes belongs to fetch-images only.");
                    }

                    if (parsed.Command == "psn-dry-run" && (parsed.WorkspaceFolder is not null || collectionGiven || parsed.Create))
                    {
                        throw new UsageException("--workspace-folder, --collection and --create belong to psn-import only.");
                    }
                }

                return parsed;
            }
        }
    }
}
