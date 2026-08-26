using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Workspace.WorkspaceTestData;

namespace Chatcheology.Data.Tests.Workspace
{
    /// <summary>
    /// Tests the migration to schema version 3 against genuine version-1 and version-2 databases,
    /// and the gate that decides whether a version-2 workspace may be migrated at all.
    /// </summary>
    /// <remarks>
    /// The starting point is always a synthetic fixture that builds the older schema itself. Migrating
    /// a database the current build created would prove nothing about migrating one a previous build
    /// created.
    /// </remarks>
    public class WorkspaceVersionThreeMigrationTests
    {
        /// <summary>The eight tables version 3 adds, and the only ones it adds.</summary>
        private static readonly string[] Phase7Tables =
        [
            "Phase7Run",
            "AttachmentEvidenceSnapshot",
            "AttachmentCandidate",
            "AttachmentCandidateOccurrenceEvidence",
            "AttachmentAdjacentDateContext",
            "AttachmentReviewEvent",
            "AttachmentCandidateDispositionEvent",
            "AttachmentResolutionEvent",
        ];

        /// <summary>The three indexes version 3 declares explicitly.</summary>
        private static readonly string[] Phase7Indexes =
        [
            "IX_AttachmentReviewEvent_AttachmentID",
            "IX_AttachmentCandidateDispositionEvent_AttachmentCandidateID",
            "IX_AttachmentResolutionEvent_AttachmentID",
        ];

        // ---------------------------------------------------------------------------------------
        // Version 2 to version 3.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Migrate_VersionTwoDatabase_BecomesVersionThree()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            using (var before = SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath))
            {
                // The fixture really is version 2, so what follows really is a migration.
                Assert.Equal(2, WorkspaceDatabase.ReadSchemaVersion(before));
                Assert.False(TableExists(before, "Phase7Run"));
            }

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(3, WorkspaceDatabase.ReadSchemaVersion(connection));

            foreach (var tableName in Phase7Tables)
            {
                Assert.True(TableExists(connection, tableName), tableName);
            }
        }

        [Fact]
        public void Migrate_VersionTwoDatabase_CreatesExactlyEighteenApplicationTables()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            var expected = SyntheticVersionTwoWorkspace.Tables.Concat(Phase7Tables).Order().ToList();

            Assert.Equal(18, expected.Count);
            Assert.Equal(expected, ReadApplicationTableNames(connection));
        }

        [Fact]
        public void Migrate_VersionTwoDatabase_CreatesTheThreeExplicitIndexes()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            var declared = ReadExplicitIndexNames(connection);

            foreach (var indexName in Phase7Indexes)
            {
                Assert.Contains(indexName, declared);
            }

            // Only three explicit indexes anywhere in the schema. Versions 1 and 2 declared none, so
            // an extra one here would be speculative indexing that crept in.
            Assert.Equal(3, declared.Count);

            // And the automatic ones, which are a consequence of the UNIQUE constraints rather than a
            // choice: one each for the four snapshot/candidate/occurrence/context uniqueness rules and
            // one for the resolution event's composite supersession parent key. The three tables with
            // no UNIQUE constraint contribute none.
            Assert.Equal(5, ReadAutomaticIndexNamesForPhase7Tables(connection).Count);
        }

        [Fact]
        public void Migrate_VersionTwoDatabase_LeavesEveryPhase7TableEmpty()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            // Nothing about the migration computes evidence, ranks a candidate or resolves anything.
            foreach (var tableName in Phase7Tables)
            {
                Assert.Equal(0, CountRows(connection, tableName));
            }
        }

        /// <remarks>
        /// The attachment the fixture carries must still be unresolved afterwards. A migration that
        /// resolved anything, or that touched the two columns resolution lives in, would be asserting
        /// a matching decision no phase has justified.
        /// </remarks>
        [Fact]
        public void Migrate_VersionTwoDatabase_LeavesEveryAttachmentUnresolved()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(
                SyntheticVersionTwoWorkspace.AttachmentCount,
                CountRows(connection, "Attachment"));

            Assert.Equal(
                SyntheticVersionTwoWorkspace.AttachmentCount,
                ScalarLong(
                    connection,
                    """
                    SELECT COUNT(*) FROM Attachment
                    WHERE ResolutionStatus = 'Unresolved' AND ResolvedMediaAssetID IS NULL;
                    """));
        }

        [Fact]
        public void Migrate_VersionTwoDatabase_DoesNotChangeAnyVersionTwoTableDefinition()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var checkpoint = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            var checkpointPath = Path.Combine(checkpoint.DirectoryPath, "before.db");

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(workspace.DatabasePath, checkpointPath);

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            // Definitions, row counts and every stored value, for all ten pre-existing tables.
            WorkspaceCheckpoint.RequireIdenticalTables(
                connection, checkpointPath, SyntheticVersionTwoWorkspace.Tables);
        }

        // ---------------------------------------------------------------------------------------
        // Version 1 to version 3.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Migrate_VersionOneDatabase_ReachesVersionThreeInOneStep()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionOneWorkspace.Create(workspace.DatabasePath);

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(3, WorkspaceDatabase.ReadSchemaVersion(connection));

            foreach (var tableName in SyntheticVersionTwoWorkspace.Tables.Concat(Phase7Tables))
            {
                Assert.True(TableExists(connection, tableName), tableName);
            }

            // The version-2 backfill still ran on the way through.
            Assert.Equal(1, CountRows(connection, "Attachment"));

            foreach (var tableName in Phase7Tables)
            {
                Assert.Equal(0, CountRows(connection, tableName));
            }
        }

        // ---------------------------------------------------------------------------------------
        // Fresh creation, idempotence and equivalence.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Initialise_NewDatabase_CreatesVersionThreeOutright()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(3, WorkspaceDatabase.ReadSchemaVersion(connection));
            Assert.Equal(18, ReadApplicationTableNames(connection).Count);
        }

        /// <remarks>
        /// The reason all three paths apply the same statement collections. If a database created
        /// fresh, one migrated from version 1 and one migrated from version 2 differed in any table,
        /// index or constraint, two of the three would be schemas no test had ever exercised.
        /// </remarks>
        [Fact]
        public void Migrate_FromEitherVersion_ProducesTheSameSchemaAsCreatingVersionThreeFresh()
        {
            using var fresh = new TemporaryWorkspaceDatabase();
            using var fromVersionOne = new TemporaryWorkspaceDatabase();
            using var fromVersionTwo = new TemporaryWorkspaceDatabase();

            SyntheticVersionOneWorkspace.Create(fromVersionOne.DatabasePath);
            SyntheticVersionTwoWorkspace.Create(fromVersionTwo.DatabasePath);

            WorkspaceDatabase.Initialise(fresh.DatabasePath);
            WorkspaceDatabase.Initialise(fromVersionOne.DatabasePath);
            WorkspaceDatabase.Initialise(fromVersionTwo.DatabasePath);

            using var freshConnection = WorkspaceDatabase.OpenConnection(fresh.DatabasePath);
            using var oneConnection = WorkspaceDatabase.OpenConnection(fromVersionOne.DatabasePath);
            using var twoConnection = WorkspaceDatabase.OpenConnection(fromVersionTwo.DatabasePath);

            var expected = ReadSchemaObjects(freshConnection);

            Assert.Equal(expected, ReadSchemaObjects(oneConnection));
            Assert.Equal(expected, ReadSchemaObjects(twoConnection));
        }

        [Fact]
        public void Initialise_OnVersionThree_ChangesNothing()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            List<string> schemaAfterFirst;

            using (var first = WorkspaceDatabase.OpenConnection(workspace.DatabasePath))
            {
                schemaAfterFirst = ReadSchemaObjects(first);
            }

            var checkpointPath = Path.Combine(workspace.DirectoryPath, "after-first.db");

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(workspace.DatabasePath, checkpointPath);

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(3, WorkspaceDatabase.ReadSchemaVersion(connection));
            Assert.Equal(schemaAfterFirst, ReadSchemaObjects(connection));

            WorkspaceCheckpoint.RequireIdenticalTables(
                connection, checkpointPath, SyntheticVersionTwoWorkspace.Tables);
        }

        // ---------------------------------------------------------------------------------------
        // The exact version-2 table-set gate.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The negative control. Without this passing, the two rejection tests below would prove
        /// only that the gate rejects everything.
        /// </remarks>
        [Fact]
        public void Gate_UnmodifiedVersionTwoWorkspace_IsAccepted()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(3, WorkspaceDatabase.ReadSchemaVersion(connection));
        }

        [Fact]
        public void Gate_VersionTwoWorkspaceWithAnExtraTable_IsRejectedBeforeAnyWrite()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            using (var setup = SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath))
            {
                Execute(setup, "CREATE TABLE Unexpected (Value TEXT NOT NULL);");
            }

            var exception = Assert.Throws<InvalidOperationException>(
                () => WorkspaceDatabase.Initialise(workspace.DatabasePath));

            Assert.Contains("Unexpected", exception.Message, StringComparison.Ordinal);

            AssertStillUnmigratedVersionTwo(workspace.DatabasePath);
        }

        [Fact]
        public void Gate_VersionTwoWorkspaceWithAMissingTable_IsRejectedBeforeAnyWrite()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            using (var setup = SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath))
            {
                // MediaAssetFile has no children, so dropping it leaves a database SQLite still
                // opens — an incomplete version-2 workspace rather than a broken file.
                Execute(setup, "DROP TABLE MediaAssetFile;");
            }

            var exception = Assert.Throws<InvalidOperationException>(
                () => WorkspaceDatabase.Initialise(workspace.DatabasePath));

            Assert.Contains("MediaAssetFile", exception.Message, StringComparison.Ordinal);

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(
                workspace.DatabasePath);

            Assert.Equal(2, WorkspaceDatabase.ReadSchemaVersion(connection));

            foreach (var tableName in Phase7Tables)
            {
                Assert.False(TableExists(connection, tableName), tableName);
            }
        }

        // ---------------------------------------------------------------------------------------
        // Atomicity: a failed migration must publish no part of version 3.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The failure is induced by an object whose name collides with a version-3 table.
        /// <c>AttachmentResolutionEvent</c> is created last, so seven Phase 7 tables and two of the
        /// three indexes already exist inside the transaction when the statement for it fails: the
        /// partially-published state that must not survive.
        /// <para>
        /// A collision rather than fault injection, because the production code should not grow a seam
        /// that exists only for a test. The gate has to be satisfied first, so the colliding object is
        /// an index rather than a table — an extra table would be refused before the migration began,
        /// which is a different test.
        /// </para>
        /// </remarks>
        [Fact]
        public void Migrate_WhenVersionThreeFails_LeavesVersionTwoAndNoPhase7Tables()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var checkpoint = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            using (var setup = SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath))
            {
                // An index name, so the table set still passes the gate and the failure happens
                // during the migration itself.
                Execute(
                    setup,
                    "CREATE INDEX IX_AttachmentResolutionEvent_AttachmentID ON Message (MessageID);");
            }

            var checkpointPath = Path.Combine(checkpoint.DirectoryPath, "before.db");

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(workspace.DatabasePath, checkpointPath);

            Assert.Throws<SqliteException>(() => WorkspaceDatabase.Initialise(workspace.DatabasePath));

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(
                workspace.DatabasePath);

            Assert.Equal(2, WorkspaceDatabase.ReadSchemaVersion(connection));

            foreach (var tableName in Phase7Tables)
            {
                Assert.False(TableExists(connection, tableName), tableName);
            }

            // And every pre-existing table is still exactly what it was.
            WorkspaceCheckpoint.RequireIdenticalTables(
                connection, checkpointPath, SyntheticVersionTwoWorkspace.Tables);
        }

        [Fact]
        public void Migrate_WhenVersionThreeFails_LeavesTheWorkspaceUsableAtVersionTwo()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            using (var setup = SyntheticVersionTwoWorkspace.OpenPlainConnection(workspace.DatabasePath))
            {
                Execute(
                    setup,
                    "CREATE INDEX IX_AttachmentResolutionEvent_AttachmentID ON Message (MessageID);");
            }

            Assert.Throws<SqliteException>(() => WorkspaceDatabase.Initialise(workspace.DatabasePath));

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(
                workspace.DatabasePath);

            // Still a working version-2 workspace that accepts an ordinary write.
            Execute(
                connection,
                $"INSERT INTO Conversation (Title, CreatedDateTimeUtc) " +
                $"VALUES ('Added after rollback', '{ImportedDateTimeUtcText}');");

            Assert.Equal(2, CountRows(connection, "Conversation"));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private static void AssertStillUnmigratedVersionTwo(string databasePath)
        {
            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(databasePath);

            Assert.Equal(2, WorkspaceDatabase.ReadSchemaVersion(connection));

            foreach (var tableName in Phase7Tables)
            {
                Assert.False(TableExists(connection, tableName), tableName);
            }
        }

        private static List<string> ReadApplicationTableNames(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT name
                FROM sqlite_master
                WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                ORDER BY name;
                """;

            using var reader = command.ExecuteReader();

            var names = new List<string>();

            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }

        /// <summary>
        /// The indexes the schema declares by name, excluding the automatic ones SQLite derives from
        /// primary-key and unique constraints.
        /// </summary>
        private static List<string> ReadExplicitIndexNames(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT name
                FROM sqlite_master
                WHERE type = 'index' AND sql IS NOT NULL
                ORDER BY name;
                """;

            using var reader = command.ExecuteReader();

            var names = new List<string>();

            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }

        /// <summary>
        /// The indexes SQLite created by itself for the Phase 7 tables' primary-key and unique
        /// constraints, as opposed to the ones the schema names.
        /// </summary>
        private static List<string> ReadAutomaticIndexNamesForPhase7Tables(SqliteConnection connection)
        {
            var names = new List<string>();

            foreach (var tableName in Phase7Tables)
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT name
                    FROM sqlite_master
                    WHERE type = 'index' AND sql IS NULL AND tbl_name = $tableName
                    ORDER BY name;
                    """;

                command.Parameters.AddWithValue("$tableName", tableName);

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    names.Add(reader.GetString(0));
                }
            }

            return names;
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
                    $"{reader.GetString(0)}{reader.GetString(1)}" +
                    $"{reader.GetString(2)}{reader.GetString(3)}");
            }

            return objects;
        }
    }
}
