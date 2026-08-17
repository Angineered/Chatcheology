using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Workspace
{
    /// <summary>
    /// An isolated workspace database file in its own temporary directory, removed on dispose.
    /// </summary>
    /// <remarks>
    /// Each instance gets a private directory under the system temporary path, well outside the
    /// repository working tree, so a test run can never leave a <c>.db</c> file among the source.
    /// Deleting the whole directory also catches any companion file SQLite might create.
    /// <para>
    /// Nothing here reads the clock or depends on ambient state beyond the temporary path itself.
    /// </para>
    /// </remarks>
    internal sealed class TemporaryWorkspaceDatabase : IDisposable
    {
        private readonly string _directoryPath;

        internal TemporaryWorkspaceDatabase()
        {
            _directoryPath = Path.Combine(
                Path.GetTempPath(),
                "Chatcheology.Data.Tests",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(_directoryPath);

            DatabasePath = Path.Combine(_directoryPath, "workspace.db");
        }

        /// <summary>
        /// The workspace database path to hand to the data layer. The file does not exist yet;
        /// SQLite creates it on first open.
        /// </summary>
        internal string DatabasePath { get; }

        /// <summary>The private temporary directory holding the database.</summary>
        internal string DirectoryPath => _directoryPath;

        /// <remarks>
        /// Deliberately allowed to throw. A failure here means a SQLite handle outlived the test,
        /// which is exactly the leak this type exists to catch, so it should fail the test rather
        /// than be swallowed.
        /// </remarks>
        public void Dispose()
        {
            // Microsoft.Data.Sqlite pools connections by connection string, so an idle pooled
            // handle keeps the file open on Windows even after every SqliteConnection has been
            // disposed. Without this, deletion fails with a sharing violation.
            SqliteConnection.ClearAllPools();

            Directory.Delete(_directoryPath, recursive: true);
        }
    }
}
