using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Chatcheology.Data.NewerMessages;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport;

/// <summary>The fixed inputs of a newer-messages plan. All three are operator-supplied, never derived.</summary>
internal sealed record NewerParameters(long ConversationID, long ChatID, long LocalParticipantID);

/// <summary>Everything a newer-messages plan found: the rows to write and the counts to print.</summary>
internal sealed class NewerComputation
{
    internal required NewerParameters Parameters { get; init; }
    internal required long OtherParticipantID { get; init; }
    internal required bool FirstRun { get; init; }
    internal required int PreviousRuns { get; init; }
    internal required string HandOffKeyID { get; init; }

    // First-run hand-off figures (zero on later runs).
    internal int LastDayWorkspaceMessages { get; init; }
    internal int EarlierDaysCompared { get; init; }
    internal int ComparedText { get; init; }
    internal int ComparedMedia { get; init; }
    internal int ComparedDirectionOnly { get; init; }

    internal required List<NewerMessageRow> Rows { get; init; }
    internal required SortedDictionary<string, int> ByMonth { get; init; }
    /// <summary>New messages by msgstore message_type and the kind given to them.</summary>
    internal required SortedDictionary<(long Type, string Kind), int> ByType { get; init; }
    internal required int NoType { get; init; }
    internal required Tally Unmatched { get; init; }
    internal required int MediaTypeWithoutRecord { get; init; }
    internal required Tally LinksRefused { get; init; }
    internal required int Links { get; init; }
    internal required int? AddOns { get; init; }
    internal required int? QuotedReplies { get; init; }
    internal required bool ForwardingKnown { get; init; }
    internal required int EditedSinceImport { get; init; }
    internal required int DeletedSinceImport { get; init; }
    internal required int GoneFromMsgstore { get; init; }
    internal required int NfcFailures { get; init; }

    internal required long MsgstoreSizeBytes { get; init; }
    internal required long MsgstoreUserVersion { get; init; }
    internal required string MsgstoreSchemaSha256 { get; init; }
    internal required string MediaRootSha256 { get; init; }

    /// <summary>The manifest this computation writes (set once, right after the computation).</summary>
    internal byte[] ManifestBytes { get; set; } = [];
}

/// <summary>
/// <c>newer plan</c> (CH-71 step 3a, D2-D4, D6): the msgstore messages of the pinned chat that come after the
/// main workspace (first run) or after the last imported message (later runs), as rows for
/// <c>newer-messages.db</c>. Read-only on the msgstore, the workspace, the newer-messages file and the media folder.
/// </summary>
/// <remarks>
/// <para><strong>Hand-off, first run (D2).</strong> The population is the census's (the CH-9 day-sequence rule's):
/// every message row of the chat with a readable time, on its local day, in <c>sort_id</c> then <c>_id</c> order,
/// against every workspace message of the conversation in <c>SequenceNumber</c> order. Every day before the
/// workspace's last day must have equal counts. On the last day the msgstore's messages must start with exactly
/// the workspace's: at each position the same direction, and (a) a workspace text message faces a msgstore message
/// with the same text after the CH-22 normalisation (the export's trailing edited marker removed first), (b) a
/// workspace attachment or media placeholder faces a msgstore message with a media record, (c) any other pair
/// (system rows, deleted messages and the like, which the export writes as text the msgstore does not hold) is
/// compared by direction only, as the day-sequence rule does, but a media record never faces a workspace
/// text or system message. The first new message is the next one in order.</para>
/// <para><strong>Later runs.</strong> The last imported message's key_id must be found in the chat (exactly once);
/// the new messages are those after it.</para>
/// <para>The plan refuses, with counts only, when the hand-off does not line up, when a message after it has no
/// readable time or no usable key_id, or when order and time disagree across the hand-off.</para>
/// </remarks>
internal static class NewerPlanner
{
    internal const string ManifestMagic = "chatcheology-newer-messages-manifest";
    internal const string ManifestVersion = "1";

    internal const string ManifestHeader =
        "MsgstoreRowID\tKeyID\tSortID\tFromMe\tTimestampUtcMs\tMessageType\tKind\tPlaceholderKind\tTextSha256\t" +
        "CaptionSha256\tIsForwarded\tForwardScore\tAttachmentState\tFileSha256\tFileSizeBytes\tLowQualityCopyOfKeyID";

    // Unmatched reasons, in print order.
    internal const string NoPath = "msgstore has no usable relative file_path";
    internal const string NoFile = "no file at file_path under the media root";
    internal const string HashEmpty = "msgstore file_hash empty";
    internal const string HashOther = "msgstore file_hash not a base64 SHA-256";
    internal const string HashDiffers = "file's SHA-256 differs from file_hash";
    internal static readonly string[] UnmatchedReasons = [NoPath, NoFile, HashEmpty, HashOther, HashDiffers];

    // Low-quality link refusals, in print order.
    internal const string LinkSide = "a side is not an imported attachment";
    internal const string LinkEarlier = "low-quality side imported in an earlier run";
    internal const string LinkMinute = "sides in different local minutes";
    internal const string LinkDirection = "sides in different directions";
    internal const string LinkSize = "low-quality file not strictly smaller (or a size unknown)";
    internal const string LinkTwoTargets = "copy of two or more full copies";
    internal const string LinkChain = "would form a chain";
    internal static readonly string[] LinkReasons = [LinkSide, LinkEarlier, LinkMinute, LinkDirection, LinkSize, LinkTwoTargets, LinkChain];

    private sealed class MsRow
    {
        internal required long ID { get; init; }
        internal required string? RawKey { get; init; }
        internal required string? Key { get; init; }
        internal required bool FromMe { get; init; }
        internal required long? Timestamp { get; init; }
        internal required DateTime? Local { get; init; }
        internal required long? Type { get; init; }
        internal required long OrderKey { get; init; }
        internal required string? Text { get; init; }
        internal required string? Norm { get; init; }
        internal required bool HasMedia { get; init; }
        internal string? FilePath { get; init; }
        internal long? FileSize { get; init; }
        internal object? FileHash { get; init; }
        internal string? Mime { get; init; }
        internal string? MediaName { get; init; }
        internal string? MediaCaption { get; init; }

        internal DateOnly? Day => Local is { } t ? DateOnly.FromDateTime(t) : null;
        internal DateTime? Minute => Local is { } t ? new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0) : null;
        internal bool TextBearing => Norm is { Length: > 0 };
    }

    private sealed record WsRow(DateOnly Day, bool Outgoing, TextProber.WsKind Kind, string Norm, string Stripped);

    /// <summary>Computes the plan, read-only on every input.</summary>
    /// <exception cref="RefusalException">An input is unsuitable or the hand-off does not line up. Nothing was written.</exception>
    internal static NewerComputation Compute(string msgstorePath, string workspacePath, string mediaRoot, NewerParameters p)
    {
        Inputs.RequireMsgstore(msgstorePath);
        Inputs.RequireWorkspace(workspacePath);

        if (!Directory.Exists(mediaRoot))
        {
            throw new RefusalException("There is no folder at the --media-root path.");
        }

        var newerPath = NewerMessagesDatabase.PathBeside(workspacePath);
        NewerMessagesContents? stored;

        try
        {
            stored = File.Exists(newerPath) ? NewerMessagesReader.Read(newerPath) : null;
        }
        catch (InvalidOperationException)
        {
            throw new RefusalException(
                "The newer-messages.db beside the workspace is not a newer-messages file of this format. Nothing was written.");
        }

        if (stored is not null && (stored.Info.ConversationID != p.ConversationID || stored.Info.ChatRowID != p.ChatID
                                   || stored.Info.LocalParticipantID != p.LocalParticipantID))
        {
            throw new RefusalException(
                "The newer-messages.db beside the workspace continues another conversation, chat or local participant. Nothing was written.");
        }

        var before = FileStamp.Of(msgstorePath);
        NewerComputation result;

        try
        {
            using var msgstore = Msgstore.OpenReadOnly(msgstorePath);
            var schema = MsgstoreSchema.Read(msgstore);

            if (schema.Refusal() is { } schemaRefusal)
            {
                throw new RefusalException(schemaRefusal);
            }

            using var workspace = WorkspaceDatabase.OpenReadOnlyConnection(workspacePath);

            Inputs.RequireSchemaVersion(workspace);
            Inputs.RequireKnown(workspace, msgstore, new PairingParameters(p.ConversationID, p.ChatID, NewerMessagesTimeZone.Offset, p.LocalParticipantID));

            result = Compute(workspace, msgstore, schema, p, stored, mediaRoot, before.Length);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new RefusalException("The workspace is busy: another program holds a lock on it. Nothing was written.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        if (FileStamp.Of(msgstorePath) != before)
        {
            throw new RefusalException("The msgstore's size or last-write time changed during the read.");
        }

        return result;
    }

    private static NewerComputation Compute(
        SqliteConnection workspace, SqliteConnection msgstore, MsgstoreSchema schema, NewerParameters p,
        NewerMessagesContents? stored, string mediaRoot, long msgstoreSize)
    {
        TextProber.NfcFailures = 0;

        // Pinned scope (data/phase6_request_scope): the workspace holds one conversation, of two participants.
        if (Scalar(workspace, "SELECT COUNT(*) FROM Conversation;") != 1)
        {
            throw new RefusalException("The workspace does not hold exactly one conversation. Nothing was written.");
        }

        var participants = Ids(workspace, $"SELECT ParticipantID FROM ConversationParticipant WHERE ConversationID = {p.ConversationID.ToString(CultureInfo.InvariantCulture)};");

        if (participants.Count != 2 || !participants.Contains(p.LocalParticipantID))
        {
            throw new RefusalException(
                "The conversation does not have exactly two participants, one of them the local participant. Nothing was written.");
        }

        var other = participants.Single(id => id != p.LocalParticipantID);
        var textColumn = TextPairing.TextColumn(msgstore);
        var all = ReadMsgstore(msgstore, schema, p.ChatID, textColumn);

        var firstRun = stored?.LastKeyID is null;
        MsRow handOff;
        int lastDayCount = 0, earlierDays = 0, comparedText = 0, comparedMedia = 0, directionOnly = 0;

        if (firstRun)
        {
            var ws = ReadWorkspace(workspace, p);

            if (ws.Count == 0)
            {
                throw new RefusalException("The workspace conversation has no message with a readable time. Nothing was written.");
            }

            var lastDay = ws.Max(w => w.Day);
            var wsDays = ws.GroupBy(w => w.Day).ToDictionary(g => g.Key, g => g.Count());
            var msDays = all.Where(m => m.Day is not null).GroupBy(m => m.Day!.Value).ToDictionary(g => g.Key, g => g.Count());

            int onlyWs = 0, onlyMs = 0, differ = 0;

            foreach (var day in wsDays.Keys.Union(msDays.Keys).Where(d => d < lastDay))
            {
                earlierDays++;
                var w = wsDays.GetValueOrDefault(day);
                var m = msDays.GetValueOrDefault(day);

                if (w == m)
                {
                    continue;
                }

                if (m == 0)
                {
                    onlyWs++;
                }
                else if (w == 0)
                {
                    onlyMs++;
                }
                else
                {
                    differ++;
                }
            }

            if (onlyWs + onlyMs + differ > 0)
            {
                throw new RefusalException(
                    $"Hand-off refused: {Text.Count(onlyWs + onlyMs + differ)} of {Text.Count(earlierDays)} days before the " +
                    $"workspace's last day have different message counts ({Text.Count(onlyWs)} with messages only in the " +
                    $"workspace, {Text.Count(onlyMs)} only in the msgstore, {Text.Count(differ)} in both with different " +
                    "counts). Nothing was written.");
            }

            var wsLast = ws.Where(w => w.Day == lastDay).ToList();
            var msLast = all.Where(m => m.Day == lastDay).ToList();
            lastDayCount = wsLast.Count;

            if (msLast.Count < wsLast.Count)
            {
                throw new RefusalException(
                    $"Hand-off refused: on the workspace's last day the msgstore has {Text.Count(msLast.Count)} messages, " +
                    $"fewer than the workspace's {Text.Count(wsLast.Count)}. Nothing was written.");
            }

            int direction = 0, text = 0, kind = 0;

            for (var k = 0; k < wsLast.Count; k++)
            {
                var w = wsLast[k];
                var m = msLast[k];

                if (w.Outgoing != m.FromMe)
                {
                    direction++;
                    continue;
                }

                switch (w.Kind)
                {
                    case TextProber.WsKind.Attachment or TextProber.WsKind.MediaWithCaption:
                        if (m.HasMedia)
                        {
                            comparedMedia++;
                        }
                        else
                        {
                            kind++;
                        }

                        break;

                    case TextProber.WsKind.Text when m.HasMedia:
                        kind++;
                        break;

                    case TextProber.WsKind.Text when m.TextBearing:
                        if (string.Equals(m.Norm, w.Norm, StringComparison.Ordinal)
                            || string.Equals(m.Norm, w.Stripped, StringComparison.Ordinal))
                        {
                            comparedText++;
                        }
                        else
                        {
                            text++;
                        }

                        break;

                    default:
                        if (m.HasMedia)
                        {
                            kind++;
                        }
                        else
                        {
                            directionOnly++;
                        }

                        break;
                }
            }

            if (direction + text + kind > 0)
            {
                throw new RefusalException(
                    $"Hand-off refused: on the workspace's last day the msgstore's first {Text.Count(wsLast.Count)} messages " +
                    $"do not match the workspace's ({Text.Count(direction)} differ in direction, {Text.Count(text)} in text, " +
                    $"{Text.Count(kind)} media against text). Nothing was written.");
            }

            handOff = msLast[wsLast.Count - 1];

            // Order and time must agree across the hand-off: nothing after it on an earlier day, nothing before
            // it on a later day.
            var crossing = all.Count(m => m.Day is { } d && (After(m, handOff) ? d < lastDay : d > lastDay));

            if (crossing > 0)
            {
                throw new RefusalException(
                    $"Hand-off refused: {Text.Count(crossing)} msgstore messages are on the wrong side of the hand-off " +
                    "(their sort_id order and their day disagree). Nothing was written.");
            }
        }
        else
        {
            var found = all.Where(m => string.Equals(m.RawKey, stored!.LastKeyID, StringComparison.Ordinal)).ToList();

            if (found.Count != 1)
            {
                throw new RefusalException(
                    $"Hand-off refused: the last imported message's key_id is found {Text.Count(found.Count)} times in this " +
                    "msgstore chat (exactly 1 is needed). A gap is possible, so nothing is imported. Nothing was written.");
            }

            handOff = found[0];
        }

        if (handOff.Key is null)
        {
            throw new RefusalException("Hand-off refused: the hand-off message has no usable key_id. Nothing was written.");
        }

        var fresh = all.Where(m => After(m, handOff)).ToList();
        var noTime = fresh.Count(m => m.Local is null);
        var noKey = fresh.Count(m => m.Key is null);
        var twice = fresh.Where(m => m.Key is not null).GroupBy(m => m.Key!, StringComparer.Ordinal).Count(g => g.Count() > 1);
        var storedKeys = stored?.Messages.Select(m => m.KeyID).ToHashSet(StringComparer.Ordinal) ?? [];
        var already = fresh.Count(m => m.Key is not null && storedKeys.Contains(m.Key));

        if (noTime + noKey + twice + already > 0)
        {
            throw new RefusalException(
                $"Refused: of the {Text.Count(fresh.Count)} messages after the hand-off, {Text.Count(noTime)} have no readable " +
                $"time, {Text.Count(noKey)} no usable key_id, {Text.Count(twice)} key_ids appear twice and " +
                $"{Text.Count(already)} are already imported. Nothing was written.");
        }

        // Forwarding: a message_forwarded row (forward_score when present); the legacy column only without the table.
        var forwardTable = TextPairing.HasColumns(msgstore, "message_forwarded", "message_row_id");
        var legacy = !forwardTable && TextPairing.HasColumns(msgstore, "message", "forwarded");
        var forwarded = ForwardedFacts.Read(msgstore, p.ChatID, forwardTable,
            forwardTable && TextPairing.HasColumns(msgstore, "message_forwarded", "forward_score"), legacy);
        var forwardingKnown = forwardTable || legacy;

        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mediaRoot));
        var unmatched = new Tally();
        var byMonth = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var byType = new SortedDictionary<(long Type, string Kind), int>();
        int noType = 0, mediaWithoutRecord = 0;

        var rows = new List<NewerMessageRow>(fresh.Count);
        var rowByID = new Dictionary<long, int>();

        foreach (var m in fresh)
        {
            var local = m.Local!.Value;
            string kind;
            string? placeholder = null, text = null, caption = null;
            NewerAttachmentRow? attachment = null;

            if (m.Type == NewerKinds.SystemType)
            {
                kind = NewerMessagesDatabase.KindSystem;
                text = NewerKinds.SystemWording;
            }
            else if (m.Type is { } t && NewerKinds.PlaceholderTypes.TryGetValue(t, out var known))
            {
                kind = NewerMessagesDatabase.KindPlaceholder;
                placeholder = known;
            }
            else if (m.Type is { } media && NewerKinds.MediaTypes.Contains(media))
            {
                if (m.HasMedia)
                {
                    kind = NewerMessagesDatabase.KindUser;
                    caption = CaptionOf(m);
                    attachment = AttachmentOf(m, rootFull, unmatched);
                }
                else
                {
                    kind = NewerMessagesDatabase.KindPlaceholder;
                    placeholder = NewerKinds.Unknown;
                    mediaWithoutRecord++;
                }
            }
            else if (m.Type == NewerKinds.TextType && m.TextBearing && !m.HasMedia)
            {
                kind = NewerMessagesDatabase.KindUser;
                text = m.Text;
            }
            else
            {
                kind = NewerMessagesDatabase.KindPlaceholder;
                placeholder = NewerKinds.Unknown;
            }

            if (placeholder is not null)
            {
                text = NewerKinds.Wording(placeholder);
            }

            var label = kind == NewerMessagesDatabase.KindPlaceholder ? "placeholder " + placeholder
                : kind == NewerMessagesDatabase.KindSystem ? "system"
                : attachment is not null ? "media" : "text";

            if (m.Type is { } typeNumber)
            {
                byType[(typeNumber, label)] = byType.GetValueOrDefault((typeNumber, label)) + 1;
            }
            else
            {
                noType++;
            }

            var month = local.ToString("yyyy'-'MM", CultureInfo.InvariantCulture);
            byMonth[month] = byMonth.GetValueOrDefault(month) + 1;

            var isForwarded = kind == NewerMessagesDatabase.KindSystem || !forwardingKnown ? (bool?)null : forwarded.Forwarded.Contains(m.ID);

            rowByID[m.ID] = rows.Count;
            rows.Add(new NewerMessageRow
            {
                KeyID = m.Key!,
                MsgstoreRowID = m.ID,
                SortID = m.OrderKey,
                FromMe = m.FromMe,
                SenderParticipantID = kind == NewerMessagesDatabase.KindSystem ? null : m.FromMe ? p.LocalParticipantID : other,
                TimestampUtcMs = m.Timestamp!.Value,
                DateTimeLocal = NewerMessagesTimeZone.Format(local),
                MessageType = m.Type,
                Kind = kind,
                PlaceholderKind = placeholder,
                Text = text,
                Caption = caption,
                IsForwarded = isForwarded,
                ForwardScore = isForwarded == true ? forwarded.Score(m.ID) : null,
                Attachment = attachment,
            });
        }

        // Low-quality copy links (association type 12), set on this run's new rows only.
        var (links, linksRefused) = Links(msgstore, p.ChatID, all, rows, rowByID, stored);

        for (var i = 0; i < rows.Count; i++)
        {
            if (links.TryGetValue(rows[i].MsgstoreRowID, out var fullKey))
            {
                var r = rows[i];
                rows[i] = Copy(r, new NewerAttachmentRow
                {
                    FileName = r.Attachment!.FileName,
                    FileSizeBytes = r.Attachment.FileSizeBytes,
                    MimeType = r.Attachment.MimeType,
                    FileHash = r.Attachment.FileHash,
                    RelativePath = r.Attachment.RelativePath,
                    State = r.Attachment.State,
                    Sha256 = r.Attachment.Sha256,
                    LowQualityCopyOfKeyID = fullKey,
                });
            }
        }

        // Earlier imported messages that changed on the phone since (D6: the archived version is kept; counted).
        int edited = 0, deleted = 0, gone = 0;

        if (stored is not null)
        {
            var byKey = all.Where(m => m.RawKey is not null).GroupBy(m => m.RawKey!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            foreach (var s in stored.Messages)
            {
                if (!byKey.TryGetValue(s.KeyID, out var now))
                {
                    gone++;
                }
                else if (now.Type == 15 && s.PlaceholderKind != NewerKinds.Deleted)
                {
                    deleted++;
                }
                else if (s.Kind == NewerMessagesDatabase.KindUser
                         && (s.Attachment is null
                             ? !string.Equals(s.Text, now.Text, StringComparison.Ordinal)
                             : !string.Equals(s.Caption, CaptionOf(now), StringComparison.Ordinal)))
                {
                    edited++;
                }
            }
        }

        var newIDs = rows.Select(r => r.MsgstoreRowID).ToHashSet();
        var addOns = CountChildren(msgstore, "message_add_on", "parent_message_row_id", newIDs);
        var quoted = CountChildren(msgstore, "message_quoted", "message_row_id", newIDs);

        var schemaSha = SchemaSha256(msgstore);
        var userVersion = Scalar(msgstore, "PRAGMA user_version;");
        var rootSha = Manifest.Sha256Hex(Encoding.UTF8.GetBytes(rootFull.ToUpperInvariant()));

        var computation = new NewerComputation
        {
            Parameters = p,
            OtherParticipantID = other,
            FirstRun = firstRun,
            PreviousRuns = stored?.Runs.Count ?? 0,
            HandOffKeyID = handOff.Key,
            LastDayWorkspaceMessages = lastDayCount,
            EarlierDaysCompared = earlierDays,
            ComparedText = comparedText,
            ComparedMedia = comparedMedia,
            ComparedDirectionOnly = directionOnly,
            Rows = rows,
            ByMonth = byMonth,
            ByType = byType,
            NoType = noType,
            Unmatched = unmatched,
            MediaTypeWithoutRecord = mediaWithoutRecord,
            Links = links.Count,
            LinksRefused = linksRefused,
            AddOns = addOns,
            QuotedReplies = quoted,
            ForwardingKnown = forwardingKnown,
            EditedSinceImport = edited,
            DeletedSinceImport = deleted,
            GoneFromMsgstore = gone,
            NfcFailures = TextProber.NfcFailures,
            MsgstoreSizeBytes = msgstoreSize,
            MsgstoreUserVersion = userVersion,
            MsgstoreSchemaSha256 = schemaSha,
            MediaRootSha256 = rootSha,
        };

        computation.ManifestBytes = ManifestBytes(computation);

        return computation;
    }

    private static bool After(MsRow m, MsRow handOff) =>
        m.OrderKey > handOff.OrderKey || (m.OrderKey == handOff.OrderKey && m.ID > handOff.ID);

    private static NewerMessageRow Copy(NewerMessageRow r, NewerAttachmentRow attachment) => new()
    {
        KeyID = r.KeyID,
        MsgstoreRowID = r.MsgstoreRowID,
        SortID = r.SortID,
        FromMe = r.FromMe,
        SenderParticipantID = r.SenderParticipantID,
        TimestampUtcMs = r.TimestampUtcMs,
        DateTimeLocal = r.DateTimeLocal,
        MessageType = r.MessageType,
        Kind = r.Kind,
        PlaceholderKind = r.PlaceholderKind,
        Text = r.Text,
        Caption = r.Caption,
        IsForwarded = r.IsForwarded,
        ForwardScore = r.ForwardScore,
        Attachment = attachment,
    };

    /// <summary>The caption rule of <c>caption-facts</c>: the text column when it holds one, else media_caption; exactly as stored.</summary>
    private static string? CaptionOf(MsRow m) =>
        m.TextBearing ? m.Text
        : m.MediaCaption is { } c && TextProber.Normalise(c).Length > 0 ? c
        : null;

    /// <summary>D4: Restored only when the file at file_path under the root has the SHA-256 msgstore records; else Unmatched.</summary>
    private static NewerAttachmentRow AttachmentOf(MsRow m, string rootFull, Tally unmatched)
    {
        var relative = UsableRelativePath(m.FilePath);
        var fileName = relative is not null ? Pairing.LastSegment(relative)
            : m.FilePath is { Length: > 0 } raw ? Pairing.LastSegment(raw.Replace('\\', '/'))
            : m.MediaName is { Length: > 0 } name ? Pairing.LastSegment(name.Replace('\\', '/'))
            : null;

        if (fileName is { Length: 0 } || fileName?.Contains('\\') == true)
        {
            fileName = null;
        }

        var format = Pairing.ClassifyHash(m.FileHash, out var hex);
        string? sha = null;
        string reason;

        if (relative is null)
        {
            reason = NoPath;
        }
        else if (format == HashFormat.Empty)
        {
            reason = HashEmpty;
        }
        else if (format != HashFormat.Base64Sha256)
        {
            reason = HashOther;
        }
        else
        {
            var full = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));

            if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                reason = NoFile;
            }
            else
            {
                using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                var actual = Convert.ToHexString(SHA256.HashData(stream));

                if (string.Equals(actual, hex, StringComparison.OrdinalIgnoreCase))
                {
                    sha = actual.ToLowerInvariant();
                    reason = "";
                }
                else
                {
                    reason = HashDiffers;
                }
            }
        }

        if (sha is null)
        {
            unmatched.Add(reason);
        }

        return new NewerAttachmentRow
        {
            FileName = fileName,
            FileSizeBytes = m.FileSize,
            MimeType = m.Mime,
            FileHash = m.FileHash switch
            {
                null => null,
                string s => s,
                byte[] b => Convert.ToBase64String(b),
                var o => Convert.ToString(o, CultureInfo.InvariantCulture),
            },
            RelativePath = relative,
            State = sha is null ? NewerMessagesDatabase.StateUnmatched : NewerMessagesDatabase.StateRestored,
            Sha256 = sha,
        };
    }

    /// <summary>
    /// msgstore's file_path as a path relative to the media root, with forward slashes: null when it is empty,
    /// rooted, has a drive or scheme, or has an empty, <c>.</c> or <c>..</c> segment.
    /// </summary>
    internal static string? UsableRelativePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var path = filePath.Replace('\\', '/');

        if (path.StartsWith('/') || path.Contains(':') || path.Any(char.IsControl))
        {
            return null;
        }

        var segments = path.Split('/');

        return segments.Any(s => s.Length == 0 || s == "." || s == "..") ? null : path;
    }

    private static (Dictionary<long, string> Links, Tally Refused) Links(
        SqliteConnection msgstore, long chat, List<MsRow> all, List<NewerMessageRow> rows, Dictionary<long, int> rowByID,
        NewerMessagesContents? stored)
    {
        var refused = new Tally();
        var result = new Dictionary<long, string>();

        if (!TextPairing.HasColumns(msgstore, "message_association", "parent_message_row_id", "child_message_row_id", "association_type"))
        {
            return (result, refused);
        }

        var msByID = all.ToDictionary(m => m.ID);
        var storedByKey = stored?.Messages.Where(m => m.Attachment is not null).ToDictionary(m => m.KeyID, StringComparer.Ordinal)
                          ?? new Dictionary<string, NewerMessageRow>(StringComparer.Ordinal);
        var candidates = new List<(long Parent, long Child)>();

        using (var command = msgstore.CreateCommand())
        {
            command.CommandText =
                """
                SELECT a.parent_message_row_id, a.child_message_row_id
                FROM message_association AS a
                WHERE a.association_type = $type
                  AND (a.parent_message_row_id IN (SELECT _id FROM message WHERE chat_row_id = $chat)
                       OR a.child_message_row_id IN (SELECT _id FROM message WHERE chat_row_id = $chat))
                ORDER BY a.parent_message_row_id, a.child_message_row_id;
                """;
            command.Parameters.AddWithValue("$type", FactsPlanner.LowQualityAssociationType);
            command.Parameters.AddWithValue("$chat", chat);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.GetValue(0) is not long parent
                    || reader.GetValue(1) is not long child || parent == child)
                {
                    continue;
                }

                NewerMessageRow? NewAttachment(long id) =>
                    rowByID.TryGetValue(id, out var i) && rows[i].Attachment is not null ? rows[i] : null;

                NewerMessageRow? StoredAttachment(long id) =>
                    msByID.TryGetValue(id, out var m) && m.RawKey is { } key && storedByKey.TryGetValue(key, out var s) ? s : null;

                var parentNew = NewAttachment(parent);
                var childNew = NewAttachment(child);

                if (parentNew is null && childNew is null)
                {
                    continue; // Not this run's business.
                }

                if (parentNew is null)
                {
                    refused.Add(StoredAttachment(parent) is not null ? LinkEarlier : LinkSide);
                    continue;
                }

                var childStored = childNew is null ? StoredAttachment(child) : null;
                var full = childNew ?? childStored;

                if (full is null)
                {
                    refused.Add(LinkSide);
                    continue;
                }

                var parentMs = msByID[parent];
                var childMinute = childNew is not null ? msByID[child].Minute : MinuteOf(full.TimestampUtcMs);

                if (parentNew.FromMe != full.FromMe)
                {
                    refused.Add(LinkDirection);
                }
                else if (parentMs.Minute is null || parentMs.Minute != childMinute)
                {
                    refused.Add(LinkMinute);
                }
                else if (parentNew.Attachment!.FileSizeBytes is not { } small || full.Attachment!.FileSizeBytes is not { } big || small >= big)
                {
                    refused.Add(LinkSize);
                }
                else
                {
                    candidates.Add((parent, child));
                }
            }
        }

        var targets = candidates.GroupBy(c => c.Parent).ToDictionary(g => g.Key, g => g.Select(c => c.Child).Distinct().ToList());
        var parents = candidates.Select(c => c.Parent).ToHashSet();
        var storedCopies = stored?.Messages.Where(m => m.Attachment?.LowQualityCopyOfAttachmentID is not null)
            .Select(m => m.KeyID).ToHashSet(StringComparer.Ordinal) ?? [];

        foreach (var (parent, children) in targets)
        {
            if (children.Count > 1)
            {
                refused.Add(LinkTwoTargets, children.Count);
                continue;
            }

            var child = children[0];
            var childKey = msByID[child].RawKey;

            if (parents.Contains(child) || targets.ContainsKey(child) || (childKey is not null && storedCopies.Contains(childKey)))
            {
                refused.Add(LinkChain);
                continue;
            }

            result[parent] = childKey!;
        }

        return (result, refused);
    }

    private static DateTime? MinuteOf(long utcMs) =>
        NewerMessagesTimeZone.ToLocal(utcMs) is { } t ? new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0) : null;

    /// <summary>Rows of <paramref name="table"/> whose <paramref name="column"/> names a new message; null when the table is absent.</summary>
    private static int? CountChildren(SqliteConnection msgstore, string table, string column, HashSet<long> ids)
    {
        if (!TextPairing.HasColumns(msgstore, table, column))
        {
            return null;
        }

        var count = 0;

        using var command = msgstore.CreateCommand();
        command.CommandText = $"SELECT \"{column}\" FROM \"{table}\";";

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (!reader.IsDBNull(0) && reader.GetValue(0) is long id && ids.Contains(id))
            {
                count++;
            }
        }

        return count;
    }

    private static List<MsRow> ReadMsgstore(SqliteConnection msgstore, MsgstoreSchema schema, long chat, string textColumn)
    {
        var sortID = schema.HasColumn("message", "sort_id") ? "m.sort_id" : "NULL";
        var hash = schema.HasColumn("message_media", "file_hash") ? "mm.file_hash" : "NULL";
        var mime = schema.HasColumn("message_media", "mime_type") ? "mm.mime_type" : "NULL";
        var mediaName = schema.HasColumn("message_media", "media_name") ? "mm.media_name" : "NULL";

        (string Table, string Key)? place = null;

        foreach (var candidate in CaptionFactsPlanner.MediaCaptionPlaces)
        {
            if (TextPairing.HasColumns(msgstore, candidate.Table, candidate.Key, "media_caption"))
            {
                place = candidate;
                break;
            }
        }

        // Table and column names come only from fixed lists, never from input.
        var mediaCaption = place is not { } pl ? "NULL"
            : pl.Table == "message" ? "m.media_caption"
            : $"(SELECT x.media_caption FROM \"{pl.Table}\" AS x WHERE x.\"{pl.Key}\" = m._id LIMIT 1)";

        using var command = msgstore.CreateCommand();
        command.CommandText =
            $"""
            SELECT m._id, m.key_id, m.from_me, m.timestamp, m.message_type, {sortID}, m."{textColumn}",
                   mm.message_row_id, mm.file_path, mm.file_size, {hash}, {mime}, {mediaName}, {mediaCaption}
            FROM message AS m
            LEFT JOIN (SELECT * FROM message_media GROUP BY message_row_id) AS mm ON mm.message_row_id = m._id
            WHERE m.chat_row_id = $chat;
            """;
        command.Parameters.AddWithValue("$chat", chat);

        var rows = new List<MsRow>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.GetValue(0) is not long id)
            {
                continue;
            }

            var raw = reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture);
            long? timestamp = !reader.IsDBNull(3) && reader.GetValue(3) is long ts ? ts : null;
            var text = reader.IsDBNull(6) ? null : reader.GetValue(6) as string;

            rows.Add(new MsRow
            {
                ID = id,
                RawKey = raw,
                Key = Pairing.UsableKeyID(reader.IsDBNull(1) ? null : reader.GetValue(1)),
                FromMe = !reader.IsDBNull(2) && reader.GetValue(2) is long fromMe && fromMe != 0,
                Timestamp = timestamp,
                Local = timestamp is > 0 ? NewerMessagesTimeZone.ToLocal(timestamp.Value) : null,
                Type = !reader.IsDBNull(4) && reader.GetValue(4) is long type ? type : null,
                OrderKey = !reader.IsDBNull(5) && reader.GetValue(5) is long sort ? sort : id,
                Text = text,
                Norm = text is null ? null : TextProber.Normalise(text),
                HasMedia = !reader.IsDBNull(7),
                FilePath = reader.IsDBNull(8) ? null : Convert.ToString(reader.GetValue(8), CultureInfo.InvariantCulture),
                FileSize = !reader.IsDBNull(9) && reader.GetValue(9) is long size ? size : null,
                FileHash = reader.IsDBNull(10) ? null : reader.GetValue(10),
                Mime = reader.IsDBNull(11) ? null : Convert.ToString(reader.GetValue(11), CultureInfo.InvariantCulture),
                MediaName = reader.IsDBNull(12) ? null : Convert.ToString(reader.GetValue(12), CultureInfo.InvariantCulture),
                MediaCaption = reader.IsDBNull(13) ? null : reader.GetValue(13) as string,
            });
        }

        rows.Sort((a, b) => a.OrderKey != b.OrderKey ? a.OrderKey.CompareTo(b.OrderKey) : a.ID.CompareTo(b.ID));

        return rows;
    }

    private static List<WsRow> ReadWorkspace(SqliteConnection workspace, NewerParameters p)
    {
        var result = new List<WsRow>();

        using var command = workspace.CreateCommand();
        command.CommandText =
            """
            SELECT m.MessageDateTimeLocal, m.SenderParticipantID, m.MessageType, m.MessageContent,
                   EXISTS (SELECT 1 FROM Attachment AS a WHERE a.MessageID = m.MessageID)
            FROM Message AS m
            WHERE m.ConversationID = $id
            ORDER BY m.SequenceNumber, m.MessageID;
            """;
        command.Parameters.AddWithValue("$id", p.ConversationID);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (Pairing.ParseMinute(reader.GetString(0)) is not { } local)
            {
                continue;
            }

            var content = reader.GetString(3);
            var kind = TextProber.Classify(reader.GetString(2), reader.GetInt64(4) != 0, content);
            var norm = kind == TextProber.WsKind.Text ? TextProber.Normalise(content) : "";

            result.Add(new WsRow(
                DateOnly.FromDateTime(local),
                !reader.IsDBNull(1) && reader.GetInt64(1) == p.LocalParticipantID,
                kind,
                norm,
                TextProber.WithoutEditedMarker(norm)));
        }

        return result;
    }

    /// <summary>SHA-256 of every table's name and column names, sorted: the msgstore's schema fingerprint.</summary>
    private static string SchemaSha256(SqliteConnection msgstore)
    {
        var lines = new List<string>();
        var tables = new List<string>();

        using (var command = msgstore.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (var table in tables)
        {
            using var command = msgstore.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info($table);";
            command.Parameters.AddWithValue("$table", table);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                lines.Add(table + "." + reader.GetString(0));
            }
        }

        lines.Sort(StringComparer.Ordinal);

        return Manifest.Sha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static List<long> Ids(SqliteConnection connection, string sql)
    {
        var ids = new List<long>();

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    // -------------------------------------------------------------------------------------------
    // Manifest: identifiers and SHA-256 digests only, never a text, caption, file name or path.
    // -------------------------------------------------------------------------------------------

    private static string Digest(string? text) => text is null ? "-" : Manifest.Sha256Hex(Encoding.UTF8.GetBytes(text));

    private static string Or(long? value) => value is { } v ? Text.Id(v) : "-";

    internal static byte[] ManifestBytes(NewerComputation c)
    {
        var text = new StringBuilder();

        void Line(string line) => text.Append(line).Append('\n');

        Line($"{ManifestMagic}\t{ManifestVersion}");
        Line("# PRIVATE: real identifiers. Keep outside the repository. Any edit invalidates its SHA-256.");
        Line($"conversation\t{Text.Id(c.Parameters.ConversationID)}");
        Line($"chat\t{Text.Id(c.Parameters.ChatID)}");
        Line($"local-participant\t{Text.Id(c.Parameters.LocalParticipantID)}");
        Line($"other-participant\t{Text.Id(c.OtherParticipantID)}");
        Line($"time-zone\t{NewerMessagesTimeZone.Name}");
        Line($"msgstore-size\t{Text.Id(c.MsgstoreSizeBytes)}");
        Line($"msgstore-user-version\t{Text.Id(c.MsgstoreUserVersion)}");
        Line($"msgstore-schema-sha256\t{c.MsgstoreSchemaSha256}");
        Line($"media-root-sha256\t{c.MediaRootSha256}");
        Line($"hand-off\t{(c.FirstRun ? NewerMessagesDatabase.HandOffWorkspace : NewerMessagesDatabase.HandOffPreviousRun)}");
        Line($"hand-off-key\t{c.HandOffKeyID}");
        Line($"previous-runs\t{c.PreviousRuns.ToString(CultureInfo.InvariantCulture)}");
        Line($"rows\t{c.Rows.Count.ToString(CultureInfo.InvariantCulture)}");
        Line(ManifestHeader);

        foreach (var r in c.Rows)
        {
            var a = r.Attachment;

            Line(string.Join('\t',
                Text.Id(r.MsgstoreRowID), r.KeyID, Text.Id(r.SortID), r.FromMe ? "1" : "0", Text.Id(r.TimestampUtcMs),
                Or(r.MessageType), r.Kind, r.PlaceholderKind ?? "-", Digest(r.Kind == NewerMessagesDatabase.KindUser ? r.Text : null),
                Digest(r.Caption), r.IsForwarded is { } f ? (f ? "1" : "0") : "-", Or(r.ForwardScore),
                a?.State ?? "-", a?.Sha256 ?? "-", Or(a?.FileSizeBytes), a?.LowQualityCopyOfKeyID ?? "-"));
        }

        return new UTF8Encoding(false).GetBytes(text.ToString());
    }

    /// <summary>The parameters a manifest was planned under, after its header checks.</summary>
    /// <exception cref="FormatException">Not a newer-messages manifest.</exception>
    internal static NewerParameters ParseParameters(byte[] bytes)
    {
        string content;

        try
        {
            content = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new FormatException("The manifest is not valid UTF-8.");
        }

        var lines = content.Split('\n');

        if (lines.Length < 17 || lines[0] != $"{ManifestMagic}\t{ManifestVersion}" || !lines[1].StartsWith('#'))
        {
            throw new FormatException("The file is not a newer-messages manifest.");
        }

        long Value(int index, string key)
        {
            var parts = lines[index].Split('\t');

            return parts.Length == 2 && parts[0] == key
                   && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new FormatException($"The manifest's '{key}' line is malformed.");
        }

        return new NewerParameters(Value(2, "conversation"), Value(3, "chat"), Value(4, "local-participant"));
    }

    /// <summary>True when the file at <paramref name="path"/> starts like a newer-messages manifest.</summary>
    internal static bool IsManifest(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        var bytes = File.ReadAllBytes(path);

        return Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 128)).StartsWith(ManifestMagic + "\t", StringComparison.Ordinal);
    }
}
