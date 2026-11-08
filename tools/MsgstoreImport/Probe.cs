using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport;

/// <summary>Counts for stdout. The detail is only in the private file.</summary>
internal sealed class ProbeSummary
{
    internal sealed record KeyCounts(int MinuteOffset, bool Outgoing, int Attachments, int Records);

    internal required List<KeyCounts> Keys { get; init; }
    internal required int TargetResolved { get; init; }
    internal required int TargetDistinctAssets { get; init; }
    internal required int TargetMaxSlotsSharingAsset { get; init; }
    internal required int TargetDistinctHashes { get; init; }
    internal required int TargetMaxRecordsSharingHash { get; init; }
    internal required int MinuteMessages { get; init; }
    internal required int MinuteMediaMessages { get; init; }
    internal required int MinuteNonMediaMessages { get; init; }
}

/// <summary>
/// <c>probe</c>: one local minute (and the minute either side, both directions) shown in detail, to
/// look at a busy minute that seems wrong in the viewer.
/// </summary>
/// <remarks>
/// Read-only on both databases, exactly as plan is: the workspace through
/// <see cref="WorkspaceDatabase.OpenReadOnlyConnection"/>, the msgstore immutable. Identifiers and file
/// names go only to the private file; stdout gets counts. No message text column is ever selected.
/// </remarks>
internal static partial class Prober
{
    [GeneratedRegex("import batch ([0-9a-f]{12})")]
    private static partial Regex BatchTag();

    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static ProbeSummary Probe(Options options)
    {
        if (Planner.ManifestOutRefusal(options.ProbeOut!, "--probe-out") is { } refusal)
        {
            throw new RefusalException(refusal);
        }

        var parameters = options.Parameters!;
        var target = options.ProbeMinute!.Value;

        Inputs.RequireMsgstore(options.MsgstorePath!);
        Inputs.RequireWorkspace(options.WorkspacePath);

        var before = FileStamp.Of(options.MsgstorePath!);
        string text;
        ProbeSummary summary;

        try
        {
            using var msgstore = Msgstore.OpenReadOnly(options.MsgstorePath!);
            var schema = MsgstoreSchema.Read(msgstore);

            if (schema.Refusal() is { } schemaRefusal)
            {
                throw new RefusalException(schemaRefusal);
            }

            using var workspace = WorkspaceDatabase.OpenReadOnlyConnection(options.WorkspacePath);

            Inputs.RequireSchemaVersion(workspace);
            Inputs.RequireKnown(workspace, msgstore, parameters);

            var (attachments, records) = new Pairing(workspace, msgstore, schema, parameters).Load();

            (text, summary) = Build(workspace, msgstore, schema, parameters, target, attachments, records);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new RefusalException("The workspace is busy: another program holds a lock on it. Nothing was written.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        if (FileStamp.Of(options.MsgstorePath!) != before)
        {
            throw new RefusalException("The msgstore's size or last-write time changed during the read. Nothing was written.");
        }

        // CreateNew: never overwrite, even if something appeared since the path was checked.
        using (var stream = new FileStream(options.ProbeOut!, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(text);
        }

        return summary;
    }

    private static (string Text, ProbeSummary Summary) Build(
        SqliteConnection workspace, SqliteConnection msgstore, MsgstoreSchema schema, PairingParameters p,
        DateTime target, List<WsAttachment> attachments, List<MediaRecord> records)
    {
        var history = new Phase7ResolutionHistoryReader();
        var text = new StringBuilder();
        var keys = new List<ProbeSummary.KeyCounts>();
        List<WsAttachment> targetSlots = [];
        List<MediaRecord> targetRecords = [];

        text.AppendLine("# Msgstore import - PRIVATE minute probe");
        text.AppendLine();
        text.AppendLine("**PRIVATE: real identifiers and file names. Keep outside the repository.** Read-only; nothing was");
        text.AppendLine("written to either database. No message text was read.");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Target local minute {target:yyyy-MM-dd HH:mm}, UTC offset {Options.FormatOffset(p.UtcOffset)}, conversation " +
            $"{p.ConversationID}, chat _id {p.ChatID}, local participant {p.LocalParticipantID}.");
        text.AppendLine();
        text.AppendLine("A key is (local minute, direction). The census pairs a key only when it has exactly as many");
        text.AppendLine("msgstore media records as workspace attachments, position by position: workspace SequenceNumber,");
        text.AppendLine("then ordinal, against msgstore sort_id, then _id.");
        text.AppendLine();

        foreach (var offset in new[] { -1, 0, 1 })
        {
            var minute = target.AddMinutes(offset);

            foreach (var outgoing in new[] { false, true })
            {
                var slots = attachments
                    .Where(a => a.Minute == minute && a.Outgoing == outgoing)
                    .OrderBy(a => a.SequenceNumber).ThenBy(a => a.Ordinal).ThenBy(a => a.AttachmentID)
                    .ToList();

                var found = records
                    .Where(r => r.Minute == minute && r.FromMe == outgoing)
                    .OrderBy(r => r.OrderKey).ThenBy(r => r.MessageID)
                    .ToList();

                keys.Add(new ProbeSummary.KeyCounts(offset, outgoing, slots.Count, found.Count));


                var isTarget = offset == 0;
                var paired = slots.Count == found.Count && slots.Count > 0;

                text.AppendLine(CultureInfo.InvariantCulture,
                    $"## {(isTarget ? "TARGET KEY: " : "")}{minute:yyyy-MM-dd HH:mm} {(outgoing ? "sent" : "received")}");
                text.AppendLine();
                var verdict = slots.Count + found.Count == 0 ? "empty."
                    : paired ? "counts equal, so the census paired this key by position."
                    : "counts differ, so the census did NOT pair this key.";

                text.AppendLine(CultureInfo.InvariantCulture,
                    $"{slots.Count} attachment(s), {found.Count} media record(s): {verdict}");
                text.AppendLine();

                if (isTarget)
                {
                    targetSlots.AddRange(slots);
                    targetRecords.AddRange(found);
                }

                if (slots.Count > 0)
                {
                    AttachmentTable(text, workspace, history, slots, paired ? found : null);
                }

                if (found.Count > 0)
                {
                    RecordTable(text, found);
                }
            }
        }

        var minuteRows = MinuteMessages(msgstore, schema, p, target);

        text.AppendLine(CultureInfo.InvariantCulture,
            $"## Every msgstore message row in the target minute ({target:HH:mm}, both directions)");
        text.AppendLine();
        text.AppendLine("Media and non-media alike, in sort_id then _id order. No text column was read.");
        text.AppendLine();
        text.AppendLine("| # | _id | sort_id | from_me | message_type | has message_media row |");
        text.AppendLine("| ---: | ---: | ---: | ---: | ---: | --- |");

        var n = 0;

        foreach (var row in minuteRows)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {++n} | {row.ID} | {Show(row.SortID)} | {Show(row.FromMe)} | {Show(row.MessageType)} | {(row.HasMedia ? "yes" : "no")} |");
        }

        if (minuteRows.Count == 0)
        {
            text.AppendLine("| - | none | | | | |");
        }

        text.AppendLine();

        var resolvedAssets = targetSlots.Where(a => a.ResolvedAsset is not null).Select(a => a.ResolvedAsset!.Value).ToList();
        var hashes = targetRecords.Where(r => r.HashHex is not null).Select(r => r.HashHex!).ToList();

        return (text.ToString(), new ProbeSummary
        {
            Keys = keys,
            TargetResolved = resolvedAssets.Count,
            TargetDistinctAssets = resolvedAssets.Distinct().Count(),
            TargetMaxSlotsSharingAsset = resolvedAssets.GroupBy(a => a).Select(g => g.Count()).DefaultIfEmpty(0).Max(),
            TargetDistinctHashes = hashes.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            TargetMaxRecordsSharingHash = hashes.GroupBy(h => h, StringComparer.OrdinalIgnoreCase).Select(g => g.Count()).DefaultIfEmpty(0).Max(),
            MinuteMessages = minuteRows.Count,
            MinuteMediaMessages = minuteRows.Count(r => r.HasMedia),
            MinuteNonMediaMessages = minuteRows.Count(r => !r.HasMedia),
        });
    }

    private static void AttachmentTable(
        StringBuilder text, SqliteConnection workspace, Phase7ResolutionHistoryReader history,
        List<WsAttachment> slots, List<MediaRecord>? pairedRecords)
    {
        var assetShare = slots.Where(a => a.ResolvedAsset is not null)
            .GroupBy(a => a.ResolvedAsset!.Value).ToDictionary(g => g.Key, g => g.Count());

        text.AppendLine("Workspace attachments, in key order:");
        text.AppendLine();
        text.AppendLine("| position | AttachmentID | SequenceNumber | ordinal | census pairs with msgstore _id | status | ResolvedMediaAssetID | resolved by (actor) | batch tag | asset file name(s) | asset SizeBytes | slots in key with this asset |");
        text.AppendLine("| --- | ---: | ---: | ---: | ---: | --- | ---: | --- | --- | --- | ---: | ---: |");

        for (var i = 0; i < slots.Count; i++)
        {
            var a = slots[i];
            var projection = history.ReplayProjection(workspace, a.AttachmentID);
            var current = projection.CurrentEffectiveEventID is { } eventID
                ? history.ReadHistory(workspace, a.AttachmentID).Single(e => e.AttachmentResolutionEventID == eventID)
                : null;

            var actor = current?.ActorLabel ?? "-";
            var tag = current?.IndependentBasisDescription is { } description && BatchTag().Match(description) is { Success: true } m
                ? m.Groups[1].Value
                : "-";

            var (names, size) = a.ResolvedAsset is { } asset ? AssetFiles(workspace, asset) : ("-", (long?)null);

            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {i + 1} of {slots.Count} | {a.AttachmentID} | {a.SequenceNumber} | {a.Ordinal} | " +
                $"{(pairedRecords is null ? "-" : Show(pairedRecords[i].MessageID))} | {(a.Resolved ? "Resolved" : "Unresolved")} | " +
                $"{Show(a.ResolvedAsset)} | {Cell(actor)} | {tag} | {names} | {Show(size)} | " +
                $"{(a.ResolvedAsset is { } shared ? Show(assetShare[shared]) : "-")} |");
        }

        text.AppendLine();
    }

    private static void RecordTable(StringBuilder text, List<MediaRecord> found)
    {
        var hashShare = found.Where(r => r.HashHex is not null)
            .GroupBy(r => r.HashHex!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        text.AppendLine("msgstore media records, in pairing order (sort_id, then _id):");
        text.AppendLine();
        text.AppendLine("| position | _id | sort_id | key_id | message_type | from_me | file name | file_size | mime_type | media_name | hash (first 12 hex) | hash format | matched asset | match kind | records in key with this hash |");
        text.AppendLine("| --- | ---: | ---: | --- | ---: | ---: | --- | ---: | --- | --- | --- | --- | ---: | --- | ---: |");

        for (var i = 0; i < found.Count; i++)
        {
            var r = found[i];

            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {i + 1} of {found.Count} | {r.MessageID} | {Show(r.SortID)} | {Cell(r.RawKeyID ?? "-")} | {Show(r.MessageType)} | " +
                $"{(r.FromMe ? 1 : 0)} | {Cell(r.HasFilePath ? r.FileName : "-")} | {Show(r.FileSize)} | {Cell(r.MimeType ?? "-")} | " +
                $"{Cell(r.MediaName ?? "-")} | {(r.HashHex is { } h ? h[..12] : "-")} | {r.Format} | {Show(r.Asset)} | " +
                $"{Kind(r)} | {(r.HashHex is { } hh ? Show(hashShare[hh]) : "-")} |");
        }

        text.AppendLine();
    }

    private static string Kind(MediaRecord r) => r.Category switch
    {
        MatchCategory.BothSameAsset => "hash and name+size",
        MatchCategory.HashOnly => "hash only",
        MatchCategory.NameSizeOnly => r.Ambiguous ? "name+size ambiguous" : "name+size only",
        MatchCategory.Conflict => "CONFLICT (hash asset shown)",
        MatchCategory.NameMatchedSizeDiffers => "name matched, size differs",
        _ => r.Ambiguous ? "name+size ambiguous" : "no match",
    };

    private static (string Names, long? Size) AssetFiles(SqliteConnection workspace, long asset)
    {
        var names = new List<string>();
        long? size = null;

        using (var command = workspace.CreateCommand())
        {
            command.CommandText = "SELECT SizeBytes FROM MediaAsset WHERE MediaAssetID = $id;";
            command.Parameters.AddWithValue("$id", asset);

            if (command.ExecuteScalar() is { } value and not DBNull)
            {
                size = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
        }

        using (var command = workspace.CreateCommand())
        {
            command.CommandText =
                """
                SELECT f.FileName FROM MediaAssetFile AS af
                INNER JOIN MediaFile AS f ON f.MediaFileID = af.MediaFileID
                WHERE af.MediaAssetID = $id
                ORDER BY f.MediaFileID;
                """;
            command.Parameters.AddWithValue("$id", asset);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                names.Add(Cell(reader.GetString(0)));
            }
        }

        return (names.Count == 0 ? "-" : string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase)), size);
    }

    private sealed record MinuteRow(long ID, long? SortID, long? FromMe, long? MessageType, bool HasMedia);

    /// <summary>Every message row of the chat in the local minute. Only these columns are read.</summary>
    private static List<MinuteRow> MinuteMessages(SqliteConnection msgstore, MsgstoreSchema schema, PairingParameters p, DateTime minute)
    {
        var from = new DateTimeOffset(minute, p.UtcOffset).ToUnixTimeMilliseconds();
        var sortID = schema.HasColumn("message", "sort_id") ? "m.sort_id" : "NULL";

        using var command = msgstore.CreateCommand();
        command.CommandText =
            $"""
            SELECT m._id, {sortID}, m.from_me, m.message_type,
                   EXISTS (SELECT 1 FROM message_media AS mm WHERE mm.message_row_id = m._id)
            FROM message AS m
            WHERE m.chat_row_id = $chat AND m.timestamp >= $from AND m.timestamp < $to
            ORDER BY COALESCE({sortID}, m._id), m._id;
            """;
        command.Parameters.AddWithValue("$chat", p.ChatID);
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", from + 60_000);

        var rows = new List<MinuteRow>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new MinuteRow(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.GetInt64(4) != 0));
        }

        return rows;
    }

    private static string Show(long? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "-";

    private static string Show(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Cell(string value) => value.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');

    internal static string Report(ProbeSummary s)
    {
        var text = new StringBuilder();

        text.AppendLine("# Msgstore import - probe");
        text.AppendLine();
        text.AppendLine("Read-only on both databases. Counts only; the detail is in the private probe file.");
        text.AppendLine();
        text.AppendLine("```text");

        foreach (var k in s.Keys)
        {
            var label = (k.MinuteOffset switch { -1 => "minute before", 0 => "TARGET minute", _ => "minute after" })
                        + (k.Outgoing ? ", sent" : ", received");

            text.AppendLine(CultureInfo.InvariantCulture,
                $"{label,-30}attachments {k.Attachments,4}   media records {k.Records,4}   {(k.Attachments == k.Records ? (k.Attachments == 0 ? "empty" : "paired") : "NOT paired")}");
        }

        text.AppendLine();
        text.AppendLine(Text.Line("target minute: resolved attachments", s.TargetResolved));
        text.AppendLine(Text.Line("  distinct resolved assets", s.TargetDistinctAssets));
        text.AppendLine(Text.Line("  most slots sharing one asset", s.TargetMaxSlotsSharingAsset));
        text.AppendLine(Text.Line("target minute: distinct record hashes", s.TargetDistinctHashes));
        text.AppendLine(Text.Line("  most records sharing one hash", s.TargetMaxRecordsSharingHash));
        text.AppendLine(Text.Line("target minute: msgstore message rows", s.MinuteMessages));
        text.AppendLine(Text.Line("  with a media row", s.MinuteMediaMessages));
        text.AppendLine(Text.Line("  without a media row", s.MinuteNonMediaMessages));
        text.AppendLine("```");

        return text.ToString();
    }
}
