namespace Chatcheology.Archive.Tests
{
    /// <summary>
    /// Format version 3 (CH-53): <c>Entry.EveryYear</c> (yearly memories), no <c>NotedDate</c> table. Round trip,
    /// the cheap yearly-entry read, the writer's refusals, and what the validator counts. Version 2 keeps its noted
    /// dates (<see cref="ArchiveVersionTwoTests"/>).
    /// </summary>
    /// <remarks>Invented names, text and bytes only.</remarks>
    public sealed class ArchiveVersionThreeTests : IDisposable
    {
        private readonly TemporaryFolder _folder = new();

        public void Dispose() => _folder.Dispose();

        private string Database => Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName);

        /// <summary>A personal yearly memory in collection 2 (Holidays).</summary>
        private static ArchiveEntry Yearly(long id = 30, string precision = "Day", DateTime? end = null, long collection = 2) => new()
        {
            EntryID = id,
            CollectionID = collection,
            SourceKey = null,
            Title = "Invented anniversary",
            StartLocal = new DateTime(2016, 3, 14),
            EndLocal = end,
            StartUtc = null,
            EndUtc = null,
            DatePrecision = precision,
            Summary = "An invented note.",
            ImageSha256 = null,
            ImageExtension = null,
            Source = "Noted date",
            DayLevel = null,
            EveryYear = true,
        };

        private ArchiveCounts WriteVersionThree(bool externalLinks = false) =>
            ArchiveVersionTwoTests.WriteMemoryArchive(
                _folder.Path, externalLinks: externalLinks, formatVersion: 3, beforeLinks: writer => writer.WriteEntry(Yearly()));

        [Fact]
        public void YearlyEntries_RoundTrip_AndValidate_WithoutANotedDateTable()
        {
            var counts = WriteVersionThree(externalLinks: true);

            Assert.Equal(0, counts.NotedDates);
            Assert.Equal(2, counts.ExternalLinks);

            var manifest = File.ReadAllText(Path.Combine(_folder.Path, ArchiveFormat.ManifestFileName));
            Assert.Contains("\"formatVersion\": 3", manifest, StringComparison.Ordinal);
            Assert.Contains("\"notedDates\": 0", manifest, StringComparison.Ordinal);

            var result = ArchiveValidator.Validate(_folder.Path);
            Assert.True(result.Passed, string.Join(", ", result.FirstProblems));

            Assert.Empty(ArchiveSample.Rows(Database, "SELECT name FROM sqlite_master WHERE name = 'NotedDate';"));

            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.Equal(3, reader.Info.FormatVersion);
            Assert.True(reader.HasEveryYear);
            Assert.False(reader.HasNotedDates);
            Assert.Empty(reader.ReadNotedDates());

            var entries = reader.ReadAllEntries().ToDictionary(e => e.EntryID);
            Assert.True(entries[30].EveryYear);
            Assert.False(entries[20].EveryYear);
            Assert.False(entries[10].EveryYear);
            Assert.True(reader.ReadEntriesOf(2).Single(e => e.EntryID == 30).EveryYear);

            var yearly = Assert.Single(reader.ReadYearlyEntries());
            Assert.Equal(30, yearly.EntryID);
            Assert.Equal("Invented anniversary", yearly.Title);
            Assert.Equal(new DateTime(2016, 3, 14), yearly.StartLocal);
            Assert.Equal("Day", yearly.DatePrecision);
            Assert.Equal(ArchiveMemoryVocabulary.KindPersonal, yearly.CollectionKind);
        }

        [Fact]
        public void VersionOneAndTwo_HaveNoYearlyEntries()
        {
            ArchiveVersionTwoTests.WriteMemoryArchive(_folder.Path, notedDates: true);

            using (var reader = ArchiveReader.Open(_folder.Path))
            {
                Assert.False(reader.HasEveryYear);
                Assert.True(reader.HasNotedDates);
                Assert.Empty(reader.ReadYearlyEntries());
                Assert.All(reader.ReadAllEntries(), e => Assert.False(e.EveryYear));
            }

            using var other = new TemporaryFolder();
            ArchiveSample.WriteFolder(other.Path, gapless: true, formatVersion: 1);

            using var v1 = ArchiveReader.Open(other.Path);
            Assert.False(v1.HasEveryYear);
            Assert.Empty(v1.ReadYearlyEntries());
        }

        [Fact]
        public void TheWriter_RefusesAYearlyEntryBelowVersionThree()
        {
            using var writer = ArchiveVersionTwoTests.Begin(Database);

            var refusal = Assert.Throws<InvalidOperationException>(() => writer.WriteEntry(Yearly()));

            Assert.Contains("version 3", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TheWriter_RefusesANotedDateInVersionThree()
        {
            using var writer = ArchiveVersionTwoTests.Begin(Database, formatVersion: 3);

            var refusal = Assert.Throws<InvalidOperationException>(() => writer.WriteNotedDate(new ArchiveNotedDate
            {
                NotedDateID = 1,
                Title = "Invented",
                Month = 3,
                Day = 14,
                Year = 2016,
                Note = null,
            }));

            Assert.Contains("version 2 archive only", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TheWriter_RefusesAYearlyEntryThatIsNotADayWithoutAnEnd()
        {
            using (var writer = ArchiveVersionTwoTests.Begin(Database, formatVersion: 3))
            {
                Assert.Throws<InvalidOperationException>(() => writer.WriteEntry(Yearly(precision: "Month")));
                Assert.Throws<InvalidOperationException>(() => writer.WriteEntry(Yearly(end: new DateTime(2016, 3, 15))));
            }

            File.Delete(Database);

            // In an imported collection: the collection is another table, so Complete refuses it.
            using var imported = ArchiveVersionTwoTests.Begin(Database, formatVersion: 3);
            imported.WriteEntry(Yearly(collection: 1));

            var refusal = Assert.Throws<InvalidOperationException>(() => imported.Complete());
            Assert.Contains("yearly", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TheWriter_RefusesAnExternalLinkOnANotedDateInVersionThree()
        {
            using var writer = ArchiveVersionTwoTests.Begin(Database, formatVersion: 3);

            writer.WriteExternalLink(new ArchiveExternalLink
            {
                ExternalLinkID = 1,
                OwnerKind = ExternalLinkOwnerKind.NotedDate,
                OwnerID = 1,
                Ordinal = 1,
                Url = "https://photos.example.org/album/a",
                Label = null,
            });

            var refusal = Assert.Throws<InvalidOperationException>(() => writer.Complete());
            Assert.Contains("personal entry of the archive", refusal.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("UPDATE Entry SET EveryYear = 1 WHERE EntryID = 10;")]
        [InlineData("UPDATE Entry SET DatePrecision = 'Month' WHERE EntryID = 30;")]
        [InlineData("UPDATE Entry SET EndLocal = '2016-03-15T00:00:00' WHERE EntryID = 30;")]
        [InlineData("UPDATE Entry SET CollectionID = 1 WHERE EntryID = 30;")]
        public void AYearlyEntryBreakingTheRule_IsFoundByTheValidator(string tamper)
        {
            WriteVersionThree();
            ArchiveSample.Tamper(_folder.Path, tamper);

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.EntryEveryYearInvalid]);
            Assert.False(result.Checks.Single(c => c.Check == ArchiveCheck.LinksAndBasis).Passed);
            Assert.Equal(ArchiveCheck.LinksAndBasis, ArchiveValidator.CheckOf(ArchiveProblemKind.EntryEveryYearInvalid));
        }

        [Fact]
        public void AnExternalLinkOnANotedDate_InVersionThree_HasNoOwner()
        {
            WriteVersionThree(externalLinks: true);
            ArchiveSample.Tamper(
                _folder.Path,
                "INSERT INTO ExternalLink VALUES (50, 'NotedDate', 1, 1, 'https://photos.example.org/album/x', NULL);");

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.ExternalLinkOwnerInvalid]);
        }

        [Fact]
        public void ANotedDateTable_InVersionThree_IsNeverRead()
        {
            WriteVersionThree();
            ArchiveSample.Tamper(
                _folder.Path,
                "CREATE TABLE NotedDate (NotedDateID INTEGER PRIMARY KEY, Title TEXT, Month INTEGER, Day INTEGER, Year INTEGER, Note TEXT);" +
                "INSERT INTO NotedDate VALUES (1, 'Hand made', 3, 14, 2016, NULL);");

            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.False(reader.HasNotedDates);
            Assert.Empty(reader.ReadNotedDates());
        }

        [Fact]
        public void AVersionThreeDatabaseWithoutTheEveryYearColumn_IsRefused()
        {
            WriteVersionThree();
            ArchiveSample.Tamper(_folder.Path, "ALTER TABLE Entry DROP COLUMN EveryYear;");

            Assert.Throws<ArchiveFormatException>(() => ArchiveReader.Open(_folder.Path));
        }

        [Fact]
        public void AnEveryYearValueOutsideTheFormat_StopsTheRowChecks()
        {
            WriteVersionThree();
            ArchiveSample.Tamper(_folder.Path, "PRAGMA ignore_check_constraints = ON; UPDATE Entry SET EveryYear = 2 WHERE EntryID = 30;");

            using (var reader = ArchiveReader.Open(_folder.Path))
            {
                Assert.Throws<ArchiveFormatException>(() => reader.ReadAllEntries());
            }

            Assert.Equal(1, ArchiveValidator.Validate(_folder.Path).ProblemCounts[ArchiveProblemKind.DatabaseUnreadable]);
        }
    }
}
