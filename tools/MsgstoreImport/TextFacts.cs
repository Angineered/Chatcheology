using System.Globalization;
using System.Text;
using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport;

/// <summary>One text message's msgstore facts, as the text-facts manifest records them.</summary>
/// <param name="MessageID">The workspace text message.</param>
/// <param name="MsgstoreMessageID">The msgstore message it pairs to (recorded so apply can recheck the pair).</param>
internal sealed record TextFactRow(long MessageID, long MsgstoreMessageID, bool IsForwarded, int? ForwardScore)
{
    internal MessageMsgstoreFactRow ToWriterRow() => new(MessageID, IsForwarded, ForwardScore);

    internal bool SameFacts(MessageMsgstoreFact fact) => fact.IsForwarded == IsForwarded && fact.ForwardScore == ForwardScore;
}

/// <summary>
/// The CH-22 text-facts manifest: the shared fact-manifest header with the rule <c>text-facts</c> and
/// four columns; <c>-</c> stands for no score. Rows in MessageID order; the SHA-256 is over the bytes.
/// </summary>
internal sealed class TextFactsManifest
{
    internal const string Token = "text-facts";
    internal const string Header = "MessageID\tMsgstoreMessageID\tIsForwarded\tForwardScore";

    internal required PairingParameters Parameters { get; init; }
    internal required IReadOnlyList<TextFactRow> Rows { get; init; }

    internal static bool IsOne(string? path) => FactsManifestFormat.Names(path, Token);

    internal byte[] ToBytes() =>
        FactsManifestFormat.ToBytes(Token, Header, Parameters, Rows.Select(row =>
            $"{Text.Id(row.MessageID)}\t{Text.Id(row.MsgstoreMessageID)}\t{(row.IsForwarded ? "1" : "0")}\t" +
            (row.ForwardScore is { } s ? s.ToString(CultureInfo.InvariantCulture) : "-")));

    /// <exception cref="FormatException">The bytes are not a well-formed text-facts manifest.</exception>
    internal static TextFactsManifest Parse(byte[] bytes)
    {
        var (parameters, body) = FactsManifestFormat.Parse(bytes, Token, Header);

        static bool Id(string text, out long value) =>
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

        var rows = new List<TextFactRow>(body.Length);
        var messages = new HashSet<long>();
        var msgstoreMessages = new HashSet<long>();

        foreach (var line in body)
        {
            var parts = line.Split('\t');

            if (parts.Length != 4 || !Id(parts[0], out var message) || !Id(parts[1], out var msgstore)
                || parts[2] is not ("0" or "1"))
            {
                throw new FormatException("A manifest row is malformed.");
            }

            int? score = null;

            if (parts[3] != "-")
            {
                score = parts[2] == "1" && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var s)
                    ? s
                    : throw new FormatException("A manifest row has a malformed ForwardScore.");
            }

            if (!messages.Add(message) || !msgstoreMessages.Add(msgstore))
            {
                throw new FormatException("The manifest names a message twice.");
            }

            rows.Add(new TextFactRow(message, msgstore, parts[2] == "1", score));
        }

        return new TextFactsManifest { Parameters = parameters, Rows = rows };
    }
}

/// <summary>What the text-facts computation found. Counts, and the rows for the private manifest.</summary>
internal sealed class TextFactsComputation
{
    internal required string TextColumn { get; init; }
    internal required string ForwardSource { get; init; }
    internal required int WorkspaceMessages { get; init; }
    internal required int SystemMessages { get; init; }
    internal required int AttachmentMessages { get; init; }
    internal required int Captions { get; init; }
    internal required int EmptyMessages { get; init; }
    internal required int TextMessages { get; init; }
    internal required int NoTime { get; init; }
    internal required int Paired { get; init; }
    internal required int Ambiguous { get; init; }
    internal required int Unmatched { get; init; }
    internal required int EditedMarker { get; init; }
    internal required int GainedByStrip { get; init; }
    internal required int LostByStrip { get; init; }
    internal required int LeftAlone { get; init; }
    internal required int ScoresUnreadable { get; init; }
    internal required int NfcFailures { get; init; }
    internal required List<TextFactRow> Rows { get; init; }
}

/// <summary>
/// <c>plan --rule text-facts</c> (CH-22): for every workspace text message that pairs uniquely to a
/// msgstore message, WhatsApp's forwarded status and forward score, copied from the msgstore.
/// </summary>
/// <remarks>
/// <para>The approved rule (design note 2026-10-19): same local minute, same direction, identical text
/// after <see cref="TextProber.Normalise"/>, with a trailing export edited marker (and the whitespace
/// before it) removed from the workspace text first; the only candidate both ways. Ambiguous and
/// unmatched messages get no row. Captions are left out: their message has an attachment, whose fact
/// already says whether it was forwarded.</para>
/// <para><c>IsForwarded</c> is the presence of a <c>message_forwarded</c> row, or, only when that
/// table is absent, a non-zero legacy <c>message.forwarded</c>. <c>ForwardScore</c> is that row's
/// <c>forward_score</c>; a value that is not a non-negative integer is recorded as none and counted.</para>
/// </remarks>
internal static class TextFactsPlanner
{
    internal const int ForwardedManyTimes = FactsPlanner.ForwardedManyTimes;
    internal const string LeftAloneReason = "already have a msgstore fact from another batch (left alone)";

    /// <summary>
    /// The facts for every uniquely paired text message, read-only on both databases. Facts already
    /// stored under <paramref name="ownBatch"/> are treated as absent, so apply can recompute the plan
    /// it is applying.
    /// </summary>
    internal static TextFactsComputation Compute(string msgstorePath, string workspacePath, PairingParameters parameters, string? ownBatch)
    {
        WorkspaceMigrator.RequireCurrent(workspacePath);

        return Inputs.WithPairing(msgstorePath, workspacePath, parameters, pairing => Compute(pairing, ownBatch));
    }

    private static TextFactsComputation Compute(Pairing pairing, string? ownBatch)
    {
        var msgstore = pairing.MsgstoreConnection;

        var forwardTable = TextPairing.HasColumns(msgstore, "message_forwarded", "message_row_id");
        var scoreColumn = forwardTable && TextPairing.HasColumns(msgstore, "message_forwarded", "forward_score");
        var legacy = !forwardTable && TextPairing.HasColumns(msgstore, "message", "forwarded");

        if (!forwardTable && !legacy)
        {
            throw new RefusalException(
                "The msgstore has neither a message_forwarded table (with message_row_id) nor a message.forwarded " +
                "column, so forwarded status cannot be read. Nothing was written.");
        }

        var textColumn = TextPairing.TextColumn(msgstore);

        TextProber.NfcFailures = 0;
        var (kinds, ws, noTime) = TextPairing.ReadWorkspace(pairing.Workspace, pairing.Parameters);
        var ms = TextPairing.ReadMsgstore(msgstore, pairing, textColumn);

        // The rule, and the same pass without the edited-marker strip (to count what the strip changes).
        var rule = new TextPairing.Pass(ws, ms, w => w.Stripped, m => m.Norm!, 0);
        var plain = new TextPairing.Pass(ws, ms, w => w.Norm, m => m.Norm!, 0);

        var timed = ws.Where(w => w.Minute is not null).ToList();
        var paired = timed.Where(rule.Unique).ToList();
        var forward = ForwardedFacts.Read(msgstore, pairing.Parameters.ChatID, forwardTable, scoreColumn, legacy);

        // Facts already in the workspace (any batch but this one's own), through the public reader.
        var stored = new MessageMsgstoreFactReader().Read(pairing.Workspace, paired.Select(w => w.MessageID).ToList());
        var leftAlone = paired
            .Where(w => stored.TryGetValue(w.MessageID, out var f) && !string.Equals(f.SourceBatch, ownBatch, StringComparison.Ordinal))
            .Select(w => w.MessageID)
            .ToHashSet();

        var rows = paired
            .Where(w => !leftAlone.Contains(w.MessageID))
            .Select(w => (Ws: w, Ms: rule.WsCandidates[w.Index][0].ID))
            .OrderBy(p => p.Ws.MessageID)
            .Select(p => new TextFactRow(p.Ws.MessageID, p.Ms, forward.Forwarded.Contains(p.Ms), forward.Score(p.Ms)))
            .ToList();

        return new TextFactsComputation
        {
            TextColumn = textColumn,
            ForwardSource = forwardTable
                ? scoreColumn ? "message_forwarded (with forward_score)" : "message_forwarded (no forward_score column)"
                : "legacy message.forwarded (no score)",
            WorkspaceMessages = kinds.Values.Sum(),
            SystemMessages = kinds.GetValueOrDefault(TextProber.WsKind.System),
            AttachmentMessages = kinds.GetValueOrDefault(TextProber.WsKind.Attachment),
            Captions = kinds.GetValueOrDefault(TextProber.WsKind.MediaWithCaption),
            EmptyMessages = kinds.GetValueOrDefault(TextProber.WsKind.Empty),
            TextMessages = ws.Count,
            NoTime = noTime,
            Paired = paired.Count,
            Ambiguous = timed.Count(w => rule.WsCandidates[w.Index].Count > 0 && !rule.Unique(w)),
            Unmatched = timed.Count(w => rule.WsCandidates[w.Index].Count == 0),
            EditedMarker = ws.Count(w => w.EditedMarker),
            GainedByStrip = timed.Count(w => rule.Unique(w) && !plain.Unique(w)),
            LostByStrip = timed.Count(w => !rule.Unique(w) && plain.Unique(w)),
            LeftAlone = leftAlone.Count,
            ScoresUnreadable = rows.Count(r => forward.Unreadable.Contains(r.MsgstoreMessageID)),
            NfcFailures = TextProber.NfcFailures,
            Rows = rows,
        };
    }

    internal sealed class TextFactsPlanResult
    {
        internal required TextFactsComputation Facts { get; init; }
        internal required string Sha256 { get; init; }
    }

    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static TextFactsPlanResult Plan(Options options)
    {
        if (Planner.ManifestOutRefusal(options.ManifestOut!) is { } refusal)
        {
            throw new RefusalException(refusal);
        }

        var parameters = options.Parameters!;
        var facts = Compute(options.MsgstorePath!, options.WorkspacePath, parameters, ownBatch: null);
        var bytes = new TextFactsManifest { Parameters = parameters, Rows = facts.Rows }.ToBytes();

        using (var stream = new FileStream(options.ManifestOut!, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
        }

        return new TextFactsPlanResult { Facts = facts, Sha256 = Manifest.Sha256Hex(bytes) };
    }

    internal static bool Reconciled(TextFactsComputation f) =>
        f.Paired == f.Rows.Count + f.LeftAlone
        && f.Rows.Count + f.LeftAlone + f.Ambiguous + f.Unmatched + f.NoTime == f.TextMessages
        && f.SystemMessages + f.AttachmentMessages + f.Captions + f.EmptyMessages + f.TextMessages == f.WorkspaceMessages;

    internal static string Report(TextFactsPlanResult result)
    {
        var f = result.Facts;
        var text = new StringBuilder();

        text.AppendLine("# Msgstore import - plan text-facts (CH-22)");
        text.AppendLine();
        text.AppendLine("Read-only on both databases. Text is compared in memory only. Counts only; identifiers are in");
        text.AppendLine("the private manifest.");
        text.AppendLine();
        text.AppendLine("Rule: same local minute, same direction, identical text after normalisation (U+200E/U+200F");
        text.AppendLine("removed, LF, NFC, trim; a trailing edited marker removed from the workspace text first), and");
        text.AppendLine("the only candidate both ways.");
        text.AppendLine();
        text.AppendLine("```text");
        text.AppendLine($"{"msgstore text column",-58}message.{f.TextColumn}");
        text.AppendLine($"{"forwarded from",-58}{f.ForwardSource}");
        text.AppendLine();
        text.AppendLine(Text.Line("workspace messages in the conversation", f.WorkspaceMessages));
        text.AppendLine(Text.Line("  system messages (excluded)", f.SystemMessages));
        text.AppendLine(Text.Line("  attachment messages (excluded)", f.AttachmentMessages));
        text.AppendLine(Text.Line("  captions (excluded; the attachment carries the fact)", f.Captions));
        text.AppendLine(Text.Line("  empty after normalisation (excluded)", f.EmptyMessages));
        text.AppendLine(Text.Line("  text messages", f.TextMessages));
        text.AppendLine();
        text.AppendLine(Text.Line("text messages paired uniquely", f.Paired));
        text.AppendLine(Text.Line("  gained by the edited-marker strip", f.GainedByStrip));
        text.AppendLine(Text.Line("  lost by the edited-marker strip (unique only without it)", f.LostByStrip));
        text.AppendLine(Text.Line("text messages ambiguous (no row)", f.Ambiguous));
        text.AppendLine(Text.Line("text messages unmatched (no row)", f.Unmatched));
        text.AppendLine(Text.Line("text messages with no readable time (no row)", f.NoTime));
        text.AppendLine(Text.Line("text messages ending in the export's edited marker", f.EditedMarker));
        text.AppendLine(Text.Line("texts the system could not normalise (compared as they are)", f.NfcFailures));
        text.AppendLine();
        text.AppendLine(Text.Line("manifest rows (one per paired text message)", f.Rows.Count));
        text.AppendLine(Text.Line("  forwarded", f.Rows.Count(r => r.IsForwarded)));
        text.AppendLine(Text.Line($"    forwarded many times (score >= {ForwardedManyTimes.ToString(CultureInfo.InvariantCulture)})", f.Rows.Count(r => r.ForwardScore >= ForwardedManyTimes)));
        text.AppendLine(Text.Line("    forward score recorded", f.Rows.Count(r => r.ForwardScore is not null)));
        text.AppendLine(Text.Line("    forward score unreadable (recorded as none)", f.ScoresUnreadable));
        text.AppendLine(Text.Line("  not forwarded", f.Rows.Count(r => !r.IsForwarded)));
        text.AppendLine(Text.Line("paired, " + LeftAloneReason, f.LeftAlone));
        text.AppendLine();
        text.AppendLine(Text.Line("rows + left alone + ambiguous + unmatched + no time", f.Rows.Count + f.LeftAlone + f.Ambiguous + f.Unmatched + f.NoTime));
        text.AppendLine($"{"reconciliation (= text messages)",-58}{(Reconciled(f) ? "OK" : "MISMATCH")}");
        text.AppendLine("```");
        text.AppendLine();
        text.AppendLine("manifest SHA-256: " + result.Sha256);
        text.AppendLine("batch tag:        " + Manifest.Tag(result.Sha256));

        return text.ToString();
    }
}

/// <summary>Apply and undo-batch for a text-facts manifest, through <see cref="MessageMsgstoreFactWriter"/> only.</summary>
/// <remarks>
/// All-or-nothing, as the writer is: apply recomputes every row from the msgstore and workspace and
/// refuses the whole batch, writing nothing, when any row differs from the manifest.
/// </remarks>
internal static class TextFactsBatch
{
    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static FactsWriteResult Apply(Options options)
    {
        var (manifest, tag) = FactsManifestFormat.Load(options.ManifestPath!, options.ManifestSha256!, TextFactsManifest.Parse);

        if (options.ReviewFile is not null)
        {
            throw new RefusalException("--review-file applies only to a manifest of rule namesize-reviewed.");
        }

        Inputs.RequireMsgstore(options.MsgstorePath!);
        WorkspaceMigrator.RequireCurrent(options.WorkspacePath);
        Inputs.RequireUnclaimed(options.WorkspacePath);

        var now = TextFactsPlanner.Compute(options.MsgstorePath!, options.WorkspacePath, manifest.Parameters, ownBatch: tag);
        var current = now.Rows.ToDictionary(r => r.MessageID);
        var planned = manifest.Rows.Select(r => r.MessageID).ToHashSet();

        var changed = manifest.Rows.Count(r => !current.TryGetValue(r.MessageID, out var c) || c != r);
        var added = current.Keys.Count(id => !planned.Contains(id));

        if (changed > 0 || added > 0)
        {
            throw new RefusalException(
                $"{Text.Count(changed)} manifest rows no longer match the msgstore and workspace, and " +
                $"{Text.Count(added)} text messages would now get a row the manifest lacks. Plan again. Nothing was written.");
        }

        IReadOnlyDictionary<long, MessageMsgstoreFact> stored;

        try
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(options.WorkspacePath);
            stored = new MessageMsgstoreFactReader().Read(connection, [.. planned]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        var already = 0;
        var toWrite = new List<MessageMsgstoreFactRow>();

        foreach (var row in manifest.Rows)
        {
            if (!stored.TryGetValue(row.MessageID, out var fact))
            {
                toWrite.Add(row.ToWriterRow());
            }
            else if (fact.SourceBatch == tag && row.SameFacts(fact))
            {
                already++;
            }
            else
            {
                throw new RefusalException(
                    "A text message in the manifest already has a different msgstore fact. Plan again. Nothing was written.");
            }
        }

        var done = 0;

        if (toWrite.Count > 0)
        {
            try
            {
                done = MessageMsgstoreFactWriter.InsertBatch(options.WorkspacePath, tag, toWrite, DateTime.UtcNow);
            }
            catch (Exception exception) when (Inputs.IsClaimFailure(exception))
            {
                throw new RefusalException(WorkspaceMigrator.ClaimedRefusal);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                // The writer's message names messages, so only the fact is printed.
                throw new RefusalException("The text fact writer refused the batch. Nothing was written.");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        return new FactsWriteResult { Mode = "apply text-facts", Tag = tag, Rows = manifest.Rows.Count, Done = done, AlreadyDone = already };
    }

    /// <summary>Removes this batch's rows, and only this batch's.</summary>
    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static FactsWriteResult Undo(Options options)
    {
        var (manifest, tag) = FactsManifestFormat.Load(options.ManifestPath!, options.ManifestSha256!, TextFactsManifest.Parse);

        WorkspaceMigrator.RequireCurrent(options.WorkspacePath);
        Inputs.RequireUnclaimed(options.WorkspacePath);

        int deleted;

        try
        {
            deleted = MessageMsgstoreFactWriter.DeleteBatch(options.WorkspacePath, tag);
        }
        catch (Exception exception) when (Inputs.IsClaimFailure(exception))
        {
            throw new RefusalException(WorkspaceMigrator.ClaimedRefusal);
        }
        catch (InvalidOperationException)
        {
            throw new RefusalException("The text fact writer refused to remove the batch. Nothing was written.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        return new FactsWriteResult
        {
            Mode = "undo-batch text-facts",
            Tag = tag,
            Rows = manifest.Rows.Count,
            Done = deleted,
            NotPresent = Math.Max(0, manifest.Rows.Count - deleted),
        };
    }
}
