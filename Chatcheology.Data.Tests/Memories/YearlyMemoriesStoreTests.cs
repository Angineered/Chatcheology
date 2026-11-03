using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Personal;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Memories
{
    /// <summary>
    /// CH-53 in the memories file: format 6 (<c>Entry.EveryYear</c>) and its upgrade, the trigger, the marker check,
    /// and the store's Every year field. Invented titles and dates only.
    /// </summary>
    public sealed class YearlyMemoriesStoreTests : IDisposable
    {
        private static readonly DateTime Now = MemoriesTestFolder.Now;

        private readonly MemoriesTestFolder _folder = new();
        private readonly PersonalMemoryStore _store;

        public YearlyMemoriesStoreTests()
        {
            _store = new PersonalMemoryStore(_folder.MemoriesPath, new PersonalMemoryStoreTests.FakeMessages(1, 2, 3));
        }

        public void Dispose() => _folder.Dispose();

        private static PersonalEntryDraft Draft(
            long collection, bool everyYear = true, PersonalDatePrecision precision = PersonalDatePrecision.Day, DateOnly? end = null) => new()
            {
                CollectionID = collection,
                Title = "Invented anniversary",
                Start = new DateOnly(2016, 3, 14),
                End = end,
                Precision = precision,
                Source = "My own memory",
                EveryYear = everyYear,
            };

        // ---------------------------------------------------------------------------------------
        // Format 6 and the upgrade.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ANewFile_IsFormatSix_WithTheColumnAndBothTriggers()
        {
            _folder.CreateMemories();

            Assert.Equal(6, MemoriesDatabase.CurrentFormatVersion);
            Assert.Equal(6, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM pragma_table_info('Entry') WHERE name = 'EveryYear' AND \"notnull\" = 1 AND dflt_value = '0';"));
            Assert.Equal(
                ["Entry_EveryYear_Insert", "Entry_EveryYear_Update"],
                _folder.Strings("SELECT name FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'Entry' ORDER BY name;"));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void AnOlderFile_OpensReadOnly_WithNoYearlyMemory_AndIsUpgradedOnTheFirstWrite(int version)
        {
            CreateOlder(version);
            Execute(
                """
                INSERT INTO Collection VALUES (5, 'Personal', 'Holidays', NULL, 1, NULL);
                INSERT INTO Entry (EntryID, CollectionID, SourceKey, Title, StartLocal, EndLocal, StartUtc, EndUtc,
                                   DatePrecision, Summary, ImageSha256, ImageSourceUrl, Source, DayLevel)
                VALUES (7, 5, NULL, 'Synthetic trip', '2018-06-01T00:00:00', NULL, NULL, NULL, 'Day', NULL, NULL, NULL, 'Synthetic note', NULL);
                """);

            // Read-only: works, nothing is yearly, nothing is written.
            Assert.False(Assert.Single(Assert.Single(_store.ReadOverview().Personal).Entries).EveryYear);
            Assert.False(_store.ReadEntry(7).EveryYear);
            Assert.Equal(version, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM pragma_table_info('Entry') WHERE name = 'EveryYear';"));

            // The first write upgrades, keeping the row, which reads as not yearly.
            var id = _store.AddEntry(Draft(5));

            Assert.Equal(6, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(["0"], _folder.Strings("SELECT EveryYear FROM Entry WHERE EntryID = 7;"));
            Assert.Equal(["Synthetic trip|2018-06-01T00:00:00|Synthetic note"], _folder.Strings("SELECT Title || '|' || StartLocal || '|' || Source FROM Entry WHERE EntryID = 7;"));
            Assert.True(_store.ReadEntry(id).EveryYear);
            Assert.False(_store.ReadEntry(7).EveryYear);
        }

        [Fact]
        public void AFailedFirstWrite_LeavesAFormatFiveFileAsItWas()
        {
            CreateOlder(5);
            Execute("INSERT INTO Collection VALUES (5, 'Personal', 'Holidays', NULL, 1, NULL);");
            _store.BeforeCommit = () => throw new InvalidOperationException("Synthetic failure.");

            Assert.Throws<InvalidOperationException>(() => _store.AddEntry(Draft(5)));

            Assert.Equal(5, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM pragma_table_info('Entry') WHERE name = 'EveryYear';"));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM Entry;"));
        }

        [Fact]
        public void AMarkerThatDisagreesWithTheColumn_IsRefused()
        {
            // Format 5 marker with the column.
            CreateOlder(5);
            Execute("ALTER TABLE Entry ADD COLUMN EveryYear INTEGER NOT NULL DEFAULT 0;");

            Assert.Throws<InvalidOperationException>(() => MemoriesDatabase.OpenConnection(_folder.MemoriesPath).Dispose());
            Assert.Throws<InvalidOperationException>(() => MemoriesDatabase.OpenReadOnlyConnection(_folder.MemoriesPath).Dispose());
            SqliteConnection.ClearAllPools();

            // Format 6 marker without it.
            File.Delete(_folder.MemoriesPath);
            _folder.CreateMemories();
            Execute("DROP TRIGGER Entry_EveryYear_Insert; DROP TRIGGER Entry_EveryYear_Update; ALTER TABLE Entry DROP COLUMN EveryYear;");

            var refusal = Assert.Throws<InvalidOperationException>(() => MemoriesDatabase.OpenConnection(_folder.MemoriesPath).Dispose());
            Assert.Contains("do not agree", refusal.Message, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------------
        // The trigger: only a personal Day entry without an end can be yearly.
        // ---------------------------------------------------------------------------------------

        private void Seed()
        {
            _folder.CreateMemories();
            Execute(
                """
                INSERT INTO Collection VALUES (1, 'Imported', 'Shared gaming', 'Synthetic', 0, 60);
                INSERT INTO Collection VALUES (2, 'Personal', 'Holidays', NULL, 1, NULL);
                INSERT INTO Entry (EntryID, CollectionID, SourceKey, Title, StartLocal, DatePrecision, EveryYear)
                VALUES (10, 1, 'key', 'Imported game', '2016-05-01T20:00:00', 'Day', 0);
                INSERT INTO Entry (EntryID, CollectionID, Title, StartLocal, DatePrecision, Source, EveryYear)
                VALUES (20, 2, 'Yearly', '2016-03-14T00:00:00', 'Day', 'Mine', 1);
                INSERT INTO Entry (EntryID, CollectionID, Title, StartLocal, DatePrecision, Source, EveryYear)
                VALUES (21, 2, 'A month', '2016-03-01T00:00:00', 'Month', 'Mine', 0);
                INSERT INTO Entry (EntryID, CollectionID, Title, StartLocal, EndLocal, DatePrecision, Source, EveryYear)
                VALUES (22, 2, 'A week', '2016-03-01T00:00:00', '2016-03-07T00:00:00', 'Day', 'Mine', 0);
                """);
        }

        [Theory]
        [InlineData("INSERT INTO Entry (CollectionID, SourceKey, Title, StartLocal, DatePrecision, EveryYear) VALUES (1, 'other', 'Imported', '2016-05-02T00:00:00', 'Day', 1);")]
        [InlineData("INSERT INTO Entry (CollectionID, Title, StartLocal, DatePrecision, Source, EveryYear) VALUES (2, 'Month', '2016-05-01T00:00:00', 'Month', 'Mine', 1);")]
        [InlineData("INSERT INTO Entry (CollectionID, Title, StartLocal, EndLocal, DatePrecision, Source, EveryYear) VALUES (2, 'End', '2016-05-01T00:00:00', '2016-05-02T00:00:00', 'Day', 'Mine', 1);")]
        [InlineData("INSERT INTO Entry (CollectionID, Title, StartLocal, DatePrecision, Source, EveryYear) VALUES (2, 'Second', '2016-05-01T10:00:00', 'Second', 'Mine', 1);")]
        [InlineData("UPDATE Entry SET EveryYear = 1 WHERE EntryID = 10;")]
        [InlineData("UPDATE Entry SET EveryYear = 1 WHERE EntryID = 21;")]
        [InlineData("UPDATE Entry SET EveryYear = 1 WHERE EntryID = 22;")]
        [InlineData("UPDATE Entry SET DatePrecision = 'Month', StartLocal = '2016-03-01T00:00:00' WHERE EntryID = 20;")]
        [InlineData("UPDATE Entry SET EndLocal = '2016-03-15T00:00:00' WHERE EntryID = 20;")]
        [InlineData("UPDATE Entry SET CollectionID = 1, SourceKey = 'moved' WHERE EntryID = 20;")]
        [InlineData("UPDATE Entry SET EveryYear = 2 WHERE EntryID = 21;")]
        public void TheTriggerAndCheck_RefuseAYearlyEntryThatBreaksTheRule(string sql)
        {
            Seed();
            var before = _folder.Strings("SELECT EntryID || '|' || CollectionID || '|' || DatePrecision || '|' || ifnull(EndLocal, '') || '|' || EveryYear FROM Entry ORDER BY EntryID;");

            Assert.Throws<SqliteException>(() => Execute(sql));

            Assert.Equal(before, _folder.Strings("SELECT EntryID || '|' || CollectionID || '|' || DatePrecision || '|' || ifnull(EndLocal, '') || '|' || EveryYear FROM Entry ORDER BY EntryID;"));
        }

        [Fact]
        public void TheTrigger_AllowsEveryChangeThatKeepsTheRule()
        {
            Seed();

            Execute(
                """
                UPDATE Entry SET Title = 'Renamed', Summary = 'Note' WHERE EntryID = 20;
                UPDATE Entry SET EveryYear = 0, DatePrecision = 'Month', StartLocal = '2016-03-01T00:00:00' WHERE EntryID = 20;
                UPDATE Entry SET EndLocal = NULL, EveryYear = 1 WHERE EntryID = 22;
                UPDATE Entry SET ImageSourceUrl = 'https://images.example.org/x.png' WHERE EntryID = 10;
                """);

            Assert.Equal(["10|0", "20|0", "21|0", "22|1"], _folder.Strings("SELECT EntryID || '|' || EveryYear FROM Entry ORDER BY EntryID;"));
        }

        // ---------------------------------------------------------------------------------------
        // The store.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AYearlyMemory_IsSavedAndReadBack_AndCanBeSwitchedOff()
        {
            var collection = _store.CreateCollection("Holidays", true, Now);

            var id = _store.AddEntry(Draft(collection));

            Assert.True(_store.ReadEntry(id).EveryYear);
            Assert.True(Assert.Single(Assert.Single(_store.ReadOverview().Personal).Entries).EveryYear);
            Assert.Equal(["1"], _folder.Strings($"SELECT EveryYear FROM Entry WHERE EntryID = {id};"));

            _store.UpdateEntry(id, Draft(collection, everyYear: false, precision: PersonalDatePrecision.Month));

            Assert.False(_store.ReadEntry(id).EveryYear);
            Assert.Equal(["0|Month"], _folder.Strings($"SELECT EveryYear || '|' || DatePrecision FROM Entry WHERE EntryID = {id};"));
        }

        [Fact]
        public void AWrongCombination_IsAFieldError_BeforeAnythingIsWritten()
        {
            var collection = _store.CreateCollection("Holidays", true, Now);
            var id = _store.AddEntry(Draft(collection));

            var month = Assert.Throws<PersonalMemoryValidationException>(() => _store.AddEntry(Draft(collection, precision: PersonalDatePrecision.Month)));
            Assert.Equal("Every year needs a memory dated to the day, not a month.", month.Errors[PersonalFields.EveryYear]);

            var end = Assert.Throws<PersonalMemoryValidationException>(() => _store.AddEntry(Draft(collection, end: new DateOnly(2016, 3, 20))));
            Assert.Equal("Every year is only for a memory without an end date.", end.Errors[PersonalFields.EveryYear]);

            // Changing a yearly memory to a month while it stays yearly: refused, the row is unchanged.
            var update = Assert.Throws<PersonalMemoryValidationException>(() => _store.UpdateEntry(id, Draft(collection, precision: PersonalDatePrecision.Month)));
            Assert.True(update.Errors.ContainsKey(PersonalFields.EveryYear));

            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM Entry;"));
            Assert.Equal(["Day|1"], _folder.Strings($"SELECT DatePrecision || '|' || EveryYear FROM Entry WHERE EntryID = {id};"));
            Assert.Null(PersonalMemoryRules.EveryYearError(false, PersonalDatePrecision.Month, new DateOnly(2016, 3, 20)));
        }

        // ---------------------------------------------------------------------------------------
        // Plumbing.
        // ---------------------------------------------------------------------------------------

        private void CreateOlder(int version)
        {
            var created = new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            switch (version)
            {
                case 1:
                    MemoriesDatabase.CreateFormatVersionOne(_folder.MemoriesPath, created);
                    break;
                case 2:
                    MemoriesDatabase.CreateFormatVersionTwo(_folder.MemoriesPath, created);
                    break;
                case 3:
                    MemoriesDatabase.CreateFormatVersionThree(_folder.MemoriesPath, created);
                    break;
                case 4:
                    MemoriesDatabase.CreateFormatVersionFour(_folder.MemoriesPath, created);
                    break;
                default:
                    MemoriesDatabase.CreateFormatVersionFive(_folder.MemoriesPath, created);
                    break;
            }
        }

        private void Execute(string sql)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={_folder.MemoriesPath};Foreign Keys=True");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }
    }
}
