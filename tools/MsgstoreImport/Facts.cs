using System.Globalization;
using System.Text;
using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport;

/// <summary>One attachment's msgstore facts, as the facts manifest records them.</summary>
internal sealed record FactRow(long AttachmentID, long MessageID, bool IsForwarded, int? ForwardScore, long? LowQualityCopyOf)
{
    internal MsgstoreFactRow ToWriterRow() => new(AttachmentID, IsForwarded, ForwardScore, LowQualityCopyOf);

    internal bool SameFacts(AttachmentMsgstoreFact fact) =>
        fact.IsForwarded == IsForwarded && fact.ForwardScore == ForwardScore
        && fact.LowQualityCopyOfAttachmentID == LowQualityCopyOf;
}

/// <summary>
/// The CH-8 / CH-10 facts manifest. Same header lines as the review-missing manifest, with the rule
/// <c>facts</c> and its own five columns; <c>-</c> stands for no value. Tab-separated UTF-8 without a
/// BOM, LF endings, rows in AttachmentID order; the SHA-256 is over the bytes.
/// </summary>
internal sealed class FactsManifest
{
    internal const string Token = "facts";
    internal const string Header = "AttachmentID\tMessageID\tIsForwarded\tForwardScore\tLowQualityCopyOfAttachmentID";

    internal required PairingParameters Parameters { get; init; }
    internal required IReadOnlyList<FactRow> Rows { get; init; }

    /// <summary>True when the file at <paramref name="path"/> names the facts rule. Reads nothing else.</summary>
    internal static bool IsOne(string? path) => FactsManifestFormat.Names(path, Token);

    internal byte[] ToBytes()
    {
        static string Opt(long? value) => value is { } v ? Text.Id(v) : "-";

        return FactsManifestFormat.ToBytes(Token, Header, Parameters, Rows.Select(row =>
            $"{Text.Id(row.AttachmentID)}\t{Text.Id(row.MessageID)}\t{(row.IsForwarded ? "1" : "0")}\t" +
            $"{Opt(row.ForwardScore)}\t{Opt(row.LowQualityCopyOf)}"));
    }

    /// <exception cref="FormatException">The bytes are not a well-formed facts manifest.</exception>
    internal static FactsManifest Parse(byte[] bytes)
    {
        var (parameters, body) = FactsManifestFormat.Parse(bytes, Token, Header);

        static bool Id(string text, out long value) =>
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

        var rows = new List<FactRow>(body.Length);
        var seen = new HashSet<long>();

        foreach (var line in body)
        {
            var parts = line.Split('\t');

            if (parts.Length != 5 || !Id(parts[0], out var attachment) || !Id(parts[1], out var message)
                || parts[2] is not ("0" or "1"))
            {
                throw new FormatException("A manifest row is malformed.");
            }

            int? score = null;

            if (parts[3] != "-")
            {
                score = int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var s)
                    ? s
                    : throw new FormatException("A manifest row has a malformed ForwardScore.");
            }

            long? copyOf = null;

            if (parts[4] != "-")
            {
                copyOf = Id(parts[4], out var c) && c != attachment
                    ? c
                    : throw new FormatException("A manifest row has a malformed LowQualityCopyOfAttachmentID.");
            }

            if (!seen.Add(attachment))
            {
                throw new FormatException("The manifest names an attachment twice.");
            }

            rows.Add(new FactRow(attachment, message, parts[2] == "1", score, copyOf));
        }

        return new FactsManifest { Parameters = parameters, Rows = rows };
    }
}

/// <summary>
/// The header and framing the msgstore fact manifests share (CH-8 / CH-10 <c>facts</c>, CH-22
/// <c>text-facts</c>): the magic line, a privacy comment, the rule, the four pairing parameters, the
/// row count and a column header, then one tab-separated row per line. UTF-8 without a BOM, LF.
/// </summary>
internal static class FactsManifestFormat
{
    /// <summary>True when the file at <paramref name="path"/> names <paramref name="token"/>. Reads nothing else.</summary>
    internal static bool Names(string? path, string token)
    {
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        var bytes = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512));
        var lines = text.Split('\n');

        return lines.Length >= 3
               && lines[0] == $"{Manifest.Magic}\t{Manifest.DaySequenceVersion}"
               && lines[2] == $"rule\t{token}";
    }

    internal static byte[] ToBytes(string token, string header, PairingParameters parameters, IEnumerable<string> rows)
    {
        var body = rows.ToList();
        var text = new StringBuilder();

        void Line(string line) => text.Append(line).Append('\n');

        Line($"{Manifest.Magic}\t{Manifest.DaySequenceVersion}");
        Line("# PRIVATE: real identifiers. Keep outside the repository. Any edit invalidates its SHA-256.");
        Line($"rule\t{token}");
        Line($"conversation\t{Text.Id(parameters.ConversationID)}");
        Line($"chat\t{Text.Id(parameters.ChatID)}");
        Line($"utc-offset\t{Options.FormatOffset(parameters.UtcOffset)}");
        Line($"local-participant\t{Text.Id(parameters.LocalParticipantID)}");
        Line($"rows\t{body.Count.ToString(CultureInfo.InvariantCulture)}");
        Line(header);

        foreach (var row in body)
        {
            Line(row);
        }

        return new UTF8Encoding(false).GetBytes(text.ToString());
    }

    /// <summary>The parameters and the row lines, after every header check.</summary>
    /// <exception cref="FormatException">The bytes are not a well-formed manifest of this rule.</exception>
    internal static (PairingParameters Parameters, string[] Body) Parse(byte[] bytes, string token, string header)
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

        if (content.Contains('\r'))
        {
            throw new FormatException("The manifest has CR characters; it was not written by this tool.");
        }

        var lines = content.Split('\n');

        if (lines.Length < 10 || lines[^1].Length != 0)
        {
            throw new FormatException("The manifest is truncated.");
        }

        if (lines[0] != $"{Manifest.Magic}\t{Manifest.DaySequenceVersion}" || !lines[1].StartsWith('#')
            || lines[2] != $"rule\t{token}")
        {
            throw new FormatException($"The manifest's first lines are not a {token} manifest header.");
        }

        long Value(int index, string key)
        {
            var parts = lines[index].Split('\t');

            return parts.Length == 2 && parts[0] == key
                   && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new FormatException($"The manifest's '{key}' line is malformed.");
        }

        var conversation = Value(3, "conversation");
        var chat = Value(4, "chat");
        var offsetParts = lines[5].Split('\t');

        if (offsetParts.Length != 2 || offsetParts[0] != "utc-offset" || !Options.TryParseOffset(offsetParts[1], out var offset))
        {
            throw new FormatException("The manifest's 'utc-offset' line is malformed.");
        }

        var local = Value(6, "local-participant");
        var count = Value(7, "rows");

        if (lines[8] != header)
        {
            throw new FormatException("The manifest's column header is not the expected one.");
        }

        var body = lines[9..^1];

        if (body.Length != count)
        {
            throw new FormatException("The manifest's row count does not match its 'rows' line.");
        }

        return (new PairingParameters(conversation, chat, offset, local), body);
    }

    /// <summary>Reads the manifest, checks its SHA-256 and parses it; the batch tag comes from the digest.</summary>
    /// <exception cref="RefusalException">Missing, tampered or malformed. Nothing was opened.</exception>
    internal static (T Manifest, string Tag) Load<T>(string path, string expectedSha256, Func<byte[], T> parse)
    {
        if (!File.Exists(path))
        {
            throw new RefusalException("There is no file at the --manifest path.");
        }

        var bytes = File.ReadAllBytes(path);
        var actual = Manifest.Sha256Hex(bytes);

        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new RefusalException(
                "The manifest's SHA-256 does not match --manifest-sha256. The manifest has changed since " +
                "it was approved, or the wrong digest was given. Nothing was opened or written.");
        }

        try
        {
            return (parse(bytes), Manifest.Tag(actual));
        }
        catch (FormatException exception)
        {
            throw new RefusalException("The manifest is malformed: " + exception.Message + " Nothing was written.");
        }
    }
}

/// <summary>WhatsApp's forwarded status and forward score for the messages of one chat, as stored.</summary>
/// <param name="Forwarded">Message ids marked forwarded.</param>
/// <param name="Scores">Readable forward scores, by message id.</param>
/// <param name="Unreadable">Message ids whose score is not a non-negative integer, or is recorded twice differently.</param>
internal sealed record ForwardedFacts(HashSet<long> Forwarded, Dictionary<long, int?> Scores, HashSet<long> Unreadable)
{
    /// <summary>The score recorded for a forwarded message; null when not forwarded, absent or unreadable.</summary>
    internal int? Score(long message) =>
        Forwarded.Contains(message) && !Unreadable.Contains(message) ? Scores.GetValueOrDefault(message) : null;

    /// <summary>
    /// Reads <c>message_forwarded</c> (with <c>forward_score</c> when <paramref name="scoreColumn"/>)
    /// when <paramref name="forwardTable"/>, and a non-zero legacy <c>message.forwarded</c> when
    /// <paramref name="legacy"/>; both, when both are asked for.
    /// </summary>
    internal static ForwardedFacts Read(SqliteConnection msgstore, long chat, bool forwardTable, bool scoreColumn, bool legacy)
    {
        var forwarded = new HashSet<long>();
        var scores = new Dictionary<long, int?>();
        var unreadable = new HashSet<long>();

        if (forwardTable)
        {
            using var command = msgstore.CreateCommand();
            command.CommandText =
                $"""
                SELECT f.message_row_id, {(scoreColumn ? "f.forward_score" : "NULL")}
                FROM message_forwarded AS f
                INNER JOIN message AS m ON m._id = f.message_row_id
                WHERE m.chat_row_id = $chat;
                """;
            command.Parameters.AddWithValue("$chat", chat);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.GetValue(0) is not long id)
                {
                    continue;
                }

                forwarded.Add(id);

                if (reader.IsDBNull(1))
                {
                    continue;
                }

                if (reader.GetValue(1) is long n && n is >= 0 and <= int.MaxValue)
                {
                    if (scores.TryGetValue(id, out var existing) && existing != (int)n)
                    {
                        unreadable.Add(id);
                    }

                    scores[id] = (int)n;
                }
                else
                {
                    unreadable.Add(id);
                }
            }
        }

        if (legacy)
        {
            using var command = msgstore.CreateCommand();
            command.CommandText = "SELECT _id, forwarded FROM message WHERE chat_row_id = $chat;";
            command.Parameters.AddWithValue("$chat", chat);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                if (!reader.IsDBNull(1) && reader.GetValue(1) is long v && v != 0)
                {
                    forwarded.Add(reader.GetInt64(0));
                }
            }
        }

        return new ForwardedFacts(forwarded, scores, unreadable);
    }
}

/// <summary>
/// The attachment pairing the msgstore fact rules share (CH-8 / CH-10 <c>facts</c>, CH-71
/// <c>caption-facts</c>): each attachment of the conversation lined up with one msgstore media message
/// by the minute rule, or, for slots it left, by the CH-9 day-sequence rule.
/// </summary>
/// <remarks>
/// A slot the two rules pair with different records, a day-sequence slot whose record is more than
/// <see cref="DaySequence.MaxMinuteGap"/> minutes away, and every attachment of a record paired to two
/// attachments get no pair; each is counted under its reason in <see cref="NoRow"/>.
/// </remarks>
internal sealed class AttachmentPairs
{
    /// <summary>The minute rule's assessment of every attachment of the conversation.</summary>
    internal required SortedDictionary<long, Assessment> Minute { get; init; }

    /// <summary>The msgstore media records of the chat.</summary>
    internal required List<MediaRecord> Records { get; init; }

    /// <summary>The msgstore message each paired attachment lines up with.</summary>
    internal required Dictionary<long, long> MessageOf { get; init; }

    internal required int ByMinute { get; init; }
    internal required int ByDay { get; init; }

    /// <summary>Unpaired attachments, by reason (the reasons are <see cref="FactsPlanner"/>'s).</summary>
    internal required Tally NoRow { get; init; }

    internal static AttachmentPairs Compute(Pairing pairing)
    {
        var (_, records) = pairing.Load();
        var minute = pairing.Compute();
        var day = new DaySequence(pairing).Compute().ByAttachment;

        var noRow = new Tally();
        var messageOf = new Dictionary<long, long>();
        int byMinute = 0, byDay = 0;

        foreach (var (attachment, assessment) in minute)
        {
            var d = day.GetValueOrDefault(attachment);
            long? dayMessage = d is { MessageID: { } dm } && d.Eligibility is not (DayEligibility.NoTime or DayEligibility.DaySkipped or DayEligibility.MinuteGap)
                ? dm
                : null;

            if (assessment.MessageID is { } mm && dayMessage is { } dd && mm != dd)
            {
                noRow.Add(FactsPlanner.RulesDisagree);
            }
            else if (assessment.MessageID is { } m)
            {
                messageOf[attachment] = m;
                byMinute++;
            }
            else if (dayMessage is { } dmsg)
            {
                messageOf[attachment] = dmsg;
                byDay++;
            }
            else if (d is { Eligibility: DayEligibility.MinuteGap })
            {
                noRow.Add(FactsPlanner.DayMinuteGap);
            }
            else
            {
                noRow.Add(FactsPlanner.NotPaired);
            }
        }

        foreach (var group in messageOf.GroupBy(p => p.Value).Where(g => g.Count() > 1).ToList())
        {
            foreach (var (attachment, _) in group)
            {
                messageOf.Remove(attachment);
                noRow.Add(FactsPlanner.MessageTwice);

                if (minute[attachment].MessageID is not null)
                {
                    byMinute--;
                }
                else
                {
                    byDay--;
                }
            }
        }

        return new AttachmentPairs
        {
            Minute = minute,
            Records = records,
            MessageOf = messageOf,
            ByMinute = byMinute,
            ByDay = byDay,
            NoRow = noRow,
        };
    }
}

/// <summary>What the facts computation found. Counts, and the rows for the private manifest.</summary>
internal sealed class FactsComputation
{
    internal required int Attachments { get; init; }
    internal required int PairedByMinute { get; init; }
    internal required int PairedByDay { get; init; }
    internal required Tally NoRow { get; init; }
    internal required int LeftAlone { get; init; }
    internal required List<FactRow> Rows { get; init; }
    internal required bool ForwardTable { get; init; }
    internal required bool LegacyForwardColumn { get; init; }
    internal required bool AssociationTable { get; init; }
    internal required int ScoresUnreadable { get; init; }
    internal required int Type12Links { get; init; }
    internal required int OtherTypeLinks { get; init; }
    internal required Tally LinksRefused { get; init; }
}

/// <summary>
/// <c>plan --rule facts</c> (CH-8, CH-10): for every attachment paired by the minute or day-sequence
/// rule, WhatsApp's forwarded status, forward score and low-quality-copy link, copied from the msgstore.
/// </summary>
/// <remarks>
/// <para>No inference and no score of our own: <c>IsForwarded</c> is the presence of a
/// <c>message_forwarded</c> row (or a non-zero legacy <c>message.forwarded</c>); <c>ForwardScore</c> is
/// that row's value. A link is recorded only for a <c>message_association</c> type-12 link whose two
/// sides are paired attachments in the same local minute and direction, with the parent's file strictly
/// smaller: the parent (smaller) attachment becomes the low-quality copy of the child. Anything else
/// records no link. Missing is better than wrong.</para>
/// <para>A slot the day-sequence rule lines up but whose record is more than
/// <see cref="DaySequence.MaxMinuteGap"/> minutes away fails that rule's own pairing guard, so it gets
/// no row.</para>
/// </remarks>
internal static class FactsPlanner
{
    internal const long LowQualityAssociationType = 12;
    internal const int ForwardedManyTimes = 5;

    internal const string NotPaired = "not paired by the minute or day-sequence rule";
    internal const string DayMinuteGap = "day-sequence position only, record minute too far from slot";
    internal const string RulesDisagree = "minute and day-sequence rules name different records";
    internal const string MessageTwice = "msgstore record paired to two attachments";
    internal const string LeftAloneReason = "already has a msgstore fact from another batch (left alone)";

    internal const string LinkSideNotPaired = "a side is not a paired attachment";
    internal const string LinkMinute = "sides in different local minutes";
    internal const string LinkDirection = "sides in different directions";
    internal const string LinkSize = "parent file not strictly smaller (or a size unknown)";
    internal const string LinkCopyLeftAlone = "copy side already has a fact from another batch";
    internal const string LinkTwoTargets = "copy of two or more full copies (attachments)";
    internal const string LinkChain = "would form a chain (attachments)";

    /// <summary>Refuses unless the workspace is at the current version, with the migrate-first message for the version before it.</summary>
    internal static void RequireFactsSchema(string workspacePath) => WorkspaceMigrator.RequireCurrent(workspacePath);

    /// <summary>
    /// The facts for every paired attachment of the conversation, read-only on both databases. Facts
    /// already stored under <paramref name="ownBatch"/> are treated as absent, so apply can recompute the
    /// plan it is applying.
    /// </summary>
    internal static FactsComputation Compute(string msgstorePath, string workspacePath, PairingParameters parameters, string? ownBatch)
    {
        RequireFactsSchema(workspacePath);

        return Inputs.WithPairing(msgstorePath, workspacePath, parameters, pairing => Compute(pairing, ownBatch));
    }

    private static FactsComputation Compute(Pairing pairing, string? ownBatch)
    {
        var msgstore = pairing.MsgstoreConnection;
        var chat = pairing.Parameters.ChatID;

        var forwardTable = HasColumns(msgstore, "message_forwarded", "message_row_id");
        var scoreColumn = forwardTable && HasColumns(msgstore, "message_forwarded", "forward_score");
        var legacy = HasColumns(msgstore, "message", "forwarded");
        var association = HasColumns(msgstore, "message_association", "parent_message_row_id", "child_message_row_id", "association_type");

        if (!forwardTable && !legacy)
        {
            throw new RefusalException(
                "The msgstore has neither a message_forwarded table (with message_row_id) nor a message.forwarded " +
                "column, so forwarded status cannot be read. Nothing was written.");
        }

        // 1. Pairing: the minute rule, then the day-sequence rule for slots it left.
        var pairs = AttachmentPairs.Compute(pairing);
        var records = pairs.Records;
        var minute = pairs.Minute;
        var noRow = pairs.NoRow;
        var messageOf = pairs.MessageOf;
        int byMinute = pairs.ByMinute, byDay = pairs.ByDay;

        var attachmentOf = messageOf.ToDictionary(p => p.Value, p => p.Key);

        // 2. Facts already in the workspace (any batch but this one's own).
        var (stored, storedTargets) = ReadStored(pairing.Workspace, minute.Keys.ToList(), ownBatch);
        var leftAlone = messageOf.Keys.Where(stored.ContainsKey).ToHashSet();

        // 3. Forwarded.
        var forward = ForwardedFacts.Read(msgstore, chat, forwardTable, scoreColumn, legacy);
        var forwarded = forward.Forwarded;
        var unreadable = forward.Unreadable;

        // 4. Low-quality links.
        var linksRefused = new Tally();
        int type12 = 0, otherTypes = 0;
        var candidates = new HashSet<(long Copy, long Full)>();

        if (association)
        {
            var media = records.GroupBy(r => r.MessageID).ToDictionary(g => g.Key, g => g.First());
            var links = new HashSet<(long Parent, long Child)>();

            using (var command = msgstore.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT a.parent_message_row_id, a.child_message_row_id, a.association_type
                    FROM message_association AS a
                    WHERE a.parent_message_row_id IN (SELECT _id FROM message WHERE chat_row_id = $chat)
                       OR a.child_message_row_id IN (SELECT _id FROM message WHERE chat_row_id = $chat);
                    """;
                command.Parameters.AddWithValue("$chat", chat);

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    var isType12 = !reader.IsDBNull(2) && reader.GetValue(2) is long t && t == LowQualityAssociationType;

                    if (!isType12)
                    {
                        otherTypes++;
                        continue;
                    }

                    if (reader.IsDBNull(0) || reader.IsDBNull(1)
                        || reader.GetValue(0) is not long parent || reader.GetValue(1) is not long child)
                    {
                        type12++;
                        linksRefused.Add(LinkSideNotPaired);
                        continue;
                    }

                    // The same pair recorded twice is one link.
                    if (links.Add((parent, child)))
                    {
                        type12++;
                    }
                }
            }

            foreach (var (parent, child) in links)
            {
                if (!attachmentOf.TryGetValue(parent, out var copy) || !attachmentOf.TryGetValue(child, out var full)
                    || !media.TryGetValue(parent, out var p) || !media.TryGetValue(child, out var c))
                {
                    linksRefused.Add(LinkSideNotPaired);
                }
                else if (p.Minute is null || p.Minute != c.Minute)
                {
                    linksRefused.Add(LinkMinute);
                }
                else if (p.FromMe != c.FromMe)
                {
                    linksRefused.Add(LinkDirection);
                }
                else if (p.FileSize is not { } ps || c.FileSize is not { } cs || ps >= cs)
                {
                    linksRefused.Add(LinkSize);
                }
                else if (leftAlone.Contains(copy))
                {
                    linksRefused.Add(LinkCopyLeftAlone);
                }
                else
                {
                    candidates.Add((copy, full));
                }
            }
        }

        // One copy, two full copies: no link for that copy.
        var twoTargets = candidates.GroupBy(c => c.Copy).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        linksRefused.Add(LinkTwoTargets, twoTargets.Count);
        candidates.RemoveWhere(c => twoTargets.Contains(c.Copy));

        // A chain: an attachment that would be both a copy and a full copy, here or with stored facts.
        var copies = candidates.Select(c => c.Copy).Concat(stored.Where(s => s.Value.LowQualityCopyOfAttachmentID is not null).Select(s => s.Key)).ToHashSet();
        var targets = candidates.Select(c => c.Full).Concat(storedTargets).ToHashSet();
        copies.IntersectWith(targets);

        var chained = candidates.Where(c => copies.Contains(c.Copy) || copies.Contains(c.Full)).ToList();
        linksRefused.Add(LinkChain, chained.Select(c => c.Copy).Distinct().Count());
        candidates.ExceptWith(chained);

        var linkOf = candidates.ToDictionary(c => c.Copy, c => c.Full);

        // 5. Rows.
        var rows = messageOf
            .Where(p => !leftAlone.Contains(p.Key))
            .OrderBy(p => p.Key)
            .Select(p => new FactRow(
                p.Key, p.Value, forwarded.Contains(p.Value), forward.Score(p.Value),
                linkOf.TryGetValue(p.Key, out var full) ? full : null))
            .ToList();

        return new FactsComputation
        {
            Attachments = minute.Count,
            PairedByMinute = byMinute,
            PairedByDay = byDay,
            NoRow = noRow,
            LeftAlone = leftAlone.Count,
            Rows = rows,
            ForwardTable = forwardTable,
            LegacyForwardColumn = legacy,
            AssociationTable = association,
            ScoresUnreadable = rows.Count(r => unreadable.Contains(r.MessageID)),
            Type12Links = type12,
            OtherTypeLinks = otherTypes,
            LinksRefused = linksRefused,
        };
    }

    /// <summary>
    /// Stored facts for the conversation's attachments, and the attachments stored as full copies, with
    /// the rows of <paramref name="ownBatch"/> left out. Through the public reader only.
    /// </summary>
    private static (Dictionary<long, AttachmentMsgstoreFact> Facts, HashSet<long> Targets) ReadStored(
        SqliteConnection workspace, List<long> attachments, string? ownBatch)
    {
        var reader = new MsgstoreFactReader();
        var set = reader.Read(workspace, attachments);

        bool Other(AttachmentMsgstoreFact f) => !string.Equals(f.SourceBatch, ownBatch, StringComparison.Ordinal);

        var facts = set.Facts.Where(f => Other(f.Value)).ToDictionary(f => f.Key, f => f.Value);

        // A full copy's copies may lie outside the conversation; read their batches too.
        var copyIDs = set.LowQualityCopiesByFullCopy.Values.SelectMany(v => v).Distinct().ToList();
        var copyFacts = copyIDs.Count == 0 ? MsgstoreFactSet.Empty.Facts : reader.Read(workspace, copyIDs).Facts;

        var targets = set.LowQualityCopiesByFullCopy
            .Where(t => t.Value.Any(copy => copyFacts.TryGetValue(copy, out var f) && Other(f)))
            .Select(t => t.Key)
            .ToHashSet();

        return (facts, targets);
    }

    private static bool HasColumns(SqliteConnection connection, string table, params string[] columns)
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

    internal sealed class FactsPlanResult
    {
        internal required FactsComputation Facts { get; init; }
        internal required string Sha256 { get; init; }
    }

    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static FactsPlanResult Plan(Options options)
    {
        if (Planner.ManifestOutRefusal(options.ManifestOut!) is { } refusal)
        {
            throw new RefusalException(refusal);
        }

        var parameters = options.Parameters!;
        var facts = Compute(options.MsgstorePath!, options.WorkspacePath, parameters, ownBatch: null);
        var bytes = new FactsManifest { Parameters = parameters, Rows = facts.Rows }.ToBytes();

        using (var stream = new FileStream(options.ManifestOut!, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
        }

        return new FactsPlanResult { Facts = facts, Sha256 = Manifest.Sha256Hex(bytes) };
    }

    internal static bool Reconciled(FactsComputation f) => f.Rows.Count + f.NoRow.Total + f.LeftAlone == f.Attachments;

    internal static string Report(FactsPlanResult result)
    {
        var f = result.Facts;
        var text = new StringBuilder();

        string Present(bool yes) => yes ? "present" : "absent";

        text.AppendLine("# Msgstore import - plan facts (CH-8, CH-10)");
        text.AppendLine();
        text.AppendLine("Read-only on both databases. Counts only; identifiers are in the private manifest.");
        text.AppendLine();
        text.AppendLine("```text");
        text.AppendLine($"{"msgstore message_forwarded table",-58}{Present(f.ForwardTable)}");
        text.AppendLine($"{"msgstore message.forwarded column (legacy)",-58}{Present(f.LegacyForwardColumn)}");
        text.AppendLine($"{"msgstore message_association table",-58}{Present(f.AssociationTable)}");
        text.AppendLine();
        text.AppendLine(Text.Line("attachments in the conversation", f.Attachments));
        text.AppendLine(Text.Line("  paired by the minute rule", f.PairedByMinute));
        text.AppendLine(Text.Line("  paired by the day-sequence rule", f.PairedByDay));
        text.AppendLine();
        text.AppendLine(Text.Line("manifest rows (one per paired attachment)", f.Rows.Count));
        text.AppendLine(Text.Line("  forwarded", f.Rows.Count(r => r.IsForwarded)));
        text.AppendLine(Text.Line($"    forwarded many times (score >= {ForwardedManyTimes.ToString(CultureInfo.InvariantCulture)})", f.Rows.Count(r => r.ForwardScore >= ForwardedManyTimes)));
        text.AppendLine(Text.Line("    forward score recorded", f.Rows.Count(r => r.ForwardScore is not null)));
        text.AppendLine(Text.Line("    forward score unreadable (recorded as none)", f.ScoresUnreadable));
        text.AppendLine(Text.Line("  not forwarded", f.Rows.Count(r => !r.IsForwarded)));
        text.AppendLine(Text.Line("  low-quality copy links", f.Rows.Count(r => r.LowQualityCopyOf is not null)));
        text.AppendLine();
        text.AppendLine(Text.Line("attachments not paired (no row)", f.NoRow.Total));

        foreach (var (reason, count) in f.NoRow.Items)
        {
            text.AppendLine(Text.Line("  " + reason, count));
        }

        text.AppendLine(Text.Line(LeftAloneReason, f.LeftAlone));
        text.AppendLine();
        text.AppendLine(Text.Line("type-12 links touching the chat", f.Type12Links));
        text.AppendLine(Text.Line("  recorded as low-quality copy links", f.Rows.Count(r => r.LowQualityCopyOf is not null)));
        text.AppendLine(Text.Line("  refused", f.LinksRefused.Total));

        foreach (var reason in new[] { LinkSideNotPaired, LinkMinute, LinkDirection, LinkSize, LinkCopyLeftAlone, LinkTwoTargets, LinkChain })
        {
            text.AppendLine(Text.Line("    " + reason, f.LinksRefused[reason]));
        }

        text.AppendLine(Text.Line("links of other types (ignored)", f.OtherTypeLinks));
        text.AppendLine();
        text.AppendLine(Text.Line("rows + not paired + left alone = attachments", f.Rows.Count + f.NoRow.Total + f.LeftAlone));
        text.AppendLine($"{"reconciliation",-58}{(Reconciled(f) ? "OK" : "MISMATCH")}");
        text.AppendLine("```");
        text.AppendLine();
        text.AppendLine("manifest SHA-256: " + result.Sha256);
        text.AppendLine("batch tag:        " + Manifest.Tag(result.Sha256));

        return text.ToString();
    }
}

/// <summary>What a facts apply or undo-batch did. Counts only.</summary>
internal sealed class FactsWriteResult
{
    internal required string Mode { get; init; }
    internal required string Tag { get; init; }
    internal required int Rows { get; init; }
    internal int Done { get; init; }
    internal int AlreadyDone { get; init; }
    internal int NotPresent { get; init; }

    internal bool Reconciled => Done + AlreadyDone + NotPresent == Rows;
}

/// <summary>Apply and undo-batch for a facts manifest, through <see cref="MsgstoreFactWriter"/> only.</summary>
/// <remarks>
/// A facts batch is all-or-nothing, as the writer is: apply recomputes every row from the msgstore and
/// workspace and refuses the whole batch, writing nothing, when any row differs from the manifest.
/// </remarks>
internal static class FactsBatch
{
    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static FactsWriteResult Apply(Options options)
    {
        var (manifest, tag) = Load(options.ManifestPath!, options.ManifestSha256!);

        if (options.ReviewFile is not null)
        {
            throw new RefusalException("--review-file applies only to a manifest of rule namesize-reviewed.");
        }

        Inputs.RequireMsgstore(options.MsgstorePath!);
        FactsPlanner.RequireFactsSchema(options.WorkspacePath);
        Inputs.RequireUnclaimed(options.WorkspacePath);

        var now = FactsPlanner.Compute(options.MsgstorePath!, options.WorkspacePath, manifest.Parameters, ownBatch: tag);
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

        Dictionary<long, AttachmentMsgstoreFact> stored;

        try
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(options.WorkspacePath);
            stored = new MsgstoreFactReader().Read(connection, [.. planned]).Facts.ToDictionary(f => f.Key, f => f.Value);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        var already = 0;
        var toWrite = new List<MsgstoreFactRow>();

        foreach (var row in manifest.Rows)
        {
            if (!stored.TryGetValue(row.AttachmentID, out var fact))
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
                    "An attachment in the manifest already has a different msgstore fact. Plan again. Nothing was written.");
            }
        }

        var done = 0;

        if (toWrite.Count > 0)
        {
            try
            {
                done = MsgstoreFactWriter.InsertBatch(options.WorkspacePath, tag, toWrite, DateTime.UtcNow);
            }
            catch (Exception exception) when (Inputs.IsClaimFailure(exception))
            {
                throw new RefusalException(WorkspaceMigrator.ClaimedRefusal);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                // The writer's message names attachments, so only the fact is printed.
                throw new RefusalException("The fact writer refused the batch. Nothing was written.");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        return new FactsWriteResult { Mode = "apply facts", Tag = tag, Rows = manifest.Rows.Count, Done = done, AlreadyDone = already };
    }

    /// <exception cref="RefusalException">Nothing was written.</exception>
    internal static FactsWriteResult Undo(Options options)
    {
        var (manifest, tag) = Load(options.ManifestPath!, options.ManifestSha256!);

        FactsPlanner.RequireFactsSchema(options.WorkspacePath);
        Inputs.RequireUnclaimed(options.WorkspacePath);

        int deleted;

        try
        {
            deleted = MsgstoreFactWriter.DeleteBatch(options.WorkspacePath, tag);
        }
        catch (Exception exception) when (Inputs.IsClaimFailure(exception))
        {
            throw new RefusalException(WorkspaceMigrator.ClaimedRefusal);
        }
        catch (InvalidOperationException)
        {
            throw new RefusalException("The fact writer refused to remove the batch. Nothing was written.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        return new FactsWriteResult
        {
            Mode = "undo-batch facts",
            Tag = tag,
            Rows = manifest.Rows.Count,
            Done = deleted,
            NotPresent = Math.Max(0, manifest.Rows.Count - deleted),
        };
    }

    internal static string Report(FactsWriteResult r)
    {
        var apply = r.Mode.StartsWith("apply", StringComparison.Ordinal);
        var text = new StringBuilder();

        text.AppendLine($"# Msgstore import - {r.Mode}");
        text.AppendLine();
        text.AppendLine("Counts only.");
        text.AppendLine();
        text.AppendLine("```text");
        text.AppendLine($"{"batch tag",-58}{r.Tag}");
        text.AppendLine(Text.Line("manifest rows", r.Rows));

        if (apply)
        {
            text.AppendLine(Text.Line("recorded", r.Done));
            text.AppendLine(Text.Line("already done (this batch, same facts)", r.AlreadyDone));
            text.AppendLine();
            text.AppendLine(Text.Line("recorded + already done", r.Done + r.AlreadyDone));
        }
        else
        {
            text.AppendLine(Text.Line("removed", r.Done));
            text.AppendLine(Text.Line("not present (never applied or already removed)", r.NotPresent));
            text.AppendLine();
            text.AppendLine(Text.Line("removed + not present", r.Done + r.NotPresent));
        }

        text.AppendLine($"{"reconciliation against manifest rows",-58}{(r.Reconciled ? "OK" : "MISMATCH")}");
        text.AppendLine("```");

        return text.ToString();
    }

    /// <exception cref="RefusalException">Missing, tampered or malformed. Nothing was opened.</exception>
    private static (FactsManifest Manifest, string Tag) Load(string path, string expectedSha256) =>
        FactsManifestFormat.Load(path, expectedSha256, FactsManifest.Parse);
}
