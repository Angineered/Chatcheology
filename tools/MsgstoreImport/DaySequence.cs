using System.Globalization;
using System.Text.RegularExpressions;
using Chatcheology.Data.Phase7;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport;

/// <summary>Why a whole day can or cannot be lined up by message sequence.</summary>
/// <remarks>Declaration order is the order the checks run in and are printed in.</remarks>
internal enum DayVerdict
{
    /// <summary>The day has messages on one side only.</summary>
    OneSideOnly,

    /// <summary>Rule 1: the two sides have different message counts.</summary>
    CountMismatch,

    /// <summary>Rule 2: some position has a different direction on the two sides.</summary>
    DirectionMismatch,

    /// <summary>Rule 4: an import confirmation on the day lines up with a different record.</summary>
    PriorDisagreement,

    /// <summary>
    /// Rule 4, extended: a slot the minute rule paired (confirmed or not) faces a different msgstore
    /// message at its day position.
    /// </summary>
    MinutePairingDisagreement,

    /// <summary>Rule 4: an import confirmation on the day names no readable key_id.</summary>
    PriorUnreadable,

    /// <summary>A workspace message carries more than one attachment (the export never does this).</summary>
    MultiAttachmentMessage,

    /// <summary>Rule 3: an attachment slot lines up with a msgstore message that has no media record.</summary>
    SlotWithoutMedia,

    /// <summary>Every rule holds.</summary>
    Usable,
}

/// <summary>Why a slot is, or is not, a new day-sequence row. Order is test and print order.</summary>
internal enum DayEligibility
{
    NoTime,
    DaySkipped,
    PairedByMinuteRule,
    MinuteGap,
    NoHashAsset,
    NameSizeOnly,
    Conflict,
    NoCompletedRun,
    NotInCandidateSet,
    Disproven,
    NoKeyID,
    Eligible,
}

/// <summary>What the day-sequence alignment says about one attachment slot.</summary>
internal sealed record DayAssessment(
    long AttachmentID,
    DayEligibility Eligibility,
    DayVerdict? Day,
    bool Resolved,
    bool Outgoing,
    DateTime? SlotMinute,
    long? MessageID,
    string? KeyID,
    long? Asset,
    MatchCategory? Category,
    DateTime? RecordMinute,
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

internal sealed class DaySequenceResult
{
    internal required SortedDictionary<long, DayAssessment> ByAttachment { get; init; }

    /// <summary>Days with at least one workspace message, by verdict.</summary>
    internal required Dictionary<DayVerdict, int> Days { get; init; }

    internal required int DaysOnlyInMsgstore { get; init; }

    /// <summary>Import confirmations compared on days whose messages line up (rules 1 and 2 hold).</summary>
    internal required int PriorAgree { get; init; }

    internal required int PriorDisagree { get; init; }

    internal required int PriorUnreadable { get; init; }

    /// <summary>Import confirmations on days that could not be lined up, so were not compared.</summary>
    internal required int PriorNotCompared { get; init; }

    /// <summary>Minute-rule pairs (confirmed or not) facing the same record at their day position.</summary>
    internal required int MinuteAgree { get; init; }

    internal required int MinuteDisagree { get; init; }

    /// <summary>On lined-up days: msgstore media rows facing a workspace message with no slot. Not a skip.</summary>
    internal required int MediaFacingText { get; init; }
}

/// <summary>
/// The CH-9 rule: line up every message of a local day, workspace SequenceNumber order against
/// msgstore sort_id-then-_id order, and pair attachment slots by position.
/// </summary>
/// <remarks>
/// <para><strong>The message population is the census's:</strong> every Message row of the
/// conversation and every message row of the chat, system messages included, each placed on its
/// local day (workspace: the stored local time; msgstore: the epoch timestamp shifted by the fixed
/// offset). A row without a readable time has no day and is left out, as in the census.</para>
/// <para><strong>Direction</strong> is the census's: sent when the workspace sender is the local
/// participant, or when <c>from_me</c> is set; everything else (system messages included) is
/// received.</para>
/// <para><strong>One attachment per message.</strong> The importer writes at most one attachment per
/// export line (ordinal 1), and a msgstore message has at most one <c>message_media</c> row, so a
/// slot maps to exactly the msgstore message at its position. A workspace message with two or more
/// attachments skips the day.</para>
/// <para><strong>Prior confirmations</strong> are read from the workspace itself: every attachment
/// whose current effective event is a Confirm by <c>Angie (msgstore import)</c> naming an import
/// batch. The record it names (by key_id) must be the record at that slot's position.</para>
/// <para>Reads no message text: only ids, order, time, direction and whether a media row exists.</para>
/// </remarks>
internal sealed partial class DaySequence(Pairing pairing)
{
    internal const int MaxMinuteGap = 2;

    /// <summary>Every import text but the reviewed one starts with the record phrase.</summary>
    [GeneratedRegex(@"^WhatsApp msgstore record \(message key_id (\S+?)\), ")]
    private static partial Regex ImportShape();

    /// <summary>The reviewed name+size text: Angie's fixed statement, then the record phrase.</summary>
    [GeneratedRegex(@"^Angie inspected the file and confirmed it \(review list [0-9a-f]{12}\); WhatsApp msgstore record \(message key_id (\S+?)\), ")]
    private static partial Regex ReviewedShape();

    /// <summary>
    /// The key_id a prior import confirmation names: the one right after the fixed prefix of a known
    /// text shape, never a later or earlier occurrence; null for any other shape.
    /// </summary>
    internal static string? ParsePriorKey(string description) =>
        ImportShape().Match(description) is { Success: true } import ? import.Groups[1].Value
        : ReviewedShape().Match(description) is { Success: true } reviewed ? reviewed.Groups[1].Value
        : null;

    /// <summary>The actors whose import-batch confirmations count as prior confirmations.</summary>
    internal static readonly string[] ImportActors = [Applier.ActorLabel, NameSize.ReviewedActorLabel];

    private sealed record WsMessage(long MessageID, DateOnly Day, bool Outgoing);

    private sealed record MsMessage(long ID, DateOnly Day, bool Outgoing, long OrderKey);

    internal DaySequenceResult Compute()
    {
        var p = pairing.Parameters;
        var (attachments, records) = pairing.Load();
        var minute = pairing.Compute();

        var recordByMessage = records.ToDictionary(r => r.MessageID);
        var attachmentsByMessage = attachments.GroupBy(a => a.MessageID).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Ordinal).ToList());
        var prior = ReadPriorKeys(attachments);

        var wsDays = ReadWorkspaceMessages(p).GroupBy(m => m.Day).ToDictionary(g => g.Key, g => g.ToList());
        var msDays = ReadMsgstoreMessages(p)
            .GroupBy(m => m.Day)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.OrderKey).ThenBy(m => m.ID).ToList());

        var result = new SortedDictionary<long, DayAssessment>();
        var days = Enum.GetValues<DayVerdict>().ToDictionary(v => v, _ => 0);
        int agree = 0, disagree = 0, unreadable = 0, notCompared = 0, minuteAgree = 0, minuteDisagree = 0, mediaFacingText = 0;

        foreach (var a in attachments.Where(a => a.Minute is null))
        {
            result[a.AttachmentID] = Slot(a, DayEligibility.NoTime, null);
        }

        foreach (var (day, ws) in wsDays)
        {
            var ms = msDays.GetValueOrDefault(day);
            var slots = ws.SelectMany(m => attachmentsByMessage.GetValueOrDefault(m.MessageID) ?? []).ToList();

            DayVerdict verdict;
            var paired = new Dictionary<long, MediaRecord?>();

            if (ms is null)
            {
                verdict = DayVerdict.OneSideOnly;
            }
            else if (ms.Count != ws.Count)
            {
                verdict = DayVerdict.CountMismatch;
            }
            else if (Enumerable.Range(0, ws.Count).Any(k => ws[k].Outgoing != ms[k].Outgoing))
            {
                verdict = DayVerdict.DirectionMismatch;
            }
            else
            {
                bool multi = false, noMedia = false, dayDisagree = false, dayUnreadable = false, dayMinuteDisagree = false;

                for (var k = 0; k < ws.Count; k++)
                {
                    var here = attachmentsByMessage.GetValueOrDefault(ws[k].MessageID) ?? [];

                    if (here.Count == 0)
                    {
                        if (recordByMessage.ContainsKey(ms[k].ID))
                        {
                            mediaFacingText++;
                        }

                        continue;
                    }

                    // Every slot the minute rule paired must face that same msgstore message here.
                    foreach (var a in here)
                    {
                        if (minute.TryGetValue(a.AttachmentID, out var byMinute)
                            && byMinute.Eligibility >= Eligibility.PairedNoAsset)
                        {
                            if (here.Count == 1 && byMinute.MessageID == ms[k].ID)
                            {
                                minuteAgree++;
                            }
                            else
                            {
                                minuteDisagree++;
                                dayMinuteDisagree = true;
                            }
                        }
                    }

                    multi |= here.Count > 1;

                    var record = recordByMessage.GetValueOrDefault(ms[k].ID);
                    noMedia |= record is null;

                    foreach (var a in here)
                    {
                        paired[a.AttachmentID] = here.Count == 1 ? record : null;

                        if (!prior.TryGetValue(a.AttachmentID, out var priorKey))
                        {
                            continue;
                        }

                        if (priorKey is null)
                        {
                            unreadable++;
                            dayUnreadable = true;
                        }
                        else if (here.Count == 1 && record is not null && (record.RawKeyID ?? "") == priorKey)
                        {
                            agree++;
                        }
                        else
                        {
                            disagree++;
                            dayDisagree = true;
                        }
                    }
                }

                verdict = dayDisagree ? DayVerdict.PriorDisagreement
                    : dayMinuteDisagree ? DayVerdict.MinutePairingDisagreement
                    : dayUnreadable ? DayVerdict.PriorUnreadable
                    : multi ? DayVerdict.MultiAttachmentMessage
                    : noMedia ? DayVerdict.SlotWithoutMedia
                    : DayVerdict.Usable;
            }

            days[verdict]++;

            if (verdict <= DayVerdict.DirectionMismatch)
            {
                notCompared += slots.Count(a => prior.ContainsKey(a.AttachmentID));
            }

            // Position among the day's attachment slots in the same direction, in sequence order.
            var positions = slots
                .GroupBy(a => a.Outgoing)
                .SelectMany(g => g.OrderBy(a => a.SequenceNumber).ThenBy(a => a.Ordinal).ThenBy(a => a.AttachmentID)
                    .Select((a, i) => (a.AttachmentID, Position: i + 1, Count: g.Count())))
                .ToDictionary(x => x.AttachmentID, x => (x.Position, x.Count));

            foreach (var a in slots)
            {
                var (position, count) = positions[a.AttachmentID];

                if (verdict != DayVerdict.Usable)
                {
                    result[a.AttachmentID] = Slot(a, DayEligibility.DaySkipped, verdict) with { Position = position, PositionCount = count };
                    continue;
                }

                var record = paired[a.AttachmentID]!;

                result[a.AttachmentID] = new DayAssessment(
                    a.AttachmentID, Guard(a, record, minute), verdict, a.Resolved, a.Outgoing, a.Minute,
                    record.MessageID, record.KeyID, record.Asset, record.Category, record.Minute,
                    record.HasFilePath ? record.FileName : null, record.FileSize, position, count);
            }
        }

        // Slots whose message has a readable time but which the day grouping above did not reach
        // cannot exist (every attachment's message is a workspace message); guard anyway.
        foreach (var a in attachments.Where(a => !result.ContainsKey(a.AttachmentID)))
        {
            result[a.AttachmentID] = Slot(a, DayEligibility.NoTime, null);
        }

        return new DaySequenceResult
        {
            ByAttachment = result,
            Days = days,
            DaysOnlyInMsgstore = msDays.Keys.Count(d => !wsDays.ContainsKey(d)),
            PriorAgree = agree,
            PriorDisagree = disagree,
            PriorUnreadable = unreadable,
            PriorNotCompared = notCompared,
            MinuteAgree = minuteAgree,
            MinuteDisagree = minuteDisagree,
            MediaFacingText = mediaFacingText,
        };
    }

    private static DayAssessment Slot(WsAttachment a, DayEligibility eligibility, DayVerdict? day) =>
        new(a.AttachmentID, eligibility, day, a.Resolved, a.Outgoing, a.Minute, null, null, null, null, null, null, null, 0, 0);

    /// <summary>The new-slot guards, in order. Resolution is checked by the caller.</summary>
    private DayEligibility Guard(WsAttachment a, MediaRecord record, SortedDictionary<long, Assessment> minute)
    {
        if (minute.TryGetValue(a.AttachmentID, out var byMinute) && byMinute.Eligibility >= Eligibility.PairedNoAsset)
        {
            return DayEligibility.PairedByMinuteRule;
        }

        if (a.Minute is not { } slot || record.Minute is not { } at
            || Math.Abs((at - slot).TotalMinutes) > MaxMinuteGap)
        {
            return DayEligibility.MinuteGap;
        }

        switch (record.Category)
        {
            case MatchCategory.BothSameAsset or MatchCategory.HashOnly:
                break;
            case MatchCategory.NameSizeOnly:
                return DayEligibility.NameSizeOnly;
            case MatchCategory.Conflict:
                return DayEligibility.Conflict;
            default:
                return DayEligibility.NoHashAsset;
        }

        var asset = record.Asset!.Value;

        switch (pairing.CandidateSet(a.AttachmentID, asset))
        {
            case Eligibility.NoCompletedRun:
                return DayEligibility.NoCompletedRun;
            case Eligibility.NotInCandidateSet:
                return DayEligibility.NotInCandidateSet;
        }

        if (pairing.IsDisproven(a.AttachmentID, asset))
        {
            return DayEligibility.Disproven;
        }

        return record.KeyID is null ? DayEligibility.NoKeyID : DayEligibility.Eligible;
    }

    /// <summary>
    /// The key_id each import-confirmed attachment names, by AttachmentID (null when unreadable).
    /// Only attachments whose current effective event is a msgstore-import Confirm appear.
    /// </summary>
    private Dictionary<long, string?> ReadPriorKeys(List<WsAttachment> attachments)
    {
        var history = new Phase7ResolutionHistoryReader();
        var result = new Dictionary<long, string?>();

        foreach (var a in attachments.Where(a => a.Resolved))
        {
            var projection = history.ReplayProjection(pairing.Workspace, a.AttachmentID);

            if (projection.CurrentEffectiveEventID is not { } eventID)
            {
                continue;
            }

            var current = history.ReadHistory(pairing.Workspace, a.AttachmentID)
                .Single(e => e.AttachmentResolutionEventID == eventID);

            if (current.EventType != Phase7ResolutionEventType.Confirm
                || !ImportActors.Contains(current.ActorLabel)
                || current.IndependentBasisDescription is not { } description
                || !description.Contains("import batch ", StringComparison.Ordinal))
            {
                continue;
            }

            result[a.AttachmentID] = ParsePriorKey(description);
        }

        return result;
    }

    private List<WsMessage> ReadWorkspaceMessages(PairingParameters p)
    {
        var result = new List<WsMessage>();

        using var command = pairing.Workspace.CreateCommand();
        command.CommandText =
            """
            SELECT MessageID, MessageDateTimeLocal, SenderParticipantID
            FROM Message
            WHERE ConversationID = $id
            ORDER BY SequenceNumber, MessageID;
            """;
        command.Parameters.AddWithValue("$id", p.ConversationID);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (Pairing.ParseMinute(reader.GetString(1)) is { } local)
            {
                result.Add(new WsMessage(
                    reader.GetInt64(0), DateOnly.FromDateTime(local),
                    !reader.IsDBNull(2) && reader.GetInt64(2) == p.LocalParticipantID));
            }
        }

        return result;
    }

    private List<MsMessage> ReadMsgstoreMessages(PairingParameters p)
    {
        var result = new List<MsMessage>();
        var sortID = pairing.HasSortID ? "sort_id" : "NULL";

        using var command = pairing.MsgstoreConnection.CreateCommand();
        command.CommandText = $"SELECT _id, from_me, timestamp, {sortID} FROM message WHERE chat_row_id = $chat;";
        command.Parameters.AddWithValue("$chat", p.ChatID);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var id = reader.GetInt64(0);

            if (pairing.Local(reader.IsDBNull(2) ? null : reader.GetInt64(2)) is { } local)
            {
                result.Add(new MsMessage(
                    id, DateOnly.FromDateTime(local),
                    !reader.IsDBNull(1) && reader.GetInt64(1) != 0,
                    reader.IsDBNull(3) ? id : reader.GetInt64(3)));
            }
        }

        return result;
    }

    internal static string DayReason(DayVerdict verdict) => verdict switch
    {
        DayVerdict.OneSideOnly => "day has workspace messages but none in the msgstore chat",
        DayVerdict.CountMismatch => "message counts differ",
        DayVerdict.DirectionMismatch => "direction differs at some position",
        DayVerdict.PriorDisagreement => "DISAGREES with a prior import confirmation",
        DayVerdict.MinutePairingDisagreement => "minute pairing DISAGREES with day position",
        DayVerdict.PriorUnreadable => "prior import confirmation names no readable key_id",
        DayVerdict.MultiAttachmentMessage => "a workspace message has 2+ attachments",
        DayVerdict.SlotWithoutMedia => "a slot lines up with a msgstore message without media",
        DayVerdict.Usable => "usable",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict)),
    };

    internal static string SlotReason(DayEligibility e, DayVerdict? day) => e switch
    {
        DayEligibility.NoTime => "slot message time unreadable",
        DayEligibility.DaySkipped => "day skipped: " + DayReason(day!.Value),
        DayEligibility.PairedByMinuteRule => "already paired by the minute rule",
        DayEligibility.MinuteGap => $"record minute more than {MaxMinuteGap.ToString(CultureInfo.InvariantCulture)} min from slot minute",
        DayEligibility.NoHashAsset => "record matched no asset by hash",
        DayEligibility.NameSizeOnly => "matched by name+size only",
        DayEligibility.Conflict => "hash and name+size named DIFFERENT assets",
        DayEligibility.NoCompletedRun => "attachment has no completed Phase 7 run",
        DayEligibility.NotInCandidateSet => "asset NOT in current candidate set",
        DayEligibility.Disproven => "asset disproven by Angie for this attachment",
        DayEligibility.NoKeyID => "msgstore record has no usable key_id",
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };
}
