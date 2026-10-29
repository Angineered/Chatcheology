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
    /// CH-48 in the memories file: format 5 and its upgrade, the external-link store, the cascades and the
    /// shared rules. Invented addresses only; nothing is fetched.
    /// </summary>
    public sealed class ExternalLinkStoreTests : IDisposable
    {
        private static readonly DateTime Now = MemoriesTestFolder.Now;

        private readonly MemoriesTestFolder _folder = new();
        private readonly ExternalLinkStore _links;
        private readonly PersonalMemoryStore _memories;
        private readonly NotedDateStore _dates;

        public ExternalLinkStoreTests()
        {
            _links = new ExternalLinkStore(_folder.MemoriesPath);
            _memories = new PersonalMemoryStore(_folder.MemoriesPath, new PersonalMemoryStoreTests.FakeMessages(1, 2, 3));
            _dates = new NotedDateStore(_folder.MemoriesPath);
        }

        public void Dispose() => _folder.Dispose();

        private static ExternalLinkDraft Link(string path, string? label = null) =>
            new("https://photos.example.org/" + path, label);

        // ---------------------------------------------------------------------------------------
        // Format 5.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ANewFile_IsFormatFive_WithTheExternalLinkTable()
        {
            _folder.CreateMemories();

            Assert.Equal(5, MemoriesDatabase.CurrentFormatVersion);
            Assert.Equal(5, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ExternalLink';"));
            Assert.Equal(2, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND tbl_name = 'ExternalLink';"));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void AnOlderFile_OpensReadOnly_WithNoLinks_AndIsUpgradedOnTheFirstWrite(int version)
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
            Assert.Empty(_links.ReadForEntry(7));
            Assert.Empty(_links.ReadForNotedDate(1));
            Assert.Equal(version, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'ExternalLink';"));

            using (var connection = MemoriesDatabase.OpenReadOnlyConnection(_folder.MemoriesPath))
            {
                Assert.Equal(version, MemoriesDatabase.ReadFormatVersion(connection));
            }

            SqliteConnection.ClearAllPools();

            // The first write upgrades, in the same transaction, keeping every row.
            _links.ReplaceForEntry(7, [Link("a", "Day 1")]);

            Assert.Equal(5, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'ExternalLink';"));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'NotedDate';"));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'EntryImage';"));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'ItemDefinition';"));
            Assert.Equal(before, DumpRows(tables));
            Assert.Equal(["2018-01-01T00:00:00Z"], _folder.Strings("SELECT CreatedDateTimeUtc FROM MemoriesInfo;"));
            Assert.Equal(["https://photos.example.org/a|Day 1"], _links.ReadForEntry(7).Select(l => l.Url + "|" + l.Label));
        }

        [Fact]
        public void AFormatFourFile_IsUpgradedByANotedDateSave_AndAPersonalMemorySave()
        {
            CreateOlder(4);
            _dates.Add(new NotedDateDraft { Title = "Invented anniversary", Month = 2, Day = 14 }, false, Now);

            Assert.Equal(5, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'ExternalLink';"));

            SqliteConnection.ClearAllPools();
            File.Delete(_folder.MemoriesPath);
            CreateOlder(4);
            _memories.RenameCollection(_memories.CreateCollection("Holidays", false, Now), "Trips");

            Assert.Equal(5, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
        }

        [Fact]
        public void AFailedFirstWrite_LeavesAnOlderFileAsItWas()
        {
            CreateOlder(4);
            Execute("INSERT INTO NotedDate VALUES (1, 'Invented anniversary', 2, 14, NULL, NULL);");
            _links.BeforeCommit = () => throw new InvalidOperationException("Synthetic failure.");

            Assert.Throws<InvalidOperationException>(() => _links.ReplaceForNotedDate(1, [Link("a")]));

            Assert.Equal(4, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'ExternalLink';"));
        }

        [Fact]
        public void AFormatFiveMarkerWithoutTheExternalLinkTable_IsRefused()
        {
            _folder.CreateMemories();
            Execute("DROP TABLE ExternalLink;");

            Assert.Throws<InvalidOperationException>(() => MemoriesDatabase.OpenConnection(_folder.MemoriesPath));
            Assert.Throws<InvalidOperationException>(() => MemoriesDatabase.OpenReadOnlyConnection(_folder.MemoriesPath));
        }

        [Fact]
        public void AFormatFourMarkerWithTheExternalLinkTable_IsRefused()
        {
            CreateOlder(4);
            Execute("CREATE TABLE ExternalLink (ExternalLinkID INTEGER PRIMARY KEY);");

            Assert.Throws<InvalidOperationException>(() => MemoriesDatabase.OpenReadOnlyConnection(_folder.MemoriesPath));
            Assert.Throws<InvalidOperationException>(() => _links.ReadForEntry(1));
        }

        [Fact]
        public void APsnReImportAndEnrichment_KeepTheLinks()
        {
            var path = PsnTrophyEnricherTests.ImportStandard(_folder);
            var entry = PersonalEntry();
            var date = _dates.Add(new NotedDateDraft { Title = "Invented anniversary", Month = 2, Day = 14 }, false, Now);
            _links.ReplaceForEntry(entry, [Link("a", "Day 1"), Link("b")]);
            _links.ReplaceForNotedDate(date, [Link("c")]);
            var rows = Rows();

            var file = Standard();
            new PsnImporter().Import(path, PsnSharedActivityReader.Parse(file.ToBytes(), LocalPrefix, OtherPrefix, file.PairAPrefix));
            PsnTrophyEnricher.Enrich(path, PsnTrophyDefinitionsReader.Parse(SyntheticTrophyDefinitions.Standard().ToBytes()));
            SqliteConnection.ClearAllPools();

            Assert.Equal(3, rows.Count);
            Assert.Equal(rows, Rows());
            Assert.Equal(5, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
        }

        // ---------------------------------------------------------------------------------------
        // The store.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ReplaceAll_KeepsTheGivenOrder_TrimsAndRenumbers()
        {
            _folder.CreateMemories();
            var entry = PersonalEntry();

            _links.ReplaceForEntry(entry, [Link("c", "  Day 3 "), new ExternalLinkDraft("  https://photos.example.org/a \n", "   "), Link("b")]);

            Assert.Equal(
                [(1, "https://photos.example.org/c", (string?)"Day 3"), (2, "https://photos.example.org/a", null), (3, "https://photos.example.org/b", null)],
                _links.ReadForEntry(entry).Select(l => (l.Ordinal, l.Url, l.Label)));
            Assert.All(_links.ReadForEntry(entry), l => Assert.Equal("photos.example.org", l.Host));

            // A new order replaces the whole list; the list is never merged.
            _links.ReplaceForEntry(entry, [Link("b"), Link("c", "Day 3")]);

            Assert.Equal(
                [(1, "https://photos.example.org/b"), (2, "https://photos.example.org/c")],
                _links.ReadForEntry(entry).Select(l => (l.Ordinal, l.Url)));

            // An empty list removes every link.
            _links.ReplaceForEntry(entry, []);

            Assert.Empty(_links.ReadForEntry(entry));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM ExternalLink;"));
        }

        [Fact]
        public void EntryAndNotedDateLists_AreSeparate_EvenWithTheSameIdentifier()
        {
            _folder.CreateMemories();
            Execute(
                """
                INSERT INTO Collection VALUES (5, 'Personal', 'Holidays', NULL, 1, NULL);
                INSERT INTO Entry (EntryID, CollectionID, SourceKey, Title, StartLocal, EndLocal, StartUtc, EndUtc,
                                   DatePrecision, Summary, ImageSha256, ImageSourceUrl, Source, DayLevel)
                VALUES (1, 5, NULL, 'Synthetic trip', '2018-06-01T00:00:00', NULL, NULL, NULL, 'Day', NULL, NULL, NULL, 'Synthetic note', NULL);
                INSERT INTO NotedDate VALUES (1, 'Invented anniversary', 2, 14, NULL, NULL);
                """);

            _links.ReplaceForEntry(1, [Link("entry")]);
            _links.ReplaceForNotedDate(1, [Link("date-1"), Link("date-2")]);

            Assert.Equal(["https://photos.example.org/entry"], _links.ReadForEntry(1).Select(l => l.Url));
            Assert.Equal(["https://photos.example.org/date-1", "https://photos.example.org/date-2"], _links.ReadForNotedDate(1).Select(l => l.Url));

            _links.ReplaceForNotedDate(1, []);

            Assert.Single(_links.ReadForEntry(1));
        }

        [Fact]
        public void ABadLink_IsRefusedByIndex_AndNothingIsWritten()
        {
            _folder.CreateMemories();
            var entry = PersonalEntry();
            _links.ReplaceForEntry(entry, [Link("kept")]);

            var refusal = Assert.Throws<PersonalMemoryValidationException>(() =>
                _links.ReplaceForEntry(entry, [Link("a"), Link("b"), new ExternalLinkDraft("http://photos.example.org/c")]));

            Assert.Equal([PersonalFields.Links], refusal.Errors.Keys);
            Assert.Equal(2, refusal.LinkIndex);
            Assert.StartsWith("Link 3: ", refusal.Errors[PersonalFields.Links], StringComparison.Ordinal);
            Assert.DoesNotContain("photos.example.org", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(["https://photos.example.org/kept"], _links.ReadForEntry(entry).Select(l => l.Url));
        }

        [Theory]
        [InlineData("http://photos.example.org/a", null)]
        [InlineData("javascript:alert(1)", null)]
        [InlineData("file:///C:/synthetic.jpg", null)]
        [InlineData("intent://photos.example.org/#Intent;end", null)]
        [InlineData("content://media/external/images/1", null)]
        [InlineData("/relative/path", null)]
        [InlineData("#fragment", null)]
        [InlineData("https://someone@photos.example.org/a", null)]
        [InlineData("", null)]
        [InlineData("https://photos.example.org/a", "Sixty-one characters for a label is exactly one too many!!!!!")]
        public void EveryRule_IsTheStoresRule(string url, string? label)
        {
            _folder.CreateMemories();
            var entry = PersonalEntry();

            var refusal = Assert.Throws<PersonalMemoryValidationException>(() => _links.ReplaceForEntry(entry, [new ExternalLinkDraft(url, label)]));

            Assert.Equal(0, refusal.LinkIndex);
            Assert.NotNull(ExternalLinkStore.Validate([new ExternalLinkDraft(url, label)]));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM ExternalLink;"));
        }

        [Fact]
        public void LengthsAndCount_AreChecked()
        {
            _folder.CreateMemories();
            var entry = PersonalEntry();
            var longest = "https://photos.example.org/" + new string('x', 2000 - 27);

            Assert.Throws<PersonalMemoryValidationException>(() => _links.ReplaceForEntry(entry, [new ExternalLinkDraft(longest + "x")]));
            Assert.Throws<PersonalMemoryValidationException>(() => _links.ReplaceForEntry(entry, [Link("a", new string('l', 61))]));

            var eleven = Enumerable.Range(1, 11).Select(i => Link("n" + i)).ToList();
            var tooMany = Assert.Throws<PersonalMemoryValidationException>(() => _links.ReplaceForEntry(entry, eleven));

            Assert.Null(tooMany.LinkIndex);
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM ExternalLink;"));

            _links.ReplaceForEntry(entry, [.. eleven.Take(9), new ExternalLinkDraft(longest, new string('l', 60))]);

            var saved = _links.ReadForEntry(entry);
            Assert.Equal(Enumerable.Range(1, 10), saved.Select(l => l.Ordinal));
            Assert.Equal(2000, saved[^1].Url.Length);
            Assert.Equal(60, saved[^1].Label!.Length);
        }

        [Fact]
        public void AnImportedEntry_NeverGetsALink()
        {
            _folder.CreateMemories();
            AddImported();

            var refusal = Assert.Throws<PersonalMemoryValidationException>(() => _links.ReplaceForEntry(90, [Link("a")]));

            Assert.Equal([PersonalFields.Entry], refusal.Errors.Keys);
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM ExternalLink;"));

            // The file refuses it too, whatever writes it.
            Assert.Throws<SqliteException>(() =>
                Execute("INSERT INTO ExternalLink (OwnerKind, OwnerID, Ordinal, Url, Label) VALUES ('Entry', 90, 1, 'https://photos.example.org/a', NULL);"));
        }

        [Fact]
        public void AGoneOwner_IsRefused_AndAMissingFileIsNotCreated()
        {
            Assert.Empty(_links.ReadForEntry(1));
            Assert.Throws<FileNotFoundException>(() => _links.ReplaceForEntry(1, [Link("a")]));
            Assert.False(File.Exists(_folder.MemoriesPath));

            _folder.CreateMemories();

            Assert.Equal([PersonalFields.Entry], Assert.Throws<PersonalMemoryValidationException>(() => _links.ReplaceForEntry(1, [Link("a")])).Errors.Keys);
            Assert.Equal([PersonalFields.NotedDate], Assert.Throws<PersonalMemoryValidationException>(() => _links.ReplaceForNotedDate(1, [Link("a")])).Errors.Keys);
            Assert.Throws<SqliteException>(() =>
                Execute("INSERT INTO ExternalLink (OwnerKind, OwnerID, Ordinal, Url, Label) VALUES ('NotedDate', 1, 1, 'https://photos.example.org/a', NULL);"));
        }

        [Fact]
        public void TheSchema_RefusesARowTheRulesRefuse()
        {
            _folder.CreateMemories();
            Execute("INSERT INTO NotedDate VALUES (1, 'Invented anniversary', 2, 14, NULL, NULL);");

            foreach (var values in new[]
                     {
                         "('NotedDate', 1, 1, 'http://photos.example.org/a', NULL)",
                         "('NotedDate', 1, 1, 'javascript:alert(1)', NULL)",
                         "('NotedDate', 1, 1, 'https://', NULL)",
                         $"('NotedDate', 1, 1, 'https://photos.example.org/{new string('x', 2000 - 26)}', NULL)",
                         "('NotedDate', 1, 1, 'https://photos.example.org/a', '  ')",
                         $"('NotedDate', 1, 1, 'https://photos.example.org/a', '{new string('l', 61)}')",
                         "('NotedDate', 1, 0, 'https://photos.example.org/a', NULL)",
                         "('Album', 1, 1, 'https://photos.example.org/a', NULL)",
                     })
            {
                Assert.Throws<SqliteException>(() => Execute("INSERT INTO ExternalLink (OwnerKind, OwnerID, Ordinal, Url, Label) VALUES " + values + ";"));
            }

            Execute("INSERT INTO ExternalLink (OwnerKind, OwnerID, Ordinal, Url, Label) VALUES ('NotedDate', 1, 1, 'HTTPS://photos.example.org/a', 'x');");

            // One ordinal per owner.
            Assert.Throws<SqliteException>(() =>
                Execute("INSERT INTO ExternalLink (OwnerKind, OwnerID, Ordinal, Url, Label) VALUES ('NotedDate', 1, 1, 'https://photos.example.org/b', NULL);"));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM ExternalLink;"));
        }

        // ---------------------------------------------------------------------------------------
        // Cascades.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void DeletingAMemory_RemovesItsLinks_Only()
        {
            _folder.CreateMemories();
            var collection = _memories.CreateCollection("Holidays", false, Now);
            var first = _memories.AddEntry(PersonalMemoryStoreTests.Draft(collection));
            var second = _memories.AddEntry(PersonalMemoryStoreTests.Draft(collection));
            var date = _dates.Add(new NotedDateDraft { Title = "Invented anniversary", Month = 2, Day = 14 }, false, Now);
            Execute($"UPDATE NotedDate SET NotedDateID = {first} WHERE NotedDateID = {date};");
            _links.ReplaceForEntry(first, [Link("a"), Link("b")]);
            _links.ReplaceForEntry(second, [Link("c")]);
            _links.ReplaceForNotedDate(first, [Link("d")]);

            _memories.DeleteEntry(first);

            Assert.Equal(
                ["Entry|" + second + "|https://photos.example.org/c", "NotedDate|" + first + "|https://photos.example.org/d"],
                Rows().Select(r => string.Join("|", r.Split('|')[1], r.Split('|')[2], r.Split('|')[4])));
        }

        [Fact]
        public void DeletingANotedDate_RemovesItsLinks_Only()
        {
            _folder.CreateMemories();
            var entry = PersonalEntry();
            var date = _dates.Add(new NotedDateDraft { Title = "Invented anniversary", Month = 2, Day = 14 }, false, Now);
            var other = _dates.Add(new NotedDateDraft { Title = "Invented birthday", Month = 3, Day = 1 }, false, Now);
            _links.ReplaceForNotedDate(date, [Link("a"), Link("b")]);
            _links.ReplaceForNotedDate(other, [Link("c")]);
            _links.ReplaceForEntry(entry, [Link("d")]);

            _dates.Delete(date);

            Assert.Empty(_links.ReadForNotedDate(date));
            Assert.Single(_links.ReadForNotedDate(other));
            Assert.Single(_links.ReadForEntry(entry));

            // A refused delete (the date is gone) removes nothing.
            Assert.Throws<PersonalMemoryValidationException>(() => _dates.Delete(date));
            Assert.Equal(2, _folder.Scalar("SELECT COUNT(*) FROM ExternalLink;"));
        }

        [Fact]
        public void DeletingACollection_RemovesTheLinksOfItsMemories_Only()
        {
            _folder.CreateMemories();
            var holidays = _memories.CreateCollection("Holidays", false, Now);
            var trips = _memories.CreateCollection("Trips", false, Now);
            var a = _memories.AddEntry(PersonalMemoryStoreTests.Draft(holidays));
            var b = _memories.AddEntry(PersonalMemoryStoreTests.Draft(holidays));
            var c = _memories.AddEntry(PersonalMemoryStoreTests.Draft(trips));
            var date = _dates.Add(new NotedDateDraft { Title = "Invented anniversary", Month = 2, Day = 14 }, false, Now);
            _links.ReplaceForEntry(a, [Link("a")]);
            _links.ReplaceForEntry(b, [Link("b1"), Link("b2")]);
            _links.ReplaceForEntry(c, [Link("c")]);
            _links.ReplaceForNotedDate(date, [Link("d")]);

            _memories.DeleteCollection(holidays, 2);

            Assert.Equal(
                ["https://photos.example.org/c", "https://photos.example.org/d"],
                _folder.Strings("SELECT Url FROM ExternalLink ORDER BY Url;"));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>A personal collection with one memory (creating the file when needed); its identifier.</summary>
        private long PersonalEntry()
        {
            var collection = _memories.CreateCollection("Invented " + Guid.NewGuid().ToString("N")[..6], !_memories.FileExists, Now);

            return _memories.AddEntry(PersonalMemoryStoreTests.Draft(collection));
        }

        /// <summary>An imported collection 9 with entry 90.</summary>
        private void AddImported() => Execute(
            """
            INSERT INTO Collection VALUES (9, 'Imported', 'Synthetic games', 'Synthetic activity', 0, 60);
            INSERT INTO Entry (EntryID, CollectionID, SourceKey, Title, StartLocal, EndLocal, StartUtc, EndUtc,
                               DatePrecision, Summary, ImageSha256, ImageSourceUrl, Source, DayLevel)
            VALUES (90, 9, 'game-1', 'Synthetic game', '2018-06-01T10:00:00', NULL, NULL, NULL, 'Second', NULL, NULL, NULL, NULL, NULL);
            """);

        private List<string> Rows() =>
            _folder.Strings("SELECT ExternalLinkID || '|' || OwnerKind || '|' || OwnerID || '|' || Ordinal || '|' || Url || '|' || ifnull(Label, '-') FROM ExternalLink ORDER BY ExternalLinkID;");

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
                default:
                    MemoriesDatabase.CreateFormatVersionFour(_folder.MemoriesPath, created);
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
                        .Select(i => reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture));
                    rows.Add(table + "|" + string.Join("|", values));
                }
            }

            SqliteConnection.ClearAllPools();

            return rows;
        }
    }
}
