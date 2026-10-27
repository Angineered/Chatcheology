using Chatcheology.Data.Memories;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Memories
{
    /// <summary>A temporary folder standing in for the main workspace's folder.</summary>
    internal sealed class MemoriesTestFolder : IDisposable
    {
        internal static readonly DateTime Now = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);

        internal MemoriesTestFolder()
        {
            Root = Path.Combine(Path.GetTempPath(), "Chatcheology.Memories.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal string MemoriesPath => Path.Combine(Root, MemoriesDatabase.FileName);

        internal string MediaFolder => Path.Combine(Root, MemoriesDatabase.MediaFolderName);

        internal string CreateMemories()
        {
            MemoriesDatabase.Create(MemoriesPath, Now);
            return MemoriesPath;
        }

        internal long Scalar(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={MemoriesPath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        internal List<string> Strings(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={MemoriesPath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var values = new List<string>();

            while (reader.Read())
            {
                values.Add(reader.IsDBNull(0) ? "<null>" : reader.GetValue(0).ToString()!);
            }

            return values;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder must not fail a test.
            }
        }
    }
}
