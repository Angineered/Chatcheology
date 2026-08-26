using Chatcheology.Data.Workspace;

namespace Chatcheology.Data.Tests.Workspace
{
    /// <summary>
    /// Tests the exclusive claim a caller holds around a whole workspace operation.
    /// </summary>
    /// <remarks>
    /// The claim's authority is the open file handle, so the tests take it and then try to take it
    /// again rather than inspecting anything about the lock file's contents or existence.
    /// </remarks>
    public class WorkspaceExclusiveLockTests
    {
        [Fact]
        public void Acquire_UnclaimedWorkspace_Succeeds()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var held = WorkspaceExclusiveLock.Acquire(workspace.DatabasePath);

            Assert.Equal(workspace.DatabasePath + ".lock", held.LockFilePath);
            Assert.True(File.Exists(held.LockFilePath));
        }

        /// <remarks>
        /// The workspace itself need not exist. The claim is made before the protocol that reads or
        /// migrates the database, so requiring the file first would mean claiming it too late.
        /// </remarks>
        [Fact]
        public void Acquire_WorkspaceFileThatDoesNotExistYet_Succeeds()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            using var held = WorkspaceExclusiveLock.Acquire(workspace.DatabasePath);

            Assert.False(File.Exists(workspace.DatabasePath));
            Assert.True(File.Exists(held.LockFilePath));
        }

        [Fact]
        public void Acquire_WhileAlreadyHeld_IsRefused()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            using var held = WorkspaceExclusiveLock.Acquire(workspace.DatabasePath);

            var exception = Assert.Throws<InvalidOperationException>(
                () => WorkspaceExclusiveLock.Acquire(workspace.DatabasePath));

            Assert.Contains(
                workspace.DatabasePath, exception.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// Both starting states are deliberately one test. Because releasing the claim leaves the lock
        /// file in place, a file left behind by a clean release and one left behind by a process that
        /// died holding it are the same thing to the next caller — which is exactly the property that
        /// makes a stale file harmless.
        /// </remarks>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Acquire_AfterAnUnheldLockFileIsLeftBehind_Succeeds(bool abandonedRatherThanReleased)
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            var lockFilePath = workspace.DatabasePath + ".lock";

            if (abandonedRatherThanReleased)
            {
                // A lock file with no handle on it, as a killed process would leave.
                File.WriteAllBytes(lockFilePath, []);
            }
            else
            {
                using (WorkspaceExclusiveLock.Acquire(workspace.DatabasePath))
                {
                }
            }

            Assert.True(File.Exists(lockFilePath));

            using var reacquired = WorkspaceExclusiveLock.Acquire(workspace.DatabasePath);

            Assert.Equal(lockFilePath, reacquired.LockFilePath);
        }

        [Fact]
        public void Dispose_LeavesTheLockFileInPlace()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            string lockFilePath;

            using (var held = WorkspaceExclusiveLock.Acquire(workspace.DatabasePath))
            {
                lockFilePath = held.LockFilePath;
            }

            // Deleting it would buy nothing an unheld file does not already give, and would race with
            // whoever opened it next.
            Assert.True(File.Exists(lockFilePath));
        }

        /// <remarks>
        /// The claim is on a sibling file, so it must not interfere with opening the workspace itself.
        /// A design that locked the database file directly could not do this.
        /// </remarks>
        [Fact]
        public void Acquire_DoesNotPreventTheWorkspaceBeingOpened()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using var held = WorkspaceExclusiveLock.Acquire(workspace.DatabasePath);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(
                WorkspaceDatabase.SchemaVersion, WorkspaceDatabase.ReadSchemaVersion(connection));
        }

        /// <remarks>
        /// A second claim on a different workspace is a different claim. The lock is per workspace,
        /// not global.
        /// </remarks>
        [Fact]
        public void Acquire_ADifferentWorkspace_IsNotBlocked()
        {
            using var first = new TemporaryWorkspaceDatabase();
            using var second = new TemporaryWorkspaceDatabase();

            using var firstLock = WorkspaceExclusiveLock.Acquire(first.DatabasePath);
            using var secondLock = WorkspaceExclusiveLock.Acquire(second.DatabasePath);

            Assert.NotEqual(firstLock.LockFilePath, secondLock.LockFilePath);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Acquire_MissingDatabasePath_IsRejected(string databasePath)
        {
            Assert.Throws<ArgumentException>(() => WorkspaceExclusiveLock.Acquire(databasePath));
        }

        // ---------------------------------------------------------------------------------------
        // Diagnostics. A path that names nothing must not be reported as a workspace somebody else
        // is using: that would send a caller hunting for a competing operation that does not exist,
        // at the first step of a protocol whose purpose is to make a one-way change safe.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// The wording that must appear only for genuine contention.
        /// </summary>
        private const string ContentionPhrase = "Another workspace operation";

        [Fact]
        public void Acquire_WhenTheContainingDirectoryDoesNotExist_IsNotReportedAsContention()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            var missingDirectoryPath = Path.Combine(
                workspace.DirectoryPath, "directory-that-does-not-exist", "workspace.db");

            var exception = Assert.Throws<InvalidOperationException>(
                () => WorkspaceExclusiveLock.Acquire(missingDirectoryPath));

            // DirectoryNotFoundException derives from IOException, so without its own catch clause
            // this would have been reported as a held lock.
            Assert.IsType<DirectoryNotFoundException>(exception.InnerException);

            Assert.DoesNotContain(ContentionPhrase, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("already held", exception.Message, StringComparison.Ordinal);

            Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing has been claimed", exception.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// The correction must not create anything on the way to reporting the failure.
        /// </remarks>
        [Fact]
        public void Acquire_WhenTheContainingDirectoryDoesNotExist_CreatesNothing()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            var missingDirectory = Path.Combine(workspace.DirectoryPath, "directory-that-does-not-exist");

            Assert.Throws<InvalidOperationException>(
                () => WorkspaceExclusiveLock.Acquire(Path.Combine(missingDirectory, "workspace.db")));

            Assert.False(Directory.Exists(missingDirectory));
            Assert.Empty(Directory.GetFileSystemEntries(workspace.DirectoryPath));
        }

        [Fact]
        public void Acquire_GenuineContention_StillReportsContention()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            using var held = WorkspaceExclusiveLock.Acquire(workspace.DatabasePath);

            var exception = Assert.Throws<InvalidOperationException>(
                () => WorkspaceExclusiveLock.Acquire(workspace.DatabasePath));

            Assert.Contains(ContentionPhrase, exception.Message, StringComparison.Ordinal);
            Assert.Contains("already held", exception.Message, StringComparison.Ordinal);

            // A real sharing violation, not one of the not-found cases that now precede it.
            Assert.IsType<IOException>(exception.InnerException, exactMatch: false);
            Assert.IsNotType<DirectoryNotFoundException>(exception.InnerException);
            Assert.IsNotType<FileNotFoundException>(exception.InnerException);
        }

        /// <remarks>
        /// The claim itself is unchanged by the diagnostic correction: a full acquire, release and
        /// reacquire round trip still behaves exactly as before, and the lock file still survives the
        /// release.
        /// </remarks>
        [Fact]
        public void Acquire_ReleaseAndReacquire_IsUnchanged()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            string lockFilePath;

            using (var first = WorkspaceExclusiveLock.Acquire(workspace.DatabasePath))
            {
                lockFilePath = first.LockFilePath;

                // Held: a second claim is refused while the first lives.
                Assert.Throws<InvalidOperationException>(
                    () => WorkspaceExclusiveLock.Acquire(workspace.DatabasePath));
            }

            Assert.True(File.Exists(lockFilePath));

            using (var second = WorkspaceExclusiveLock.Acquire(workspace.DatabasePath))
            {
                Assert.Equal(lockFilePath, second.LockFilePath);
            }

            // And the workspace itself is still readable and untouched throughout.
            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(
                WorkspaceDatabase.SchemaVersion, WorkspaceDatabase.ReadSchemaVersion(connection));
        }
    }
}
