using Chatcheology.Data.OlderExports;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.OlderExports
{
    public class OlderExportsDatabaseTests
    {
        private static readonly DateTime Now = new(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Create_MakesAVersionFiveWorkspaceWithTheAddedTablesAndMarker()
        {
            using var folders = new SyntheticExportFolders();

            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            using var connection = OlderExportsDatabase.OpenConnection(folders.OlderExportsPath);

            Assert.Equal(WorkspaceDatabase.SchemaVersion, WorkspaceDatabase.ReadSchemaVersion(connection));

            var tables = WorkspaceDatabase.ReadApplicationTableNames(connection);
            Assert.All(OlderExportsDatabase.AddedTableNames, name => Assert.Contains(name, tables));
            Assert.Contains("Message", tables);
        }

        [Fact]
        public void PathBeside_IsTheFixedNameInTheWorkspaceFolder()
        {
            var path = OlderExportsDatabase.PathBeside(Path.Combine(Path.GetTempPath(), "x", "workspace.db"));

            Assert.Equal(Path.Combine(Path.GetTempPath(), "x", "older-exports.db"), path);
        }

        [Fact]
        public void Create_RefusesAnExistingFile_AndLeavesItUntouched()
        {
            using var folders = new SyntheticExportFolders();
            File.WriteAllText(folders.OlderExportsPath, "not mine");

            Assert.Throws<InvalidOperationException>(
                () => OlderExportsDatabase.Create(folders.OlderExportsPath, Now));

            Assert.Equal("not mine", File.ReadAllText(folders.OlderExportsPath));
        }

        [Fact]
        public void Create_RefusesAnyOtherFileName()
        {
            using var folders = new SyntheticExportFolders();
            var path = Path.Combine(folders.WorkspaceFolder, "workspace.db");

            Assert.Throws<ArgumentException>(() => OlderExportsDatabase.Create(path, Now));
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void Create_RequiresUtc()
        {
            using var folders = new SyntheticExportFolders();

            Assert.Throws<ArgumentException>(
                () => OlderExportsDatabase.Create(folders.OlderExportsPath, DateTime.SpecifyKind(Now, DateTimeKind.Local)));
            Assert.False(File.Exists(folders.OlderExportsPath));
        }

        [Fact]
        public void Open_RefusesAMainWorkspace_EvenUnderTheFixedName()
        {
            using var folders = new SyntheticExportFolders();

            // A main workspace (current schema, no marker) placed at the older-exports path.
            WorkspaceDatabase.Initialise(folders.OlderExportsPath);
            SqliteConnection.ClearAllPools();
            var before = File.ReadAllBytes(folders.OlderExportsPath);

            var exception = Assert.Throws<InvalidOperationException>(
                () => OlderExportsDatabase.OpenConnection(folders.OlderExportsPath));

            Assert.Contains("not an older-exports file", exception.Message, StringComparison.Ordinal);

            SqliteConnection.ClearAllPools();
            Assert.Equal(before, File.ReadAllBytes(folders.OlderExportsPath));
        }

        [Fact]
        public void Open_RefusesAMainWorkspaceByName()
        {
            using var folders = new SyntheticExportFolders();
            var main = Path.Combine(folders.WorkspaceFolder, "workspace.db");
            WorkspaceDatabase.Initialise(main);

            Assert.Throws<ArgumentException>(() => OlderExportsDatabase.OpenConnection(main));
        }

        [Fact]
        public void Open_RefusesAFileWhoseMarkerRowIsMissing()
        {
            using var folders = new SyntheticExportFolders();
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            using (var connection = WorkspaceDatabase.OpenConnection(folders.OlderExportsPath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM OlderExportsInfo;";
                command.ExecuteNonQuery();
            }

            Assert.Throws<InvalidOperationException>(
                () => OlderExportsDatabase.OpenConnection(folders.OlderExportsPath));
        }

        [Fact]
        public void Open_RefusesAnEarlierFormatFile_WithAClearReason()
        {
            using var folders = new SyntheticExportFolders();
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            // Simulate a version-1 file: rebuild the marker without its version CHECK.
            using (var connection = WorkspaceDatabase.OpenConnection(folders.OlderExportsPath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    DROP TABLE OlderExportsInfo;
                    CREATE TABLE OlderExportsInfo (
                        OlderExportsInfoID INTEGER PRIMARY KEY, Purpose TEXT NOT NULL,
                        FormatVersion INTEGER NOT NULL, CreatedDateTimeUtc TEXT NOT NULL);
                    INSERT INTO OlderExportsInfo VALUES (1, 'OlderExports', 1, 'x');
                    """;
                command.ExecuteNonQuery();
            }

            var exception = Assert.Throws<InvalidOperationException>(
                () => OlderExportsDatabase.OpenConnection(folders.OlderExportsPath));

            Assert.Contains("earlier format", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Open_MissingFile_CreatesNothing()
        {
            using var folders = new SyntheticExportFolders();

            Assert.Throws<FileNotFoundException>(
                () => OlderExportsDatabase.OpenConnection(folders.OlderExportsPath));
            Assert.False(File.Exists(folders.OlderExportsPath));
        }

        [Theory]
        [InlineData("INSERT INTO OlderExportsInfo VALUES (2, 'OlderExports', 1, 'x');")]
        [InlineData("INSERT INTO ExportOrder VALUES (1, 0, 1, NULL, NULL);")]
        [InlineData("INSERT INTO ExportOrder VALUES (1, 1, 2, NULL, NULL);")]
        [InlineData("INSERT INTO ExportOrder VALUES (1, 1, 1, 0, 2);")]
        [InlineData("INSERT INTO ExportOrder VALUES (1, 1, 0, 5, 2);")]
        [InlineData("INSERT INTO ExportOrder VALUES (1, 1, 1, 5, NULL);")]
        [InlineData("INSERT INTO ExportOrder VALUES (1, 1, 1, NULL, 2);")]
        [InlineData("INSERT INTO ExportOrder VALUES (1, 1, 1, 5, 1);")]
        [InlineData("INSERT INTO ExportRole VALUES (1, 1, 'Someone');")]
        [InlineData("INSERT INTO ExportAttachmentName VALUES (1, 'a/b.jpg');")]
        [InlineData("INSERT INTO ExportAttachmentName VALUES (1, 'a\\b.jpg');")]
        [InlineData("INSERT INTO ExportAttachmentName VALUES (1, '  ');")]
        public void Constraints_RejectInvalidRows(string statement)
        {
            using var folders = new SyntheticExportFolders();
            OlderExportsDatabase.Create(folders.OlderExportsPath, Now);

            using var connection = WorkspaceDatabase.OpenConnection(folders.OlderExportsPath);
            using var command = connection.CreateCommand();

            // Foreign keys are switched off for this statement only, so that the CHECK under test
            // is what rejects it rather than a missing parent row.
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            command.ExecuteNonQuery();
            command.CommandText = statement;

            var exception = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
            Assert.Contains("CHECK", exception.Message, StringComparison.Ordinal);
        }
    }
}
