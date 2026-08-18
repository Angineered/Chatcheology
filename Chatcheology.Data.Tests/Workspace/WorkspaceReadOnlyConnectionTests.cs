using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Workspace.WorkspaceTestData;

namespace Chatcheology.Data.Tests.Workspace
{
    /// <summary>
    /// Tests for opening a workspace read-only.
    /// </summary>
    /// <remarks>
    /// The point of the API is that inspection cannot change what it inspects. That is a claim
    /// about SQLite's enforcement rather than about callers being careful, so the tests attempt
    /// writes and require them to fail.
    /// </remarks>
    public class WorkspaceReadOnlyConnectionTests
    {
        [Fact]
        public void OpenReadOnlyConnection_ExistingWorkspace_ReadsItNormally()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(workspace.DatabasePath);

            Assert.Equal(WorkspaceDatabase.SchemaVersion, WorkspaceDatabase.ReadSchemaVersion(connection));
            Assert.Equal(0, CountRows(connection, "MediaSource"));
            Assert.Equal(0, CountRows(connection, "Message"));
        }

        [Fact]
        public void OpenReadOnlyConnection_AttemptedWrite_IsRefusedBySqlite()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using (var connection = WorkspaceDatabase.OpenReadOnlyConnection(workspace.DatabasePath))
            {
                Assert.Throws<SqliteException>(() => Execute(
                    connection,
                    """
                    INSERT INTO MediaSource (
                        DisplayName, SourceType, RootPath, DeviceDescription, ImportedDateTimeUtc)
                    VALUES ('x', 'x', 'x', NULL, 'x');
                    """));

                // Including the header field, which a schema change would have to write.
                Assert.Throws<SqliteException>(() => Execute(connection, "PRAGMA user_version = 9;"));
            }

            using var verify = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(WorkspaceDatabase.SchemaVersion, WorkspaceDatabase.ReadSchemaVersion(verify));
            Assert.Equal(0, CountRows(verify, "MediaSource"));
        }

        /// <remarks>
        /// <c>ReadOnly</c> already prevents SQLite creating a file, but the failure it raises reads
        /// as a corrupt or locked workspace rather than as a path holding nothing. The explicit
        /// check is what makes the difference reportable.
        /// </remarks>
        [Fact]
        public void OpenReadOnlyConnection_PathThatDoesNotExist_IsRejectedWithoutCreatingAFile()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            Assert.Throws<FileNotFoundException>(
                () => WorkspaceDatabase.OpenReadOnlyConnection(workspace.DatabasePath));

            SqliteConnection.ClearAllPools();

            Assert.False(File.Exists(workspace.DatabasePath));
            Assert.Empty(Directory.GetFiles(workspace.DirectoryPath));
        }

        /// <remarks>
        /// A workspace this build cannot use is still a file that can be looked at. Reading is how
        /// a caller finds out which version it is; refusing to open it would leave no way to ask.
        /// </remarks>
        [Fact]
        public void OpenReadOnlyConnection_VersionOneWorkspace_CanStillBeInspected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionOneWorkspace.Create(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(workspace.DatabasePath);

            Assert.Equal(1, WorkspaceDatabase.ReadSchemaVersion(connection));
        }

        /// <remarks>
        /// Read-only is not the same as unobserved. Microsoft.Data.Sqlite pools by connection
        /// string, so a disposed read-only connection can keep the file open until the pool is
        /// cleared — which is what code that goes on to hash or copy the workspace file itself has
        /// to do.
        /// </remarks>
        [Fact]
        public void OpenReadOnlyConnection_AfterDisposeAndPoolClear_ReleasesTheFile()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using (var connection = WorkspaceDatabase.OpenReadOnlyConnection(workspace.DatabasePath))
            {
                Assert.Equal(WorkspaceDatabase.SchemaVersion, WorkspaceDatabase.ReadSchemaVersion(connection));
            }

            SqliteConnection.ClearAllPools();

            // Openable for exclusive reading, which a lingering handle would prevent.
            using var stream = new FileStream(
                workspace.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.None);

            Assert.True(stream.Length > 0);
        }

        /// <remarks>
        /// No write-ahead logging, so a workspace stays a single file. Inspecting one must not
        /// change that: a read-only open that produced companion files would leave a workspace
        /// that no longer looks like the one that was inspected.
        /// </remarks>
        [Fact]
        public void OpenReadOnlyConnection_LeavesNoCompanionFiles()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using (var connection = WorkspaceDatabase.OpenReadOnlyConnection(workspace.DatabasePath))
            {
                Assert.Equal(0, CountRows(connection, "Message"));
            }

            SqliteConnection.ClearAllPools();

            Assert.Equal(
                [Path.GetFileName(workspace.DatabasePath)],
                Directory.GetFiles(workspace.DirectoryPath).Select(Path.GetFileName));
        }
    }
}
