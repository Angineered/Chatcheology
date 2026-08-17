using System.Globalization;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Workspace.WorkspaceTestData;

namespace Chatcheology.Data.Tests.Workspace
{
    /// <summary>
    /// Tests for workspace creation, the <c>PRAGMA user_version</c> schema version, and the
    /// transaction that wraps schema creation.
    /// </summary>
    public class WorkspaceDatabaseTests
    {
        private static readonly string[] VersionOneTables =
        [
            "ImportSource",
            "Conversation",
            "Participant",
            "ConversationParticipant",
            "Message",
        ];

        [Fact]
        public void Initialise_NewDatabase_SetsSchemaVersionToOne()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(1, WorkspaceDatabase.ReadSchemaVersion(connection));
        }

        [Fact]
        public void Initialise_NewDatabase_CreatesExactlyTheFiveVersionOneTables()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            foreach (var tableName in VersionOneTables)
            {
                Assert.True(TableExists(connection, tableName), tableName);
            }

            // The later-phase tables must not have crept in early.
            Assert.False(TableExists(connection, "Attachment"));
            Assert.False(TableExists(connection, "MediaAsset"));
        }

        [Fact]
        public void Initialise_ExistingVersionOneDatabase_IsAcceptedWithoutRecreatingTheSchema()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using (var seed = WorkspaceDatabase.OpenConnection(workspace.DatabasePath))
            {
                Execute(
                    seed,
                    $"INSERT INTO Conversation (Title, CreatedDateTimeUtc) " +
                    $"VALUES ('Kept', '{ImportedDateTimeUtcText}');");
            }

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(1, WorkspaceDatabase.ReadSchemaVersion(connection));

            // Recreating the schema would have destroyed this row.
            Assert.Equal(1, CountRows(connection, "Conversation"));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(99)]
        public void Initialise_UnsupportedSchemaVersion_IsRejected(int userVersion)
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            using (var setup = WorkspaceDatabase.OpenConnection(workspace.DatabasePath))
            {
                Execute(setup, $"PRAGMA user_version = {userVersion};");
            }

            var exception = Assert.Throws<InvalidOperationException>(
                () => WorkspaceDatabase.Initialise(workspace.DatabasePath));

            Assert.Contains(
                userVersion.ToString(CultureInfo.InvariantCulture),
                exception.Message,
                StringComparison.Ordinal);
        }

        /// <remarks>
        /// The failure is induced by a version-0 database that already contains an object colliding
        /// with a workspace table. <c>Message</c> is created last, so creation gets part way through
        /// and then fails, which is exactly the partial-schema case that must not survive.
        /// </remarks>
        [Fact]
        public void Initialise_WhenSchemaCreationFails_LeavesVersionZeroAndNoWorkspaceTables()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            using (var setup = WorkspaceDatabase.OpenConnection(workspace.DatabasePath))
            {
                Execute(setup, "CREATE TABLE Message (Unrelated TEXT NOT NULL);");
            }

            Assert.Throws<SqliteException>(
                () => WorkspaceDatabase.Initialise(workspace.DatabasePath));

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(0, WorkspaceDatabase.ReadSchemaVersion(connection));

            Assert.False(TableExists(connection, "ImportSource"));
            Assert.False(TableExists(connection, "Conversation"));
            Assert.False(TableExists(connection, "Participant"));
            Assert.False(TableExists(connection, "ConversationParticipant"));

            // The rollback undid only what the failed attempt created.
            Assert.True(ColumnExists(connection, "Message", "Unrelated"));
        }

        [Fact]
        public void BuildConnectionString_EnablesForeignKeys()
        {
            var connectionString = WorkspaceDatabase.BuildConnectionString("workspace.db");

            var builder = new SqliteConnectionStringBuilder(connectionString);

            Assert.True(builder.ForeignKeys);
            Assert.Equal("workspace.db", builder.DataSource);
        }

        /// <remarks>
        /// A path containing the characters that delimit a connection string must survive as a path
        /// rather than changing what the connection string means, which is why the data layer builds
        /// it with <see cref="SqliteConnectionStringBuilder"/> instead of concatenating.
        /// </remarks>
        [Fact]
        public void BuildConnectionString_PreservesAPathContainingConnectionStringDelimiters()
        {
            const string awkwardPath = "C:\\a folder;with=odd\"characters\\workspace.db";

            var builder = new SqliteConnectionStringBuilder(
                WorkspaceDatabase.BuildConnectionString(awkwardPath));

            Assert.Equal(awkwardPath, builder.DataSource);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void BuildConnectionString_MissingDatabasePath_IsRejected(string databasePath)
        {
            Assert.Throws<ArgumentException>(
                () => WorkspaceDatabase.BuildConnectionString(databasePath));
        }

        [Fact]
        public void Initialise_DoesNotCreateWriteAheadLogCompanionFiles()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            SqliteConnection.ClearAllPools();

            Assert.Empty(Directory.GetFiles(workspace.DirectoryPath, "*.db-wal"));
            Assert.Empty(Directory.GetFiles(workspace.DirectoryPath, "*.db-shm"));
        }

        /// <remarks>
        /// Guards the cleanup contract the rest of the suite depends on: a test run must leave no
        /// SQLite file behind anywhere.
        /// </remarks>
        [Fact]
        public void TemporaryWorkspaceDatabase_RemovesEveryFileItCreatedOnDispose()
        {
            string directoryPath;

            using (var workspace = new TemporaryWorkspaceDatabase())
            {
                directoryPath = workspace.DirectoryPath;

                WorkspaceDatabase.Initialise(workspace.DatabasePath);

                Assert.True(File.Exists(workspace.DatabasePath));
            }

            Assert.False(Directory.Exists(directoryPath));
        }
    }
}
