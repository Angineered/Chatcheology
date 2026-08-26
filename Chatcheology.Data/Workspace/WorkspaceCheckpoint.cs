using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Workspace
{
    /// <summary>
    /// The primitives a workspace migration needs to be reversible and to be proved not to have
    /// disturbed what was already there: file identity, a permanent checkpoint copy, and a comparison
    /// of pre-existing tables against that checkpoint.
    /// </summary>
    /// <remarks>
    /// Every path is a parameter. No workspace, backup or archive location appears anywhere in this
    /// file, because which database is the real one is not a fact the data layer should carry — the
    /// caller that knows says so.
    /// <para>
    /// Nothing here manages connections on a caller's behalf. Microsoft.Data.Sqlite pools by
    /// connection string, and clearing that pool cannot release a connection that is still open and in
    /// use, so <see cref="ComputeFileSha256"/> and <see cref="CopyAsPermanentCheckpoint"/> require the
    /// caller to have disposed the connections it was using before reading the file. Doing it for them
    /// invisibly would mean this type deciding when someone else's connection should close.
    /// </para>
    /// </remarks>
    public static class WorkspaceCheckpoint
    {
        /// <summary>
        /// The SQLite companion files a workspace must never have, given write-ahead logging is not
        /// enabled.
        /// </summary>
        private static readonly string[] CompanionFileSuffixes = ["-wal", "-shm", "-journal"];

        /// <summary>
        /// Computes the SHA-256 of a file's exact bytes, upper-case hexadecimal.
        /// </summary>
        /// <remarks>
        /// The caller must have disposed any connection to this file first; the pool is cleared here
        /// so a pooled idle handle cannot keep it open, but an in-use connection is the caller's to
        /// close. With write-ahead logging disabled and no transaction open, the file on disk is
        /// complete between commits, which is what makes hashing it meaningful at all.
        /// </remarks>
        public static string ComputeFileSha256(string filePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException(
                    "There is no file to hash at the supplied path. Nothing has been created.",
                    filePath);
            }

            SqliteConnection.ClearAllPools();

            using var stream = File.OpenRead(filePath);

            return Convert.ToHexString(SHA256.HashData(stream));
        }

        /// <summary>
        /// Copies a workspace to a checkpoint that is created once and never replaced.
        /// </summary>
        /// <remarks>
        /// An existing destination is a failure rather than something to overwrite. A checkpoint is
        /// the record of what a workspace was before it was changed, and a second copy written over
        /// the first would destroy exactly the state it exists to preserve.
        /// <para>
        /// No directory is created. A destination whose folder does not exist is a caller mistake
        /// worth reporting rather than a structure to invent.
        /// </para>
        /// </remarks>
        /// <exception cref="IOException">
        /// A file already exists at <paramref name="destinationPath"/>. It is left exactly as it is.
        /// </exception>
        public static void CopyAsPermanentCheckpoint(string sourcePath, string destinationPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    "There is no workspace to checkpoint at the supplied path. Nothing has been " +
                    "created.",
                    sourcePath);
            }

            SqliteConnection.ClearAllPools();

            File.Copy(sourcePath, destinationPath, overwrite: false);
        }

        /// <summary>
        /// Throws unless the workspace is a single file with none of SQLite's companion files beside
        /// it.
        /// </summary>
        /// <remarks>
        /// Write-ahead logging is deliberately not enabled, so a companion file means an unfinished
        /// transaction or a connection that is still open. Either way the file on disk is not the
        /// whole of the database's state, and hashing, copying or migrating it would be working from
        /// an incomplete picture.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// A companion file exists. Nothing has been read, copied or written.
        /// </exception>
        public static void RequireNoCompanionFiles(string databasePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

            var found = CompanionFileSuffixes
                .Select(suffix => databasePath + suffix)
                .Where(File.Exists)
                .ToList();

            if (found.Count == 0)
            {
                return;
            }

            throw new InvalidOperationException(
                $"The workspace at '{databasePath}' has SQLite companion files beside it: " +
                $"{string.Join(", ", found.Select(Path.GetFileName))}. This project does not enable " +
                $"write-ahead logging, so a companion file means an unfinished transaction or an " +
                $"open connection and the file on disk is not the whole database. Nothing has been " +
                $"read, copied or written.");
        }

        /// <summary>
        /// Proves that every named table in <paramref name="live"/> is identical to the same table in
        /// the checkpoint — in definition, in row count, and in every stored value.
        /// </summary>
        /// <param name="live">
        /// An open connection to the workspace being checked. It is only read from.
        /// </param>
        /// <param name="checkpointPath">
        /// The checkpoint file. It is opened read-only here, through
        /// <see cref="WorkspaceDatabase.OpenReadOnlyConnection"/>, so the comparison cannot alter the
        /// evidence it is comparing against. A path rather than a connection precisely so that this
        /// method decides how the checkpoint is opened instead of trusting that a supplied connection
        /// was opened safely.
        /// </param>
        /// <param name="tableNames">
        /// The tables that must be unchanged — for a Phase 7 migration,
        /// <see cref="WorkspaceDatabase.VersionTwoTableNames"/>.
        /// </param>
        /// <remarks>
        /// The three proofs run in that order, and the order matters. Definitions are compared first
        /// because the row comparison reads whole rows positionally and is only meaningful once both
        /// sides are known to expose the same columns in the same order; a value comparison run
        /// against two differently-shaped tables would be answering a different question. Row counts
        /// come next as a cheap way to fail early on a table that has plainly gained or lost rows.
        /// <para>
        /// Rows are compared in <c>rowid</c> order, which is exact here rather than approximate. Every
        /// table this is used for declares an <c>INTEGER PRIMARY KEY</c>, which in SQLite *is* the
        /// rowid; none is <c>WITHOUT ROWID</c>, so a rowid always exists and is stable; and because
        /// that key is itself one of the columns <c>SELECT *</c> returns, and is unique, comparing
        /// rowid-ordered rows position by position proves the same thing as a set difference computed
        /// in both directions. A missing, added or altered row shows up as a mismatch at the position
        /// where it differs.
        /// </para>
        /// <para>
        /// Nulls compare equal to each other, which ordinary SQL equality would not give but a
        /// comparison of stored content has to have: two tables that both record "no value" in a
        /// column are the same there.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// Some table differs. The message names the table and what differed. Nothing is written by
        /// this method under any circumstances.
        /// </exception>
        public static void RequireIdenticalTables(
            SqliteConnection live, string checkpointPath, IReadOnlyList<string> tableNames)
        {
            ArgumentNullException.ThrowIfNull(live);
            ArgumentException.ThrowIfNullOrWhiteSpace(checkpointPath);
            ArgumentNullException.ThrowIfNull(tableNames);

            using var checkpoint = WorkspaceDatabase.OpenReadOnlyConnection(checkpointPath);

            foreach (var tableName in tableNames)
            {
                RequireIdenticalSchemaObjects(live, checkpoint, tableName);
                RequireIdenticalRowCount(live, checkpoint, tableName);
                RequireIdenticalRows(live, checkpoint, tableName);
            }
        }

        /// <summary>
        /// Compares every schema object belonging to one table — the table itself and every index on
        /// it, including the automatic ones SQLite creates for primary keys and unique constraints.
        /// </summary>
        /// <remarks>
        /// Including the automatic indexes is what makes a lost or added constraint visible: their
        /// names are derived from the table's, so a changed unique constraint changes this listing
        /// even when the table's own definition text somehow did not.
        /// </remarks>
        private static void RequireIdenticalSchemaObjects(
            SqliteConnection live, SqliteConnection checkpoint, string tableName)
        {
            var liveObjects = ReadSchemaObjects(live, tableName);
            var checkpointObjects = ReadSchemaObjects(checkpoint, tableName);

            if (liveObjects.SequenceEqual(checkpointObjects, StringComparer.Ordinal))
            {
                return;
            }

            throw new InvalidOperationException(
                $"Table '{tableName}' does not have the same schema definition as the checkpoint. " +
                $"The migration was supposed to add new tables and change nothing that already " +
                $"existed. Checkpoint: [{string.Join(" | ", checkpointObjects)}]. Live: " +
                $"[{string.Join(" | ", liveObjects)}].");
        }

        private static List<string> ReadSchemaObjects(SqliteConnection connection, string tableName)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT type, name, tbl_name, COALESCE(sql, '<automatic>')
                FROM sqlite_master
                WHERE tbl_name = $tableName
                ORDER BY type, name;
                """;

            command.Parameters.AddWithValue("$tableName", tableName);

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

        private static void RequireIdenticalRowCount(
            SqliteConnection live, SqliteConnection checkpoint, string tableName)
        {
            var liveCount = ReadRowCount(live, tableName);
            var checkpointCount = ReadRowCount(checkpoint, tableName);

            if (liveCount == checkpointCount)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Table '{tableName}' holds {liveCount} rows but the checkpoint holds " +
                $"{checkpointCount}. The migration was supposed to add new tables and change nothing " +
                $"that already existed.");
        }

        private static long ReadRowCount(SqliteConnection connection, string tableName)
        {
            using var command = connection.CreateCommand();

            // The name is not caller input: it comes from the schema-object comparison that has
            // already matched it against sqlite_master, and a table name cannot be parameterised.
            command.CommandText = $"SELECT COUNT(*) FROM {tableName};";

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Compares every row of one table, in rowid order, value by value.
        /// </summary>
        private static void RequireIdenticalRows(
            SqliteConnection live, SqliteConnection checkpoint, string tableName)
        {
            using var liveCommand = CreateOrderedRowCommand(live, tableName);
            using var checkpointCommand = CreateOrderedRowCommand(checkpoint, tableName);

            using var liveReader = liveCommand.ExecuteReader();
            using var checkpointReader = checkpointCommand.ExecuteReader();

            var rowNumber = 0;

            while (true)
            {
                var liveHasRow = liveReader.Read();
                var checkpointHasRow = checkpointReader.Read();

                if (!liveHasRow && !checkpointHasRow)
                {
                    return;
                }

                if (liveHasRow != checkpointHasRow)
                {
                    // The row counts already matched, so reaching here means one side ran out early.
                    throw new InvalidOperationException(
                        $"Table '{tableName}' stopped matching the checkpoint at row " +
                        $"{rowNumber + 1}: one side has a row the other does not.");
                }

                rowNumber++;

                RequireIdenticalRow(liveReader, checkpointReader, tableName, rowNumber);
            }
        }

        private static SqliteCommand CreateOrderedRowCommand(
            SqliteConnection connection, string tableName)
        {
            var command = connection.CreateCommand();

            // Ordered by rowid, which every table compared here has as its INTEGER PRIMARY KEY, so
            // the order is total and identical on both sides.
            command.CommandText = $"SELECT * FROM {tableName} ORDER BY rowid;";

            return command;
        }

        private static void RequireIdenticalRow(
            SqliteDataReader live, SqliteDataReader checkpoint, string tableName, int rowNumber)
        {
            if (live.FieldCount != checkpoint.FieldCount)
            {
                throw new InvalidOperationException(
                    $"Table '{tableName}' has {live.FieldCount} columns but the checkpoint has " +
                    $"{checkpoint.FieldCount}.");
            }

            for (var field = 0; field < live.FieldCount; field++)
            {
                var liveName = live.GetName(field);
                var checkpointName = checkpoint.GetName(field);

                if (!string.Equals(liveName, checkpointName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Table '{tableName}' has column {field + 1} named '{liveName}' but the " +
                        $"checkpoint names it '{checkpointName}'.");
                }

                var liveIsNull = live.IsDBNull(field);
                var checkpointIsNull = checkpoint.IsDBNull(field);

                // Two stored nulls are the same stored value, which ordinary SQL equality would call
                // unknown rather than equal.
                if (liveIsNull && checkpointIsNull)
                {
                    continue;
                }

                if (liveIsNull != checkpointIsNull
                    || !Equals(live.GetValue(field), checkpoint.GetValue(field)))
                {
                    throw new InvalidOperationException(
                        $"Table '{tableName}' differs from the checkpoint at row {rowNumber}, " +
                        $"column '{liveName}': the checkpoint holds " +
                        $"{Describe(checkpoint, field)} and the workspace holds " +
                        $"{Describe(live, field)}. The migration was supposed to add new tables and " +
                        $"change nothing that already existed.");
                }
            }
        }

        private static string Describe(SqliteDataReader reader, int field) =>
            reader.IsDBNull(field)
                ? "<null>"
                : $"'{reader.GetValue(field)}'";
    }
}
