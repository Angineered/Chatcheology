using System.Globalization;
using System.Text.RegularExpressions;

namespace MsgstoreImport;

/// <summary>The command line of <c>newer plan</c> and <c>newer apply</c> (CH-71 step 3a).</summary>
internal sealed partial class NewerOptions
{
    internal const string Usage = """
        Usage (newer messages, CH-71 step 3a):

          Plan (read-only on the msgstore, the workspace, the newer-messages file and the media folder;
          writes the private manifest; counts only on screen):

            MsgstoreImport newer plan --msgstore <path> --workspace <path> --conversation <ConversationID>
                --chat <chat _id> --local-participant <ParticipantID> --media-root <WhatsApp folder copy>
                --manifest-out <path outside the repository>

          Apply (one transaction into newer-messages.db beside the workspace; refuses if anything differs
          from the plan; a rerun of the same manifest writes nothing):

            MsgstoreImport newer apply --msgstore <path> --workspace <path> --media-root <same folder>
                --manifest <path> --manifest-sha256 <hex> --i-have-a-backup

        Times are converted with the Africa/Johannesburg zone. The media root is the folder msgstore's
        file_path values are relative to (the copy of the phone's WhatsApp folder, holding Media\...).
        Nothing is ever assumed; paths are never printed.
        """;

    internal required bool Apply { get; init; }
    internal required string MsgstorePath { get; init; }
    internal required string WorkspacePath { get; init; }
    internal required string MediaRoot { get; init; }
    internal long ConversationID { get; init; }
    internal long ChatID { get; init; }
    internal long LocalParticipantID { get; init; }
    internal string? ManifestOut { get; init; }
    internal string? ManifestPath { get; init; }
    internal string? ManifestSha256 { get; init; }

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();

    /// <exception cref="OptionsException">The arguments are not a valid newer command.</exception>
    internal static NewerOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("plan" or "apply"))
        {
            throw new OptionsException("newer needs plan or apply.");
        }

        var apply = args[0] == "apply";
        string? msgstore = null, workspace = null, root = null, manifestOut = null, manifest = null, sha = null;
        long? conversation = null, chat = null, local = null;
        var backup = false;

        string Next(ref int index, string name) =>
            index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1])
                ? args[++index]
                : throw new OptionsException($"{name} needs a value.");

        long Id(ref int index, string name) =>
            long.TryParse(Next(ref index, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new OptionsException($"{name} must be a whole, non-negative number.");

        for (var index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--msgstore": msgstore = Next(ref index, "--msgstore"); break;
                case "--workspace": workspace = Next(ref index, "--workspace"); break;
                case "--media-root": root = Next(ref index, "--media-root"); break;
                case "--conversation": conversation = Id(ref index, "--conversation"); break;
                case "--chat": chat = Id(ref index, "--chat"); break;
                case "--local-participant": local = Id(ref index, "--local-participant"); break;
                case "--manifest-out": manifestOut = Next(ref index, "--manifest-out"); break;
                case "--manifest": manifest = Next(ref index, "--manifest"); break;
                case "--manifest-sha256": sha = Next(ref index, "--manifest-sha256"); break;
                case "--i-have-a-backup": backup = true; break;
                default:
                    // The value is not echoed: a mistyped option may be followed by, or be, a path.
                    throw new OptionsException(
                        $"Argument {(index + 2).ToString(CultureInfo.InvariantCulture)} is not a recognised option.");
            }
        }

        if (msgstore is null || workspace is null || root is null)
        {
            throw new OptionsException("newer needs --msgstore, --workspace and --media-root. None is ever assumed.");
        }

        if (!apply)
        {
            if (manifest is not null || sha is not null || backup)
            {
                throw new OptionsException("newer plan takes no --manifest, --manifest-sha256 or --i-have-a-backup.");
            }

            if (conversation is null || chat is null || local is null || manifestOut is null)
            {
                throw new OptionsException(
                    "newer plan needs --conversation, --chat, --local-participant and --manifest-out. None is ever assumed.");
            }

            return new NewerOptions
            {
                Apply = false,
                MsgstorePath = msgstore,
                WorkspacePath = workspace,
                MediaRoot = root,
                ConversationID = conversation.Value,
                ChatID = chat.Value,
                LocalParticipantID = local.Value,
                ManifestOut = manifestOut,
            };
        }

        if (conversation is not null || chat is not null || local is not null || manifestOut is not null)
        {
            throw new OptionsException("newer apply takes the conversation, chat and participant from the manifest, never from the command line.");
        }

        if (manifest is null || sha is null)
        {
            throw new OptionsException("newer apply needs --manifest and --manifest-sha256.");
        }

        if (!Sha256Pattern().IsMatch(sha))
        {
            throw new OptionsException("--manifest-sha256 must be 64 hexadecimal characters.");
        }

        if (!backup)
        {
            throw new OptionsException(
                "--i-have-a-backup is required: back up the workspace folder (newer-messages.db included) first. Nothing was opened.");
        }

        return new NewerOptions
        {
            Apply = true,
            MsgstorePath = msgstore,
            WorkspacePath = workspace,
            MediaRoot = root,
            ManifestPath = manifest,
            ManifestSha256 = sha.ToLowerInvariant(),
        };
    }
}
