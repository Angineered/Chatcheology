using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Workspace
{
    /// <summary>
    /// Refuses to let an operation touch anything but a workspace at the current schema version.
    /// </summary>
    /// <remarks>
    /// Creating and migrating a workspace belongs to <see cref="WorkspaceDatabase.Initialise"/>;
    /// storing things in one belongs to the services that store them. Keeping those responsibilities
    /// apart is what makes every such operation predictable — a caller who has not initialised the
    /// workspace is told so, rather than having the schema changed underneath them as a side effect
    /// of asking for something else.
    /// <para>
    /// One guard shared by every writing service, rather than one per service. The rule is the same
    /// rule each time, and separate copies of it would be free to drift into disagreeing about
    /// which versions are acceptable.
    /// </para>
    /// </remarks>
    internal static class WorkspaceSchemaGuard
    {
        /// <summary>
        /// Throws unless <paramref name="connection"/> is open on a workspace at
        /// <see cref="WorkspaceDatabase.SchemaVersion"/>.
        /// </summary>
        /// <param name="operationDescription">
        /// What the caller was about to do, as a noun phrase such as <c>an import</c> or
        /// <c>an inventory</c>, so the diagnostic says which operation was refused.
        /// </param>
        /// <remarks>
        /// Call this before opening a transaction, so a workspace that must not be written to is
        /// never written to at all rather than being written to and rolled back.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The database is not at the current schema version. It is left exactly as it was found.
        /// </exception>
        internal static void RequireCurrentSchemaVersion(
            SqliteConnection connection, string operationDescription)
        {
            var schemaVersion = WorkspaceDatabase.ReadSchemaVersion(connection);

            if (schemaVersion == WorkspaceDatabase.SchemaVersion)
            {
                return;
            }

            throw new InvalidOperationException(schemaVersion switch
            {
                WorkspaceDatabase.UninitialisedSchemaVersion =>
                    $"The database at the supplied path has no workspace schema (user_version " +
                    $"{WorkspaceDatabase.UninitialisedSchemaVersion}), and {operationDescription} " +
                    $"requires version {WorkspaceDatabase.SchemaVersion}. Create the workspace with " +
                    $"WorkspaceDatabase.Initialise first. Nothing has been written.",

                WorkspaceDatabase.VersionOneSchemaVersion =>
                    $"The workspace database is schema version " +
                    $"{WorkspaceDatabase.VersionOneSchemaVersion} and has not been migrated to " +
                    $"version {WorkspaceDatabase.SchemaVersion}. Run WorkspaceDatabase.Initialise " +
                    $"to migrate it first; {operationDescription} does not migrate implicitly. " +
                    $"Nothing has been written and the database is still version " +
                    $"{WorkspaceDatabase.VersionOneSchemaVersion}.",

                _ =>
                    $"The workspace database reports schema version {schemaVersion}, which this " +
                    $"build does not support; {operationDescription} requires version " +
                    $"{WorkspaceDatabase.SchemaVersion}. Nothing has been written and the database " +
                    $"is unchanged.",
            });
        }
    }
}
