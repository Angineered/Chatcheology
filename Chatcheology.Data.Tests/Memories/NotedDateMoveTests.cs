using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Personal;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Memories
{
    /// <summary>
    /// CH-53: moving noted dates to yearly memories, once, in one transaction, after a copy of the file. Invented
    /// titles, dates and addresses only.
    /// </summary>
    public sealed class NotedDateMoveTests : IDisposable
    {
        private static readonly DateTime Now = MemoriesTestFolder.Now;

        private readonly MemoriesTestFolder _folder = new();
        private readonly NotedDateStore _store;
        private readonly ExternalLinkStore _links;

        public NotedDateMoveTests()
        {
            _store = new NotedDateStore(_folder.MemoriesPath);
            _links = new ExternalLinkStore(_folder.MemoriesPath);
        }

        public void Dispose() => _folder.Dispose();

        private string Backup => Path.Combine(_folder.Root, "memories.db.before-CH-53");

        /// <summary>
        /// Three noted dates with a year (one with a note and two gallery links, one on 29 February) and one without
        /// a year (with a link of its own). Returns their identifiers.
        /// </summary>
        private (long Anniversary, long Leap, long Plain, long Yearless) Seed()
        {
            var anniversary = _store.Add(new() { Title = "Invented anniversary", Month = 2, Day = 14, Year = 2019, Note = "An invented note" }, true, Now);
            var leap = _store.Add(new() { Title = "Invented leap day", Month = 2, Day = 29, Year = 2020 }, false, Now);
            var plain = _store.Add(new() { Title = "Year end", Month = 12, Day = 31, Year = 2000 }, false, Now);
            var yearless = _store.Add(new() { Title = "Invented birthday", Month = 8, Day = 2 }, false, Now);

            _links.ReplaceForNotedDate(anniversary, [new("https://photos.example.org/album/a", "Day 1"), new("https://photos.example.org/album/b")]);
            _links.ReplaceForNotedDate(yearless, [new("https://photos.example.org/album/c")]);
            SqliteConnection.ClearAllPools();

            return (anniversary, leap, plain, yearless);
        }

        private List<string> LinkRows() =>
            _folder.Strings("SELECT ExternalLinkID || '|' || OwnerKind || '|' || OwnerID || '|' || Ordinal || '|' || Url || '|' || ifnull(Label, '-') FROM ExternalLink ORDER BY ExternalLinkID;");

        [Fact]
        public void TheMove_MakesYearlyMemories_RepointsTheLinks_AndLeavesTheYearlessDate()
        {
            var (anniversary, leap, plain, yearless) = Seed();
            var linkIDs = _folder.Strings($"SELECT ExternalLinkID FROM ExternalLink WHERE OwnerID = {anniversary} ORDER BY Ordinal;");
            var original = File.ReadAllBytes(_folder.MemoriesPath);

            var result = _store.MoveToMemories(4);

            Assert.Equal(3, result.Moved);
            Assert.Equal(1, result.LeftWithoutYear);
            Assert.True(result.CollectionCreated);
            Assert.Equal([anniversary, leap, plain], result.Pairs.Select(p => p.NotedDateID));

            // The copy is the file exactly as it was before the move.
            Assert.Equal(original, File.ReadAllBytes(Backup));

            // The collection: personal, named, shown.
            Assert.Equal(
                [$"{result.CollectionID}|Personal|Noted dates|<null>|1|<null>"],
                _folder.Strings("SELECT CollectionID || '|' || Kind || '|' || Name || '|' || ifnull(SourceDescription, '<null>') || '|' || ShownByDefault || '|' || ifnull(PeriodGapMinutes, '<null>') FROM Collection;"));

            // Each entry exactly as the form writes a Day entry, Every year on.
            Assert.Equal(
                [
                    "Invented anniversary|2019-02-14T00:00:00|-|-|-|Day|An invented note|-|-|Noted date|-|-|1",
                    "Invented leap day|2020-02-29T00:00:00|-|-|-|Day|-|-|-|Noted date|-|-|1",
                    "Year end|2000-12-31T00:00:00|-|-|-|Day|-|-|-|Noted date|-|-|1",
                ],
                _folder.Strings(
                    """
                    SELECT Title || '|' || StartLocal || '|' || ifnull(EndLocal, '-') || '|' || ifnull(StartUtc, '-') || '|' || ifnull(EndUtc, '-')
                           || '|' || DatePrecision || '|' || ifnull(Summary, '-') || '|' || ifnull(ImageSha256, '-') || '|' || ifnull(ImageSourceUrl, '-')
                           || '|' || Source || '|' || ifnull(SourceKey, '-') || '|' || ifnull(DayLevel, '-') || '|' || EveryYear
                    FROM Entry ORDER BY EntryID;
                    """));
            Assert.All(result.Pairs, p => Assert.Equal(result.CollectionID, _folder.Scalar($"SELECT CollectionID FROM Entry WHERE EntryID = {p.EntryID};")));

            // The links: same rows (identifiers, order, labels), now on the entry; the yearless date keeps its own.
            var entry = result.Pairs[0].EntryID;
            Assert.Equal(linkIDs, _folder.Strings($"SELECT ExternalLinkID FROM ExternalLink WHERE OwnerKind = 'Entry' AND OwnerID = {entry} ORDER BY Ordinal;"));
            Assert.Equal(
                [
                    $"{linkIDs[0]}|Entry|{entry}|1|https://photos.example.org/album/a|Day 1",
                    $"{linkIDs[1]}|Entry|{entry}|2|https://photos.example.org/album/b|-",
                    $"3|NotedDate|{yearless}|1|https://photos.example.org/album/c|-",
                ],
                LinkRows());
            Assert.Equal(2, _links.ReadForEntry(entry).Count);

            // Only the yearless date is left; the memories read back as yearly.
            Assert.Equal([yearless], _store.List().Select(d => d.NotedDateID));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM NotedDate;"));

            var store = new PersonalMemoryStore(_folder.MemoriesPath, new PersonalMemoryStoreTests.FakeMessages());
            var details = store.ReadEntry(entry);
            Assert.True(details.EveryYear);
            Assert.Equal(PersonalDatePrecision.Day, details.Precision);
            Assert.Equal("Noted date", details.Source);
            Assert.Empty(PersonalMemoryRules.Validate(new PersonalEntryDraft
            {
                CollectionID = details.CollectionID,
                Title = details.Title,
                Start = details.Start,
                End = details.End,
                Precision = details.Precision,
                Summary = details.Summary,
                Source = details.Source,
                EveryYear = details.EveryYear,
            }));

            // A saved form keeps it as it is.
            store.UpdateEntry(entry, new PersonalEntryDraft
            {
                CollectionID = details.CollectionID,
                Title = details.Title,
                Start = details.Start,
                Precision = details.Precision,
                Summary = details.Summary,
                Source = details.Source,
                EveryYear = false,
            });
            Assert.False(store.ReadEntry(entry).EveryYear);
        }

        [Fact]
        public void AnExistingNotedDatesCollection_IsReused_WithItsShownSetting()
        {
            Seed();
            var memories = new PersonalMemoryStore(_folder.MemoriesPath, new PersonalMemoryStoreTests.FakeMessages());
            var existing = memories.CreateCollection("  noted DATES ", false, Now);
            new SqliteCommandRunner(_folder.MemoriesPath).Run($"UPDATE Collection SET ShownByDefault = 0 WHERE CollectionID = {existing};");

            var result = _store.MoveToMemories(4);

            Assert.False(result.CollectionCreated);
            Assert.Equal(existing, result.CollectionID);
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM Collection;"));
            Assert.Equal(0, _folder.Scalar("SELECT ShownByDefault FROM Collection;"));
            Assert.Equal(3, _folder.Scalar($"SELECT COUNT(*) FROM Entry WHERE CollectionID = {existing};"));
        }

        [Fact]
        public void AChangedCount_IsRefused_AndNothingIsWrittenOrCopied()
        {
            Seed();
            var before = File.ReadAllBytes(_folder.MemoriesPath);

            var refusal = Assert.Throws<PersonalMemoryValidationException>(() => _store.MoveToMemories(3));

            Assert.Contains("changed since you were asked", refusal.Errors[PersonalFields.NotedDate], StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllBytes(_folder.MemoriesPath));
            Assert.False(File.Exists(Backup));
        }

        [Fact]
        public void AnExistingCopy_IsNeverOverwritten_AndTheMoveIsRefused()
        {
            Seed();
            File.WriteAllText(Backup, "an earlier copy");
            var before = File.ReadAllBytes(_folder.MemoriesPath);

            var refusal = Assert.Throws<PersonalMemoryValidationException>(() => _store.MoveToMemories(4));

            Assert.Contains("memories.db.before-CH-53", refusal.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_folder.Root, refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("an earlier copy", File.ReadAllText(Backup));
            Assert.Equal(before, File.ReadAllBytes(_folder.MemoriesPath));
            Assert.Equal(4, _store.List().Count);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AFailureMidWay_RollsEverythingBack_AndRemovesTheCopy(bool beforeCommit)
        {
            Seed();
            var links = LinkRows();
            var dates = _store.List();

            if (beforeCommit)
            {
                _store.BeforeCommit = () => throw new InvalidOperationException("Synthetic failure.");
            }
            else
            {
                _store.AfterEachMovedForTests = count =>
                {
                    if (count == 2)
                    {
                        throw new InvalidOperationException("Synthetic failure.");
                    }
                };
            }

            Assert.Throws<InvalidOperationException>(() => _store.MoveToMemories(4));

            Assert.Equal(dates, _store.List());
            Assert.Equal(links, LinkRows());
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM Entry;"));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM Collection;"));
            Assert.False(File.Exists(Backup));

            // So it can be tried again.
            _store.BeforeCommit = null;
            _store.AfterEachMovedForTests = null;
            Assert.Equal(3, _store.MoveToMemories(4).Moved);
        }

        [Fact]
        public void OnlyYearlessDates_AreRefused_AndAFileWithoutNotedDatesToo()
        {
            _store.Add(new() { Title = "Invented birthday", Month = 8, Day = 2 }, true, Now);
            SqliteConnection.ClearAllPools();

            var refusal = Assert.Throws<PersonalMemoryValidationException>(() => _store.MoveToMemories(1));
            Assert.Contains("No noted date has a year", refusal.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Backup));

            _store.Delete(_store.List()[0].NotedDateID);
            Assert.Throws<PersonalMemoryValidationException>(() => _store.MoveToMemories(0));
            Assert.False(File.Exists(Backup));
        }

        [Fact]
        public void AFormatFourFile_IsCopiedAsItWas_ThenUpgraded_AndMoved()
        {
            MemoriesDatabase.CreateFormatVersionFour(_folder.MemoriesPath, Now);
            new SqliteCommandRunner(_folder.MemoriesPath).Run("INSERT INTO NotedDate VALUES (3, 'Invented anniversary', 2, 14, 2019, NULL);");
            var before = File.ReadAllBytes(_folder.MemoriesPath);

            var result = _store.MoveToMemories(1);

            Assert.Equal(1, result.Moved);
            Assert.Equal(before, File.ReadAllBytes(Backup));
            Assert.Equal(6, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM Entry WHERE EveryYear = 1;"));

            using var copy = new SqliteConnection($"Data Source={Backup};Mode=ReadOnly;Pooling=False");
            copy.Open();
            using var command = copy.CreateCommand();
            command.CommandText = "SELECT FormatVersion FROM MemoriesInfo;";
            Assert.Equal(4L, command.ExecuteScalar());
        }

        [Fact]
        public void WithoutAFile_NothingIsCreated()
        {
            Assert.Throws<FileNotFoundException>(() => _store.MoveToMemories(0));
            Assert.False(File.Exists(_folder.MemoriesPath));
            Assert.False(File.Exists(Backup));
        }

        /// <summary>Runs one statement on the file, outside any store.</summary>
        private sealed class SqliteCommandRunner(string path)
        {
            internal void Run(string sql)
            {
                try
                {
                    using var connection = new SqliteConnection($"Data Source={path};Foreign Keys=True");
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
}
