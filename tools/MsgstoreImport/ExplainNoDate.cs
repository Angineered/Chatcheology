using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MsgstoreImport;

/// <summary>What kind of name an asset's copies carry, best class first.</summary>
internal enum NamePattern
{
    /// <summary>Camera or screenshot style with an embedded date (IMG_YYYYMMDD_HHMMSS and similar).</summary>
    CameraDate,

    /// <summary>Some other plausible date in the name (20YYMMDD or YYYY-MM-DD).</summary>
    OtherDate,

    /// <summary>Document-like extension, no date.</summary>
    Document,

    /// <summary>Voice note or audio, no date.</summary>
    Audio,

    /// <summary>No date at all.</summary>
    NoDate,
}

/// <summary>One slot excluded by the outside rule only for "no naming-derived date".</summary>
internal sealed record NoDateRow(
    OutsideAssessment Slot,
    string MimeClass,
    long? MessageType,
    string? MimeType,
    long? AssetSize,
    List<(string FileName, long MediaSourceID)> Copies,
    NamePattern Pattern,
    DateOnly? EmbeddedDate,
    int? GapDays);

/// <summary>
/// <c>explain-nodate</c>: read-only. Explains the slots the outside-candidate-set rule excludes only
/// because the asset has no naming-derived date: what the copies are called, and whether those names
/// carry some other date.
/// </summary>
internal static partial class ExplainNoDate
{
    [GeneratedRegex(@"^(?:IMG|VID)_(\d{8})_\d{6}", RegexOptions.IgnoreCase)]
    private static partial Regex CameraUnderscore();

    [GeneratedRegex(@"^(\d{8})_\d{6}")]
    private static partial Regex BareCamera();

    [GeneratedRegex(@"^(?:PXL|VID|Screenshot)_(\d{8})", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixedCompact();

    [GeneratedRegex(@"^Screenshot_(\d{4}-\d{2}-\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex ScreenshotDashed();

    [GeneratedRegex(@"(?<!\d)(20\d{6})(?!\d)")]
    private static partial Regex CompactDate();

    [GeneratedRegex(@"(?<!\d)(\d{4}-\d{2}-\d{2})(?!\d)")]
    private static partial Regex DashedDate();

    private static readonly string[] DocumentExtensions =
        [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv", ".rtf", ".odt", ".ods", ".zip", ".vcf", ".apk"];

    private static readonly string[] AudioExtensions =
        [".opus", ".ogg", ".m4a", ".mp3", ".aac", ".amr", ".wav", ".3ga"];

    /// <summary>One file name's class and the date it embeds, if any.</summary>
    internal static (NamePattern Pattern, DateOnly? Date) Classify(string fileName)
    {
        foreach (var (regex, format) in new[]
                 {
                     (CameraUnderscore(), "yyyyMMdd"), (BareCamera(), "yyyyMMdd"),
                     (ScreenshotDashed(), "yyyy-MM-dd"), (PrefixedCompact(), "yyyyMMdd"),
                 })
        {
            if (regex.Match(fileName) is { Success: true } m && Parse(m.Groups[1].Value, format) is { } date)
            {
                return (NamePattern.CameraDate, date);
            }
        }

        foreach (Match m in CompactDate().Matches(fileName))
        {
            if (Parse(m.Groups[1].Value, "yyyyMMdd") is { } date)
            {
                return (NamePattern.OtherDate, date);
            }
        }

        foreach (Match m in DashedDate().Matches(fileName))
        {
            if (Parse(m.Groups[1].Value, "yyyy-MM-dd") is { } date)
            {
                return (NamePattern.OtherDate, date);
            }
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (DocumentExtensions.Contains(extension))
        {
            return (NamePattern.Document, null);
        }

        if (AudioExtensions.Contains(extension)
            || fileName.StartsWith("PTT-", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("AUD-", StringComparison.OrdinalIgnoreCase))
        {
            return (NamePattern.Audio, null);
        }

        return (NamePattern.NoDate, null);
    }

    /// <summary>A date between 2000 and 2099 that exists on the calendar, or null.</summary>
    private static DateOnly? Parse(string text, string format) =>
        DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        && date.Year is >= 2000 and <= 2099
            ? date
            : null;

    internal static List<NoDateRow> Compute(Options options)
    {
        if (Planner.ManifestOutRefusal(options.DetailOut!, "--detail-out") is { } refusal)
        {
            throw new RefusalException(refusal);
        }

        return Inputs.WithPairing(options.MsgstorePath!, options.WorkspacePath, options.Parameters!, pairing =>
        {
            var outside = new OutsideCandidateSet(pairing).Compute();
            var records = pairing.Load().Records.ToDictionary(r => r.MessageID);
            var rows = new List<NoDateRow>();

            foreach (var slot in outside.Values.Where(a => a.Eligibility == OutsideEligibility.NoFileDate && !a.Resolved))
            {
                var record = records[slot.MessageID!.Value];
                var copies = Copies(pairing, slot.Asset!.Value, out var size);

                // The asset's class is the best class among its copies; its date comes from the
                // first copy (by MediaFileID) of that class.
                var classified = copies.Select(c => Classify(c.FileName)).ToList();
                var best = classified.Count == 0 ? (NamePattern.NoDate, (DateOnly?)null) : classified.MinBy(c => c.Pattern);
                var gap = best.Item2 is { } embedded ? embedded.DayNumber - DateOnly.FromDateTime(slot.SlotMinute!.Value).DayNumber : (int?)null;

                rows.Add(new NoDateRow(
                    slot, MimeClass(record.MimeType), record.MessageType, record.MimeType, size, copies,
                    best.Item1, best.Item2, gap));
            }

            return rows;
        });
    }

    private static List<(string FileName, long MediaSourceID)> Copies(Pairing pairing, long asset, out long? size)
    {
        using (var command = pairing.Workspace.CreateCommand())
        {
            command.CommandText = "SELECT SizeBytes FROM MediaAsset WHERE MediaAssetID = $a;";
            command.Parameters.AddWithValue("$a", asset);
            size = command.ExecuteScalar() is { } v and not DBNull ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : null;
        }

        var copies = new List<(string, long)>();

        using (var command = pairing.Workspace.CreateCommand())
        {
            command.CommandText =
                """
                SELECT f.FileName, f.MediaSourceID
                FROM MediaAssetFile AS af
                INNER JOIN MediaFile AS f ON f.MediaFileID = af.MediaFileID
                WHERE af.MediaAssetID = $a
                ORDER BY f.MediaFileID;
                """;
            command.Parameters.AddWithValue("$a", asset);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                copies.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }

        return copies;
    }

    /// <summary>The census's mime classes.</summary>
    internal static string MimeClass(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime))
        {
            return "null";
        }

        var lower = mime.Trim().ToLowerInvariant();

        foreach (var prefix in new[] { "image", "video", "audio", "application" })
        {
            if (lower.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                return prefix;
            }
        }

        return "other";
    }

    internal static string PatternLabel(NamePattern p) => p switch
    {
        NamePattern.CameraDate => "camera/screenshot style with embedded date",
        NamePattern.OtherDate => "other name with a plausible date",
        NamePattern.Document => "document-like, no date",
        NamePattern.Audio => "voice note or audio, no date",
        NamePattern.NoDate => "no date at all",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    internal static string GapBucket(int gap) => gap switch
    {
        0 => "same day (gap 0)",
        -1 or 1 => "+/-1 day",
        <= -2 and >= -30 => "older by 2-30 days",
        < -30 => "older by more than 30 days",
        _ => "newer by more than 1 day",
    };

    private static readonly string[] GapBuckets =
        ["same day (gap 0)", "+/-1 day", "older by 2-30 days", "older by more than 30 days", "newer by more than 1 day"];

    internal static string Report(List<NoDateRow> rows)
    {
        var text = new StringBuilder();

        void Group(string title, IEnumerable<string> keys)
        {
            text.AppendLine(title);

            foreach (var g in keys.GroupBy(k => k).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                text.AppendLine(Text.Line("  " + g.Key, g.Count()));
            }

            text.AppendLine();
        }

        text.AppendLine("# Msgstore import - explain-nodate");
        text.AppendLine();
        text.AppendLine("Read-only. Slots the outside-candidate-set rule excludes ONLY for \"file has no naming-derived");
        text.AppendLine("date\". Counts only; names and paths are in the private detail file.");
        text.AppendLine();
        text.AppendLine("```text");
        text.AppendLine(Text.Line("slots", rows.Count));
        text.AppendLine();

        Group("by msgstore mime class / message_type:",
            rows.Select(r => $"{r.MimeClass} / type {(r.MessageType is { } t ? t.ToString(CultureInfo.InvariantCulture) : "null")}"));
        Group("by match kind:", rows.Select(r => Manifest.KindDescription(r.Slot.Kind!.Value)));
        Group("by direction:", rows.Select(r => r.Slot.Outgoing ? "sent" : "received"));

        text.AppendLine("by file-name pattern of the asset's copies:");

        foreach (var pattern in Enum.GetValues<NamePattern>())
        {
            text.AppendLine(Text.Line("  " + PatternLabel(pattern), rows.Count(r => r.Pattern == pattern)));
        }

        text.AppendLine();

        foreach (var pattern in new[] { NamePattern.CameraDate, NamePattern.OtherDate })
        {
            text.AppendLine($"embedded date against message date ({PatternLabel(pattern)}):");

            foreach (var bucket in GapBuckets)
            {
                text.AppendLine(Text.Line("  " + bucket, rows.Count(r => r.Pattern == pattern && r.GapDays is { } g && GapBucket(g) == bucket)));
            }

            text.AppendLine();
        }

        text.AppendLine(Text.Line("assets shared by 2+ of these slots",
            rows.GroupBy(r => r.Slot.Asset).Count(g => g.Count() >= 2)));
        text.AppendLine();

        text.AppendLine("copies by MediaSource id (distinct copies / slots with a copy there):");

        foreach (var source in rows.SelectMany(r => r.Copies.Select(c => c.MediaSourceID)).Distinct().Order())
        {
            var copies = rows.DistinctBy(r => r.Slot.Asset).Sum(r => r.Copies.Count(c => c.MediaSourceID == source));
            var slots = rows.Count(r => r.Copies.Any(c => c.MediaSourceID == source));

            text.AppendLine(
                $"{("  source " + source.ToString(CultureInfo.InvariantCulture)),-58}" +
                $"{copies.ToString("N0", CultureInfo.InvariantCulture)} / {slots.ToString("N0", CultureInfo.InvariantCulture)}");
        }

        text.AppendLine("```");

        return text.ToString();
    }

    internal static string Detail(List<NoDateRow> rows, Options options)
    {
        var text = new StringBuilder();
        var p = options.Parameters!;

        text.AppendLine("# Msgstore import - explain-nodate - PRIVATE detail");
        text.AppendLine();
        text.AppendLine("**PRIVATE: real identifiers and file names. Keep outside the repository.** Read-only; nothing was written.");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Conversation {p.ConversationID}, chat _id {p.ChatID}, UTC offset {Options.FormatOffset(p.UtcOffset)}, local participant {p.LocalParticipantID}. {rows.Count} slot(s).");
        text.AppendLine();
        text.AppendLine("| AttachmentID | date | time | direction | record file name | copy file names (MediaSource id) | embedded date | pattern | gap (days) | size | mime | match kind |");
        text.AppendLine("| ---: | --- | --- | --- | --- | --- | --- | --- | ---: | ---: | --- | --- |");

        foreach (var r in rows.OrderBy(r => r.Slot.SlotMinute).ThenBy(r => r.Slot.AttachmentID))
        {
            var s = r.Slot;
            var copies = string.Join("; ", r.Copies.Select(c => $"{Cell(c.FileName)} ({c.MediaSourceID.ToString(CultureInfo.InvariantCulture)})"));

            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {s.AttachmentID} | {s.SlotMinute:yyyy-MM-dd} | {s.SlotMinute:HH:mm} | {(s.Outgoing ? "sent" : "received")} | " +
                $"{Cell(s.FileName ?? "-")} | {(copies.Length == 0 ? "-" : copies)} | " +
                $"{(r.EmbeddedDate is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "-")} | {PatternLabel(r.Pattern)} | " +
                $"{(r.GapDays is { } g ? g.ToString(CultureInfo.InvariantCulture) : "-")} | " +
                $"{(r.AssetSize is { } size ? size.ToString(CultureInfo.InvariantCulture) : "-")} | {Cell(r.MimeType ?? "-")} | " +
                $"{Manifest.KindDescription(s.Kind!.Value)} |");
        }

        text.AppendLine();

        return text.ToString();
    }

    private static string Cell(string value) => value.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>Computes, writes the private detail (never overwriting), and returns the stdout report.</summary>
    internal static string Run(Options options)
    {
        var rows = Compute(options);

        using (var stream = new FileStream(options.DetailOut!, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(Detail(rows, options));
        }

        return Report(rows);
    }
}
