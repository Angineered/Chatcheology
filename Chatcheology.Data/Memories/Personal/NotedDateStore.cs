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

    /// <summary>
    /// Noted dates in the memories file beside the open workspace (CH-47): list, add, update, delete.
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
