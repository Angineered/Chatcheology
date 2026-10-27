using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Memories.Psn
{
    /// <summary>What one PSN import changed. Counts only.</summary>
    /// <param name="CollectionCreated">The collection did not exist and was created.</param>
    /// <param name="CollectionUpdated">The collection existed and its period gap was changed.</param>
    /// <param name="Added">Records new to the collection.</param>
    /// <param name="Updated">Records whose stored entry, periods or items differed and were rewritten in place.</param>
    /// <param name="Unchanged">Records identical to what was stored.</param>
    /// <param name="MissingFromFile">Entries in the collection with no record in this file. Kept, not deleted.</param>
    public sealed record PsnImportResult(
        bool CollectionCreated,
        bool CollectionUpdated,
        int Added,
        int Updated,
        int Unchanged,
        int MissingFromFile)
    {
        /// <summary>Everything that was written: 0 for a re-import of an identical file.</summary>
        public int Changes => (CollectionCreated ? 1 : 0) + (CollectionUpdated ? 1 : 0) + Added + Updated;
    }

    /// <summary>
    /// Writes a read PSN file into an Imported collection of the memories file, idempotently, in
    /// one transaction.
    /// </summary>
    /// <remarks>
    /// An entry is matched by <c>SourceKey</c> within the collection and updated in place (its
    /// <c>EntryID</c> never changes, so links to it stay valid). Its periods and items are replaced
    /// only when anything differs; the unused <c>EntryItem.Name</c> / <c>IconSha256</c> columns are
    /// carried over by (role, set, item id). Trophy names and icons live in <c>ItemDefinition</c>
    /// (CH-43), which this import never touches. The image hash is kept while the image source URL is
    /// unchanged. Entries missing from the file are counted, never deleted.
    /// </remarks>
    public sealed class PsnImporter
    {
        /// <summary>The default collection name.</summary>
        public const string DefaultCollectionName = "Shared gaming";

        /// <summary>The source line stored on each entry.</summary>
        public const string EntrySource = "PSN trophies";

        /// <summary>The collection's source description.</summary>
        public const string CollectionSourceDescription = "PSN shared activity (" + PsnSharedActivityReader.Schema + ")";

        private const char Separator = '\u001f';

        /// <summary>Test hook: called after each record is written, inside the transaction.</summary>
        internal Action<int>? AfterRecordWritten { get; set; }

        /// <summary>Imports <paramref name="activity"/> into the collection named <paramref name="collectionName"/>.</summary>
        public PsnImportResult Import(string memoriesPath, PsnActivity activity, string collectionName = DefaultCollectionName)
        {
            ArgumentNullException.ThrowIfNull(activity);
            ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

            using var connection = MemoriesDatabase.OpenConnection(memoriesPath);
            using var transaction = connection.BeginTransaction();

            var inFile = activity.Records.Select(record => record.SourceKey).ToHashSet(StringComparer.Ordinal);
            var (collectionId, created, updated, stored) =
                EnsureCollection(connection, transaction, collectionName, activity.PeriodGapMinutes, inFile);
            var added = 0;
            var changed = 0;
            var unchanged = 0;

            for (var index = 0; index < activity.Records.Count; index++)
            {
                var record = activity.Records[index];

                if (stored.TryGetValue(record.SourceKey, out var entryId))
                {
                    if (ReadCanonical(connection, transaction, entryId).SequenceEqual(Canonical(record), StringComparer.Ordinal))
                    {
                        unchanged++;
                    }
                    else
                    {
                        UpdateEntry(connection, transaction, entryId, record);
                        changed++;
                    }
                }
                else
                {
                    InsertEntry(connection, transaction, collectionId, record);
                    added++;
                }

                AfterRecordWritten?.Invoke(index);
            }

            var missing = stored.Keys.Count(key => !inFile.Contains(key));

            transaction.Commit();

            return new PsnImportResult(created, updated, added, changed, unchanged, missing);
        }

        /// <summary>
        /// Finds or creates the collection, before anything is written. Refuses a collection of
        /// that name holding another source, and refuses a gap change while any stored entry is
        /// missing from the file (its periods would keep the old gap under a collection stating
        /// the new one).
        /// </summary>
        private static (long Id, bool Created, bool Updated, Dictionary<string, long> Stored) EnsureCollection(
            SqliteConnection connection, SqliteTransaction transaction, string name, int gapMinutes, HashSet<string> inFile)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT CollectionID, PeriodGapMinutes, SourceDescription FROM Collection WHERE Kind = $kind AND Name = $name;";
            command.Parameters.AddWithValue("$kind", MemoriesDatabase.KindImported);
            command.Parameters.AddWithValue("$name", name);

            long? existingId = null;
            long storedGap = 0;
            string? storedSource = null;

            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    existingId = reader.GetInt64(0);
                    storedGap = reader.GetInt64(1);
                    storedSource = reader.IsDBNull(2) ? null : reader.GetString(2);
                }
            }

            if (existingId is { } id)
            {
                if (!string.Equals(storedSource, CollectionSourceDescription, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "An imported collection of that name already holds another source. Nothing has been written.");
                }

                var stored = ReadStoredKeys(connection, transaction, id);

                if (storedGap == gapMinutes)
                {
                    return (id, false, false, stored);
                }

                var missing = stored.Keys.Count(key => !inFile.Contains(key));

                if (missing > 0)
                {
                    throw new InvalidOperationException(
                        $"The period gap differs from the collection's, and {missing} stored entries are missing from " +
                        "this file, so their periods could not be recomputed. Re-import the full file to change the " +
                        "gap. Nothing has been written.");
                }

                command.CommandText = "UPDATE Collection SET PeriodGapMinutes = $gap WHERE CollectionID = $id;";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$gap", gapMinutes);
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
                return (id, false, true, stored);
            }

            command.CommandText =
                """
                INSERT INTO Collection (Kind, Name, SourceDescription, ShownByDefault, PeriodGapMinutes)
                VALUES ($kind, $name, $description, 0, $gap)
                RETURNING CollectionID;
                """;
            command.Parameters.AddWithValue("$description", CollectionSourceDescription);
            command.Parameters.AddWithValue("$gap", gapMinutes);

            var newId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            return (newId, true, false, new Dictionary<string, long>(StringComparer.Ordinal));
        }

        private static Dictionary<string, long> ReadStoredKeys(SqliteConnection connection, SqliteTransaction transaction, long collectionId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT SourceKey, EntryID FROM Entry WHERE CollectionID = $id AND SourceKey IS NOT NULL;";
            command.Parameters.AddWithValue("$id", collectionId);

            var keys = new Dictionary<string, long>(StringComparer.Ordinal);
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                keys.Add(reader.GetString(0), reader.GetInt64(1));
            }

            return keys;
        }

        /// <summary>The record as sorted text rows, in the same shape <see cref="ReadCanonical"/> reads back.</summary>
        private static List<string> Canonical(PsnRecord record)
        {
            var rows = new List<string>
            {
                Row("E", record.Title, MemoriesDatabase.FormatLocal(record.StartLocal), MemoriesDatabase.FormatLocal(record.EndLocal),
                    MemoriesDatabase.FormatUtc(record.StartUtc), MemoriesDatabase.FormatUtc(record.EndUtc),
                    MemoriesDatabase.PrecisionSecond, null, record.ImageSourceUrl, EntrySource, record.DayLevel),
            };

            rows.AddRange(record.Periods.Select(period => Row("P",
                period.Index.ToString(CultureInfo.InvariantCulture),
                MemoriesDatabase.FormatUtc(period.StartUtc), MemoriesDatabase.FormatUtc(period.EndUtc),
                MemoriesDatabase.FormatLocal(period.StartLocal), MemoriesDatabase.FormatLocal(period.EndLocal),
                period.Label,
                period.LocalItemCount.ToString(CultureInfo.InvariantCulture),
                period.OtherItemCount.ToString(CultureInfo.InvariantCulture))));

            rows.AddRange(record.Items.Select(item => Row("I",
                item.PeriodIndex.ToString(CultureInfo.InvariantCulture), item.Role, item.SetId, item.ItemId, item.ItemType,
                MemoriesDatabase.FormatUtc(item.TimeUtc), MemoriesDatabase.FormatLocal(item.TimeLocal))));

            rows.Sort(StringComparer.Ordinal);
            return rows;
        }

        private static List<string> ReadCanonical(SqliteConnection connection, SqliteTransaction transaction, long entryId)
        {
            var rows = new List<string>();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$id", entryId);

            command.CommandText =
                """
                SELECT Title, StartLocal, EndLocal, StartUtc, EndUtc, DatePrecision, Summary, ImageSourceUrl, Source, DayLevel
                FROM Entry WHERE EntryID = $id;
                """;
            ReadRows(command, "E", rows);

            command.CommandText =
                """
                SELECT CAST(PeriodIndex AS TEXT), StartUtc, EndUtc, StartLocal, EndLocal, Label,
                       CAST(LocalItemCount AS TEXT), CAST(OtherItemCount AS TEXT)
                FROM EntryPeriod WHERE EntryID = $id;
                """;
            ReadRows(command, "P", rows);

            command.CommandText =
                """
                SELECT CAST(PeriodIndex AS TEXT), Role, SetId, ItemId, ItemType, TimeUtc, TimeLocal
                FROM EntryItem WHERE EntryID = $id;
                """;
            ReadRows(command, "I", rows);

            rows.Sort(StringComparer.Ordinal);
            return rows;
        }

        private static void ReadRows(SqliteCommand command, string kind, List<string> rows)
        {
            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var values = new string?[reader.FieldCount + 1];
                values[0] = kind;

                for (var i = 0; i < reader.FieldCount; i++)
                {
                    values[i + 1] = reader.IsDBNull(i) ? null : reader.GetString(i);
                }

                rows.Add(Row(values));
            }
        }

        // Null is written as a lone \u0000 so it never equals an empty string.
        private static string Row(params string?[] values) =>
            string.Join(Separator, values.Select(value => value ?? "\0"));

        private static void InsertEntry(SqliteConnection connection, SqliteTransaction transaction, long collectionId, PsnRecord record)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO Entry (CollectionID, SourceKey, Title, StartLocal, EndLocal, StartUtc, EndUtc,
                                   DatePrecision, Summary, ImageSha256, ImageSourceUrl, Source, DayLevel)
                VALUES ($collection, $key, $title, $startLocal, $endLocal, $startUtc, $endUtc,
                        $precision, NULL, NULL, $url, $source, $level)
                RETURNING EntryID;
                """;
            command.Parameters.AddWithValue("$collection", collectionId);
            command.Parameters.AddWithValue("$key", record.SourceKey);
            AddEntryParameters(command, record);

            var entryId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            InsertPeriodsAndItems(connection, transaction, entryId, record, new Dictionary<(string, string, string), (object, object)>());
        }

        private static void UpdateEntry(SqliteConnection connection, SqliteTransaction transaction, long entryId, PsnRecord record)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$id", entryId);

            // Enrichment (names, icons) survives a rewrite of the items.
            var enrichment = new Dictionary<(string, string, string), (object, object)>();
            command.CommandText = "SELECT Role, SetId, ItemId, Name, IconSha256 FROM EntryItem WHERE EntryID = $id;";

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    enrichment[(reader.GetString(0), reader.GetString(1), reader.GetString(2))] = (reader.GetValue(3), reader.GetValue(4));
                }
            }

            command.CommandText = "DELETE FROM EntryItem WHERE EntryID = $id; DELETE FROM EntryPeriod WHERE EntryID = $id;";
            command.ExecuteNonQuery();

            command.CommandText =
                """
                UPDATE Entry
                SET Title = $title, StartLocal = $startLocal, EndLocal = $endLocal, StartUtc = $startUtc, EndUtc = $endUtc,
                    DatePrecision = $precision, Summary = NULL, Source = $source, DayLevel = $level,
                    ImageSha256 = CASE WHEN ImageSourceUrl IS $url THEN ImageSha256 ELSE NULL END,
                    ImageSourceUrl = $url
                WHERE EntryID = $id;
                """;
            AddEntryParameters(command, record);
            command.ExecuteNonQuery();

            InsertPeriodsAndItems(connection, transaction, entryId, record, enrichment);
        }

        private static void AddEntryParameters(SqliteCommand command, PsnRecord record)
        {
            command.Parameters.AddWithValue("$title", record.Title);
            command.Parameters.AddWithValue("$startLocal", MemoriesDatabase.FormatLocal(record.StartLocal));
            command.Parameters.AddWithValue("$endLocal", MemoriesDatabase.FormatLocal(record.EndLocal));
            command.Parameters.AddWithValue("$startUtc", MemoriesDatabase.FormatUtc(record.StartUtc));
            command.Parameters.AddWithValue("$endUtc", MemoriesDatabase.FormatUtc(record.EndUtc));
            command.Parameters.AddWithValue("$precision", MemoriesDatabase.PrecisionSecond);
            command.Parameters.AddWithValue("$url", (object?)record.ImageSourceUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$source", EntrySource);
            command.Parameters.AddWithValue("$level", record.DayLevel);
        }

        private static void InsertPeriodsAndItems(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long entryId,
            PsnRecord record,
            Dictionary<(string, string, string), (object Name, object Icon)> enrichment)
        {
            using var period = connection.CreateCommand();
            period.Transaction = transaction;
            period.CommandText =
                """
                INSERT INTO EntryPeriod (EntryID, PeriodIndex, StartUtc, EndUtc, StartLocal, EndLocal, Label, LocalItemCount, OtherItemCount)
                VALUES ($entry, $index, $startUtc, $endUtc, $startLocal, $endLocal, $label, $local, $other);
                """;

            foreach (var p in record.Periods)
            {
                period.Parameters.Clear();
                period.Parameters.AddWithValue("$entry", entryId);
                period.Parameters.AddWithValue("$index", p.Index);
                period.Parameters.AddWithValue("$startUtc", MemoriesDatabase.FormatUtc(p.StartUtc));
                period.Parameters.AddWithValue("$endUtc", MemoriesDatabase.FormatUtc(p.EndUtc));
                period.Parameters.AddWithValue("$startLocal", MemoriesDatabase.FormatLocal(p.StartLocal));
                period.Parameters.AddWithValue("$endLocal", MemoriesDatabase.FormatLocal(p.EndLocal));
                period.Parameters.AddWithValue("$label", p.Label);
                period.Parameters.AddWithValue("$local", p.LocalItemCount);
                period.Parameters.AddWithValue("$other", p.OtherItemCount);
                period.ExecuteNonQuery();
            }

            using var item = connection.CreateCommand();
            item.Transaction = transaction;
            item.CommandText =
                """
                INSERT INTO EntryItem (EntryID, PeriodIndex, Role, SetId, ItemId, ItemType, TimeUtc, TimeLocal, Name, IconSha256)
                VALUES ($entry, $period, $role, $set, $item, $type, $utc, $local, $name, $icon);
                """;

            foreach (var i in record.Items)
            {
                var (name, icon) = enrichment.TryGetValue((i.Role, i.SetId, i.ItemId), out var kept)
                    ? kept
                    : (DBNull.Value, DBNull.Value);

                item.Parameters.Clear();
                item.Parameters.AddWithValue("$entry", entryId);
                item.Parameters.AddWithValue("$period", i.PeriodIndex);
                item.Parameters.AddWithValue("$role", i.Role);
                item.Parameters.AddWithValue("$set", i.SetId);
                item.Parameters.AddWithValue("$item", i.ItemId);
                item.Parameters.AddWithValue("$type", i.ItemType);
                item.Parameters.AddWithValue("$utc", MemoriesDatabase.FormatUtc(i.TimeUtc));
                item.Parameters.AddWithValue("$local", MemoriesDatabase.FormatLocal(i.TimeLocal));
                item.Parameters.AddWithValue("$name", name);
                item.Parameters.AddWithValue("$icon", icon);
                item.ExecuteNonQuery();
            }
        }
    }
}
