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
                    ParticipantID = reader.GetInt64(0),
                    DisplayName = reader.GetString(1),
                    IsLocal = reader.GetInt64(2) == 1,
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
                days.Add(DateOnly.FromDateTime(ArchiveFormat.ParseLocal(reader.GetString(0) + "T00:00:00")));
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

            var result = command.ExecuteScalar();

            return result is null or DBNull ? null : Convert.ToInt32(result, CultureInfo.InvariantCulture);
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
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    ArchiveFormat.ParseLocal(reader.GetString(2)),
                    ParseKind(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6) == 1,
                    reader.IsDBNull(7) ? null : reader.GetInt32(7)));
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
                    AttachmentID = reader.GetInt64(0),
                    MessageID = reader.GetInt64(1),
                    Ordinal = reader.GetInt32(2),
                    State = ParseState(reader.GetString(3)),
                    MediaSha256 = reader.IsDBNull(4) ? null : reader.GetString(4),
                    OriginalFileName = reader.IsDBNull(5) ? null : reader.GetString(5),
                    IsForwarded = reader.IsDBNull(6) ? null : reader.GetInt64(6) == 1,
                    ForwardScore = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    LowQualityCopyOfAttachmentID = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    Basis = reader.IsDBNull(9)
                        ? null
                        : new ArchiveAttachmentBasis
                        {
                            BasisType = reader.GetString(9),
                            BasisText = reader.GetString(10),
                            Actor = reader.IsDBNull(11) ? null : reader.GetString(11),
                            ConfirmedUtc = ArchiveFormat.ParseUtc(reader.GetString(12)),
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
                    Sha256 = reader.GetString(0),
                    MediaType = reader.GetString(1),
                    SizeBytes = reader.GetInt64(2),
                    DurationMS = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    Width = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    Height = reader.IsDBNull(5) ? null : reader.GetInt32(5),
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
                    values[reader.GetString(0)] = reader.GetString(1);
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
