using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Workspace.WorkspaceTestData;

namespace Chatcheology.Data.Tests.Workspace
{
    /// <summary>
    /// Tests the version-1 to version-2 migration against a genuine version-1 database.
    /// </summary>
    /// <remarks>
    /// The starting point is always <see cref="SyntheticVersionOneWorkspace"/>, which builds the
    /// version-1 schema itself. Migrating a database that the current build created would prove
    /// nothing about migrating one that the previous build created.
    /// </remarks>
    public class WorkspaceMigrationTests
    {
        /// <summary>
        /// Joins a row's fields for comparison. The ASCII unit separator cannot occur in the
        /// fixture's text, so two different rows can never flatten to the same string.
        /// </summary>
        private const string FieldSeparator = "\u001f";

        private static readonly string[] VersionTwoTables =
        [
            "MediaSource",
            "MediaAsset",
            "MediaFile",
            "MediaAssetFile",
            "Attachment",
        ];

        [Fact]
        public void Migrate_VersionOneDatabase_BecomesVersionTwo()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionOneWorkspace.Create(workspace.DatabasePath);

            using (var before = SyntheticVersionOneWorkspace.OpenPlainConnection(workspace.DatabasePath))
            {
                // The fixture really is version 1, so what follows really is a migration.
                Assert.Equal(1, WorkspaceDatabase.ReadSchemaVersion(before));
                Assert.False(TableExists(before, "Attachment"));
            }

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(2, WorkspaceDatabase.ReadSchemaVersion(connection));

            foreach (var tableName in VersionTwoTables)
            {
                Assert.True(TableExists(connection, tableName), tableName);
            }
        }

        [Fact]
        public void Migrate_LeavesEveryVersionOneRowUnchanged()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionOneWorkspace.Create(workspace.DatabasePath);

            var before = ReadVersionOneFacts(workspace.DatabasePath);

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            var after = ReadVersionOneFacts(workspace.DatabasePath);

            Assert.Equal(before, after);
        }

        /// <remarks>
        /// Exactly one of the fixture's six messages is exactly the placeholder. Two others contain
        /// it inside longer text, and a substring backfill would wrongly have created attachments for
        /// them.
        /// </remarks>
        [Fact]
        public void Migrate_CreatesOneAttachmentPerExactPlaceholderMessageOnly()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionOneWorkspace.Create(workspace.DatabasePath);

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(1, CountRows(connection, "Attachment"));

            var attachedSequenceNumber = ScalarLong(
                connection,
                """
                SELECT Message.SequenceNumber
                FROM Attachment
                JOIN Message ON Message.MessageID = Attachment.MessageID;
                """);

            Assert.Equal(
                SyntheticVersionOneWorkspace.ExactPlaceholderSequenceNumber,
                attachedSequenceNumber);
        }

        [Fact]
        public void Migrate_CreatesEveryAttachmentUnresolvedWithNoTypeAndOrdinalOne()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionOneWorkspace.Create(workspace.DatabasePath);

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(1, ScalarLong(connection, "SELECT COUNT(*) FROM Attachment WHERE Ordinal = 1;"));
            Assert.Equal(
                1,
                ScalarLong(
                    connection,
                    "SELECT COUNT(*) FROM Attachment WHERE ResolutionStatus = 'Unresolved';"));
            Assert.Equal(
                1,
                ScalarLong(
                    connection,
                    "SELECT COUNT(*) FROM Attachment WHERE ExpectedMediaType IS NULL;"));
            Assert.Equal(
                1,
                ScalarLong(
                    connection,
                    "SELECT COUNT(*) FROM Attachment WHERE ResolvedMediaAssetID IS NULL;"));
        }

        /// <remarks>
        /// Nothing about the migration reads a folder, hashes a file or resolves anything, so the
        /// four media tables must exist and be empty.
        /// </remarks>
        [Fact]
        public void Migrate_LeavesTheMediaTablesEmpty()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionOneWorkspace.Create(workspace.DatabasePath);

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(0, CountRows(connection, "MediaSource"));
            Assert.Equal(0, CountRows(connection, "MediaFile"));
            Assert.Equal(0, CountRows(connection, "MediaAsset"));
            Assert.Equal(0, CountRows(connection, "MediaAssetFile"));
        }

        /// <remarks>
        /// The whole point of applying the same version-2 statements on both paths. If a fresh
        /// version-2 database and a migrated version-1 database differed in any table, index or
        /// constraint, one of the two would be a schema no test had ever exercised.
        /// </remarks>
        [Fact]
        public void Migrate_ProducesTheSameSchemaAsCreatingVersionTwoFresh()
        {
            using var migrated = new TemporaryWorkspaceDatabase();
            using var fresh = new TemporaryWorkspaceDatabase();

            SyntheticVersionOneWorkspace.Create(migrated.DatabasePath);

            WorkspaceDatabase.Initialise(migrated.DatabasePath);
            WorkspaceDatabase.Initialise(fresh.DatabasePath);

            using var migratedConnection = WorkspaceDatabase.OpenConnection(migrated.DatabasePath);
            using var freshConnection = WorkspaceDatabase.OpenConnection(fresh.DatabasePath);

            Assert.Equal(ReadSchemaObjects(freshConnection), ReadSchemaObjects(migratedConnection));

            Assert.Equal(
                WorkspaceDatabase.ReadSchemaVersion(freshConnection),
                WorkspaceDatabase.ReadSchemaVersion(migratedConnection));
        }

        [Fact]
        public void Migrate_RunTwice_ChangesNothingTheSecondTime()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionOneWorkspace.Create(workspace.DatabasePath);

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            List<string> schemaAfterFirst;
            List<string> versionOneFactsAfterFirst;

            using (var first = WorkspaceDatabase.OpenConnection(workspace.DatabasePath))
            {
                schemaAfterFirst = ReadSchemaObjects(first);
                versionOneFactsAfterFirst = ReadVersionOneFacts(first);
            }

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(2, WorkspaceDatabase.ReadSchemaVersion(connection));
            Assert.Equal(schemaAfterFirst, ReadSchemaObjects(connection));
            Assert.Equal(versionOneFactsAfterFirst, ReadVersionOneFacts(connection));

            // The backfill must not run again and create a second attachment for the same message.
            Assert.Equal(1, CountRows(connection, "Attachment"));
        }

        // ---------------------------------------------------------------------------------------
        // Atomicity.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The failure is induced by an object in the version-1 database whose name collides with a
        /// version-2 table. <c>Attachment</c> is created last of the five, so four tables have
        /// already been created inside the transaction when the fifth statement fails: the
        /// partially-migrated state that must not survive.
        /// <para>
        /// A collision is used rather than fault injection because the production code should not
        /// grow a seam that exists only for a test. It does mean the backfill is never reached — the
        /// backfill runs after all five tables exist — so this proves that a failed migration commits
        /// no schema change and no version change. That the backfill rows would go the same way is
        /// carried by the migration being one transaction, which
        /// <see cref="Migrate_WhenMigrationFails_LeavesTheOriginalDatabaseUsableAtVersionOne"/> then
        /// confirms from the outside.
        /// </para>
        /// </remarks>
        [Fact]
        public void Migrate_WhenMigrationFails_LeavesVersionOneAndNoVersionTwoTables()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionOneWorkspace.Create(workspace.DatabasePath);

            using (var setup = SyntheticVersionOneWorkspace.OpenPlainConnection(workspace.DatabasePath))
            {
                Execute(setup, "CREATE TABLE Attachment (Unrelated TEXT NOT NULL);");
            }

            Assert.Throws<SqliteException>(() => WorkspaceDatabase.Initialise(workspace.DatabasePath));

            using var connection = SyntheticVersionOneWorkspace.OpenPlainConnection(workspace.DatabasePath);

            Assert.Equal(1, WorkspaceDatabase.ReadSchemaVersion(connection));

            Assert.False(TableExists(connection, "MediaSource"));
            Assert.False(TableExists(connection, "MediaAsset"));
            Assert.False(TableExists(connection, "MediaFile"));
            Assert.False(TableExists(connection, "MediaAssetFile"));

            // The rollback undid only what the failed attempt created.
            Assert.True(ColumnExists(connection, "Attachment", "Unrelated"));
        }

        [Fact]
        public void Migrate_WhenMigrationFails_LeavesTheOriginalDatabaseUsableAtVersionOne()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionOneWorkspace.Create(workspace.DatabasePath);

            var before = ReadVersionOneFacts(workspace.DatabasePath);

            using (var setup = SyntheticVersionOneWorkspace.OpenPlainConnection(workspace.DatabasePath))
            {
                Execute(setup, "CREATE TABLE Attachment (Unrelated TEXT NOT NULL);");
            }

            Assert.Throws<SqliteException>(() => WorkspaceDatabase.Initialise(workspace.DatabasePath));

            Assert.Equal(before, ReadVersionOneFacts(workspace.DatabasePath));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private static List<string> ReadVersionOneFacts(string databasePath)
        {
            using var connection = SyntheticVersionOneWorkspace.OpenPlainConnection(databasePath);

            return ReadVersionOneFacts(connection);
        }

        /// <summary>
        /// Reads every version-1 row as text, so the whole of the pre-existing data can be compared
        /// before and after a migration in one assertion.
        /// </summary>
        /// <remarks>
        /// Includes the identifiers and the ordering columns, so a row that was rewritten, renumbered
        /// or reordered would change the result even if the counts stayed the same.
        /// </remarks>
        private static List<string> ReadVersionOneFacts(SqliteConnection connection)
        {
            var facts = new List<string>();

            foreach (var sql in VersionOneFactQueries)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    var values = new string[reader.FieldCount];

                    for (var field = 0; field < reader.FieldCount; field++)
                    {
                        values[field] = reader.IsDBNull(field)
                            ? "<null>"
                            : reader.GetValue(field).ToString() ?? "<null>";
                    }

                    facts.Add(string.Join("\u001f", values));
                }
            }

            return facts;
        }

        private static readonly string[] VersionOneFactQueries =
        [
            "SELECT * FROM ImportSource ORDER BY ImportSourceID;",
            "SELECT * FROM Conversation ORDER BY ConversationID;",
            "SELECT * FROM Participant ORDER BY ParticipantID;",
            "SELECT * FROM ConversationParticipant ORDER BY ConversationParticipantID;",
            "SELECT * FROM Message ORDER BY MessageID;",
        ];

        /// <summary>
        /// Reads every schema object SQLite records, so two databases' schemas can be compared
        /// exactly rather than by checking that a list of names exists.
        /// </summary>
        /// <remarks>
        /// Includes the automatic indexes SQLite creates for primary keys and unique constraints,
        /// whose names are derived from the table's, so a missing or extra constraint shows up here
        /// too.
        /// </remarks>
        private static List<string> ReadSchemaObjects(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT type, name, tbl_name, COALESCE(sql, '<automatic>')
                FROM sqlite_master
                ORDER BY type, name;
                """;

            using var reader = command.ExecuteReader();

            var objects = new List<string>();

            while (reader.Read())
            {
                objects.Add(
                    $"{reader.GetString(0)}{reader.GetString(1)}" +
                    $"{reader.GetString(2)}{reader.GetString(3)}");
            }

            return objects;
        }
    }
}
