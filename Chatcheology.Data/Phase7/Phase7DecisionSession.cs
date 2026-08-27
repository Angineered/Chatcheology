using System.Globalization;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// One Stage 7.2 decision write: the cooperative claim on the workspace, the read-write connection,
    /// and the single non-deferred transaction every validation read and every insert runs inside.
    /// </summary>
    /// <remarks>
    /// <para><strong>One scaffold, three services.</strong> Review, candidate disposition and the
    /// resolution transitions all need the same protocol in the same order, and three copies of it
    /// would be three chances for one of them to drift.</para>
    /// <para><strong>The order is load-bearing.</strong> The missing-file check comes before any
    /// connection is built, because the workspace connection string opens <c>ReadWriteCreate</c> and
    /// would otherwise create the file — the same reasoning
    /// <c>Phase7EvidenceSnapshotService.OpenSession</c> and <c>WorkspaceImporter.Import</c> both use.
    /// The schema guard runs on a read-only connection which is then disposed, so a workspace this
    /// operation must refuse never has a read-write handle on it at all; and that disposal also matters
    /// for the operation that proceeds, because a lingering read connection holds SQLite's
    /// <c>SHARED</c> lock, which refuses the <c>EXCLUSIVE</c> upgrade the commit needs.</para>
    /// <para><strong>Why the transaction is non-deferred.</strong>
    /// <c>BeginTransaction(deferred: false)</c> takes SQLite's <c>RESERVED</c> writer reservation at
    /// <c>BEGIN</c>, <em>before</em> the first validation read. Every read a decision depends on and the
    /// write that acts on it therefore share one atomic view, which is what closes the
    /// time-of-check/time-of-use race where two writers both validate the same old state and both
    /// commit. A second conforming writer cannot even begin: it receives <c>SQLITE_BUSY</c>.</para>
    /// <para><strong>What is not claimed.</strong> <see cref="WorkspaceExclusiveLock"/> is a
    /// sibling-file claim honoured by cooperating Chatcheology callers. It excludes a second
    /// cooperating run — a Stage 7.1 evidence run included — with a clear diagnostic before any SQLite
    /// lock is attempted, but it cannot prevent an unrelated third-party SQLite client, a backup tool
    /// or a file-sync agent from writing this file. The reservation excludes any <em>conforming</em>
    /// SQLite writer; a process that ignores SQLite's locking protocol and writes raw bytes is outside
    /// the model altogether.</para>
    /// <para><strong>It cannot migrate a workspace.</strong> <c>WorkspaceDatabase.Initialise</c> is
    /// never called from here or from anything Stage 7.2 owns. A workspace at any version but the
    /// current one is refused by the guard, with nothing written.</para>
    /// <para>
    /// No retry loop. The provider's default busy timeout from
    /// <c>WorkspaceDatabase.BuildConnectionString</c> is the only waiting this repository sanctions,
    /// and it is left exactly as it is.
    /// </para>
    /// </remarks>
    internal sealed class Phase7DecisionSession : IDisposable
    {
        private readonly WorkspaceExclusiveLock _workspaceLock;
        private readonly SqliteConnection _connection;

        private SqliteTransaction? _transaction;

        private Phase7DecisionSession(
            WorkspaceExclusiveLock workspaceLock, SqliteConnection connection)
        {
            _workspaceLock = workspaceLock;
            _connection = connection;
        }

        /// <summary>The write connection. Every command must carry <see cref="Transaction"/>.</summary>
        internal SqliteConnection Connection => _connection;

        /// <summary>The open write transaction.</summary>
        internal SqliteTransaction Transaction =>
            _transaction
                ?? throw new InvalidOperationException(
                    "The decision transaction is not open, so there is no atomic view to validate or " +
                    "write under. Nothing has been written.");

        /// <summary>
        /// Claims the workspace, proves it is one this operation may write, and opens the write
        /// connection — without writing anything.
        /// </summary>
        /// <param name="operationDescription">
        /// What the caller was about to do, as a noun phrase such as <c>a Phase 7 review</c>, so a
        /// refusal says which operation was refused.
        /// </param>
        /// <exception cref="ArgumentException">No database path was supplied. Nothing was opened.</exception>
        /// <exception cref="FileNotFoundException">
        /// There is no workspace at the supplied path. <strong>No file is created.</strong>
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The workspace is already claimed by another cooperating operation, or is not at the current
        /// schema version. Nothing has been written and the workspace is unchanged.
        /// </exception>
        internal static Phase7DecisionSession Open(string databasePath, string operationDescription)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

            if (!File.Exists(databasePath))
            {
                throw new FileNotFoundException(
                    "There is no workspace database at the supplied path. A Phase 7 decision is " +
                    "recorded against a workspace that already exists; it never creates one, and no " +
                    "file has been created here.",
                    databasePath);
            }

            var workspaceLock = WorkspaceExclusiveLock.Acquire(databasePath);

            try
            {
                using (var readOnly = WorkspaceDatabase.OpenReadOnlyConnection(databasePath))
                {
                    WorkspaceSchemaGuard.RequireCurrentSchemaVersion(readOnly, operationDescription);
                }

                var connection = WorkspaceDatabase.OpenConnection(databasePath);

                return new Phase7DecisionSession(workspaceLock, connection);
            }
            catch
            {
                workspaceLock.Dispose();

                throw;
            }
        }

        /// <summary>
        /// Takes SQLite's writer reservation, before the first validation read.
        /// </summary>
        /// <exception cref="SqliteException">
        /// Another conforming writer already owns the reservation (<c>SQLITE_BUSY</c>). Nothing has
        /// been written.
        /// </exception>
        internal void Begin()
        {
            if (_transaction is not null)
            {
                throw new InvalidOperationException(
                    "The decision transaction is already open. One decision holds one transaction, " +
                    "which is also the transaction that commits it.");
            }

            _transaction = _connection.BeginTransaction(deferred: false);
        }

        /// <summary>Commits the decision.</summary>
        internal void Commit()
        {
            var transaction = Transaction;

            transaction.Commit();

            _transaction = null;

            transaction.Dispose();
        }

        /// <summary>A command on this session's connection, carrying its transaction.</summary>
        internal SqliteCommand CreateCommand(string statement)
        {
            var command = _connection.CreateCommand();
            command.Transaction = Transaction;
            command.CommandText = statement;

            return command;
        }

        /// <summary>Reads one integer, or null when the statement returns null or no row.</summary>
        internal long? ScalarLongOrNull(
            string statement, params (string Name, object? Value)[] parameters)
        {
            using var command = CreateCommand(statement);

            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            var scalar = command.ExecuteScalar();

            return scalar is null || scalar is DBNull
                ? null
                : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }

        /// <summary>Reads one integer that the statement is known to produce.</summary>
        internal long ScalarLong(string statement, params (string Name, object? Value)[] parameters) =>
            ScalarLongOrNull(statement, parameters)
                ?? throw new InvalidOperationException(
                    "A Stage 7.2 validation read returned no value where one was required. Nothing " +
                    "has been committed.");

        /// <summary>Reads one text value, or null when the statement returns null or no row.</summary>
        internal string? ScalarTextOrNull(
            string statement, params (string Name, object? Value)[] parameters)
        {
            using var command = CreateCommand(statement);

            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            var scalar = command.ExecuteScalar();

            return scalar is null || scalar is DBNull ? null : (string)scalar;
        }

        /// <summary>
        /// Releases everything this session holds, rolling the transaction back if it is still open.
        /// </summary>
        /// <remarks>
        /// An uncommitted transaction is rolled back here rather than left to the connection's own
        /// disposal, so "a failed decision commits nothing" holds by construction whichever way the
        /// operation unwinds — including the paths where the insert and the projection update have
        /// already executed and a later check refused.
        /// </remarks>
        public void Dispose()
        {
            if (_transaction is { } transaction)
            {
                _transaction = null;

                try
                {
                    transaction.Rollback();
                }
                catch (InvalidOperationException)
                {
                    // The provider already completed or already rolled this transaction back. There
                    // is nothing to undo, and the primary failure is what the caller must see.
                }

                transaction.Dispose();
            }

            _connection.Dispose();
            _workspaceLock.Dispose();
        }
    }
}
