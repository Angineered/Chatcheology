using System.Globalization;
using Chatcheology.Core.Models;
using Chatcheology.Data.OlderExports;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Tools.OlderExportImport
{
    /// <summary>
    /// The older-export import tool: <c>dry-run</c> (the default), <c>senders</c> and
    /// <c>import</c>. See the README beside this file for usage.
    /// </summary>
    /// <remarks>
    /// Privacy rule for output: <c>dry-run</c> and <c>import</c> print counts, layouts and dates
    /// only, and label exports by their position on the command line, never by folder or file name.
    /// Sender names are printed only by <c>senders</c>, which has to be asked for by name. Error
    /// output never echoes an argument value (which may be a path or a name) and never passes on a
    /// framework I/O message (which may quote a path).
    /// <para>
    /// The tool only reads export folders. <c>import</c> writes the older-exports file, and creates
    /// it only when <c>--create</c> is given.
    /// </para>
    /// </remarks>
    public static class OlderExportCommand
    {
        /// <summary>Exit code: success.</summary>
        public const int Success = 0;

        /// <summary>Exit code: the operation was refused or failed.</summary>
        public const int Failed = 1;

        /// <summary>Exit code: the command line could not be understood.</summary>
        public const int UsageError = 2;

        private const string DateFormat = "yyyy'-'MM'-'dd' 'HH':'mm";

        /// <summary>Runs the tool.</summary>
        /// <param name="args">The command line.</param>
        /// <param name="output">Where results go.</param>
        /// <param name="error">Where refusals and usage errors go.</param>
        /// <param name="utcNow">The clock, for the import timestamp.</param>
        public static int Run(string[] args, TextWriter output, TextWriter error, Func<DateTime> utcNow)
        {
            ArgumentNullException.ThrowIfNull(args);
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(error);
            ArgumentNullException.ThrowIfNull(utcNow);

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
                    "senders" => RunSenders(parsed, output),
                    "import" => RunImport(parsed, output, utcNow),
                    "migrate" => RunMigrate(parsed, output),
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
        /// The printable reason for a stop. The library's deliberate refusals carry counts and
        /// reasons only, so their message is shown; anything else may carry a path or other source
        /// detail, so only its type is.
        /// </summary>
        private static string DescribeStop(Exception exception) =>
            exception is FormatException or InvalidOperationException or ArgumentException
                ? exception.Message
                : $"{exception.GetType().Name} (details suppressed because they may contain a path).";

        private static int RunDryRun(ParsedArguments parsed, TextWriter output)
        {
            if (parsed.ExportFolders.Count == 0 && parsed.OverlapFolders is null && parsed.CutFolders is null)
            {
                throw new UsageException("dry-run needs at least one export folder or --overlap.");
            }

            output.WriteLine("Dry run: nothing is written.");

            // One export that stops must not hide the others: each is reported on its own, and
            // the exit code says whether anything stopped.
            var anyStopped = false;

            for (var index = 0; index < parsed.ExportFolders.Count; index++)
            {
                OlderExportSummary summary;

                try
                {
                    var export = OlderExportSource.Load(parsed.ExportFolders[index]);
                    summary = OlderExportSummary.From(export, parsed.FallbackFolder);
                }
                catch (Exception exception)
                {
                    anyStopped = true;
                    output.WriteLine();
                    output.WriteLine($"Export {index + 1}: Stopped: {DescribeStop(exception)}");
                    continue;
                }

                output.WriteLine();
                output.WriteLine($"Export {index + 1}");
                output.WriteLine($"  Shape: {DescribeShape(summary.Shape)}");
                output.WriteLine($"  First: {FormatDate(summary.First)}");
                output.WriteLine($"  Last: {FormatDate(summary.Last)}");
                output.WriteLine($"  Messages: {summary.MessageCount}");
                output.WriteLine(
                    "  Messages per year: " +
                    (summary.MessagesPerYear.Count == 0
                        ? "none"
                        : string.Join(
                            ", ",
                            summary.MessagesPerYear.Select(pair =>
                                string.Create(CultureInfo.InvariantCulture, $"{pair.Key}={pair.Value}")))));
                output.WriteLine($"  System lines: {summary.SystemMessageCount}");
                output.WriteLine($"  File attached, file present: {summary.FileAttachedPresentCount}");
                output.WriteLine($"  File attached, file absent: {summary.FileAttachedAbsentCount}");

                if (summary.FileAttachedAbsentButInFallbackCount is { } inFallback)
                {
                    output.WriteLine($"  File attached, absent but in fallback folder: {inFallback}");
                }

                output.WriteLine($"  Media omitted: {summary.MediaOmittedCount}");
                output.WriteLine($"  Distinct senders: {summary.DistinctSenderCount}");
                output.WriteLine($"  Largest message, source lines: {summary.LargestMessageLineCount}");
                output.WriteLine($"  Year supplied by rule: {summary.YearSuppliedByRuleCount}");
            }

            if (parsed.CutFolders is { } cutFolders)
            {
                output.WriteLine();

                try
                {
                    var inner = LoadForOverlap(cutFolders.Inner, "first");
                    var outer = LoadForOverlap(cutFolders.Outer, "second");
                    var cut = OlderExportCut.Find(inner.Messages, outer.Messages);

                    output.WriteLine(
                        $"Cut: the first --cut export's last line occurs {cut.MatchCount} time(s) " +
                        $"exactly in the second (must be 1).");

                    if (cut.IsFound)
                    {
                        output.WriteLine($"  second export messages hidden (up to and including the match): {cut.HiddenCount}");
                        output.WriteLine($"    hidden, dated within the first's period: {cut.HiddenWithinInnerPeriodCount}");
                        output.WriteLine($"    hidden, dated before the first's first message (shown nowhere): {cut.HiddenBeforeInnerPeriodCount}");
                        output.WriteLine($"    hidden, dated after the first's last message (shown nowhere): {cut.HiddenAfterInnerPeriodCount}");
                        output.WriteLine($"  second export messages shown: {cut.ShownCount}");
                        output.WriteLine($"  shown outer lines that duplicate an inner line: {cut.ShownDuplicatingInnerCount} (must be 0)");
                        output.WriteLine($"  shown part first: {FormatDate(cut.ShownFirst)}");
                        output.WriteLine($"  shown part last: {FormatDate(cut.ShownLast)}");
                    }

                    if (!cut.IsFound)
                    {
                        anyStopped = true;
                        output.WriteLine("Cut: Stopped: no cut can be made unless the match count is 1.");
                    }
                    else if (!cut.IsUsable)
                    {
                        anyStopped = true;
                        output.WriteLine(
                            "Cut: Stopped: shown lines would duplicate inner lines, so they would be shown twice.");
                    }
                }
                catch (OverlapStoppedException exception)
                {
                    anyStopped = true;
                    output.WriteLine(
                        $"Cut: Stopped ({exception.Which} --cut export): " +
                        $"{DescribeStop(exception.InnerException!)}");
                }
                catch (ArgumentException exception)
                {
                    anyStopped = true;
                    output.WriteLine($"Cut: Stopped: {DescribeStop(exception)}");
                }
            }

            if (parsed.OverlapFolders is { } overlapFolders)
            {
                output.WriteLine();

                OlderExportOverlapBreakdown overlap;

                try
                {
                    var inner = LoadForOverlap(overlapFolders.Inner, "first");
                    var outer = LoadForOverlap(overlapFolders.Outer, "second");
                    overlap = OlderExportOverlap.Analyse(inner.Messages, outer.Messages);
                }
                catch (OverlapStoppedException exception)
                {
                    output.WriteLine(
                        $"Overlap: Stopped ({exception.Which} --overlap export): " +
                        $"{DescribeStop(exception.InnerException!)}");
                    return Failed;
                }

                output.WriteLine(
                    $"Overlap: {overlap.Forward.NotFoundCount} of {overlap.Forward.ComparedCount} " +
                    $"lines of the first --overlap export are not found exactly in the second.");
                WriteDirection(output, overlap.Forward, "second");

                output.WriteLine(
                    $"Reverse: {overlap.Reverse.NotFoundCount} of {overlap.Reverse.ComparedCount} " +
                    $"lines of the second export within the first's period are not found exactly " +
                    $"in the first.");
                WriteDirection(output, overlap.Reverse, "first");

                if (parsed.OverlapLines)
                {
                    output.WriteLine();
                    output.WriteLine("Unmatched source line numbers (line numbers only):");
                    WriteLineNumbers(output, overlap.Forward, "First export");
                    WriteLineNumbers(output, overlap.Reverse, "Second export");
                }
            }

            return anyStopped ? Failed : Success;
        }

        /// <summary>
        /// Writes one overlap direction: each category with its count split by line kind, then the
        /// clock-shift hints for lines with no counterpart at the same time and sender.
        /// </summary>
        private static void WriteDirection(TextWriter output, OlderExportOverlapDirection direction, string other)
        {
            output.WriteLine("  by category (user text / file attached / media omitted / system):");

            foreach (var (category, label) in CategoryLabels(other))
            {
                output.WriteLine(
                    $"    {label}: {direction.Count(category)} (" +
                    $"{direction.Count(category, OverlapLineKind.UserText)} / " +
                    $"{direction.Count(category, OverlapLineKind.UserFileAttached)} / " +
                    $"{direction.Count(category, OverlapLineKind.UserMediaOmitted)} / " +
                    $"{direction.Count(category, OverlapLineKind.System)})");
            }

            output.WriteLine(
                $"  of the lines with no line at the same time and sender in the {other}:");
            output.WriteLine(
                $"    same sender within +/-1 minute: {direction.SameSenderWithinOneMinuteCount}");
            output.WriteLine(
                $"    identical line within +/-60 minutes: {direction.IdenticalWithinSixtyMinutesCount}");
            output.WriteLine(
                $"    identical line within +/-120 minutes: " +
                $"{direction.IdenticalWithinOneHundredTwentyMinutesCount}");
        }

        /// <summary>
        /// Writes the 1-based source line numbers of one direction's unmatched lines, grouped by
        /// category. Only with <c>--overlap-lines</c>; never text, names or file names.
        /// </summary>
        private static void WriteLineNumbers(TextWriter output, OlderExportOverlapDirection direction, string export)
        {
            foreach (var (category, label) in CategoryLabels("other"))
            {
                var numbers = direction.Unmatched
                    .Where(line => line.Category == category)
                    .Select(line => line.SourceLineStart.ToString(CultureInfo.InvariantCulture))
                    .ToList();

                output.WriteLine(
                    $"  {export}, {label}: {(numbers.Count == 0 ? "none" : string.Join(", ", numbers))}");
            }
        }

        private static (OverlapCategory Category, string Label)[] CategoryLabels(string other) =>
        [
            (OverlapCategory.FileAttachedVersusMediaOmitted,
                "file attached vs media omitted (same time and sender)"),
            (OverlapCategory.RepeatedMoreOften, $"identical line, repeated more often than in the {other}"),
            (OverlapCategory.WhitespaceOnly, "same time and sender, text differs only in leading/trailing whitespace"),
            (OverlapCategory.OtherContent, "same time and sender, text differs otherwise"),
            (OverlapCategory.NoLineAtTimestampAndSender, $"no line in the {other} with the same time and sender"),
        ];

        /// <summary>Loads one of the two overlap exports, marking which one stopped.</summary>
        private static OlderExportSource LoadForOverlap(string folder, string which)
        {
            try
            {
                return OlderExportSource.Load(folder);
            }
            catch (Exception exception)
            {
                throw new OverlapStoppedException(which, exception);
            }
        }

        /// <summary>One of the overlap exports could not be read; carries which one.</summary>
        private sealed class OverlapStoppedException(string which, Exception inner)
            : Exception(null, inner)
        {
            internal string Which { get; } = which;
        }

        private static int RunSenders(ParsedArguments parsed, TextWriter output)
        {
            if (parsed.ExportFolders.Count != 1)
            {
                throw new UsageException("senders takes exactly one export folder.");
            }

            var export = OlderExportSource.Load(parsed.ExportFolders[0]);

            output.WriteLine("Sender names (personal data: for choosing roles only)");

            foreach (var (name, count) in OlderExportSummary.CountBySender(export))
            {
                output.WriteLine($"  {count,8}  {name}");
            }

            return Success;
        }

        private static int RunImport(ParsedArguments parsed, TextWriter output, Func<DateTime> utcNow)
        {
            if (parsed.ExportFolders.Count != 1)
            {
                throw new UsageException("import takes exactly one export folder.");
            }

            var workspaceFolder = parsed.WorkspaceFolder
                ?? throw new UsageException("import needs --workspace-folder.");
            var local = parsed.Local ?? throw new UsageException("import needs --local.");
            var other = parsed.Other ?? throw new UsageException("import needs --other.");
            var position = parsed.Position ?? throw new UsageException("import needs --position.");
            var shown = parsed.Shown ?? throw new UsageException("import needs --shown yes|no.");

            if (string.Equals(local, other, StringComparison.Ordinal))
            {
                throw new UsageException("--local and --other must be different names.");
            }

            var databasePath = Path.Combine(workspaceFolder, OlderExportsDatabase.FileName);

            var export = OlderExportSource.Load(parsed.ExportFolders[0]);
            var now = utcNow();

            var request = new OlderExportImportRequest
            {
                Export = export,
                Roles = new Dictionary<string, OlderExportRole>(StringComparer.Ordinal)
                {
                    [local] = OlderExportRole.Local,
                    [other] = OlderExportRole.Other,
                },
                Position = position,
                Shown = shown,
                FallbackMediaFolder = parsed.FallbackFolder,
                ShownAfter = parsed.ShownAfterFolder is null
                    ? null
                    : OlderExportSource.Load(parsed.ShownAfterFolder),
                ImportedDateTimeUtc = now,
            };

            // Everything that can be refused without the file is refused before it is created,
            // so a refused request never leaves an empty older-exports file behind.
            OlderExportImporter.ValidateRequest(request);

            var created = false;

            if (!File.Exists(databasePath))
            {
                if (!parsed.Create)
                {
                    throw new InvalidOperationException(
                        "There is no older-exports file in the workspace folder. Add --create to " +
                        "create it. Nothing has been written.");
                }

                OlderExportsDatabase.Create(databasePath, now);
                created = true;
            }

            OlderExportImportResult result;

            try
            {
                result = new OlderExportImporter().Import(databasePath, request);
            }
            catch when (created)
            {
                // The import rolled back, so the file this run created holds nothing but the
                // schema. Removing it restores the folder to how it was found.
                SqliteConnection.ClearAllPools();
                File.Delete(databasePath);
                throw;
            }

            if (created)
            {
                output.WriteLine("Created the older-exports file.");
            }

            output.WriteLine("Imported.");
            output.WriteLine($"  Conversation: {result.ConversationID}");
            output.WriteLine($"  Position: {position}, shown: {(shown ? "yes" : "no")}");
            output.WriteLine($"  Messages: {result.MessageCount}");
            output.WriteLine($"  Messages shown: {result.ShownMessageCount}");

            if (result.ShownFromSequenceNumber is { } shownFrom)
            {
                output.WriteLine($"  Shown from message: {shownFrom} (after the cut)");
            }

            output.WriteLine($"  Media omitted attachments (unresolved): {result.MediaOmittedAttachmentCount}");
            output.WriteLine($"  File attached attachments: {result.FileAttachedAttachmentCount}");
            output.WriteLine($"    resolved in own folder: {result.ResolvedInOwnFolderCount}");
            output.WriteLine($"    resolved in fallback folder: {result.ResolvedInFallbackFolderCount}");
            output.WriteLine($"    unresolved: {result.UnresolvedFileAttachedCount}");

            return Success;
        }

        private static string DescribeShape(WhatsAppHeaderShape? shape) => shape switch
        {
            WhatsAppHeaderShape.TimeFirst => "A (HH:mm, d MMM yyyy)",
            WhatsAppHeaderShape.DayFirst => "B (dd/MM/yyyy, HH:mm)",
            WhatsAppHeaderShape.YearFirst => "C (yyyy/MM/dd, HH:mm)",
            _ => "none (no messages)",
        };

        private static string FormatDate(DateTime? value) =>
            value?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? "none";

        /// <summary>
        /// <c>migrate</c> (CH-71): brings the older-exports file from workspace schema version 5 to
        /// the current version through <see cref="OlderExportsDatabase.MigrateToCurrent"/>. A dry
        /// run unless <c>--apply</c>; the dry run opens the file read-only.
        /// </summary>
        private static int RunMigrate(ParsedArguments parsed, TextWriter output)
        {
            if (parsed.ExportFolders.Count != 0)
            {
                throw new UsageException("migrate takes no export folder, only --workspace-folder.");
            }

            var workspaceFolder = parsed.WorkspaceFolder
                ?? throw new UsageException("migrate needs --workspace-folder.");
            var databasePath = Path.Combine(workspaceFolder, OlderExportsDatabase.FileName);

            if (!File.Exists(databasePath))
            {
                throw new InvalidOperationException(
                    "There is no older-exports file in the workspace folder. Nothing was written.");
            }

            int before;

            try
            {
                using var connection = Chatcheology.Data.Workspace.WorkspaceDatabase.OpenReadOnlyConnection(databasePath);
                before = Chatcheology.Data.Workspace.WorkspaceDatabase.ReadSchemaVersion(connection);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }

            var current = Chatcheology.Data.Workspace.WorkspaceDatabase.SchemaVersion;

            output.WriteLine($"older-exports workspace schema version: {before.ToString(CultureInfo.InvariantCulture)}");

            if (!parsed.Apply)
            {
                output.WriteLine(before == current
                    ? "Already current; nothing to migrate."
                    : $"Dry run: nothing was written. Back up the file, close the viewer, then add --apply to migrate it to version {current.ToString(CultureInfo.InvariantCulture)}.");

                return Success;
            }

            bool migrated;

            try
            {
                migrated = OlderExportsDatabase.MigrateToCurrent(databasePath);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }

            output.WriteLine(migrated
                ? $"Migrated to version {current.ToString(CultureInfo.InvariantCulture)} in one transaction."
                : "Already current; nothing was written.");

            return Success;
        }

        private static void WriteHelp(TextWriter output)
        {
            output.WriteLine("OlderExportImport: dry run, sender listing and import of older WhatsApp exports.");
            output.WriteLine();
            output.WriteLine("  [dry-run] <export-folder>... [--fallback-folder <folder>] [--overlap <inner-folder> <outer-folder> [--overlap-lines]]");
            output.WriteLine("            [--cut <inner-folder> <outer-folder>]");
            output.WriteLine("  senders <export-folder>");
            output.WriteLine("  import <export-folder> --workspace-folder <folder> --local <name> --other <name>");
            output.WriteLine("         --position <n> --shown yes|no [--fallback-folder <folder>] [--shown-after <inner-folder>] [--create]");
            output.WriteLine("  migrate --workspace-folder <folder> [--apply]   (workspace schema 5 to the current version; dry run without --apply)");
            output.WriteLine();
            output.WriteLine("  --parent <folder>  export folders (and --overlap/--fallback-folder) are names under this folder.");
            output.WriteLine();
            output.WriteLine("Each export folder holds exactly one .txt (the chat) and its media. Folders are only read.");
            output.WriteLine("dry-run and import print counts only; senders prints names and must be asked for.");
        }

        /// <summary>A command line the tool cannot act on. Messages never quote an argument value.</summary>
        private sealed class UsageException(string message) : Exception(message);

        /// <summary>The parsed command line.</summary>
        private sealed class ParsedArguments
        {
            internal string Command { get; private set; } = "dry-run";

            internal bool Help { get; private set; }

            internal List<string> ExportFolders { get; } = [];

            internal string? FallbackFolder { get; private set; }

            internal (string Inner, string Outer)? OverlapFolders { get; private set; }

            internal (string Inner, string Outer)? CutFolders { get; private set; }

            internal string? ShownAfterFolder { get; private set; }

            internal string? WorkspaceFolder { get; private set; }

            internal string? Local { get; private set; }

            internal string? Other { get; private set; }

            internal int? Position { get; private set; }

            internal bool? Shown { get; private set; }

            internal bool Create { get; private set; }

            internal bool OverlapLines { get; private set; }

            internal bool Apply { get; private set; }

            internal static ParsedArguments Parse(string[] args)
            {
                var parsed = new ParsedArguments();
                string? parent = null;
                var index = 0;

                if (args.Length > 0 && args[0] is "dry-run" or "senders" or "import" or "migrate")
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

                        case "--parent":
                            parent = NextValue(argument);
                            break;

                        case "--fallback-folder":
                            parsed.FallbackFolder = NextValue(argument);
                            break;

                        case "--cut":
                            var cutInner = NextValue(argument);
                            parsed.CutFolders = (cutInner, NextValue(argument));
                            break;

                        case "--shown-after":
                            parsed.ShownAfterFolder = NextValue(argument);
                            break;

                        case "--overlap":
                            var inner = NextValue(argument);
                            parsed.OverlapFolders = (inner, NextValue(argument));
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

                        case "--position":
                            if (!int.TryParse(
                                    NextValue(argument),
                                    NumberStyles.None,
                                    CultureInfo.InvariantCulture,
                                    out var position)
                                || position < 1)
                            {
                                throw new UsageException("--position needs a whole number of 1 or more.");
                            }

                            parsed.Position = position;
                            break;

                        case "--shown":
                            parsed.Shown = NextValue(argument) switch
                            {
                                "yes" => true,
                                "no" => false,
                                _ => throw new UsageException("--shown needs yes or no."),
                            };
                            break;

                        case "--overlap-lines":
                            parsed.OverlapLines = true;
                            break;

                        case "--create":
                            parsed.Create = true;
                            break;

                        case "--apply":
                            parsed.Apply = true;
                            break;

                        default:
                            if (argument.StartsWith("--", StringComparison.Ordinal))
                            {
                                // The option name itself is safe to show only if it is short
                                // and plain; a mistyped value could be anything.
                                throw new UsageException(
                                    $"unknown option at argument {index + 1}.");
                            }

                            parsed.ExportFolders.Add(argument);
                            break;
                    }
                }

                if (parsed.Command != "import" && (parsed.Local ?? parsed.Other) is not null)
                {
                    throw new UsageException("--local and --other belong to import only.");
                }

                if (parsed.Command is not ("import" or "migrate") && parsed.WorkspaceFolder is not null)
                {
                    throw new UsageException("--workspace-folder belongs to import and migrate only.");
                }

                if (parsed.Command != "migrate" && parsed.Apply)
                {
                    throw new UsageException("--apply belongs to migrate only.");
                }

                if (parsed.Command == "migrate"
                    && (parsed.Position is not null || parsed.Shown is not null || parsed.Create
                        || parsed.FallbackFolder is not null || parsed.OverlapFolders is not null
                        || parsed.CutFolders is not null || parsed.ShownAfterFolder is not null
                        || parsed.OverlapLines || parent is not null))
                {
                    throw new UsageException("migrate takes only --workspace-folder and --apply.");
                }

                if (parsed.OverlapLines && parsed.OverlapFolders is null)
                {
                    throw new UsageException("--overlap-lines needs --overlap.");
                }

                if (parsed.Command != "import" && parsed.ShownAfterFolder is not null)
                {
                    throw new UsageException("--shown-after belongs to import only.");
                }

                if (parsed.Command == "import" && parsed.CutFolders is not null)
                {
                    throw new UsageException("--cut belongs to dry-run only.");
                }

                if (parent is not null)
                {
                    for (var i = 0; i < parsed.ExportFolders.Count; i++)
                    {
                        parsed.ExportFolders[i] = Path.Combine(parent, parsed.ExportFolders[i]);
                    }

                    if (parsed.FallbackFolder is not null)
                    {
                        parsed.FallbackFolder = Path.Combine(parent, parsed.FallbackFolder);
                    }

                    if (parsed.OverlapFolders is { } overlap)
                    {
                        parsed.OverlapFolders =
                            (Path.Combine(parent, overlap.Inner), Path.Combine(parent, overlap.Outer));
                    }

                    if (parsed.CutFolders is { } cut)
                    {
                        parsed.CutFolders =
                            (Path.Combine(parent, cut.Inner), Path.Combine(parent, cut.Outer));
                    }

                    if (parsed.ShownAfterFolder is not null)
                    {
                        parsed.ShownAfterFolder = Path.Combine(parent, parsed.ShownAfterFolder);
                    }
                }

                return parsed;
            }
        }
    }
}
