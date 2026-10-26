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
                    _ => RunDryRun(parsed, output),
                };
            }
            catch (UsageException exception)
            {
                error.WriteLine($"Usage error: {exception.Message}");
                return UsageError;
            }
            catch (Exception exception) when (
                exception is FormatException or InvalidOperationException or ArgumentException)
            {
                // These are the library's deliberate refusals, whose messages carry counts and
                // reasons only.
                error.WriteLine($"Stopped: {exception.Message}");
                return Failed;
            }
            catch (Exception exception)
            {
                // Anything else may carry a path or other source detail in its message, so only
                // its type is reported.
                error.WriteLine(
                    $"Stopped: {exception.GetType().Name} (details suppressed because they may " +
                    $"contain a path).");
                return Failed;
            }
        }

        private static int RunDryRun(ParsedArguments parsed, TextWriter output)
        {
            if (parsed.ExportFolders.Count == 0 && parsed.OverlapFolders is null)
            {
                throw new UsageException("dry-run needs at least one export folder or --overlap.");
            }

            output.WriteLine("Dry run: nothing is written.");

            for (var index = 0; index < parsed.ExportFolders.Count; index++)
            {
                var export = OlderExportSource.Load(parsed.ExportFolders[index]);
                var summary = OlderExportSummary.From(export, parsed.FallbackFolder);

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
            }

            if (parsed.OverlapFolders is { } overlapFolders)
            {
                var inner = OlderExportSource.Load(overlapFolders.Inner);
                var outer = OlderExportSource.Load(overlapFolders.Outer);
                var overlap = OlderExportOverlap.Analyse(inner.Messages, outer.Messages);

                output.WriteLine();
                output.WriteLine(
                    $"Overlap: {overlap.NotFoundCount} of {overlap.InnerCount} lines of the first " +
                    $"--overlap export are not found exactly in the second.");
                output.WriteLine(
                    $"  file attached where the second has media omitted (same time and sender): " +
                    $"{overlap.FileAttachedWhereOuterHasMediaOmittedCount}");
                output.WriteLine(
                    $"  identical line, repeated more often than in the second: " +
                    $"{overlap.RepeatedMoreOftenThanInOuterCount}");
                output.WriteLine(
                    $"  same time and sender, text differs only in leading/trailing whitespace: " +
                    $"{overlap.WhitespaceOnlyDifferenceCount}");
                output.WriteLine(
                    $"  same time and sender, text differs otherwise: " +
                    $"{overlap.OtherContentDifferenceCount}");
                output.WriteLine(
                    $"  no line in the second with the same time and sender: " +
                    $"{overlap.NoOuterLineAtTimestampAndSenderCount}");
                output.WriteLine(
                    $"Reverse: {overlap.OuterInInnerPeriodNotFoundInInnerCount} of " +
                    $"{overlap.OuterInInnerPeriodCount} lines of the second export within the " +
                    $"first's period are not found exactly in the first.");
            }

            return Success;
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

        private static void WriteHelp(TextWriter output)
        {
            output.WriteLine("OlderExportImport: dry run, sender listing and import of older WhatsApp exports.");
            output.WriteLine();
            output.WriteLine("  [dry-run] <export-folder>... [--fallback-folder <folder>] [--overlap <inner-folder> <outer-folder>]");
            output.WriteLine("  senders <export-folder>");
            output.WriteLine("  import <export-folder> --workspace-folder <folder> --local <name> --other <name>");
            output.WriteLine("         --position <n> --shown yes|no [--fallback-folder <folder>] [--create]");
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

            internal string? WorkspaceFolder { get; private set; }

            internal string? Local { get; private set; }

            internal string? Other { get; private set; }

            internal int? Position { get; private set; }

            internal bool? Shown { get; private set; }

            internal bool Create { get; private set; }

            internal static ParsedArguments Parse(string[] args)
            {
                var parsed = new ParsedArguments();
                string? parent = null;
                var index = 0;

                if (args.Length > 0 && args[0] is "dry-run" or "senders" or "import")
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

                        case "--create":
                            parsed.Create = true;
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

                if (parsed.Command != "import"
                    && (parsed.WorkspaceFolder ?? parsed.Local ?? parsed.Other) is not null)
                {
                    throw new UsageException(
                        "--workspace-folder, --local and --other belong to import only.");
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
                }

                return parsed;
            }
        }
    }
}
