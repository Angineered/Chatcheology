using System.Globalization;
using System.Text;
using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport;

/// <summary>One attachment's caption, as the caption-facts manifest records it.</summary>
/// <param name="AttachmentID">The workspace attachment.</param>
/// <param name="MessageID">The msgstore message the attachment pairs to.</param>
/// <param name="CaptionMessageID">The msgstore message the caption was read from: the same, or its type-12 partner.</param>
/// <param name="FromPartner">True when the caption was read from the type-12 partner.</param>
/// <param name="CaptionSha256">SHA-256 (lower-case hex) of the caption's UTF-8 bytes, exactly as stored.</param>
/// <remarks>
/// The manifest never holds the caption itself: it is personal content. Apply reads the text from the
/// msgstore again and writes it only when its digest still equals the approved one.
/// </remarks>
internal sealed record CaptionRow(long AttachmentID, long MessageID, long CaptionMessageID, bool FromPartner, string CaptionSha256)
{
    internal static string Digest(string caption) => Manifest.Sha256Hex(Encoding.UTF8.GetBytes(caption));
}

/// <summary>
/// The CH-71 caption-facts manifest: the shared fact-manifest header with the rule <c>caption-facts</c>
/// and five columns. Rows in AttachmentID order; the SHA-256 is over the bytes.
/// </summary>
internal sealed class CaptionFactsManifest
{
    internal const string Token = "caption-facts";
    internal const string Header = "AttachmentID\tMsgstoreMessageID\tCaptionMessageID\tFromPartner\tCaptionSha256";

    internal required PairingParameters Parameters { get; init; }
    internal required IReadOnlyList<CaptionRow> Rows { get; init; }

    internal static bool IsOne(string? path) => FactsManifestFormat.Names(path, Token);

    internal byte[] ToBytes() =>
        FactsManifestFormat.ToBytes(Token, Header, Parameters, Rows.Select(row =>
            $"{Text.Id(row.AttachmentID)}\t{Text.Id(row.MessageID)}\t{Text.Id(row.CaptionMessageID)}\t" +
            $"{(row.FromPartner ? "1" : "0")}\t{row.CaptionSha256}"));

    /// <exception cref="FormatException">The bytes are not a well-formed caption-facts manifest.</exception>
    internal static CaptionFactsManifest Parse(byte[] bytes)
    {
        var (parameters, body) = FactsManifestFormat.Parse(bytes, Token, Header);

        static bool Id(string text, out long value) =>
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

        var rows = new List<CaptionRow>(body.Length);
        var seen = new HashSet<long>();

        foreach (var line in body)
        {
            var parts = line.Split('\t');

            if (parts.Length != 5 || !Id(parts[0], out var attachment) || !Id(parts[1], out var message)
                || !Id(parts[2], out var captionMessage) || parts[3] is not ("0" or "1")
                || parts[4].Length != 64 || !parts[4].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                throw new FormatException("A manifest row is malformed.");
            }

            var fromCopy = parts[3] == "1";

            if (fromCopy == (captionMessage == message))
            {
                throw new FormatException("A manifest row's caption source does not agree with its FromPartner flag.");
            }

            if (!seen.Add(attachment))
            {
                throw new FormatException("The manifest names an attachment twice.");
            }

            rows.Add(new CaptionRow(attachment, message, captionMessage, fromCopy, parts[4]));
        }

        return new CaptionFactsManifest { Parameters = parameters, Rows = rows };
    }
}

/// <summary>What the caption computation found. Counts, and the rows (with their text, in memory only).</summary>
internal sealed class CaptionComputation
{
    internal required string TextColumn { get; init; }
    internal required string? MediaCaptionColumn { get; init; }
    internal required bool AssociationTable { get; init; }
    internal required int Attachments { get; init; }
    internal required int PairedByMinute { get; init; }
    internal required int PairedByDay { get; init; }
    internal required Tally Unpaired { get; init; }
    internal required Tally Ambiguous { get; init; }
    internal required int LeftAlone { get; init; }
    internal required int NoCaption { get; init; }
    internal required int FromMediaCaptionColumn { get; init; }
    internal required int OwnAndPartnerDiffer { get; init; }
    internal required Tally PartnerLinksRefused { get; init; }
    internal required int NfcFailures { get; init; }
    internal required List<CaptionRow> Rows { get; init; }

    /// <summary>The caption text of each row, by AttachmentID. Never printed.</summary>
    internal required Dictionary<long, string> Captions { get; init; }

    internal int Paired => PairedByMinute + PairedByDay;
}

/// <summary>
/// <c>plan --rule caption-facts</c> (CH-63, CH-71): for every attachment paired by the minute or
/// day-sequence rule, the picture caption WhatsApp stored for its message, copied from the msgstore.
/// </summary>
/// <remarks>
/// <para><strong>Pairing</strong> is exactly the <c>facts</c> rule's (<see cref="AttachmentPairs"/>):
/// the minute rule, then the CH-9 day-sequence rule. An attachment the two rules pair differently, or
/// whose record is paired to two attachments, is ambiguous and gets no row; one neither rule pairs is
/// unpaired and gets no row.</para>
/// <para><strong>The caption</strong> of a msgstore message is its text column (<c>message.text_data</c>,
/// or <c>message.data</c> in an older schema). Only when that holds no caption is a
/// <c>media_caption</c> column read, if the msgstore has one (on <c>message_media</c>, else on
/// <c>message</c>). The text column wins because it is where the current schema keeps a caption, the
/// same column the text rules read; <c>media_caption</c> is a legacy column whose value, after a schema
/// upgrade, may be stale. A value counts as a caption when it is a string that is not empty after the
/// CH-22 normalisation (U+200E/U+200F removed, LF, NFC, trim). The normalisation is used only to decide
/// that and to compare; the stored caption is the msgstore value exactly, untrimmed.</para>
/// <para><strong>The type-12 partner</strong> (either side of the link: the hidden low-quality copy, or the shown full copy for a caption that sits on it while the attachment is the hidden copy). When the attachment's own message has no caption,
/// the messages linked to it by <c>message_association</c> type 12 (either side) are looked at, but
/// only a partner in the same chat, the same direction and the same local minute, as the <c>facts</c>
/// rule requires of a link. Exactly one distinct caption among them is used and marked
/// <c>FromPartner</c>; two or more different ones make the attachment ambiguous (no row). When
/// the own message has a caption it is used, whatever the partner holds; a different partner caption
/// is only counted.</para>
/// <para>Missing is better than wrong: no caption is ever guessed, merged or edited.</para>
/// </remarks>
internal static class CaptionFactsPlanner
{
    internal const string RulesDisagree = FactsPlanner.RulesDisagree;
    internal const string MessageTwice = FactsPlanner.MessageTwice;
    internal const string PartnersDisagree = "own message has no caption, partners' captions differ";
    internal const string LeftAloneReason = "already have a caption from another batch (left alone)";

    internal const string LinkOtherChat = "partner not a message of the chat";
    internal const string LinkDirection = "partner in the other direction";
    internal const string LinkMinute = "partner in another local minute (or no time)";

    /// <summary>The <c>media_caption</c> places looked for, in order.</summary>
    internal static readonly (string Table, string Key)[] MediaCaptionPlaces =
    [
        ("message_media", "message_row_id"),
        ("message", "_id"),
    ];

    /// <summary>
    /// The caption of every paired attachment, read-only on both databases. Captions already stored
    /// under <paramref name="ownBatch"/> are treated as absent, so apply can recompute the plan it is
    /// applying.
    /// </summary>
    internal static CaptionComputation Compute(string msgstorePath, string workspacePath, PairingParameters parameters, string? ownBatch)
    {
        WorkspaceMigrator.RequireCurrent(workspacePath);

        return Inputs.WithPairing(msgstorePath, workspacePath, parameters, pairing => Compute(pairing, ownBatch));
    }

    private sealed record Message(bool FromMe, DateTime? Minute, string? Text, string? MediaCaption);

    private static CaptionComputation Compute(Pairing pairing, string? ownBatch)
    {
        var msgstore = pairing.MsgstoreConnection;
        var chat = pairing.Parameters.ChatID;

        var textColumn = TextPairing.TextColumn(msgstore);
        (string Table, string Key)? place = null;

        foreach (var candidate in MediaCaptionPlaces)
        {
            if (TextPairing.HasColumns(msgstore, candidate.Table, candidate.Key, "media_caption"))
            {
                place = candidate;
                break;
            }
        }
        var association = TextPairing.HasColumns(msgstore, "message_association", "parent_message_row_id", "child_message_row_id", "association_type");

        TextProber.NfcFailures = 0;

        // 1. Pairing, exactly as the facts rule.
        var pairs = AttachmentPairs.Compute(pairing);
        var unpaired = new Tally();
        var ambiguous = new Tally();

        foreach (var (reason, count) in pairs.NoRow.Items)
        {
            if (reason is FactsPlanner.RulesDisagree or FactsPlanner.MessageTwice)
            {
                ambiguous.Add(reason, count);
            }
            else
            {
                unpaired.Add(reason, count);
            }
        }

        // 2. Every message of the chat: direction, local minute, text column and media_caption.
        var messages = ReadMessages(msgstore, pairing, textColumn, place);

        // 3. Type-12 partners, either side.
        var partners = new Dictionary<long, HashSet<long>>();

        if (association)
        {
            using var command = msgstore.CreateCommand();
            command.CommandText =
                """
                SELECT a.parent_message_row_id, a.child_message_row_id
                FROM message_association AS a
                WHERE a.association_type = $type
                  AND (a.parent_message_row_id IN (SELECT _id FROM message WHERE chat_row_id = $chat)
                       OR a.child_message_row_id IN (SELECT _id FROM message WHERE chat_row_id = $chat));
                """;
            command.Parameters.AddWithValue("$type", FactsPlanner.LowQualityAssociationType);
            command.Parameters.AddWithValue("$chat", chat);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)
                    || reader.GetValue(0) is not long parent || reader.GetValue(1) is not long child || parent == child)
                {
                    continue;
                }

                Link(parent, child);
                Link(child, parent);
            }

            void Link(long from, long to)
            {
                if (!partners.TryGetValue(from, out var set))
                {
                    partners[from] = set = [];
                }

                set.Add(to);
            }
        }

        // 4. Captions already stored (any batch but this one's own), through the public reader.
        var stored = new AttachmentCaptionFactReader().Read(pairing.Workspace, pairs.MessageOf.Keys.ToList());
        var leftAlone = pairs.MessageOf.Keys
            .Where(a => stored.TryGetValue(a, out var f) && !string.Equals(f.SourceBatch, ownBatch, StringComparison.Ordinal))
            .ToHashSet();

        // 5. Rows.
        var rows = new List<CaptionRow>();
        var captions = new Dictionary<long, string>();
        var linksRefused = new Tally();
        int noCaption = 0, fromMediaCaption = 0, differ = 0;

        foreach (var (attachment, messageID) in pairs.MessageOf.OrderBy(p => p.Key))
        {
            if (leftAlone.Contains(attachment))
            {
                continue;
            }

            var own = messages.GetValueOrDefault(messageID);
            var ownFromMediaCaption = false;
            var ownCaption = own is null ? null : CaptionOf(own, out ownFromMediaCaption);

            // The partners that pass the link checks, and the distinct captions among them.
            var partnerCaptions = new List<(long ID, string Caption, bool FromMediaCaption)>();

            foreach (var partnerID in partners.GetValueOrDefault(messageID) ?? [])
            {
                if (!messages.TryGetValue(partnerID, out var partner))
                {
                    linksRefused.Add(LinkOtherChat);
                }
                else if (own is null || partner.FromMe != own.FromMe)
                {
                    linksRefused.Add(LinkDirection);
                }
                else if (partner.Minute is null || partner.Minute != own.Minute)
                {
                    linksRefused.Add(LinkMinute);
                }
                else if (CaptionOf(partner, out var partnerFromMediaCaption) is { } c)
                {
                    partnerCaptions.Add((partnerID, c, partnerFromMediaCaption));
                }
            }

            if (ownCaption is not null)
            {
                if (partnerCaptions.Any(p => !string.Equals(TextProber.Normalise(p.Caption), TextProber.Normalise(ownCaption), StringComparison.Ordinal)))
                {
                    differ++;
                }

                fromMediaCaption += ownFromMediaCaption ? 1 : 0;
                rows.Add(new CaptionRow(attachment, messageID, messageID, false, CaptionRow.Digest(ownCaption)));
                captions[attachment] = ownCaption;

                continue;
            }

            var distinct = partnerCaptions.GroupBy(p => p.Caption, StringComparer.Ordinal).ToList();

            if (distinct.Count == 0)
            {
                noCaption++;
            }
            else if (distinct.Count > 1)
            {
                ambiguous.Add(PartnersDisagree);
            }
            else
            {
                // One distinct caption; if two partners hold it, the lowest message id is named.
                var chosen = distinct[0].OrderBy(p => p.ID).First();

                fromMediaCaption += chosen.FromMediaCaption ? 1 : 0;
                rows.Add(new CaptionRow(attachment, messageID, chosen.ID, true, CaptionRow.Digest(chosen.Caption)));
                captions[attachment] = chosen.Caption;
            }
        }

        return new CaptionComputation
        {
            TextColumn = textColumn,
            MediaCaptionColumn = place is { } found ? $"{found.Table}.media_caption" : null,
            AssociationTable = association,
            Attachments = pairs.Minute.Count,
            PairedByMinute = pairs.ByMinute,
            PairedByDay = pairs.ByDay,
            Unpaired = unpaired,
            Ambiguous = ambiguous,
            LeftAlone = leftAlone.Count,
            NoCaption = noCaption,
            FromMediaCaptionColumn = fromMediaCaption,
            OwnAndPartnerDiffer = differ,
            PartnerLinksRefused = linksRefused,
            NfcFailures = TextProber.NfcFailures,
            Rows = rows,
            Captions = captions,
        };
    }

    /// <summary>
    /// The message's caption: the text column when it holds one, else <c>media_caption</c> when it
    /// holds one, else null. Returned exactly as stored.
    /// </summary>
    private static string? CaptionOf(Message message, out bool fromMediaCaption)
    {
        fromMediaCaption = false;

        if (HasCaption(message.Text))
        {
            return message.Text;
        }

        if (HasCaption(message.MediaCaption))
        {
            fromMediaCaption = true;

            return message.MediaCaption;
        }

        return null;
    }

    private static bool HasCaption(string? value) => value is not null && TextProber.Normalise(value).Length > 0;

    private static Dictionary<long, Message> ReadMessages(
        SqliteConnection msgstore, Pairing pairing, string textColumn, (string Table, string Key)? place)
    {
        // Table and column names come only from MediaCaptionPlaces, never from input.
        var mediaCaption = place is not { } p
            ? "NULL"
            : p.Table == "message"
                ? "m.media_caption"
                : $"(SELECT x.media_caption FROM \"{p.Table}\" AS x WHERE x.\"{p.Key}\" = m._id LIMIT 1)";

        using var command = msgstore.CreateCommand();
        command.CommandText =
            $"""
            SELECT m._id, m.from_me, m.timestamp, m."{textColumn}", {mediaCaption}
            FROM message AS m
            WHERE m.chat_row_id = $chat;
            """;
        command.Parameters.AddWithValue("$chat", pairing.Parameters.ChatID);

        var result = new Dictionary<long, Message>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.GetValue(0) is not long id)
            {
                continue;
            }

            var local = pairing.Local(reader.IsDBNull(2) || reader.GetValue(2) is not long ts ? null : ts);

            result[id] = new Message(
                !reader.IsDBNull(1) && reader.GetValue(1) is long fromMe && fromMe != 0,
                local is { } t ? new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0) : null,
                reader.IsDBNull(3) ? null : reader.GetValue(3) as string,
                reader.IsDBNull(4) ? null : reader.GetValue(4) as string);
        }

        return result;
    }

    internal sealed class CaptionPlanResult
    {
        internal required CaptionComputation Captions { get; init; }
        internal required string Sha256 { get; init; }
    }

    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static CaptionPlanResult Plan(Options options)
    {
        if (Planner.ManifestOutRefusal(options.ManifestOut!) is { } refusal)
        {
            throw new RefusalException(refusal);
        }

        var parameters = options.Parameters!;
        var captions = Compute(options.MsgstorePath!, options.WorkspacePath, parameters, ownBatch: null);
        var bytes = new CaptionFactsManifest { Parameters = parameters, Rows = captions.Rows }.ToBytes();

        using (var stream = new FileStream(options.ManifestOut!, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
        }

        return new CaptionPlanResult { Captions = captions, Sha256 = Manifest.Sha256Hex(bytes) };
    }

    internal static bool Reconciled(CaptionComputation c) =>
        c.Rows.Count + c.NoCaption + c.LeftAlone + c.Ambiguous[PartnersDisagree] == c.Paired
        && c.Paired + c.Unpaired.Total + c.Ambiguous.Total - c.Ambiguous[PartnersDisagree] == c.Attachments;

    internal static string Report(CaptionPlanResult result)
    {
        var c = result.Captions;
        var text = new StringBuilder();

        string Present(bool yes) => yes ? "present" : "absent";

        text.AppendLine("# Msgstore import - plan caption-facts (CH-63, CH-71)");
        text.AppendLine();
        text.AppendLine("Read-only on both databases. Captions are read in memory only. Counts only; identifiers and");
        text.AppendLine("caption digests are in the private manifest, never the captions themselves.");
        text.AppendLine();
        text.AppendLine("Rule: the facts rule's pairing (minute, then day sequence). Caption = the text column; only when");
        text.AppendLine("that is empty, media_caption. If the own message has none, the type-12 partner's (same chat,");
        text.AppendLine("direction and minute). Stored exactly as the msgstore holds it.");
        text.AppendLine();
        text.AppendLine("```text");
        text.AppendLine($"{"msgstore caption column",-58}message.{c.TextColumn}");
        text.AppendLine($"{"msgstore media_caption column (fallback)",-58}{c.MediaCaptionColumn ?? "absent"}");
        text.AppendLine($"{"msgstore message_association table",-58}{Present(c.AssociationTable)}");
        text.AppendLine();
        text.AppendLine(Text.Line("attachments in the conversation", c.Attachments));
        text.AppendLine(Text.Line("  paired", c.Paired));
        text.AppendLine(Text.Line("    by the minute rule", c.PairedByMinute));
        text.AppendLine(Text.Line("    by the day-sequence rule", c.PairedByDay));
        text.AppendLine(Text.Line("  unpaired (no row)", c.Unpaired.Total));

        foreach (var (reason, count) in c.Unpaired.Items)
        {
            text.AppendLine(Text.Line("    " + reason, count));
        }

        text.AppendLine(Text.Line("  ambiguous pairing (no row)", c.Ambiguous.Total - c.Ambiguous[PartnersDisagree]));

        foreach (var reason in new[] { RulesDisagree, MessageTwice })
        {
            text.AppendLine(Text.Line("    " + reason, c.Ambiguous[reason]));
        }

        text.AppendLine();
        text.AppendLine(Text.Line("paired attachments with a caption (manifest rows)", c.Rows.Count));
        text.AppendLine(Text.Line("  from their own message", c.Rows.Count(r => !r.FromPartner)));
        text.AppendLine(Text.Line("  from the type-12 linked partner (either side)", c.Rows.Count(r => r.FromPartner)));
        text.AppendLine(Text.Line("  read from media_caption (text column empty)", c.FromMediaCaptionColumn));
        text.AppendLine(Text.Line("  own caption used, partner's differs", c.OwnAndPartnerDiffer));
        text.AppendLine(Text.Line("paired attachments with no caption (no row)", c.NoCaption));
        text.AppendLine(Text.Line("paired, ambiguous caption (no row)", c.Ambiguous[PartnersDisagree]));
        text.AppendLine(Text.Line("  " + PartnersDisagree, c.Ambiguous[PartnersDisagree]));
        text.AppendLine(Text.Line("paired, " + LeftAloneReason, c.LeftAlone));
        text.AppendLine();
        text.AppendLine(Text.Line("type-12 partner links refused", c.PartnerLinksRefused.Total));

        foreach (var reason in new[] { LinkOtherChat, LinkDirection, LinkMinute })
        {
            text.AppendLine(Text.Line("  " + reason, c.PartnerLinksRefused[reason]));
        }

        text.AppendLine(Text.Line("texts the system could not normalise (compared as they are)", c.NfcFailures));
        text.AppendLine();
        text.AppendLine(Text.Line("rows + no caption + ambiguous caption + left alone", c.Rows.Count + c.NoCaption + c.Ambiguous[PartnersDisagree] + c.LeftAlone));
        text.AppendLine($"{"reconciliation (paired, and attachments)",-58}{(Reconciled(c) ? "OK" : "MISMATCH")}");
        text.AppendLine("```");
        text.AppendLine();
        text.AppendLine("manifest SHA-256: " + result.Sha256);
        text.AppendLine("batch tag:        " + Manifest.Tag(result.Sha256));

        return text.ToString();
    }
}

/// <summary>Apply and undo-batch for a caption-facts manifest, through <see cref="AttachmentCaptionFactWriter"/> only.</summary>
/// <remarks>
/// All-or-nothing, as the writer is: apply recomputes every row from the msgstore and workspace and
/// refuses the whole batch, writing nothing, when any row (caption digest included) differs from the
/// manifest, or an attachment would now get a row the manifest lacks.
/// </remarks>
internal static class CaptionFactsBatch
{
    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static FactsWriteResult Apply(Options options)
    {
        var (manifest, tag) = FactsManifestFormat.Load(options.ManifestPath!, options.ManifestSha256!, CaptionFactsManifest.Parse);

        if (options.ReviewFile is not null)
        {
            throw new RefusalException("--review-file applies only to a manifest of rule namesize-reviewed.");
        }

        Inputs.RequireMsgstore(options.MsgstorePath!);
        WorkspaceMigrator.RequireCurrent(options.WorkspacePath);
        Inputs.RequireUnclaimed(options.WorkspacePath);

        var now = CaptionFactsPlanner.Compute(options.MsgstorePath!, options.WorkspacePath, manifest.Parameters, ownBatch: tag);
        var current = now.Rows.ToDictionary(r => r.AttachmentID);
        var planned = manifest.Rows.Select(r => r.AttachmentID).ToHashSet();

        var changed = manifest.Rows.Count(r => !current.TryGetValue(r.AttachmentID, out var c) || c != r);
        var added = current.Keys.Count(id => !planned.Contains(id));

        if (changed > 0 || added > 0)
        {
            throw new RefusalException(
                $"{Text.Count(changed)} manifest rows no longer match the msgstore and workspace, and " +
                $"{Text.Count(added)} attachments would now get a row the manifest lacks. Plan again. Nothing was written.");
        }

        IReadOnlyDictionary<long, AttachmentCaptionFact> stored;

        try
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(options.WorkspacePath);
            stored = new AttachmentCaptionFactReader().Read(connection, [.. planned]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        var already = 0;
        var toWrite = new List<AttachmentCaptionFactRow>();

        foreach (var row in manifest.Rows)
        {
            if (!stored.TryGetValue(row.AttachmentID, out var fact))
            {
                toWrite.Add(new AttachmentCaptionFactRow(row.AttachmentID, now.Captions[row.AttachmentID], row.FromPartner));
            }
            else if (fact.SourceBatch == tag && fact.FromPartner == row.FromPartner
                     && CaptionRow.Digest(fact.CaptionText) == row.CaptionSha256)
            {
                already++;
            }
            else
            {
                throw new RefusalException(
                    "An attachment in the manifest already has a different caption. Plan again. Nothing was written.");
            }
        }

        var done = 0;

        if (toWrite.Count > 0)
        {
            try
            {
                done = AttachmentCaptionFactWriter.InsertBatch(options.WorkspacePath, tag, toWrite, DateTime.UtcNow);
            }
            catch (Exception exception) when (Inputs.IsClaimFailure(exception))
            {
                throw new RefusalException(WorkspaceMigrator.ClaimedRefusal);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                // The writer's message names attachments, so only the fact is printed.
                throw new RefusalException("The caption writer refused the batch. Nothing was written.");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        return new FactsWriteResult { Mode = "apply caption-facts", Tag = tag, Rows = manifest.Rows.Count, Done = done, AlreadyDone = already };
    }

    /// <summary>Removes this batch's rows, and only this batch's.</summary>
    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static FactsWriteResult Undo(Options options)
    {
        var (manifest, tag) = FactsManifestFormat.Load(options.ManifestPath!, options.ManifestSha256!, CaptionFactsManifest.Parse);

        WorkspaceMigrator.RequireCurrent(options.WorkspacePath);
        Inputs.RequireUnclaimed(options.WorkspacePath);

        int deleted;

        try
        {
            deleted = AttachmentCaptionFactWriter.DeleteBatch(options.WorkspacePath, tag);
        }
        catch (Exception exception) when (Inputs.IsClaimFailure(exception))
        {
            throw new RefusalException(WorkspaceMigrator.ClaimedRefusal);
        }
        catch (InvalidOperationException)
        {
            throw new RefusalException("The caption writer refused to remove the batch. Nothing was written.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        return new FactsWriteResult
        {
            Mode = "undo-batch caption-facts",
            Tag = tag,
            Rows = manifest.Rows.Count,
            Done = deleted,
            NotPresent = Math.Max(0, manifest.Rows.Count - deleted),
        };
    }
}
