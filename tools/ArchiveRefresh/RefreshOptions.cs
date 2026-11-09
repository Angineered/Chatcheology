using System.Globalization;
using System.Text.RegularExpressions;
using Chatcheology.Data.Memories.Psn;

namespace ArchiveRefresh;

/// <summary>A command line the tool cannot act on. Messages never quote an argument value.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>The PSN inputs of a refresh (CH-71 D8), when PSN is included.</summary>
internal sealed record PsnInputs(
    IReadOnlyList<string> Timestamps,
    string Manifest,
    string AccountA,
    string AccountB,
    string Local,
    string Current,
    string Definitions,
    string Collection)
{
    /// <summary>The account that is not <see cref="Local"/>.</summary>
    internal string Other => string.Equals(Local, AccountA, StringComparison.OrdinalIgnoreCase) ? AccountB : AccountA;
}

/// <summary>The command line of <c>plan</c> and <c>apply</c>.</summary>
internal sealed partial class RefreshOptions
{
    internal const string Usage = """
        ArchiveRefresh (CH-71 step 5): the monthly archive refresh. Counts only on screen.

          Plan (the default; read-only on every input; writes only the new private plan folder):

            ArchiveRefresh [plan] --workspace <workspace.db> --msgstore <decrypted msgstore.db> --media-root <WhatsApp folder copy>
                --conversation <ConversationID> --chat <chat _id> --local-participant <ParticipantID>
                --expect-restored <n> --expect-missing <n> --plan-folder <new folder outside the repository>
                [PSN options]

          Apply (backs up, then applies each step in order, then exports, validates and compares the archive):

            ArchiveRefresh apply --workspace <same> --msgstore <same> --media-root <same> [same PSN options]
                --plan-folder <the plan folder> --plan-sha256 <plan SHA-256>
                --backup-root <existing folder for the backup> --archive-out <new archive folder>

          PSN options (all or none; with none, PSN is left out of this refresh):

            --psn-timestamps <fetch output> [--psn-timestamps <another> ...] --psn-manifest <game list>
            --psn-account-a <name> --psn-account-b <name> --psn-local <the a or b name that is the local account>
            --psn-current <current shared-activity file> --psn-definitions <trophy definitions file>
            [--psn-collection <name>]

        Nothing is ever assumed: every path and id is given. Times use Africa/Johannesburg. Paths, names and
        ids are never printed. Apply refuses unless every input is exactly as planned (or as a stopped apply
        left it); rerun the same apply after a stop and the finished steps are skipped.
        """;

    internal required bool Apply { get; init; }
    internal required string WorkspacePath { get; init; }
    internal required string MsgstorePath { get; init; }
    internal required string MediaRoot { get; init; }
    internal required string PlanFolder { get; init; }
    internal long ConversationID { get; init; }
    internal long ChatID { get; init; }
    internal long LocalParticipantID { get; init; }
    internal int ExpectRestored { get; init; }
    internal int ExpectMissing { get; init; }
    internal string? PlanSha256 { get; init; }
    internal string? BackupRoot { get; init; }
    internal string? ArchiveOut { get; init; }
    internal PsnInputs? Psn { get; init; }

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex AccountPattern();

    /// <exception cref="UsageException">The arguments are not a valid command.</exception>
    internal static RefreshOptions Parse(string[] args)
    {
        var index = 0;
        var apply = false;

        if (args.Length > 0 && args[0] is "plan" or "apply")
        {
            apply = args[0] == "apply";
            index = 1;
        }

        string? workspace = null, msgstore = null, root = null, planFolder = null, sha = null, backupRoot = null, archiveOut = null;
        long? conversation = null, chat = null, local = null;
        int? expectRestored = null, expectMissing = null;
        var timestamps = new List<string>();
        string? psnManifest = null, accountA = null, accountB = null, psnLocal = null, psnCurrent = null, definitions = null, collection = null;

        string Next(ref int i, string name) =>
            i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1])
                ? args[++i]
                : throw new UsageException($"{name} needs a value.");

        long Id(ref int i, string name) =>
            long.TryParse(Next(ref i, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new UsageException($"{name} must be a whole, non-negative number.");

        int Count(ref int i, string name) =>
            int.TryParse(Next(ref i, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new UsageException($"{name} must be a whole, non-negative number.");

        string Once(string? current, ref int i, string name) =>
            current is null ? Next(ref i, name) : throw new UsageException($"{name} is given twice.");

        for (; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--workspace": workspace = Once(workspace, ref index, "--workspace"); break;
                case "--msgstore": msgstore = Once(msgstore, ref index, "--msgstore"); break;
                case "--media-root": root = Once(root, ref index, "--media-root"); break;
                case "--plan-folder": planFolder = Once(planFolder, ref index, "--plan-folder"); break;
                case "--conversation": conversation = Id(ref index, "--conversation"); break;
                case "--chat": chat = Id(ref index, "--chat"); break;
                case "--local-participant": local = Id(ref index, "--local-participant"); break;
                case "--expect-restored": expectRestored = Count(ref index, "--expect-restored"); break;
                case "--expect-missing": expectMissing = Count(ref index, "--expect-missing"); break;
                case "--plan-sha256": sha = Once(sha, ref index, "--plan-sha256"); break;
                case "--backup-root": backupRoot = Once(backupRoot, ref index, "--backup-root"); break;
                case "--archive-out": archiveOut = Once(archiveOut, ref index, "--archive-out"); break;
                case "--psn-timestamps": timestamps.Add(Next(ref index, "--psn-timestamps")); break;
                case "--psn-manifest": psnManifest = Once(psnManifest, ref index, "--psn-manifest"); break;
                case "--psn-account-a": accountA = Once(accountA, ref index, "--psn-account-a"); break;
                case "--psn-account-b": accountB = Once(accountB, ref index, "--psn-account-b"); break;
                case "--psn-local": psnLocal = Once(psnLocal, ref index, "--psn-local"); break;
                case "--psn-current": psnCurrent = Once(psnCurrent, ref index, "--psn-current"); break;
                case "--psn-definitions": definitions = Once(definitions, ref index, "--psn-definitions"); break;
                case "--psn-collection": collection = Once(collection, ref index, "--psn-collection"); break;
                default:
                    // The value is not echoed: a mistyped option may be followed by, or be, a path.
                    throw new UsageException(
                        $"Argument {(index + 1).ToString(CultureInfo.InvariantCulture)} is not a recognised option.");
            }
        }

        if (workspace is null || msgstore is null || root is null || planFolder is null)
        {
            throw new UsageException("--workspace, --msgstore, --media-root and --plan-folder are all needed. None is ever assumed.");
        }

        PsnInputs? psn = null;
        var anyPsn = timestamps.Count > 0 || psnManifest is not null || accountA is not null || accountB is not null
                     || psnLocal is not null || psnCurrent is not null || definitions is not null || collection is not null;

        if (anyPsn)
        {
            if (timestamps.Count == 0 || psnManifest is null || accountA is null || accountB is null || psnLocal is null
                || psnCurrent is null || definitions is null)
            {
                throw new UsageException(
                    "PSN needs all of --psn-timestamps, --psn-manifest, --psn-account-a, --psn-account-b, --psn-local, " +
                    "--psn-current and --psn-definitions (or none of them, to leave PSN out).");
            }

            if (!AccountPattern().IsMatch(accountA) || !AccountPattern().IsMatch(accountB)
                || string.Equals(accountA, accountB, StringComparison.OrdinalIgnoreCase))
            {
                throw new UsageException("--psn-account-a and --psn-account-b must be two different names of letters and digits.");
            }

            if (!string.Equals(psnLocal, accountA, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(psnLocal, accountB, StringComparison.OrdinalIgnoreCase))
            {
                throw new UsageException("--psn-local must be the --psn-account-a or the --psn-account-b name.");
            }

            if (collection is not null && string.IsNullOrWhiteSpace(collection))
            {
                throw new UsageException("--psn-collection needs a non-empty name.");
            }

            psn = new PsnInputs(timestamps, psnManifest, accountA, accountB, psnLocal, psnCurrent, definitions,
                collection ?? PsnImporter.DefaultCollectionName);
        }

        if (!apply)
        {
            if (sha is not null || backupRoot is not null || archiveOut is not null)
            {
                throw new UsageException("plan takes no --plan-sha256, --backup-root or --archive-out.");
            }

            if (conversation is null || chat is null || local is null || expectRestored is null || expectMissing is null)
            {
                throw new UsageException(
                    "plan needs --conversation, --chat, --local-participant, --expect-restored and --expect-missing. None is ever assumed.");
            }

            return new RefreshOptions
            {
                Apply = false,
                WorkspacePath = workspace,
                MsgstorePath = msgstore,
                MediaRoot = root,
                PlanFolder = planFolder,
                ConversationID = conversation.Value,
                ChatID = chat.Value,
                LocalParticipantID = local.Value,
                ExpectRestored = expectRestored.Value,
                ExpectMissing = expectMissing.Value,
                Psn = psn,
            };
        }

        if (conversation is not null || chat is not null || local is not null || expectRestored is not null || expectMissing is not null)
        {
            throw new UsageException(
                "apply takes the conversation, chat, participant and expected counts from the plan, never from the command line.");
        }

        if (sha is null || backupRoot is null || archiveOut is null)
        {
            throw new UsageException("apply needs --plan-sha256, --backup-root and --archive-out.");
        }

        if (!Sha256Pattern().IsMatch(sha))
        {
            throw new UsageException("--plan-sha256 must be 64 hexadecimal characters.");
        }

        return new RefreshOptions
        {
            Apply = true,
            WorkspacePath = workspace,
            MsgstorePath = msgstore,
            MediaRoot = root,
            PlanFolder = planFolder,
            PlanSha256 = sha.ToLowerInvariant(),
            BackupRoot = backupRoot,
            ArchiveOut = archiveOut,
            Psn = psn,
        };
    }
}
