using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport;

/// <summary>Why a slot is, or is not, an outside-candidate-set row. Order is test and print order.</summary>
internal enum OutsideEligibility
{
    NotPaired,
    DayNotUsable,
    MinuteGap,
    NoHashAsset,
    NameSizeOnly,
    Conflict,
    EmptyAsset,
    NoCompletedRun,
    InCandidateSet,
    Disproven,
    NoKeyID,
    NoFileDate,
    FileDatedAfterMessage,
    Eligible,
}

internal sealed record OutsideAssessment(
    long AttachmentID,
    OutsideEligibility Eligibility,
    DayVerdict? Day,
    bool Resolved,
    bool Outgoing,
    DateTime? SlotMinute,
    PairingRule? PairedBy,
    long? MessageID,
    string? KeyID,
    long? Asset,
    MatchCategory? Category,
    DateOnly? FileDate,
    int? GapDays,
    string? FileName,
    long? FileSize,
    int Position,
    int PositionCount)
{
    internal MatchKind? Kind => Category switch
    {
        MatchCategory.BothSameAsset => MatchKind.HashAndNameSize,
        MatchCategory.HashOnly => MatchKind.HashOnly,
        _ => null,
    };
}

/// <summary>
/// The outside-candidate-set rule (CH-2 leftovers): a slot paired by the minute rule or by the
/// day-sequence rule, on a day that passes every day-sequence check, whose hash-matched asset fails
/// only the date candidate-set guard.
/// </summary>
/// <remarks>
/// <para><strong>The file date is the naming-derived <c>MediaFile.FileDate</c></strong>
/// (<c>MediaClassification.DeriveFileDate</c>: the <c>-YYYYMMDD-WA</c> pattern in the file name, never
/// a filesystem or EXIF time), which is also what the Phase 7 exact-date candidate set is built on.
/// An asset's date is the earliest FileDate among its copies. A file dated more than one day after
/// the message is excluded; a file with no dated copy is excluded.</para>
/// <para>On a usable day, a minute pair already agrees with its day position (the day would
/// otherwise be skipped as a minute-pairing disagreement), so both pairings name the same record.</para>
/// </remarks>
internal sealed class OutsideCandidateSet(Pairing pairing)
{
    internal const int MaxDaysAfterMessage = 1;

    internal SortedDictionary<long, OutsideAssessment> Compute()
    {
        var (attachments, records) = pairing.Load();
        var recordByMessage = records.ToDictionary(r => r.MessageID);
        var minute = pairing.Compute();
        var day = new DaySequence(pairing).Compute();
        var fileDates = new Dictionary<long, DateOnly?>();
        var result = new SortedDictionary<long, OutsideAssessment>();

        foreach (var a in attachments)
        {
            var d = day.ByAttachment[a.AttachmentID];
            var m = minute[a.AttachmentID];
            var minutePaired = m.Eligibility >= Eligibility.PairedNoAsset;
            var dayPaired = d.Day == DayVerdict.Usable && d.MessageID is not null;

            var empty = new OutsideAssessment(
                a.AttachmentID, OutsideEligibility.NotPaired, d.Day, a.Resolved, a.Outgoing, a.Minute,
                null, null, null, null, null, null, null, null, null, d.Position, d.PositionCount);

            if (!minutePaired && !dayPaired)
            {
                // Day sequence pairs only on usable days, so an unpaired slot on an unusable day is
                // reported under the day's reason; that is why it could not be paired.
                result[a.AttachmentID] = d.Day == DayVerdict.Usable
                    ? empty
                    : empty with { Eligibility = OutsideEligibility.DayNotUsable };
                continue;
            }

            var record = recordByMessage[minutePaired ? m.MessageID!.Value : d.MessageID!.Value];

            var row = empty with
            {
                PairedBy = minutePaired ? PairingRule.Minute : PairingRule.DaySequence,
                MessageID = record.MessageID,
                KeyID = record.KeyID,
                Asset = record.Asset,
                Category = record.Category,
                FileName = record.HasFilePath ? record.FileName : null,
                FileSize = record.FileSize,
            };

            if (d.Day != DayVerdict.Usable)
            {
                result[a.AttachmentID] = row with { Eligibility = OutsideEligibility.DayNotUsable };
                continue;
            }

            if (!minutePaired
                && (a.Minute is not { } slot || record.Minute is not { } at
                    || Math.Abs((at - slot).TotalMinutes) > DaySequence.MaxMinuteGap))
            {
                result[a.AttachmentID] = row with { Eligibility = OutsideEligibility.MinuteGap };
                continue;
            }

            var guard = record.Category switch
            {
                MatchCategory.BothSameAsset or MatchCategory.HashOnly => (OutsideEligibility?)null,
                MatchCategory.NameSizeOnly => OutsideEligibility.NameSizeOnly,
                MatchCategory.Conflict => OutsideEligibility.Conflict,
                _ => OutsideEligibility.NoHashAsset,
            };

            if (guard is null)
            {
                var asset = record.Asset!.Value;

                // Phase 7 never makes a zero-byte asset a candidate, so "not in the candidate set"
                // could mean "empty" rather than "wrong date". Empty assets are excluded first.
                guard = AssetSize(asset) is not > 0 ? OutsideEligibility.EmptyAsset
                    : pairing.CandidateSet(a.AttachmentID, asset) switch
                    {
                        Eligibility.NoCompletedRun => OutsideEligibility.NoCompletedRun,
                        null => OutsideEligibility.InCandidateSet,
                        _ => null,
                    };

                if (guard is null && pairing.IsDisproven(a.AttachmentID, asset))
                {
                    guard = OutsideEligibility.Disproven;
                }

                if (guard is null && record.KeyID is null)
                {
                    guard = OutsideEligibility.NoKeyID;
                }

                if (guard is null)
                {
                    if (!fileDates.TryGetValue(asset, out var fileDate))
                    {
                        fileDate = EarliestFileDate(asset);
                        fileDates[asset] = fileDate;
                    }

                    if (fileDate is not { } date)
                    {
                        guard = OutsideEligibility.NoFileDate;
                    }
                    else
                    {
                        var gap = date.DayNumber - DateOnly.FromDateTime(a.Minute!.Value).DayNumber;
                        row = row with { FileDate = date, GapDays = gap };
                        guard = gap > MaxDaysAfterMessage ? OutsideEligibility.FileDatedAfterMessage : OutsideEligibility.Eligible;
                    }
                }
            }

            result[a.AttachmentID] = row with { Eligibility = guard!.Value };
        }

        return result;
    }

    private long? AssetSize(long asset)
    {
        using var command = pairing.Workspace.CreateCommand();
        command.CommandText = "SELECT SizeBytes FROM MediaAsset WHERE MediaAssetID = $asset;";
        command.Parameters.AddWithValue("$asset", asset);

        return command.ExecuteScalar() is { } value and not DBNull ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// The date a file name carries under the same <c>-YYYYMMDD-WA</c> convention FileDate uses, or
    /// null. Display only (sample).
    /// </summary>
    internal static DateOnly? NameDate(string? fileName)
    {
        if (fileName is null)
        {
            return null;
        }

        for (var start = 1; start + 11 <= fileName.Length; start++)
        {
            if (fileName[start - 1] == '-'
                && string.CompareOrdinal(fileName, start + 8, "-WA", 0, 3) == 0
                && DateOnly.TryParseExact(fileName.AsSpan(start, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date;
            }
        }

        return null;
    }

    /// <summary>The earliest naming-derived FileDate among the asset's copies, or null.</summary>
    private DateOnly? EarliestFileDate(long asset)
    {
        using var command = pairing.Workspace.CreateCommand();
        command.CommandText =
            """
            SELECT MIN(f.FileDate)
            FROM MediaAssetFile AS af
            INNER JOIN MediaFile AS f ON f.MediaFileID = af.MediaFileID
            WHERE af.MediaAssetID = $asset AND f.FileDate IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$asset", asset);

        return command.ExecuteScalar() is string text
               && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    internal static string Reason(OutsideAssessment a) => a.Eligibility switch
    {
        OutsideEligibility.NotPaired => "not paired by the minute rule or by day sequence",
        OutsideEligibility.DayNotUsable => "day not usable: " + (a.Day is { } v ? DaySequence.DayReason(v) : "message time unreadable"),
        OutsideEligibility.MinuteGap => $"record minute more than {DaySequence.MaxMinuteGap.ToString(CultureInfo.InvariantCulture)} min from slot minute",
        OutsideEligibility.NoHashAsset => "record matched no asset by hash",
        OutsideEligibility.NameSizeOnly => "matched by name+size only",
        OutsideEligibility.Conflict => "hash and name+size named DIFFERENT assets",
        OutsideEligibility.EmptyAsset => "asset is empty (zero bytes)",
        OutsideEligibility.NoCompletedRun => "attachment has no completed Phase 7 run",
        OutsideEligibility.InCandidateSet => "asset IS in the candidate set (earlier rules)",
        OutsideEligibility.Disproven => "asset disproven by Angie for this attachment",
        OutsideEligibility.NoKeyID => "msgstore record has no usable key_id",
        OutsideEligibility.NoFileDate => "file has no naming-derived date",
        OutsideEligibility.FileDatedAfterMessage => "file dated more than 1 day AFTER the message",
        _ => throw new ArgumentOutOfRangeException(nameof(a)),
    };
}

internal sealed class OutsidePlanResult
{
    internal required int Attachments { get; init; }
    internal required int HashAndNameSize { get; init; }
    internal required int HashOnly { get; init; }
    internal required int ByMinute { get; init; }
    internal required int ByDaySequence { get; init; }
    internal required Tally Excluded { get; init; }
    internal required int SameDay { get; init; }
    internal required int PlusMinusOne { get; init; }
    internal required int AssetsOnTwoOrMoreRows { get; init; }
    internal required int Older2To30 { get; init; }
    internal required int OlderOver30 { get; init; }
    internal required int NewerOverOne { get; init; }
    internal required int NoDate { get; init; }
    internal required string Sha256 { get; init; }
    internal int SampleRows { get; init; }
    internal bool SampleWritten { get; init; }
    internal int Rows => HashAndNameSize + HashOnly;
}

/// <summary><c>plan --rule outside-candidate-set</c>.</summary>
internal static class OutsidePlanner
{
    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static OutsidePlanResult Plan(Options options)
    {
        DayPlanner.RequireOutputPaths(options);

        var parameters = options.Parameters!;
        var assessed = Inputs.ComputeOutside(options.MsgstorePath!, options.WorkspacePath, parameters);

        var rows = new List<ManifestRow>();
        var chosen = new List<OutsideAssessment>();
        var excluded = new Tally();

        foreach (var a in assessed.Values)
        {
            if (a.Eligibility != OutsideEligibility.Eligible)
            {
                excluded.Add(OutsideCandidateSet.Reason(a));
            }
            else if (a.Resolved)
            {
                excluded.Add(Reasons.AlreadyResolved);
            }
            else
            {
                rows.Add(new ManifestRow(a.AttachmentID, a.Asset!.Value, a.MessageID!.Value, a.KeyID!, a.Kind!.Value, a.PairedBy, a.FileDate));
                chosen.Add(a);
            }
        }

        var bytes = new Manifest { Parameters = parameters, Rows = rows, Rule = PairingRule.OutsideCandidateSet }.ToBytes();

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

        return new OutsidePlanResult
        {
            Attachments = assessed.Count,
            HashAndNameSize = rows.Count(r => r.Kind == MatchKind.HashAndNameSize),
            HashOnly = rows.Count(r => r.Kind == MatchKind.HashOnly),
            ByMinute = rows.Count(r => r.PairedBy == PairingRule.Minute),
            ByDaySequence = rows.Count(r => r.PairedBy == PairingRule.DaySequence),
            Excluded = excluded,
            SameDay = chosen.Count(a => a.GapDays == 0),
            PlusMinusOne = chosen.Count(a => a.GapDays is -1 or 1),
            AssetsOnTwoOrMoreRows = rows.GroupBy(r => r.MediaAssetID).Count(g => g.Count() >= 2),
            Older2To30 = chosen.Count(a => a.GapDays is <= -2 and >= -30),
            OlderOver30 = chosen.Count(a => a.GapDays < -30),
            NewerOverOne = assessed.Values.Count(a => a.Eligibility == OutsideEligibility.FileDatedAfterMessage),
            NoDate = assessed.Values.Count(a => a.Eligibility == OutsideEligibility.NoFileDate),
            Sha256 = sha,
            SampleRows = sampleRows,
            SampleWritten = options.SampleOut is not null,
        };
    }

    private static (string Text, int Count) Sample(List<OutsideAssessment> rows, Options options, string sha)
    {
        var random = new Random(options.Seed);
        var pool = rows.OrderBy(r => r.AttachmentID).ToList();
        var take = Math.Min(options.SampleSize, pool.Count);

        for (var i = 0; i < take; i++)
        {
            var j = random.Next(i, pool.Count);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        var text = new StringBuilder();

        text.AppendLine("# Msgstore import (outside the candidate set) - PRIVATE sample");
        text.AppendLine();
        text.AppendLine("**PRIVATE: real identifiers and file names. Keep outside the repository.** Check each row against");
        text.AppendLine("the phone and mark R (right), W (wrong) or ? (cannot tell).");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Seed {options.Seed}, sample size {options.SampleSize}, {take} of {rows.Count} manifest rows, batch tag {Manifest.Tag(sha)}.");
        text.AppendLine("Position = place among that day's attachments in that direction, in export order. Gap = file date minus");
        text.AppendLine("message date, in days (negative: the file is older, e.g. forwarded).");
        text.AppendLine();
        text.AppendLine("| AttachmentID | date | time (export) | direction | position in day | paired by | record file name | record name date | earliest copy date | gap (days) | file_size | matched by | R / W / ? |");
        text.AppendLine("| ---: | --- | --- | --- | --- | --- | --- | --- | --- | ---: | ---: | --- | --- |");

        foreach (var a in pool.Take(take).OrderBy(a => a.SlotMinute).ThenBy(a => a.AttachmentID))
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {a.AttachmentID} | {a.SlotMinute:yyyy-MM-dd} | {a.SlotMinute:HH:mm} | {(a.Outgoing ? "sent" : "received")} | " +
                $"{a.Position} of {a.PositionCount} | {(a.PairedBy == PairingRule.Minute ? "minute" : "day sequence")} | " +
                $"{(a.FileName ?? "-").Replace('|', '/')} | " +
                $"{(OutsideCandidateSet.NameDate(a.FileName) is { } nameDate ? nameDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "-")} | " +
                $"{a.FileDate:yyyy-MM-dd} | {a.GapDays} | " +
                $"{(a.FileSize is { } size ? size.ToString(CultureInfo.InvariantCulture) : "-")} | {Manifest.KindDescription(a.Kind!.Value)} |  |");
        }

        text.AppendLine();

        return (text.ToString(), take);
    }

    internal static string Report(OutsidePlanResult r)
    {
        var text = new StringBuilder();

        text.AppendLine("# Msgstore import - plan (rule: outside the candidate set)");
        text.AppendLine();
        text.AppendLine("Read-only on both databases. Counts only; identifiers are in the private manifest. Each row is");
        text.AppendLine("paired (minute rule or day sequence) on a day that passes every day-sequence check, matched by");
        text.AppendLine("hash, and fails only the date candidate-set guard. File dates are naming-derived (-YYYYMMDD-WA).");
        text.AppendLine();
        text.AppendLine("```text");
        text.AppendLine(Text.Line("attachments in the conversation", r.Attachments));
        text.AppendLine(Text.Line("new manifest rows", r.Rows));
        text.AppendLine(Text.Line("  matched by hash and name+size (same asset)", r.HashAndNameSize));
        text.AppendLine(Text.Line("  matched by hash only", r.HashOnly));
        text.AppendLine(Text.Line("  paired by the minute rule", r.ByMinute));
        text.AppendLine(Text.Line("  paired by day sequence", r.ByDaySequence));
        text.AppendLine();
        text.AppendLine("file date against message date:");
        text.AppendLine(Text.Line("  same day (gap 0) - not explained by date (rows)", r.SameDay));
        text.AppendLine(Text.Line("  +/-1 day (rows)", r.PlusMinusOne));
        text.AppendLine(Text.Line("  older by 2-30 days (rows)", r.Older2To30));
        text.AppendLine(Text.Line("  older by more than 30 days (rows)", r.OlderOver30));
        text.AppendLine(Text.Line("  newer by more than 1 day (EXCLUDED)", r.NewerOverOne));
        text.AppendLine(Text.Line("  no file date (EXCLUDED)", r.NoDate));
        text.AppendLine();
        text.AppendLine(Text.Line("excluded", r.Excluded.Total));

        foreach (var (reason, count) in r.Excluded.Items)
        {
            text.AppendLine(Text.Line("  " + reason, count));
        }

        text.AppendLine();
        text.AppendLine(Text.Line("rows + excluded = attachments", r.Rows + r.Excluded.Total));
        text.AppendLine(Text.Line("assets named by 2+ manifest rows (informational)", r.AssetsOnTwoOrMoreRows));
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
