using System.Globalization;
using Chatcheology.Data.Phase7;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport;

// -----------------------------------------------------------------------------------------------
// The census's matching, alignment and coverage rules, copied from .temp\MsgstoreCensus\Census.cs
// with the report code removed. Every rule below is the census's rule, unchanged: the hash and
// name+size matching, the (local minute, direction) keys, pairing by order only when the counts are
// equal, and candidate-set membership through the public Phase7SupportReader. The only additions
// are reading message.key_id, classifying each attachment instead of printing counts, and (after
// the headline rules) excluding an asset independently disproven for the attachment.
// A test runs the census itself on the same synthetic data and compares its headline figures.
// -----------------------------------------------------------------------------------------------

/// <summary>How a record's <c>file_hash</c> value looked.</summary>
internal enum HashFormat
{
    ColumnAbsent,
    Empty,
    Base64Sha256,
    Hex64,
    Blob32,
    Other,
}

/// <summary>The census's matching categories.</summary>
internal enum MatchCategory
{
    BothSameAsset,
    HashOnly,
    NameSizeOnly,
    NameMatchedSizeDiffers,
    Conflict,
    NoMatch,
}

/// <summary>Why an attachment is, or is not, importable, judged on the match alone.</summary>
/// <remarks>Declaration order is the order the reasons are tested and printed in.</remarks>
internal enum Eligibility
{
    /// <summary>The attachment's message time could not be read, so it has no key.</summary>
    NoTime,

    /// <summary>Its (minute, direction) key had more media records than attachments.</summary>
    MoreRecordsInKey,

    /// <summary>Its key had fewer media records than attachments (possibly none).</summary>
    FewerRecordsInKey,

    /// <summary>Paired, but the record matched no single asset.</summary>
    PairedNoAsset,

    /// <summary>Paired with an asset, but no completed Phase 7 run evaluated the attachment.</summary>
    NoCompletedRun,

    /// <summary>The record's asset is not in the attachment's current candidate set.</summary>
    NotInCandidateSet,

    /// <summary>In the census headline, matched by name+size only: excluded by Angie's decision.</summary>
    NameSizeOnly,

    /// <summary>In the census headline, but hash and name+size named different assets.</summary>
    Conflict,

    /// <summary>
    /// In the census headline, but the record's asset carries an independent disproof for this
    /// attachment in some Phase 7 run (Angie's rule, approved 2026-10-13).
    /// </summary>
    Disproven,

    /// <summary>In the census headline with a hash match, but the record has no usable key_id.</summary>
    NoKeyID,

    /// <summary>In the headline, hash matched (both or hash only), with a key_id.</summary>
    Eligible,
}

/// <summary>One <c>message_media</c> row of the chosen chat, with what it matched.</summary>
internal sealed class MediaRecord
{
    internal required long MessageID { get; init; }
    internal required bool FromMe { get; init; }
    internal required DateTime? LocalTime { get; init; }
    internal required long OrderKey { get; init; }
    internal required string FileName { get; init; }
    internal required bool HasFilePath { get; init; }
    internal required long? FileSize { get; init; }
    internal required HashFormat Format { get; init; }
    internal required string? HashHex { get; init; }

    /// <summary>The message's key_id when it is usable in a basis description; else null.</summary>
    internal required string? KeyID { get; init; }

    // Display-only facts for the probe. They play no part in matching or pairing.
    internal string? RawKeyID { get; init; }
    internal long? MessageType { get; init; }
    internal long? SortID { get; init; }
    internal string? MimeType { get; init; }
    internal string? MediaName { get; init; }

    internal long? HashAsset { get; set; }
    internal bool NameMatched { get; set; }
    internal List<long> NameSizeAssets { get; set; } = [];
    internal MatchCategory Category { get; set; }
    internal long? Asset { get; set; }
    internal bool Ambiguous { get; set; }

    internal DateTime? Minute => LocalTime is { } t ? new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0) : null;
}

/// <summary>One workspace attachment of the conversation.</summary>
internal sealed record WsAttachment(
    long AttachmentID, long Ordinal, long SequenceNumber, DateTime? Minute, bool Outgoing,
    bool Resolved, long? ResolvedAsset, long MessageID);

/// <summary>What the pairing says about one attachment.</summary>
internal sealed record Assessment(
    long AttachmentID,
    Eligibility Eligibility,
    bool Resolved,
    long? ResolvedAsset,
    long? MessageID,
    string? KeyID,
    long? Asset,
    MatchCategory? Category)
{
    /// <summary>True when the census would count this attachment in its headline.</summary>
    internal bool InHeadline => Eligibility >= Eligibility.NameSizeOnly;

    internal MatchKind? Kind => Category switch
    {
        MatchCategory.BothSameAsset => MatchKind.HashAndNameSize,
        MatchCategory.HashOnly => MatchKind.HashOnly,
        _ => null,
    };
}

/// <summary>The fixed inputs a pairing is computed under. All four are operator-supplied.</summary>
internal sealed record PairingParameters(long ConversationID, long ChatID, TimeSpan UtcOffset, long LocalParticipantID);

/// <summary>The census pairing, per attachment.</summary>
internal sealed class Pairing(SqliteConnection workspace, SqliteConnection msgstore, MsgstoreSchema schema, PairingParameters parameters)
{
    private readonly Phase7SupportReader _support = new();

    private readonly Dictionary<string, long> _assetBySha = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(long SizeBytes, long? Asset)>> _filesByName = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _hasSortID = schema.HasColumn("message", "sort_id");
    private readonly bool _hasHash = schema.HasColumn("message_media", "file_hash");
    private readonly bool _hasKeyID = schema.HasColumn("message", "key_id");

    /// <summary>
    /// The matched records and the conversation's attachments, before alignment: what the probe
    /// shows. The same reads and the same matching as <see cref="Compute"/>.
    /// </summary>
    internal (List<WsAttachment> Attachments, List<MediaRecord> Records) Load()
    {
        if (_loaded is { } loaded)
        {
            return loaded;
        }

        ReadInventory();

        var records = ReadRecords();

        foreach (var record in records)
        {
            Match(record);
        }

        _loaded = (ReadAttachments(), records);

        return _loaded.Value;
    }

    private (List<WsAttachment> Attachments, List<MediaRecord> Records)? _loaded;

    internal SqliteConnection Workspace => workspace;
    internal SqliteConnection MsgstoreConnection => msgstore;
    internal bool HasSortID => _hasSortID;
    internal PairingParameters Parameters => parameters;

    /// <summary>Every attachment of the conversation, by AttachmentID.</summary>
    internal SortedDictionary<long, Assessment> Compute()
    {
        var (attachments, records) = Load();
        var result = new SortedDictionary<long, Assessment>();

        var unpaired = Unpaired(attachments, records, out var pairs);

        foreach (var (attachment, eligibility) in unpaired)
        {
            result[attachment.AttachmentID] = new Assessment(
                attachment.AttachmentID, eligibility, attachment.Resolved, attachment.ResolvedAsset,
                null, null, null, null);
        }

        foreach (var (attachment, record) in pairs)
        {
            result[attachment.AttachmentID] = new Assessment(
                attachment.AttachmentID, Cover(attachment, record), attachment.Resolved, attachment.ResolvedAsset,
                record.MessageID, record.KeyID, record.Asset, record.Category);
        }

        return result;
    }

    // -------------------------------------------------------------------------------------
    // Time. The offset is the operator's, fixed; no system time zone is consulted.
    // -------------------------------------------------------------------------------------

    internal DateTime? Local(long? epochMs) =>
        epochMs is > 0 and < 253_402_300_799_000 - 86_400_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(epochMs.Value).ToOffset(parameters.UtcOffset).DateTime
            : null;

    private static readonly string[] MessageFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
    ];

    internal static DateTime? ParseMinute(string stamp) =>
        DateTime.TryParseExact(stamp, MessageFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
            ? new DateTime(v.Year, v.Month, v.Day, v.Hour, v.Minute, 0)
            : null;

    // -------------------------------------------------------------------------------------
    // Workspace inventory (census ReadInventory).
    // -------------------------------------------------------------------------------------

    private void ReadInventory()
    {
        using (var command = workspace.CreateCommand())
        {
            command.CommandText = "SELECT MediaAssetID, SHA256 FROM MediaAsset;";

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                _assetBySha[reader.GetString(1).ToUpperInvariant()] = reader.GetInt64(0);
            }
        }

        using (var command = workspace.CreateCommand())
        {
            command.CommandText =
                """
                SELECT f.FileName, f.SizeBytes, af.MediaAssetID
                FROM MediaFile AS f
                LEFT JOIN MediaAssetFile AS af ON af.MediaFileID = f.MediaFileID;
                """;

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var name = reader.GetString(0);

                if (!_filesByName.TryGetValue(name, out var list))
                {
                    list = [];
                    _filesByName[name] = list;
                }

                list.Add((reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)));
            }
        }
    }

    // -------------------------------------------------------------------------------------
    // Msgstore reads (census ReadRecords, plus key_id).
    // -------------------------------------------------------------------------------------

    private List<MediaRecord> ReadRecords()
    {
        var sortID = _hasSortID ? "m.sort_id" : "NULL";
        var hash = _hasHash ? "mm.file_hash" : "NULL";
        var keyID = _hasKeyID ? "m.key_id" : "NULL";
        var mime = schema.HasColumn("message_media", "mime_type") ? "mm.mime_type" : "NULL";
        var mediaName = schema.HasColumn("message_media", "media_name") ? "mm.media_name" : "NULL";

        using var command = msgstore.CreateCommand();
        command.CommandText =
            $"""
            SELECT m._id, m.from_me, m.timestamp, {sortID},
                   mm.file_path, mm.file_size, {hash}, {keyID}, m.message_type, {mime}, {mediaName}
            FROM message_media AS mm
            INNER JOIN message AS m ON m._id = mm.message_row_id
            WHERE m.chat_row_id = $chat;
            """;
        command.Parameters.AddWithValue("$chat", parameters.ChatID);

        var records = new List<MediaRecord>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var path = reader.IsDBNull(4) ? null : Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture);
            var hasPath = !string.IsNullOrWhiteSpace(path);
            string? hex = null;
            var format = _hasHash
                ? ClassifyHash(reader.IsDBNull(6) ? null : reader.GetValue(6), out hex)
                : HashFormat.ColumnAbsent;

            records.Add(new MediaRecord
            {
                MessageID = id,
                FromMe = !reader.IsDBNull(1) && reader.GetInt64(1) != 0,
                LocalTime = Local(reader.IsDBNull(2) ? null : reader.GetInt64(2)),
                OrderKey = !_hasSortID || reader.IsDBNull(3) ? id : reader.GetInt64(3),
                HasFilePath = hasPath,
                FileName = hasPath ? LastSegment(path!) : "",
                FileSize = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                Format = format,
                HashHex = hex,
                KeyID = UsableKeyID(reader.IsDBNull(7) ? null : reader.GetValue(7)),
                RawKeyID = reader.IsDBNull(7) ? null : Convert.ToString(reader.GetValue(7), CultureInfo.InvariantCulture),
                SortID = !_hasSortID || reader.IsDBNull(3) ? null : reader.GetInt64(3),
                MessageType = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                MimeType = reader.IsDBNull(9) ? null : Convert.ToString(reader.GetValue(9), CultureInfo.InvariantCulture),
                MediaName = reader.IsDBNull(10) ? null : Convert.ToString(reader.GetValue(10), CultureInfo.InvariantCulture),
            });
        }

        return records;
    }

    /// <summary>
    /// The key_id as text when it can go into a basis description and a tab-separated manifest:
    /// non-empty, at most 128 characters, printable ASCII without spaces. Anything else is null.
    /// </summary>
    internal static string? UsableKeyID(object? value) =>
        value is string text && text.Length is > 0 and <= 128 && text.All(c => c is > ' ' and <= '~')
            ? text
            : null;

    internal static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);

        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    /// <summary>Census ClassifyHash, unchanged. Only base64-of-32-bytes is used to match.</summary>
    internal static HashFormat ClassifyHash(object? value, out string? hex)
    {
        hex = null;

        switch (value)
        {
            case null or DBNull:
                return HashFormat.Empty;

            case byte[] { Length: 32 } blob:
                hex = Convert.ToHexString(blob);
                return HashFormat.Blob32;

            case byte[] { Length: 0 }:
                return HashFormat.Empty;

            case byte[]:
                return HashFormat.Other;
        }

        var text = (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Trim();

        if (text.Length == 0)
        {
            return HashFormat.Empty;
        }

        if (text.Length == 64 && text.All(Uri.IsHexDigit))
        {
            hex = text.ToUpperInvariant();
            return HashFormat.Hex64;
        }

        Span<byte> buffer = stackalloc byte[64];

        if (Convert.TryFromBase64String(text, buffer, out var written) && written == 32)
        {
            hex = Convert.ToHexString(buffer[..32]);
            return HashFormat.Base64Sha256;
        }

        return HashFormat.Other;
    }

    // -------------------------------------------------------------------------------------
    // Matching (census Match, unchanged).
    // -------------------------------------------------------------------------------------

    private void Match(MediaRecord record)
    {
        if (record.HashHex is { } hex && _assetBySha.TryGetValue(hex, out var hashAsset)
            && record.Format == HashFormat.Base64Sha256)
        {
            record.HashAsset = hashAsset;
        }

        if (record.FileName.Length > 0 && _filesByName.TryGetValue(record.FileName, out var files))
        {
            record.NameMatched = true;
            record.NameSizeAssets =
            [
                .. files
                    .Where(file => record.FileSize is { } size && file.SizeBytes == size && file.Asset is not null)
                    .Select(file => file.Asset!.Value)
                    .Distinct()
                    .Order(),
            ];
        }

        var nameSize = record.NameSizeAssets;

        record.Category = record.HashAsset is { } h
            ? nameSize.Count == 0 ? MatchCategory.HashOnly
              : nameSize.Contains(h) ? MatchCategory.BothSameAsset
              : MatchCategory.Conflict
            : nameSize.Count > 0 ? MatchCategory.NameSizeOnly
            : record.NameMatched ? MatchCategory.NameMatchedSizeDiffers
            : MatchCategory.NoMatch;

        if (record.HashAsset is { } asset)
        {
            record.Asset = asset;
        }
        else if (nameSize.Count == 1)
        {
            record.Asset = nameSize[0];
        }
        else if (nameSize.Count > 1)
        {
            record.Ambiguous = true;
        }
    }

    // -------------------------------------------------------------------------------------
    // Workspace reads (census ReadAttachments, unchanged).
    // -------------------------------------------------------------------------------------

    /// <remarks>A system message (no sender) counts as not outgoing.</remarks>
    private List<WsAttachment> ReadAttachments()
    {
        var result = new List<WsAttachment>();

        using var command = workspace.CreateCommand();
        command.CommandText =
            """
            SELECT a.AttachmentID, a.Ordinal, m.SequenceNumber, m.MessageDateTimeLocal,
                   m.SenderParticipantID, a.ResolutionStatus, a.ResolvedMediaAssetID, a.MessageID
            FROM Attachment AS a
            INNER JOIN Message AS m ON m.MessageID = a.MessageID
            WHERE m.ConversationID = $id
            ORDER BY a.AttachmentID;
            """;
        command.Parameters.AddWithValue("$id", parameters.ConversationID);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            result.Add(new WsAttachment(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                ParseMinute(reader.GetString(3)),
                !reader.IsDBNull(4) && reader.GetInt64(4) == parameters.LocalParticipantID,
                reader.GetString(5) == "Resolved",
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetInt64(7)));
        }

        return result;
    }

    // -------------------------------------------------------------------------------------
    // Alignment (census Align): keyed by (local minute, direction); paired by order only when the
    // counts are equal. Returns the attachments that were not paired, with why.
    // -------------------------------------------------------------------------------------

    private static List<(WsAttachment Attachment, Eligibility Reason)> Unpaired(
        List<WsAttachment> attachments, List<MediaRecord> records,
        out List<(WsAttachment Attachment, MediaRecord Record)> pairs)
    {
        pairs = [];

        var unpaired = attachments
            .Where(a => a.Minute is null)
            .Select(a => (a, Eligibility.NoTime))
            .ToList();

        var wsKeys = attachments
            .Where(a => a.Minute is not null)
            .GroupBy(a => (Minute: a.Minute!.Value, a.Outgoing))
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.SequenceNumber).ThenBy(a => a.Ordinal).ThenBy(a => a.AttachmentID).ToList());

        var msKeys = records
            .Where(r => r.Minute is not null)
            .GroupBy(r => (Minute: r.Minute!.Value, Outgoing: r.FromMe))
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.OrderKey).ThenBy(r => r.MessageID).ToList());

        foreach (var (key, group) in wsKeys)
        {
            var found = msKeys.GetValueOrDefault(key) ?? [];

            if (found.Count == group.Count)
            {
                for (var i = 0; i < group.Count; i++)
                {
                    pairs.Add((group[i], found[i]));
                }
            }
            else
            {
                var reason = found.Count > group.Count ? Eligibility.MoreRecordsInKey : Eligibility.FewerRecordsInKey;
                unpaired.AddRange(group.Select(a => (a, reason)));
            }
        }

        return unpaired;
    }

    // -------------------------------------------------------------------------------------
    // Coverage (census Cover), through the public Phase 7 readers only, then the import's own two
    // narrowing rules on top of the headline: a hash match is required, and a key_id.
    // -------------------------------------------------------------------------------------

    private Eligibility Cover(WsAttachment attachment, MediaRecord record)
    {
        if (record.Asset is not { } asset)
        {
            return Eligibility.PairedNoAsset;
        }

        if (CandidateSet(attachment.AttachmentID, asset) is { } outside)
        {
            return outside;
        }

        return record.Category switch
        {
            MatchCategory.NameSizeOnly => Eligibility.NameSizeOnly,
            MatchCategory.Conflict => Eligibility.Conflict,
            _ when IsDisproven(attachment.AttachmentID, asset) => Eligibility.Disproven,
            _ when record.KeyID is null => Eligibility.NoKeyID,
            _ => Eligibility.Eligible,
        };
    }

    // -------------------------------------------------------------------------------------
    // Independent disproofs (the import's third narrowing rule), through the public
    // Phase7CandidateDispositionReader. A disproof belongs to one run's candidate row, so every run
    // is asked, not only the current one: a disproof of this asset for this attachment in any run
    // excludes the row. Missing is better than wrong.
    // -------------------------------------------------------------------------------------

    private readonly Phase7CandidateDispositionReader _dispositions = new();
    private List<long>? _runIDs;

    /// <summary>
    /// <see cref="Eligibility.NoCompletedRun"/> or <see cref="Eligibility.NotInCandidateSet"/> when the
    /// asset is not in the attachment's current candidate set; null when it is. Census rule, unchanged.
    /// </summary>
    internal Eligibility? CandidateSet(long attachmentID, long asset)
    {
        if (_support.ReadLatestCompletedRunContaining(workspace, attachmentID) is not { } run)
        {
            return Eligibility.NoCompletedRun;
        }

        var inSet = _support.ReadRankedCandidateSet(workspace, run, attachmentID)
            .Tiers
            .SelectMany(tier => tier)
            .Any(support => support.MediaAssetID == asset);

        return inSet ? null : Eligibility.NotInCandidateSet;
    }

    /// <summary>The latest completed Phase 7 run that evaluated the attachment, or null.</summary>
    internal long? LatestRun(long attachmentID) =>
        _support.ReadLatestCompletedRunContaining(workspace, attachmentID);

    internal bool IsDisproven(long attachmentID, long asset)
    {
        _runIDs ??= ReadRunIDs();

        foreach (var runID in _runIDs)
        {
            // Served by the (RunID, AttachmentID, MediaAssetID) unique index.
            foreach (var candidateID in _dispositions.ReadIndependentlyDisprovenCandidateIDs(workspace, runID, attachmentID))
            {
                if (CandidateAsset(candidateID) == asset)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private List<long> ReadRunIDs()
    {
        var result = new List<long>();

        using var command = workspace.CreateCommand();
        command.CommandText = "SELECT RunID FROM Phase7Run ORDER BY RunID;";

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    /// <summary>The asset a candidate row names, by its primary key.</summary>
    private long? CandidateAsset(long candidateID)
    {
        using var command = workspace.CreateCommand();
        command.CommandText = "SELECT MediaAssetID FROM AttachmentCandidate WHERE AttachmentCandidateID = $id;";
        command.Parameters.AddWithValue("$id", candidateID);

        var value = command.ExecuteScalar();

        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
