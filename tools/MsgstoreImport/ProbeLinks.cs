using System.Globalization;
using System.Text;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport;

/// <summary>
/// <c>probe-links</c> (CH-8, CH-10): how the msgstore records that a message was forwarded, and that a
/// media message has a linked HD / lower-quality copy. Read-only on both databases; stdout only.
/// </summary>
/// <remarks>
/// <para>Prints schema names (tables, columns, declared types), enumeration values (message_type,
/// association type, bucketed scores) and counts. Never a message text, participant name, file
/// name, key_id, path or row id: only ids, direction, time, type, media-row presence and file_size
/// are ever selected from message rows.</para>
/// <para>The WhatsApp structures it looks for are assumptions about the schema, recognised by name:
/// a table whose name contains <c>forward</c> with a <c>message_row_id</c> key, a
/// <c>message.forwarded</c> column, and a table whose name contains <c>assoc</c> with a parent and a
/// child <c>..._row_id</c> column (plus an optional <c>...type</c> column).</para>
/// </remarks>
internal static class LinkProber
{
    internal static readonly string[] ScanTerms =
        ["forward", "hd", "quality", "association", "assoc", "parent", "child", "original", "add_on", "addon",
         "related", "edit", "revision", "thumb"];

    /// <exception cref="RefusalException">Nothing was read to completion.</exception>
    internal static string Run(Options options)
    {
        var p = options.Parameters!;

        Inputs.RequireMsgstore(options.MsgstorePath!);
        Inputs.RequireWorkspace(options.WorkspacePath);

        var before = FileStamp.Of(options.MsgstorePath!);
        string text;

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
            text = new Build(msgstore, pairing, p, options.LinksMinute).Render();
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

    internal static bool Matches(string name) =>
        ScanTerms.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));

    /// <summary>A score-like integer, bucketed so no raw value that could be an id is printed.</summary>
    internal static string Bucket(object? value)
    {
        if (value is null or DBNull)
        {
            return "null";
        }

        if (value is not long n)
        {
            return "not an integer";
        }

        return n switch
        {
            < 0 => "negative",
            0 => "0",
            1 => "1",
            <= 4 => "2-4",
            <= 9 => "5-9",
            <= 99 => "10-99",
            _ => "100+",
        };
    }

    private sealed record Column(string Name, string Type);

    private sealed record MessageInfo(long ID, bool FromMe, long? Type, DateTime? Minute, bool HasMedia, long? FileSize);

    private sealed record Link(string Table, long? Parent, long? Child, string Type);

    private sealed class Build(SqliteConnection msgstore, Pairing pairing, PairingParameters p, DateTime? minute)
    {
        private readonly StringBuilder _text = new();
        private readonly Dictionary<string, List<Column>> _columns = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Table, string Error)> _unreadable = [];
        private Dictionary<long, MessageInfo>? _messages;
        private HashSet<long>? _byMinuteRule;
        private HashSet<long>? _byDaySequence;

        internal string Render()
        {
            _text.AppendLine("# Msgstore import - probe-links (CH-8, CH-10)");
            _text.AppendLine();
            _text.AppendLine("Read-only on both databases; nothing was written. Schema names, enumeration values and counts");
            _text.AppendLine("only: no message text, name, file name, key_id, path or row id.");
            _text.AppendLine();

            foreach (var table in Tables())
            {
                // A virtual table whose module this SQLite build lacks (e.g. an FTS table with a custom
                // tokenizer) cannot be introspected. It is reported by name and skipped.
                try
                {
                    _columns[table] = TableColumns(table);
                }
                catch (SqliteException exception)
                {
                    _unreadable.Add((table, exception.GetType().Name));
                }
            }

            SchemaScan();

            var forwarded = Forwarded();
            var linked = Linked();

            if (minute is not null)
            {
                MinuteSection(linked.Links);
            }

            _text.AppendLine("## 4. Verdict");
            _text.AppendLine();

            if (!forwarded && !linked.Found)
            {
                _text.AppendLine("None of the expected structures exist: no forward table keyed by message_row_id, no");
                _text.AppendLine("message.forwarded column, and no association table with parent and child row ids. The");
                _text.AppendLine("schema scan (section 1) is the answer.");
            }
            else
            {
                _text.AppendLine(CultureInfo.InvariantCulture,
                    $"Forwarding structure found: {(forwarded ? "yes" : "no")}. Linked-copy structure found: {(linked.Found ? "yes" : "no")}.");
            }

            return _text.ToString();
        }

        // -----------------------------------------------------------------------------------
        // 1. Schema scan.
        // -----------------------------------------------------------------------------------

        private void SchemaScan()
        {
            _text.AppendLine("## 1. Schema scan");
            _text.AppendLine();
            _text.AppendLine("Terms (case-insensitive substring): " + string.Join(", ", ScanTerms) + ".");
            _text.AppendLine();
            if (_unreadable.Count > 0)
            {
                _text.AppendLine("Tables that could not be introspected (any table, matching or not):");
                _text.AppendLine();
                _text.AppendLine("```text");

                foreach (var (table, error) in _unreadable.OrderBy(u => u.Table, StringComparer.OrdinalIgnoreCase))
                {
                    _text.AppendLine(CultureInfo.InvariantCulture, $"table {table}: schema not readable ({error})");
                }

                _text.AppendLine("```");
                _text.AppendLine();
            }

            _text.AppendLine("Tables whose name matches (row count, then every column):");
            _text.AppendLine();
            _text.AppendLine("```text");

            var matchingTables = _columns.Keys.Where(Matches).Order(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var table in matchingTables)
            {
                try
                {
                    _text.AppendLine(Text.Line(table + " rows", (int)Math.Min(int.MaxValue, Count($"SELECT COUNT(*) FROM {Q(table)};"))));
                }
                catch (SqliteException exception)
                {
                    _text.AppendLine(CultureInfo.InvariantCulture, $"table {table}: row count not readable ({exception.GetType().Name})");
                }

                foreach (var column in _columns[table])
                {
                    _text.AppendLine(CultureInfo.InvariantCulture, $"    {column.Name} {Type(column)}");
                }
            }

            if (matchingTables.Count == 0)
            {
                _text.AppendLine("none");
            }

            _text.AppendLine("```");
            _text.AppendLine();
            _text.AppendLine("Columns whose name matches, in any table:");
            _text.AppendLine();
            _text.AppendLine("```text");

            var any = false;

            foreach (var (table, columns) in _columns.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var column in columns.Where(c => Matches(c.Name)))
                {
                    _text.AppendLine(CultureInfo.InvariantCulture, $"{table}.{column.Name} {Type(column)}");
                    any = true;
                }
            }

            if (!any)
            {
                _text.AppendLine("none");
            }

            _text.AppendLine("```");
            _text.AppendLine();
        }

        private static string Type(Column column) => column.Type.Length == 0 ? "(no declared type)" : column.Type;

        // -----------------------------------------------------------------------------------
        // 2. Forwarded (CH-10).
        // -----------------------------------------------------------------------------------

        private bool Forwarded()
        {
            _text.AppendLine("## 2. Forwarded (CH-10)");
            _text.AppendLine();

            var tables = _columns
                .Where(t => t.Key.Contains("forward", StringComparison.OrdinalIgnoreCase))
                .Select(t => (Table: t.Key, Key: KeyColumn(t.Value), Columns: t.Value))
                .OrderBy(t => t.Table, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var legacy = _columns.TryGetValue("message", out var messageColumns)
                ? messageColumns.FirstOrDefault(c => string.Equals(c.Name, "forwarded", StringComparison.OrdinalIgnoreCase))
                : null;

            var found = false;

            foreach (var (table, key, columns) in tables)
            {
                if (key is null)
                {
                    _text.AppendLine(CultureInfo.InvariantCulture,
                        $"Table {table} has no message_row_id column, so it was not read beyond the schema scan.");
                    _text.AppendLine();
                    continue;
                }

                var extras = columns.Where(c => !string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase)).ToList();
                var select = string.Join("", extras.Select(c => ", f." + Q(c.Name)));

                var rows = new List<(long ID, object?[] Extras)>();

                try
                {
                    using (var command = msgstore.CreateCommand())
                    {
                        command.CommandText =
                            $"""
                            SELECT f.{Q(key)}{select}
                            FROM {Q(table)} AS f
                            INNER JOIN message AS m ON m._id = f.{Q(key)}
                            WHERE m.chat_row_id = $chat;
                            """;
                        command.Parameters.AddWithValue("$chat", p.ChatID);

                        using var reader = command.ExecuteReader();

                        while (reader.Read())
                        {
                            if (reader.IsDBNull(0) || reader.GetValue(0) is not long id)
                            {
                                continue;
                            }

                            rows.Add((id, Enumerable.Range(1, extras.Count).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray()));
                        }
                    }
                }
                catch (SqliteException exception)
                {
                    _text.AppendLine(CultureInfo.InvariantCulture, $"table {table}: rows not readable ({exception.GetType().Name})");
                    _text.AppendLine();
                    continue;
                }

                found = true;
                _text.AppendLine(CultureInfo.InvariantCulture, $"### Table {table}, keyed by {key}");
                _text.AppendLine();
                _text.AppendLine("```text");
                ForwardCounts(rows.Select(r => r.ID).ToList());

                for (var i = 0; i < extras.Count; i++)
                {
                    _text.AppendLine(CultureInfo.InvariantCulture, $"{extras[i].Name} distribution (bucketed):");

                    foreach (var bucket in rows.GroupBy(r => Bucket(r.Extras[i])).OrderBy(g => BucketOrder(g.Key)))
                    {
                        _text.AppendLine(Text.Line("  " + bucket.Key, bucket.Count()));
                    }
                }

                _text.AppendLine("```");
                _text.AppendLine();
            }

            if (legacy is not null)
            {
                found = true;
                var rows = new List<(long ID, object? Value)>();

                using (var command = msgstore.CreateCommand())
                {
                    command.CommandText = $"SELECT m._id, m.{Q(legacy.Name)} FROM message AS m WHERE m.chat_row_id = $chat;";
                    command.Parameters.AddWithValue("$chat", p.ChatID);

                    using var reader = command.ExecuteReader();

                    while (reader.Read())
                    {
                        rows.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetValue(1)));
                    }
                }

                _text.AppendLine(CultureInfo.InvariantCulture, $"### Legacy column message.{legacy.Name}");
                _text.AppendLine();
                _text.AppendLine("Marked forwarded = a non-null integer value other than 0.");
                _text.AppendLine();
                _text.AppendLine("```text");
                ForwardCounts(rows.Where(r => r.Value is long v && v != 0).Select(r => r.ID).ToList());
                _text.AppendLine("value distribution over every message of the chat (bucketed):");

                foreach (var bucket in rows.GroupBy(r => Bucket(r.Value)).OrderBy(g => BucketOrder(g.Key)))
                {
                    _text.AppendLine(Text.Line("  " + bucket.Key, bucket.Count()));
                }

                _text.AppendLine("```");
                _text.AppendLine();
            }

            if (!found)
            {
                _text.AppendLine("Not present: no table whose name contains 'forward' keyed by message_row_id, and no");
                _text.AppendLine("message.forwarded column.");
                _text.AppendLine();
            }

            return found;
        }

        private void ForwardCounts(List<long> ids)
        {
            var messages = Messages();
            var known = ids.Distinct().Where(messages.ContainsKey).Select(id => messages[id]).ToList();
            var media = known.Where(m => m.HasMedia).ToList();

            _text.AppendLine(Text.Line("messages of the chat marked forwarded", known.Count));
            _text.AppendLine(Text.Line("  with a message_media row (media)", media.Count));
            _text.AppendLine(Text.Line("  without one (text and other)", known.Count - media.Count));
            _text.AppendLine(Text.Line("  from_me = 1 (sent)", known.Count(m => m.FromMe)));
            _text.AppendLine(Text.Line("  from_me = 0 (received)", known.Count(m => !m.FromMe)));
            _text.AppendLine("  by message_type:");

            foreach (var group in known.GroupBy(m => m.Type).OrderBy(g => g.Key ?? long.MinValue))
            {
                _text.AppendLine(Text.Line($"    {Show(group.Key)}", group.Count()));
            }

            _text.AppendLine(Text.Line("forwarded media paired to a workspace attachment", media.Count(m => Paired(m.ID))));
            _text.AppendLine(Text.Line("  by the minute rule", media.Count(m => ByMinute().Contains(m.ID))));
            _text.AppendLine(Text.Line("  by the day-sequence rule", media.Count(m => ByDay().Contains(m.ID))));
        }

        // -----------------------------------------------------------------------------------
        // 3. Linked copies (CH-8).
        // -----------------------------------------------------------------------------------

        private (bool Found, List<Link> Links) Linked()
        {
            _text.AppendLine("## 3. Linked copies (CH-8)");
            _text.AppendLine();

            var all = new List<Link>();
            var found = false;

            foreach (var (table, columns) in _columns
                         .Where(t => t.Key.Contains("assoc", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
            {
                var parent = SideColumn(columns, "parent");
                var child = SideColumn(columns, "child");
                var type = columns.FirstOrDefault(c => c.Name.Contains("type", StringComparison.OrdinalIgnoreCase));

                if (parent is null || child is null)
                {
                    _text.AppendLine(CultureInfo.InvariantCulture,
                        $"Table {table} has no parent and child ..._row_id columns, so it was not read beyond the schema scan.");
                    _text.AppendLine();
                    continue;
                }

                var links = new List<Link>();

                try
                {
                    using (var command = msgstore.CreateCommand())
                    {
                        command.CommandText =
                            $"""
                            SELECT a.{Q(parent)}, a.{Q(child)}, {(type is null ? "NULL" : "a." + Q(type.Name))}
                            FROM {Q(table)} AS a
                            WHERE a.{Q(parent)} IN (SELECT _id FROM message WHERE chat_row_id = $chat)
                               OR a.{Q(child)} IN (SELECT _id FROM message WHERE chat_row_id = $chat);
                            """;
                        command.Parameters.AddWithValue("$chat", p.ChatID);

                        using var reader = command.ExecuteReader();

                        while (reader.Read())
                        {
                            links.Add(new Link(
                                table,
                                reader.IsDBNull(0) ? null : reader.GetValue(0) as long?,
                                reader.IsDBNull(1) ? null : reader.GetValue(1) as long?,
                                type is null ? "(no type column)" : TypeValue(reader.IsDBNull(2) ? null : reader.GetValue(2))));
                        }
                    }
                }
                catch (SqliteException exception)
                {
                    _text.AppendLine(CultureInfo.InvariantCulture, $"table {table}: rows not readable ({exception.GetType().Name})");
                    _text.AppendLine();
                    continue;
                }

                found = true;
                all.AddRange(links);

                _text.AppendLine(CultureInfo.InvariantCulture,
                    $"### Table {table}: parent {parent}, child {child}, type {(type is null ? "(none)" : type.Name)}");
                _text.AppendLine();
                _text.AppendLine("Links with at least one side in the chat, by type. Paired = the side's msgstore message is");
                _text.AppendLine("matched to a workspace attachment by the minute rule or the CH-9 day-sequence rule.");
                _text.AppendLine();
                _text.AppendLine("```text");
                LinkCounts(links, null);
                _text.AppendLine("```");
                _text.AppendLine();
            }

            if (!found)
            {
                _text.AppendLine("Not present: no table whose name contains 'assoc' with parent and child ..._row_id columns.");
                _text.AppendLine();
            }

            return (found, all);
        }

        private void LinkCounts(List<Link> links, DateTime? inMinute)
        {
            var messages = Messages();

            if (links.Count == 0)
            {
                _text.AppendLine("no links");
                return;
            }

            foreach (var group in links.GroupBy(l => (l.Table, l.Type))
                         .OrderBy(g => g.Key.Table, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(g => g.Key.Type, StringComparer.Ordinal))
            {
                var rows = group
                    .Select(l => (Parent: l.Parent is { } a ? messages.GetValueOrDefault(a) : null,
                                  Child: l.Child is { } b ? messages.GetValueOrDefault(b) : null))
                    .ToList();
                var both = rows.Where(r => r.Parent is not null && r.Child is not null).ToList();
                var media = both.Where(r => r.Parent!.HasMedia && r.Child!.HasMedia).ToList();
                var sized = media.Where(r => r.Parent!.FileSize is not null && r.Child!.FileSize is not null).ToList();

                var label = group.Key.Table.Length == 0 ? $"type {group.Key.Type}" : $"{group.Key.Table} type {group.Key.Type}";
                _text.AppendLine(Text.Line($"{label}: links", rows.Count));
                _text.AppendLine(Text.Line("  one side outside the chat or missing", rows.Count - both.Count));
                _text.AppendLine(Text.Line("  both sides media", media.Count));
                _text.AppendLine(Text.Line("  both sides paired to attachments", media.Count(r => Paired(r.Parent!.ID) && Paired(r.Child!.ID))));
                _text.AppendLine(Text.Line("    by the minute rule", media.Count(r => ByMinute().Contains(r.Parent!.ID) && ByMinute().Contains(r.Child!.ID))));
                _text.AppendLine(Text.Line("  exactly one side paired", media.Count(r => Paired(r.Parent!.ID) != Paired(r.Child!.ID))));
                _text.AppendLine(Text.Line("  both sides in the same local minute", both.Count(r => r.Parent!.Minute is { } m && m == r.Child!.Minute)));
                _text.AppendLine(Text.Line("  same direction (from_me)", both.Count(r => r.Parent!.FromMe == r.Child!.FromMe)));
                _text.AppendLine(Text.Line("  media: child file_size smaller", sized.Count(r => r.Child!.FileSize < r.Parent!.FileSize)));
                _text.AppendLine(Text.Line("  media: parent file_size smaller", sized.Count(r => r.Parent!.FileSize < r.Child!.FileSize)));
                _text.AppendLine(Text.Line("  media: sizes equal", sized.Count(r => r.Parent!.FileSize == r.Child!.FileSize)));
                _text.AppendLine(Text.Line("  media: a size unknown", media.Count - sized.Count));

                if (inMinute is { } target)
                {
                    var inside = MediaInMinute(target);
                    var hiddenChild = media.Where(r => r.Child!.Minute == target).Select(r => r.Child!.ID).ToHashSet();
                    var hiddenParent = media.Where(r => r.Parent!.Minute == target).Select(r => r.Parent!.ID).ToHashSet();

                    _text.AppendLine(Text.Line("  media records left in the minute, child side hidden", inside - hiddenChild.Count));
                    _text.AppendLine(Text.Line("  media records left in the minute, parent side hidden", inside - hiddenParent.Count));
                }
            }
        }

        // -----------------------------------------------------------------------------------
        // The --minute restriction.
        // -----------------------------------------------------------------------------------

        private void MinuteSection(List<Link> links)
        {
            var target = minute!.Value;
            var messages = Messages();
            var (attachments, _) = pairing.Load();

            bool InMinute(long? id) => id is { } i && messages.GetValueOrDefault(i)?.Minute == target;

            var here = links.Where(l => InMinute(l.Parent) || InMinute(l.Child)).ToList();

            _text.AppendLine(CultureInfo.InvariantCulture, $"## 3a. Restricted to the local minute {target:yyyy-MM-dd HH:mm}");
            _text.AppendLine();
            _text.AppendLine("Links with at least one side in the minute. \"Left\" = the minute's media records (both directions)");
            _text.AppendLine("minus the distinct records that are that side of a both-media link of the type, inside the minute.");
            _text.AppendLine();
            _text.AppendLine("```text");
            _text.AppendLine(Text.Line("msgstore media records in the minute (both directions)", MediaInMinute(target)));
            _text.AppendLine(Text.Line("  received", messages.Values.Count(m => m.HasMedia && m.Minute == target && !m.FromMe)));
            _text.AppendLine(Text.Line("  sent", messages.Values.Count(m => m.HasMedia && m.Minute == target && m.FromMe)));
            _text.AppendLine(Text.Line("workspace attachments in the minute (both directions)", attachments.Count(a => a.Minute == target)));
            _text.AppendLine(Text.Line("media records in the minute paired to attachments", messages.Values.Count(m => m.HasMedia && m.Minute == target && Paired(m.ID))));

            if (here.Count > 0)
            {
                var all = here.Select(l => l with { Table = "", Type = "(all tables and types)" }).ToList();
                LinkCounts(all, target);
            }

            LinkCounts(here, target);
            _text.AppendLine("```");
            _text.AppendLine();
        }

        private int MediaInMinute(DateTime target) => Messages().Values.Count(m => m.HasMedia && m.Minute == target);

        // -----------------------------------------------------------------------------------
        // Reads.
        // -----------------------------------------------------------------------------------

        /// <summary>Every message of the chat: id, direction, minute, type, media-row presence and size only.</summary>
        private Dictionary<long, MessageInfo> Messages()
        {
            if (_messages is not null)
            {
                return _messages;
            }

            _messages = [];

            using var command = msgstore.CreateCommand();
            command.CommandText =
                """
                SELECT m._id, m.from_me, m.timestamp, m.message_type,
                       mm.message_row_id IS NOT NULL, mm.file_size
                FROM message AS m
                LEFT JOIN message_media AS mm ON mm.message_row_id = m._id
                WHERE m.chat_row_id = $chat;
                """;
            command.Parameters.AddWithValue("$chat", p.ChatID);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var local = pairing.Local(reader.IsDBNull(2) ? null : reader.GetInt64(2));

                _messages.TryAdd(reader.GetInt64(0), new MessageInfo(
                    reader.GetInt64(0),
                    !reader.IsDBNull(1) && reader.GetInt64(1) != 0,
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    local is { } t ? new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0) : null,
                    reader.GetInt64(4) != 0,
                    reader.IsDBNull(5) ? null : reader.GetInt64(5)));
            }

            return _messages;
        }

        private bool Paired(long id) => ByMinute().Contains(id) || ByDay().Contains(id);

        /// <summary>msgstore messages the census minute rule pairs with an attachment (equal-count keys).</summary>
        private HashSet<long> ByMinute() =>
            _byMinuteRule ??= pairing.Compute().Values.Where(a => a.MessageID is not null).Select(a => a.MessageID!.Value).ToHashSet();

        /// <summary>msgstore messages the CH-9 day-sequence rule lines up with an attachment slot (usable days).</summary>
        private HashSet<long> ByDay() =>
            _byDaySequence ??= new DaySequence(pairing).Compute().ByAttachment.Values
                .Where(d => d.MessageID is not null && d.Eligibility is not (DayEligibility.NoTime or DayEligibility.DaySkipped))
                .Select(d => d.MessageID!.Value)
                .ToHashSet();

        private List<string> Tables()
        {
            var tables = new List<string>();

            using var command = msgstore.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }

            return tables;
        }

        private List<Column> TableColumns(string table)
        {
            var columns = new List<Column>();

            using var command = msgstore.CreateCommand();
            command.CommandText = $"PRAGMA table_info({Q(table)});";

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                columns.Add(new Column(reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2)));
            }

            return columns;
        }

        private long Count(string sql)
        {
            using var command = msgstore.CreateCommand();
            command.CommandText = sql;

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        /// <summary><c>message_row_id</c>, else a column ending in it, else null.</summary>
        private static string? KeyColumn(List<Column> columns) =>
            columns.FirstOrDefault(c => string.Equals(c.Name, "message_row_id", StringComparison.OrdinalIgnoreCase))?.Name
            ?? columns.FirstOrDefault(c => c.Name.EndsWith("message_row_id", StringComparison.OrdinalIgnoreCase))?.Name;

        /// <summary><c>{side}_message_row_id</c>, else any <c>..._row_id</c> column containing the side.</summary>
        private static string? SideColumn(List<Column> columns, string side) =>
            columns.FirstOrDefault(c => string.Equals(c.Name, side + "_message_row_id", StringComparison.OrdinalIgnoreCase))?.Name
            ?? columns.FirstOrDefault(c => c.Name.Contains(side, StringComparison.OrdinalIgnoreCase)
                                           && c.Name.EndsWith("row_id", StringComparison.OrdinalIgnoreCase))?.Name;

        /// <summary>An enumeration value: small integers as themselves; anything else only by kind.</summary>
        private static string TypeValue(object? value) => value switch
        {
            null => "null",
            long n when n is >= -1000 and <= 1000 => n.ToString(CultureInfo.InvariantCulture),
            long => "integer outside -1000..1000",
            _ => "not an integer",
        };

        private static int BucketOrder(string bucket) =>
            Array.IndexOf(new[] { "null", "negative", "0", "1", "2-4", "5-9", "10-99", "100+", "not an integer" }, bucket);

        private static string Show(long? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "null";

        private static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    }
}
