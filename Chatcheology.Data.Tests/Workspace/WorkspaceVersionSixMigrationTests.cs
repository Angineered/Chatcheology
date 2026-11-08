using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Workspace.WorkspaceTestData;

namespace Chatcheology.Data.Tests.Workspace
{
    /// <summary>
    /// Tests the version-5-to-6 migration (CH-71): additive, one transaction, and refused for
    /// anything that is not exactly a version-5 workspace.
    /// </summary>
    /// <remarks>
    /// The version-5 starting point is the synthetic version-2 fixture migrated to the current
    /// schema with the version-6 objects then dropped and <c>user_version</c> set back to 5. A
    /// Phase 7 run row, an attachment msgstore fact and a text-message msgstore fact are added so
    /// none of those tables is empty when the migration is checked for leaving them alone.
    /// Synthetic data only.
    /// </remarks>
    public class WorkspaceVersionSixMigrationTests
    {
        private const string CaptionTable = "AttachmentCaptionFact";

        private const string CaptionIndex = "IX_AttachmentCaptionFact_SourceBatch";

        private static readonly string[] VersionFiveTables =
            SyntheticVersionTwoWorkspace.Tables
                .Concat(WorkspaceDatabase.VersionThreeTableNames)
                .Append("AttachmentMsgstoreFact")
                .Append("MessageMsgstoreFact")
                .ToArray();

        [Fact]
        public void Migrate_VersionFiveDatabase_BecomesVersionSixWithAnEmptyCaptionTable()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            CreateVersionFive(workspace.DatabasePath);

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(6, WorkspaceDatabase.ReadSchemaVersion(connection));
            Assert.True(TableExists(connection, CaptionTable));
            Assert.Equal(0, CountRows(connection, CaptionTable));
            Assert.Contains(CaptionIndex, WorkspaceDatabase.ReadNamedIndexNames(connection));
        }

        /// <remarks>
        /// Every schema object a version-5 workspace has is still there with its exact definition,
        /// and the only new objects are the caption table and its index. Every row of every
        /// version-5 table is unchanged, Phase 7 and both msgstore fact tables included.
        /// </remarks>
        [Fact]
        public void Migrate_VersionFiveDatabase_IsAdditiveAndLeavesEveryVersionFiveObjectIdentical()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var checkpoint = new TemporaryWorkspaceDatabase();

            CreateVersionFive(workspace.DatabasePath);

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

            Assert.Equal(new[] { CaptionIndex, CaptionTable }.Order().ToList(), added);

            WorkspaceCheckpoint.RequireIdenticalTables(connection, checkpointPath, VersionFiveTables);

            Assert.Equal(1, CountRows(connection, "Phase7Run"));
            Assert.Equal(1, CountRows(connection, "AttachmentMsgstoreFact"));
            Assert.Equal(1, CountRows(connection, "MessageMsgstoreFact"));
        }

        [Fact]
        public void Migrate_VersionFiveDatabase_ProducesTheSameSchemaAsCreatingFresh()
        {
            using var fresh = new TemporaryWorkspaceDatabase();
            using var fromVersionFive = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(fresh.DatabasePath);

            CreateVersionFive(fromVersionFive.DatabasePath);
            WorkspaceDatabase.Initialise(fromVersionFive.DatabasePath);

            using var freshConnection = WorkspaceDatabase.OpenConnection(fresh.DatabasePath);
            using var migrated = WorkspaceDatabase.OpenConnection(fromVersionFive.DatabasePath);

            Assert.Equal(ReadSchemaObjects(freshConnection), ReadSchemaObjects(migrated));
        }

        [Fact]
        public void Migrate_VersionsOneToFour_ReachVersionSixStraight()
        {
            using var fromVersionOne = new TemporaryWorkspaceDatabase();
            using var fromVersionTwo = new TemporaryWorkspaceDatabase();
            using var fromVersionThree = new TemporaryWorkspaceDatabase();
            using var fromVersionFour = new TemporaryWorkspaceDatabase();
            using var fresh = new TemporaryWorkspaceDatabase();

            SyntheticVersionOneWorkspace.Create(fromVersionOne.DatabasePath);
            SyntheticVersionTwoWorkspace.Create(fromVersionTwo.DatabasePath);
            WorkspaceVersionFourMigrationTests.CreateVersionThree(fromVersionThree.DatabasePath);
            WorkspaceVersionFiveMigrationTests.CreateVersionFour(fromVersionFour.DatabasePath);

            WorkspaceDatabase.Initialise(fresh.DatabasePath);

            List<string> expected;

            using (var freshConnection = WorkspaceDatabase.OpenConnection(fresh.DatabasePath))
            {
                expected = ReadSchemaObjects(freshConnection);
            }

            foreach (var path in new[]
            {
                fromVersionOne.DatabasePath, fromVersionTwo.DatabasePath, fromVersionThree.DatabasePath,
                fromVersionFour.DatabasePath,
            })
            {
                WorkspaceDatabase.Initialise(path);

                using var connection = WorkspaceDatabase.OpenConnection(path);

                Assert.Equal(6, WorkspaceDatabase.ReadSchemaVersion(connection));
                Assert.True(TableExists(connection, CaptionTable));
                Assert.Equal(0, CountRows(connection, CaptionTable));
                Assert.Equal(expected, ReadSchemaObjects(connection));
            }
        }

        // ---------------------------------------------------------------------------------------
        // The version-5 shape gate: refused, and nothing written.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("CREATE TABLE Unexpected (Value TEXT NOT NULL);", "Unexpected")]
        [InlineData("DROP TABLE MessageMsgstoreFact;", "MessageMsgstoreFact")]
        [InlineData("DROP TABLE AttachmentMsgstoreFact;", "AttachmentMsgstoreFact")]
        [InlineData("DROP INDEX IX_MessageMsgstoreFact_SourceBatch;", "IX_MessageMsgstoreFact_SourceBatch")]
        [InlineData("CREATE INDEX IX_Extra ON Message (MessageDateTimeLocal);", "IX_Extra")]
        [InlineData(
            "CREATE TABLE AttachmentCaptionFact (AttachmentID INTEGER PRIMARY KEY);",
            "AttachmentCaptionFact")]
        public void Gate_MalformedVersionFive_IsRefusedWithNothingWritten(string damage, string named)
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            CreateVersionFive(workspace.DatabasePath);

            using (var setup = SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath))
            {
                Execute(setup, damage);
            }

            SqliteConnection.ClearAllPools();

            var before = WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath);

            var exception = Assert.Throws<InvalidOperationException>(
                () => WorkspaceDatabase.Initialise(workspace.DatabasePath));

            Assert.Contains(named, exception.Message, StringComparison.Ordinal);
            Assert.Contains("still version 5", exception.Message, StringComparison.Ordinal);

            SqliteConnection.ClearAllPools();

            Assert.Equal(before, WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath));

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath);

            Assert.Equal(5, WorkspaceDatabase.ReadSchemaVersion(connection));
        }

        /// <remarks>
        /// A view whose name collides with the version-6 index passes the gate and makes the
        /// migration fail after the caption table already exists inside the transaction. None of it
        /// may survive.
        /// </remarks>
        [Fact]
        public void Migrate_WhenVersionSixFails_LeavesVersionFiveAndNoCaptionTable()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var checkpoint = new TemporaryWorkspaceDatabase();

            CreateVersionFive(workspace.DatabasePath);

            using (var setup = SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath))
            {
                Execute(setup, $"CREATE VIEW {CaptionIndex} AS SELECT 1 AS Value;");
            }

            var checkpointPath = Path.Combine(checkpoint.DirectoryPath, "before.db");

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(workspace.DatabasePath, checkpointPath);

            Assert.Throws<SqliteException>(() => WorkspaceDatabase.Initialise(workspace.DatabasePath));

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath);

            Assert.Equal(5, WorkspaceDatabase.ReadSchemaVersion(connection));
            Assert.False(TableExists(connection, CaptionTable));
            Assert.DoesNotContain(CaptionIndex, WorkspaceDatabase.ReadNamedIndexNames(connection));

            WorkspaceCheckpoint.RequireIdenticalTables(connection, checkpointPath, VersionFiveTables);
        }

        // ---------------------------------------------------------------------------------------
        // A version-6 tool against a version-5 workspace.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void VersionSixWritersAndReaders_RefuseAVersionFiveWorkspaceWithAMigrateFirstMessage()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            CreateVersionFive(workspace.DatabasePath);

            SqliteConnection.ClearAllPools();

            var before = WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath);

            var refusals = new List<InvalidOperationException>
            {
                Assert.Throws<InvalidOperationException>(
                    () => AttachmentCaptionFactWriter.InsertBatch(
                        workspace.DatabasePath,
                        "caption-1",
                        [new AttachmentCaptionFactRow(1, "Synthetic caption", false)],
                        DateTime.UtcNow)),

                Assert.Throws<InvalidOperationException>(
                    () => AttachmentCaptionFactWriter.DeleteBatch(workspace.DatabasePath, "caption-1")),

                Assert.Throws<InvalidOperationException>(
                    () => MessageMsgstoreFactWriter.DeleteBatch(workspace.DatabasePath, "text-1")),
            };

            using (var connection = WorkspaceDatabase.OpenReadOnlyConnection(workspace.DatabasePath))
            {
                refusals.Add(Assert.Throws<InvalidOperationException>(
                    () => new AttachmentCaptionFactReader().Read(connection, [1])));
            }

            foreach (var exception in refusals)
            {
                Assert.Contains("schema version 5", exception.Message, StringComparison.Ordinal);
                Assert.Contains("migrate it first", exception.Message, StringComparison.Ordinal);
            }

            SqliteConnection.ClearAllPools();

            Assert.Equal(before, WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Builds a version-5 workspace: the version-4 fixture through the unchanged version-5
        /// statements, without the version-6 objects, plus one text-message msgstore fact.
        /// </summary>
        internal static void CreateVersionFive(string databasePath)
        {
            SyntheticVersionTwoWorkspace.Create(databasePath);
            WorkspaceDatabase.Initialise(databasePath);

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(databasePath);

            Execute(connection, $"DROP INDEX {CaptionIndex};");
            Execute(connection, $"DROP TABLE {CaptionTable};");

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

            Execute(
                connection,
                $"""
                INSERT INTO MessageMsgstoreFact (
                    MessageID, IsForwarded, ForwardScore, SourceBatch, RecordedDateTimeUtc)
                SELECT m.MessageID, 0, NULL, 'synthetic-v5', '{ImportedDateTimeUtcText}'
                FROM Message AS m
                WHERE m.MessageType = 'User'
                  AND NOT EXISTS (SELECT 1 FROM Attachment AS a WHERE a.MessageID = m.MessageID)
                ORDER BY m.MessageID
                LIMIT 1;
                """);

            Execute(connection, "PRAGMA user_version = 5;");
        }

        internal static List<string> ReadSchemaObjects(SqliteConnection connection)
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
