using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Mobile.OwnMemories
{
    /// <summary>The own memories file could not be used: refused (WAL, an unknown version) or damaged. Nothing was written.</summary>
    public sealed class OwnMemoriesStoreException : Exception
    {
        /// <summary>Creates the refusal.</summary>
        public OwnMemoriesStoreException(string message)
            : base(message)
        {
        }

        /// <summary>Creates the refusal with its cause.</summary>
        public OwnMemoriesStoreException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>
    /// The phone owner's own memories file, <c>files/own/own-memories.db</c> (CH-54), and the one connection policy
    /// every reader and writer uses (the UI and, from step 1b, the alarm receiver).
    /// </summary>
    /// <remarks>
    /// <para>Policy: a new connection per operation, no pooling (so the file can be deleted at once), journal mode
    /// DELETE (a WAL file is refused, never converted), a busy timeout of <see cref="BusyTimeoutMilliseconds"/> so a
    /// read that meets a write's commit waits instead of failing, and foreign keys on.</para>
    /// <para>Version: <c>PRAGMA user_version</c> = <see cref="SchemaVersion"/>. A new or empty file is created at
    /// that version; a higher or unknown version, or a version 0 file that already holds tables, is refused.</para>
    /// <para>The file lives outside <c>files/archives/</c>, so removing an archive or forgetting its state never
    /// touches it.</para>
    /// </remarks>
    public static class OwnMemoriesDatabase
    {
        /// <summary>The folder under the app's files folder.</summary>
        public const string FolderName = "own";

        /// <summary>The file's name.</summary>
        public const string FileName = "own-memories.db";

        /// <summary>The photos folder's name, inside <see cref="FolderName"/>.</summary>
        public const string PhotosFolderName = "photos";

        /// <summary>The schema version this code writes and reads.</summary>
        public const int SchemaVersion = 1;

        /// <summary>How long an operation waits for another connection's lock before it fails.</summary>
        public const int BusyTimeoutMilliseconds = 5000;

        private const string Schema =
            """
            CREATE TABLE OwnMemory (
                OwnMemoryID INTEGER PRIMARY KEY AUTOINCREMENT,
                MemoryGuid TEXT NOT NULL UNIQUE CHECK (typeof(MemoryGuid) = 'text' AND length(MemoryGuid) = 36),
                StartDate TEXT NOT NULL CHECK (typeof(StartDate) = 'text' AND length(StartDate) = 10),
                EndDate TEXT NULL CHECK (EndDate IS NULL OR (typeof(EndDate) = 'text' AND length(EndDate) = 10)),
                DatePrecision TEXT NOT NULL CHECK (DatePrecision IN ('Day', 'Month')),
                Title TEXT NOT NULL CHECK (typeof(Title) = 'text' AND length(trim(Title)) > 0 AND length(Title) <= 200),
                Summary TEXT NULL CHECK (Summary IS NULL OR (typeof(Summary) = 'text' AND length(trim(Summary)) > 0 AND length(Summary) <= 2000)),
                EveryYear INTEGER NOT NULL DEFAULT 0 CHECK (EveryYear IN (0, 1)),
                CHECK (EveryYear = 0 OR (DatePrecision = 'Day' AND EndDate IS NULL)),
                CHECK (EndDate IS NULL OR EndDate >= StartDate)
            );

            CREATE TABLE OwnMemoryPhoto (
                OwnMemoryID INTEGER NOT NULL REFERENCES OwnMemory (OwnMemoryID) ON DELETE CASCADE,
                Ordinal INTEGER NOT NULL CHECK (typeof(Ordinal) = 'integer' AND Ordinal >= 1),
                FileName TEXT NOT NULL CHECK (typeof(FileName) = 'text' AND length(FileName) BETWEEN 68 AND 69),
                Caption TEXT NULL CHECK (Caption IS NULL OR (typeof(Caption) = 'text' AND length(trim(Caption)) > 0 AND length(Caption) <= 500)),
                PRIMARY KEY (OwnMemoryID, Ordinal),
                UNIQUE (OwnMemoryID, FileName)
            );

            CREATE INDEX OwnMemoryPhotoFile ON OwnMemoryPhoto (FileName);

            CREATE TABLE OwnMemoryLink (
                OwnMemoryID INTEGER NOT NULL REFERENCES OwnMemory (OwnMemoryID) ON DELETE CASCADE,
                Ordinal INTEGER NOT NULL CHECK (typeof(Ordinal) = 'integer' AND Ordinal BETWEEN 1 AND 10),
                Url TEXT NOT NULL CHECK (typeof(Url) = 'text' AND length(Url) > 8 AND length(Url) <= 2000),
                Label TEXT NULL CHECK (Label IS NULL OR (typeof(Label) = 'text' AND length(trim(Label)) > 0 AND length(Label) <= 60)),
                PRIMARY KEY (OwnMemoryID, Ordinal)
            );

            CREATE TABLE OwnSetting (
                SettingKey TEXT NOT NULL PRIMARY KEY,
                SettingValue TEXT NOT NULL
            );

            CREATE TABLE OwnNotifyMute (
                MemoryGuid TEXT NOT NULL PRIMARY KEY REFERENCES OwnMemory (MemoryGuid) ON DELETE CASCADE
            );
            """;

        /// <summary>The file inside <paramref name="filesFolder"/> (the app's files folder).</summary>
        public static string PathIn(string filesFolder) => Path.Combine(filesFolder, FolderName, FileName);

        /// <summary>The photos folder inside <paramref name="filesFolder"/>.</summary>
        public static string PhotosFolderIn(string filesFolder) => Path.Combine(filesFolder, FolderName, PhotosFolderName);

        /// <summary>
        /// Opens the file at <paramref name="path"/> with the policy, creating it (and its folder) at
        /// <see cref="SchemaVersion"/> when it is not there or empty.
        /// </summary>
        /// <exception cref="OwnMemoriesStoreException">WAL, an unknown or higher version, or a foreign file.</exception>
        public static SqliteConnection Open(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = BusyTimeoutMilliseconds / 1000,
            }.ToString());

            try
            {
                connection.Open();
                Execute(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)};");

                // Read the journal mode without changing it: a WAL file is someone else's and is refused as it is.
                var mode = Convert.ToString(Scalar(connection, "PRAGMA journal_mode;"), CultureInfo.InvariantCulture);

                if (string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    throw new OwnMemoriesStoreException("The own memories file uses WAL journaling, which this app does not use. It was left as it is.");
                }

                var version = Convert.ToInt64(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);

                if (version == 0)
                {
                    Create(connection);
                }
                else if (version != SchemaVersion)
                {
                    throw new OwnMemoriesStoreException(
                        $"The own memories file has version {version.ToString(CultureInfo.InvariantCulture)}, which this app cannot read. It was left as it is.");
                }

                Execute(connection, "PRAGMA foreign_keys = ON;");
                return connection;
            }
            catch (SqliteException exception)
            {
                connection.Dispose();
                throw new OwnMemoriesStoreException("The own memories file could not be opened.", exception);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        /// <summary>Creates the schema in an empty version 0 file, in one transaction; refuses a file that holds tables.</summary>
        private static void Create(SqliteConnection connection)
        {
            using var transaction = connection.BeginTransaction(deferred: false);

            var tables = Convert.ToInt64(
                Scalar(connection, "SELECT count(*) FROM sqlite_master;", transaction), CultureInfo.InvariantCulture);

            // Another connection may have created it between the version read and this lock.
            var version = Convert.ToInt64(Scalar(connection, "PRAGMA user_version;", transaction), CultureInfo.InvariantCulture);

            if (version == SchemaVersion)
            {
                transaction.Rollback();
                return;
            }

            if (version != 0 || tables > 0)
            {
                throw new OwnMemoriesStoreException("The own memories file holds something this app did not write. It was left as it is.");
            }

            // A new file's journal mode is DELETE (the default); only WAL is kept in a file, and that was refused above.
            Execute(connection, Schema, transaction);
            Execute(connection, $"PRAGMA user_version = {SchemaVersion.ToString(CultureInfo.InvariantCulture)};", transaction);
            transaction.Commit();
        }

        internal static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }

        internal static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = transaction;
            return command.ExecuteScalar();
        }
    }
}
