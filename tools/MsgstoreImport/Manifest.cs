using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MsgstoreImport;

/// <summary>How a manifest row's file was matched. Both kinds carry a hash match.</summary>
internal enum MatchKind
{
    HashAndNameSize,
    HashOnly,

    /// <summary>Name and exact size only, no hash match: only ever imported after Angie's review.</summary>
    NameSizeOnly,
}

/// <summary>How the manifest's rows were paired.</summary>
internal enum PairingRule
{
    /// <summary>The census rule: (minute, direction) keys with equal counts. Manifest format 1.</summary>
    Minute,

    /// <summary>CH-9: whole local days lined up by message sequence. Manifest format 2.</summary>
    DaySequence,

    /// <summary>
    /// CH-2 leftovers: paired slots whose hash-matched asset is outside the date candidate set, on
    /// usable days. Manifest format 2 with two extra columns (PairedBy, FileDate).
    /// </summary>
    OutsideCandidateSet,

    /// <summary>
    /// Addendum: no-date slots whose msgstore message is a document (message_type 9). Manifest
    /// format 2 with one extra column (PairedBy).
    /// </summary>
    Documents,

    /// <summary>
    /// Addendum: name+size-only matches Angie marked R on a review list. Manifest format 2 with two
    /// extra columns (PairedBy, ReviewList: the first 12 hex of the review list's SHA-256).
    /// </summary>
    NameSizeReviewed,

    /// <summary>
    /// CH-13: not a pairing. Every unresolved, unreviewed attachment, recorded as reviewed with
    /// insufficient evidence. Its own manifest layout (see <c>MissingManifest</c>); never parsed by
    /// <see cref="Manifest"/>.
    /// </summary>
    ReviewMissing,

    /// <summary>
    /// CH-8 / CH-10: not a pairing and not a Phase 7 write. WhatsApp's forwarded status and low-quality
    /// copy links, one row per paired attachment. Its own manifest layout (see <c>FactsManifest</c>).
    /// </summary>
    Facts,

    /// <summary>
    /// CH-22: not a pairing of attachments and not a Phase 7 write. WhatsApp's forwarded status and
    /// forward score for text messages, one row per uniquely paired text message. Its own manifest
    /// layout (see <c>TextFactsManifest</c>).
    /// </summary>
    TextFacts,

    /// <summary>
    /// CH-63 / CH-71: not a Phase 7 write. Picture captions copied from the msgstore, one row per paired
    /// attachment that has a caption (its own, or its hidden low-quality partner's). Its own manifest
    /// layout (see <c>CaptionFactsManifest</c>).
    /// </summary>
    CaptionFacts,
}

/// <summary>One attachment the manifest proposes to confirm.</summary>
/// <remarks>
/// <see cref="PairedBy"/> is set for the outside, documents and reviewed rules; <see cref="FileDate"/>
/// for the outside rule; <see cref="ReviewList"/> for the reviewed rule.
/// </remarks>
internal sealed record ManifestRow(
    long AttachmentID, long MediaAssetID, long MessageID, string KeyID, MatchKind Kind,
    PairingRule? PairedBy = null, DateOnly? FileDate = null, string? ReviewList = null);

/// <summary>
/// The private manifest: the pairing parameters and one row per attachment. Its SHA-256 is the
/// operator's approval of exactly this set.
/// </summary>
/// <remarks>
/// Tab-separated UTF-8 without a BOM, LF line endings, rows in AttachmentID order. The digest is
/// over the file's bytes, so any change at all — a row, a parameter, a space — is refused.
/// </remarks>
internal sealed class Manifest
{
    internal const string Magic = "chatcheology-msgstore-import-manifest";
    internal const string Version = "1";
    internal const string DaySequenceVersion = "2";
    internal const string DaySequenceToken = "day-sequence";
    internal const string Header = "AttachmentID\tMediaAssetID\tMessageID\tKeyID\tMatchKind";
    internal const string OutsideToken = "outside-candidate-set";
    internal const string OutsideHeader = Header + "\tPairedBy\tFileDate";
    internal const string DocumentsToken = "documents";
    internal const string DocumentsHeader = Header + "\tPairedBy";
    internal const string NameSizeReviewedToken = "namesize-reviewed";
    internal const string NameSizeReviewedHeader = Header + "\tPairedBy\tReviewList";

    private static string RuleToken(PairingRule rule) => rule switch
    {
        PairingRule.DaySequence => DaySequenceToken,
        PairingRule.OutsideCandidateSet => OutsideToken,
        PairingRule.Documents => DocumentsToken,
        PairingRule.NameSizeReviewed => NameSizeReviewedToken,
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };

    private static string RuleHeader(PairingRule rule) => rule switch
    {
        PairingRule.OutsideCandidateSet => OutsideHeader,
        PairingRule.Documents => DocumentsHeader,
        PairingRule.NameSizeReviewed => NameSizeReviewedHeader,
        _ => Header,
    };

    internal required PairingParameters Parameters { get; init; }
    internal required IReadOnlyList<ManifestRow> Rows { get; init; }

    /// <summary>Minute manifests keep format 1 byte for byte, so batches already approved still verify.</summary>
    internal PairingRule Rule { get; init; } = PairingRule.Minute;

    internal static string KindToken(MatchKind kind) => kind switch
    {
        MatchKind.HashAndNameSize => "hash-and-name-size",
        MatchKind.HashOnly => "hash-only",
        MatchKind.NameSizeOnly => "name-size-only",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The wording used in the basis description.</summary>
    internal static string KindDescription(MatchKind kind) => kind switch
    {
        MatchKind.HashAndNameSize => "hash and name+size",
        MatchKind.HashOnly => "hash only",
        MatchKind.NameSizeOnly => "name+size only",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static string Tag(string sha256Hex) => sha256Hex[..12].ToLowerInvariant();

    internal static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal byte[] ToBytes()
    {
        var text = new StringBuilder();

        void Line(string line) => text.Append(line).Append('\n');

        Line($"{Magic}\t{(Rule == PairingRule.Minute ? Version : DaySequenceVersion)}");
        Line("# PRIVATE: real identifiers. Keep outside the repository. Any edit invalidates its SHA-256.");

        if (Rule != PairingRule.Minute)
        {
            Line($"rule\t{RuleToken(Rule)}");
        }
        Line($"conversation\t{Text.Id(Parameters.ConversationID)}");
        Line($"chat\t{Text.Id(Parameters.ChatID)}");
        Line($"utc-offset\t{Options.FormatOffset(Parameters.UtcOffset)}");
        Line($"local-participant\t{Text.Id(Parameters.LocalParticipantID)}");
        Line($"rows\t{Rows.Count.ToString(CultureInfo.InvariantCulture)}");
        Line(RuleHeader(Rule));

        foreach (var row in Rows)
        {
            var fields = new List<string>
            {
                Text.Id(row.AttachmentID), Text.Id(row.MediaAssetID), Text.Id(row.MessageID), row.KeyID, KindToken(row.Kind),
            };

            if (Rule is PairingRule.OutsideCandidateSet or PairingRule.Documents or PairingRule.NameSizeReviewed)
            {
                fields.Add(row.PairedBy == PairingRule.DaySequence ? DaySequenceToken : "minute");
            }

            if (Rule == PairingRule.NameSizeReviewed)
            {
                fields.Add(row.ReviewList!);
            }

            if (Rule == PairingRule.OutsideCandidateSet)
            {
                fields.Add(row.FileDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }

            Line(string.Join('\t', fields));
        }

        return new UTF8Encoding(false).GetBytes(text.ToString());
    }

    /// <summary>Parses manifest bytes strictly. Anything unexpected is refused, never guessed.</summary>
    /// <exception cref="FormatException">The bytes are not a well-formed manifest.</exception>
    internal static Manifest Parse(byte[] bytes)
    {
        string content;

        try
        {
            content = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new FormatException("The manifest is not valid UTF-8.");
        }

        if (content.Contains('\r'))
        {
            throw new FormatException("The manifest has CR characters; it was not written by this tool.");
        }

        var lines = content.Split('\n');
        var rule = PairingRule.Minute;

        if (lines.Length >= 3 && lines[0] == $"{Magic}\t{DaySequenceVersion}")
        {
            rule = lines[2] == $"rule\t{DaySequenceToken}" ? PairingRule.DaySequence
                : lines[2] == $"rule\t{OutsideToken}" ? PairingRule.OutsideCandidateSet
                : lines[2] == $"rule\t{DocumentsToken}" ? PairingRule.Documents
                : lines[2] == $"rule\t{NameSizeReviewedToken}" ? PairingRule.NameSizeReviewed
                : throw new FormatException("A format 2 manifest must name its rule on line 3.");

            // The rest has format 1's layout.
            lines = [$"{Magic}\t{Version}", lines[1], .. lines[3..]];
        }

        if (lines.Length < 9 || lines[^1].Length != 0)
        {
            throw new FormatException("The manifest is truncated.");
        }

        if (lines[0] != $"{Magic}\t{Version}" || !lines[1].StartsWith('#'))
        {
            throw new FormatException("The manifest's first lines are not this tool's manifest header.");
        }

        long Value(int index, string key)
        {
            var parts = lines[index].Split('\t');

            return parts.Length == 2 && parts[0] == key
                   && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new FormatException($"The manifest's '{key}' line is malformed.");
        }

        var conversation = Value(2, "conversation");
        var chat = Value(3, "chat");

        var offsetParts = lines[4].Split('\t');

        if (offsetParts.Length != 2 || offsetParts[0] != "utc-offset"
            || !Options.TryParseOffset(offsetParts[1], out var offset))
        {
            throw new FormatException("The manifest's 'utc-offset' line is malformed.");
        }

        var local = Value(5, "local-participant");
        var count = Value(6, "rows");

        if (lines[7] != RuleHeader(rule))
        {
            throw new FormatException("The manifest's column header is not the expected one.");
        }

        var body = lines[8..^1];

        if (body.Length != count)
        {
            throw new FormatException("The manifest's row count does not match its 'rows' line.");
        }

        var rows = new List<ManifestRow>(body.Length);
        var seen = new HashSet<long>();

        foreach (var line in body)
        {
            var parts = line.Split('\t');

            var outside = rule == PairingRule.OutsideCandidateSet;
            var documents = rule == PairingRule.Documents;
            var reviewed = rule == PairingRule.NameSizeReviewed;

            if (parts.Length != (outside || reviewed ? 7 : documents ? 6 : 5)
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var attachment)
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var asset)
                || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var message)
                || Pairing.UsableKeyID(parts[3]) is not { } keyID)
            {
                throw new FormatException("A manifest row is malformed.");
            }

            var kind = parts[4] switch
            {
                "hash-and-name-size" => MatchKind.HashAndNameSize,
                "hash-only" => MatchKind.HashOnly,
                "name-size-only" when reviewed => MatchKind.NameSizeOnly,
                _ => throw new FormatException("A manifest row has an unknown match kind."),
            };

            if (!seen.Add(attachment))
            {
                throw new FormatException("The manifest names an attachment twice.");
            }

            PairingRule? pairedBy = null;
            DateOnly? fileDate = null;

            string? reviewList = null;

            if (reviewed)
            {
                reviewList = parts[6].Length == 12 && parts[6].All(Uri.IsHexDigit)
                    ? parts[6]
                    : throw new FormatException("A manifest row has a malformed ReviewList.");
            }

            if (outside || documents || reviewed)
            {
                pairedBy = parts[5] switch
                {
                    "minute" => PairingRule.Minute,
                    DaySequenceToken => PairingRule.DaySequence,
                    _ => throw new FormatException("A manifest row has an unknown PairedBy value."),
                };
            }

            if (outside)
            {
                fileDate = DateOnly.TryParseExact(parts[6], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? date
                    : throw new FormatException("A manifest row has a malformed FileDate.");
            }

            rows.Add(new ManifestRow(attachment, asset, message, keyID, kind, pairedBy, fileDate, reviewList));
        }

        return new Manifest
        {
            Parameters = new PairingParameters(conversation, chat, offset, local),
            Rows = rows,
            Rule = rule,
        };
    }
}
