using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Chatcheology.Core.Models;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using MsMessage = MsgstoreImport.TextPairing.MsMessage;
using Pass = MsgstoreImport.TextPairing.Pass;
using WsText = MsgstoreImport.TextPairing.WsText;

namespace MsgstoreImport;

/// <summary>
/// <c>probe-text</c> (CH-22): how well the workspace's text messages pair with msgstore messages, so a
/// forwarded mark could later be shown on a text message. Read-only on both databases; stdout only.
/// </summary>
/// <remarks>
/// <para>Text is read from both databases and compared in memory only. The output is counts, column
/// names and message_type values: never a message text, participant name, file name, key_id, path or
/// row id.</para>
/// <para>The pairing rule is a candidate rule, measured, not adopted: same local minute, same direction
/// and identical text after <see cref="Normalise"/>. A workspace message pairs uniquely when it has
/// exactly one msgstore candidate and that candidate has exactly one workspace candidate.</para>
/// </remarks>
internal static class TextProber
{
    /// <summary>The text columns looked for on <c>message</c>, in order: current schema, then older.</summary>
    internal static readonly string[] TextColumns = ["text_data", "data"];

    /// <summary>What the Android export appends to an edited message.</summary>
    internal const string EditedMarker = "<This message was edited>";

    /// <exception cref="RefusalException">Nothing was read to completion.</exception>
    internal static string Run(Options options)
    {
        var p = options.Parameters!;

        Inputs.RequireMsgstore(options.MsgstorePath!);
        Inputs.RequireWorkspace(options.WorkspacePath);

        var before = FileStamp.Of(options.MsgstorePath!);
        string text;
        NfcFailures = 0;

        try
        {
            using var msgstore = Msgstore.OpenReadOnly(options.MsgstorePath!);
            var schema = MsgstoreSchema.Read(msgstore);

            if (schema.Refusal() is { } schemaRefusal)
            {
                throw new RefusalException(schemaRefusal);
            }

            using var workspace = WorkspaceDatabase.OpenReadOnlyConnection(options.WorkspacePath);

            Inputs.RequireSchemaVersion(workspace);
            Inputs.RequireKnown(workspace, msgstore, p);

            var pairing = new Pairing(workspace, msgstore, schema, p);
            text = new Build(workspace, msgstore, pairing, p).Render();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new RefusalException("The workspace is busy: another program holds a lock on it. Nothing was written.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        if (FileStamp.Of(options.MsgstorePath!) != before)
        {
            throw new RefusalException("The msgstore's size or last-write time changed during the read.");
        }

        return text;
    }

    /// <summary>
    /// U+200E and U+200F removed (as the export parser removes them from workspace content), line
    /// endings to LF, Unicode NFC, then trimmed.
    /// </summary>
    internal static string Normalise(string text) =>
        Nfc(text.Replace("\u200E", "", StringComparison.Ordinal).Replace("\u200F", "", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')).Trim();

    /// <summary>Normalised text with a trailing export edited marker, and the whitespace before it, removed.</summary>
    internal static string WithoutEditedMarker(string normalised) =>
        normalised.EndsWith(EditedMarker, StringComparison.Ordinal)
            ? normalised[..^EditedMarker.Length].TrimEnd()
            : normalised;

    /// <summary>Unicode NFC.</summary>
    /// <remarks>
    /// The tool runs with <c>InvariantGlobalization</c>, under which <see cref="string.Normalize()"/>
    /// leaves non-ASCII text unchanged. On Windows the system's own <c>NormalizeString</c> is used
    /// instead; elsewhere the probe refuses rather than compare unnormalised text.
    /// </remarks>
    internal static string Nfc(string text)
    {
        if (text.All(char.IsAscii))
        {
            return text;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new RefusalException("Unicode normalisation needs Windows in this tool. Nothing was written.");
        }

        var size = NativeMethods.NormalizeString(NormalizationFormC, text, text.Length, null, 0);

        for (var attempt = 0; attempt < 5 && size > 0; attempt++)
        {
            var buffer = new char[size];
            var written = NativeMethods.NormalizeString(NormalizationFormC, text, text.Length, buffer, buffer.Length);

            if (written > 0)
            {
                return new string(buffer, 0, written);
            }

            if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
            {
                break;
            }

            size = -written;
        }

        // Invalid UTF-16 (for example a lone surrogate): compared as it is, and counted.
        NfcFailures++;
        return text;
    }

    /// <summary>Texts the system could not normalise in this run (compared as they are).</summary>
    /// <remarks>Per thread, so concurrent runs (tests run in parallel) do not count each other's.</remarks>
    internal static int NfcFailures { get => _nfcFailures; set => _nfcFailures = value; }

    [ThreadStatic]
    private static int _nfcFailures;

    private const int NormalizationFormC = 1;
    private const int ErrorInsufficientBuffer = 122;

    private static class NativeMethods
    {
        [DllImport("normaliz.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int NormalizeString(
            int normForm, string source, int sourceLength, [Out] char[]? destination, int destinationLength);
    }

    /// <summary>How a workspace message was classified.</summary>
    internal enum WsKind
    {
        System,
        Attachment,
        MediaWithCaption,
        Empty,
        Text,
    }

    internal static WsKind Classify(string messageType, bool hasAttachment, string content)
    {
        if (messageType != "User")
        {
            return WsKind.System;
        }

        if (hasAttachment || content == ParsedMessage.MediaPlaceholderContent)
        {
            return WsKind.Attachment;
        }

        if (content.StartsWith(ParsedMessage.MediaPlaceholderContent + ParsedMessage.LineSeparator, StringComparison.Ordinal))
        {
            return WsKind.MediaWithCaption;
        }

        return Normalise(content).Length == 0 ? WsKind.Empty : WsKind.Text;
    }

    private sealed class Build(SqliteConnection workspace, SqliteConnection msgstore, Pairing pairing, PairingParameters p)
    {
        private readonly StringBuilder _text = new();

        internal string Render()
        {
            var textColumn = TextPairing.TextColumn(msgstore);
            var (wsKinds, ws, noTime) = TextPairing.ReadWorkspace(workspace, p);
            var ms = TextPairing.ReadMsgstore(msgstore, pairing, textColumn);

            _text.AppendLine("# Msgstore import - probe-text (CH-22)");
            _text.AppendLine();
            _text.AppendLine("Read-only on both databases; nothing was written. Text is compared in memory only. Counts,");
            _text.AppendLine("column names and message_type values only: no message text, name, file name, key_id, path or");
            _text.AppendLine("row id.");
            _text.AppendLine();

            // 1. Inputs.
            _text.AppendLine("## 1. Messages");
            _text.AppendLine();
            _text.AppendLine(CultureInfo.InvariantCulture, $"msgstore text column used: message.{textColumn}");
            _text.AppendLine();
            _text.AppendLine("Workspace text message = MessageType 'User', no Attachment row, content not the media");
            _text.AppendLine("placeholder, not the placeholder followed by a caption, and not empty after normalisation.");
            _text.AppendLine("msgstore text-bearing = the text column holds text that is not empty after normalisation.");
            _text.AppendLine("Normalisation: U+200E/U+200F removed, line endings to LF, Unicode NFC, trim.");
            _text.AppendLine();
            _text.AppendLine("```text");
            _text.AppendLine(Text.Line("workspace messages in the conversation", wsKinds.Values.Sum()));
            _text.AppendLine(Text.Line("  system messages (excluded)", wsKinds.GetValueOrDefault(WsKind.System)));
            _text.AppendLine(Text.Line("  attachment messages (excluded)", wsKinds.GetValueOrDefault(WsKind.Attachment)));
            _text.AppendLine(Text.Line("  media placeholder with a caption (excluded)", wsKinds.GetValueOrDefault(WsKind.MediaWithCaption)));
            _text.AppendLine(Text.Line("  empty content (excluded)", wsKinds.GetValueOrDefault(WsKind.Empty)));
            _text.AppendLine(Text.Line("  text messages", wsKinds.GetValueOrDefault(WsKind.Text)));
            _text.AppendLine(Text.Line("    sent (local participant)", ws.Count(w => w.Outgoing)));
            _text.AppendLine(Text.Line("    received", ws.Count(w => !w.Outgoing)));
            _text.AppendLine(Text.Line("    time not readable (cannot pair)", noTime));
            _text.AppendLine(Text.Line("    ending in the export's edited marker", ws.Count(w => w.EditedMarker)));

            var textBearing = ms.Where(m => m.TextBearing).ToList();

            _text.AppendLine(Text.Line("msgstore messages in the chat", ms.Count));
            _text.AppendLine(Text.Line("  text-bearing", textBearing.Count));
            _text.AppendLine(Text.Line("    without a message_media row", textBearing.Count(m => !m.HasMedia)));
            _text.AppendLine(Text.Line("    with one (captions)", textBearing.Count(m => m.HasMedia)));
            _text.AppendLine(Text.Line("    from_me = 1 (sent)", textBearing.Count(m => m.FromMe)));
            _text.AppendLine(Text.Line("    from_me = 0 (received)", textBearing.Count(m => !m.FromMe)));
            _text.AppendLine(Text.Line("    time not readable", textBearing.Count(m => m.Minute is null)));
            _text.AppendLine("    without a media row, by message_type:");

            foreach (var group in textBearing.Where(m => !m.HasMedia).GroupBy(m => m.Type).OrderBy(g => g.Key ?? long.MinValue))
            {
                _text.AppendLine(Text.Line($"      {Show(group.Key)}", group.Count()));
            }

            _text.AppendLine(Text.Line("  non-text value in the text column (blob or number)", ms.Count(m => m.NonTextValue)));
            _text.AppendLine(Text.Line("texts the system could not normalise (compared as they are)", NfcFailures));
            _text.AppendLine("```");
            _text.AppendLine();

            // 2. Pairing.
            var exact = new Pass(ws, ms, w => w.Norm, m => m.Norm!, 0);
            var raw = new Pass(ws, ms, w => w.Raw, m => m.Raw!, 0);
            var wide = new Pass(ws, ms, w => w.Norm, m => m.Norm!, 1);

            var editRows = EditedMessages();

            _text.AppendLine("## 2. Pairing workspace text messages");
            _text.AppendLine();
            _text.AppendLine("Candidate = a text-bearing msgstore message (with or without a media row) in the same local");
            _text.AppendLine("minute, same direction, identical normalised text. Unique = one candidate, which has no other");
            _text.AppendLine("workspace candidate. Unmatched messages are split by what the msgstore holds in that minute.");
            _text.AppendLine();
            _text.AppendLine("```text");
            PairingCounts(ws, exact, "normalised text, same minute (the rule)");

            var unmatched = ws.Where(w => w.Minute is not null && exact.WsCandidates[w.Index].Count == 0).ToList();
            var sameMinuteDirection = MinuteIndex(ms, textOnly: true);
            var anyInMinute = ms.Where(m => m.Minute is not null).Select(m => m.Minute!.Value).ToHashSet();
            var oppositeExact = ms.Where(m => m.TextBearing && m.Minute is not null)
                .Select(m => (m.FromMe, m.Norm!, m.Minute!.Value)).ToHashSet();

            var differentText = unmatched.Where(w => sameMinuteDirection.ContainsKey((w.Outgoing, w.Minute!.Value))).ToList();
            var rest = unmatched.Except(differentText).ToList();
            var directionMismatch = rest.Where(w => oppositeExact.Contains((!w.Outgoing, w.Norm, w.Minute!.Value))).ToList();
            rest = rest.Except(directionMismatch).ToList();
            var otherOnly = rest.Where(w => anyInMinute.Contains(w.Minute!.Value)).ToList();

            _text.AppendLine("  unmatched, by what the msgstore has in that minute:");
            _text.AppendLine(Text.Line("    same direction text-bearing, different text", differentText.Count));
            _text.AppendLine(Text.Line("      of which ending in the export's edited marker", differentText.Count(w => w.EditedMarker)));
            _text.AppendLine(Text.Line("      would match with that marker removed (reported only)", differentText.Count(w =>
                w.EditedMarker && sameMinuteDirection[(w.Outgoing, w.Minute!.Value)].Any(m => m.Norm == WithoutEditedMarker(w.Norm)))));
            _text.AppendLine(Text.Line("    identical text, other direction only", directionMismatch.Count));
            _text.AppendLine(Text.Line("    other msgstore messages only (no text or other direction)", otherOnly.Count));
            _text.AppendLine(Text.Line("    no msgstore message in that minute at all", rest.Count - otherOnly.Count));
            _text.AppendLine();
            PairingCounts(ws, raw, "exact text without normalisation, same minute");
            _text.AppendLine(Text.Line("  unique by the rule but not without normalisation", ws.Count(w => exact.Unique(w) && !raw.Unique(w))));
            _text.AppendLine();
            _text.AppendLine("+-1 minute variant (reported only, not adopted):");
            PairingCounts(ws, wide, "normalised text, minute -1 .. +1");
            _text.AppendLine(Text.Line("  unique with +-1 but not by the rule (would be gained)", ws.Count(w => wide.Unique(w) && !exact.Unique(w))));
            _text.AppendLine(Text.Line("  unique by the rule but not with +-1 (would be lost)", ws.Count(w => exact.Unique(w) && !wide.Unique(w))));
            _text.AppendLine("```");
            _text.AppendLine();

            // 3. Forwarded.
            var (forwardSource, forwarded) = Forwarded();
            var forwardedNoMedia = ms.Where(m => forwarded.Contains(m.ID) && !m.HasMedia).ToList();

            _text.AppendLine("## 3. Forwarded messages without a message_media row");
            _text.AppendLine();

            if (forwardSource is null)
            {
                _text.AppendLine("Not present: no message_forwarded table with message_row_id and no message.forwarded column.");
                _text.AppendLine();
            }
            else
            {
                _text.AppendLine(CultureInfo.InvariantCulture, $"Forwarded = {forwardSource}. Paired from the msgstore side by the same rule.");
                _text.AppendLine();
                _text.AppendLine("```text");
                _text.AppendLine(Text.Line("forwarded messages of the chat", ms.Count(m => forwarded.Contains(m.ID))));
                _text.AppendLine(Text.Line("  without a message_media row", forwardedNoMedia.Count));
                _text.AppendLine(Text.Line("    from_me = 1 (sent)", forwardedNoMedia.Count(m => m.FromMe)));
                _text.AppendLine(Text.Line("    from_me = 0 (received)", forwardedNoMedia.Count(m => !m.FromMe)));
                _text.AppendLine("    by message_type (total, then paired uniquely):");

                foreach (var group in forwardedNoMedia.GroupBy(m => m.Type).OrderBy(g => g.Key ?? long.MinValue))
                {
                    _text.AppendLine(Text.Line($"      {Show(group.Key)}", group.Count()));
                    _text.AppendLine(Text.Line("        paired uniquely", group.Count(exact.Unique)));
                }

                MsSideCounts(forwardedNoMedia, exact, wide, "    ");
                _text.AppendLine("```");
                _text.AppendLine();
            }

            // 4. Edits.
            _text.AppendLine("## 4. Edits (message_edit_info)");
            _text.AppendLine();

            if (editRows is null)
            {
                _text.AppendLine("Not present: no message_edit_info table with message_row_id.");
                _text.AppendLine();
            }
            else
            {
                bool Edited(MsMessage m) => editRows.Contains(m.ID);
                _text.AppendLine("```text");
                _text.AppendLine(Text.Line("msgstore messages of the chat in message_edit_info", ms.Count(Edited)));
                MsSideCounts(ms.Where(Edited).ToList(), exact, null, "  ");
                _text.AppendLine(Text.Line("text-bearing msgstore messages not edited, paired uniquely", textBearing.Count(m => !Edited(m) && exact.Unique(m))));
                _text.AppendLine(Text.Line("workspace unmatched with a different-text candidate:", differentText.Count));
                _text.AppendLine(Text.Line("  a same-minute candidate is in message_edit_info",
                    differentText.Count(w => sameMinuteDirection[(w.Outgoing, w.Minute!.Value)].Any(Edited))));

                if (forwardSource is not null)
                {
                    _text.AppendLine(Text.Line("forwarded without a media row, in message_edit_info", forwardedNoMedia.Count(Edited)));
                }

                _text.AppendLine("```");
                _text.AppendLine();
            }

            // 5. Verdict.
            var unique = ws.Count(exact.Unique);
            var forwardedUnique = forwardedNoMedia.Count(exact.Unique);

            _text.AppendLine("## 5. Verdict");
            _text.AppendLine();
            _text.AppendLine(CultureInfo.InvariantCulture,
                $"{Text.Count(unique)} of {Text.Count(ws.Count)} workspace text messages ({Percent(unique, ws.Count)}) pair uniquely by minute, " +
                $"direction and normalised text; {Text.Count(forwardedUnique)} of {Text.Count(forwardedNoMedia.Count)} forwarded " +
                $"messages without a media row ({Percent(forwardedUnique, forwardedNoMedia.Count)}) do.");

            return _text.ToString();
        }

        private void PairingCounts(List<WsText> ws, Pass pass, string label)
        {
            var timed = ws.Where(w => w.Minute is not null).ToList();
            var unique = timed.Count(pass.Unique);
            var manyMs = timed.Count(w => pass.WsCandidates[w.Index].Count >= 2);
            var sharedMs = timed.Count(w => pass.WsCandidates[w.Index].Count == 1 && !pass.Unique(w));
            var unmatched = timed.Count(w => pass.WsCandidates[w.Index].Count == 0);

            _text.AppendLine(label + ":");
            _text.AppendLine(Text.Line("  matched uniquely", unique));
            _text.AppendLine(Text.Line("  ambiguous", manyMs + sharedMs));
            _text.AppendLine(Text.Line("    2+ msgstore candidates", manyMs));
            _text.AppendLine(Text.Line("    one candidate, shared with another workspace message", sharedMs));
            _text.AppendLine(Text.Line("  unmatched (no candidate)", unmatched));
            _text.AppendLine(Text.Line("  time not readable", ws.Count - timed.Count));
        }

        private void MsSideCounts(List<MsMessage> messages, Pass exact, Pass? wide, string indent)
        {
            var text = messages.Where(m => m.TextBearing).ToList();

            _text.AppendLine(Text.Line(indent + "no text (not text-bearing)", messages.Count - text.Count));
            _text.AppendLine(Text.Line(indent + "text-bearing", text.Count));
            _text.AppendLine(Text.Line(indent + "  paired uniquely to a workspace text message", text.Count(exact.Unique)));
            _text.AppendLine(Text.Line(indent + "  ambiguous", text.Count(m => exact.MsCandidates[m.Index].Count > 0 && !exact.Unique(m))));
            _text.AppendLine(Text.Line(indent + "  unmatched (no workspace candidate)", text.Count(m => exact.MsCandidates[m.Index].Count == 0)));

            if (wide is not null)
            {
                _text.AppendLine(Text.Line(indent + "  unique with +-1 minute but not by the rule (reported only)", text.Count(m => wide.Unique(m) && !exact.Unique(m))));
            }
        }

        private static Dictionary<(bool, DateTime), List<MsMessage>> MinuteIndex(List<MsMessage> ms, bool textOnly) =>
            ms.Where(m => m.Minute is not null && (!textOnly || m.TextBearing))
                .GroupBy(m => (m.FromMe, m.Minute!.Value))
                .ToDictionary(g => g.Key, g => g.ToList());

        /// <summary>The forwarded message ids of the chat, and where they came from; null source when neither exists.</summary>
        private (string? Source, HashSet<long> IDs) Forwarded()
        {
            var ids = new HashSet<long>();
            string sql;
            string source;

            if (TextPairing.HasColumns(msgstore, "message_forwarded", "message_row_id"))
            {
                source = "a message_forwarded row (message_row_id)";
                sql = """
                      SELECT f.message_row_id FROM message_forwarded AS f
                      INNER JOIN message AS m ON m._id = f.message_row_id
                      WHERE m.chat_row_id = $chat;
                      """;
            }
            else if (TextPairing.HasColumns(msgstore, "message", "forwarded"))
            {
                source = "a non-zero legacy message.forwarded";
                sql = "SELECT _id FROM message WHERE chat_row_id = $chat AND forwarded IS NOT NULL AND forwarded <> 0;";
            }
            else
            {
                return (null, ids);
            }

            using var command = msgstore.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$chat", p.ChatID);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                if (!reader.IsDBNull(0) && reader.GetValue(0) is long id)
                {
                    ids.Add(id);
                }
            }

            return (source, ids);
        }

        /// <summary>Message ids of the chat with a message_edit_info row, or null when the table is absent.</summary>
        private HashSet<long>? EditedMessages()
        {
            if (!TextPairing.HasColumns(msgstore, "message_edit_info", "message_row_id"))
            {
                return null;
            }

            var ids = new HashSet<long>();

            using var command = msgstore.CreateCommand();
            command.CommandText =
                """
                SELECT e.message_row_id FROM message_edit_info AS e
                INNER JOIN message AS m ON m._id = e.message_row_id
                WHERE m.chat_row_id = $chat;
                """;
            command.Parameters.AddWithValue("$chat", p.ChatID);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                if (!reader.IsDBNull(0) && reader.GetValue(0) is long id)
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        private static string Show(long? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "null";

        private static string Percent(int part, int whole) =>
            whole == 0 ? "n/a" : (100.0 * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }
}
