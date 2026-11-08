using Microsoft.Data.Sqlite;

namespace MsgstoreImport;

/// <summary>
/// The CH-22 text pairing pieces shared by <c>probe-text</c> and <c>plan --rule text-facts</c>: what a
/// workspace text message and a msgstore message are, how both are read, and one pairing pass.
/// </summary>
/// <remarks>
/// Text is read and compared in memory only. Nothing here prints or returns a text to a caller that
/// prints it; the callers report counts.
/// </remarks>
internal static class TextPairing
{
    /// <summary>One workspace text message (see <see cref="TextProber.Classify"/>).</summary>
    /// <param name="Norm">The normalised content.</param>
    /// <param name="Stripped">The normalised content with a trailing export edited marker removed.</param>
    internal sealed record WsText(
        int Index, long MessageID, DateTime? Minute, bool Outgoing, string Raw, string Norm, string Stripped, bool EditedMarker);

    /// <summary>One msgstore message of the chat. <see cref="Norm"/> is null when the text column holds no text.</summary>
    internal sealed record MsMessage(
        int Index, long ID, DateTime? Minute, bool FromMe, long? Type, bool HasMedia, string? Raw, string? Norm, bool NonTextValue)
    {
        internal bool TextBearing => Norm is { Length: > 0 };
    }

    /// <summary>One pairing pass: candidates both ways under a key function and a minute window.</summary>
    /// <remarks>
    /// A workspace message pairs uniquely when it has exactly one msgstore candidate and that candidate
    /// has exactly one workspace candidate. Only text-bearing msgstore messages with a readable time
    /// are candidates.
    /// </remarks>
    internal sealed class Pass
    {
        internal Pass(List<WsText> ws, List<MsMessage> ms, Func<WsText, string> wsKey, Func<MsMessage, string> msKey, int window)
        {
            var msIndex = new Dictionary<(bool, string, DateTime), List<MsMessage>>();
            var wsIndex = new Dictionary<(bool, string, DateTime), List<WsText>>();

            foreach (var m in ms.Where(m => m.TextBearing && m.Minute is not null))
            {
                var key = (m.FromMe, msKey(m), m.Minute!.Value);

                if (!msIndex.TryGetValue(key, out var list))
                {
                    msIndex[key] = list = [];
                }

                list.Add(m);
            }

            foreach (var w in ws.Where(w => w.Minute is not null))
            {
                var key = (w.Outgoing, wsKey(w), w.Minute!.Value);

                if (!wsIndex.TryGetValue(key, out var list))
                {
                    wsIndex[key] = list = [];
                }

                list.Add(w);
            }

            WsCandidates = new List<MsMessage>[ws.Count];
            MsCandidates = new List<WsText>[ms.Count];

            foreach (var w in ws)
            {
                WsCandidates[w.Index] = w.Minute is { } minute
                    ? [.. Window(minute, window).SelectMany(t => msIndex.GetValueOrDefault((w.Outgoing, wsKey(w), t)) ?? [])]
                    : [];
            }

            foreach (var m in ms)
            {
                MsCandidates[m.Index] = m.TextBearing && m.Minute is { } minute
                    ? [.. Window(minute, window).SelectMany(t => wsIndex.GetValueOrDefault((m.FromMe, msKey(m), t)) ?? [])]
                    : [];
            }
        }

        internal List<MsMessage>[] WsCandidates { get; }
        internal List<WsText>[] MsCandidates { get; }

        internal bool Unique(WsText w) =>
            WsCandidates[w.Index] is [var m] && MsCandidates[m.Index].Count == 1;

        internal bool Unique(MsMessage m) =>
            MsCandidates[m.Index] is [var w] && WsCandidates[w.Index].Count == 1;

        private static IEnumerable<DateTime> Window(DateTime minute, int window) =>
            Enumerable.Range(-window, 2 * window + 1).Select(i => minute.AddMinutes(i));
    }

    /// <summary>The msgstore's text column (current schema first), or a refusal when it has none.</summary>
    internal static string TextColumn(SqliteConnection msgstore) =>
        TextProber.TextColumns.FirstOrDefault(c => HasColumns(msgstore, "message", c))
        ?? throw new RefusalException(
            "The msgstore's message table has none of the text columns looked for (" +
            string.Join(", ", TextProber.TextColumns) + "), so text cannot be compared. Nothing was written.");

    /// <summary>Every workspace message of the conversation classified, and the text messages.</summary>
    internal static (Dictionary<TextProber.WsKind, int> Kinds, List<WsText> Texts, int NoTime) ReadWorkspace(
        SqliteConnection workspace, PairingParameters p)
    {
        var kinds = new Dictionary<TextProber.WsKind, int>();
        var texts = new List<WsText>();
        var noTime = 0;

        using var command = workspace.CreateCommand();
        command.CommandText =
            """
            SELECT m.MessageDateTimeLocal, m.SenderParticipantID, m.MessageType, m.MessageContent,
                   EXISTS (SELECT 1 FROM Attachment AS a WHERE a.MessageID = m.MessageID), m.MessageID
            FROM Message AS m
            WHERE m.ConversationID = $id
            ORDER BY m.SequenceNumber;
            """;
        command.Parameters.AddWithValue("$id", p.ConversationID);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var content = reader.GetString(3);
            var kind = TextProber.Classify(reader.GetString(2), reader.GetInt64(4) != 0, content);
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;

            if (kind != TextProber.WsKind.Text)
            {
                continue;
            }

            var minute = Pairing.ParseMinute(reader.GetString(0));

            if (minute is null)
            {
                noTime++;
            }

            var norm = TextProber.Normalise(content);

            texts.Add(new WsText(
                texts.Count,
                reader.GetInt64(5),
                minute,
                !reader.IsDBNull(1) && reader.GetInt64(1) == p.LocalParticipantID,
                content,
                norm,
                TextProber.WithoutEditedMarker(norm),
                norm.EndsWith(TextProber.EditedMarker, StringComparison.Ordinal)));
        }

        return (kinds, texts, noTime);
    }

    /// <summary>Every msgstore message of the chat, with its local minute and normalised text.</summary>
    internal static List<MsMessage> ReadMsgstore(SqliteConnection msgstore, Pairing pairing, string textColumn)
    {
        var result = new List<MsMessage>();

        using var command = msgstore.CreateCommand();
        command.CommandText =
            $"""
            SELECT m._id, m.from_me, m.timestamp, m.message_type, m."{textColumn}",
                   EXISTS (SELECT 1 FROM message_media AS mm WHERE mm.message_row_id = m._id)
            FROM message AS m
            WHERE m.chat_row_id = $chat
            ORDER BY m._id;
            """;
        command.Parameters.AddWithValue("$chat", pairing.Parameters.ChatID);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var local = pairing.Local(reader.IsDBNull(2) ? null : reader.GetInt64(2));
            var value = reader.IsDBNull(4) ? null : reader.GetValue(4);
            var raw = value as string;

            result.Add(new MsMessage(
                result.Count,
                reader.GetInt64(0),
                local is { } t ? new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0) : null,
                !reader.IsDBNull(1) && reader.GetInt64(1) != 0,
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.GetInt64(5) != 0,
                raw,
                raw is null ? null : TextProber.Normalise(raw),
                value is not null && raw is null));
        }

        return result;
    }

    internal static bool HasColumns(SqliteConnection connection, string table, params string[] columns)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", table);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            found.Add(reader.GetString(0));
        }

        return columns.All(found.Contains);
    }
}
