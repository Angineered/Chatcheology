using System.Globalization;
using System.Text.RegularExpressions;

namespace MsgstoreImport;

internal sealed class OptionsException(string message) : Exception(message);

internal enum Mode
{
    Plan,
    Apply,
    UndoBatch,
    Probe,
    ExplainNoDate,
    ReviewList,
    ListUnresolved,
    AddSource,
    ProbeLinks,
    Migrate,
    ProbeText,
}

internal sealed partial class Options
{
    internal const string Usage = """
        Usage:

          Plan (read-only on both databases; writes the private manifest):

            MsgstoreImport plan --msgstore <path> --workspace <path> --conversation <ConversationID>
                --chat <chat _id> --utc-offset +HH:MM --local-participant <ParticipantID>
                --manifest-out <path outside the repository>
                [--rule minute|day-sequence|outside-candidate-set|documents|facts|text-facts|caption-facts]
                [--sample-out <path outside the repository> --seed <int> [--sample-size <n>]]

            --rule defaults to minute (the census rule). --sample-out applies to the other rules.

          Apply (writes one Phase 7 confirmation per manifest row):

            MsgstoreImport apply --msgstore <path> --workspace <path> --manifest <path>
                --manifest-sha256 <hex> --i-have-a-backup

          Undo a batch (revokes this batch's confirmations that are still current):

            MsgstoreImport undo-batch --workspace <path> --manifest <path>
                --manifest-sha256 <hex> --i-have-a-backup

          Migrate a schema version 5 workspace to version 6 (dry run unless --apply; back up first):

            MsgstoreImport migrate --workspace <path> [--apply]

          Plan msgstore facts (CH-8, CH-10: forwarded status and low-quality copies; read-only):

            MsgstoreImport plan --rule facts --msgstore ... --workspace ... --conversation ... --chat ...
                --utc-offset ... --local-participant ... --manifest-out <path outside the repository>

            apply and undo-batch take the facts manifest as usual; undo-batch removes the batch's rows.

          Plan text-message facts (CH-22: forwarded status of text messages; read-only):

            MsgstoreImport plan --rule text-facts --msgstore ... --workspace ... --conversation ... --chat ...
                --utc-offset ... --local-participant ... --manifest-out <path outside the repository>

            apply and undo-batch take the text-facts manifest as usual; undo-batch removes the batch's rows.

          Plan picture captions (CH-63, CH-71: captions from the msgstore, own or the type-12 partner's;
          read-only; counts only, never the captions):

            MsgstoreImport plan --rule caption-facts --msgstore ... --workspace ... --conversation ... --chat ...
                --utc-offset ... --local-participant ... --manifest-out <path outside the repository>

            apply and undo-batch take the caption-facts manifest as usual; undo-batch removes the batch's rows.

          Probe one minute in detail (read-only; writes a private file):

            MsgstoreImport probe --msgstore <path> --workspace <path> --conversation <ConversationID>
                --chat <chat _id> --utc-offset +HH:MM --local-participant <ParticipantID>
                --date YYYY-MM-DD --time HH:MM --probe-out <path outside the repository>

          Probe how the msgstore records forwarding (CH-10) and linked HD / lower-quality copies (CH-8)
          (read-only on both databases; counts and schema names only, on stdout; writes nothing):

            MsgstoreImport probe-links --msgstore <path> --workspace <path> --conversation <ConversationID>
                --chat <chat _id> --utc-offset +HH:MM --local-participant <ParticipantID>
                [--minute YYYY-MM-DDTHH:MM]

          Probe how well workspace text messages pair to msgstore messages (CH-22; read-only on both
          databases; counts only, on stdout; writes nothing):

            MsgstoreImport probe-text --msgstore <path> --workspace <path> --conversation <ConversationID>
                --chat <chat _id> --utc-offset +HH:MM --local-participant <ParticipantID>

          Explain the outside rule's "no naming-derived date" exclusions (read-only; private detail file):

            MsgstoreImport explain-nodate --msgstore <path> --workspace <path> --conversation <ConversationID>
                --chat <chat _id> --utc-offset +HH:MM --local-participant <ParticipantID>
                --detail-out <path outside the repository>

          Write the name+size review list (read-only; private file for Angie to mark R / W / ?):

            MsgstoreImport review-list --rule namesize --msgstore <path> --workspace <path>
                --conversation <ConversationID> --chat <chat _id> --utc-offset +HH:MM
                --local-participant <ParticipantID> --review-out <path outside the repository>

          Plan the rows Angie marked R (apply then also needs --review-file):

            MsgstoreImport plan --rule namesize-reviewed --review-file <marked list> [--review-sha256 <hex>]
                --msgstore ... --workspace ... --conversation ... --chat ... --utc-offset ...
                --local-participant ... --manifest-out <path outside the repository>

          Plan recording every unresolved, unreviewed attachment as missing (CH-13; read-only):

            MsgstoreImport plan --rule review-missing --expected-count <n> --msgstore ... --workspace ...
                --conversation ... --chat ... --utc-offset ... --local-participant ...
                --manifest-out <path outside the repository>

            Refuses, writing nothing, unless exactly <n> attachments qualify. Apply as usual; each row
            is recorded as ReviewedInsufficientEvidence. undo-batch cannot withdraw reviews.

          List every unresolved attachment and why (read-only; private file):

            MsgstoreImport list-unresolved --msgstore <path> --workspace <path> --conversation <ConversationID>
                --chat <chat _id> --utc-offset +HH:MM --local-participant <ParticipantID>
                --list-out <path outside the repository>

          Add a media folder to the workspace (dry run unless --apply; source type WhatsAppMediaDirectory):

            MsgstoreImport add-source --workspace <path> --root <folder> --display-name <text>
                [--device <text>] [--apply]

            The dry run walks the folder and prints counts; the workspace is not opened. --apply
            inventories the folder in one transaction, then hashes its files. Back up first.

          This help:

            MsgstoreImport -h | --help

        Nothing is ever assumed: no default offset, conversation, chat or participant. Output is
        counts only; identifiers go only to the private manifest.
        """;

    internal required Mode Mode { get; init; }
    internal string? MsgstorePath { get; init; }
    internal required string WorkspacePath { get; init; }
    internal PairingParameters? Parameters { get; init; }
    internal string? ManifestOut { get; init; }
    internal string? ManifestPath { get; init; }
    internal string? ManifestSha256 { get; init; }
    internal bool HaveBackup { get; init; }
    internal DateTime? ProbeMinute { get; init; }
    internal string? ProbeOut { get; init; }
    internal string? DetailOut { get; init; }
    internal string? ListOut { get; init; }
    internal PairingRule Rule { get; init; } = PairingRule.Minute;
    internal string? SampleOut { get; init; }
    internal int Seed { get; init; }
    internal int SampleSize { get; init; } = 20;
    internal string? ReviewOut { get; init; }
    internal string? ReviewFile { get; init; }
    internal string? ReviewSha256 { get; init; }
    internal int? ExpectedCount { get; init; }
    internal string? SourceRoot { get; init; }
    internal string? SourceDisplayName { get; init; }
    internal string? SourceDevice { get; init; }
    internal bool ApplySource { get; init; }
    internal DateTime? LinksMinute { get; init; }

    [GeneratedRegex(@"^([+-])(\d{2}):(\d{2})$")]
    private static partial Regex OffsetPattern();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();

    internal static Options Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new OptionsException("A mode is required: plan, apply, undo-batch, migrate, probe, probe-links, probe-text, explain-nodate, review-list, list-unresolved or add-source.");
        }

        var mode = args[0] switch
        {
            "plan" => Mode.Plan,
            "apply" => Mode.Apply,
            "undo-batch" => Mode.UndoBatch,
            "probe" => Mode.Probe,
            "explain-nodate" => Mode.ExplainNoDate,
            "review-list" => Mode.ReviewList,
            "list-unresolved" => Mode.ListUnresolved,
            "add-source" => Mode.AddSource,
            "probe-links" => Mode.ProbeLinks,
            "migrate" => Mode.Migrate,
            "probe-text" => Mode.ProbeText,
            _ => throw new OptionsException("The first argument must be plan, apply, undo-batch, migrate, probe, probe-links, probe-text, explain-nodate, review-list, list-unresolved or add-source."),
        };

        string? msgstore = null, workspace = null, manifestOut = null, manifest = null, sha = null;
        long? conversation = null, chat = null, local = null;
        TimeSpan? offset = null;
        var backup = false;
        string? probeOut = null;
        string? detailOut = null;
        string? listOut = null;
        PairingRule? rule = null;
        var namesizeRule = false;
        string? reviewOut = null, reviewFile = null, reviewSha = null;
        string? sampleOut = null;
        int? seed = null, sampleSize = null, expectedCount = null;
        DateOnly? date = null;
        TimeOnly? time = null;
        string? root = null, displayName = null, device = null;
        var applySource = false;
        DateTime? linksMinute = null;

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
                case "--conversation": conversation = Id(ref index, "--conversation"); break;
                case "--chat": chat = Id(ref index, "--chat"); break;
                case "--local-participant": local = Id(ref index, "--local-participant"); break;
                case "--manifest-out": manifestOut = Next(ref index, "--manifest-out"); break;
                case "--manifest": manifest = Next(ref index, "--manifest"); break;
                case "--manifest-sha256": sha = Next(ref index, "--manifest-sha256"); break;
                case "--i-have-a-backup": backup = true; break;
                case "--probe-out": probeOut = Next(ref index, "--probe-out"); break;
                case "--detail-out": detailOut = Next(ref index, "--detail-out"); break;
                case "--list-out": listOut = Next(ref index, "--list-out"); break;
                case "--sample-out": sampleOut = Next(ref index, "--sample-out"); break;
                case "--review-out": reviewOut = Next(ref index, "--review-out"); break;
                case "--review-file": reviewFile = Next(ref index, "--review-file"); break;
                case "--review-sha256": reviewSha = Next(ref index, "--review-sha256"); break;
                case "--root": root = Next(ref index, "--root"); break;
                case "--display-name": displayName = Next(ref index, "--display-name"); break;
                case "--device": device = Next(ref index, "--device"); break;
                case "--apply": applySource = true; break;

                case "--rule" when index + 1 < args.Length && args[index + 1] == "namesize":
                    namesizeRule = true;
                    index++;
                    break;

                case "--rule":
                    rule = Next(ref index, "--rule") switch
                    {
                        "namesize-reviewed" => PairingRule.NameSizeReviewed,
                        "minute" => PairingRule.Minute,
                        "day-sequence" => PairingRule.DaySequence,
                        "outside-candidate-set" => PairingRule.OutsideCandidateSet,
                        "documents" => PairingRule.Documents,
                        "review-missing" => PairingRule.ReviewMissing,
                        "facts" => PairingRule.Facts,
                        "text-facts" => PairingRule.TextFacts,
                        "caption-facts" => PairingRule.CaptionFacts,
                        _ => throw new OptionsException("--rule must be minute, day-sequence, outside-candidate-set, documents, namesize-reviewed, review-missing, facts, text-facts or caption-facts."),
                    };
                    break;

                case "--expected-count":
                    expectedCount = int.TryParse(Next(ref index, "--expected-count"), NumberStyles.None, CultureInfo.InvariantCulture, out var e)
                        ? e
                        : throw new OptionsException("--expected-count must be a whole, non-negative number.");
                    break;

                case "--seed":
                    seed = int.TryParse(Next(ref index, "--seed"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var s)
                        ? s
                        : throw new OptionsException("--seed must be a whole number.");
                    break;

                case "--sample-size":
                    sampleSize = int.TryParse(Next(ref index, "--sample-size"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
                        ? n
                        : throw new OptionsException("--sample-size must be a positive whole number.");
                    break;

                case "--date":
                    date = DateOnly.TryParseExact(Next(ref index, "--date"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var d)
                        ? d
                        : throw new OptionsException("--date must look like 2024-01-31.");
                    break;

                case "--time":
                    time = TimeOnly.TryParseExact(Next(ref index, "--time"), "HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var t)
                        ? t
                        : throw new OptionsException("--time must look like 14:05 (24-hour, local).");
                    break;

                case "--minute":
                    linksMinute = DateTime.TryParseExact(Next(ref index, "--minute"), "yyyy-MM-dd'T'HH:mm",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var lm)
                        ? lm
                        : throw new OptionsException("--minute must look like 2024-01-31T14:05 (local, 24-hour).");
                    break;

                case "--utc-offset":
                    offset = TryParseOffset(Next(ref index, "--utc-offset"), out var parsed)
                        ? parsed
                        : throw new OptionsException("--utc-offset must look like +02:00 or -05:30, within -14:00 .. +14:00.");
                    break;

                default:
                    // The value is not echoed: a mistyped option may be followed by, or be, a path.
                    throw new OptionsException(
                        $"Argument {(index + 1).ToString(CultureInfo.InvariantCulture)} is not a recognised option.");
            }
        }

        if (workspace is null)
        {
            throw new OptionsException("--workspace is required and is never assumed.");
        }

        if (mode == Mode.Migrate)
        {
            if (msgstore is not null || conversation is not null || chat is not null || offset is not null || local is not null
                || manifest is not null || sha is not null || backup || manifestOut is not null || probeOut is not null
                || detailOut is not null || listOut is not null || date is not null || time is not null || rule is not null
                || namesizeRule || sampleOut is not null || seed is not null || sampleSize is not null || reviewOut is not null
                || reviewFile is not null || reviewSha is not null || expectedCount is not null || root is not null
                || displayName is not null || device is not null || linksMinute is not null)
            {
                throw new OptionsException("migrate takes only --workspace and --apply.");
            }

            return new Options { Mode = mode, WorkspacePath = workspace, ApplySource = applySource };
        }

        if (expectedCount is not null && !(mode == Mode.Plan && rule == PairingRule.ReviewMissing))
        {
            throw new OptionsException("--expected-count applies only to plan --rule review-missing.");
        }

        if (mode == Mode.Plan && rule == PairingRule.ReviewMissing && expectedCount is null)
        {
            throw new OptionsException(
                "plan --rule review-missing needs --expected-count: the number of attachments you expect to " +
                "record as missing. It is never assumed.");
        }

        if (mode != Mode.ProbeLinks && linksMinute is not null)
        {
            throw new OptionsException("--minute applies only to probe-links.");
        }

        if (mode == Mode.ProbeText)
        {
            if (manifest is not null || sha is not null || backup || manifestOut is not null || probeOut is not null
                || detailOut is not null || listOut is not null || date is not null || time is not null || rule is not null
                || namesizeRule || sampleOut is not null || seed is not null || sampleSize is not null || reviewOut is not null
                || reviewFile is not null || reviewSha is not null || expectedCount is not null || root is not null
                || displayName is not null || device is not null || applySource)
            {
                throw new OptionsException("probe-text takes only the plan inputs. It writes no file.");
            }

            if (msgstore is null || conversation is null || chat is null || offset is null || local is null)
            {
                throw new OptionsException(
                    "probe-text needs --msgstore, --workspace, --conversation, --chat, --utc-offset and " +
                    "--local-participant. None is ever assumed.");
            }

            return new Options
            {
                Mode = mode,
                MsgstorePath = msgstore,
                WorkspacePath = workspace,
                Parameters = new PairingParameters(conversation.Value, chat.Value, offset.Value, local.Value),
            };
        }

        if (mode == Mode.ProbeLinks)
        {
            if (manifest is not null || sha is not null || backup || manifestOut is not null || probeOut is not null
                || detailOut is not null || listOut is not null || date is not null || time is not null || rule is not null
                || namesizeRule || sampleOut is not null || seed is not null || sampleSize is not null || reviewOut is not null
                || reviewFile is not null || reviewSha is not null || expectedCount is not null || root is not null
                || displayName is not null || device is not null || applySource)
            {
                throw new OptionsException("probe-links takes only the plan inputs and an optional --minute. It writes no file.");
            }

            if (msgstore is null || conversation is null || chat is null || offset is null || local is null)
            {
                throw new OptionsException(
                    "probe-links needs --msgstore, --workspace, --conversation, --chat, --utc-offset and " +
                    "--local-participant. None is ever assumed.");
            }

            return new Options
            {
                Mode = mode,
                MsgstorePath = msgstore,
                WorkspacePath = workspace,
                Parameters = new PairingParameters(conversation.Value, chat.Value, offset.Value, local.Value),
                LinksMinute = linksMinute,
            };
        }

        if (mode != Mode.AddSource && (root is not null || displayName is not null || device is not null || applySource))
        {
            throw new OptionsException("--root, --display-name, --device and --apply apply only to add-source (and --apply to migrate).");
        }

        if (mode == Mode.AddSource)
        {
            if (msgstore is not null || conversation is not null || chat is not null || offset is not null || local is not null
                || manifest is not null || sha is not null || backup || manifestOut is not null || probeOut is not null
                || detailOut is not null || listOut is not null || date is not null || time is not null || rule is not null
                || namesizeRule || sampleOut is not null || seed is not null || sampleSize is not null || reviewOut is not null
                || reviewFile is not null || reviewSha is not null)
            {
                throw new OptionsException("add-source takes only --workspace, --root, --display-name, --device and --apply.");
            }

            if (root is null || displayName is null)
            {
                throw new OptionsException("add-source needs --workspace, --root and --display-name. None is ever assumed.");
            }

            return new Options
            {
                Mode = mode,
                WorkspacePath = workspace,
                SourceRoot = root,
                SourceDisplayName = displayName,
                SourceDevice = device,
                ApplySource = applySource,
            };
        }

        if (mode != Mode.ListUnresolved && listOut is not null)
        {
            throw new OptionsException("--list-out applies only to list-unresolved.");
        }

        if (mode == Mode.ListUnresolved)
        {
            if (manifest is not null || sha is not null || backup || manifestOut is not null || probeOut is not null
                || detailOut is not null || date is not null || time is not null || rule is not null || namesizeRule
                || sampleOut is not null || seed is not null || sampleSize is not null || reviewOut is not null
                || reviewFile is not null || reviewSha is not null)
            {
                throw new OptionsException("list-unresolved takes only the plan inputs and --list-out.");
            }

            if (msgstore is null || conversation is null || chat is null || offset is null || local is null || listOut is null)
            {
                throw new OptionsException(
                    "list-unresolved needs --msgstore, --workspace, --conversation, --chat, --utc-offset, " +
                    "--local-participant and --list-out. None is ever assumed.");
            }

            return new Options
            {
                Mode = mode,
                MsgstorePath = msgstore,
                WorkspacePath = workspace,
                Parameters = new PairingParameters(conversation.Value, chat.Value, offset.Value, local.Value),
                ListOut = listOut,
            };
        }

        if (reviewSha is not null && !Sha256Pattern().IsMatch(reviewSha))
        {
            throw new OptionsException("--review-sha256 must be 64 hexadecimal characters.");
        }

        if (mode != Mode.ReviewList && (reviewOut is not null || namesizeRule))
        {
            throw new OptionsException("--review-out and --rule namesize apply only to review-list.");
        }

        if (mode == Mode.ReviewList)
        {
            if (!namesizeRule || rule is not null || manifest is not null || sha is not null || backup || manifestOut is not null
                || probeOut is not null || detailOut is not null || date is not null || time is not null || sampleOut is not null
                || seed is not null || sampleSize is not null || reviewFile is not null || reviewSha is not null)
            {
                throw new OptionsException("review-list takes --rule namesize, the plan inputs and --review-out only.");
            }

            if (msgstore is null || conversation is null || chat is null || offset is null || local is null || reviewOut is null)
            {
                throw new OptionsException(
                    "review-list needs --rule namesize, --msgstore, --workspace, --conversation, --chat, --utc-offset, " +
                    "--local-participant and --review-out. None is ever assumed.");
            }

            return new Options
            {
                Mode = mode,
                MsgstorePath = msgstore,
                WorkspacePath = workspace,
                Parameters = new PairingParameters(conversation.Value, chat.Value, offset.Value, local.Value),
                ReviewOut = reviewOut,
            };
        }

        if (!(mode == Mode.Plan && rule == PairingRule.NameSizeReviewed) && reviewSha is not null)
        {
            throw new OptionsException("--review-sha256 applies only to plan --rule namesize-reviewed.");
        }

        if (reviewFile is not null && !(mode == Mode.Apply || (mode == Mode.Plan && rule == PairingRule.NameSizeReviewed)))
        {
            throw new OptionsException("--review-file applies only to plan --rule namesize-reviewed and to apply.");
        }

        if (mode == Mode.Plan && rule == PairingRule.NameSizeReviewed && (reviewFile is null || reviewSha is null))
        {
            throw new OptionsException(
                "plan --rule namesize-reviewed needs --review-file (the marked review list) and --review-sha256 " +
                "(the SHA-256 review-list printed).");
        }

        if (mode != Mode.ExplainNoDate && detailOut is not null)
        {
            throw new OptionsException("--detail-out applies only to explain-nodate.");
        }

        if (mode == Mode.ExplainNoDate)
        {
            if (manifest is not null || sha is not null || backup || manifestOut is not null || probeOut is not null
                || date is not null || time is not null || rule is not null || sampleOut is not null || seed is not null
                || sampleSize is not null)
            {
                throw new OptionsException("explain-nodate takes only the plan inputs and --detail-out.");
            }

            if (msgstore is null || workspace is null || conversation is null || chat is null || offset is null
                || local is null || detailOut is null)
            {
                throw new OptionsException(
                    "explain-nodate needs --msgstore, --workspace, --conversation, --chat, --utc-offset, " +
                    "--local-participant and --detail-out. None is ever assumed.");
            }

            return new Options
            {
                Mode = mode,
                MsgstorePath = msgstore,
                WorkspacePath = workspace,
                Parameters = new PairingParameters(conversation.Value, chat.Value, offset.Value, local.Value),
                DetailOut = detailOut,
            };
        }

        if (mode != Mode.Plan && (rule is not null || sampleOut is not null || seed is not null || sampleSize is not null))
        {
            throw new OptionsException("--rule, --sample-out, --seed and --sample-size apply only to plan.");
        }

        if (mode == Mode.Plan)
        {
            if (sampleOut is not null && rule is not (PairingRule.DaySequence or PairingRule.OutsideCandidateSet or PairingRule.Documents))
            {
                throw new OptionsException("--sample-out applies only to plan --rule day-sequence, outside-candidate-set or documents.");
            }

            if (sampleOut is null && (seed is not null || sampleSize is not null))
            {
                throw new OptionsException("--seed and --sample-size only apply with --sample-out.");
            }

            if (sampleOut is not null && seed is null)
            {
                throw new OptionsException("--seed is required with --sample-out, so a sample can be reproduced.");
            }
        }

        if (mode != Mode.Probe && (probeOut is not null || date is not null || time is not null))
        {
            throw new OptionsException("--probe-out, --date and --time apply only to probe.");
        }

        if (mode == Mode.Probe)
        {
            if (manifest is not null || sha is not null || backup || manifestOut is not null)
            {
                throw new OptionsException("probe takes no manifest options and no --i-have-a-backup.");
            }

            if (msgstore is null || conversation is null || chat is null || offset is null || local is null
                || date is null || time is null || probeOut is null)
            {
                throw new OptionsException(
                    "probe needs --msgstore, --workspace, --conversation, --chat, --utc-offset, " +
                    "--local-participant, --date, --time and --probe-out. None is ever assumed.");
            }

            return new Options
            {
                Mode = mode,
                MsgstorePath = msgstore,
                WorkspacePath = workspace,
                Parameters = new PairingParameters(conversation.Value, chat.Value, offset.Value, local.Value),
                ProbeMinute = date.Value.ToDateTime(time.Value),
                ProbeOut = probeOut,
            };
        }

        if (mode == Mode.Plan)
        {
            if (manifest is not null || sha is not null || backup)
            {
                throw new OptionsException("plan takes no --manifest, --manifest-sha256 or --i-have-a-backup.");
            }

            if (msgstore is null || conversation is null || chat is null || offset is null || local is null || manifestOut is null)
            {
                throw new OptionsException(
                    "plan needs --msgstore, --workspace, --conversation, --chat, --utc-offset, " +
                    "--local-participant and --manifest-out. None is ever assumed.");
            }

            return new Options
            {
                Mode = mode,
                MsgstorePath = msgstore,
                WorkspacePath = workspace,
                Parameters = new PairingParameters(conversation.Value, chat.Value, offset.Value, local.Value),
                ManifestOut = manifestOut,
                Rule = rule ?? PairingRule.Minute,
                SampleOut = sampleOut,
                Seed = seed ?? 0,
                SampleSize = sampleSize ?? 20,
                ReviewFile = reviewFile,
                ReviewSha256 = reviewSha?.ToLowerInvariant(),
                ExpectedCount = expectedCount,
            };
        }

        if (conversation is not null || chat is not null || offset is not null || local is not null || manifestOut is not null)
        {
            throw new OptionsException(
                "apply and undo-batch take the pairing parameters from the manifest, never from the " +
                "command line.");
        }

        if (mode == Mode.Apply && msgstore is null)
        {
            throw new OptionsException("apply needs --msgstore: every row is re-checked against it.");
        }

        if (mode == Mode.UndoBatch && msgstore is not null)
        {
            throw new OptionsException("undo-batch does not read the msgstore.");
        }

        if (manifest is null || sha is null)
        {
            throw new OptionsException("--manifest and --manifest-sha256 are both required.");
        }

        if (!Sha256Pattern().IsMatch(sha))
        {
            throw new OptionsException("--manifest-sha256 must be 64 hexadecimal characters.");
        }

        if (!backup)
        {
            throw new OptionsException(
                "--i-have-a-backup is required: back up workspace.db first. Nothing was opened.");
        }

        return new Options
        {
            Mode = mode,
            MsgstorePath = msgstore,
            WorkspacePath = workspace,
            ManifestPath = manifest,
            ManifestSha256 = sha.ToLowerInvariant(),
            HaveBackup = true,
            ReviewFile = reviewFile,
        };
    }

    internal static bool TryParseOffset(string text, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        var match = OffsetPattern().Match(text);

        if (!match.Success)
        {
            return false;
        }

        var hours = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

        if (hours > 14 || minutes > 59 || (hours == 14 && minutes > 0))
        {
            return false;
        }

        var value = new TimeSpan(hours, minutes, 0);
        offset = match.Groups[1].Value == "-" ? -value : value;

        return true;
    }

    internal static string FormatOffset(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
}
