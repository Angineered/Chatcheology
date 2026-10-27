using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Psn;
using Chatcheology.Tests.Memories;
using Microsoft.Data.Sqlite;
using static Chatcheology.Tests.Memories.SyntheticPsnFile;

namespace Chatcheology.Data.Tests.Memories
{
    public class PsnImporterTests
    {
        private static PsnActivity Read(SyntheticPsnFile file, int gap = 60) =>
            PsnSharedActivityReader.Parse(file.ToBytes(), LocalPrefix, OtherPrefix, file.PairAPrefix, gap);

        [Fact]
        public void Import_WritesCollectionEntriesPeriodsAndItems()
        {
            using var folder = new MemoriesTestFolder();
            var path = folder.CreateMemories();

            var result = new PsnImporter().Import(path, Read(Standard()));

            Assert.Equal(new PsnImportResult(true, false, 2, 0, 0, 0), result);
            Assert.Equal(1, folder.Scalar("SELECT COUNT(*) FROM Collection WHERE Kind = 'Imported' AND Name = 'Shared gaming' AND ShownByDefault = 0 AND PeriodGapMinutes = 60;"));
            Assert.Equal(2, folder.Scalar("SELECT COUNT(*) FROM Entry;"));
            Assert.Equal(4, folder.Scalar("SELECT COUNT(*) FROM EntryPeriod;"));
            Assert.Equal(6, folder.Scalar("SELECT COUNT(*) FROM EntryItem;"));
            Assert.Equal(
                ["Example Quest|2015-03-01T20:00:00|2015-03-01T23:10:00|2015-03-01T18:00:00Z|2015-03-01T21:10:00Z|Second|strong|PSN trophies|" + Quest.Icon],
                folder.Strings(
                    "SELECT Title || '|' || StartLocal || '|' || EndLocal || '|' || StartUtc || '|' || EndUtc || '|' || DatePrecision || '|' || DayLevel || '|' || Source || '|' || ImageSourceUrl " +
                    "FROM Entry WHERE SourceKey = 'NPWR99999_00|NPWR99999_00|2015-03-01';"));
            Assert.Equal(
                ["0|strong|1|1|2015-03-01T20:00:00|2015-03-01T20:00:30", "1|supporting|1|1|2015-03-01T23:00:00|2015-03-01T23:10:00"],
                folder.Strings(
                    "SELECT p.PeriodIndex || '|' || p.Label || '|' || p.LocalItemCount || '|' || p.OtherItemCount || '|' || p.StartLocal || '|' || p.EndLocal " +
                    "FROM EntryPeriod p JOIN Entry e ON e.EntryID = p.EntryID WHERE e.Title = 'Example Quest' ORDER BY p.PeriodIndex;"));
            Assert.Equal(
                ["Local|NPWR88888_00|5|bronze|2016-07-04T10:00:00Z|2016-07-04T12:00:00", "Other|NPWR77777_00|6|bronze|2016-07-04T15:00:00Z|2016-07-04T17:00:00"],
                folder.Strings(
                    "SELECT Role || '|' || SetId || '|' || ItemId || '|' || ItemType || '|' || TimeUtc || '|' || TimeLocal " +
                    "FROM EntryItem i JOIN Entry e ON e.EntryID = i.EntryID WHERE e.Title = 'Sample Racer' ORDER BY TimeUtc;"));
        }

        [Fact]
        public void Import_IdenticalFileAgain_ChangesNothing()
        {
            using var folder = new MemoriesTestFolder();
            var path = folder.CreateMemories();
            new PsnImporter().Import(path, Read(Standard()));
            SqliteConnection.ClearAllPools();
            var before = File.ReadAllBytes(path);

            var result = new PsnImporter().Import(path, Read(Standard()));

            Assert.Equal(new PsnImportResult(false, false, 0, 0, 2, 0), result);
            Assert.Equal(0, result.Changes);
            Assert.Equal(2, folder.Scalar("SELECT COUNT(*) FROM Entry;"));
            Assert.Equal(6, folder.Scalar("SELECT COUNT(*) FROM EntryItem;"));
            SqliteConnection.ClearAllPools();
            Assert.Equal(before, File.ReadAllBytes(path));
        }

        [Fact]
        public void Import_ChangedRecord_UpdatesInPlace_KeepingItsIdAndLinks()
        {
            using var folder = new MemoriesTestFolder();
            var path = folder.CreateMemories();
            new PsnImporter().Import(path, Read(Standard()));
            var questId = folder.Scalar("SELECT EntryID FROM Entry WHERE Title = 'Example Quest';");

            using (var connection = MemoriesDatabase.OpenConnection(path))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    $"""
                    INSERT INTO Collection (Kind, Name, ShownByDefault) VALUES ('Personal', 'Holidays', 1);
                    INSERT INTO Entry (CollectionID, Title, StartLocal, DatePrecision) SELECT CollectionID, 'Trip', '2015-03-01T00:00:00', 'Day' FROM Collection WHERE Kind = 'Personal';
                    INSERT INTO EntryLink (FromEntryID, ToEntryID) SELECT EntryID, {questId} FROM Entry WHERE Title = 'Trip';
                    UPDATE EntryItem SET Name = 'Invented Trophy Name' WHERE EntryID = {questId} AND Role = 'Local' AND ItemId = '1';
                    """;
                command.ExecuteNonQuery();
            }

            var changed = Standard();
            changed.Events[0].Local.Add(Trophy("7", Utc(2015, 3, 1, 21, 5, 0), "gold"));

            var result = new PsnImporter().Import(path, Read(changed));

            Assert.Equal(new PsnImportResult(false, false, 0, 1, 1, 0), result);
            Assert.Equal(questId, folder.Scalar("SELECT EntryID FROM Entry WHERE Title = 'Example Quest';"));
            Assert.Equal(5, folder.Scalar($"SELECT COUNT(*) FROM EntryItem WHERE EntryID = {questId};"));
            Assert.Equal(1, folder.Scalar($"SELECT COUNT(*) FROM EntryLink WHERE ToEntryID = {questId};"));
            Assert.Equal(1, folder.Scalar($"SELECT COUNT(*) FROM EntryItem WHERE EntryID = {questId} AND Name = 'Invented Trophy Name';"));
        }

        [Fact]
        public void Import_ChangedDayLevel_IsAnUpdate()
        {
            using var folder = new MemoriesTestFolder();
            var path = folder.CreateMemories();
            new PsnImporter().Import(path, Read(Standard()));

            var file = Standard();
            file.Tamper = root => root["events"]![1]!["quality"] = "supporting";

            var result = new PsnImporter().Import(path, Read(file));

            Assert.Equal(1, result.Updated);
            Assert.Equal(["supporting"], folder.Strings("SELECT DayLevel FROM Entry WHERE Title = 'Sample Racer';"));
        }

        [Fact]
        public void Import_RemovedRecord_IsCountedNotDeleted()
        {
            using var folder = new MemoriesTestFolder();
            var path = folder.CreateMemories();
            new PsnImporter().Import(path, Read(Standard()));

            var smaller = Standard();
            smaller.Events.RemoveAt(1);

            var result = new PsnImporter().Import(path, Read(smaller));

            Assert.Equal(new PsnImportResult(false, false, 0, 0, 1, 1), result);
            Assert.Equal(2, folder.Scalar("SELECT COUNT(*) FROM Entry;"));
            Assert.Equal(6, folder.Scalar("SELECT COUNT(*) FROM EntryItem;"));
        }

        [Fact]
        public void Import_OtherGap_UpdatesCollectionAndRecomputesPeriods()
        {
            using var folder = new MemoriesTestFolder();
            var path = folder.CreateMemories();
            new PsnImporter().Import(path, Read(Standard()));

            var result = new PsnImporter().Import(path, Read(Standard(), gap: 240));

            Assert.True(result.CollectionUpdated);
            Assert.Equal(1, result.Updated);
            Assert.Equal(1, result.Unchanged);
            Assert.Equal(240, folder.Scalar("SELECT PeriodGapMinutes FROM Collection;"));
            Assert.Equal(1, folder.Scalar("SELECT COUNT(*) FROM EntryPeriod p JOIN Entry e ON e.EntryID = p.EntryID WHERE e.Title = 'Example Quest';"));
        }

        [Fact]
        public void Import_FailureMidway_RollsBackEverything()
        {
            using var folder = new MemoriesTestFolder();
            var path = folder.CreateMemories();

            var importer = new PsnImporter
            {
                AfterRecordWritten = index =>
                {
                    if (index == 1)
                    {
                        throw new InvalidOperationException("synthetic failure");
                    }
                },
            };

            Assert.Throws<InvalidOperationException>(() => importer.Import(path, Read(Standard())));

            Assert.Equal(0, folder.Scalar("SELECT COUNT(*) FROM Collection;"));
            Assert.Equal(0, folder.Scalar("SELECT COUNT(*) FROM Entry;"));
            Assert.Equal(0, folder.Scalar("SELECT COUNT(*) FROM EntryPeriod;"));
            Assert.Equal(0, folder.Scalar("SELECT COUNT(*) FROM EntryItem;"));
        }

        [Fact]
        public void Import_FailureDuringUpdate_KeepsThePreviousVersion()
        {
            using var folder = new MemoriesTestFolder();
            var path = folder.CreateMemories();
            new PsnImporter().Import(path, Read(Standard()));

            var changed = Standard();
            changed.Events[0].Local.Add(Trophy("7", Utc(2015, 3, 1, 21, 5, 0), "gold"));
            var importer = new PsnImporter { AfterRecordWritten = _ => throw new InvalidOperationException("synthetic failure") };

            Assert.Throws<InvalidOperationException>(() => importer.Import(path, Read(changed)));

            Assert.Equal(6, folder.Scalar("SELECT COUNT(*) FROM EntryItem;"));
        }

        [Fact]
        public void Import_ImageHashIsKeptWhileTheUrlIsUnchanged_AndClearedWhenItChanges()
        {
            using var folder = new MemoriesTestFolder();
            var path = folder.CreateMemories();
            new PsnImporter().Import(path, Read(Standard()));
            var sha = new string('a', 64);

            using (var connection = MemoriesDatabase.OpenConnection(path))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    $"INSERT INTO MediaFile VALUES ('{sha}', 'png', 10); UPDATE Entry SET ImageSha256 = '{sha}';";
                command.ExecuteNonQuery();
            }

            var changed = Standard();
            changed.Events[0].Local.Add(Trophy("7", Utc(2015, 3, 1, 21, 5, 0), "gold"));
            changed.Tamper = root => root["games"]![1]!["icon"] = "https://image.api.playstation.com/zz/other.png";

            new PsnImporter().Import(path, Read(changed));

            Assert.Equal([sha], folder.Strings("SELECT ImageSha256 FROM Entry WHERE Title = 'Example Quest';"));
            Assert.Equal(["<null>"], folder.Strings("SELECT ImageSha256 FROM Entry WHERE Title = 'Sample Racer';"));
        }

        [Fact]
        public void Import_SecondCollectionName_IsSeparate()
        {
            using var folder = new MemoriesTestFolder();
            var path = folder.CreateMemories();
            new PsnImporter().Import(path, Read(Standard()));

            var result = new PsnImporter().Import(path, Read(Standard()), "Invented layer");

            Assert.Equal(2, result.Added);
            Assert.Equal(2, folder.Scalar("SELECT COUNT(*) FROM Collection;"));
            Assert.Equal(4, folder.Scalar("SELECT COUNT(*) FROM Entry;"));
        }

        [Fact]
        public void Import_RefusesAFileThatIsNotAMemoriesFile()
        {
            using var folder = new MemoriesTestFolder();
            Chatcheology.Data.Workspace.WorkspaceDatabase.Initialise(folder.MemoriesPath);

            Assert.Throws<InvalidOperationException>(() => new PsnImporter().Import(folder.MemoriesPath, Read(Standard())));
        }
    }
}
