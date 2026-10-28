using System.Globalization;
using Chatcheology.Archive;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Memories.Personal
{
    /// <summary>
    /// Personal memories in the memories file beside the open workspace (CH-42 step 4): personal
    /// collections, and dated entries with an optional image and links.
    /// </summary>
    /// <remarks>
    /// <para><strong>Only the memories file is written.</strong> The main workspace is read through
    /// <see cref="IMessageLinkTargets"/> (read-only) to check a message link; nothing here opens it for
    /// writing. A chosen image file is only read; its bytes are copied into the memories media folder
    /// under their SHA-256, once per distinct content.</para>
    /// <para><strong>Imported collections and entries are read-only.</strong> Renaming, adding to,
    /// editing or deleting them is refused. They can be the target of a link.</para>
    /// <para><strong>One transaction per save.</strong> Every check runs first; a failure anywhere rolls
    /// the whole save back, and an image file this save copied in is removed again.</para>
    /// <para><strong>The file is created only when asked.</strong> A write to a missing file is refused,
    /// except <see cref="CreateCollection"/> with <c>createFileIfMissing</c>, which the caller passes
    /// only after the operator confirmed. If that first save fails, the new file is removed.</para>
    /// </remarks>
    public sealed class PersonalMemoryStore
    {
        /// <summary>The default size cap for a personal image.</summary>
        public const long DefaultMaxImageBytes = 20 * 1024 * 1024;

        private readonly IMessageLinkTargets _messages;

        /// <summary>Creates a store over the memories file at <paramref name="memoriesPath"/>.</summary>
        /// <param name="memoriesPath">A path named <see cref="MemoriesDatabase.FileName"/>.</param>
        /// <param name="messages">The open conversation's messages, for message links.</param>
        /// <param name="maxImageBytes">The size cap for an image.</param>
        public PersonalMemoryStore(string memoriesPath, IMessageLinkTargets messages, long maxImageBytes = DefaultMaxImageBytes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(memoriesPath);

            if (!string.Equals(Path.GetFileName(memoriesPath), MemoriesDatabase.FileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"The memories file must be named '{MemoriesDatabase.FileName}'.", nameof(memoriesPath));
            }

            if (maxImageBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxImageBytes), "The size cap must be positive.");
            }

            MemoriesPath = Path.GetFullPath(memoriesPath);
            MediaFolder = MemoriesDatabase.MediaFolderFor(MemoriesPath);
            _messages = messages ?? throw new ArgumentNullException(nameof(messages));
            MaxImageBytes = maxImageBytes;
        }

        /// <summary>The store for the memories file beside a main workspace.</summary>
        public static PersonalMemoryStore Beside(string workspacePath, IMessageLinkTargets messages, long maxImageBytes = DefaultMaxImageBytes) =>
            new(MemoriesDatabase.PathBeside(workspacePath), messages, maxImageBytes);

        /// <summary>The memories file.</summary>
        public string MemoriesPath { get; }

        /// <summary>The image folder beside it.</summary>
        public string MediaFolder { get; }

        /// <summary>The size cap for an image.</summary>
        public long MaxImageBytes { get; }

        /// <summary>Whether the memories file exists.</summary>
        public bool FileExists => File.Exists(MemoriesPath);

        /// <summary>For tests: runs inside each save's transaction, just before it commits.</summary>
        internal Action? BeforeCommit { get; set; }

        // -----------------------------------------------------------------------------------
        // Reading.
        // -----------------------------------------------------------------------------------

        /// <summary>Every collection: personal ones with their entries (date order), imported ones by count.</summary>
        public MemoriesOverview ReadOverview()
        {
            if (!FileExists)
            {
                return MemoriesOverview.Missing;
            }

            return Read(connection =>
            {
                var personal = new List<(long ID, string Name)>();
                var imported = new List<ImportedCollectionSummary>();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        """
                        SELECT c.CollectionID, c.Kind, c.Name, (SELECT COUNT(*) FROM Entry e WHERE e.CollectionID = c.CollectionID)
                        FROM Collection c ORDER BY c.Name COLLATE NOCASE, c.CollectionID;
                        """;

                    using var reader = command.ExecuteReader();

                    while (reader.Read())
                    {
                        if (reader.GetString(1) == MemoriesDatabase.KindPersonal)
                        {
                            personal.Add((reader.GetInt64(0), reader.GetString(2)));
                        }
                        else
                        {
                            imported.Add(new ImportedCollectionSummary(reader.GetInt64(0), reader.GetString(2), reader.GetInt32(3)));
                        }
                    }
                }

                var entries = new Dictionary<long, List<PersonalEntrySummary>>();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        $"""
                        SELECT e.EntryID, e.CollectionID, e.Title, e.StartLocal, e.EndLocal, e.DatePrecision
                        FROM Entry e JOIN Collection c ON c.CollectionID = e.CollectionID
                        WHERE c.Kind = '{MemoriesDatabase.KindPersonal}'
                        ORDER BY e.StartLocal, e.EntryID;
                        """;

                    using var reader = command.ExecuteReader();

                    while (reader.Read())
                    {
                        var summary = new PersonalEntrySummary(
                            reader.GetInt64(0),
                            reader.GetInt64(1),
                            reader.GetString(2),
                            Local(reader.GetString(3)),
                            reader.IsDBNull(4) ? null : Local(reader.GetString(4)),
                            reader.GetString(5));

                        if (!entries.TryGetValue(summary.CollectionID, out var list))
                        {
                            list = [];
                            entries[summary.CollectionID] = list;
                        }

                        list.Add(summary);
                    }
                }

                return new MemoriesOverview(
                    true,
                    personal.Select(p => new PersonalCollectionSummary(p.ID, p.Name, entries.GetValueOrDefault(p.ID) ?? [])).ToList(),
                    imported);
            });
        }

        /// <summary>One personal memory with its links, for the form.</summary>
        /// <exception cref="PersonalMemoryValidationException">No such entry, an imported one, or one with a time of day.</exception>
        public PersonalEntryDetails ReadEntry(long entryID) => Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT e.CollectionID, c.Kind, e.Title, e.StartLocal, e.EndLocal, e.DatePrecision, e.Summary, e.Source, e.ImageSha256, m.Extension
                FROM Entry e JOIN Collection c ON c.CollectionID = e.CollectionID
                LEFT JOIN MediaFile m ON m.Sha256 = e.ImageSha256
                WHERE e.EntryID = $id;
                """;
            command.Parameters.AddWithValue("$id", entryID);

            PersonalEntryDetails details;

            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    throw new PersonalMemoryValidationException(PersonalFields.Entry, "That memory is no longer there.");
                }

                if (reader.GetString(1) != MemoriesDatabase.KindPersonal)
                {
                    throw new PersonalMemoryValidationException(PersonalFields.Entry, ImportedReadOnly);
                }

                if (!PersonalMemoryRules.TryParsePrecision(reader.GetString(5), out var precision))
                {
                    throw new PersonalMemoryValidationException(
                        PersonalFields.Entry, "That memory has a time of day; this form edits memories dated by day or month only.");
                }

                var sha = reader.IsDBNull(8) ? null : reader.GetString(8);
                var imagePath = sha is not null && !reader.IsDBNull(9) ? Path.Combine(MediaFolder, sha + "." + reader.GetString(9)) : null;

                details = new PersonalEntryDetails
                {
                    EntryID = entryID,
                    CollectionID = reader.GetInt64(0),
                    Title = reader.GetString(2),
                    Start = DateOnly.FromDateTime(Local(reader.GetString(3))),
                    End = reader.IsDBNull(4) ? null : DateOnly.FromDateTime(Local(reader.GetString(4))),
                    Precision = precision,
                    Summary = reader.IsDBNull(6) ? null : reader.GetString(6),
                    Source = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    ImageSha256 = sha,
                    ImagePath = imagePath is not null && File.Exists(imagePath) ? imagePath : null,
                    Links = [],
                };
            }

            command.Parameters.Clear();
            command.CommandText =
                """
                SELECT l.EntryLinkID, l.ToEntryID, l.ToMessageSequenceNumber, t.Title, t.StartLocal, t.EndLocal, t.DatePrecision, c.Name
                FROM EntryLink l
                LEFT JOIN Entry t ON t.EntryID = l.ToEntryID
                LEFT JOIN Collection c ON c.CollectionID = t.CollectionID
                WHERE l.FromEntryID = $id ORDER BY l.EntryLinkID;
                """;
            command.Parameters.AddWithValue("$id", entryID);

            var links = new List<PersonalLinkDetails>();

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.IsDBNull(1))
                    {
                        links.Add(new PersonalLinkDetails(
                            reader.GetInt64(0), PersonalLinkTarget.ToMessage(reader.GetInt32(2)), null));
                    }
                    else
                    {
                        links.Add(new PersonalLinkDetails(
                            reader.GetInt64(0),
                            PersonalLinkTarget.ToEntry(reader.GetInt64(1)),
                            Describe(reader.GetString(7), reader.GetString(3), reader.GetString(6), Local(reader.GetString(4)),
                                reader.IsDBNull(5) ? null : Local(reader.GetString(5)))));
                    }
                }
            }

            return details with { Links = links };
        });

        /// <summary>Every entry (any collection) starting on <paramref name="day"/>, for the link picker.</summary>
        public IReadOnlyList<EntryLinkCandidate> ReadEntriesOn(DateOnly day)
        {
            if (!FileExists)
            {
                return [];
            }

            return Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT e.EntryID, c.Name, e.Title, e.StartLocal, e.EndLocal, e.DatePrecision
                    FROM Entry e JOIN Collection c ON c.CollectionID = e.CollectionID
                    WHERE e.StartLocal >= $from AND e.StartLocal < $to
                    ORDER BY e.StartLocal, e.EntryID;
                    """;
                command.Parameters.AddWithValue("$from", MemoriesDatabase.FormatLocal(day.ToDateTime(TimeOnly.MinValue)));
                command.Parameters.AddWithValue("$to", MemoriesDatabase.FormatLocal(day.AddDays(1).ToDateTime(TimeOnly.MinValue)));

                var candidates = new List<EntryLinkCandidate>();

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    var start = Local(reader.GetString(3));
                    var end = reader.IsDBNull(4) ? (DateTime?)null : Local(reader.GetString(4));

                    candidates.Add(new EntryLinkCandidate(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        PersonalMemoryRules.DateText(reader.GetString(5), start, end)));
                }

                return (IReadOnlyList<EntryLinkCandidate>)candidates;
            });
        }

        /// <summary>An entry as a link line shows it, or null when it is not there.</summary>
        public string? DescribeEntry(long entryID)
        {
            if (!FileExists)
            {
                return null;
            }

            return Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT c.Name, e.Title, e.DatePrecision, e.StartLocal, e.EndLocal
                    FROM Entry e JOIN Collection c ON c.CollectionID = e.CollectionID WHERE e.EntryID = $id;
                    """;
                command.Parameters.AddWithValue("$id", entryID);

                using var reader = command.ExecuteReader();

                return reader.Read()
                    ? Describe(reader.GetString(0), reader.GetString(1), reader.GetString(2), Local(reader.GetString(3)),
                        reader.IsDBNull(4) ? null : Local(reader.GetString(4)))
                    : null;
            });
        }

        // -----------------------------------------------------------------------------------
        // Collections.
        // -----------------------------------------------------------------------------------

        /// <summary>Creates a personal collection (shown by default).</summary>
        /// <param name="name">Required, unique among personal collections (ignoring case and outer spaces).</param>
        /// <param name="createFileIfMissing">
        /// True only after the operator confirmed: creates the memories file (current format) when there
        /// is none. A failure afterwards removes the new file again.
        /// </param>
        /// <param name="nowUtc">The creation time recorded in a new file.</param>
        /// <returns>The new collection's identifier.</returns>
        /// <exception cref="PersonalMemoryValidationException">The name is missing, too long or taken.</exception>
        /// <exception cref="FileNotFoundException">There is no file and creating one was not asked for.</exception>
        public long CreateCollection(string name, bool createFileIfMissing, DateTime nowUtc)
        {
            var trimmed = RequireCollectionName(name);
            var created = false;

            if (!FileExists)
            {
                if (!createFileIfMissing)
                {
                    throw new FileNotFoundException(MissingFile);
                }

                MemoriesDatabase.Create(MemoriesPath, nowUtc);
                created = true;
            }

            try
            {
                return Write(transaction =>
                {
                    RequireUniqueName(transaction, trimmed, exceptCollectionID: null);

                    using var command = Command(transaction,
                        """
                        INSERT INTO Collection (Kind, Name, SourceDescription, ShownByDefault, PeriodGapMinutes)
                        VALUES ($kind, $name, NULL, 1, NULL);
                        SELECT last_insert_rowid();
                        """);
                    command.Parameters.AddWithValue("$kind", MemoriesDatabase.KindPersonal);
                    command.Parameters.AddWithValue("$name", trimmed);

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

        /// <summary>Renames a personal collection.</summary>
        /// <exception cref="PersonalMemoryValidationException">The name is missing, too long or taken, or the collection is imported or gone.</exception>
        public void RenameCollection(long collectionID, string name)
        {
            var trimmed = RequireCollectionName(name);

            Write(transaction =>
            {
                RequirePersonalCollection(transaction, collectionID, PersonalFields.CollectionName);
                RequireUniqueName(transaction, trimmed, collectionID);

                using var command = Command(transaction, "UPDATE Collection SET Name = $name WHERE CollectionID = $id;");
                command.Parameters.AddWithValue("$name", trimmed);
                command.Parameters.AddWithValue("$id", collectionID);
                command.ExecuteNonQuery();

                return 0;
            });
        }

        // -----------------------------------------------------------------------------------
        // Entries.
        // -----------------------------------------------------------------------------------

        /// <summary>Adds a personal memory.</summary>
        /// <returns>The new entry's identifier.</returns>
        /// <exception cref="PersonalMemoryValidationException">Anything wrong with the draft. Nothing was written.</exception>
        public long AddEntry(PersonalEntryDraft draft) => Save(null, draft);

        /// <summary>Saves a personal memory in place: same identifier; links kept unless the draft drops them.</summary>
        /// <exception cref="PersonalMemoryValidationException">Anything wrong with the draft, or the entry is imported or gone.</exception>
        public void UpdateEntry(long entryID, PersonalEntryDraft draft) => Save(entryID, draft);

        /// <summary>Deletes a personal memory and every link from or to it.</summary>
        /// <returns>The number of links removed.</returns>
        /// <exception cref="PersonalMemoryValidationException">The entry is imported or gone.</exception>
        public int DeleteEntry(long entryID) => Write(transaction =>
        {
            RequirePersonalEntry(transaction, entryID);

            using var command = Command(transaction, "DELETE FROM EntryLink WHERE FromEntryID = $id OR ToEntryID = $id;");
            command.Parameters.AddWithValue("$id", entryID);
            var links = command.ExecuteNonQuery();

            command.CommandText = "DELETE FROM Entry WHERE EntryID = $id;";
            command.ExecuteNonQuery();

            return links;
        });

        private long Save(long? entryID, PersonalEntryDraft draft)
        {
            ArgumentNullException.ThrowIfNull(draft);

            var errors = new Dictionary<string, string>(PersonalMemoryRules.Validate(draft));

            // The image is read and checked before anything is opened for writing.
            PersonalImageFile? image = null;

            if (draft.Image.NewFilePath is { } imagePath)
            {
                image = PersonalImageFile.Read(imagePath, MaxImageBytes, out var imageError);

                if (imageError is not null)
                {
                    errors[PersonalFields.Image] = imageError;
                }
            }

            var links = draft.Links.Distinct().ToList();

            foreach (var link in links)
            {
                if (link.MessageSequenceNumber is { } sequence && (sequence < 1 || !_messages.Exists(sequence)))
                {
                    errors[PersonalFields.Links] = "A linked message is not in the open conversation.";
                }
            }

            if (errors.Count > 0)
            {
                throw new PersonalMemoryValidationException(errors);
            }

            string? newImageFile = null;

            try
            {
                return Write(transaction =>
                {
                    var failures = new Dictionary<string, string>();

                    if (!IsPersonalCollection(transaction, draft.CollectionID))
                    {
                        failures[PersonalFields.Collection] = "Choose one of your own collections.";
                    }

                    if (entryID is { } existing)
                    {
                        RequirePersonalEntry(transaction, existing);
                    }

                    foreach (var link in links)
                    {
                        if (link.EntryID is { } target && (target == entryID || !EntryExists(transaction, target)))
                        {
                            failures[PersonalFields.Links] = target == entryID
                                ? "A memory cannot link to itself."
                                : "A linked memory is no longer there.";
                        }
                    }

                    if (draft.Image.ExistingSha256 is { } sha && !MediaRecorded(transaction, sha))
                    {
                        failures[PersonalFields.Image] = "The memory's image is no longer recorded; choose it again or remove it.";
                    }

                    if (failures.Count > 0)
                    {
                        throw new PersonalMemoryValidationException(failures);
                    }

                    var imageSha = draft.Image.ExistingSha256;

                    if (image is not null)
                    {
                        newImageFile = StoreImage(transaction, image);
                        imageSha = image.Sha256;
                    }

                    var id = entryID is { } update
                        ? UpdateRow(transaction, update, draft, imageSha)
                        : InsertRow(transaction, draft, imageSha);

                    ReplaceLinks(transaction, id, links);

                    return id;
                });
            }
            catch
            {
                if (newImageFile is not null && File.Exists(newImageFile))
                {
                    File.Delete(newImageFile);
                }

                throw;
            }
        }

        private static long InsertRow(SqliteTransaction transaction, PersonalEntryDraft draft, string? imageSha)
        {
            using var command = Command(transaction,
                """
                INSERT INTO Entry (CollectionID, SourceKey, Title, StartLocal, EndLocal, StartUtc, EndUtc,
                                   DatePrecision, Summary, ImageSha256, ImageSourceUrl, Source, DayLevel)
                VALUES ($collection, NULL, $title, $start, $end, NULL, NULL, $precision, $summary, $image, NULL, $source, NULL);
                SELECT last_insert_rowid();
                """);
            Bind(command, draft, imageSha);

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static long UpdateRow(SqliteTransaction transaction, long entryID, PersonalEntryDraft draft, string? imageSha)
        {
            using var command = Command(transaction,
                """
                UPDATE Entry SET CollectionID = $collection, Title = $title, StartLocal = $start, EndLocal = $end,
                                 DatePrecision = $precision, Summary = $summary, ImageSha256 = $image, Source = $source
                WHERE EntryID = $id;
                """);
            Bind(command, draft, imageSha);
            command.Parameters.AddWithValue("$id", entryID);
            command.ExecuteNonQuery();

            return entryID;
        }

        private static void Bind(SqliteCommand command, PersonalEntryDraft draft, string? imageSha)
        {
            var start = PersonalMemoryRules.Normalise(draft.Start!.Value, draft.Precision);
            var end = draft.End is { } last ? PersonalMemoryRules.Normalise(last, draft.Precision) : (DateOnly?)null;
            var summary = draft.Summary?.Trim();

            command.Parameters.AddWithValue("$collection", draft.CollectionID);
            command.Parameters.AddWithValue("$title", draft.Title.Trim());
            command.Parameters.AddWithValue("$start", MemoriesDatabase.FormatLocal(start.ToDateTime(TimeOnly.MinValue)));
            command.Parameters.AddWithValue("$end", end is { } e ? MemoriesDatabase.FormatLocal(e.ToDateTime(TimeOnly.MinValue)) : DBNull.Value);
            command.Parameters.AddWithValue("$precision", PersonalMemoryRules.StoredPrecision(draft.Precision));
            command.Parameters.AddWithValue("$summary", string.IsNullOrEmpty(summary) ? DBNull.Value : summary);
            command.Parameters.AddWithValue("$image", (object?)imageSha ?? DBNull.Value);
            command.Parameters.AddWithValue("$source", draft.Source.Trim());
        }

        /// <summary>Keeps the links the draft still has (same rows), removes the others, adds the new ones in order.</summary>
        private static void ReplaceLinks(SqliteTransaction transaction, long entryID, IReadOnlyList<PersonalLinkTarget> links)
        {
            var existing = new List<(long ID, PersonalLinkTarget Target)>();

            using (var read = Command(transaction,
                       "SELECT EntryLinkID, ToEntryID, ToMessageSequenceNumber FROM EntryLink WHERE FromEntryID = $id ORDER BY EntryLinkID;"))
            {
                read.Parameters.AddWithValue("$id", entryID);
                using var reader = read.ExecuteReader();

                while (reader.Read())
                {
                    existing.Add((reader.GetInt64(0), reader.IsDBNull(1)
                        ? PersonalLinkTarget.ToMessage(reader.GetInt32(2))
                        : PersonalLinkTarget.ToEntry(reader.GetInt64(1))));
                }
            }

            var wanted = links.ToHashSet();
            var kept = new HashSet<PersonalLinkTarget>();

            using var command = Command(transaction, "DELETE FROM EntryLink WHERE EntryLinkID = $link;");

            foreach (var (id, target) in existing)
            {
                if (wanted.Contains(target) && kept.Add(target))
                {
                    continue;
                }

                command.Parameters.Clear();
                command.Parameters.AddWithValue("$link", id);
                command.ExecuteNonQuery();
            }

            command.CommandText =
                "INSERT INTO EntryLink (FromEntryID, ToEntryID, ToMessageSequenceNumber) VALUES ($from, $entry, $message);";

            foreach (var target in links.Where(link => !kept.Contains(link)))
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$from", entryID);
                command.Parameters.AddWithValue("$entry", (object?)target.EntryID ?? DBNull.Value);
                command.Parameters.AddWithValue("$message", (object?)target.MessageSequenceNumber ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Writes the image file (when not already there with these bytes) and its row. Returns the
        /// file's path when this call created it, so a failed save can remove it again.
        /// </summary>
        private string? StoreImage(SqliteTransaction transaction, PersonalImageFile image)
        {
            using (var check = Command(transaction, "SELECT Extension FROM MediaFile WHERE Sha256 = $sha;"))
            {
                check.Parameters.AddWithValue("$sha", image.Sha256);

                if (check.ExecuteScalar() is string recorded && recorded != image.Extension)
                {
                    throw new PersonalMemoryValidationException(
                        PersonalFields.Image, "That image is already recorded with a different type.");
                }
            }

            var target = Path.Combine(MediaFolder, image.Sha256 + "." + image.Extension);
            var existed = File.Exists(target);

            MemoryImageFetcher.Store(MediaFolder, image.Sha256, image.Extension, image.Bytes);

            using var command = Command(transaction,
                "INSERT OR IGNORE INTO MediaFile (Sha256, Extension, SizeBytes) VALUES ($sha, $ext, $size);");
            command.Parameters.AddWithValue("$sha", image.Sha256);
            command.Parameters.AddWithValue("$ext", image.Extension);
            command.Parameters.AddWithValue("$size", image.SizeBytes);
            command.ExecuteNonQuery();

            return existed ? null : target;
        }

        // -----------------------------------------------------------------------------------
        // Checks.
        // -----------------------------------------------------------------------------------

        private const string ImportedReadOnly = "Imported memories are read-only.";

        private const string MissingFile =
            "There is no memories file beside this workspace yet. Nothing has been created.";

        private static string RequireCollectionName(string name)
        {
            if (PersonalMemoryRules.CollectionNameError(name) is { } error)
            {
                throw new PersonalMemoryValidationException(PersonalFields.CollectionName, error);
            }

            return name.Trim();
        }

        private static void RequireUniqueName(SqliteTransaction transaction, string name, long? exceptCollectionID)
        {
            using var command = Command(transaction,
                """
                SELECT COUNT(*) FROM Collection
                WHERE Kind = $kind AND lower(trim(Name)) = lower($name) AND CollectionID IS NOT $except;
                """);
            command.Parameters.AddWithValue("$kind", MemoriesDatabase.KindPersonal);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$except", (object?)exceptCollectionID ?? DBNull.Value);

            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                throw new PersonalMemoryValidationException(
                    PersonalFields.CollectionName, "You already have a collection with that name.");
            }
        }

        private static void RequirePersonalCollection(SqliteTransaction transaction, long collectionID, string field)
        {
            var kind = Kind(transaction, "SELECT Kind FROM Collection WHERE CollectionID = $id;", collectionID);

            if (kind is null)
            {
                throw new PersonalMemoryValidationException(field, "That collection is no longer there.");
            }

            if (kind != MemoriesDatabase.KindPersonal)
            {
                throw new PersonalMemoryValidationException(field, "Imported collections are read-only.");
            }
        }

        private static bool IsPersonalCollection(SqliteTransaction transaction, long collectionID) =>
            Kind(transaction, "SELECT Kind FROM Collection WHERE CollectionID = $id;", collectionID) == MemoriesDatabase.KindPersonal;

        private static void RequirePersonalEntry(SqliteTransaction transaction, long entryID)
        {
            var kind = Kind(transaction,
                "SELECT c.Kind FROM Entry e JOIN Collection c ON c.CollectionID = e.CollectionID WHERE e.EntryID = $id;",
                entryID);

            if (kind is null)
            {
                throw new PersonalMemoryValidationException(PersonalFields.Entry, "That memory is no longer there.");
            }

            if (kind != MemoriesDatabase.KindPersonal)
            {
                throw new PersonalMemoryValidationException(PersonalFields.Entry, ImportedReadOnly);
            }
        }

        private static string? Kind(SqliteTransaction transaction, string sql, long id)
        {
            using var command = Command(transaction, sql);
            command.Parameters.AddWithValue("$id", id);

            return command.ExecuteScalar() as string;
        }

        private static bool EntryExists(SqliteTransaction transaction, long entryID)
        {
            using var command = Command(transaction, "SELECT COUNT(*) FROM Entry WHERE EntryID = $id;");
            command.Parameters.AddWithValue("$id", entryID);

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        }

        private bool MediaRecorded(SqliteTransaction transaction, string sha)
        {
            using var command = Command(transaction, "SELECT Extension FROM MediaFile WHERE Sha256 = $sha;");
            command.Parameters.AddWithValue("$sha", sha);

            return command.ExecuteScalar() is string extension && File.Exists(Path.Combine(MediaFolder, sha + "." + extension));
        }

        // -----------------------------------------------------------------------------------
        // Plumbing.
        // -----------------------------------------------------------------------------------

        private static string Describe(string collection, string title, string precision, DateTime start, DateTime? end) =>
            title + ", " + PersonalMemoryRules.DateText(precision, start, end) + " (" + collection + ")";

        private T Read<T>(Func<SqliteConnection, T> read)
        {
            try
            {
                using var connection = MemoriesDatabase.OpenReadOnlyConnection(MemoriesPath);

                return read(connection);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        private T Write<T>(Func<SqliteTransaction, T> write)
        {
            try
            {
                using var connection = MemoriesDatabase.OpenConnection(MemoriesPath);
                using var transaction = connection.BeginTransaction();

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

        private static DateTime Local(string text) =>
            DateTime.ParseExact(text, ArchiveFormat.LocalDateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None);
    }
}
