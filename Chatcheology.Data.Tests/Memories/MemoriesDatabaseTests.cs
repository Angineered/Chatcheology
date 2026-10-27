using Chatcheology.Data.Memories;
using Chatcheology.Data.OlderExports;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Memories
{
    public class MemoriesDatabaseTests
    {
        [Fact]
        public void Create_MakesEveryTableAndOneMarker_AndNoWorkspaceTables()
        {
            using var folder = new MemoriesTestFolder();
            folder.CreateMemories();

            using var connection = MemoriesDatabase.OpenConnection(folder.MemoriesPath);
            var tables = WorkspaceDatabase.ReadApplicationTableNames(connection);

            Assert.Equal(MemoriesDatabase.TableNames.OrderBy(n => n, StringComparer.Ordinal), tables);
            Assert.Equal(1, folder.Scalar("SELECT COUNT(*) FROM MemoriesInfo WHERE FormatVersion = 1 AND Purpose = 'Memories';"));
        }

        [Fact]
        public void PathBeside_IsTheFixedNameInTheWorkspaceFolder()
        {
            var path = MemoriesDatabase.PathBeside(Path.Combine(Path.GetTempPath(), "x", "workspace.db"));

            Assert.Equal(Path.Combine(Path.GetTempPath(), "x", "memories.db"), path);
            Assert.Equal(Path.Combine(Path.GetTempPath(), "x", "memories-media"), MemoriesDatabase.MediaFolderFor(path));
        }

        [Fact]
        public void Create_RefusesAnExistingFile_AndLeavesItUntouched()
        {
            using var folder = new MemoriesTestFolder();
            File.WriteAllText(folder.MemoriesPath, "not mine");

            Assert.Throws<InvalidOperationException>(() => MemoriesDatabase.Create(folder.MemoriesPath, MemoriesTestFolder.Now));
            Assert.Equal("not mine", File.ReadAllText(folder.MemoriesPath));
        }

        [Fact]
        public void Create_And_Open_RefuseAnyOtherFileName()
        {
            using var folder = new MemoriesTestFolder();
            var other = Path.Combine(folder.Root, "workspace.db");

            Assert.Throws<ArgumentException>(() => MemoriesDatabase.Create(other, MemoriesTestFolder.Now));
            Assert.Throws<ArgumentException>(() => MemoriesDatabase.OpenConnection(other));
            Assert.False(File.Exists(other));
        }

        [Fact]
        public void Open_MissingFile_CreatesNothing()
        {
            using var folder = new MemoriesTestFolder();

            Assert.Throws<FileNotFoundException>(() => MemoriesDatabase.OpenConnection(folder.MemoriesPath));
            Assert.False(File.Exists(folder.MemoriesPath));
        }

        [Fact]
        public void Open_RefusesAMainWorkspaceUnderTheMemoriesName()
        {
            using var folder = new MemoriesTestFolder();
            WorkspaceDatabase.Initialise(folder.MemoriesPath);

            var exception = Assert.Throws<InvalidOperationException>(() => MemoriesDatabase.OpenConnection(folder.MemoriesPath));
            Assert.Contains("workspace", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Open_RefusesAnOlderExportsFileUnderTheMemoriesName()
        {
            using var folder = new MemoriesTestFolder();
            var olderPath = Path.Combine(folder.Root, OlderExportsDatabase.FileName);
            OlderExportsDatabase.Create(olderPath, MemoriesTestFolder.Now);
            SqliteConnection.ClearAllPools();
            File.Copy(olderPath, folder.MemoriesPath);

            var exception = Assert.Throws<InvalidOperationException>(() => MemoriesDatabase.OpenConnection(folder.MemoriesPath));
            Assert.Contains("older-exports", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void WorkspaceInitialise_OnAMemoriesFile_ThrowsAndLeavesItUnchanged()
        {
            using var folder = new MemoriesTestFolder();
            folder.CreateMemories();
            SqliteConnection.ClearAllPools();
            var before = File.ReadAllBytes(folder.MemoriesPath);

            Assert.Throws<InvalidOperationException>(() => WorkspaceDatabase.Initialise(folder.MemoriesPath));

            SqliteConnection.ClearAllPools();
            Assert.Equal(before, File.ReadAllBytes(folder.MemoriesPath));
            using var connection = MemoriesDatabase.OpenConnection(folder.MemoriesPath);
            Assert.DoesNotContain("Message", WorkspaceDatabase.ReadApplicationTableNames(connection));
        }

        [Fact]
        public void Open_RefusesAMemoriesFileWithAnotherUserVersion()
        {
            using var folder = new MemoriesTestFolder();
            folder.CreateMemories();

            using (var connection = new SqliteConnection($"Data Source={folder.MemoriesPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version = 0;";
                command.ExecuteNonQuery();
            }

            Assert.Throws<InvalidOperationException>(() => MemoriesDatabase.OpenConnection(folder.MemoriesPath));
        }

        [Fact]
        public void Open_RefusesAFileWithoutTheMarker()
        {
            using var folder = new MemoriesTestFolder();
            folder.CreateMemories();

            using (var connection = new SqliteConnection($"Data Source={folder.MemoriesPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM MemoriesInfo;";
                command.ExecuteNonQuery();
            }

            Assert.Throws<InvalidOperationException>(() => MemoriesDatabase.OpenConnection(folder.MemoriesPath));
        }

        [Fact]
        public void Open_RefusesAnEmptyDatabase()
        {
            using var folder = new MemoriesTestFolder();

            using (var connection = new SqliteConnection($"Data Source={folder.MemoriesPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE Unrelated (X INTEGER);";
                command.ExecuteNonQuery();
            }

            Assert.Throws<InvalidOperationException>(() => MemoriesDatabase.OpenConnection(folder.MemoriesPath));
        }

        [Theory]
        [InlineData("INSERT INTO Collection (Kind, Name, ShownByDefault, PeriodGapMinutes) VALUES ('Other', 'x', 0, 60);")]
        [InlineData("INSERT INTO Collection (Kind, Name, ShownByDefault, PeriodGapMinutes) VALUES ('Imported', 'x', 0, NULL);")]
        [InlineData("INSERT INTO Collection (Kind, Name, ShownByDefault, PeriodGapMinutes) VALUES ('Personal', 'x', 0, 60);")]
        [InlineData("INSERT INTO Collection (Kind, Name, ShownByDefault, PeriodGapMinutes) VALUES ('Personal', 'x', 2, NULL);")]
        [InlineData("INSERT INTO Entry (CollectionID, Title, StartLocal, DatePrecision) VALUES (1, 't', '2015-03-01T10:00:00', 'Week');")]
        [InlineData("INSERT INTO Entry (CollectionID, Title, StartLocal, DatePrecision) VALUES (1, 't', '2015-03-01 10:00:00', 'Day');")]
        [InlineData("INSERT INTO Entry (CollectionID, Title, StartLocal, StartUtc, DatePrecision) VALUES (1, 't', '2015-03-01T10:00:00', '2015-03-01T08:00:00', 'Day');")]
        [InlineData("INSERT INTO Entry (CollectionID, Title, StartLocal, DatePrecision, DayLevel) VALUES (1, 't', '2015-03-01T10:00:00', 'Day', 'medium');")]
        [InlineData("INSERT INTO MediaFile (Sha256, Extension, SizeBytes) VALUES ('ABC', 'png', 1);")]
        [InlineData("INSERT INTO EntryLink (FromEntryID, ToEntryID, ToMessageSequenceNumber) VALUES (1, NULL, NULL);")]
        public void Constraints_RefuseValuesOutsideTheVocabulary(string sql)
        {
            using var folder = new MemoriesTestFolder();
            folder.CreateMemories();

            using var connection = MemoriesDatabase.OpenConnection(folder.MemoriesPath);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO Collection (Kind, Name, ShownByDefault, PeriodGapMinutes) VALUES ('Personal', 'Holidays', 1, NULL);
                INSERT INTO Entry (CollectionID, Title, StartLocal, DatePrecision) VALUES (1, 'Trip', '2015-03-01T00:00:00', 'Day');
                """;
            command.ExecuteNonQuery();

            command.CommandText = sql;
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }

        [Fact]
        public void Links_StartAtPersonalEntriesOnly()
        {
            using var folder = new MemoriesTestFolder();
            folder.CreateMemories();

            using var connection = MemoriesDatabase.OpenConnection(folder.MemoriesPath);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO Collection (Kind, Name, ShownByDefault, PeriodGapMinutes) VALUES ('Personal', 'Holidays', 1, NULL);
                INSERT INTO Collection (Kind, Name, ShownByDefault, PeriodGapMinutes) VALUES ('Imported', 'Shared gaming', 0, 60);
                INSERT INTO Entry (CollectionID, Title, StartLocal, DatePrecision) VALUES (1, 'Trip', '2015-03-01T00:00:00', 'Day');
                INSERT INTO Entry (CollectionID, SourceKey, Title, StartLocal, DatePrecision) VALUES (2, 'k', 'Game', '2015-03-01T10:00:00', 'Second');
                INSERT INTO EntryLink (FromEntryID, ToEntryID) VALUES (1, 2);
                INSERT INTO EntryLink (FromEntryID, ToMessageSequenceNumber) VALUES (1, 42);
                """;
            command.ExecuteNonQuery();

            command.CommandText = "INSERT INTO EntryLink (FromEntryID, ToEntryID) VALUES (2, 1);";
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());

            command.CommandText = "UPDATE EntryLink SET FromEntryID = 2 WHERE EntryLinkID = 1;";
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }
    }
}
