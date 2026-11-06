using System.Collections.Concurrent;
using System.Globalization;
using Chatcheology.Archive;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Mobile.OwnMemories
{
    /// <summary>
    /// The phone owner's own memories (CH-54): add, update, delete, list, get, delete all. Kept in
    /// <c>files/own/own-memories.db</c> with their photos in <c>files/own/photos/</c>, apart from every archive.
    /// </summary>
    /// <remarks>
    /// <para>Every write is one transaction: a refused or failed write changes nothing. A draft is checked with
    /// <see cref="OwnMemoryRules.Validate"/> and each photo file must be in the photos folder.</para>
    /// <para>Deleting or updating a memory deletes the photo files no memory uses any more, after the commit. A
    /// file that cannot be deleted is left (it is only unused).</para>
    /// <para>Each operation opens its own connection with <see cref="OwnMemoriesDatabase"/>'s policy, so the UI and
    /// the alarm receiver can use the file at the same time.</para>
    /// <para>Writes are also serialised in the process by one lock per file (review S4): the photo-file check, the
    /// transaction and the deletes after the commit run under it, so a delete never removes a file another write has
    /// just started to use.</para>
    /// <para>At first use in a process the store sweeps the photos folder: partial files older than
    /// <see cref="OwnPhotoIntake.PartialFileAge"/>, and photo files no memory uses that are older than
    /// <see cref="UnusedPhotoAge"/> (a photo added to a form that was then cancelled).</para>
    /// </remarks>
    public sealed class OwnMemoryStore
    {
        /// <summary>How old an unused photo file must be before a sweep deletes it.</summary>
        public static readonly TimeSpan UnusedPhotoAge = TimeSpan.FromHours(24);

        private const string DateFormat = "yyyy'-'MM'-'dd";

        private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, bool> Swept = new(StringComparer.Ordinal);

        private readonly object _writeLock;
        private readonly string _key;

        /// <summary>The store inside <paramref name="filesFolder"/>, the app's files folder.</summary>
        public OwnMemoryStore(string filesFolder)
        {
            ArgumentException.ThrowIfNullOrEmpty(filesFolder);

            DatabasePath = OwnMemoriesDatabase.PathIn(filesFolder);
            PhotosFolder = OwnMemoriesDatabase.PhotosFolderIn(filesFolder);
            Settings = new OwnMemorySettings(this);
            _key = Path.GetFullPath(DatabasePath);
            _writeLock = Locks.GetOrAdd(_key, _ => new object());
        }

        /// <summary>Adds a memory with a new identity.</summary>
        /// <exception cref="OwnMemoryValidationException">The draft breaks a rule; nothing was written.</exception>
        public OwnMemory Add(OwnMemoryDraft draft)
        {
            lock (_writeLock)
            {
                return AddCore(draft);
            }
        }

        /// <summary>Replaces every field of the memory <paramref name="id"/>; photo files it no longer uses and no other memory uses are deleted.</summary>
        /// <exception cref="OwnMemoryValidationException">The draft breaks a rule, or the memory is no longer there; nothing was written.</exception>
        public OwnMemory Update(Guid id, OwnMemoryDraft draft)
        {
            lock (_writeLock)
            {
                return UpdateCore(id, draft);
            }
        }

        /// <summary>Deletes the memory <paramref name="id"/>, its mute, and the photo files no other memory uses.</summary>
        /// <returns>False when it was not there.</returns>
        public bool Delete(Guid id)
        {
            lock (_writeLock)
            {
                return DeleteCore(id);
            }
        }

        /// <summary>
        /// Deletes every own memory, their mutes and every stored photo (and any photo left half-added). The other
        /// own settings (switches, posted day) are kept.
        /// </summary>
        /// <returns>The number of memories deleted.</returns>
        public int DeleteAll()
        {
            lock (_writeLock)
            {
                return DeleteAllCore();
            }
        }

        /// <summary>
        /// Deletes partial files older than <see cref="OwnPhotoIntake.PartialFileAge"/> and photo files that no memory
        /// uses and that were last written before <paramref name="nowUtc"/> minus <see cref="UnusedPhotoAge"/>. A
        /// photo a memory uses is never deleted. Runs once by itself at first use; step 2 may call it again.
        /// </summary>
        /// <returns>The number of files deleted.</returns>
        public int SweepPhotos(DateTime nowUtc)
        {
            lock (_writeLock)
            {
                var deleted = OwnPhotoIntake.SweepPartialFiles(PhotosFolder, nowUtc);

                if (!Directory.Exists(PhotosFolder))
                {
                    return deleted;
                }

                var old = Directory.EnumerateFiles(PhotosFolder)
                    .Where(path => OwnMemoryRules.IsPhotoFileName(Path.GetFileName(path))
                        && File.GetLastWriteTimeUtc(path) < nowUtc - UnusedPhotoAge)
                    .Select(path => Path.GetFileName(path)!)
                    .ToList();

                if (old.Count == 0)
                {
                    return deleted;
                }

                List<string> unused;

                using (var connection = Open())
                using (var transaction = connection.BeginTransaction(deferred: true))
                {
                    unused = Unused(connection, transaction, old);
                    transaction.Commit();
                }

                return deleted + DeleteFiles(unused);
            }
        }

        /// <summary>Opens a connection with the policy (creating the file on first use), sweeping the photos once per process first.</summary>
        internal SqliteConnection Open()
        {
            if (Swept.TryAdd(_key, true))
            {
                SweepPhotos(DateTime.UtcNow);
            }

            return OwnMemoriesDatabase.Open(DatabasePath);
        }

        /// <summary>The own memories file.</summary>
        public string DatabasePath { get; }

        /// <summary>The own photos folder.</summary>
        public string PhotosFolder { get; }

        /// <summary>The per-phone own settings kept in the same file.</summary>
        public OwnMemorySettings Settings { get; }

        /// <summary>Adds a memory with a new identity.</summary>
        /// <exception cref="OwnMemoryValidationException">The draft breaks a rule; nothing was written.</exception>
        private OwnMemory AddCore(OwnMemoryDraft draft)
        {
            Check(draft);

            var id = Guid.NewGuid();

            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                using var command = Command(
                    connection,
                    transaction,
                    """
                    INSERT INTO OwnMemory (MemoryGuid, StartDate, EndDate, DatePrecision, Title, Summary, EveryYear)
                    VALUES ($guid, $start, $end, $precision, $title, $summary, $everyYear);
                    SELECT last_insert_rowid();
                    """);
                command.Parameters.AddWithValue("$guid", GuidText(id));
                AddFields(command, draft);
                var rowID = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

                WriteChildren(connection, transaction, rowID, draft);
                transaction.Commit();
            }

            return Get(id) ?? throw new OwnMemoriesStoreException("The memory was saved but could not be read back.");
        }

        /// <summary>Replaces every field of the memory <paramref name="id"/>.</summary>
        /// <exception cref="OwnMemoryValidationException">The draft breaks a rule, or the memory is no longer there; nothing was written.</exception>
        private OwnMemory UpdateCore(Guid id, OwnMemoryDraft draft)
        {
            Check(draft);

            IReadOnlyList<string> before;

            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                var rowID = RowIDOf(connection, transaction, id)
                    ?? throw Gone();

                before = PhotoFilesOf(connection, transaction, rowID);

                using (var command = Command(
                    connection,
                    transaction,
                    """
                    UPDATE OwnMemory
                    SET StartDate = $start, EndDate = $end, DatePrecision = $precision, Title = $title,
                        Summary = $summary, EveryYear = $everyYear
                    WHERE OwnMemoryID = $id;
                    DELETE FROM OwnMemoryPhoto WHERE OwnMemoryID = $id;
                    DELETE FROM OwnMemoryLink WHERE OwnMemoryID = $id;
                    """))
                {
                    command.Parameters.AddWithValue("$id", rowID);
                    AddFields(command, draft);
                    command.ExecuteNonQuery();
                }

                WriteChildren(connection, transaction, rowID, draft);
                before = Unused(connection, transaction, before);
                transaction.Commit();
            }

            DeleteFiles(before);
            return Get(id) ?? throw Gone();
        }

        /// <summary>Deletes the memory <paramref name="id"/>, its mute, and the photo files no other memory uses.</summary>
        /// <returns>False when it was not there.</returns>
        private bool DeleteCore(Guid id)
        {
            IReadOnlyList<string> files;

            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                if (RowIDOf(connection, transaction, id) is not { } rowID)
                {
                    return false;
                }

                files = PhotoFilesOf(connection, transaction, rowID);

                using (var command = Command(
                    connection,
                    transaction,
                    """
                    DELETE FROM OwnNotifyMute WHERE MemoryGuid = $guid;
                    DELETE FROM OwnMemoryPhoto WHERE OwnMemoryID = $id;
                    DELETE FROM OwnMemoryLink WHERE OwnMemoryID = $id;
                    DELETE FROM OwnMemory WHERE OwnMemoryID = $id;
                    """))
                {
                    command.Parameters.AddWithValue("$id", rowID);
                    command.Parameters.AddWithValue("$guid", GuidText(id));
                    command.ExecuteNonQuery();
                }

                files = Unused(connection, transaction, files);
                transaction.Commit();
            }

            DeleteFiles(files);
            return true;
        }

        /// <summary>
        /// Deletes every own memory, their mutes and every stored photo (and any photo left half-added). The other
        /// own settings (switches, posted day) are kept.
        /// </summary>
        /// <returns>The number of memories deleted.</returns>
        private int DeleteAllCore()
        {
            int deleted;

            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                deleted = Convert.ToInt32(
                    OwnMemoriesDatabase.Scalar(connection, "SELECT count(*) FROM OwnMemory;", transaction), CultureInfo.InvariantCulture);
                OwnMemoriesDatabase.Execute(
                    connection,
                    """
                    DELETE FROM OwnNotifyMute;
                    DELETE FROM OwnMemoryPhoto;
                    DELETE FROM OwnMemoryLink;
                    DELETE FROM OwnMemory;
                    """,
                    transaction);
                transaction.Commit();
            }

            if (Directory.Exists(PhotosFolder))
            {
                DeleteFiles(Directory.EnumerateFiles(PhotosFolder)
                    .Select(Path.GetFileName)
                    .Where(name => OwnMemoryRules.IsPhotoFileName(name) || OwnPhotoIntake.IsIncomingFileName(name))
                    .ToList()!);
            }

            return deleted;
        }

        /// <summary>The memory <paramref name="id"/>, or null.</summary>
        public OwnMemory? Get(Guid id) => Read(id).SingleOrDefault();

        /// <summary>Every own memory, by start date, then in the order they were added.</summary>
        public IReadOnlyList<OwnMemory> List() => Read(null);

        /// <summary>The number of own memories.</summary>
        public int Count()
        {
            using var connection = Open();
            return Convert.ToInt32(OwnMemoriesDatabase.Scalar(connection, "SELECT count(*) FROM OwnMemory;"), CultureInfo.InvariantCulture);
        }

        /// <summary>Whether a stored photo file is used by any memory.</summary>
        public bool IsPhotoUsed(string fileName)
        {
            using var connection = Open();
            using var command = Command(connection, null, "SELECT EXISTS (SELECT 1 FROM OwnMemoryPhoto WHERE FileName = $name);");
            command.Parameters.AddWithValue("$name", fileName);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }

        private IReadOnlyList<OwnMemory> Read(Guid? only)
        {
            using var connection = Open();

            // One read transaction: the memories, photos and links come from the same moment.
            using var transaction = connection.BeginTransaction(deferred: true);
            var filter = only is null ? string.Empty : " WHERE m.MemoryGuid = $guid";

            var photos = new Dictionary<long, List<OwnMemoryPhoto>>();

            using (var command = Command(
                connection,
                transaction,
                "SELECT p.OwnMemoryID, p.FileName, p.Caption FROM OwnMemoryPhoto p JOIN OwnMemory m ON m.OwnMemoryID = p.OwnMemoryID"
                    + filter + " ORDER BY p.OwnMemoryID, p.Ordinal;"))
            {
                AddGuidFilter(command, only);
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    var name = reader.GetString(1);
                    List(photos, reader.GetInt64(0)).Add(new OwnMemoryPhoto(
                        name, reader.IsDBNull(2) ? null : reader.GetString(2), Path.Combine(PhotosFolder, name)));
                }
            }

            var links = new Dictionary<long, List<OwnLink>>();

            using (var command = Command(
                connection,
                transaction,
                "SELECT l.OwnMemoryID, l.Url, l.Label FROM OwnMemoryLink l JOIN OwnMemory m ON m.OwnMemoryID = l.OwnMemoryID"
                    + filter + " ORDER BY l.OwnMemoryID, l.Ordinal;"))
            {
                AddGuidFilter(command, only);
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    List(links, reader.GetInt64(0)).Add(new OwnLink(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
                }
            }

            var memories = new List<OwnMemory>();

            using (var command = Command(
                connection,
                transaction,
                "SELECT m.OwnMemoryID, m.MemoryGuid, m.StartDate, m.EndDate, m.DatePrecision, m.Title, m.Summary, m.EveryYear FROM OwnMemory m"
                    + filter + " ORDER BY m.StartDate, m.OwnMemoryID;"))
            {
                AddGuidFilter(command, only);
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    var rowID = reader.GetInt64(0);
                    memories.Add(new OwnMemory
                    {
                        RowID = rowID,
                        Id = Guid.ParseExact(reader.GetString(1), "D"),
                        Start = ParseDate(reader.GetString(2)),
                        End = reader.IsDBNull(3) ? null : ParseDate(reader.GetString(3)),
                        Precision = reader.GetString(4) == nameof(MemoryDatePrecision.Month) ? MemoryDatePrecision.Month : MemoryDatePrecision.Day,
                        Title = reader.GetString(5),
                        Summary = reader.IsDBNull(6) ? null : reader.GetString(6),
                        EveryYear = reader.GetInt64(7) == 1,
                        Photos = photos.TryGetValue(rowID, out var p) ? p : [],
                        Links = links.TryGetValue(rowID, out var l) ? l : [],
                    });
                }
            }

            transaction.Commit();
            return memories;
        }

        private static List<T> List<T>(Dictionary<long, List<T>> byID, long id)
        {
            if (!byID.TryGetValue(id, out var list))
            {
                list = [];
                byID[id] = list;
            }

            return list;
        }

        private static void AddGuidFilter(SqliteCommand command, Guid? only)
        {
            if (only is { } id)
            {
                command.Parameters.AddWithValue("$guid", GuidText(id));
            }
        }

        private void Check(OwnMemoryDraft draft)
        {
            ArgumentNullException.ThrowIfNull(draft);

            var errors = new Dictionary<OwnMemoryField, string>(OwnMemoryRules.Validate(draft));

            if (!errors.ContainsKey(OwnMemoryField.Photos)
                && (draft.Photos ?? []).Any(p => !File.Exists(Path.Combine(PhotosFolder, p.FileName))))
            {
                errors[OwnMemoryField.Photos] = "A photo is no longer there: add it again.";
            }

            if (errors.Count > 0)
            {
                throw new OwnMemoryValidationException(errors);
            }
        }

        private static void AddFields(SqliteCommand command, OwnMemoryDraft draft)
        {
            var start = PersonalMemoryFieldRules.Normalise(draft.Start!.Value, draft.Precision);
            var end = draft.End is { } last ? PersonalMemoryFieldRules.Normalise(last, draft.Precision) : (DateOnly?)null;

            command.Parameters.AddWithValue("$start", start.ToString(DateFormat, CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$end", end is { } e ? e.ToString(DateFormat, CultureInfo.InvariantCulture) : DBNull.Value);
            command.Parameters.AddWithValue("$precision", draft.Precision.ToString());
            command.Parameters.AddWithValue("$title", draft.Title.Trim());
            command.Parameters.AddWithValue("$summary", (object?)PersonalMemoryFieldRules.NormaliseText(draft.Summary) ?? DBNull.Value);
            command.Parameters.AddWithValue("$everyYear", draft.EveryYear ? 1 : 0);
        }

        private static void WriteChildren(SqliteConnection connection, SqliteTransaction transaction, long rowID, OwnMemoryDraft draft)
        {
            for (var i = 0; i < draft.Photos.Count; i++)
            {
                using var command = Command(
                    connection,
                    transaction,
                    "INSERT INTO OwnMemoryPhoto (OwnMemoryID, Ordinal, FileName, Caption) VALUES ($id, $ordinal, $name, $caption);");
                command.Parameters.AddWithValue("$id", rowID);
                command.Parameters.AddWithValue("$ordinal", i + 1);
                command.Parameters.AddWithValue("$name", draft.Photos[i].FileName);
                command.Parameters.AddWithValue("$caption", (object?)PersonalMemoryFieldRules.NormaliseText(draft.Photos[i].Caption) ?? DBNull.Value);
                command.ExecuteNonQuery();
            }

            for (var i = 0; i < draft.Links.Count; i++)
            {
                using var command = Command(
                    connection,
                    transaction,
                    "INSERT INTO OwnMemoryLink (OwnMemoryID, Ordinal, Url, Label) VALUES ($id, $ordinal, $url, $label);");
                command.Parameters.AddWithValue("$id", rowID);
                command.Parameters.AddWithValue("$ordinal", i + 1);
                command.Parameters.AddWithValue("$url", ExternalLinkRules.NormaliseUrl(draft.Links[i].Url)!);
                command.Parameters.AddWithValue("$label", (object?)ExternalLinkRules.NormaliseLabel(draft.Links[i].Label) ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        private static long? RowIDOf(SqliteConnection connection, SqliteTransaction transaction, Guid id)
        {
            using var command = Command(connection, transaction, "SELECT OwnMemoryID FROM OwnMemory WHERE MemoryGuid = $guid;");
            command.Parameters.AddWithValue("$guid", GuidText(id));
            return command.ExecuteScalar() is long rowID ? rowID : null;
        }

        private static List<string> PhotoFilesOf(SqliteConnection connection, SqliteTransaction transaction, long rowID)
        {
            using var command = Command(connection, transaction, "SELECT FileName FROM OwnMemoryPhoto WHERE OwnMemoryID = $id;");
            command.Parameters.AddWithValue("$id", rowID);
            using var reader = command.ExecuteReader();
            var files = new List<string>();

            while (reader.Read())
            {
                files.Add(reader.GetString(0));
            }

            return files;
        }

        /// <summary>The files of <paramref name="files"/> no memory uses, as the open transaction sees it.</summary>
        private static List<string> Unused(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<string> files)
        {
            var unused = new List<string>();

            foreach (var name in files.Distinct(StringComparer.Ordinal))
            {
                using var command = Command(connection, transaction, "SELECT EXISTS (SELECT 1 FROM OwnMemoryPhoto WHERE FileName = $name);");
                command.Parameters.AddWithValue("$name", name);

                if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                {
                    unused.Add(name);
                }
            }

            return unused;
        }

        private int DeleteFiles(IEnumerable<string> names)
        {
            var deleted = 0;

            foreach (var name in names)
            {
                // Only names of the store's own patterns, never a path.
                if (!OwnMemoryRules.IsPhotoFileName(name) && !OwnPhotoIntake.IsIncomingFileName(name))
                {
                    continue;
                }

                try
                {
                    var path = Path.Combine(PhotosFolder, name);

                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        deleted++;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Left in place: it is only unused.
                }
            }

            return deleted;
        }

        private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = transaction;
            return command;
        }

        private static OwnMemoryValidationException Gone() =>
            new(new Dictionary<OwnMemoryField, string> { [OwnMemoryField.Memory] = "This memory is no longer there." });

        internal static string GuidText(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

        private static DateOnly ParseDate(string text) => DateOnly.ParseExact(text, DateFormat, CultureInfo.InvariantCulture);
    }
}
