using System.Globalization;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Psn;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Tools.MemoryImport
{
    /// <summary>
    /// The memories import tool: <c>psn-dry-run</c> (the default), <c>psn-import</c>,
    /// <c>psn-enrich-dry-run</c>, <c>psn-enrich</c> and <c>fetch-images</c>. See the README beside
    /// this file for usage.
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
            Func<IMemoryImageSource> imageSource)
        {
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

        private static PsnActivity ReadPsn(ParsedArguments parsed)
        {
            if (parsed.PsnFile is null)
            {
                throw new UsageException("a PSN file is needed.");
            }

            if (parsed.Local is null || parsed.Other is null || parsed.PairA is null)
            {
                throw new UsageException("--local, --other and --pair-a are all needed.");
            }

            return PsnSharedActivityReader.Read(parsed.PsnFile, parsed.Local, parsed.Other, parsed.PairA, parsed.GapMinutes);
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
            output.WriteLine("MemoryImport: PSN shared-activity import into the memories file, trophy definitions, and image fetch.");
            output.WriteLine();
            output.WriteLine("  [psn-dry-run] <psn-file> --local <prefix> --other <prefix> --pair-a <prefix> [--gap <minutes>]");
            output.WriteLine("  psn-import <psn-file> --workspace-folder <folder> --local <prefix> --other <prefix> --pair-a <prefix>");
            output.WriteLine("             [--collection <name>] [--gap <minutes>] [--create]");
            output.WriteLine("  psn-enrich-dry-run --workspace-folder <folder> <definitions-file>");
            output.WriteLine("  psn-enrich --workspace-folder <folder> <definitions-file>");
            output.WriteLine("  fetch-images --workspace-folder <folder> [--max-bytes <n>]");
            output.WriteLine();
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

            internal static ParsedArguments Parse(string[] args)
            {
                var parsed = new ParsedArguments();
                var index = 0;
                var collectionGiven = false;
                var gapGiven = false;
                var maxBytesGiven = false;

                if (args.Length > 0 && args[0] is "psn-dry-run" or "psn-import" or "psn-enrich-dry-run" or "psn-enrich" or "fetch-images")
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

                        default:
                            if (argument.StartsWith("--", StringComparison.Ordinal))
                            {
                                throw new UsageException($"unknown option at argument {index + 1}.");
                            }

                            if (parsed.PsnFile is not null)
                            {
                                throw new UsageException($"only one input file is accepted (argument {index + 1}).");
                            }

                            parsed.PsnFile = argument;
                            break;
                    }
                }

                if (parsed.Help)
                {
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
