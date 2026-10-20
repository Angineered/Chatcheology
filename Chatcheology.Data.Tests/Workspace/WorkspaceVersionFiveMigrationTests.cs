using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Workspace.WorkspaceTestData;

namespace Chatcheology.Data.Tests.Workspace
{
    /// <summary>
    /// Tests the version-4-to-5 migration (CH-22): additive, one transaction, and refused for
    /// anything that is not exactly a version-4 workspace.
    /// </summary>
    /// <remarks>
    /// The version-4 starting point is the synthetic version-2 fixture migrated to the current
    /// schema with the version-5 objects then dropped and <c>user_version</c> set back to 4. The
    /// remaining objects are the ones the unchanged version-3 and version-4 statements created,
    /// which is exactly what a version-4 build left behind. A Phase 7 run row and an attachment
    /// msgstore fact are added so neither the Phase 7 tables nor the version-4 table is empty when
    /// the migration is checked for leaving them alone. Synthetic data only.
    /// </remarks>
    public class WorkspaceVersionFiveMigrationTests
    {
        private const string MessageFactTable = "MessageMsgstoreFact";

        private const string MessageFactIndex = "IX_MessageMsgstoreFact_SourceBatch";

        private static readonly string[] VersionFourTables =
            SyntheticVersionTwoWorkspace.Tables
                .Concat(WorkspaceDatabase.VersionThreeTableNames)
                .Append("AttachmentMsgstoreFact")
                .ToArray();

        [Fact]
        public void Migrate_VersionFourDatabase_BecomesVersionFiveWithAnEmptyMessageFactTable()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            CreateVersionFour(workspace.DatabasePath);

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(5, WorkspaceDatabase.ReadSchemaVersion(connection));
            Assert.True(TableExists(connection, MessageFactTable));
            Assert.Equal(0, CountRows(connection, MessageFactTable));
            Assert.Contains(MessageFactIndex, WorkspaceDatabase.ReadNamedIndexNames(connection));
        }

        /// <remarks>
        /// Every schema object a version-4 workspace has is still there with its exact definition,
        /// and the only new objects are the message fact table and its index. Every row of every
        /// version-4 table is unchanged, Phase 7 tables and the attachment fact table included.
        /// </remarks>
        [Fact]
        public void Migrate_VersionFourDatabase_IsAdditiveAndLeavesEveryVersionFourObjectIdentical()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var checkpoint = new TemporaryWorkspaceDatabase();

            CreateVersionFour(workspace.DatabasePath);

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

            Assert.Equal(new[] { MessageFactIndex, MessageFactTable }.Order().ToList(), added);

            WorkspaceCheckpoint.RequireIdenticalTables(connection, checkpointPath, VersionFourTables);

            Assert.Equal(1, CountRows(connection, "Phase7Run"));
            Assert.Equal(1, CountRows(connection, "AttachmentMsgstoreFact"));
        }

        [Fact]
        public void Migrate_VersionFourDatabase_ProducesTheSameSchemaAsCreatingFresh()
        {
            using var fresh = new TemporaryWorkspaceDatabase();
            using var fromVersionFour = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(fresh.DatabasePath);

            CreateVersionFour(fromVersionFour.DatabasePath);
            WorkspaceDatabase.Initialise(fromVersionFour.DatabasePath);

            using var freshConnection = WorkspaceDatabase.OpenConnection(fresh.DatabasePath);
            using var migrated = WorkspaceDatabase.OpenConnection(fromVersionFour.DatabasePath);

            Assert.Equal(ReadSchemaObjects(freshConnection), ReadSchemaObjects(migrated));
        }

        [Fact]
        public void Migrate_VersionsOneTwoAndThree_ReachVersionFiveStraight()
        {
            using var fromVersionOne = new TemporaryWorkspaceDatabase();
            using var fromVersionTwo = new TemporaryWorkspaceDatabase();
            using var fromVersionThree = new TemporaryWorkspaceDatabase();
            using var fresh = new TemporaryWorkspaceDatabase();

            SyntheticVersionOneWorkspace.Create(fromVersionOne.DatabasePath);
            SyntheticVersionTwoWorkspace.Create(fromVersionTwo.DatabasePath);
            WorkspaceVersionFourMigrationTests.CreateVersionThree(fromVersionThree.DatabasePath);

            WorkspaceDatabase.Initialise(fresh.DatabasePath);

            List<string> expected;

            using (var freshConnection = WorkspaceDatabase.OpenConnection(fresh.DatabasePath))
            {
                expected = ReadSchemaObjects(freshConnection);
            }

            foreach (var path in new[]
            {
                fromVersionOne.DatabasePath, fromVersionTwo.DatabasePath, fromVersionThree.DatabasePath,
            })
            {
                WorkspaceDatabase.Initialise(path);

                using var connection = WorkspaceDatabase.OpenConnection(path);

                Assert.Equal(5, WorkspaceDatabase.ReadSchemaVersion(connection));
                Assert.True(TableExists(connection, MessageFactTable));
                Assert.Equal(0, CountRows(connection, MessageFactTable));
                Assert.Equal(expected, ReadSchemaObjects(connection));
            }
        }

        // ---------------------------------------------------------------------------------------
        // The version-4 shape gate: refused, and nothing written.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("CREATE TABLE Unexpected (Value TEXT NOT NULL);", "Unexpected")]
        [InlineData("DROP TABLE AttachmentMsgstoreFact;", "AttachmentMsgstoreFact")]
        [InlineData("DROP TABLE AttachmentAdjacentDateContext;", "AttachmentAdjacentDateContext")]
        [InlineData("DROP INDEX IX_AttachmentMsgstoreFact_SourceBatch;", "IX_AttachmentMsgstoreFact_SourceBatch")]
        [InlineData("DROP INDEX IX_AttachmentReviewEvent_AttachmentID;", "IX_AttachmentReviewEvent_AttachmentID")]
        [InlineData("CREATE INDEX IX_Extra ON Message (MessageDateTimeLocal);", "IX_Extra")]
        [InlineData(
            "CREATE TABLE MessageMsgstoreFact (MessageID INTEGER PRIMARY KEY);",
            "MessageMsgstoreFact")]
        public void Gate_MalformedVersionFour_IsRefusedWithNothingWritten(string damage, string named)
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            CreateVersionFour(workspace.DatabasePath);

            using (var setup = SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath))
            {
                Execute(setup, damage);
            }

            SqliteConnection.ClearAllPools();

            var before = WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath);

            var exception = Assert.Throws<InvalidOperationException>(
                () => WorkspaceDatabase.Initialise(workspace.DatabasePath));

            Assert.Contains(named, exception.Message, StringComparison.Ordinal);
            Assert.Contains("still version 4", exception.Message, StringComparison.Ordinal);

            SqliteConnection.ClearAllPools();

            Assert.Equal(before, WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath));

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath);

            Assert.Equal(4, WorkspaceDatabase.ReadSchemaVersion(connection));
        }

        /// <remarks>
        /// A view whose name collides with the version-5 index passes the gate (it is neither a
        /// table nor an index) and makes the migration fail after the message fact table already
        /// exists inside the transaction. None of it may survive.
        /// </remarks>
        [Fact]
        public void Migrate_WhenVersionFiveFails_LeavesVersionFourAndNoMessageFactTable()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var checkpoint = new TemporaryWorkspaceDatabase();

            CreateVersionFour(workspace.DatabasePath);

            using (var setup = SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath))
            {
                Execute(setup, $"CREATE VIEW {MessageFactIndex} AS SELECT 1 AS Value;");
            }

            var checkpointPath = Path.Combine(checkpoint.DirectoryPath, "before.db");

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(workspace.DatabasePath, checkpointPath);

            Assert.Throws<SqliteException>(() => WorkspaceDatabase.Initialise(workspace.DatabasePath));

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath);

            Assert.Equal(4, WorkspaceDatabase.ReadSchemaVersion(connection));
            Assert.False(TableExists(connection, MessageFactTable));
            Assert.DoesNotContain(MessageFactIndex, WorkspaceDatabase.ReadNamedIndexNames(connection));

            WorkspaceCheckpoint.RequireIdenticalTables(connection, checkpointPath, VersionFourTables);
        }

        // ---------------------------------------------------------------------------------------
        // A version-5 tool against a version-4 workspace.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void VersionFiveWritersAndReaders_RefuseAVersionFourWorkspaceWithAMigrateFirstMessage()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            CreateVersionFour(workspace.DatabasePath);

            SqliteConnection.ClearAllPools();

            var before = WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath);

            var refusals = new List<InvalidOperationException>
            {
                Assert.Throws<InvalidOperationException>(
                    () => MessageMsgstoreFactWriter.InsertBatch(
                        workspace.DatabasePath,
                        "text-1",
                        [new MessageMsgstoreFactRow(1, true, 1)],
                        DateTime.UtcNow)),

                Assert.Throws<InvalidOperationException>(
                    () => MessageMsgstoreFactWriter.DeleteBatch(workspace.DatabasePath, "text-1")),

                Assert.Throws<InvalidOperationException>(
                    () => MsgstoreFactWriter.DeleteBatch(workspace.DatabasePath, "batch-1")),
            };

            using (var connection = WorkspaceDatabase.OpenReadOnlyConnection(workspace.DatabasePath))
            {
                refusals.Add(Assert.Throws<InvalidOperationException>(
                    () => new MessageMsgstoreFactReader().Read(connection, [1])));
            }

            foreach (var exception in refusals)
            {
                Assert.Contains("schema version 4", exception.Message, StringComparison.Ordinal);
                Assert.Contains("migrate it first", exception.Message, StringComparison.Ordinal);
            }

            SqliteConnection.ClearAllPools();

            Assert.Equal(before, WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Builds a version-4 workspace: the synthetic version-2 fixture through the unchanged
        /// version-3 and version-4 statements, without the version-5 objects, plus one Phase 7 run
        /// row and one attachment msgstore fact.
        /// </summary>
        internal static void CreateVersionFour(string databasePath)
        {
            SyntheticVersionTwoWorkspace.Create(databasePath);
            WorkspaceDatabase.Initialise(databasePath);

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(databasePath);

            Execute(connection, $"DROP INDEX {MessageFactIndex};");
            Execute(connection, $"DROP TABLE {MessageFactTable};");

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

            Execute(
                connection,
                $"""
                INSERT INTO AttachmentMsgstoreFact (
                    AttachmentID, IsForwarded, ForwardScore, LowQualityCopyOfAttachmentID,
                    SourceBatch, RecordedDateTimeUtc)
                SELECT AttachmentID, 1, 3, NULL, 'synthetic-v4', '{ImportedDateTimeUtcText}'
                FROM Attachment
                ORDER BY AttachmentID
                LIMIT 1;
                """);

            Execute(connection, "PRAGMA user_version = 4;");
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
