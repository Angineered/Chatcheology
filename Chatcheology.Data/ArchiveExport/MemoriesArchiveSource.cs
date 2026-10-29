using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.ArchiveExport
{
    /// <summary>
    /// What an archive export needs from the memories file (CH-42), read on one read-only connection
    /// before anything is written.
    /// </summary>
    /// <remarks>
    /// <para>The file is found beside the main workspace by its fixed name
    /// (<see cref="MemoriesDatabase.FileName"/>); no file means no memories. It must prove it is a
    /// memories file (its marker) or the export is refused.</para>
    /// <para>Read: collections, entries, periods, items and links, with each entry image's hash and
    /// extension, from a format 2 file the item definitions (CH-43) that some item uses, with each
    /// icon's hash and extension (a format 1 file has none), from a format 3 file each personal
    /// entry's extra photos with their captions (CH-46; older files have none), and from a format 4 file the
    /// noted dates as they are (CH-47; older files have none). Not read at all: image and icon source
    /// URLs, definitions no item uses, and nothing naming a source account is in the file to read.
    /// Items carry their role; the combined source maps it onto an archive participant.</para>
    /// </remarks>
    internal sealed class MemoriesArchiveSource
    {
        /// <summary>The first memories file format with <c>ItemDefinition</c> (CH-43); fixed, whatever the current format is.</summary>
        private const int FirstFormatWithDefinitions = 2;

        internal required IReadOnlyList<ArchiveCollection> Collections { get; init; }

        /// <summary>Every entry with its periods and their items; each item carries its role.</summary>
        internal required IReadOnlyList<MemoryEntryRow> Entries { get; init; }

        /// <summary>Every link, as the memories file records it (a message by its workspace sequence number).</summary>
        internal required IReadOnlyList<ArchiveEntryLink> Links { get; init; }

        /// <summary>The item definitions some item uses, ordered by set and item; each icon's extension is the memories file's own.</summary>
        internal required IReadOnlyList<ArchiveItemDefinition> Definitions { get; init; }

        /// <summary>The noted dates (CH-47, format 4; none before), ordered by identifier, as stored.</summary>
        internal IReadOnlyList<ArchiveNotedDate> NotedDates { get; init; } = [];

        /// <summary>Each entry image's and used item icon's file in the memories media folder, by hash.</summary>
        internal required IReadOnlyDictionary<string, MemoryImage> Images { get; init; }

        /// <summary>The memories media folder: the output may not sit inside it.</summary>
        internal required string MediaFolder { get; init; }

        /// <summary>
        /// Reads the file beside <paramref name="workspacePath"/>, or returns <see langword="null"/> when
        /// there is none.
        /// </summary>
        /// <exception cref="ArchiveExportRefusedException">The file is there but unusable. Nothing was written.</exception>
        internal static MemoriesArchiveSource? ReadBeside(string workspacePath, CancellationToken cancellationToken)
        {
            var path = MemoriesDatabase.PathBeside(workspacePath);

            if (!File.Exists(path))
            {
                return null;
            }

            var mediaFolder = MemoriesDatabase.MediaFolderFor(path);

            try
            {
                using var connection = WorkspaceDatabase.OpenReadOnlyConnection(path);

                int formatVersion;

                try
                {
                    formatVersion = MemoriesDatabase.RequireMemoriesFile(connection);
                }
                catch (InvalidOperationException exception)
                {
                    throw new ArchiveExportRefusedException(
                        "The memories file beside the workspace is not a usable memories file. Nothing was written.",
                        exception);
                }

                var hasDefinitions = formatVersion >= FirstFormatWithDefinitions;
                var hasPhotos = formatVersion >= MemoriesDatabase.FirstFormatWithEntryImages;
                var collections = ReadCollections(connection);
                var images = ReadImages(connection, mediaFolder, hasDefinitions, hasPhotos);
                var photos = hasPhotos ? ReadPhotos(connection, images) : [];
                var entries = ReadEntries(connection, images, photos, cancellationToken);
                var links = ReadLinks(connection);
                var definitions = hasDefinitions ? ReadDefinitions(connection, images) : [];
                var notedDates = formatVersion >= MemoriesDatabase.FirstFormatWithNotedDates ? ReadNotedDates(connection) : [];

                return new MemoriesArchiveSource
                {
                    Collections = collections,
                    Entries = entries,
                    Links = links,
                    Definitions = definitions,
                    NotedDates = notedDates,
                    Images = images,
                    MediaFolder = mediaFolder,
                };
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                throw new ArchiveExportRefusedException(
                    "The memories file is busy: another program holds a lock on it. Nothing was written.");
            }
            catch (SqliteException exception)
            {
                throw new ArchiveExportRefusedException(
                    "The memories file beside the workspace could not be read. Nothing was written.", exception);
            }
            catch (FormatException exception)
            {
                throw new ArchiveExportRefusedException(
                    "A time in the memories file is not in its stored form. Nothing was written.", exception);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        private static List<ArchiveCollection> ReadCollections(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT CollectionID, Kind, Name, SourceDescription, ShownByDefault, PeriodGapMinutes
                FROM Collection ORDER BY CollectionID;
                """;

            var collections = new List<ArchiveCollection>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                collections.Add(new ArchiveCollection
                {
                    CollectionID = reader.GetInt64(0),
                    Kind = reader.GetString(1),
                    Name = reader.GetString(2),
                    SourceDescription = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ShownByDefault = reader.GetInt64(4) == 1,
                    PeriodGapMinutes = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                });
            }

            return collections;
        }

        /// <summary>
        /// Every image an entry names, from a format 2 file every icon a used definition names, and from a
        /// format 3 file every extra photo, with its recorded extension and size.
        /// </summary>
        private static Dictionary<string, MemoryImage> ReadImages(
            SqliteConnection connection, string mediaFolder, bool hasDefinitions, bool hasPhotos)
        {
            using var command = connection.CreateCommand();
            command.CommandText = hasDefinitions
                ? $"""
                  SELECT DISTINCT m.Sha256, m.Extension, m.SizeBytes
                  FROM MediaFile AS m
                  WHERE m.Sha256 IN (SELECT ImageSha256 FROM Entry WHERE ImageSha256 IS NOT NULL)
                     OR m.Sha256 IN (SELECT d.IconSha256 FROM ItemDefinition AS d
                                     WHERE d.IconSha256 IS NOT NULL
                                       AND EXISTS (SELECT 1 FROM EntryItem AS i WHERE i.SetId = d.SetId AND i.ItemId = d.ItemId))
                     {(hasPhotos ? "OR m.Sha256 IN (SELECT Sha256 FROM EntryImage)" : string.Empty)}
                  ORDER BY m.Sha256;
                  """
                : """
                  SELECT DISTINCT m.Sha256, m.Extension, m.SizeBytes
                  FROM Entry AS e JOIN MediaFile AS m ON m.Sha256 = e.ImageSha256
                  ORDER BY m.Sha256;
                  """;

            var images = new Dictionary<string, MemoryImage>(StringComparer.Ordinal);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var sha = reader.GetString(0);
                var extension = "." + reader.GetString(1);

                if (!ArchiveFormat.IsSha256(sha) || ArchiveFormat.SafeExtension(extension) != extension)
                {
                    throw new ArchiveExportRefusedException(
                        "An entry image's or item icon's recorded hash or extension is not in its stored form. Nothing was written.");
                }

                images[sha] = new MemoryImage(sha, extension, reader.GetInt64(2), Path.Combine(mediaFolder, sha + extension));
            }

            return images;
        }

        /// <summary>The extra photos of every entry (format 3), by entry, in ordinal order; no file name or path.</summary>
        private static Dictionary<long, List<MemoryPhotoRow>> ReadPhotos(
            SqliteConnection connection, IReadOnlyDictionary<string, MemoryImage> images)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EntryID, Ordinal, Sha256, Caption FROM EntryImage ORDER BY EntryID, Ordinal;";

            var photos = new Dictionary<long, List<MemoryPhotoRow>>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var sha = reader.GetString(2);

                if (!images.ContainsKey(sha))
                {
                    throw new ArchiveExportRefusedException(
                        "An extra photo names an image the memories file does not record. Nothing was written.");
                }

                var entryID = reader.GetInt64(0);

                if (!photos.TryGetValue(entryID, out var list))
                {
                    list = [];
                    photos[entryID] = list;
                }

                list.Add(new MemoryPhotoRow(entryID, reader.GetInt32(1), sha, Nullable(reader, 3)));
            }

            return photos;
        }

        private static List<MemoryEntryRow> ReadEntries(
            SqliteConnection connection,
            IReadOnlyDictionary<string, MemoryImage> images,
            IReadOnlyDictionary<long, List<MemoryPhotoRow>> photos,
            CancellationToken cancellationToken)
        {
            var items = ReadItems(connection);
            var periods = ReadPeriods(connection, items);

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT EntryID, CollectionID, SourceKey, Title, StartLocal, EndLocal, StartUtc, EndUtc, DatePrecision,
                       Summary, ImageSha256, Source, DayLevel
                FROM Entry ORDER BY EntryID;
                """;

            var entries = new List<MemoryEntryRow>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entryID = reader.GetInt64(0);
                var sha = reader.IsDBNull(10) ? null : reader.GetString(10);

                if (sha is not null && !images.ContainsKey(sha))
                {
                    throw new ArchiveExportRefusedException(
                        "An entry names an image the memories file does not record. Nothing was written.");
                }

                entries.Add(new MemoryEntryRow(
                    new ArchiveEntry
                    {
                        EntryID = entryID,
                        CollectionID = reader.GetInt64(1),
                        SourceKey = Nullable(reader, 2),
                        Title = reader.GetString(3),
                        StartLocal = Local(reader.GetString(4)),
                        EndLocal = Nullable(reader, 5) is { } end ? Local(end) : null,
                        StartUtc = Nullable(reader, 6) is { } startUtc ? Utc(startUtc) : null,
                        EndUtc = Nullable(reader, 7) is { } endUtc ? Utc(endUtc) : null,
                        DatePrecision = reader.GetString(8),
                        Summary = Nullable(reader, 9),
                        ImageSha256 = sha,
                        ImageExtension = sha is null ? null : images[sha].Extension,
                        Source = Nullable(reader, 11),
                        DayLevel = Nullable(reader, 12),
                    },
                    periods.GetValueOrDefault(entryID) ?? [])
                {
                    Photos = photos.GetValueOrDefault(entryID) ?? [],
                });
            }

            return entries;
        }

        private static Dictionary<long, List<MemoryPeriodRow>> ReadPeriods(
            SqliteConnection connection, Dictionary<(long, int), List<MemoryItemRow>> items)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT EntryID, PeriodIndex, StartUtc, EndUtc, StartLocal, EndLocal, Label, LocalItemCount, OtherItemCount
                FROM EntryPeriod ORDER BY EntryID, PeriodIndex;
                """;

            var periods = new Dictionary<long, List<MemoryPeriodRow>>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var entryID = reader.GetInt64(0);
                var index = reader.GetInt32(1);

                var period = new MemoryPeriodRow(
                    entryID,
                    index,
                    Utc(reader.GetString(2)),
                    Utc(reader.GetString(3)),
                    Local(reader.GetString(4)),
                    Local(reader.GetString(5)),
                    reader.GetString(6),
                    reader.GetInt32(7),
                    reader.GetInt32(8),
                    items.GetValueOrDefault((entryID, index)) ?? []);

                if (!periods.TryGetValue(entryID, out var list))
                {
                    list = [];
                    periods[entryID] = list;
                }

                list.Add(period);
            }

            return periods;
        }

        private static Dictionary<(long, int), List<MemoryItemRow>> ReadItems(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT EntryItemID, EntryID, PeriodIndex, Role, SetId, ItemId, ItemType, TimeUtc, TimeLocal
                FROM EntryItem ORDER BY EntryID, PeriodIndex, TimeUtc, EntryItemID;
                """;

            var items = new Dictionary<(long, int), List<MemoryItemRow>>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var item = new MemoryItemRow(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    Utc(reader.GetString(7)),
                    Local(reader.GetString(8)));

                var key = (item.EntryID, item.PeriodIndex);

                if (!items.TryGetValue(key, out var list))
                {
                    list = [];
                    items[key] = list;
                }

                list.Add(item);
            }

            return items;
        }

        /// <summary>The definitions some item uses, ordered by set and item. No icon source URL is read.</summary>
        private static List<ArchiveItemDefinition> ReadDefinitions(
            SqliteConnection connection, IReadOnlyDictionary<string, MemoryImage> images)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT d.SetId, d.ItemId, d.ItemType, d.Name, d.Detail, d.Hidden, d.GroupId, d.IconSha256
                FROM ItemDefinition AS d
                WHERE EXISTS (SELECT 1 FROM EntryItem AS i WHERE i.SetId = d.SetId AND i.ItemId = d.ItemId)
                ORDER BY d.SetId, d.ItemId;
                """;

            var definitions = new List<ArchiveItemDefinition>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var sha = Nullable(reader, 7);

                if (sha is not null && !images.ContainsKey(sha))
                {
                    throw new ArchiveExportRefusedException(
                        "An item definition names an icon the memories file does not record. Nothing was written.");
                }

                definitions.Add(new ArchiveItemDefinition
                {
                    SetId = reader.GetString(0),
                    ItemId = reader.GetString(1),
                    ItemType = reader.GetString(2),
                    Name = Nullable(reader, 3),
                    Detail = Nullable(reader, 4),
                    Hidden = reader.IsDBNull(5) ? null : reader.GetInt64(5) == 1,
                    GroupId = Nullable(reader, 6),
                    IconSha256 = sha,
                    IconExtension = sha is null ? null : images[sha].Extension,
                });
            }

            return definitions;
        }

        /// <summary>Every noted date (format 4), ordered by identifier, exactly as stored.</summary>
        private static List<ArchiveNotedDate> ReadNotedDates(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT NotedDateID, Title, Month, Day, Year, Note FROM NotedDate ORDER BY NotedDateID;";

            var dates = new List<ArchiveNotedDate>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                dates.Add(new ArchiveNotedDate
                {
                    NotedDateID = reader.GetInt64(0),
                    Title = reader.GetString(1),
                    Month = reader.GetInt32(2),
                    Day = reader.GetInt32(3),
                    Year = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    Note = Nullable(reader, 5),
                });
            }

            return dates;
        }

        private static List<ArchiveEntryLink> ReadLinks(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT EntryLinkID, FromEntryID, ToEntryID, ToMessageSequenceNumber FROM EntryLink ORDER BY EntryLinkID;";

            var links = new List<ArchiveEntryLink>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                links.Add(new ArchiveEntryLink
                {
                    EntryLinkID = reader.GetInt64(0),
                    FromEntryID = reader.GetInt64(1),
                    ToEntryID = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    ToMessageSequenceNumber = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                });
            }

            return links;
        }

        private static string? Nullable(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

        private static DateTime Local(string text) =>
            DateTime.SpecifyKind(
                DateTime.ParseExact(text, ArchiveFormat.LocalDateTimeFormat, CultureInfo.InvariantCulture),
                DateTimeKind.Unspecified);

        private static DateTime Utc(string text) =>
            DateTime.ParseExact(
                text,
                ArchiveFormat.UtcDateTimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    /// <summary>One entry of the memories file with its periods.</summary>
    internal sealed record MemoryEntryRow(ArchiveEntry Entry, IReadOnlyList<MemoryPeriodRow> Periods)
    {
        /// <summary>Its extra photos after the cover, in ordinal order (format 3; none before).</summary>
        internal IReadOnlyList<MemoryPhotoRow> Photos { get; init; } = [];
    }

    /// <summary>One extra photo of the memories file (CH-46): its hash and caption; the file is in <see cref="MemoriesArchiveSource.Images"/>.</summary>
    internal sealed record MemoryPhotoRow(long EntryID, int Ordinal, string Sha256, string? Caption);


    /// <summary>One period of the memories file with its items.</summary>
    internal sealed record MemoryPeriodRow(
        long EntryID,
        int PeriodIndex,
        DateTime StartUtc,
        DateTime EndUtc,
        DateTime StartLocal,
        DateTime EndLocal,
        string Label,
        int LocalItemCount,
        int OtherItemCount,
        IReadOnlyList<MemoryItemRow> Items);

    /// <summary>One item of the memories file, with its role (<see cref="MemoryRoles"/>).</summary>
    internal sealed record MemoryItemRow(
        long EntryItemID,
        long EntryID,
        int PeriodIndex,
        string Role,
        string SetId,
        string ItemId,
        string ItemType,
        DateTime TimeUtc,
        DateTime TimeLocal);

    /// <summary>One entry image or item icon in the memories media folder.</summary>
    internal sealed record MemoryImage(string Sha256, string Extension, long SizeBytes, string FullPath);
}
