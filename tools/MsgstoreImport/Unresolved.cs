using System.Globalization;
using System.Text;

namespace MsgstoreImport;

/// <summary>One unresolved slot, with why no rule confirmed it.</summary>
internal sealed record UnresolvedRow(
    WsAttachment Attachment,
    int Position,
    int PositionCount,
    string PairedBy,
    MediaRecord? Record,
    string Reason);

/// <summary>
/// <c>list-unresolved</c>: read-only. Every attachment of the conversation still unresolved, with
/// the msgstore record it paired with (if any) and the reason, in plain words, that no rule took it.
/// </summary>
internal static class Unresolved
{
    internal const string NoFile = "WhatsApp kept no file";
    internal const string NotRecovered = "file not in any recovered copy";
    internal const string SizeDiffers = "same name found but different size";
    internal const string SeveralSameNameSize = "same name and size found in 2+ files";
    internal const string NameSizeNotReviewed = "same name and size only (no content match), not marked R in the review";
    internal const string Conflict = "content and name+size point to different files";
    internal const string DatedAfter = "file dated after the message";
    internal const string NoDateNotDocument = "file has no date in its name and is not a document";
    internal const string Disproven = "file disproven by Angie";
    internal const string Empty = "matched file is empty (0 bytes)";
    internal const string NoKey = "WhatsApp record has no usable key_id";
    internal const string Gap = "record more than 2 minutes from the message";
    internal const string NoRun = "no Phase 7 run covers this attachment";
    internal const string NotConfirmed = "matched, but not confirmed (revoked, or not in any applied batch)";

    internal static string NotPaired(DayVerdict? day) =>
        day is null ? "not paired (message time unreadable)"
        : day == DayVerdict.Usable ? "not paired"
        : "not paired (minute counts differ; day: " + DaySequence.DayReason(day.Value) + ")";

    internal static string DayNotUsable(DayVerdict? day) =>
        "paired by minute, but the day does not line up (" + (day is { } v ? DaySequence.DayReason(v) : "time unreadable") + ")";

    internal static List<UnresolvedRow> Compute(Options options)
    {
        if (Planner.ManifestOutRefusal(options.ListOut!, "--list-out") is { } refusal)
        {
            throw new RefusalException(refusal);
        }

        return ComputeRows(options.MsgstorePath!, options.WorkspacePath, options.Parameters!);
    }

    /// <summary>The unresolved rows, read-only on both databases; writes nothing.</summary>
    internal static List<UnresolvedRow> ComputeRows(string msgstorePath, string workspacePath, PairingParameters parameters)
    {
        return Inputs.WithPairing(msgstorePath, workspacePath, parameters, pairing =>
        {
            var (attachments, records) = pairing.Load();
            var recordByMessage = records.ToDictionary(r => r.MessageID);
            var minute = pairing.Compute();
            var day = new DaySequence(pairing).Compute().ByAttachment;
            var outside = new OutsideCandidateSet(pairing).Compute();

            // Position in its (minute, direction) key, in export order.
            var positions = attachments
                .Where(a => a.Minute is not null)
                .GroupBy(a => (a.Minute, a.Outgoing))
                .SelectMany(g => g.OrderBy(a => a.SequenceNumber).ThenBy(a => a.Ordinal).ThenBy(a => a.AttachmentID)
                    .Select((a, i) => (a.AttachmentID, Position: i + 1, Count: g.Count())))
                .ToDictionary(x => x.AttachmentID, x => (x.Position, x.Count));

            var rows = new List<UnresolvedRow>();

            foreach (var a in attachments.Where(a => !a.Resolved))
            {
                var m = minute[a.AttachmentID];
                var d = day[a.AttachmentID];
                var minutePaired = m.Eligibility >= Eligibility.PairedNoAsset;
                var dayPaired = d.Day == DayVerdict.Usable && d.MessageID is not null;
                var (position, count) = positions.GetValueOrDefault(a.AttachmentID);

                if (!minutePaired && !dayPaired)
                {
                    rows.Add(new UnresolvedRow(a, position, count, "not paired", null, NotPaired(d.Day)));
                    continue;
                }

                var record = recordByMessage[minutePaired ? m.MessageID!.Value : d.MessageID!.Value];

                rows.Add(new UnresolvedRow(
                    a, position, count, minutePaired ? "minute" : "day sequence", record,
                    Reason(record, outside[a.AttachmentID], pairing.IsDisproven)));
            }

            return rows;
        });
    }

    private static string Reason(MediaRecord record, OutsideAssessment outside, Func<long, long, bool> isDisproven)
    {
        if (!record.HasFilePath && record.Asset is null)
        {
            return NoFile;
        }

        // A disproof is the most direct answer, whatever else holds.
        if (record.Asset is { } asset && isDisproven(outside.AttachmentID, asset))
        {
            return Disproven;
        }

        switch (record.Category)
        {
            case MatchCategory.NoMatch:
                return NotRecovered;
            case MatchCategory.NameMatchedSizeDiffers:
                return SizeDiffers;
            case MatchCategory.NameSizeOnly:
                return record.Asset is null ? SeveralSameNameSize : NameSizeNotReviewed;
            case MatchCategory.Conflict:
                return Conflict;
        }

        // A content (hash) match: the outside rule's assessment says which guard stopped it.
        return outside.Eligibility switch
        {
            OutsideEligibility.DayNotUsable => DayNotUsable(outside.Day),
            OutsideEligibility.MinuteGap => Gap,
            OutsideEligibility.EmptyAsset => Empty,
            OutsideEligibility.NoCompletedRun => NoRun,
            OutsideEligibility.Disproven => Disproven,
            OutsideEligibility.NoKeyID => NoKey,
            OutsideEligibility.NoFileDate => NoDateNotDocument,
            OutsideEligibility.FileDatedAfterMessage => DatedAfter,
            OutsideEligibility.InCandidateSet or OutsideEligibility.Eligible => NotConfirmed,
            _ => OutsideCandidateSet.Reason(outside),
        };
    }

    private static string Cell(string? value) =>
        value is null ? "-" : value.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');

    internal static string List(List<UnresolvedRow> rows)
    {
        var text = new StringBuilder();

        text.AppendLine("Every attachment still unresolved, with the WhatsApp record it paired with and why no rule took it; mark the last column after looking on the phones.");
        text.AppendLine();
        text.AppendLine("# Msgstore import - unresolved attachments - PRIVATE");
        text.AppendLine();
        text.AppendLine("| AttachmentID | date | time | direction | position in minute | paired by | record file name | file_size | mime | message_type | media_name | why unresolved | found on a phone? (which phone / no) |");
        text.AppendLine("| ---: | --- | --- | --- | --- | --- | --- | ---: | --- | ---: | --- | --- | --- |");

        foreach (var r in rows.OrderBy(r => r.Attachment.Minute ?? DateTime.MaxValue).ThenBy(r => r.Attachment.SequenceNumber).ThenBy(r => r.Attachment.AttachmentID))
        {
            var a = r.Attachment;
            var rec = r.Record;
            var fileName = rec is null ? "-" : rec.HasFilePath ? Cell(rec.FileName) : "- (WhatsApp kept no file)";

            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {a.AttachmentID} | {(a.Minute is { } t ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "-")} | " +
                $"{(a.Minute is { } h ? h.ToString("HH:mm", CultureInfo.InvariantCulture) : "-")} | {(a.Outgoing ? "sent" : "received")} | " +
                $"{(r.PositionCount == 0 ? "-" : $"{r.Position} of {r.PositionCount}")} | {r.PairedBy} | {fileName} | " +
                $"{(rec?.FileSize is { } size ? size.ToString(CultureInfo.InvariantCulture) : "-")} | {Cell(rec?.MimeType)} | " +
                $"{(rec?.MessageType is { } type ? type.ToString(CultureInfo.InvariantCulture) : "-")} | {Cell(rec?.MediaName)} | {r.Reason} |  |");
        }

        return text.ToString().Replace("\r\n", "\n");
    }

    internal static string Report(List<UnresolvedRow> rows)
    {
        var text = new StringBuilder();

        void Group(string title, IEnumerable<string> keys)
        {
            text.AppendLine(title);

            foreach (var g in keys.GroupBy(k => k).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                text.AppendLine(Text.Line("  " + g.Key, g.Count()));
            }

            text.AppendLine();
        }

        text.AppendLine("# Msgstore import - list-unresolved");
        text.AppendLine();
        text.AppendLine("Read-only. Counts only; the rows are in the private list.");
        text.AppendLine();
        text.AppendLine("```text");
        text.AppendLine(Text.Line("unresolved attachments", rows.Count));
        text.AppendLine();
        Group("by reason:", rows.Select(r => r.Reason));
        Group("by direction:", rows.Select(r => r.Attachment.Outgoing ? "sent" : "received"));
        Group("by msgstore mime class:", rows.Select(r => r.Record is null ? "not paired" : ExplainNoDate.MimeClass(r.Record.MimeType)));
        Group("by year:", rows.Select(r => r.Attachment.Minute is { } t ? t.Year.ToString(CultureInfo.InvariantCulture) : "time unreadable"));
        text.AppendLine("```");

        return text.ToString();
    }

    /// <summary>Computes, writes the private list (never overwriting), and returns the stdout report.</summary>
    internal static string Run(Options options)
    {
        var rows = Compute(options);

        using (var stream = new FileStream(options.ListOut!, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(List(rows));
        }

        return Report(rows);
    }
}
