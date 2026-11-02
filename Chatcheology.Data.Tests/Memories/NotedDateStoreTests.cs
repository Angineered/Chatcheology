using Chatcheology.Archive;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Personal;
using Chatcheology.Data.Memories.Psn;
using Chatcheology.Tests.Memories;
using Microsoft.Data.Sqlite;
using static Chatcheology.Tests.Memories.SyntheticPsnFile;

namespace Chatcheology.Data.Tests.Memories
{
    /// <summary>
    /// CH-47 in the memories file: format 4 and its upgrade, the noted-date store, and the shared field
    /// rules. Invented titles and dates only.
    /// </summary>
    public sealed class NotedDateStoreTests : IDisposable
    {
        private static readonly DateTime Now = MemoriesTestFolder.Now;

        private readonly MemoriesTestFolder _folder = new();
        private readonly NotedDateStore _store;

        public NotedDateStoreTests()
        {
            _store = new NotedDateStore(_folder.MemoriesPath);
        }

        public void Dispose() => _folder.Dispose();

        private static NotedDateDraft Draft(string title = "Invented anniversary", int month = 2, int day = 14, int? year = 2019, string? note = null) =>
            new() { Title = title, Month = month, Day = day, Year = year, Note = note };

        // ---------------------------------------------------------------------------------------
        // Format 4.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ANewFile_IsTheCurrentFormat_WithTheNotedDateTable()
        {
            _folder.CreateMemories();

            Assert.Equal(MemoriesDatabase.CurrentFormatVersion, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'NotedDate';"));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void AnOlderFile_OpensReadOnly_WithNoNotedDates_AndIsUpgradedOnTheFirstWrite(int version)
        {
            CreateOlder(version);
            Execute(
                """
                INSERT INTO Collection VALUES (5, 'Personal', 'Holidays', NULL, 1, NULL);
                INSERT INTO Entry (EntryID, CollectionID, SourceKey, Title, StartLocal, EndLocal, StartUtc, EndUtc,
                                   DatePrecision, Summary, ImageSha256, ImageSourceUrl, Source, DayLevel)
                VALUES (7, 5, NULL, 'Synthetic trip', '2018-06-01T00:00:00', NULL, NULL, NULL, 'Day', NULL, NULL, NULL, 'Synthetic note', NULL);
                """);
            var tables = MemoriesDatabase.VersionOneTableNames.Where(t => t != "MemoriesInfo").ToArray();
            var before = DumpRows(tables);

            // Read-only: works, lists nothing, writes nothing.
            Assert.Empty(_store.List());
            Assert.Equal(version, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'NotedDate';"));

            using (var connection = MemoriesDatabase.OpenReadOnlyConnection(_folder.MemoriesPath))
            {
                Assert.Equal(version, MemoriesDatabase.ReadFormatVersion(connection));
            }

            SqliteConnection.ClearAllPools();

            // The first write upgrades, in the same transaction, keeping every row.
            var id = _store.Add(Draft(), false, Now);

            Assert.Equal(MemoriesDatabase.CurrentFormatVersion, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'NotedDate';"));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'EntryImage';"));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'ItemDefinition';"));
            Assert.Equal(before, DumpRows(tables));
            Assert.Equal(["2018-01-01T00:00:00Z"], _folder.Strings("SELECT CreatedDateTimeUtc FROM MemoriesInfo;"));
            Assert.Equal([id], _store.List().Select(d => d.NotedDateID));
        }

        [Fact]
        public void AFailedFirstWrite_LeavesAnOlderFileAsItWas()
        {
            CreateOlder(3);
            _store.BeforeCommit = () => throw new InvalidOperationException("Synthetic failure.");

            Assert.Throws<InvalidOperationException>(() => _store.Add(Draft(), false, Now));

            Assert.Equal(3, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'NotedDate';"));
        }

        [Fact]
        public void AFormatFourMarkerWithoutTheNotedDateTable_IsRefused()
        {
            MemoriesDatabase.CreateFormatVersionFour(_folder.MemoriesPath, Now);
            Execute("DROP TABLE NotedDate;");

            Assert.Throws<InvalidOperationException>(() => MemoriesDatabase.OpenConnection(_folder.MemoriesPath));
            Assert.Throws<InvalidOperationException>(() => MemoriesDatabase.OpenReadOnlyConnection(_folder.MemoriesPath));
        }

        [Fact]
        public void AFormatThreeMarkerWithTheNotedDateTable_IsRefused()
        {
            CreateOlder(3);
            Execute("CREATE TABLE NotedDate (NotedDateID INTEGER PRIMARY KEY);");

            Assert.Throws<InvalidOperationException>(() => MemoriesDatabase.OpenReadOnlyConnection(_folder.MemoriesPath));
        }

        [Fact]
        public void APsnReImportAndEnrichment_OnAFormatFourFile_KeepTheNotedDates()
        {
            var path = PsnTrophyEnricherTests.ImportStandard(_folder);
            _store.Add(Draft(), false, Now);
            _store.Add(Draft("Leap day", 2, 29, null, "An invented note"), false, Now);
            var rows = Rows();

            var file = Standard();
            new PsnImporter().Import(path, PsnSharedActivityReader.Parse(file.ToBytes(), LocalPrefix, OtherPrefix, file.PairAPrefix));
            PsnTrophyEnricher.Enrich(path, PsnTrophyDefinitionsReader.Parse(SyntheticTrophyDefinitions.Standard().ToBytes()));
            SqliteConnection.ClearAllPools();

            Assert.Equal(rows, Rows());
            Assert.Equal(2, rows.Count);
            Assert.Equal(MemoriesDatabase.CurrentFormatVersion, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
        }

        // ---------------------------------------------------------------------------------------
        // The store.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TheFirstDate_CreatesTheFileOnlyWhenAsked()
        {
            Assert.False(_store.FileExists);
            Assert.Empty(_store.List());

            Assert.Throws<FileNotFoundException>(() => _store.Add(Draft(), false, Now));
            Assert.False(File.Exists(_folder.MemoriesPath));

            // A refused draft creates nothing either, even when creating was confirmed.
            Assert.Throws<PersonalMemoryValidationException>(() => _store.Add(Draft(title: " "), true, Now));
            Assert.False(File.Exists(_folder.MemoriesPath));

            var id = _store.Add(Draft(), true, Now);

            Assert.True(_store.FileExists);
            Assert.Equal(MemoriesDatabase.CurrentFormatVersion, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(new NotedDate(id, "Invented anniversary", 2, 14, 2019, null), _store.List().Single());

            // Nothing else is written: no collection, entry or link.
            Assert.Equal(0, _folder.Scalar("SELECT (SELECT COUNT(*) FROM Collection) + (SELECT COUNT(*) FROM Entry) + (SELECT COUNT(*) FROM EntryLink);"));
        }

        [Fact]
        public void AFailedFirstSave_RemovesTheNewFile()
        {
            _store.BeforeCommit = () => throw new InvalidOperationException("Synthetic failure.");

            Assert.Throws<InvalidOperationException>(() => _store.Add(Draft(), true, Now));
            Assert.False(File.Exists(_folder.MemoriesPath));
        }

        [Fact]
        public void TitleAndNote_AreTrimmed_AndABlankNoteIsNone()
        {
            _folder.CreateMemories();

            var first = _store.Add(Draft(title: "  Invented first date  ", note: "   "), false, Now);
            var second = _store.Add(Draft(title: "Invented birthday", month: 1, day: 3, year: null, note: "  An invented note \n"), false, Now);

            Assert.Equal(
                [new NotedDate(second, "Invented birthday", 1, 3, null, "An invented note"), new NotedDate(first, "Invented first date", 2, 14, 2019, null)],
                _store.List());
        }

        [Fact]
        public void List_IsOrderedByMonthDayAndTitle()
        {
            _folder.CreateMemories();

            _store.Add(Draft("b", 12, 31, null), false, Now);
            _store.Add(Draft("B", 1, 1, null), false, Now);
            _store.Add(Draft("a", 1, 1, null), false, Now);
            _store.Add(Draft("c", 2, 29, null), false, Now);

            Assert.Equal(["a", "B", "c", "b"], _store.List().Select(d => d.Title));
        }

        [Fact]
        public void Update_SavesInPlace_AndDelete_RemovesOnlyThatDate()
        {
            _folder.CreateMemories();
            var id = _store.Add(Draft(), false, Now);
            var other = _store.Add(Draft("Invented other", 5, 5, null), false, Now);

            _store.Update(id, Draft("Invented renamed", 3, 31, null, "An invented note"), Now);

            Assert.Equal(new NotedDate(id, "Invented renamed", 3, 31, null, "An invented note"), _store.List().Single(d => d.NotedDateID == id));

            _store.Delete(id);

            Assert.Equal([other], _store.List().Select(d => d.NotedDateID));

            var gone = Assert.Throws<PersonalMemoryValidationException>(() => _store.Delete(id));
            Assert.Contains(PersonalFields.NotedDate, gone.Errors.Keys);
            Assert.Throws<PersonalMemoryValidationException>(() => _store.Update(id, Draft(), Now));
            Assert.Equal([other], _store.List().Select(d => d.NotedDateID));
        }

        [Fact]
        public void UpdateAndDelete_WithoutAFile_CreateNothing()
        {
            Assert.Throws<FileNotFoundException>(() => _store.Update(1, Draft(), Now));
            Assert.Throws<FileNotFoundException>(() => _store.Delete(1));
            Assert.False(File.Exists(_folder.MemoriesPath));
        }

        [Fact]
        public void AnInvalidUpdate_WritesNothing()
        {
            _folder.CreateMemories();
            var id = _store.Add(Draft(), false, Now);

            var refusal = Assert.Throws<PersonalMemoryValidationException>(() => _store.Update(id, Draft(day: 30), Now));

            Assert.Equal([PersonalFields.Date], refusal.Errors.Keys);
            Assert.Equal(new NotedDate(id, "Invented anniversary", 2, 14, 2019, null), _store.List().Single());
        }

        // ---------------------------------------------------------------------------------------
        // Field rules (shared with the archive writer and validator).
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData(2, 0, null, "Date")]
        [InlineData(4, 31, null, "Date")]
        [InlineData(0, 1, null, "Date")]
        [InlineData(13, 1, null, "Date")]
        [InlineData(2, 30, null, "Date")]
        [InlineData(2, 29, 2019, "Date")]
        [InlineData(2, 29, 1900, "Date")]
        [InlineData(5, 1, 2027, "Year")]
        [InlineData(5, 1, 1899, "Year")]
        public void ADateBreakingARule_IsRefused_ByField(int month, int day, int? year, string field)
        {
            _folder.CreateMemories();

            var refusal = Assert.Throws<PersonalMemoryValidationException>(() => _store.Add(Draft(month: month, day: day, year: year), false, Now));

            Assert.Equal([field], refusal.Errors.Keys);
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM NotedDate;"));
        }

        [Theory]
        [InlineData(2, 29, null)]
        [InlineData(2, 29, 2020)]
        [InlineData(2, 29, 2000)]
        [InlineData(4, 30, null)]
        [InlineData(12, 31, 1900)]
        [InlineData(1, 1, 2026)]
        public void AValidDate_IsSaved(int month, int day, int? year)
        {
            _folder.CreateMemories();

            var id = _store.Add(Draft(month: month, day: day, year: year), false, Now);

            Assert.Equal(new NotedDate(id, "Invented anniversary", month, day, year, null), _store.List().Single());
        }

        [Fact]
        public void TitleAndNoteLengths_AreChecked()
        {
            _folder.CreateMemories();

            Assert.Equal([PersonalFields.Title], Refused(Draft(title: "")));
            Assert.Equal([PersonalFields.Title], Refused(Draft(title: "   ")));
            Assert.Equal([PersonalFields.Title], Refused(Draft(title: new string('t', 81))));
            Assert.Equal([PersonalFields.Note], Refused(Draft(note: new string('n', 501))));
            Assert.Equal(
                [PersonalFields.Title, PersonalFields.Date, PersonalFields.Year, PersonalFields.Note],
                Refused(Draft(title: "", month: 4, day: 31, year: 3000, note: new string('n', 501))));

            var id = _store.Add(Draft(title: new string('t', 80), note: new string('n', 500)), false, Now);

            var saved = _store.List().Single(d => d.NotedDateID == id);
            Assert.Equal(80, saved.Title.Length);
            Assert.Equal(500, saved.Note!.Length);
        }

        [Fact]
        public void TheRules_AreTheArchivesRules()
        {
            Assert.Equal(80, NotedDateRules.MaxTitleLength);
            Assert.Equal(500, NotedDateRules.MaxNoteLength);
            Assert.Equal(1900, NotedDateRules.MinYear);

            // The store's validation is NotedDateRules', field by field.
            var draft = Draft(title: "", month: 2, day: 29, year: 2019, note: "  ");
            Assert.Equal(
                NotedDateRules.Validate("", 2, 29, 2019, null, 2026).Values,
                NotedDateStore.Validate(draft, Now).Values);

            // The latest year is the current local year.
            Assert.Empty(NotedDateStore.Validate(Draft(year: 2026), Now));
            Assert.NotEmpty(NotedDateStore.Validate(Draft(year: 2027), Now));
            Assert.Throws<ArgumentException>(() => NotedDateStore.Validate(Draft(), DateTime.SpecifyKind(Now, DateTimeKind.Unspecified)));
        }

        [Fact]
        public void TheSchema_RefusesARowTheRulesRefuse()
        {
            _folder.CreateMemories();

            foreach (var values in new[]
                     {
                         "(1, ' ', 1, 1, NULL, NULL)",
                         $"(1, '{new string('t', 81)}', 1, 1, NULL, NULL)",
                         "(1, 'x', 0, 1, NULL, NULL)",
                         "(1, 'x', 2, 0, NULL, NULL)",
                         "(1, 'x', 4, 31, NULL, NULL)",
                         "(1, 'x', 2, 29, 2019, NULL)",
                         "(1, 'x', 2, 29, 1900, NULL)",
                         "(1, 'x', 1, 1, 1899, NULL)",
                         "(1, 'x', 1, 1, NULL, ' ')",
                         $"(1, 'x', 1, 1, NULL, '{new string('n', 501)}')",
                         "(1, 'x', 'one', 1, NULL, NULL)",
                     })
            {
                Assert.Throws<SqliteException>(() => Execute("INSERT INTO NotedDate VALUES " + values + ";"));
            }

            Execute("INSERT INTO NotedDate VALUES (1, 'x', 2, 29, 2000, NULL), (2, 'y', 2, 29, NULL, 'z');");
            Assert.Equal(2, _folder.Scalar("SELECT COUNT(*) FROM NotedDate;"));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private List<string> Refused(NotedDateDraft draft) =>
            Assert.Throws<PersonalMemoryValidationException>(() => _store.Add(draft, false, Now)).Errors.Keys.ToList();

        private List<string> Rows() =>
            _folder.Strings("SELECT NotedDateID || '|' || Title || '|' || Month || '|' || Day || '|' || ifnull(Year, '-') || '|' || ifnull(Note, '-') FROM NotedDate ORDER BY NotedDateID;");

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
                default:
                    MemoriesDatabase.CreateFormatVersionThree(_folder.MemoriesPath, created);
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

        private List<string> DumpRows(string[] tables)
        {
            var rows = new List<string>();

            foreach (var table in tables)
            {
                using var connection = new SqliteConnection($"Data Source={_folder.MemoriesPath};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM {table} ORDER BY 1;";
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    var values = Enumerable.Range(0, reader.FieldCount)
                        .Where(i => reader.GetName(i) != "EveryYear") // CH-53: format 6 adds it; checked on its own
                        .Select(i => reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture));
                    rows.Add(table + "|" + string.Join("|", values));
                }
            }

            SqliteConnection.ClearAllPools();

            return rows;
        }
    }
}
