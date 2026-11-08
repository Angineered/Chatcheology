using System.Globalization;
using System.Text;

namespace MsgstoreImport;

/// <summary>One slot under the name+size review rule.</summary>
internal sealed record NameSizeAssessment(
    OutsideAssessment Slot,
    bool Eligible,
    string? Reason,
    bool InCandidateSet,
    long? LatestRun,
    long? AssetSize,
    DateOnly? NamingDate,
    List<string> CopyPaths)
{
    internal bool DaySkipped => Slot.Eligibility == OutsideEligibility.DayNotUsable;
    internal bool Disproven => Reason == OutsideCandidateSet.Reason(Slot with { Eligibility = OutsideEligibility.Disproven });
}

/// <summary>A mark Angie put on a review-list row.</summary>
internal enum ReviewMark
{
    Right,
    Wrong,
    Unsure,
    Blank,
    Other,
}

internal sealed record ReviewRow(long AttachmentID, long MediaAssetID, string KeyID, ReviewMark Mark);

/// <summary>
/// The name+size-only matches (addendum, approved 2026-10-14). No automatic rule: a private review
/// list goes to Angie, and only rows she marks R are imported, after a full re-check.
/// </summary>
/// <remarks>
/// Population: unresolved slots paired by the minute rule or by day sequence, on a day that passes
/// every day-sequence check (and, when paired by day sequence, within the minute gap), whose record
/// matched exactly one asset by name and exact size and none by hash, where that asset is not empty,
/// not disproven for the slot, and the record has a usable key_id. Candidate-set membership is not a
/// guard; it is shown, because it tells Angie whether the viewer lists the file.
/// </remarks>
internal sealed class NameSize(Pairing pairing)
{
    internal const string ReviewedActorLabel = ActorLabels.ReviewedNameSize;
    internal const string NotNameSize = "matched by hash (earlier rules)";
    internal const string Ambiguous = "name+size matches 2+ assets (no single proposal)";

    internal SortedDictionary<long, NameSizeAssessment> Compute()
    {
        var outside = new OutsideCandidateSet(pairing).Compute();
        var result = new SortedDictionary<long, NameSizeAssessment>();

        foreach (var slot in outside.Values)
        {
            NameSizeAssessment Excluded(string reason) =>
                new(slot, false, reason, false, null, null, null, []);

            if (slot.Eligibility < OutsideEligibility.NameSizeOnly)
            {
                result[slot.AttachmentID] = Excluded(OutsideCandidateSet.Reason(slot));
                continue;
            }

            if (slot.Eligibility != OutsideEligibility.NameSizeOnly)
            {
                result[slot.AttachmentID] = Excluded(NotNameSize);
                continue;
            }

            if (slot.Asset is not { } asset)
            {
                result[slot.AttachmentID] = Excluded(Ambiguous);
                continue;
            }

            var (size, namingDate, paths) = AssetFacts(asset);
            var inSet = pairing.CandidateSet(slot.AttachmentID, asset) is null;

            string? reason =
                size is not > 0 ? OutsideCandidateSet.Reason(slot with { Eligibility = OutsideEligibility.EmptyAsset })
                : pairing.IsDisproven(slot.AttachmentID, asset) ? OutsideCandidateSet.Reason(slot with { Eligibility = OutsideEligibility.Disproven })
                // A key_id with a pipe would break the list's table; line breaks never pass UsableKeyID.
                : slot.KeyID is null || slot.KeyID.Contains('|') || slot.KeyID.Contains('\n') || slot.KeyID.Contains('\r')
                    ? OutsideCandidateSet.Reason(slot with { Eligibility = OutsideEligibility.NoKeyID })
                : null;

            result[slot.AttachmentID] = new NameSizeAssessment(
                slot, reason is null, reason, inSet, pairing.LatestRun(slot.AttachmentID), size, namingDate, paths);
        }

        return result;
    }

    private (long? Size, DateOnly? NamingDate, List<string> Paths) AssetFacts(long asset)
    {
        long? size;

        using (var command = pairing.Workspace.CreateCommand())
        {
            command.CommandText = "SELECT SizeBytes FROM MediaAsset WHERE MediaAssetID = $a;";
            command.Parameters.AddWithValue("$a", asset);
            size = command.ExecuteScalar() is { } v and not DBNull ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : null;
        }

        DateOnly? earliest = null;
        var paths = new List<string>();

        using (var command = pairing.Workspace.CreateCommand())
        {
            command.CommandText =
                """
                SELECT s.RootPath, f.RelativePath, f.FileDate
                FROM MediaAssetFile AS af
                INNER JOIN MediaFile AS f ON f.MediaFileID = af.MediaFileID
                INNER JOIN MediaSource AS s ON s.MediaSourceID = f.MediaSourceID
                WHERE af.MediaAssetID = $a
                ORDER BY f.MediaFileID;
                """;
            command.Parameters.AddWithValue("$a", asset);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                paths.Add(Path.Combine(reader.GetString(0), reader.GetString(1).Replace('/', Path.DirectorySeparatorChar)));

                if (!reader.IsDBNull(2)
                    && DateOnly.TryParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                    && (earliest is null || d < earliest))
                {
                    earliest = d;
                }
            }
        }

        return (size, earliest, paths);
    }

    // -------------------------------------------------------------------------------------------
    // The review list.
    // -------------------------------------------------------------------------------------------

    internal const string ListTitle = "# Msgstore import - name+size review list - PRIVATE";

    internal const string Instructions =
        "Open each file and compare it with the phone; in the last column type R (right), W (wrong) or ? (cannot tell). Change nothing else.";

    internal const int Columns = 13;

    internal const string ListHeader =
        "| AttachmentID | date | time | direction | position in day | MediaAssetID | proposed file name | size | naming date | in candidate set (viewer lists it) | copy path(s) | msgstore key_id | R / W / ? |";

    internal static string List(IEnumerable<NameSizeAssessment> rows)
    {
        static string Cell(string value) => value.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');

        var text = new StringBuilder();

        text.Append(Instructions).Append('\n');
        text.Append('\n');
        text.Append(ListTitle).Append('\n');
        text.Append('\n');
        text.Append(ListHeader).Append('\n');
        text.Append("| ---: | --- | --- | --- | --- | ---: | --- | ---: | --- | --- | --- | --- | --- |").Append('\n');

        foreach (var a in rows.OrderBy(r => r.Slot.SlotMinute).ThenBy(r => r.Slot.AttachmentID))
        {
            var s = a.Slot;

            text.Append(CultureInfo.InvariantCulture,
                $"| {s.AttachmentID} | {s.SlotMinute:yyyy-MM-dd} | {s.SlotMinute:HH:mm} | {(s.Outgoing ? "sent" : "received")} | " +
                $"{s.Position} of {s.PositionCount} | {s.Asset} | {Cell(s.FileName ?? "-")} | {Show(a.AssetSize)} | " +
                $"{(a.NamingDate is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "-")} | {(a.InCandidateSet ? "yes" : "no")} | " +
                $"{Cell(string.Join("; ", a.CopyPaths))} | {s.KeyID} |  |").Append('\n');
        }

        return text.ToString();
    }

    private static string Show(long? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "-";

    /// <summary>
    /// Parses a marked review list. Returns the rows and the SHA-256 of the list as it was written
    /// (the marks cleared, line endings normalised), which identifies the list in the basis text.
    /// </summary>
    /// <exception cref="FormatException">The file is not a review list, or a row is malformed.</exception>
    internal static (List<ReviewRow> Rows, string ListSha256) Parse(byte[] bytes)
    {
        var text = new UTF8Encoding(false).GetString(bytes).TrimStart('﻿').Replace("\r\n", "\n");
        var lines = text.Split('\n');
        var rows = new List<ReviewRow>();
        var original = new StringBuilder();
        var sawHeader = false;

        if (!lines.Contains(ListTitle) || !lines.Contains(ListHeader))
        {
            throw new FormatException("This is not a name+size review list written by this tool.");
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var isLast = i == lines.Length - 1;

            if (line == ListHeader)
            {
                sawHeader = true;
            }

            if (!sawHeader || !line.StartsWith("| ", StringComparison.Ordinal) || line == ListHeader || line.StartsWith("| ---", StringComparison.Ordinal))
            {
                original.Append(line);

                if (!isLast)
                {
                    original.Append('\n');
                }

                continue;
            }

            var parts = line.Split('|');

            // "| c1 | ... | c12 | mark |" splits into 15 parts; with the closing pipe dropped, 14.
            if (parts.Length is not (Columns + 1 or Columns + 2))
            {
                throw new FormatException("A review-list row has the wrong number of columns.");
            }

            var cell = parts[Columns].Trim();
            var trailing = parts.Length == Columns + 2 ? parts[Columns + 1].Trim() : "";
            var markText = cell.Length > 0 && trailing.Length > 0 && !string.Equals(cell, trailing, StringComparison.OrdinalIgnoreCase)
                ? "\u0000conflict"
                : cell.Length > 0 ? cell : trailing;

            var mark = markText.ToUpperInvariant() switch
            {
                "R" => ReviewMark.Right,
                "W" => ReviewMark.Wrong,
                "?" => ReviewMark.Unsure,
                "" => ReviewMark.Blank,
                _ => ReviewMark.Other,
            };

            if (!long.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var attachment)
                || !long.TryParse(parts[6].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var asset))
            {
                throw new FormatException("A review-list row has no readable AttachmentID or MediaAssetID.");
            }

            if (rows.Any(r => r.AttachmentID == attachment))
            {
                throw new FormatException("The review list names an attachment twice.");
            }

            rows.Add(new ReviewRow(attachment, asset, parts[12].Trim(), mark));

            original.Append(string.Join('|', parts[..Columns])).Append("|  |");

            if (!isLast)
            {
                original.Append('\n');
            }
        }

        return (rows, Manifest.Sha256Hex(new UTF8Encoding(false).GetBytes(original.ToString())));
    }

    internal static string MarkLabel(ReviewMark mark) => mark switch
    {
        ReviewMark.Right => "marked R",
        ReviewMark.Wrong => "marked W (excluded)",
        ReviewMark.Unsure => "marked ? (excluded)",
        ReviewMark.Blank => "not marked (excluded)",
        _ => "unreadable mark (excluded)",
    };
}

internal sealed class ReviewListResult
{
    internal required int Attachments { get; init; }
    internal required int Rows { get; init; }
    internal required int InCandidateSet { get; init; }
    internal required Tally Excluded { get; init; }
    internal required string Sha256 { get; init; }
}

internal static class ReviewLister
{
    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static ReviewListResult Run(Options options)
    {
        if (Planner.ManifestOutRefusal(options.ReviewOut!, "--review-out") is { } refusal)
        {
            throw new RefusalException(refusal);
        }

        var assessed = Inputs.ComputeNameSize(options.MsgstorePath!, options.WorkspacePath, options.Parameters!);
        var excluded = new Tally();
        var rows = new List<NameSizeAssessment>();

        foreach (var a in assessed.Values)
        {
            if (!a.Eligible)
            {
                excluded.Add(a.Reason!);
            }
            else if (a.Slot.Resolved)
            {
                excluded.Add(Reasons.AlreadyResolved);
            }
            else
            {
                rows.Add(a);
            }
        }

        var bytes = new UTF8Encoding(false).GetBytes(NameSize.List(rows));

        using (var stream = new FileStream(options.ReviewOut!, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
        }

        return new ReviewListResult
        {
            Attachments = assessed.Count,
            Rows = rows.Count,
            InCandidateSet = rows.Count(r => r.InCandidateSet),
            Excluded = excluded,
            Sha256 = Manifest.Sha256Hex(bytes),
        };
    }

    internal static string Report(ReviewListResult r)
    {
        var text = new StringBuilder();

        text.AppendLine("# Msgstore import - review-list (name+size)");
        text.AppendLine();
        text.AppendLine("Read-only on both databases. Counts only; the rows are in the private review list.");
        text.AppendLine();
        text.AppendLine("```text");
        text.AppendLine(Text.Line("attachments in the conversation", r.Attachments));
        text.AppendLine(Text.Line("rows in the review list", r.Rows));
        text.AppendLine(Text.Line("  asset in the slot's candidate set (viewer lists it)", r.InCandidateSet));
        text.AppendLine(Text.Line("  asset outside the candidate set", r.Rows - r.InCandidateSet));
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
        text.AppendLine("```");
        text.AppendLine();
        text.AppendLine("review list SHA-256: " + r.Sha256);

        return text.ToString();
    }
}

internal sealed class NameSizePlanResult
{
    internal required int ListRows { get; init; }
    internal required Tally ByMark { get; init; }
    internal required Tally Excluded { get; init; }
    internal required int Rows { get; init; }
    internal required string ListSha256 { get; init; }
    internal required string Sha256 { get; init; }
}

/// <summary><c>plan --rule namesize-reviewed --review-file &lt;marked list&gt;</c>.</summary>
internal static class NameSizePlanner
{
    internal const string Changed = "R row changed since the list (slot, asset or record differs)";
    internal const string NoLongerEligible = "R row no longer passes the guards";

    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static NameSizePlanResult Plan(Options options)
    {
        if (Planner.ManifestOutRefusal(options.ManifestOut!) is { } refusal)
        {
            throw new RefusalException(refusal);
        }

        var (marks, listSha) = ReadReviewFile(options.ReviewFile!, options.ReviewSha256);
        var parameters = options.Parameters!;
        var assessed = Inputs.ComputeNameSize(options.MsgstorePath!, options.WorkspacePath, parameters);

        var byMark = new Tally();
        var excluded = new Tally();
        var rows = new List<ManifestRow>();

        foreach (var mark in marks.OrderBy(m => m.AttachmentID))
        {
            byMark.Add(NameSize.MarkLabel(mark.Mark));

            if (mark.Mark != ReviewMark.Right)
            {
                excluded.Add(NameSize.MarkLabel(mark.Mark));
                continue;
            }

            switch (Verify(mark, assessed))
            {
                case { } reason:
                    excluded.Add(reason);
                    break;

                default:
                    var s = assessed[mark.AttachmentID].Slot;
                    rows.Add(new ManifestRow(
                        s.AttachmentID, s.Asset!.Value, s.MessageID!.Value, s.KeyID!, MatchKind.NameSizeOnly, s.PairedBy,
                        ReviewList: listSha[..12]));
                    break;
            }
        }

        var bytes = new Manifest { Parameters = parameters, Rows = rows, Rule = PairingRule.NameSizeReviewed }.ToBytes();

        using (var stream = new FileStream(options.ManifestOut!, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
        }

        return new NameSizePlanResult
        {
            ListRows = marks.Count,
            ByMark = byMark,
            Excluded = excluded,
            Rows = rows.Count,
            ListSha256 = listSha,
            Sha256 = Manifest.Sha256Hex(bytes),
        };
    }

    /// <summary>Why an R row cannot be imported against fresh data, or null.</summary>
    internal static string? Verify(ReviewRow mark, SortedDictionary<long, NameSizeAssessment> assessed)
    {
        if (!assessed.TryGetValue(mark.AttachmentID, out var now))
        {
            return Changed;
        }

        if (now.Slot.Asset != mark.MediaAssetID || now.Slot.KeyID != mark.KeyID)
        {
            return Changed;
        }

        if (!now.Eligible)
        {
            return NoLongerEligible;
        }

        return now.Slot.Resolved ? Reasons.AlreadyResolved : null;
    }

    /// <exception cref="RefusalException">Missing, malformed or not the list with the given digest.</exception>
    internal static (List<ReviewRow> Rows, string ListSha) ReadReviewFile(string path, string? expectedSha)
    {
        if (!File.Exists(path))
        {
            throw new RefusalException("There is no file at the --review-file path.");
        }

        List<ReviewRow> rows;
        string sha;

        try
        {
            (rows, sha) = NameSize.Parse(File.ReadAllBytes(path));
        }
        catch (FormatException exception)
        {
            throw new RefusalException("The review file is malformed: " + exception.Message);
        }

        if (expectedSha is not null && !string.Equals(sha, expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new RefusalException(
                "With its marks cleared, the review file does not match --review-sha256: something other than " +
                "the R / W / ? column was changed, or this is a different list.");
        }

        return (rows, sha);
    }

    internal static string Report(NameSizePlanResult r)
    {
        var text = new StringBuilder();

        text.AppendLine("# Msgstore import - plan (rule: name+size, reviewed)");
        text.AppendLine();
        text.AppendLine("Read-only on both databases. Counts only; identifiers are in the private manifest. Only rows");
        text.AppendLine("Angie marked R are taken, and each is re-checked against fresh data.");
        text.AppendLine();
        text.AppendLine("```text");
        text.AppendLine(Text.Line("rows in the review list", r.ListRows));

        foreach (var (mark, count) in r.ByMark.Items)
        {
            text.AppendLine(Text.Line("  " + mark, count));
        }

        text.AppendLine();
        text.AppendLine(Text.Line("new manifest rows", r.Rows));
        text.AppendLine(Text.Line("excluded", r.Excluded.Total));

        foreach (var (reason, count) in r.Excluded.Items)
        {
            text.AppendLine(Text.Line("  " + reason, count));
        }

        text.AppendLine();
        text.AppendLine(Text.Line("rows + excluded = review list rows", r.Rows + r.Excluded.Total));
        text.AppendLine(r.Rows + r.Excluded.Total == r.ListRows
            ? "reconciliation                                            OK"
            : "reconciliation                                            MISMATCH");
        text.AppendLine("```");
        text.AppendLine();
        text.AppendLine("review list SHA-256 (marks cleared): " + r.ListSha256);
        text.AppendLine("manifest SHA-256: " + r.Sha256);
        text.AppendLine("batch tag:        " + Manifest.Tag(r.Sha256));

        return text.ToString();
    }
}
