using System.Globalization;
using System.Text.RegularExpressions;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Memories.Personal
{
    /// <summary>
    /// Finds and deletes the files of the memories media folder that nothing in the memories file uses
    /// (CH-46 "Tidy up images").
    /// </summary>
    /// <remarks>
    /// <para><strong>What counts as used.</strong> Every column of every table of the memories file is
    /// read, except <c>MediaFile</c> (the register of stored files, not a use of one), and every value that
    /// is a SHA-256 marks the file of that hash as used, whatever its extension. So
    /// <c>Entry.ImageSha256</c> (covers and game images), <c>EntryImage.Sha256</c> (extra photos),
    /// <c>ItemDefinition.IconSha256</c> (trophy icons) and the unused <c>EntryItem.IconSha256</c> are
    /// all covered, and so is any column a later format adds.</para>
    /// <para><strong>Only <c>&lt;sha256&gt;.&lt;ext&gt;</c> files are ever touched</strong> (64
    /// lower-case hex digits, then png, jpg, gif or webp, exactly as the store writes them). Anything else
    /// in the folder, subfolders included, is counted and left alone.</para>
    /// <para><strong>Deleting checks again.</strong> <see cref="Delete"/> holds a write lock on the memories
    /// file (so no save can add a use meanwhile), reads the uses again, and deletes only the listed files
    /// that are still unused. The memories file itself is not changed: <c>MediaFile</c> rows stay (a later
    /// save of the same bytes writes the file again).</para>
    /// <para>No memories file means nothing is used yet, so nothing is listed or deleted.</para>
    /// </remarks>
    internal static partial class MemoriesMediaTidy
    {
        [GeneratedRegex("^[0-9a-f]{64}\\.(png|jpg|gif|webp)\\z", RegexOptions.CultureInvariant)]
        private static partial Regex StoredName();

        [GeneratedRegex("^[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant)]
        private static partial Regex Sha256Value();

        /// <summary>Lists the unused files, read-only.</summary>
        internal static UnusedImagesSurvey Survey(string memoriesPath, string mediaFolder)
        {
            if (!File.Exists(memoriesPath) || !Directory.Exists(mediaFolder))
            {
                return UnusedImagesSurvey.Empty;
            }

            HashSet<string> used;

            try
            {
                using var connection = MemoriesDatabase.OpenReadOnlyConnection(memoriesPath);
                used = ReadUsed(connection, null);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }

            var (stored, other) = ListFolder(mediaFolder);
            var unused = new List<string>();
            long bytes = 0;
            var usedFiles = 0;

            foreach (var (name, size) in stored)
            {
                if (used.Contains(name[..64]))
                {
                    usedFiles++;
                }
                else
                {
                    unused.Add(name);
                    bytes += size;
                }
            }

            unused.Sort(StringComparer.Ordinal);

            return new UnusedImagesSurvey(unused, bytes, usedFiles, other);
        }

        /// <summary>
        /// Deletes the <paramref name="listed"/> files that are still unused, under a write lock on the
        /// memories file and after reading every use again. A listed name not of the stored pattern is
        /// never deleted.
        /// </summary>
        internal static UnusedImagesDeleteResult Delete(string memoriesPath, string mediaFolder, IReadOnlyCollection<string> listed)
        {
            ArgumentNullException.ThrowIfNull(listed);

            if (!File.Exists(memoriesPath))
            {
                throw new FileNotFoundException("There is no memories file beside this workspace, so no image is deleted.");
            }

            int deleted = 0, nowUsed = 0, gone = 0, failed = 0;
            long bytes = 0;

            try
            {
                using var connection = MemoriesDatabase.OpenConnection(memoriesPath);

                // BEGIN IMMEDIATE: no other connection can write (add a use) until this ends.
                using var transaction = connection.BeginTransaction(deferred: false);
                var used = ReadUsed(connection, transaction);

                foreach (var name in listed.Distinct(StringComparer.Ordinal))
                {
                    if (!StoredName().IsMatch(name))
                    {
                        failed++;
                        continue;
                    }

                    if (used.Contains(name[..64]))
                    {
                        nowUsed++;
                        continue;
                    }

                    var path = Path.Combine(mediaFolder, name);
                    var info = new FileInfo(path);

                    if (!info.Exists)
                    {
                        gone++;
                        continue;
                    }

                    try
                    {
                        var size = info.Length;
                        info.Delete();
                        deleted++;
                        bytes += size;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        failed++;
                    }
                }

                // Nothing in the memories file was changed.
                transaction.Rollback();
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }

            return new UnusedImagesDeleteResult(deleted, bytes, nowUsed, gone, failed);
        }

        /// <summary>Every SHA-256 any column of any table (but <c>MediaFile</c>) holds, lower-cased.</summary>
        private static HashSet<string> ReadUsed(SqliteConnection connection, SqliteTransaction? transaction)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var columns = new List<(string Table, string Column)>();

            foreach (var table in WorkspaceDatabase.ReadApplicationTableNames(connection))
            {
                if (string.Equals(table, "MediaFile", StringComparison.Ordinal))
                {
                    continue;
                }

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT name FROM pragma_table_info($table);";
                command.Parameters.AddWithValue("$table", table);

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    columns.Add((table, reader.GetString(0)));
                }
            }

            foreach (var (table, column) in columns)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = string.Create(
                    CultureInfo.InvariantCulture,
                    $"SELECT DISTINCT {Quote(column)} FROM {Quote(table)} WHERE typeof({Quote(column)}) = 'text' AND length({Quote(column)}) = 64;");

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    var value = reader.GetString(0);

                    if (Sha256Value().IsMatch(value))
                    {
                        used.Add(value.ToLowerInvariant());
                    }
                }
            }

            return used;
        }

        /// <summary>The folder's stored-pattern files with their sizes, and how many other files and folders it holds.</summary>
        private static (List<(string Name, long Size)> Stored, int Other) ListFolder(string mediaFolder)
        {
            var stored = new List<(string, long)>();
            var other = 0;

            foreach (var item in new DirectoryInfo(mediaFolder).EnumerateFileSystemInfos())
            {
                if (item is FileInfo file && StoredName().IsMatch(file.Name))
                {
                    stored.Add((file.Name, file.Length));
                }
                else
                {
                    other++;
                }
            }

            return (stored, other);
        }

        private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
