using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Workspace.WorkspaceTestData;

namespace Chatcheology.Data.Tests.Workspace
{
    /// <summary>
    /// Tests the version-3 migration (CH-8, CH-10): additive, one transaction, and refused for
    /// anything that is not exactly a version-3 workspace. Since version 5 (CH-22) a version-3
    /// workspace reaches version 5 in the same single transaction, so the version-5 table and index
    /// are among the added objects; version 4 to 5 has its own tests in
    /// <see cref="WorkspaceVersionFiveMigrationTests"/>.
    /// </summary>
    /// <remarks>
    /// The version-3 starting point is the synthetic version-2 fixture migrated to the current
    /// schema with the version-4 and version-5 objects then dropped and <c>user_version</c> set back
    /// to 3. The
    /// remaining objects are the ones the unchanged version-3 statements created, which is exactly
    /// what a version-3 build left behind. A Phase 7 run row is added so the Phase 7 tables are not
    /// empty when the migration is checked for leaving them alone. Synthetic data only.
    /// </remarks>
    public class WorkspaceVersionFourMigrationTests
    {
        private const string FactTable = "AttachmentMsgstoreFact";

        private const string MessageFactTable = "MessageMsgstoreFact";

        private static readonly string[] FactIndexes =
        [
            "IX_AttachmentMsgstoreFact_LowQualityCopyOfAttachmentID",
            "IX_AttachmentMsgstoreFact_SourceBatch",
        ];

        private const string MessageFactIndex = "IX_MessageMsgstoreFact_SourceBatch";

        private static readonly string[] VersionThreeTables =
            SyntheticVersionTwoWorkspace.Tables
                .Concat(WorkspaceDatabase.VersionThreeTableNames)
                .ToArray();

        [Fact]
        public void Migrate_VersionThreeDatabase_BecomesVersionFiveWithEmptyFactTables()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            CreateVersionThree(workspace.DatabasePath);

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(5, WorkspaceDatabase.ReadSchemaVersion(connection));

            foreach (var table in new[] { FactTable, MessageFactTable })
            {
                Assert.True(TableExists(connection, table), table);
                Assert.Equal(0, CountRows(connection, table));
            }

            var indexes = WorkspaceDatabase.ReadNamedIndexNames(connection);

            foreach (var index in FactIndexes.Append(MessageFactIndex))
            {
                Assert.Contains(index, indexes);
            }
        }

        /// <remarks>
        /// Every schema object a version-3 workspace has is still there with its exact definition,
        /// and the only new objects are the two fact tables and their three indexes. Every row of every
        /// version-3 table is unchanged, Phase 7 tables included.
        /// </remarks>
        [Fact]
        public void Migrate_VersionThreeDatabase_IsAdditiveAndLeavesEveryVersionThreeObjectIdentical()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var checkpoint = new TemporaryWorkspaceDatabase();

            CreateVersionThree(workspace.DatabasePath);

            List<string> before;

            using (var plain = SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath))
            {
                before = ReadSchemaObjects(plain);
            }

            var checkpointPath = Path.Combine(checkpoint.DirectoryPath, "before.db");

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(workspace.DatabasePath, checkpointPath);

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            var after = ReadSchemaObjects(connection);

            Assert.Empty(before.Except(after));

            var added = after.Except(before).Select(entry => entry.Split('|')[1]).Order().ToList();

            Assert.Equal(
                FactIndexes.Append(MessageFactIndex).Append(FactTable).Append(MessageFactTable)
                    .Order().ToList(),
                added);

            WorkspaceCheckpoint.RequireIdenticalTables(connection, checkpointPath, VersionThreeTables);

            Assert.Equal(1, CountRows(connection, "Phase7Run"));
        }

        [Fact]
        public void Migrate_VersionThreeDatabase_ProducesTheSameSchemaAsCreatingFresh()
        {
            using var fresh = new TemporaryWorkspaceDatabase();
            using var fromVersionThree = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(fresh.DatabasePath);

            CreateVersionThree(fromVersionThree.DatabasePath);
            WorkspaceDatabase.Initialise(fromVersionThree.DatabasePath);

            using var freshConnection = WorkspaceDatabase.OpenConnection(fresh.DatabasePath);
            using var migrated = WorkspaceDatabase.OpenConnection(fromVersionThree.DatabasePath);

            Assert.Equal(ReadSchemaObjects(freshConnection), ReadSchemaObjects(migrated));
        }

        [Fact]
        public void Migrate_VersionOneAndVersionTwo_StillReachVersionFive()
        {
            using var fromVersionOne = new TemporaryWorkspaceDatabase();
            using var fromVersionTwo = new TemporaryWorkspaceDatabase();

            SyntheticVersionOneWorkspace.Create(fromVersionOne.DatabasePath);
            SyntheticVersionTwoWorkspace.Create(fromVersionTwo.DatabasePath);

            WorkspaceDatabase.Initialise(fromVersionOne.DatabasePath);
            WorkspaceDatabase.Initialise(fromVersionTwo.DatabasePath);

            foreach (var path in new[] { fromVersionOne.DatabasePath, fromVersionTwo.DatabasePath })
            {
                using var connection = WorkspaceDatabase.OpenConnection(path);

                Assert.Equal(5, WorkspaceDatabase.ReadSchemaVersion(connection));

                foreach (var table in new[] { FactTable, MessageFactTable })
                {
                    Assert.True(TableExists(connection, table), table);
                    Assert.Equal(0, CountRows(connection, table));
                }
            }
        }

        // ---------------------------------------------------------------------------------------
        // The version-3 shape gate: refused, and nothing written.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("CREATE TABLE Unexpected (Value TEXT NOT NULL);", "Unexpected")]
        [InlineData("DROP TABLE AttachmentAdjacentDateContext;", "AttachmentAdjacentDateContext")]
        [InlineData("DROP INDEX IX_AttachmentReviewEvent_AttachmentID;", "IX_AttachmentReviewEvent_AttachmentID")]
        [InlineData("CREATE INDEX IX_Extra ON Message (MessageDateTimeLocal);", "IX_Extra")]
        [InlineData(
            "CREATE TABLE AttachmentMsgstoreFact (AttachmentID INTEGER PRIMARY KEY);",
            "AttachmentMsgstoreFact")]
        public void Gate_MalformedVersionThree_IsRefusedWithNothingWritten(string damage, string named)
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            CreateVersionThree(workspace.DatabasePath);

            using (var setup = SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath))
            {
                Execute(setup, damage);
            }

            var before = WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath);

            var exception = Assert.Throws<InvalidOperationException>(
                () => WorkspaceDatabase.Initialise(workspace.DatabasePath));

            Assert.Contains(named, exception.Message, StringComparison.Ordinal);
            Assert.Contains("still version 3", exception.Message, StringComparison.Ordinal);

            Assert.Equal(before, WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath));

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath);

            Assert.Equal(3, WorkspaceDatabase.ReadSchemaVersion(connection));
        }

        /// <remarks>
        /// A view whose name collides with the second version-4 index passes the gate (it is neither
        /// a table nor an index) and makes the migration fail after the fact table and the first
        /// index already exist inside the transaction. None of it may survive.
        /// </remarks>
        [Fact]
        public void Migrate_WhenVersionFourFails_LeavesVersionThreeAndNoFactTable()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var checkpoint = new TemporaryWorkspaceDatabase();

            CreateVersionThree(workspace.DatabasePath);

            using (var setup = SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath))
            {
                Execute(
                    setup,
                    "CREATE VIEW IX_AttachmentMsgstoreFact_SourceBatch AS SELECT 1 AS Value;");
            }

            var checkpointPath = Path.Combine(checkpoint.DirectoryPath, "before.db");

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(workspace.DatabasePath, checkpointPath);

            Assert.Throws<SqliteException>(() => WorkspaceDatabase.Initialise(workspace.DatabasePath));

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath);

            Assert.Equal(3, WorkspaceDatabase.ReadSchemaVersion(connection));
            Assert.False(TableExists(connection, FactTable));
            Assert.False(TableExists(connection, MessageFactTable));
            Assert.DoesNotContain(FactIndexes[0], WorkspaceDatabase.ReadNamedIndexNames(connection));

            WorkspaceCheckpoint.RequireIdenticalTables(connection, checkpointPath, VersionThreeTables);
        }

        // ---------------------------------------------------------------------------------------
        // A version-4 tool against a version-3 workspace.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AVersionFourWriter_RefusesAVersionThreeWorkspaceWithAMigrateFirstMessage()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            CreateVersionThree(workspace.DatabasePath);

            var before = WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath);

            var exception = Assert.Throws<InvalidOperationException>(
                () => MsgstoreFactWriter.InsertBatch(
                    workspace.DatabasePath,
                    "batch-1",
                    [new MsgstoreFactRow(1, true, 1, null)],
                    DateTime.UtcNow));

            Assert.Contains("schema version 3", exception.Message, StringComparison.Ordinal);
            Assert.Contains("migrate it first", exception.Message, StringComparison.Ordinal);

            Assert.Equal(before, WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Builds a version-3 workspace: the synthetic version-2 fixture through the unchanged
        /// version-3 statements, without the version-4 and version-5 objects, plus one Phase 7 run
        /// row.
        /// </summary>
        internal static void CreateVersionThree(string databasePath)
        {
            SyntheticVersionTwoWorkspace.Create(databasePath);
            WorkspaceDatabase.Initialise(databasePath);

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(databasePath);

            Execute(connection, "DROP INDEX IX_MessageMsgstoreFact_SourceBatch;");
            Execute(connection, "DROP TABLE MessageMsgstoreFact;");

            Execute(connection, "DROP INDEX IX_AttachmentMsgstoreFact_SourceBatch;");
            Execute(connection, "DROP INDEX IX_AttachmentMsgstoreFact_LowQualityCopyOfAttachmentID;");
            Execute(connection, "DROP TABLE AttachmentMsgstoreFact;");

            Execute(
                connection,
                $"""
                INSERT INTO Phase7Run (
                    StartedAtUtc, CompletedAtUtc, Status, PreRunWorkspaceFileSha256,
                    RankingDimensionSetVersion)
                VALUES (
                    '{ImportedDateTimeUtcText}', '{ImportedDateTimeUtcText}', 'Completed',
                    '{new string('A', 64)}', 'synthetic');
                """);

            Execute(connection, "PRAGMA user_version = 3;");
        }

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
                    $"{reader.GetString(0)}|{reader.GetString(1)}|" +
                    $"{reader.GetString(2)}|{reader.GetString(3)}");
            }

            return objects;
        }
    }
}
