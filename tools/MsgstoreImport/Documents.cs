using System.Globalization;
using System.Text;

namespace MsgstoreImport;

/// <summary>One slot under the documents rule.</summary>
internal sealed record DocumentAssessment(
    OutsideAssessment Slot, bool Eligible, string? Reason, long? MessageType, string? MediaName, string? MimeType)
{
    internal bool DaySkipped => Slot.Eligibility == OutsideEligibility.DayNotUsable;
    internal bool Disproven => Slot.Eligibility == OutsideEligibility.Disproven;
}

/// <summary>
/// The documents rule (addendum, approved 2026-10-14): exactly the slots the outside-candidate-set
/// rule excludes only for "file has no naming-derived date", where the msgstore message is a
/// document (message_type 9). WhatsApp keeps a document's original name, so it never carries a
/// <c>-YYYYMMDD-WA</c> date; the date check is dropped and every other outside guard still holds.
/// </summary>
internal sealed class Documents(Pairing pairing)
{
    internal const long DocumentMessageType = 9;

    internal const string NotDocument = "no-date file, but the msgstore message is not a document (type 9)";
    internal const string Dated = "file has a naming-derived date (outside rule decides)";

    internal SortedDictionary<long, DocumentAssessment> Compute()
    {
        var outside = new OutsideCandidateSet(pairing).Compute();
        var records = pairing.Load().Records.ToDictionary(r => r.MessageID);
        var result = new SortedDictionary<long, DocumentAssessment>();

        foreach (var slot in outside.Values)
        {
            var record = slot.MessageID is { } id ? records.GetValueOrDefault(id) : null;

            var (eligible, reason) = slot.Eligibility switch
            {
                OutsideEligibility.NoFileDate when record?.MessageType == DocumentMessageType => (true, (string?)null),
                OutsideEligibility.NoFileDate => (false, NotDocument),
                OutsideEligibility.Eligible or OutsideEligibility.FileDatedAfterMessage => (false, Dated),
                _ => (false, OutsideCandidateSet.Reason(slot)),
            };

            result[slot.AttachmentID] = new DocumentAssessment(slot, eligible, reason, record?.MessageType, record?.MediaName, record?.MimeType);
        }

        return result;
    }
}

internal sealed class DocumentsPlanResult
{
    internal required int Attachments { get; init; }
    internal required int HashAndNameSize { get; init; }
    internal required int HashOnly { get; init; }
    internal required int ByMinute { get; init; }
    internal required int ByDaySequence { get; init; }
    internal required Tally Excluded { get; init; }
    internal required Dictionary<string, int> ByMime { get; init; }
    internal required int AssetsOnTwoOrMoreRows { get; init; }
    internal required string Sha256 { get; init; }
    internal int SampleRows { get; init; }
    internal bool SampleWritten { get; init; }
    internal int Rows => HashAndNameSize + HashOnly;
}

/// <summary><c>plan --rule documents</c>.</summary>
internal static class DocumentsPlanner
{
    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static DocumentsPlanResult Plan(Options options)
    {
        DayPlanner.RequireOutputPaths(options);

        var parameters = options.Parameters!;
        var assessed = Inputs.ComputeDocuments(options.MsgstorePath!, options.WorkspacePath, parameters);

        var rows = new List<ManifestRow>();
        var chosen = new List<DocumentAssessment>();
        var excluded = new Tally();

        foreach (var a in assessed.Values)
        {
            var s = a.Slot;

            if (!a.Eligible)
            {
                excluded.Add(a.Reason!);
            }
            else if (s.Resolved)
            {
                excluded.Add(Reasons.AlreadyResolved);
            }
            else
            {
                rows.Add(new ManifestRow(s.AttachmentID, s.Asset!.Value, s.MessageID!.Value, s.KeyID!, s.Kind!.Value, s.PairedBy));
                chosen.Add(a);
            }
        }

        var bytes = new Manifest { Parameters = parameters, Rows = rows, Rule = PairingRule.Documents }.ToBytes();

        using (var stream = new FileStream(options.ManifestOut!, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
        }

        var sha = Manifest.Sha256Hex(bytes);
        var sampleRows = 0;

        if (options.SampleOut is { } path)
        {
            var (text, count) = Sample(chosen, options, sha);
            sampleRows = count;

            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(text);
        }

        return new DocumentsPlanResult
        {
            Attachments = assessed.Count,
            HashAndNameSize = rows.Count(r => r.Kind == MatchKind.HashAndNameSize),
            HashOnly = rows.Count(r => r.Kind == MatchKind.HashOnly),
            ByMinute = rows.Count(r => r.PairedBy == PairingRule.Minute),
            ByDaySequence = rows.Count(r => r.PairedBy == PairingRule.DaySequence),
            Excluded = excluded,
            ByMime = chosen.GroupBy(a => ExplainNoDate.MimeClass(a.MimeType)).ToDictionary(g => g.Key, g => g.Count()),
            AssetsOnTwoOrMoreRows = rows.GroupBy(r => r.MediaAssetID).Count(g => g.Count() >= 2),
            Sha256 = sha,
            SampleRows = sampleRows,
            SampleWritten = options.SampleOut is not null,
        };
    }

    private static (string Text, int Count) Sample(List<DocumentAssessment> rows, Options options, string sha)
    {
        var random = new Random(options.Seed);
        var pool = rows.OrderBy(r => r.Slot.AttachmentID).ToList();
        var take = Math.Min(options.SampleSize, pool.Count);

        for (var i = 0; i < take; i++)
        {
            var j = random.Next(i, pool.Count);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        static string Cell(string? value) => value is null ? "-" : value.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');

        var text = new StringBuilder();

        text.AppendLine("# Msgstore import (documents) - PRIVATE sample");
        text.AppendLine();
        text.AppendLine("**PRIVATE: real identifiers and file names. Keep outside the repository.** Check each row on the");
        text.AppendLine("phone by the document name in the bubble and mark R (right), W (wrong) or ? (cannot tell).");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Seed {options.Seed}, sample size {options.SampleSize}, {take} of {rows.Count} manifest rows, batch tag {Manifest.Tag(sha)}.");
        text.AppendLine("Position = place among that day's attachments in that direction, in export order.");
        text.AppendLine();
        text.AppendLine("| AttachmentID | date | time (export) | direction | position in day | record file name | media_name | file_size | mime | matched by | R / W / ? |");
        text.AppendLine("| ---: | --- | --- | --- | --- | --- | --- | ---: | --- | --- | --- |");

        foreach (var a in pool.Take(take).OrderBy(a => a.Slot.SlotMinute).ThenBy(a => a.Slot.AttachmentID))
        {
            var s = a.Slot;

            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {s.AttachmentID} | {s.SlotMinute:yyyy-MM-dd} | {s.SlotMinute:HH:mm} | {(s.Outgoing ? "sent" : "received")} | " +
                $"{s.Position} of {s.PositionCount} | {Cell(s.FileName)} | {Cell(a.MediaName)} | " +
                $"{(s.FileSize is { } size ? size.ToString(CultureInfo.InvariantCulture) : "-")} | {Cell(a.MimeType)} | " +
                $"{Manifest.KindDescription(s.Kind!.Value)} |  |");
        }

        text.AppendLine();

        return (text.ToString(), take);
    }

    internal static string Report(DocumentsPlanResult r)
    {
        var text = new StringBuilder();

        text.AppendLine("# Msgstore import - plan (rule: documents)");
        text.AppendLine();
        text.AppendLine("Read-only on both databases. Counts only; identifiers are in the private manifest. Each row is a");
        text.AppendLine("slot the outside rule excludes only for 'no naming-derived date', whose msgstore message is a");
        text.AppendLine("document (message_type 9). Every other outside guard holds.");
        text.AppendLine();
        text.AppendLine("```text");
        text.AppendLine(Text.Line("attachments in the conversation", r.Attachments));
        text.AppendLine(Text.Line("new manifest rows", r.Rows));
        text.AppendLine(Text.Line("  matched by hash and name+size (same asset)", r.HashAndNameSize));
        text.AppendLine(Text.Line("  matched by hash only", r.HashOnly));
        text.AppendLine(Text.Line("  paired by the minute rule", r.ByMinute));
        text.AppendLine(Text.Line("  paired by day sequence", r.ByDaySequence));

        foreach (var (mime, count) in r.ByMime.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            text.AppendLine(Text.Line("  msgstore mime class " + mime, count));
        }

        text.AppendLine();
        text.AppendLine(Text.Line("excluded", r.Excluded.Total));

        foreach (var (reason, count) in r.Excluded.Items)
        {
            text.AppendLine(Text.Line("  " + reason, count));
        }

        text.AppendLine();
        text.AppendLine(Text.Line("rows + excluded = attachments", r.Rows + r.Excluded.Total));
        text.AppendLine(r.Rows + r.Excluded.Total == r.Attachments
            ? "reconciliation                                            OK"
            : "reconciliation                                            MISMATCH");
        text.AppendLine(Text.Line("assets named by 2+ manifest rows (informational)", r.AssetsOnTwoOrMoreRows));

        if (r.SampleWritten)
        {
            text.AppendLine(Text.Line("private sample rows written", r.SampleRows));
        }

        text.AppendLine("```");
        text.AppendLine();
        text.AppendLine("manifest SHA-256: " + r.Sha256);
        text.AppendLine("batch tag:        " + Manifest.Tag(r.Sha256));

        return text.ToString();
    }
}
