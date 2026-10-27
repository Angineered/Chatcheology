using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Memories.Psn
{
    /// <summary>What one enrichment run did (or, for a dry run, would do). Counts only.</summary>
    /// <param name="FormatVersionBefore">The memories file's format before the run (1 or 2).</param>
    /// <param name="Upgraded">The file was format 1 and is (or would be) upgraded to format 2.</param>
    /// <param name="Sets">Trophy sets in the definitions file.</param>
    /// <param name="Definitions">Definitions in the file.</param>
    /// <param name="Hidden">Definitions in the file marked hidden.</param>
    /// <param name="ItemsWithDefinition">Entry items whose (set, item) has a stored definition after the run.</param>
    /// <param name="ItemsWithoutDefinition">Entry items with no definition and no type mismatch.</param>
    /// <param name="ItemsTypeMismatch">Entry items whose definition in the file has another type.</param>
    /// <param name="TypeMismatches">Definitions not stored because an item with the same (set, item) has another type.</param>
    /// <param name="Added">Definitions new to the file.</param>
    /// <param name="Updated">Stored definitions whose values differed and were replaced.</param>
    /// <param name="Unchanged">Stored definitions identical to the file's.</param>
    /// <param name="RemovedForMismatch">Stored definitions removed because they now mismatch an item's type.</param>
    public sealed record PsnEnrichResult(
        int FormatVersionBefore,
        bool Upgraded,
        int Sets,
        int Definitions,
        int Hidden,
        int ItemsWithDefinition,
        int ItemsWithoutDefinition,
        int ItemsTypeMismatch,
        int TypeMismatches,
        int Added,
        int Updated,
        int Unchanged,
        int RemovedForMismatch)
    {
        /// <summary>Everything written: 0 for a re-run of the same file.</summary>
        public int Changes => (Upgraded ? 1 : 0) + Added + Updated + RemovedForMismatch;
    }

    /// <summary>
    /// Stores trophy definitions in <c>ItemDefinition</c> by (set, item), idempotently, in one
    /// transaction. Entry items are never changed; viewers join on (SetId, ItemId).
    /// </summary>
    /// <remarks>
    /// Values are stored as the file holds them. A definition whose type differs from the type of an
    /// entry item with the same (set, item) is not stored (missing is better than wrong); types are
    /// compared ignoring case only. A stored icon hash is kept while the icon URL is unchanged.
    /// Stored definitions absent from the file are kept.
    /// </remarks>
    public static class PsnTrophyEnricher
    {
        private sealed record Stored(string ItemType, string? Name, string? Detail, long? Hidden, string? GroupId, string? IconUrl);

        private enum WriteKind
        {
            Add,
            Update,
            Remove,
        }

        /// <summary>Counts what <see cref="Enrich"/> would do. Opens the file read-only; writes nothing.</summary>
        public static PsnEnrichResult DryRun(string memoriesPath, PsnTrophyDefinitions definitions)
        {
            ArgumentNullException.ThrowIfNull(definitions);

            using var connection = MemoriesDatabase.OpenReadOnlyConnection(memoriesPath);
            var version = MemoriesDatabase.ReadFormatVersion(connection);
            var (result, _) = Plan(connection, null, version, definitions);
            return result;
        }

        /// <summary>
        /// Upgrades a format 1 file and stores the definitions, all in one transaction.
        /// </summary>
        public static PsnEnrichResult Enrich(string memoriesPath, PsnTrophyDefinitions definitions)
        {
            ArgumentNullException.ThrowIfNull(definitions);

            using var connection = MemoriesDatabase.OpenConnection(memoriesPath);
            var version = MemoriesDatabase.ReadFormatVersion(connection);
            using var transaction = connection.BeginTransaction();

            MemoriesDatabase.UpgradeToCurrent(connection, transaction);
            var (result, actions) = Plan(connection, transaction, MemoriesDatabase.MarkerFormatVersion, definitions);
            Write(connection, transaction, actions);

            transaction.Commit();

            return result with { FormatVersionBefore = version, Upgraded = version != MemoriesDatabase.MarkerFormatVersion };
        }

        private static (PsnEnrichResult Result, List<(WriteKind Kind, PsnTrophyDefinition Definition)> Actions) Plan(
            SqliteConnection connection, SqliteTransaction? transaction, int version, PsnTrophyDefinitions definitions)
        {
            var stored = version == MemoriesDatabase.MarkerFormatVersion
                ? ReadStored(connection, transaction)
                : new Dictionary<(string, string), Stored>();
            var itemTypes = ReadItemTypes(connection, transaction);

            var actions = new List<(WriteKind, PsnTrophyDefinition)>();
            var mismatched = new HashSet<(string, string)>();
            int added = 0, updated = 0, unchanged = 0, removed = 0;

            foreach (var definition in definitions.Definitions)
            {
                var key = (definition.SetId, definition.ItemId);

                if (itemTypes.TryGetValue(key, out var types)
                    && types.Any(type => !string.Equals(type, definition.ItemType, StringComparison.OrdinalIgnoreCase)))
                {
                    mismatched.Add(key);

                    if (stored.ContainsKey(key))
                    {
                        actions.Add((WriteKind.Remove, definition));
                        removed++;
                    }

                    continue;
                }

                if (!stored.TryGetValue(key, out var existing))
                {
                    actions.Add((WriteKind.Add, definition));
                    added++;
                }
                else if (existing == ToStored(definition))
                {
                    unchanged++;
                }
                else
                {
                    actions.Add((WriteKind.Update, definition));
                    updated++;
                }
            }

            // Stored definitions the file does not mention: an item re-imported since may now have
            // another type, and a wrong definition must not stay.
            var inFile = definitions.Definitions.Select(d => (d.SetId, d.ItemId)).ToHashSet();

            foreach (var (key, existing) in stored)
            {
                if (!inFile.Contains(key)
                    && itemTypes.TryGetValue(key, out var types)
                    && types.Any(type => !string.Equals(type, existing.ItemType, StringComparison.OrdinalIgnoreCase)))
                {
                    mismatched.Add(key);
                    actions.Add((WriteKind.Remove,
                        new PsnTrophyDefinition(key.Item1, key.Item2, existing.ItemType, null, null, null, null, null)));
                    removed++;
                }
            }

            // Definitions stored after the run: what was there, less removals, plus the file's matching ones.
            var after = stored.Keys.Where(key => !mismatched.Contains(key)).ToHashSet();
            after.UnionWith(definitions.Definitions.Select(d => (d.SetId, d.ItemId)).Where(key => !mismatched.Contains(key)));

            int withDefinition = 0, withoutDefinition = 0, itemMismatch = 0;

            foreach (var (key, types) in itemTypes)
            {
                var count = types.Count;

                if (mismatched.Contains(key))
                {
                    itemMismatch += count;
                }
                else if (after.Contains(key))
                {
                    withDefinition += count;
                }
                else
                {
                    withoutDefinition += count;
                }
            }

            var result = new PsnEnrichResult(
                version,
                version != MemoriesDatabase.MarkerFormatVersion,
                definitions.SetCount,
                definitions.Definitions.Count,
                definitions.Definitions.Count(d => d.Hidden == true),
                withDefinition,
                withoutDefinition,
                itemMismatch,
                mismatched.Count,
                added,
                updated,
                unchanged,
                removed);

            return (result, actions);
        }

        private static Stored ToStored(PsnTrophyDefinition d) =>
            new(d.ItemType, d.Name, d.Detail, d.Hidden is null ? null : d.Hidden.Value ? 1 : 0, d.GroupId, d.IconUrl);

        private static Dictionary<(string, string), Stored> ReadStored(SqliteConnection connection, SqliteTransaction? transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT SetId, ItemId, ItemType, Name, Detail, Hidden, GroupId, IconSourceUrl FROM ItemDefinition;";

            var stored = new Dictionary<(string, string), Stored>();
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                stored[(reader.GetString(0), reader.GetString(1))] = new Stored(
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7));
            }

            return stored;
        }

        /// <summary>Every entry item's type, grouped by (set, item); one list element per item row.</summary>
        private static Dictionary<(string, string), List<string>> ReadItemTypes(SqliteConnection connection, SqliteTransaction? transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT SetId, ItemId, ItemType FROM EntryItem;";

            var types = new Dictionary<(string, string), List<string>>();
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var key = (reader.GetString(0), reader.GetString(1));

                if (!types.TryGetValue(key, out var list))
                {
                    types[key] = list = [];
                }

                list.Add(reader.GetString(2));
            }

            return types;
        }

        private static void Write(
            SqliteConnection connection, SqliteTransaction transaction, List<(WriteKind Kind, PsnTrophyDefinition Definition)> actions)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            foreach (var (kind, d) in actions)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$set", d.SetId);
                command.Parameters.AddWithValue("$item", d.ItemId);

                if (kind == WriteKind.Remove)
                {
                    command.CommandText = "DELETE FROM ItemDefinition WHERE SetId = $set AND ItemId = $item;";
                    command.ExecuteNonQuery();
                    continue;
                }

                command.CommandText = kind == WriteKind.Add
                    ? """
                      INSERT INTO ItemDefinition (SetId, ItemId, ItemType, Name, Detail, Hidden, GroupId, IconSourceUrl, IconSha256)
                      VALUES ($set, $item, $type, $name, $detail, $hidden, $group, $url, NULL);
                      """
                    : """
                      UPDATE ItemDefinition
                      SET ItemType = $type, Name = $name, Detail = $detail, Hidden = $hidden, GroupId = $group,
                          IconSha256 = CASE WHEN IconSourceUrl IS $url THEN IconSha256 ELSE NULL END,
                          IconSourceUrl = $url
                      WHERE SetId = $set AND ItemId = $item;
                      """;
                command.Parameters.AddWithValue("$type", d.ItemType);
                command.Parameters.AddWithValue("$name", (object?)d.Name ?? DBNull.Value);
                command.Parameters.AddWithValue("$detail", (object?)d.Detail ?? DBNull.Value);
                command.Parameters.AddWithValue("$hidden", d.Hidden is null ? DBNull.Value : d.Hidden.Value ? 1 : 0);
                command.Parameters.AddWithValue("$group", (object?)d.GroupId ?? DBNull.Value);
                command.Parameters.AddWithValue("$url", (object?)d.IconUrl ?? DBNull.Value);

                if (command.ExecuteNonQuery() != 1)
                {
                    throw new InvalidOperationException(
                        "A definition write did not change exactly one row. Nothing has been written.");
                }
            }
        }
    }
}
