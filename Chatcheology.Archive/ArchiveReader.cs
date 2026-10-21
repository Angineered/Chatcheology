using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Archive
{
    /// <summary>
    /// Opens an archive folder read-only and reads its conversation page by page.
    /// </summary>
    /// <remarks>
    /// <para><strong>Read-only.</strong> The database is opened with SQLite's read-only mode and
    /// without connection pooling, so nothing is written and no handle outlives
    /// <see cref="Dispose"/>. Every statement is a <c>SELECT</c>.</para>
    /// <para><strong>It refuses what it does not know.</strong> An archive of a higher format
    /// version than <see cref="ArchiveFormat.FormatVersion"/>, an incomplete one (version 0), or a
    /// database missing any of the format's tables is refused with
    /// <see cref="ArchiveFormatException"/>.</para>
    /// <para><strong>Order comes from <c>SequenceNumber</c>.</strong> Pages are keyset pages on that
    /// key, so a page costs the same anywhere in a long conversation. A day is found through the
    /// <c>DateTimeLocal</c> index.</para>
    /// <para><strong>Every value is checked for its kind as it is read.</strong> SQLite lets any
    /// column of a plain table hold any kind of value, so a damaged or hand-edited archive could put
    /// text where a number belongs or a number too large for its field. Such a value is refused with
    /// <see cref="ArchiveFormatException"/>, never converted, truncated or thrown as a cast or
    /// overflow error.</para>
    /// <para>This reader opens archive files only; it does not check them against
    /// <see cref="ArchiveFormat.ContentsFileName"/>. <see cref="ArchiveContents.Check"/> does that.</para>
    /// </remarks>
    public sealed class ArchiveReader : IDisposable
    {
        /// <summary>The largest page any caller may request.</summary>
        public const int MaximumPageSize = 500;

        private readonly SqliteConnection _connection;

        private ArchiveReader(string folderPath, SqliteConnection connection, ArchiveInfo info)
        {
            FolderPath = folderPath;
            _connection = connection;
            Info = info;
        }

        /// <summary>The archive folder, as a full path.</summary>
        public string FolderPath { get; }

        /// <summary>What the archive says about itself.</summary>
        public ArchiveInfo Info { get; }

        /// <summary>Opens the archive in <paramref name="archiveFolder"/>.</summary>
        /// <exception cref="ArchiveFormatException">
        /// There is no archive there, or it is incomplete, damaged or of a newer format version.
        /// </exception>
        public static ArchiveReader Open(string archiveFolder)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(archiveFolder);

            string folder;

            try
            {
                folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(archiveFolder));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ArchiveFormatException("The archive folder path is not a usable path.");
            }

            var databasePath = Path.Combine(folder, ArchiveFormat.DatabaseFileName);

            if (!File.Exists(databasePath))
            {
                throw new ArchiveFormatException("There is no archive database in the archive folder.");
            }

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());

            try
            {
                connection.Open();

                var version = Convert.ToInt32(
                    Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);

                if (version == 0)
                {
                    throw new ArchiveFormatException(
                        "The archive database is not a complete archive: it has no format version.");
                }

                if (version > ArchiveFormat.FormatVersion)
                {
                    throw new ArchiveFormatException(
                        $"The archive is format version {version.ToString(CultureInfo.InvariantCulture)}; " +
                        $"this reader knows versions up to {ArchiveFormat.FormatVersion.ToString(CultureInfo.InvariantCulture)}. " +
                        "Use a newer viewer.");
                }

                RequireTables(connection);

                var info = ReadInfo(connection);

                if (info.FormatVersion != version)
                {
                    throw new ArchiveFormatException(
                        "The archive's recorded format version does not match its database version.");
                }

                return new ArchiveReader(folder, connection, info);
            }
            catch (SqliteException)
            {
                connection.Dispose();
                throw new ArchiveFormatException("The archive database could not be read as an SQLite database.");
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        /// <summary>Every participant, ordered by identifier.</summary>
        public IReadOnlyList<ArchiveParticipant> ReadParticipants()
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT ParticipantID, DisplayName, IsLocal FROM Participant ORDER BY ParticipantID;";

            var participants = new List<ArchiveParticipant>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                participants.Add(new ArchiveParticipant
                {
                    ParticipantID = Integer(reader, 0),
                    DisplayName = Text(reader, 1),
                    IsLocal = Integer(reader, 2) == 1,
                });
            }

            return participants;
        }

        /// <summary>How many messages the archive holds.</summary>
        public int ReadMessageCount() =>
            Convert.ToInt32(Scalar(_connection, "SELECT COUNT(*) FROM Message;"), CultureInfo.InvariantCulture);

        /// <summary>The first page of the conversation.</summary>
        public ArchivePage ReadFirstPage(int pageSize) => ReadPageFrom(1, pageSize);

        /// <summary>
        /// The page beginning at <paramref name="startSequenceNumber"/>, or at the first message
        /// after it.
        /// </summary>
        public ArchivePage ReadPageFrom(int startSequenceNumber, int pageSize)
        {
            ValidatePageSize(pageSize);

            using var command = _connection.CreateCommand();
            command.CommandText =
                $"""
                {MessageColumns}
                WHERE SequenceNumber >= $start
                ORDER BY SequenceNumber
                LIMIT $pageSize;
                """;
            command.Parameters.AddWithValue("$start", startSequenceNumber);
            command.Parameters.AddWithValue("$pageSize", pageSize);

            return BuildPage(ReadMessageRows(command));
        }

        /// <summary>
        /// The page of messages immediately before <paramref name="beforeSequenceNumber"/>,
        /// returned ascending like every other page.
        /// </summary>
        public ArchivePage ReadPageBefore(int beforeSequenceNumber, int pageSize)
        {
            ValidatePageSize(pageSize);

            using var command = _connection.CreateCommand();
            command.CommandText =
                $"""
                {MessageColumns}
                WHERE SequenceNumber < $before
                ORDER BY SequenceNumber DESC
                LIMIT $pageSize;
                """;
            command.Parameters.AddWithValue("$before", beforeSequenceNumber);
            command.Parameters.AddWithValue("$pageSize", pageSize);

            var rows = ReadMessageRows(command);
            rows.Reverse();

            return BuildPage(rows);
        }

        /// <summary>
        /// The whole conversation, page by page in chat order, for a caller that must see every
        /// message (a check, a comparison, a full export).
        /// </summary>
        /// <remarks>
        /// Each page is read only when the previous one has been used. The pages must add up to the
        /// table: sequence numbers must rise strictly from page to page, and exactly as many messages
        /// must be read as the table holds, so a message with a sequence number below 1 is not
        /// silently left out and a damaged order can never page forever.
        /// </remarks>
        /// <exception cref="ArchiveFormatException">The messages cannot all be read in order.</exception>
        public IEnumerable<ArchivePage> ReadAllPages()
        {
            var total = ReadMessageCount();
            var read = 0;
            int? last = null;

            var page = ReadFirstPage(MaximumPageSize);

            while (true)
            {
                foreach (var message in page.Messages)
                {
                    if (message.SequenceNumber <= last || ++read > total)
                    {
                        throw new ArchiveFormatException(
                            "The archive's messages are not in a strictly rising sequence order.");
                    }

                    last = message.SequenceNumber;
                }

                yield return page;

                if (!page.HasLater || page.LastSequenceNumber is not { } end || end == int.MaxValue)
                {
                    break;
                }

                page = ReadPageFrom(end + 1, MaximumPageSize);
            }

            if (read != total)
            {
                throw new ArchiveFormatException(
                    "Some of the archive's messages have a sequence number below 1 and cannot be read in order.");
            }
        }

        /// <summary>
        /// Every message whose local wall-clock time falls on <paramref name="day"/>, in chat order.
        /// </summary>
        public ArchivePage ReadDay(DateOnly day)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                $"""
                {MessageColumns}
                WHERE DateTimeLocal >= $from AND DateTimeLocal < $to
                ORDER BY SequenceNumber;
                """;
            command.Parameters.AddWithValue("$from", DayStart(day));
            command.Parameters.AddWithValue("$to", DayStart(day.AddDays(1)));

            return BuildPage(ReadMessageRows(command));
        }

        /// <summary>Every day that has at least one message, ascending.</summary>
        public IReadOnlyList<DateOnly> ReadDays()
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT DISTINCT substr(DateTimeLocal, 1, 10) FROM Message ORDER BY 1;";

            var days = new List<DateOnly>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                days.Add(DateOnly.FromDateTime(ArchiveFormat.ParseLocal(Text(reader, 0) + "T00:00:00")));
            }

            return days;
        }

        /// <summary>
        /// The sequence number of the first message on or after <paramref name="day"/>, or
        /// <see langword="null"/> when none is.
        /// </summary>
        /// <remarks>
        /// The smallest sequence number among the qualifying messages, not the first one in time:
        /// nothing here assumes the stored times rise with the chat order.
        /// </remarks>
        public int? FindFirstSequenceNumberOnOrAfter(DateOnly day)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT MIN(SequenceNumber) FROM Message WHERE DateTimeLocal >= $from;";
            command.Parameters.AddWithValue("$from", DayStart(day));

            return command.ExecuteScalar() switch
            {
                null or DBNull => null,
                long value and >= int.MinValue and <= int.MaxValue => (int)value,
                _ => throw new ArchiveFormatException(NotAWholeNumber),
            };
        }

        /// <summary>
        /// The full path of a restored attachment's media file, or <see langword="null"/> when the
        /// attachment is not restored. Composing the path is not a claim the file is there.
        /// </summary>
        public string? MediaFullPath(ArchiveAttachment attachment)
        {
            ArgumentNullException.ThrowIfNull(attachment);

            string? relative;

            try
            {
                relative = attachment.MediaRelativePath;
            }
            catch (ArgumentException)
            {
                throw new ArchiveFormatException(
                    "The attachment names a media hash that is not 64 lower-case hex digits.");
            }

            return relative is not null
                ? Path.Combine(FolderPath, relative.Replace('/', Path.DirectorySeparatorChar))
                : null;
        }

        /// <summary>Closes the database.</summary>
        public void Dispose() => _connection.Dispose();

        // -------------------------------------------------------------------------------------
        // Whole-table questions for ArchiveValidator, which a viewer never asks. Fixed SELECTs only.
        // -------------------------------------------------------------------------------------

        /// <summary>The first line of <c>PRAGMA integrity_check</c>: <c>ok</c> for a sound file.</summary>
        internal string ReadIntegrityCheck() =>
            Convert.ToString(Scalar(_connection, "PRAGMA integrity_check;"), CultureInfo.InvariantCulture) ?? "";

        /// <summary>The one whole number a fixed <c>SELECT</c> returns.</summary>
        internal long ReadCount(string sql) =>
            Convert.ToInt64(Scalar(_connection, sql), CultureInfo.InvariantCulture);

        /// <summary>The first column of every row a fixed <c>SELECT</c> returns, as identifiers.</summary>
        internal IReadOnlyList<long> ReadIdentifiers(string sql)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;

            var values = new List<long>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                values.Add(reader.GetInt64(0));
            }

            return values;
        }

        // -------------------------------------------------------------------------------------
        // Reading.
        // -------------------------------------------------------------------------------------

        private const string MessageColumns =
            """
            SELECT MessageID, SequenceNumber, DateTimeLocal, Kind, SenderParticipantID, Text,
                   IsForwarded, ForwardScore
            FROM Message
            """;

        private sealed record MessageRow(
            long MessageID,
            int SequenceNumber,
            DateTime DateTimeLocal,
            ArchiveMessageKind Kind,
            long? SenderParticipantID,
            string Text,
            bool? IsForwarded,
            int? ForwardScore);

        private static List<MessageRow> ReadMessageRows(SqliteCommand command)
        {
            var rows = new List<MessageRow>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                rows.Add(new MessageRow(
                    Integer(reader, 0),
                    Int32(reader, 1),
                    ArchiveFormat.ParseLocal(Text(reader, 2)),
                    ParseKind(Text(reader, 3)),
                    NullableInteger(reader, 4),
                    Text(reader, 5),
                    NullableInteger(reader, 6) is { } forwarded ? forwarded == 1 : null,
                    NullableInt32(reader, 7)));
            }

            return rows;
        }

        private ArchivePage BuildPage(List<MessageRow> rows)
        {
            if (rows.Count == 0)
            {
                return new ArchivePage
                {
                    Messages = [],
                    Media = new Dictionary<string, ArchiveMedia>(),
                    FirstSequenceNumber = null,
                    LastSequenceNumber = null,
                    HasEarlier = false,
                    HasLater = false,
                };
            }

            var messageIDs = rows.Select(row => row.MessageID).ToList();
            var attachments = ReadAttachments(messageIDs);

            var shas = attachments.Values
                .SelectMany(list => list)
                .Select(attachment => attachment.MediaSha256)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var first = rows[0].SequenceNumber;
            var last = rows[^1].SequenceNumber;

            return new ArchivePage
            {
                Messages = rows.Select(row => new ArchiveMessage
                {
                    MessageID = row.MessageID,
                    SequenceNumber = row.SequenceNumber,
                    DateTimeLocal = row.DateTimeLocal,
                    Kind = row.Kind,
                    SenderParticipantID = row.SenderParticipantID,
                    Text = row.Text,
                    IsForwarded = row.IsForwarded,
                    ForwardScore = row.ForwardScore,
                    Attachments = attachments.TryGetValue(row.MessageID, out var carried) ? carried : [],
                }).ToList(),
                Media = ReadMedia(shas),
                FirstSequenceNumber = first,
                LastSequenceNumber = last,
                HasEarlier = Exists("SELECT EXISTS (SELECT 1 FROM Message WHERE SequenceNumber < $s);", first),
                HasLater = Exists("SELECT EXISTS (SELECT 1 FROM Message WHERE SequenceNumber > $s);", last),
            };
        }

        /// <remarks>
        /// The identifiers travel as one JSON array parameter, so a page costs one statement
        /// whatever it holds and never meets SQLite's parameter limit.
        /// </remarks>
        private Dictionary<long, List<ArchiveAttachment>> ReadAttachments(IReadOnlyList<long> messageIDs)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT a.AttachmentID, a.MessageID, a.Ordinal, a.State, a.MediaSha256, a.OriginalFileName,
                       a.IsForwarded, a.ForwardScore, a.LowQualityCopyOfAttachmentID,
                       b.BasisType, b.BasisText, b.Actor, b.ConfirmedUtc
                FROM Attachment AS a
                LEFT JOIN AttachmentBasis AS b ON b.AttachmentID = a.AttachmentID
                WHERE a.MessageID IN (SELECT value FROM json_each($ids))
                ORDER BY a.MessageID, a.Ordinal, a.AttachmentID;
                """;
            command.Parameters.AddWithValue("$ids", Json(messageIDs));

            var byMessage = new Dictionary<long, List<ArchiveAttachment>>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var attachment = new ArchiveAttachment
                {
                    AttachmentID = Integer(reader, 0),
                    MessageID = Integer(reader, 1),
                    Ordinal = Int32(reader, 2),
                    State = ParseState(Text(reader, 3)),
                    MediaSha256 = NullableText(reader, 4),
                    OriginalFileName = NullableText(reader, 5),
                    IsForwarded = NullableInteger(reader, 6) is { } forwarded ? forwarded == 1 : null,
                    ForwardScore = NullableInt32(reader, 7),
                    LowQualityCopyOfAttachmentID = NullableInteger(reader, 8),
                    Basis = reader.IsDBNull(9)
                        ? null
                        : new ArchiveAttachmentBasis
                        {
                            BasisType = Text(reader, 9),
                            BasisText = Text(reader, 10),
                            Actor = NullableText(reader, 11),
                            ConfirmedUtc = ArchiveFormat.ParseUtc(Text(reader, 12)),
                        },
                };

                // A hash the format cannot name a file by is damage, not an attachment to show.
                if (attachment.MediaSha256 is { } sha && !ArchiveFormat.IsSha256(sha))
                {
                    throw new ArchiveFormatException(
                        "An attachment in the archive names a media hash that is not 64 lower-case hex digits.");
                }

                if (!byMessage.TryGetValue(attachment.MessageID, out var list))
                {
                    list = [];
                    byMessage[attachment.MessageID] = list;
                }

                list.Add(attachment);
            }

            return byMessage;
        }

        private Dictionary<string, ArchiveMedia> ReadMedia(IReadOnlyList<string> shas)
        {
            var media = new Dictionary<string, ArchiveMedia>(StringComparer.Ordinal);

            if (shas.Count == 0)
            {
                return media;
            }

            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT Sha256, MediaType, SizeBytes, DurationMS, Width, Height
                FROM Media
                WHERE Sha256 IN (SELECT value FROM json_each($shas))
                ORDER BY Sha256;
                """;
            command.Parameters.AddWithValue("$shas", Json(shas));

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var item = new ArchiveMedia
                {
                    Sha256 = Text(reader, 0),
                    MediaType = Text(reader, 1),
                    SizeBytes = Integer(reader, 2),
                    DurationMS = NullableInteger(reader, 3),
                    Width = NullableInt32(reader, 4),
                    Height = NullableInt32(reader, 5),
                };

                media[item.Sha256] = item;
            }

            return media;
        }

        private static ArchiveInfo ReadInfo(SqliteConnection connection)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Key, Value FROM ArchiveInfo;";

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    values[Text(reader, 0)] = Text(reader, 1);
                }
            }

            string Value(string key) =>
                values.TryGetValue(key, out var value)
                    ? value
                    : throw new ArchiveFormatException("The archive's info table is missing a required key.");

            return new ArchiveInfo
            {
                FormatVersion = int.TryParse(
                    Value(ArchiveInfo.Keys.FormatVersion), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                    ? version
                    : throw new ArchiveFormatException("The archive's recorded format version is not a number."),
                CreatedUtc = ArchiveFormat.ParseUtc(Value(ArchiveInfo.Keys.CreatedUtc)),
                ProducerVersion = Value(ArchiveInfo.Keys.ProducerVersion),
                ConversationTitle = Value(ArchiveInfo.Keys.ConversationTitle),
                SourceExportSha256 = Value(ArchiveInfo.Keys.SourceExportSha256),
                TimeZoneID = Value(ArchiveInfo.Keys.TimeZoneID),
                LocalParticipantID = long.TryParse(
                    Value(ArchiveInfo.Keys.LocalParticipantID), NumberStyles.None, CultureInfo.InvariantCulture, out var local)
                    ? local
                    : throw new ArchiveFormatException("The archive's local participant is not a number."),
            };
        }

        private static void RequireTables(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";

            var tables = new HashSet<string>(StringComparer.Ordinal);

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            if (!ArchiveFormat.TableNames.All(tables.Contains))
            {
                throw new ArchiveFormatException("The archive database is missing one of the format's tables.");
            }
        }

        // -------------------------------------------------------------------------------------
        // Plumbing.
        // -------------------------------------------------------------------------------------

        private const string NotAWholeNumber =
            "A value in the archive that must be a whole number in range is not one.";

        private const string NotText = "A value in the archive that must be text is not text.";

        /// <summary>A stored whole number. Text, a fraction, a blob or a null is refused.</summary>
        private static long Integer(SqliteDataReader reader, int ordinal) =>
            reader.GetValue(ordinal) is long value ? value : throw new ArchiveFormatException(NotAWholeNumber);

        private static long? NullableInteger(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : Integer(reader, ordinal);

        /// <summary>A stored whole number that must fit a 32-bit field.</summary>
        private static int Int32(SqliteDataReader reader, int ordinal) =>
            Integer(reader, ordinal) is var value and >= int.MinValue and <= int.MaxValue
                ? (int)value
                : throw new ArchiveFormatException(NotAWholeNumber);

        private static int? NullableInt32(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : Int32(reader, ordinal);

        /// <summary>Stored text. A number, a blob or a null is refused.</summary>
        private static string Text(SqliteDataReader reader, int ordinal) =>
            reader.GetValue(ordinal) is string value ? value : throw new ArchiveFormatException(NotText);

        private static string? NullableText(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : Text(reader, ordinal);

        private static ArchiveMessageKind ParseKind(string text) => text switch
        {
            "User" => ArchiveMessageKind.User,
            "System" => ArchiveMessageKind.System,
            _ => throw new ArchiveFormatException("A message in the archive has a kind the format does not define."),
        };

        private static ArchiveAttachmentState ParseState(string text) => text switch
        {
            "Restored" => ArchiveAttachmentState.Restored,
            "Missing" => ArchiveAttachmentState.Missing,
            "Unmatched" => ArchiveAttachmentState.Unmatched,
            _ => throw new ArchiveFormatException("An attachment in the archive has a state the format does not define."),
        };

        private static string DayStart(DateOnly day) =>
            ArchiveFormat.FormatLocal(day.ToDateTime(TimeOnly.MinValue));

        private static void ValidatePageSize(int pageSize)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaximumPageSize);
        }

        private bool Exists(string sql, int sequenceNumber)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$s", sequenceNumber);

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }

        private static object? Scalar(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            return command.ExecuteScalar();
        }

        private static string Json(IEnumerable<long> values) =>
            "[" + string.Join(",", values.Select(value => value.ToString(CultureInfo.InvariantCulture))) + "]";

        private static string Json(IEnumerable<string> values)
        {
            // The values are validated hex hashes, but they are still quoted properly rather than trusted.
            var builder = new StringBuilder("[");
            var first = true;

            foreach (var value in values)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                builder.Append(System.Text.Json.JsonSerializer.Serialize(value));
                first = false;
            }

            return builder.Append(']').ToString();
        }
    }
}
