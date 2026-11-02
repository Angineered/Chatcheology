using System.Globalization;
using Chatcheology.Archive;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Memories.Personal
{
    /// <summary>One noted date as the memories file holds it (CH-47).</summary>
    /// <param name="NotedDateID">Its identifier.</param>
    /// <param name="Title">The title.</param>
    /// <param name="Month">The month, 1 to 12.</param>
    /// <param name="Day">The day of the month.</param>
    /// <param name="Year">The year it started, or <see langword="null"/>.</param>
    /// <param name="Note">The note, or <see langword="null"/>.</param>
    public sealed record NotedDate(long NotedDateID, string Title, int Month, int Day, int? Year, string? Note);

    /// <summary>What a save of a noted date hands to the store: every field.</summary>
    public sealed record NotedDateDraft
    {
        /// <summary>Required; trimmed, 1 to <see cref="NotedDateRules.MaxTitleLength"/> characters.</summary>
        public required string Title { get; init; }

        /// <summary>1 to 12.</summary>
        public required int Month { get; init; }

        /// <summary>Valid for the month; 29 February needs no year or a leap year.</summary>
        public required int Day { get; init; }

        /// <summary>Optional; from <see cref="NotedDateRules.MinYear"/> to the current year.</summary>
        public int? Year { get; init; }

        /// <summary>Optional; trimmed, empty means none, at most <see cref="NotedDateRules.MaxNoteLength"/> characters.</summary>
        public string? Note { get; init; }
    }

    /// <summary>One noted date that became a memory (CH-53).</summary>
    /// <param name="NotedDateID">The noted date it was (now deleted).</param>
    /// <param name="EntryID">The memory it became.</param>
    public sealed record NotedDateMoved(long NotedDateID, long EntryID);

    /// <summary>What moving noted dates to memories did (CH-53).</summary>
    /// <param name="Moved">Noted dates that became yearly memories.</param>
    /// <param name="LeftWithoutYear">Noted dates without a year, left as they were.</param>
    /// <param name="CollectionID">The personal collection the memories are in.</param>
    /// <param name="CollectionCreated">Whether that collection was made by the move (otherwise an existing one was reused).</param>
    /// <param name="Pairs">Each moved noted date and its memory, in noted-date order.</param>
    public sealed record NotedDateMoveResult(
        int Moved, int LeftWithoutYear, long CollectionID, bool CollectionCreated, IReadOnlyList<NotedDateMoved> Pairs);

    /// <summary>
    /// Noted dates in the memories file beside the open workspace (CH-47): list, add, update, delete, and the
    /// one-off move into yearly memories (CH-53). Since CH-53 the desktop no longer adds noted dates; <see cref="Add"/>
    /// stays for tests and for files made before.
    /// </summary>
    /// <remarks>
    /// <para><strong>Not memories.</strong> A noted date has no collection, entry or link; only the
    /// <c>NotedDate</c> table is written. The workspace is never opened.</para>
    /// <para><strong>One set of rules.</strong> Every field is checked with <see cref="NotedDateRules"/>, the
    /// same rules the archive writer and validator use, before anything is opened for writing.</para>
    /// <para><strong>One transaction per save.</strong> An older memories file (format 1 to 4) is upgraded to
    /// the current format inside the save's transaction, so a failed save leaves it as it was. Read-only opens
    /// of a file before format 4 list no noted dates.</para>
    /// <para><strong>The file is created only when asked.</strong> A write to a missing file is refused, except
    /// <see cref="Add"/> with <c>createFileIfMissing</c>, which the caller passes only after the operator
    /// confirmed (the same rule as the first collection). If that first save fails, the new file is removed.</para>
    /// </remarks>
    public sealed class NotedDateStore
    {
        /// <summary>Creates a store over the memories file at <paramref name="memoriesPath"/>.</summary>
        /// <param name="memoriesPath">A path named <see cref="MemoriesDatabase.FileName"/>.</param>
        public NotedDateStore(string memoriesPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(memoriesPath);

            if (!string.Equals(Path.GetFileName(memoriesPath), MemoriesDatabase.FileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"The memories file must be named '{MemoriesDatabase.FileName}'.", nameof(memoriesPath));
            }

            MemoriesPath = Path.GetFullPath(memoriesPath);
        }

        /// <summary>The store for the memories file beside a main workspace.</summary>
        public static NotedDateStore Beside(string workspacePath) => new(MemoriesDatabase.PathBeside(workspacePath));

        /// <summary>The memories file.</summary>
        public string MemoriesPath { get; }

        /// <summary>
        /// Whether the memories file exists. When it does not, the caller asks the operator before passing
        /// <c>createFileIfMissing</c> to <see cref="Add"/>.
        /// </summary>
        public bool FileExists => File.Exists(MemoriesPath);

        /// <summary>For tests: runs inside each save's transaction, just before it commits.</summary>
        internal Action? BeforeCommit { get; set; }

        /// <summary>
        /// The draft's problems, one message per field (<see cref="PersonalFields.Title"/>,
        /// <see cref="PersonalFields.Date"/>, <see cref="PersonalFields.Year"/>, <see cref="PersonalFields.Note"/>);
        /// empty when it may be saved.
        /// </summary>
        /// <param name="draft">The draft.</param>
        /// <param name="nowUtc">Now, for the latest year allowed (the local current year).</param>
        public static IReadOnlyDictionary<string, string> Validate(NotedDateDraft draft, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(draft);

            return NotedDateRules
                .Validate(draft.Title?.Trim(), draft.Month, draft.Day, draft.Year, NotedDateRules.NormaliseNote(draft.Note), LatestYear(nowUtc))
                .ToDictionary(pair => FieldName(pair.Key), pair => pair.Value);
        }

        /// <summary>
        /// Every noted date, ordered by month, day, title and identifier. None when there is no memories file or
        /// it is an older format (read-only: nothing is upgraded).
        /// </summary>
        public IReadOnlyList<NotedDate> List()
        {
            if (!FileExists)
            {
                return [];
            }

            try
            {
                using var connection = MemoriesDatabase.OpenReadOnlyConnection(MemoriesPath);

                if (MemoriesDatabase.ReadFormatVersion(connection) < MemoriesDatabase.FirstFormatWithNotedDates)
                {
                    return [];
                }

                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT NotedDateID, Title, Month, Day, Year, Note FROM NotedDate ORDER BY Month, Day, Title COLLATE NOCASE, NotedDateID;";

                var dates = new List<NotedDate>();

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    dates.Add(new NotedDate(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5)));
                }

                return dates;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        /// <summary>Adds a noted date.</summary>
        /// <param name="draft">Its fields.</param>
        /// <param name="createFileIfMissing">
        /// True only after the operator confirmed: creates the memories file (current format) when there is
        /// none. A failure afterwards removes the new file again.
        /// </param>
        /// <param name="nowUtc">Now: the creation time recorded in a new file, and the latest year allowed.</param>
        /// <returns>The new noted date's identifier.</returns>
        /// <exception cref="PersonalMemoryValidationException">A field breaks a rule. Nothing was written.</exception>
        /// <exception cref="FileNotFoundException">There is no file and creating one was not asked for.</exception>
        public long Add(NotedDateDraft draft, bool createFileIfMissing, DateTime nowUtc)
        {
            RequireValid(draft, nowUtc);

            var created = false;

            if (!FileExists)
            {
                if (!createFileIfMissing)
                {
                    throw new FileNotFoundException(
                        "There is no memories file beside this workspace yet. Nothing has been created.");
                }

                MemoriesDatabase.Create(MemoriesPath, nowUtc);
                created = true;
            }

            try
            {
                return Write(transaction =>
                {
                    using var command = Command(transaction,
                        """
                        INSERT INTO NotedDate (Title, Month, Day, Year, Note) VALUES ($title, $month, $day, $year, $note);
                        SELECT last_insert_rowid();
                        """);
                    Bind(command, draft);

                    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                });
            }
            catch when (created)
            {
                SqliteConnection.ClearAllPools();

                if (File.Exists(MemoriesPath))
                {
                    File.Delete(MemoriesPath);
                }

                throw;
            }
        }

        /// <summary>Saves a noted date in place (same identifier).</summary>
        /// <exception cref="PersonalMemoryValidationException">A field breaks a rule, or the date is gone. Nothing was written.</exception>
        /// <exception cref="FileNotFoundException">There is no memories file.</exception>
        public void Update(long notedDateID, NotedDateDraft draft, DateTime nowUtc)
        {
            RequireValid(draft, nowUtc);

            Write(transaction =>
            {
                using var command = Command(transaction,
                    "UPDATE NotedDate SET Title = $title, Month = $month, Day = $day, Year = $year, Note = $note WHERE NotedDateID = $id;");
                Bind(command, draft);
                command.Parameters.AddWithValue("$id", notedDateID);

                return command.ExecuteNonQuery() == 1 ? 0 : throw Gone();
            });
        }

        /// <summary>Deletes a noted date and its external links (CH-48).</summary>
        /// <exception cref="PersonalMemoryValidationException">The date is gone. Nothing was written.</exception>
        /// <exception cref="FileNotFoundException">There is no memories file.</exception>
        public void Delete(long notedDateID) => Write(transaction =>
        {
            ExternalLinkStore.DeleteFor(transaction, ExternalLinkOwnerKind.NotedDate, notedDateID);

            using var command = Command(transaction, "DELETE FROM NotedDate WHERE NotedDateID = $id;");
            command.Parameters.AddWithValue("$id", notedDateID);

            return command.ExecuteNonQuery() == 1 ? 0 : throw Gone();
        });

        // -----------------------------------------------------------------------------------
        // Moving noted dates to memories (CH-53).
        // -----------------------------------------------------------------------------------

        /// <summary>The name of the copy of the memories file kept beside it before the move (CH-53).</summary>
        public const string MoveBackupFileName = MemoriesDatabase.FileName + ".before-CH-53";

        /// <summary>The personal collection the moved noted dates go to (CH-53).</summary>
        public const string MovedCollectionName = "Noted dates";

        /// <summary>The source line of a moved noted date (CH-53).</summary>
        public const string MovedSource = "Noted date";

        /// <summary>The copy kept beside the memories file before the move.</summary>
        public string MoveBackupPath => Path.Combine(Path.GetDirectoryName(MemoriesPath)!, MoveBackupFileName);

        /// <summary>For tests: runs inside the move's transaction after each noted date became a memory.</summary>
        internal Action<int>? AfterEachMovedForTests { get; set; }

        /// <summary>
        /// Moves every noted date that has a year into memories, once (CH-53): each becomes a yearly personal memory
        /// dated to its day in the personal collection <see cref="MovedCollectionName"/>, with its gallery links.
        /// Noted dates without a year are left as they are and counted.
        /// </summary>
        /// <remarks>
        /// <para><strong>A copy first.</strong> Before anything is written the memories file is copied, byte for
        /// byte, to <see cref="MoveBackupFileName"/> beside it, under the write lock, so the copy is exactly the
        /// file before the move. An existing copy is never overwritten: the move is refused.</para>
        /// <para><strong>One transaction.</strong> An older file is upgraded to the current format; the number of
        /// noted dates must still be <paramref name="confirmedCount"/>; an existing personal collection of that name
        /// (ignoring case and outer spaces) is reused with its shown-by-default setting, otherwise one is created
        /// and shown; each moved date becomes an entry (title, local midnight of the day, precision Day, summary =
        /// note, source <see cref="MovedSource"/>, Every year; no end, times, key, day level or image); its links
        /// are re-pointed to the entry (same identifiers, order and labels); the moved rows are deleted with plain
        /// SQL; and no link may remain on a moved date. Any failure rolls everything back and removes the copy
        /// this call made, so the move can be tried again.</para>
        /// </remarks>
        /// <param name="confirmedCount">How many noted dates the operator was told there are (all of them, with or without a year).</param>
        /// <exception cref="PersonalMemoryValidationException">
        /// The count changed, no noted date has a year, or the copy already exists. Nothing was written.
        /// </exception>
        /// <exception cref="FileNotFoundException">There is no memories file.</exception>
        public NotedDateMoveResult MoveToMemories(int confirmedCount)
        {
            if (!FileExists)
            {
                throw new FileNotFoundException(
                    "There is no memories file beside this workspace. Nothing has been created.");
            }

            var backup = MoveBackupPath;
            var copied = false;

            try
            {
                using var connection = MemoriesDatabase.OpenConnection(MemoriesPath);
                var version = MemoriesDatabase.ReadFormatVersion(connection);

                // Not deferred: the write lock is held from here, so the copy is the file exactly as it was.
                using var transaction = connection.BeginTransaction(deferred: false);

                var all = version >= MemoriesDatabase.FirstFormatWithNotedDates ? ReadAll(transaction) : [];

                if (all.Count != confirmedCount)
                {
                    throw new PersonalMemoryValidationException(
                        PersonalFields.NotedDate,
                        "The noted dates changed since you were asked, so nothing was moved. Try again.");
                }

                var moving = all.Where(d => d.Year is not null).ToList();

                if (moving.Count == 0)
                {
                    throw new PersonalMemoryValidationException(
                        PersonalFields.NotedDate,
                        "No noted date has a year, so none can become a memory. Give them a year or delete them.");
                }

                if (File.Exists(backup) || Directory.Exists(backup))
                {
                    throw new PersonalMemoryValidationException(
                        PersonalFields.NotedDate,
                        $"A copy named {MoveBackupFileName} is already beside the memories file, and it is never " +
                        "overwritten. Move it somewhere else first.");
                }

                CopyFile(MemoriesPath, backup);
                copied = true;

                MemoriesDatabase.UpgradeToCurrent(connection, transaction);

                var (collectionID, created) = FindOrCreateCollection(transaction);
                var moved = new List<(long NotedDateID, long EntryID)>();

                foreach (var date in moving)
                {
                    var entryID = InsertMovedEntry(transaction, collectionID, date);
                    RepointLinks(transaction, date.NotedDateID, entryID);
                    moved.Add((date.NotedDateID, entryID));

                    AfterEachMovedForTests?.Invoke(moved.Count);
                }

                using (var delete = Command(transaction, "DELETE FROM NotedDate WHERE NotedDateID = $id;"))
                {
                    foreach (var (notedDateID, _) in moved)
                    {
                        delete.Parameters.Clear();
                        delete.Parameters.AddWithValue("$id", notedDateID);

                        if (delete.ExecuteNonQuery() != 1)
                        {
                            throw new InvalidOperationException("A noted date being moved could not be removed. Nothing was moved.");
                        }
                    }
                }

                RequireMoveComplete(transaction, moved, all.Count - moving.Count);

                BeforeCommit?.Invoke();
                transaction.Commit();

                return new NotedDateMoveResult(
                    moved.Count,
                    all.Count - moving.Count,
                    collectionID,
                    created,
                    moved.Select(pair => new NotedDateMoved(pair.NotedDateID, pair.EntryID)).ToList());
            }
            catch
            {
                // The transaction has rolled back: the copy this call made is of a file that did not change.
                SqliteConnection.ClearAllPools();

                if (copied && File.Exists(backup))
                {
                    File.Delete(backup);
                }

                throw;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        private static List<NotedDate> ReadAll(SqliteTransaction transaction)
        {
            using var command = Command(transaction,
                "SELECT NotedDateID, Title, Month, Day, Year, Note FROM NotedDate ORDER BY NotedDateID;");

            var dates = new List<NotedDate>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                dates.Add(new NotedDate(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }

            return dates;
        }

        /// <summary>Copies the file byte for byte to a new file; an existing target is never overwritten.</summary>
        private static void CopyFile(string source, string target)
        {
            // Shared read and write: SQLite holds the file open on this process's own connection.
            using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);

            from.CopyTo(to);
            to.Flush(flushToDisk: true);
        }

        /// <summary>The personal collection named <see cref="MovedCollectionName"/>, matched as names are kept unique, or a new one (shown).</summary>
        private static (long CollectionID, bool Created) FindOrCreateCollection(SqliteTransaction transaction)
        {
            using (var read = Command(transaction, "SELECT CollectionID, Name FROM Collection WHERE Kind = $kind ORDER BY CollectionID;"))
            {
                read.Parameters.AddWithValue("$kind", MemoriesDatabase.KindPersonal);

                using var reader = read.ExecuteReader();

                while (reader.Read())
                {
                    // Compared here, not in SQL, as PersonalMemoryStore keeps names unique.
                    if (string.Equals(reader.GetString(1).Trim(), MovedCollectionName, StringComparison.OrdinalIgnoreCase))
                    {
                        return (reader.GetInt64(0), false);
                    }
                }
            }

            using var insert = Command(transaction,
                """
                INSERT INTO Collection (Kind, Name, SourceDescription, ShownByDefault, PeriodGapMinutes)
                VALUES ($kind, $name, NULL, 1, NULL);
                SELECT last_insert_rowid();
                """);
            insert.Parameters.AddWithValue("$kind", MemoriesDatabase.KindPersonal);
            insert.Parameters.AddWithValue("$name", MovedCollectionName);

            return (Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture), true);
        }

        /// <summary>The entry a moved noted date becomes: a personal Day entry as the memory form writes one, Every year.</summary>
        private static long InsertMovedEntry(SqliteTransaction transaction, long collectionID, NotedDate date)
        {
            // The NotedDate checks guarantee a real day; a stored value that is not one stops the move here.
            var day = new DateOnly(date.Year!.Value, date.Month, date.Day);

            using var command = Command(transaction,
                $"""
                INSERT INTO Entry (CollectionID, SourceKey, Title, StartLocal, EndLocal, StartUtc, EndUtc,
                                   DatePrecision, Summary, ImageSha256, ImageSourceUrl, Source, DayLevel, EveryYear)
                VALUES ($collection, NULL, $title, $start, NULL, NULL, NULL, '{MemoriesDatabase.PrecisionDay}', $summary, NULL, NULL,
                        $source, NULL, 1);
                SELECT last_insert_rowid();
                """);
            command.Parameters.AddWithValue("$collection", collectionID);
            command.Parameters.AddWithValue("$title", date.Title);
            command.Parameters.AddWithValue("$start", MemoriesDatabase.FormatLocal(day.ToDateTime(TimeOnly.MinValue)));
            command.Parameters.AddWithValue("$summary", (object?)date.Note ?? DBNull.Value);
            command.Parameters.AddWithValue("$source", MovedSource);

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        /// <summary>Re-points a noted date's gallery links to the entry it became: same rows, so identifiers, order and labels stay.</summary>
        private static void RepointLinks(SqliteTransaction transaction, long notedDateID, long entryID)
        {
            using var command = Command(transaction,
                $"""
                UPDATE ExternalLink SET OwnerKind = '{ExternalLinkRules.OwnerEntry}', OwnerID = $entry
                WHERE OwnerKind = '{ExternalLinkRules.OwnerNotedDate}' AND OwnerID = $date;
                """);
            command.Parameters.AddWithValue("$entry", entryID);
            command.Parameters.AddWithValue("$date", notedDateID);
            command.ExecuteNonQuery();
        }

        /// <summary>The checks before the move commits: nothing left on a moved date, every other date still there.</summary>
        private static void RequireMoveComplete(SqliteTransaction transaction, IReadOnlyList<(long NotedDateID, long EntryID)> moved, int left)
        {
            var ids = "[" + string.Join(",", moved.Select(m => m.NotedDateID.ToString(CultureInfo.InvariantCulture))) + "]";

            using var command = Command(transaction,
                $"""
                SELECT
                    (SELECT COUNT(*) FROM ExternalLink
                     WHERE OwnerKind = '{ExternalLinkRules.OwnerNotedDate}' AND OwnerID IN (SELECT value FROM json_each($ids))),
                    (SELECT COUNT(*) FROM NotedDate WHERE NotedDateID IN (SELECT value FROM json_each($ids))),
                    (SELECT COUNT(*) FROM NotedDate);
                """);
            command.Parameters.AddWithValue("$ids", ids);

            using var reader = command.ExecuteReader();
            reader.Read();

            if (reader.GetInt64(0) != 0 || reader.GetInt64(1) != 0 || reader.GetInt64(2) != left)
            {
                throw new InvalidOperationException(
                    "The move did not leave the memories file as expected, so nothing was moved.");
            }
        }

        // -----------------------------------------------------------------------------------
        // Plumbing.
        // -----------------------------------------------------------------------------------

        private static PersonalMemoryValidationException Gone() =>
            new(PersonalFields.NotedDate, "That date is no longer there.");

        private static void RequireValid(NotedDateDraft draft, DateTime nowUtc)
        {
            var errors = Validate(draft, nowUtc);

            if (errors.Count > 0)
            {
                throw new PersonalMemoryValidationException(errors);
            }
        }

        /// <summary>The local current year at <paramref name="nowUtc"/>.</summary>
        private static int LatestYear(DateTime nowUtc)
        {
            if (nowUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("Now must be DateTimeKind.Utc.", nameof(nowUtc));
            }

            return TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.Local).Year;
        }

        private static string FieldName(NotedDateField field) => field switch
        {
            NotedDateField.Title => PersonalFields.Title,
            NotedDateField.Date => PersonalFields.Date,
            NotedDateField.Year => PersonalFields.Year,
            _ => PersonalFields.Note,
        };

        private static void Bind(SqliteCommand command, NotedDateDraft draft)
        {
            command.Parameters.AddWithValue("$title", draft.Title.Trim());
            command.Parameters.AddWithValue("$month", draft.Month);
            command.Parameters.AddWithValue("$day", draft.Day);
            command.Parameters.AddWithValue("$year", (object?)draft.Year ?? DBNull.Value);
            command.Parameters.AddWithValue("$note", (object?)NotedDateRules.NormaliseNote(draft.Note) ?? DBNull.Value);
        }

        private T Write<T>(Func<SqliteTransaction, T> write)
        {
            try
            {
                using var connection = MemoriesDatabase.OpenConnection(MemoriesPath);
                using var transaction = connection.BeginTransaction();

                // An older file is upgraded on the first write (CH-47: format 4 adds NotedDate), inside the
                // same transaction, so a failed save leaves the file as it was.
                MemoriesDatabase.UpgradeToCurrent(connection, transaction);

                var result = write(transaction);

                BeforeCommit?.Invoke();
                transaction.Commit();

                return result;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        private static SqliteCommand Command(SqliteTransaction transaction, string sql)
        {
            var command = transaction.Connection!.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;

            return command;
        }
    }
}
