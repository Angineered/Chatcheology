using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Workspace.WorkspaceTestData;

namespace Chatcheology.Data.Tests.Workspace
{
    /// <summary>
    /// Tests the checkpoint and reconciliation primitives a migration needs to be reversible and to be
    /// proved not to have disturbed what was already there.
    /// </summary>
    /// <remarks>
    /// No real path appears anywhere here. Every checkpoint is written inside a temporary directory
    /// that is removed when the test finishes.
    /// </remarks>
    public class WorkspaceCheckpointTests
    {
        // ---------------------------------------------------------------------------------------
        // File identity.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ComputeFileSha256_MatchesAnIndependentlyComputedHash()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            var computed = WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath);

            SqliteConnection.ClearAllPools();

            // Fully qualified: the shared test data has its own SHA256 constant.
            var expected = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(workspace.DatabasePath)));

            Assert.Equal(expected, computed);
            Assert.Equal(64, computed.Length);
        }

        [Fact]
        public void ComputeFileSha256_PathThatDoesNotExist_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            Assert.Throws<FileNotFoundException>(
                () => WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath));
        }

        // ---------------------------------------------------------------------------------------
        // Checkpoint creation.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void CopyAsPermanentCheckpoint_ProducesAByteIdenticalCopy()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            var checkpointPath = Path.Combine(workspace.DirectoryPath, "before.db");

            var sourceHash = WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath);

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(workspace.DatabasePath, checkpointPath);

            Assert.Equal(sourceHash, WorkspaceCheckpoint.ComputeFileSha256(checkpointPath));

            Assert.Equal(
                new FileInfo(workspace.DatabasePath).Length,
                new FileInfo(checkpointPath).Length);
        }

        /// <remarks>
        /// A checkpoint records what a workspace was before it was changed. A second copy written over
        /// the first would destroy exactly the state it exists to preserve, so an existing destination
        /// is a failure rather than something to overwrite.
        /// </remarks>
        [Fact]
        public void CopyAsPermanentCheckpoint_ExistingDestination_IsRefusedAndLeftAlone()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            var checkpointPath = Path.Combine(workspace.DirectoryPath, "before.db");

            File.WriteAllText(checkpointPath, "an earlier checkpoint");

            Assert.Throws<IOException>(
                () => WorkspaceCheckpoint.CopyAsPermanentCheckpoint(
                    workspace.DatabasePath, checkpointPath));

            Assert.Equal("an earlier checkpoint", File.ReadAllText(checkpointPath));
        }

        [Fact]
        public void CopyAsPermanentCheckpoint_SourceThatDoesNotExist_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            Assert.Throws<FileNotFoundException>(
                () => WorkspaceCheckpoint.CopyAsPermanentCheckpoint(
                    workspace.DatabasePath,
                    Path.Combine(workspace.DirectoryPath, "before.db")));
        }

        // ---------------------------------------------------------------------------------------
        // Companion files.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void RequireNoCompanionFiles_OrdinaryWorkspace_Passes()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            SqliteConnection.ClearAllPools();

            WorkspaceCheckpoint.RequireNoCompanionFiles(workspace.DatabasePath);
        }

        [Theory]
        [InlineData("-wal")]
        [InlineData("-shm")]
        [InlineData("-journal")]
        public void RequireNoCompanionFiles_WhenOneExists_IsRejected(string suffix)
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            SqliteConnection.ClearAllPools();

            File.WriteAllBytes(workspace.DatabasePath + suffix, []);

            var exception = Assert.Throws<InvalidOperationException>(
                () => WorkspaceCheckpoint.RequireNoCompanionFiles(workspace.DatabasePath));

            Assert.Contains(suffix, exception.Message, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------------
        // Reconciliation: the positive case.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void RequireIdenticalTables_UntouchedWorkspace_Passes()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            var checkpointPath = Path.Combine(workspace.DirectoryPath, "before.db");

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(workspace.DatabasePath, checkpointPath);

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(
                workspace.DatabasePath);

            WorkspaceCheckpoint.RequireIdenticalTables(
                connection, checkpointPath, SyntheticVersionTwoWorkspace.Tables);
        }

        /// <remarks>
        /// The fixture leaves nullable columns null on some rows and filled on others, so a comparison
        /// that treated two stored nulls as unequal — which ordinary SQL equality would — could not
        /// pass this.
        /// </remarks>
        [Fact]
        public void RequireIdenticalTables_TreatsTwoStoredNullsAsEqual()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            var checkpointPath = Path.Combine(workspace.DirectoryPath, "before.db");

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(workspace.DatabasePath, checkpointPath);

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(
                workspace.DatabasePath);

            // Proves the fixture really does exercise the null path.
            Assert.True(
                ScalarLong(
                    connection,
                    "SELECT COUNT(*) FROM MediaFile WHERE SHA256 IS NULL OR DurationMS IS NULL;") > 0);

            WorkspaceCheckpoint.RequireIdenticalTables(
                connection, checkpointPath, SyntheticVersionTwoWorkspace.Tables);
        }

        // ---------------------------------------------------------------------------------------
        // Reconciliation: the negative cases that make it worth running.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The case row counts cannot catch. One value changes and the count does not move, which is
        /// exactly what a migration that rewrote existing data would look like from a distance.
        /// </remarks>
        [Fact]
        public void RequireIdenticalTables_ChangedValueWithUnchangedRowCount_IsDetected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            var checkpointPath = Path.Combine(workspace.DirectoryPath, "before.db");

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(workspace.DatabasePath, checkpointPath);

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(
                workspace.DatabasePath);

            Execute(connection, "UPDATE Conversation SET Title = 'Quietly rewritten';");

            var countBefore = CountRows(connection, "Conversation");

            var exception = Assert.Throws<InvalidOperationException>(
                () => WorkspaceCheckpoint.RequireIdenticalTables(
                    connection, checkpointPath, SyntheticVersionTwoWorkspace.Tables));

            Assert.Contains("Conversation", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Title", exception.Message, StringComparison.Ordinal);

            // The row count really was unchanged, so a count-only comparison would have passed.
            Assert.Equal(countBefore, ReadCheckpointRowCount(checkpointPath, "Conversation"));
        }

        /// <remarks>
        /// A null replaced by a value, and the reverse. Both are changes a comparison that skipped
        /// nulls would miss entirely.
        /// </remarks>
        [Fact]
        public void RequireIdenticalTables_NullReplacedByAValue_IsDetected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            var checkpointPath = Path.Combine(workspace.DirectoryPath, "before.db");

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(workspace.DatabasePath, checkpointPath);

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(
                workspace.DatabasePath);

            Execute(connection, "UPDATE MediaFile SET DurationMS = 1 WHERE DurationMS IS NULL;");

            var exception = Assert.Throws<InvalidOperationException>(
                () => WorkspaceCheckpoint.RequireIdenticalTables(
                    connection, checkpointPath, SyntheticVersionTwoWorkspace.Tables));

            Assert.Contains("MediaFile", exception.Message, StringComparison.Ordinal);
            Assert.Contains("DurationMS", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RequireIdenticalTables_AddedRow_IsDetected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            var checkpointPath = Path.Combine(workspace.DirectoryPath, "before.db");

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(workspace.DatabasePath, checkpointPath);

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(
                workspace.DatabasePath);

            Execute(
                connection,
                $"INSERT INTO Conversation (Title, CreatedDateTimeUtc) " +
                $"VALUES ('Added', '{ImportedDateTimeUtcText}');");

            var exception = Assert.Throws<InvalidOperationException>(
                () => WorkspaceCheckpoint.RequireIdenticalTables(
                    connection, checkpointPath, SyntheticVersionTwoWorkspace.Tables));

            Assert.Contains("Conversation", exception.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// Structure, not content. The table gains a column, every original value is still there, and
        /// the row count has not moved — so only a comparison that checks the table's definition can
        /// see it. This is the case row-and-value comparison alone would pass.
        /// </remarks>
        [Fact]
        public void RequireIdenticalTables_ChangedTableDefinition_IsDetected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            var checkpointPath = Path.Combine(workspace.DirectoryPath, "before.db");

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(workspace.DatabasePath, checkpointPath);

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(
                workspace.DatabasePath);

            Execute(connection, "ALTER TABLE Conversation ADD COLUMN Extra TEXT NULL;");

            var exception = Assert.Throws<InvalidOperationException>(
                () => WorkspaceCheckpoint.RequireIdenticalTables(
                    connection, checkpointPath, SyntheticVersionTwoWorkspace.Tables));

            Assert.Contains("Conversation", exception.Message, StringComparison.Ordinal);
            Assert.Contains("schema definition", exception.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// An index added to a pre-existing table changes nothing about its rows or its own definition
        /// text, so only comparing every schema object belonging to that table catches it. The same
        /// listing is what would catch a lost unique constraint, whose automatic index would vanish.
        /// </remarks>
        [Fact]
        public void RequireIdenticalTables_IndexAddedToAnExistingTable_IsDetected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            var checkpointPath = Path.Combine(workspace.DirectoryPath, "before.db");

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(workspace.DatabasePath, checkpointPath);

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(
                workspace.DatabasePath);

            Execute(connection, "CREATE INDEX IX_Conversation_Title ON Conversation (Title);");

            var exception = Assert.Throws<InvalidOperationException>(
                () => WorkspaceCheckpoint.RequireIdenticalTables(
                    connection, checkpointPath, SyntheticVersionTwoWorkspace.Tables));

            Assert.Contains("Conversation", exception.Message, StringComparison.Ordinal);
            Assert.Contains("schema definition", exception.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// The checkpoint is opened through the read-only primitive, so a comparison cannot alter the
        /// evidence it compares against. Proved by the checkpoint's bytes being identical afterwards.
        /// </remarks>
        [Fact]
        public void RequireIdenticalTables_LeavesTheCheckpointByteIdentical()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            var checkpointPath = Path.Combine(workspace.DirectoryPath, "before.db");

            WorkspaceCheckpoint.CopyAsPermanentCheckpoint(workspace.DatabasePath, checkpointPath);

            var before = WorkspaceCheckpoint.ComputeFileSha256(checkpointPath);

            using (var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(
                workspace.DatabasePath))
            {
                WorkspaceCheckpoint.RequireIdenticalTables(
                    connection, checkpointPath, SyntheticVersionTwoWorkspace.Tables);
            }

            Assert.Equal(before, WorkspaceCheckpoint.ComputeFileSha256(checkpointPath));
        }

        [Fact]
        public void RequireIdenticalTables_CheckpointThatDoesNotExist_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionTwoWorkspace.Create(workspace.DatabasePath);

            using var connection = SyntheticVersionTwoWorkspace.OpenPlainConnection(
                workspace.DatabasePath);

            Assert.Throws<FileNotFoundException>(
                () => WorkspaceCheckpoint.RequireIdenticalTables(
                    connection,
                    Path.Combine(workspace.DirectoryPath, "missing.db"),
                    SyntheticVersionTwoWorkspace.Tables));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private static long ReadCheckpointRowCount(string checkpointPath, string tableName)
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(checkpointPath);

            return CountRows(connection, tableName);
        }
    }
}
