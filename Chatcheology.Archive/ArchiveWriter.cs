using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Archive
{
    /// <summary>
    /// Writes one new <c>archive.db</c>, once.
    /// </summary>
    /// <remarks>
    /// <para><strong>All or nothing.</strong> Everything is written in one transaction, and
    /// <c>PRAGMA user_version</c> is set only in <see cref="Complete"/>, just before the commit. A
    /// database the writer did not complete therefore reads as version 0, which no reader accepts as
    /// an archive.</para>
    /// <para><strong>The format's rules are checked as rows arrive</strong>, with a fixed message
    /// that names the rule and never a value, and the schema's own constraints check them again.
    /// Cross-row rules (exactly one local participant, no media nobody names, every link pointing at
    /// a row that exists) are checked in <see cref="Complete"/>.</para>
    /// <para>After the commit the file is put in rollback-journal mode and vacuumed, so the archive
    /// is one compact file with no companion files.</para>
    /// </remarks>
    public sealed class ArchiveWriter : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ArchiveInfo _info;
        private SqliteTransaction? _transaction;

        private int _participants;
        private int _messages;
        private int _attachments;
        private int _restored;
        private int _missing;
        private int _unmatched;
        private int _media;
        private bool _completed;

        private ArchiveWriter(SqliteConnection connection, SqliteTransaction transaction, ArchiveInfo info)
        {
            _connection = connection;
            _transaction = transaction;
            _info = info;
        }

        /// <summary>Creates a new archive database at <paramref name="databasePath"/>.</summary>
        /// <exception cref="InvalidOperationException">
        /// A file already exists there, or the info breaks a format rule. Nothing was written.
        /// </exception>
        public static ArchiveWriter Create(string databasePath, ArchiveInfo info)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            ArgumentNullException.ThrowIfNull(info);

            ValidateInfo(info);

            if (File.Exists(databasePath) || Directory.Exists(databasePath))
            {
                throw new InvalidOperationException(
                    "Something already exists where the archive database would go. An archive is " +
                    "only ever written as a new file. Nothing was written.");
            }

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true,
                Pooling = false,
            }.ToString());

            try
            {
                connection.Open();

                Execute(connection, null, "PRAGMA journal_mode = DELETE;");

                var transaction = connection.BeginTransaction();

                foreach (var statement in ArchiveSchema.Statements)
                {
                    Execute(connection, transaction, statement);
                }

                var writer = new ArchiveWriter(connection, transaction, info);
                writer.WriteInfo();

                return writer;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        /// <summary>Writes one participant.</summary>
        /// <exception cref="InvalidOperationException">The row breaks a format rule.</exception>
        public void WriteParticipant(ArchiveParticipant participant)
        {
            ArgumentNullException.ThrowIfNull(participant);
            var transaction = RequireOpen();

            if (participant.IsLocal != (participant.ParticipantID == _info.LocalParticipantID))
            {
                throw new InvalidOperationException(
                    "A participant's IsLocal must be true exactly for the archive's LocalParticipantID.");
            }

            using var command = Command(
                transaction,
                "INSERT INTO Participant (ParticipantID, DisplayName, IsLocal) VALUES ($id, $name, $local);");
            command.Parameters.AddWithValue("$id", participant.ParticipantID);
            command.Parameters.AddWithValue("$name", participant.DisplayName);
            command.Parameters.AddWithValue("$local", participant.IsLocal ? 1 : 0);
            command.ExecuteNonQuery();

            _participants++;
        }

        /// <summary>Writes one distinct media payload. Each restored attachment names one.</summary>
        /// <exception cref="InvalidOperationException">The row breaks a format rule.</exception>
        public void WriteMedia(ArchiveMedia media)
        {
            ArgumentNullException.ThrowIfNull(media);
            var transaction = RequireOpen();

            if (!ArchiveFormat.IsSha256(media.Sha256))
            {
                throw new InvalidOperationException("A media hash must be 64 lower-case hexadecimal digits.");
            }

            using var command = Command(
                transaction,
                """
                INSERT INTO Media (Sha256, MediaType, SizeBytes, DurationMS, Width, Height)
                VALUES ($sha, $type, $size, $duration, $width, $height);
                """);
            command.Parameters.AddWithValue("$sha", media.Sha256);
            command.Parameters.AddWithValue("$type", media.MediaType);
            command.Parameters.AddWithValue("$size", media.SizeBytes);
            command.Parameters.AddWithValue("$duration", (object?)media.DurationMS ?? DBNull.Value);
            command.Parameters.AddWithValue("$width", (object?)media.Width ?? DBNull.Value);
            command.Parameters.AddWithValue("$height", (object?)media.Height ?? DBNull.Value);
            command.ExecuteNonQuery();

            _media++;
        }

        /// <summary>Writes one message, its attachments and their basis lines.</summary>
        /// <exception cref="InvalidOperationException">A row breaks a format rule.</exception>
        public void WriteMessage(ArchiveMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            var transaction = RequireOpen();

            if ((message.Kind == ArchiveMessageKind.User) != message.SenderParticipantID.HasValue)
            {
                throw new InvalidOperationException(
                    "A user message must have a sender and a system message must have none.");
            }

            if (message.DateTimeLocal.Kind == DateTimeKind.Utc)
            {
                throw new InvalidOperationException(
                    "A message time is a local wall-clock reading; a UTC time would be stored as the " +
                    "wrong reading.");
            }

            using (var command = Command(
                       transaction,
                       """
                       INSERT INTO Message (
                           MessageID, SequenceNumber, DateTimeLocal, Kind, SenderParticipantID, Text,
                           IsForwarded, ForwardScore)
                       VALUES ($id, $sequence, $time, $kind, $sender, $text, $forwarded, $score);
                       """))
            {
                command.Parameters.AddWithValue("$id", message.MessageID);
                command.Parameters.AddWithValue("$sequence", message.SequenceNumber);
                command.Parameters.AddWithValue("$time", ArchiveFormat.FormatLocal(message.DateTimeLocal));
                command.Parameters.AddWithValue("$kind", FormatKind(message.Kind));
                command.Parameters.AddWithValue("$sender", (object?)message.SenderParticipantID ?? DBNull.Value);
                command.Parameters.AddWithValue("$text", message.Text);
                command.Parameters.AddWithValue("$forwarded", Flag(message.IsForwarded));
                command.Parameters.AddWithValue("$score", (object?)message.ForwardScore ?? DBNull.Value);
                command.ExecuteNonQuery();
            }

            foreach (var attachment in message.Attachments)
            {
                WriteAttachment(transaction, message.MessageID, attachment);
            }

            _messages++;
        }

        /// <summary>
        /// Checks the cross-row rules, stamps the format version, commits, and compacts the file.
        /// </summary>
        /// <returns>What was written.</returns>
        /// <exception cref="InvalidOperationException">
        /// A cross-row rule is broken. Nothing was committed.
        /// </exception>
        public ArchiveCounts Complete()
        {
            var transaction = RequireOpen();

            RequireCount(
                transaction,
                "SELECT COUNT(*) FROM Participant WHERE IsLocal = 1;",
                1,
                "The archive must have exactly one local participant, and it must be a participant of " +
                "the conversation.");

            RequireCount(
                transaction,
                "SELECT COUNT(*) FROM Media WHERE Sha256 NOT IN (SELECT MediaSha256 FROM Attachment " +
                "WHERE MediaSha256 IS NOT NULL);",
                0,
                "Every media payload must be named by at least one restored attachment.");

            RequireCount(
                transaction,
                "SELECT COUNT(*) FROM Attachment AS a LEFT JOIN AttachmentBasis AS b " +
                "ON b.AttachmentID = a.AttachmentID WHERE (a.State = 'Restored') <> (b.AttachmentID IS NOT NULL);",
                0,
                "Every restored attachment, and only a restored attachment, must have one basis line.");

            RequireCount(transaction, "SELECT COUNT(*) FROM pragma_foreign_key_check;", 0,
                "A message, attachment or basis line refers to a row the archive does not hold.");

            Execute(
                _connection,
                transaction,
                $"PRAGMA user_version = {ArchiveFormat.FormatVersion.ToString(CultureInfo.InvariantCulture)};");

            transaction.Commit();
            transaction.Dispose();
            _transaction = null;
            _completed = true;

            foreach (var statement in CompactionStatements)
            {
                Execute(_connection, null, statement);
            }

            _connection.Close();

            return new ArchiveCounts(_participants, _messages, _attachments, _restored, _missing, _unmatched, _media);
        }

        /// <summary>
        /// Closes the database. An archive not completed is rolled back and left at version 0; the
        /// caller removes the file.
        /// </summary>
        public void Dispose()
        {
            if (_transaction is not null)
            {
                try
                {
                    _transaction.Rollback();
                }
                catch (SqliteException)
                {
                    // Already rolled back by SQLite itself; the file is version 0 either way.
                }

                _transaction.Dispose();
                _transaction = null;
            }

            _connection.Dispose();
        }

        // -------------------------------------------------------------------------------------
        // Rows.
        // -------------------------------------------------------------------------------------

        private void WriteInfo()
        {
            var values = new Dictionary<string, string>
            {
                [ArchiveInfo.Keys.FormatVersion] = _info.FormatVersion.ToString(CultureInfo.InvariantCulture),
                [ArchiveInfo.Keys.CreatedUtc] = ArchiveFormat.FormatUtc(_info.CreatedUtc),
                [ArchiveInfo.Keys.ProducerVersion] = _info.ProducerVersion,
                [ArchiveInfo.Keys.ConversationTitle] = _info.ConversationTitle,
                [ArchiveInfo.Keys.SourceExportSha256] = _info.SourceExportSha256,
                [ArchiveInfo.Keys.TimeZoneID] = _info.TimeZoneID,
                [ArchiveInfo.Keys.LocalParticipantID] = _info.LocalParticipantID.ToString(CultureInfo.InvariantCulture),
            };

            foreach (var key in ArchiveInfo.Keys.All)
            {
                using var command = Command(
                    _transaction!, "INSERT INTO ArchiveInfo (Key, Value) VALUES ($key, $value);");
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$value", values[key]);
                command.ExecuteNonQuery();
            }
        }

        private void WriteAttachment(SqliteTransaction transaction, long messageID, ArchiveAttachment attachment)
        {
            if (attachment.MessageID != messageID)
            {
                throw new InvalidOperationException("An attachment must be written with the message that carries it.");
            }

            var restored = attachment.State == ArchiveAttachmentState.Restored;

            if (restored != (attachment.MediaSha256 is not null)
                || restored != (attachment.OriginalFileName is not null)
                || restored != (attachment.Basis is not null))
            {
                throw new InvalidOperationException(
                    "A restored attachment must have a media hash, an original file name and a basis, " +
                    "and any other attachment must have none of them.");
            }

            if (restored)
            {
                if (!ArchiveFormat.IsSha256(attachment.MediaSha256))
                {
                    throw new InvalidOperationException("A media hash must be 64 lower-case hexadecimal digits.");
                }

                if (attachment.OriginalFileName!.Length == 0
                    || attachment.OriginalFileName.IndexOfAny(['/', '\\', ':']) >= 0
                    || attachment.OriginalFileName.Any(char.IsControl))
                {
                    throw new InvalidOperationException(
                        "An original file name must be a bare, non-empty file name with no separator, colon " +
                        "or control character.");
                }
            }

            using (var command = Command(
                       transaction,
                       """
                       INSERT INTO Attachment (
                           AttachmentID, MessageID, Ordinal, State, MediaSha256, OriginalFileName,
                           IsForwarded, ForwardScore, LowQualityCopyOfAttachmentID)
                       VALUES ($id, $message, $ordinal, $state, $sha, $name, $forwarded, $score, $copyOf);
                       """))
            {
                command.Parameters.AddWithValue("$id", attachment.AttachmentID);
                command.Parameters.AddWithValue("$message", messageID);
                command.Parameters.AddWithValue("$ordinal", attachment.Ordinal);
                command.Parameters.AddWithValue("$state", FormatState(attachment.State));
                command.Parameters.AddWithValue("$sha", (object?)attachment.MediaSha256 ?? DBNull.Value);
                command.Parameters.AddWithValue("$name", (object?)attachment.OriginalFileName ?? DBNull.Value);
                command.Parameters.AddWithValue("$forwarded", Flag(attachment.IsForwarded));
                command.Parameters.AddWithValue("$score", (object?)attachment.ForwardScore ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "$copyOf", (object?)attachment.LowQualityCopyOfAttachmentID ?? DBNull.Value);
                command.ExecuteNonQuery();
            }

            if (attachment.Basis is { } basis)
            {
                using var command = Command(
                    transaction,
                    """
                    INSERT INTO AttachmentBasis (AttachmentID, BasisType, BasisText, Actor, ConfirmedUtc)
                    VALUES ($id, $type, $text, $actor, $confirmed);
                    """);
                command.Parameters.AddWithValue("$id", attachment.AttachmentID);
                command.Parameters.AddWithValue("$type", basis.BasisType);
                command.Parameters.AddWithValue("$text", basis.BasisText);
                command.Parameters.AddWithValue("$actor", (object?)basis.Actor ?? DBNull.Value);
                command.Parameters.AddWithValue("$confirmed", ArchiveFormat.FormatUtc(basis.ConfirmedUtc));
                command.ExecuteNonQuery();
            }

            _attachments++;

            switch (attachment.State)
            {
                case ArchiveAttachmentState.Restored: _restored++; break;
                case ArchiveAttachmentState.Missing: _missing++; break;
                default: _unmatched++; break;
            }
        }

        // -------------------------------------------------------------------------------------
        // Vocabulary and plumbing.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// Run after the commit, in this order. VACUUM builds a temporary copy of the whole database;
        /// with <c>temp_store = MEMORY</c> that copy stays in memory instead of going to the system
        /// temporary folder, so no copy of the archive is ever written outside the output folder.
        /// </summary>
        internal static IReadOnlyList<string> CompactionStatements { get; } =
        [
            "PRAGMA temp_store = MEMORY;",
            "VACUUM;",
        ];

        internal static string FormatKind(ArchiveMessageKind kind) => kind switch
        {
            ArchiveMessageKind.User => "User",
            ArchiveMessageKind.System => "System",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a message kind of the format."),
        };

        internal static string FormatState(ArchiveAttachmentState state) => state switch
        {
            ArchiveAttachmentState.Restored => "Restored",
            ArchiveAttachmentState.Missing => "Missing",
            ArchiveAttachmentState.Unmatched => "Unmatched",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not an attachment state of the format."),
        };

        private static void ValidateInfo(ArchiveInfo info)
        {
            if (info.FormatVersion != ArchiveFormat.FormatVersion)
            {
                throw new InvalidOperationException(
                    "This build writes only format version " +
                    ArchiveFormat.FormatVersion.ToString(CultureInfo.InvariantCulture) + ".");
            }

            if (info.SourceExportSha256.Length != 0 && !ArchiveFormat.IsSha256(info.SourceExportSha256))
            {
                throw new InvalidOperationException(
                    "SourceExportSha256 must be empty or 64 lower-case hexadecimal digits.");
            }

            // Checked here so a bad value is refused before anything is created.
            ArchiveFormat.FormatUtc(info.CreatedUtc);
        }

        private static object Flag(bool? value) => value is { } flag ? (flag ? 1 : 0) : DBNull.Value;

        private SqliteTransaction RequireOpen() =>
            _transaction
            ?? throw new InvalidOperationException(
                _completed
                    ? "The archive is complete; nothing more can be written to it."
                    : "The archive writer is closed.");

        private SqliteCommand Command(SqliteTransaction transaction, string sql)
        {
            var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;

            return command;
        }

        private void RequireCount(SqliteTransaction transaction, string sql, long expected, string rule)
        {
            using var command = Command(transaction, sql);

            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != expected)
            {
                throw new InvalidOperationException(rule + " Nothing was committed.");
            }
        }

        private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>How many rows of each kind an archive holds.</summary>
    /// <param name="Participants">Participant rows.</param>
    /// <param name="Messages">Message rows.</param>
    /// <param name="Attachments">Attachment rows, of any state.</param>
    /// <param name="Restored">Attachments in the Restored state.</param>
    /// <param name="Missing">Attachments in the Missing state.</param>
    /// <param name="Unmatched">Attachments in the Unmatched state.</param>
    /// <param name="MediaFiles">Distinct media payloads, one file each.</param>
    public sealed record ArchiveCounts(
        int Participants, int Messages, int Attachments, int Restored, int Missing, int Unmatched, int MediaFiles);
}
