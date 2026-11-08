using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MsgstoreImport;

/// <summary>Opening the msgstore without any chance of writing beside it.</summary>
/// <remarks>Copied unchanged from <c>.temp\MsgstoreCensus\Msgstore.cs</c>.</remarks>
internal static class Msgstore
{
    /// <remarks>
    /// <c>Mode=ReadOnly</c> alone is not enough for a WAL-mode file: SQLite would still create
    /// <c>-wal</c> and <c>-shm</c> files next to it. <c>immutable=1</c> tells SQLite the file cannot
    /// change, so it takes no locks and creates nothing. The caller has already refused when a
    /// <c>-wal</c> or <c>-journal</c> exists, because an immutable read would ignore it. No pooling,
    /// so nothing holds the file after the connection is disposed.
    /// </remarks>
    internal static SqliteConnection OpenReadOnly(string path)
    {
        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?mode=ro&immutable=1";

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = uri,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();

        return connection;
    }
}

/// <summary>What the msgstore holds, found by introspection rather than assumed.</summary>
/// <remarks>
/// The census's schema check, without its report. The import additionally requires
/// <c>message.key_id</c>, because every confirmation it writes names the msgstore record by it.
/// </remarks>
internal sealed class MsgstoreSchema
{
    internal static readonly (string Table, string[] Required, string[] Optional)[] Expected =
    [
        ("message", ["_id", "chat_row_id", "from_me", "timestamp", "message_type"], ["key_id", "sort_id"]),
        ("message_media", ["message_row_id", "file_path", "file_size"], ["file_hash", "mime_type", "media_name"]),
        ("chat", ["_id"], []),
    ];

    private readonly Dictionary<string, List<string>> _columns = new(StringComparer.OrdinalIgnoreCase);

    internal List<string> Tables { get; } = [];

    internal bool IsOldStyle => !HasTable("message") && HasTable("messages");

    internal bool HasTable(string table) => Tables.Contains(table, StringComparer.OrdinalIgnoreCase);

    internal bool HasColumn(string table, string column) =>
        _columns.TryGetValue(table, out var columns)
        && columns.Any(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase));

    internal static MsgstoreSchema Read(SqliteConnection connection)
    {
        var schema = new MsgstoreSchema();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                schema.Tables.Add(reader.GetString(0));
            }
        }

        foreach (var table in Expected.Select(e => e.Table).Where(schema.HasTable))
        {
            var actual = schema.Tables.First(t => string.Equals(t, table, StringComparison.OrdinalIgnoreCase));
            var columns = new List<string>();

            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{actual}\");";

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }

            schema._columns[table] = columns;
        }

        return schema;
    }

    /// <summary>Why the import must refuse, or null.</summary>
    internal string? Refusal()
    {
        if (IsOldStyle)
        {
            return "This is an old-style msgstore: it has a single 'messages' table and no 'message' " +
                   "table. This tool reads only the current schema and refuses.";
        }

        var missing = Expected
            .SelectMany(e => e.Required.Where(column => !HasColumn(e.Table, column)).Select(column => $"{e.Table}.{column}"))
            .ToList();

        if (missing.Count > 0)
        {
            return "The msgstore lacks required column(s): " + string.Join(", ", missing) + ".";
        }

        if (!HasColumn("message", "key_id"))
        {
            return "The msgstore has no message.key_id column. Every confirmation names its msgstore " +
                   "record by key_id, so the import refuses.";
        }

        if (!HasColumn("message_media", "file_hash"))
        {
            return "The msgstore has no message_media.file_hash column. Every imported row must have a " +
                   "hash match, so the import refuses.";
        }

        return null;
    }
}

/// <summary>The msgstore's size and last-write time, to show a run did not change it.</summary>
internal readonly record struct FileStamp(long Length, DateTime LastWriteUtc)
{
    internal static FileStamp Of(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();

        return new FileStamp(info.Length, info.LastWriteTimeUtc);
    }
}

/// <summary>Formatting shared by every mode.</summary>
internal static class Text
{
    internal static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    internal static string Line(string label, int value) =>
        (label.Length >= 58 ? label + "  " : label.PadRight(58)) + Count(value);

    internal static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
}
