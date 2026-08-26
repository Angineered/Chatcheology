namespace Chatcheology.Data.Workspace
{
    /// <summary>
    /// An exclusive claim on one workspace, held for as long as a caller needs a whole operation
    /// protected from a second writer.
    /// </summary>
    /// <remarks>
    /// Phase 7 is the first phase that intentionally writes to a workspace that already holds
    /// evidence, so an operation that spans several steps — verify, hash, checkpoint, migrate,
    /// reconcile — needs those steps to happen against a workspace nothing else is changing
    /// underneath them.
    /// <para>
    /// What is locked is a sibling file, <c>&lt;workspace&gt;.lock</c>, rather than the workspace
    /// database itself. Microsoft.Data.Sqlite opens the database file with its own sharing rules, and
    /// a caller that held the database exclusively through a plain file handle could not then open a
    /// normal connection to it. A separate file has no such conflict: it is claimed and released
    /// through ordinary file sharing, entirely independently of SQLite.
    /// </para>
    /// <para>
    /// The authority is the open handle, never the file's existence. The file is created if it is
    /// missing, is left in place on release, and an unlocked one left behind by an earlier run — or
    /// by a process that died holding it, since the operating system releases the handle either way —
    /// blocks nothing. There is no lease, no timeout and no heartbeat, because this is one local
    /// offline workspace used by one person at a time rather than something several machines contend
    /// for.
    /// </para>
    /// <para>
    /// This type never opens a database connection, and no code here manages one. A caller that goes
    /// on to read, hash or copy the workspace file still has to clear the connection pool itself, for
    /// the reasons <see cref="WorkspaceDatabase.OpenReadOnlyConnection"/> documents.
    /// </para>
    /// </remarks>
    public sealed class WorkspaceExclusiveLock : IDisposable
    {
        /// <summary>The suffix appended to a workspace path to name its lock file.</summary>
        internal const string LockFileSuffix = ".lock";

        private readonly FileStream _handle;

        private WorkspaceExclusiveLock(string lockFilePath, FileStream handle)
        {
            LockFilePath = lockFilePath;
            _handle = handle;
        }

        /// <summary>The lock file this instance holds open.</summary>
        public string LockFilePath { get; }

        /// <summary>
        /// Claims the workspace at <paramref name="databasePath"/> exclusively.
        /// </summary>
        /// <param name="databasePath">
        /// The workspace database path. The database itself is neither opened nor created, and it does
        /// not have to exist yet; only its sibling lock file is touched.
        /// </param>
        /// <returns>
        /// The held lock. The caller owns it and must dispose it, which is what releases the claim.
        /// </returns>
        /// <remarks>
        /// One attempt, and no retry loop. A second holder is a real conflict rather than a transient
        /// condition to wait out, and a caller told immediately can decide what to do about it; a
        /// silent poll would only delay the same answer.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The workspace is already claimed, the directory that would hold its lock file does not
        /// exist, or the lock file cannot be created or opened. Nothing has been written to the
        /// workspace and no directory has been created.
        /// </exception>
        public static WorkspaceExclusiveLock Acquire(string databasePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

            var lockFilePath = databasePath + LockFileSuffix;

            try
            {
                var handle = new FileStream(
                    lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);

                return new WorkspaceExclusiveLock(lockFilePath, handle);
            }

            // Both of the following derive from IOException and so must precede it. A path that names
            // nothing is not a workspace somebody else is using, and reporting it as contention would
            // send a caller looking for a competing operation that does not exist — the worst possible
            // first diagnostic for a protocol whose whole purpose is to make a one-way change safe.
            catch (DirectoryNotFoundException exception)
            {
                throw new InvalidOperationException(
                    $"The workspace at '{databasePath}' could not be claimed exclusively, because " +
                    $"the directory that would hold its lock file '{lockFilePath}' does not exist. " +
                    $"This is a path that names nothing, not a workspace another operation is using. " +
                    $"Nothing has been claimed, nothing has been written, and no directory has been " +
                    $"created.",
                    exception);
            }
            catch (FileNotFoundException exception)
            {
                // Not reachable through OpenOrCreate, which creates a missing lock file rather than
                // failing. Handled separately anyway, so that if some path or platform ever does
                // produce it, it cannot be reported as contention either.
                throw new InvalidOperationException(
                    $"The workspace at '{databasePath}' could not be claimed exclusively, because " +
                    $"its lock file '{lockFilePath}' could not be opened or created. Nothing has " +
                    $"been claimed and nothing has been written.",
                    exception);
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException(
                    $"The workspace at '{databasePath}' could not be claimed exclusively, because " +
                    $"its lock file '{lockFilePath}' is already held. Another workspace operation " +
                    $"is very likely running against it. Nothing has been written and the workspace " +
                    $"is unchanged.",
                    exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new InvalidOperationException(
                    $"The workspace at '{databasePath}' could not be claimed exclusively, because " +
                    $"its lock file '{lockFilePath}' cannot be opened for writing. Nothing has been " +
                    $"written and the workspace is unchanged.",
                    exception);
            }
        }

        /// <summary>
        /// Releases the claim.
        /// </summary>
        /// <remarks>
        /// The lock file is deliberately left in place. Deleting it would buy nothing — an unlocked
        /// file is already defined as harmless, and the next claim reopens it — while introducing a
        /// race with any caller that opened it in the moment between the handle closing and the
        /// delete being attempted.
        /// </remarks>
        public void Dispose() => _handle.Dispose();
    }
}
