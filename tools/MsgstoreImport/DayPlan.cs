using System.Globalization;
using System.Text;

namespace MsgstoreImport;

internal sealed class DayPlanResult
{
    internal required int Attachments { get; init; }
    internal required int HashAndNameSize { get; init; }
    internal required int HashOnly { get; init; }
    internal required Tally Excluded { get; init; }
    internal required DaySequenceResult Alignment { get; init; }
    internal required string Sha256 { get; init; }
    internal int SampleRows { get; init; }
    internal bool SampleWritten { get; init; }
    internal int Rows => HashAndNameSize + HashOnly;
}

/// <summary><c>plan --rule day-sequence</c> (CH-9).</summary>
internal static class DayPlanner
{
    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static DayPlanResult Plan(Options options)
    {
        RequireOutputPaths(options);

        var parameters = options.Parameters!;
        var alignment = Inputs.ComputeDaySequence(options.MsgstorePath!, options.WorkspacePath, parameters);

        return Build(options, parameters, alignment);
    }

    /// <summary>The manifest and sample path guards, before anything is opened.</summary>
    internal static void RequireOutputPaths(Options options)
    {
        if (Planner.ManifestOutRefusal(options.ManifestOut!) is { } refusal)
        {
            throw new RefusalException(refusal);
        }

        if (options.SampleOut is { } sampleOut)
        {
            if (Planner.ManifestOutRefusal(sampleOut, "--sample-out") is { } sampleRefusal)
            {
                throw new RefusalException(sampleRefusal);
            }

            if (string.Equals(Path.GetFullPath(sampleOut), Path.GetFullPath(options.ManifestOut!), StringComparison.OrdinalIgnoreCase))
            {
                throw new RefusalException("--sample-out and --manifest-out must be different files.");
            }
        }
    }

    private static DayPlanResult Build(Options options, PairingParameters parameters, DaySequenceResult alignment)
    {
        var rows = new List<ManifestRow>();
        var excluded = new Tally();

        foreach (var a in alignment.ByAttachment.Values)
        {
            if (a.Eligibility != DayEligibility.Eligible)
            {
                excluded.Add(DaySequence.SlotReason(a.Eligibility, a.Day));
            }
            else if (a.Resolved)
            {
                excluded.Add(Reasons.AlreadyResolved);
            }
            else
            {
                rows.Add(new ManifestRow(a.AttachmentID, a.Asset!.Value, a.MessageID!.Value, a.KeyID!, a.Kind!.Value));
            }
        }

        var bytes = new Manifest { Parameters = parameters, Rows = rows, Rule = PairingRule.DaySequence }.ToBytes();

        using (var stream = new FileStream(options.ManifestOut!, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
        }

        var sha = Manifest.Sha256Hex(bytes);
        var sampleRows = 0;

        if (options.SampleOut is { } path)
        {
            var (text, count) = Sample(rows, alignment, options, sha);
            sampleRows = count;

            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(text);
        }

        return new DayPlanResult
        {
            Attachments = alignment.ByAttachment.Count,
            HashAndNameSize = rows.Count(r => r.Kind == MatchKind.HashAndNameSize),
            HashOnly = rows.Count(r => r.Kind == MatchKind.HashOnly),
            Excluded = excluded,
            Alignment = alignment,
            Sha256 = sha,
            SampleRows = sampleRows,
            SampleWritten = options.SampleOut is not null,
        };
    }

    /// <summary>A seeded partial Fisher-Yates over rows in AttachmentID order (the census's sampler).</summary>
    private static (string Text, int Count) Sample(
        List<ManifestRow> rows, DaySequenceResult alignment, Options options, string sha)
    {
        var random = new Random(options.Seed);
        var pool = rows.OrderBy(r => r.AttachmentID).ToList();
        var take = Math.Min(options.SampleSize, pool.Count);

        for (var i = 0; i < take; i++)
        {
            var j = random.Next(i, pool.Count);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        var chosen = pool.Take(take)
            .Select(r => alignment.ByAttachment[r.AttachmentID])
            .OrderBy(a => a.SlotMinute).ThenBy(a => a.AttachmentID)
            .ToList();

        var text = new StringBuilder();

        text.AppendLine("# Msgstore import (day sequence) - PRIVATE sample");
        text.AppendLine();
        text.AppendLine("**PRIVATE: real identifiers and file names. Keep outside the repository.** Check each row against");
        text.AppendLine("the phone and mark R (right), W (wrong) or ? (cannot tell).");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Seed {options.Seed}, sample size {options.SampleSize}, {take} of {rows.Count} manifest rows, batch tag {Manifest.Tag(sha)}.");
        text.AppendLine("Position = place among that day's attachments in that direction, in export order.");
        text.AppendLine();
        text.AppendLine("| AttachmentID | date | time (export) | direction | position in day | msgstore minute | file name | file_size | matched by | R / W / ? |");
        text.AppendLine("| ---: | --- | --- | --- | --- | --- | --- | ---: | --- | --- |");

        foreach (var a in chosen)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {a.AttachmentID} | {a.SlotMinute:yyyy-MM-dd} | {a.SlotMinute:HH:mm} | {(a.Outgoing ? "sent" : "received")} | " +
                $"{a.Position} of {a.PositionCount} | {a.RecordMinute:HH:mm} | {Cell(a.FileName ?? "-")} | " +
                $"{(a.FileSize is { } size ? size.ToString(CultureInfo.InvariantCulture) : "-")} | " +
                $"{Manifest.KindDescription(a.Kind!.Value)} |  |");
        }

        text.AppendLine();

        return (text.ToString(), take);
    }

    private static string Cell(string value) => value.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');

    internal static string Report(DayPlanResult r)
    {
        var a = r.Alignment;
        var text = new StringBuilder();
        var daysWithMessages = a.Days.Values.Sum();

        text.AppendLine("# Msgstore import - plan (rule: day sequence)");
        text.AppendLine();
        text.AppendLine("Read-only on both databases. Counts only; identifiers are in the private manifest.");
        text.AppendLine("Each local day's messages are lined up position by position (workspace SequenceNumber against");
        text.AppendLine("msgstore sort_id, then _id), all messages, system messages included.");
        text.AppendLine();

        if (a.PriorDisagree > 0)
        {
            var days = a.Days[DayVerdict.PriorDisagreement].ToString(CultureInfo.InvariantCulture);
            var slots = a.PriorDisagree.ToString(CultureInfo.InvariantCulture);

            text.AppendLine(
                $"**ATTENTION: {slots} prior import confirmation(s) line up with a DIFFERENT msgstore record " +
                $"on {days} day(s).** Those days were skipped. This casts doubt on those confirmations; look " +
                "at them before applying anything.");
            text.AppendLine();
        }

        if (a.MinuteDisagree > 0)
        {
            var days = a.Days[DayVerdict.MinutePairingDisagreement].ToString(CultureInfo.InvariantCulture);
            var slots = a.MinuteDisagree.ToString(CultureInfo.InvariantCulture);

            text.AppendLine(
                $"**ATTENTION: {slots} minute-rule pair(s) face a DIFFERENT msgstore record at their day position " +
                $"(days skipped for this reason: {days}).** The minute pairing and the day sequence disagree there; " +
                "look before relying on either.");
            text.AppendLine();
        }

        text.AppendLine("```text");
        text.AppendLine(Text.Line("days with workspace messages", daysWithMessages));
        text.AppendLine(Text.Line("  usable", a.Days[DayVerdict.Usable]));
        text.AppendLine(Text.Line("  skipped", daysWithMessages - a.Days[DayVerdict.Usable]));

        foreach (var verdict in Enum.GetValues<DayVerdict>().Where(v => v != DayVerdict.Usable))
        {
            text.AppendLine(Text.Line("    " + DaySequence.DayReason(verdict), a.Days[verdict]));
        }

        text.AppendLine(Text.Line("days with msgstore messages only (no slots)", a.DaysOnlyInMsgstore));
        text.AppendLine();
        text.AppendLine(Text.Line("prior import confirmations compared", a.PriorAgree + a.PriorDisagree + a.PriorUnreadable));
        text.AppendLine(Text.Line("  agree (same msgstore record)", a.PriorAgree));
        text.AppendLine(Text.Line("  DISAGREE (different record)", a.PriorDisagree));
        text.AppendLine(Text.Line("  unreadable key_id", a.PriorUnreadable));
        text.AppendLine(Text.Line("  not compared (day counts or directions differ)", a.PriorNotCompared));
        text.AppendLine();
        text.AppendLine(Text.Line("minute-rule pairs agreeing with day position", a.MinuteAgree));
        text.AppendLine(Text.Line("minute-rule pairs DISAGREEING with day position", a.MinuteDisagree));
        text.AppendLine(Text.Line("msgstore media rows facing a workspace text line (not a skip)", a.MediaFacingText));
        text.AppendLine();
        text.AppendLine(Text.Line("attachments in the conversation", r.Attachments));
        text.AppendLine(Text.Line("new manifest rows", r.Rows));
        text.AppendLine(Text.Line("  matched by hash and name+size (same asset)", r.HashAndNameSize));
        text.AppendLine(Text.Line("  matched by hash only", r.HashOnly));
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
