using System.Globalization;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.NewerMessages
{
    /// <summary>
    /// Appends one refresh run to the newer-messages file, in one transaction (CH-71 step 3, D2-D4).
    /// </summary>
    /// <remarks>
    /// <para><strong>Append only.</strong> Inserts one <c>RefreshRun</c> row, its messages in the order given
    /// (which becomes the timeline order) and their attachments; a media root is registered once and reused.
    /// The only update is in the same transaction: this run's own new attachment rows get their low-quality link
    /// after all of them are inserted. No row of an earlier run is updated and nothing is deleted, so identifiers
    /// and states never change once a run has committed.</para>
    /// <para><strong>Identity.</strong> A message is (from_me, key_id), as in WhatsApp; the same key_id may occur once
    /// in each direction.</para>
    /// <para><strong>Hand-off.</strong> A <see cref="NewerMessagesDatabase.HandOffWorkspace"/> run is accepted only
    /// while nothing has been imported yet; a <see cref="NewerMessagesDatabase.HandOffPreviousRun"/> run only when its
    /// hand-off (from_me, key_id) is the file's last imported message. The hand-off itself is checked
    /// against the msgstore by the caller; this is the file's own guard against a gap or an overlap.</para>
    /// <para><strong>Idempotent.</strong> A run whose manifest SHA-256 is already recorded writes nothing and
    /// reports <see cref="NewerMessagesWriteResult.AlreadyApplied"/>.</para>
    /// <para>The first run creates the file: tables, marker and rows in the same transaction; if it fails the new
    /// file is deleted. The file is claimed with <see cref="WorkspaceExclusiveLock"/> for the whole call.</para>
    /// </remarks>
    public static class NewerMessagesWriter
    {
        /// <exception cref="ArgumentException">A row breaks a rule (nothing written).</exception>
        /// <exception cref="InvalidOperationException">The file, the hand-off or the claim refuses (nothing written).</exception>
        public static NewerMessagesWriteResult Append(
            string databasePath, NewerMessagesInfo info, NewerRefreshRun run, IReadOnlyList<NewerMessageRow> messages)
        {
            NewerMessagesDatabase.RequireFixedFileName(databasePath);
            ArgumentNullException.ThrowIfNull(info);
            ArgumentNullException.ThrowIfNull(run);
            ArgumentNullException.ThrowIfNull(messages);

            Validate(info, run, messages);

            using var claim = WorkspaceExclusiveLock.Acquire(databasePath);

            var creating = !File.Exists(databasePath);

            if (creating && Directory.Exists(databasePath))
            {
                throw new InvalidOperationException("A folder sits at the newer-messages path. Nothing has been written.");
            }

            try
            {
                using var connection = WorkspaceDatabase.OpenConnection(databasePath);
                using var transaction = connection.BeginTransaction();

                if (creating)
                {
                    if (run.HandOff != NewerMessagesDatabase.HandOffWorkspace)
                    {
                        throw new InvalidOperationException(
                            "There is no newer-messages file, so the first run must hand off from the workspace. " +
                            "Nothing has been written.");
                    }

                    NewerMessagesDatabase.CreateIn(connection, transaction, info);
                }
                else
                {
                    NewerMessagesDatabase.RequireNewerMessagesFile(connection);

                    var stored = NewerMessagesReader.ReadInfo(connection, transaction);

                    if (stored.ConversationID != info.ConversationID || stored.LocalParticipantID != info.LocalParticipantID
                        || stored.OtherParticipantID != info.OtherParticipantID || stored.ChatRowID != info.ChatRowID
                        || stored.WorkspaceLastDay != info.WorkspaceLastDay)
                    {
                        throw new InvalidOperationException(
                            "The newer-messages file continues another conversation, participant, msgstore chat or " +
                            "workspace end. Nothing has been written.");
                    }

                    if (Scalar(connection, transaction, "SELECT COUNT(*) FROM RefreshRun WHERE ManifestSha256 = $sha;",
                            ("$sha", run.ManifestSha256)) > 0)
                    {
                        transaction.Rollback();

                        return new NewerMessagesWriteResult(true, false, 0, 0, 0, 0);
                    }

                    var last = LastMessage(connection, transaction);

                    if (run.HandOff == NewerMessagesDatabase.HandOffWorkspace && last is not null)
                    {
                        throw new InvalidOperationException(
                            "Messages have already been imported, so a run cannot hand off from the workspace again. " +
                            "Nothing has been written.");
                    }

                    if (run.HandOff == NewerMessagesDatabase.HandOffPreviousRun
                        && (last is not { } l || l.FromMe != run.HandOffFromMe || !string.Equals(l.KeyID, run.HandOffKeyID, StringComparison.Ordinal)))
                    {
                        throw new InvalidOperationException(
                            "The run does not continue from the last imported message. Nothing has been written.");
                    }

                    var duplicate = messages.Count(m => Exists(connection, transaction, m.FromMe, m.KeyID));

                    if (duplicate > 0)
                    {
                        throw new InvalidOperationException(
                            $"{duplicate.ToString(CultureInfo.InvariantCulture)} messages of the run are already imported. " +
                            "Nothing has been written.");
                    }
                }

                var result = Insert(connection, transaction, run, messages, creating);
                transaction.Commit();

                return result;
            }
            catch
            {
                SqliteConnection.ClearAllPools();

                if (creating && File.Exists(databasePath))
                {
                    File.Delete(databasePath);
                }

                throw;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        private static void Validate(NewerMessagesInfo info, NewerRefreshRun run, IReadOnlyList<NewerMessageRow> messages)
        {
            if (info.CreatedDateTimeUtc.Kind != DateTimeKind.Utc || run.RunDateTimeUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("Times must be DateTimeKind.Utc.");
            }

            if (run.ManifestSha256.Length != 64 || !run.ManifestSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                throw new ArgumentException("The manifest SHA-256 must be 64 lower-case hex characters.");
            }

            var keys = new HashSet<(bool, string)>();

            foreach (var m in messages)
            {
                if (!keys.Add((m.FromMe, m.KeyID)))
                {
                    throw new ArgumentException("A message (from_me, key_id) appears twice in the run.");
                }

                if (NewerMessagesTimeZone.ToLocal(m.TimestampUtcMs) is not { } local
                    || NewerMessagesTimeZone.Format(local) != m.DateTimeLocal)
                {
                    throw new ArgumentException("A message's local time is not its UTC time in the archive's zone.");
                }

                var expectedSender = m.Kind == NewerMessagesDatabase.KindSystem
                    ? (long?)null
                    : m.FromMe ? info.LocalParticipantID : info.OtherParticipantID;

                if (m.SenderParticipantID != expectedSender)
                {
                    throw new ArgumentException("A message's sender does not follow from_me.");
                }

                if (m.Attachment is { } a)
                {
                    if (m.Kind != NewerMessagesDatabase.KindUser)
                    {
                        throw new ArgumentException("Only a user message can carry an attachment.");
                    }

                    if (a.State == NewerMessagesDatabase.StateRestored && run.MediaRootPath is null)
                    {
                        throw new ArgumentException("A Restored attachment needs the run's media root.");
                    }

                    if (a.LowQualityCopyOfKeyID is { } full && string.Equals(full, m.KeyID, StringComparison.Ordinal))
                    {
                        throw new ArgumentException("An attachment cannot be a low-quality copy of itself.");
                    }
                }
            }
        }

        private static NewerMessagesWriteResult Insert(
            SqliteConnection connection, SqliteTransaction transaction, NewerRefreshRun run, IReadOnlyList<NewerMessageRow> messages, bool created)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            long? rootID = null;

            if (run.MediaRootPath is { } root)
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

                command.CommandText = "SELECT MediaRootID FROM MediaRoot WHERE RootPath = $path;";
                command.Parameters.AddWithValue("$path", full);
                rootID = command.ExecuteScalar() is long existing ? existing : null;

                if (rootID is null)
                {
                    command.CommandText =
                        "INSERT INTO MediaRoot (RootPath, RegisteredDateTimeUtc) VALUES ($path, $utc); SELECT last_insert_rowid();";
                    command.Parameters.AddWithValue("$utc", NewerMessagesDatabase.FormatUtc(run.RunDateTimeUtc));
                    rootID = (long)command.ExecuteScalar()!;
                }

                command.Parameters.Clear();
            }

            var attachments = messages.Where(m => m.Attachment is not null).Select(m => m.Attachment!).ToList();
            var lastMessage = messages.Count == 0 ? null : messages[^1];

            command.CommandText =
                """
                INSERT INTO RefreshRun (
                    RunDateTimeUtc, ManifestSha256, MsgstoreSizeBytes, MsgstoreUserVersion, MsgstoreSchemaSha256,
                    HandOff, HandOffKeyID, HandOffFromMe, MediaRootID, Messages, UserMessages, SystemMessages, Placeholders,
                    Attachments, Restored, Unmatched, LastKeyID, LastFromMe, LastSortID)
                VALUES ($utc, $sha, $size, $uv, $schema, $handoff, $handoffKey, $handoffFromMe, $root, $messages, $user, $system,
                        $placeholders, $attachments, $restored, $unmatched, $lastKey, $lastFromMe, $lastSort);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$utc", NewerMessagesDatabase.FormatUtc(run.RunDateTimeUtc));
            command.Parameters.AddWithValue("$sha", run.ManifestSha256);
            command.Parameters.AddWithValue("$size", run.MsgstoreSizeBytes);
            command.Parameters.AddWithValue("$uv", run.MsgstoreUserVersion);
            command.Parameters.AddWithValue("$schema", run.MsgstoreSchemaSha256);
            command.Parameters.AddWithValue("$handoff", run.HandOff);
            command.Parameters.AddWithValue("$handoffKey", run.HandOffKeyID);
            command.Parameters.AddWithValue("$handoffFromMe", run.HandOffFromMe ? 1 : 0);
            command.Parameters.AddWithValue("$lastFromMe", lastMessage is null ? DBNull.Value : lastMessage.FromMe ? 1 : 0);
            command.Parameters.AddWithValue("$root", (object?)rootID ?? DBNull.Value);
            command.Parameters.AddWithValue("$messages", messages.Count);
            command.Parameters.AddWithValue("$user", messages.Count(m => m.Kind == NewerMessagesDatabase.KindUser));
            command.Parameters.AddWithValue("$system", messages.Count(m => m.Kind == NewerMessagesDatabase.KindSystem));
            command.Parameters.AddWithValue("$placeholders", messages.Count(m => m.Kind == NewerMessagesDatabase.KindPlaceholder));
            command.Parameters.AddWithValue("$attachments", attachments.Count);
            command.Parameters.AddWithValue("$restored", attachments.Count(a => a.State == NewerMessagesDatabase.StateRestored));
            command.Parameters.AddWithValue("$unmatched", attachments.Count(a => a.State == NewerMessagesDatabase.StateUnmatched));
            command.Parameters.AddWithValue("$lastKey", (object?)lastMessage?.KeyID ?? DBNull.Value);
            command.Parameters.AddWithValue("$lastSort", (object?)lastMessage?.SortID ?? DBNull.Value);

            var runID = (long)command.ExecuteScalar()!;
            command.Parameters.Clear();

            var attachmentByKey = new Dictionary<(bool, string), long>();

            foreach (var m in messages)
            {
                command.CommandText =
                    """
                    INSERT INTO NewerMessage (
                        RunID, KeyID, MsgstoreRowID, SortID, FromMe, SenderParticipantID, TimestampUtcMs, DateTimeLocal,
                        MessageType, Kind, PlaceholderKind, Text, Caption, IsForwarded, ForwardScore)
                    VALUES ($run, $key, $row, $sort, $fromMe, $sender, $ts, $local, $type, $kind, $placeholder, $text,
                            $caption, $forwarded, $score);
                    SELECT last_insert_rowid();
                    """;
                command.Parameters.AddWithValue("$run", runID);
                command.Parameters.AddWithValue("$key", m.KeyID);
                command.Parameters.AddWithValue("$row", m.MsgstoreRowID);
                command.Parameters.AddWithValue("$sort", m.SortID);
                command.Parameters.AddWithValue("$fromMe", m.FromMe ? 1 : 0);
                command.Parameters.AddWithValue("$sender", (object?)m.SenderParticipantID ?? DBNull.Value);
                command.Parameters.AddWithValue("$ts", m.TimestampUtcMs);
                command.Parameters.AddWithValue("$local", m.DateTimeLocal);
                command.Parameters.AddWithValue("$type", (object?)m.MessageType ?? DBNull.Value);
                command.Parameters.AddWithValue("$kind", m.Kind);
                command.Parameters.AddWithValue("$placeholder", (object?)m.PlaceholderKind ?? DBNull.Value);
                command.Parameters.AddWithValue("$text", (object?)m.Text ?? DBNull.Value);
                command.Parameters.AddWithValue("$caption", (object?)m.Caption ?? DBNull.Value);
                command.Parameters.AddWithValue("$forwarded", m.IsForwarded is { } f ? (f ? 1 : 0) : DBNull.Value);
                command.Parameters.AddWithValue("$score", (object?)m.ForwardScore ?? DBNull.Value);

                var messageID = (long)command.ExecuteScalar()!;
                command.Parameters.Clear();

                if (m.Attachment is not { } a)
                {
                    continue;
                }

                command.CommandText =
                    """
                    INSERT INTO NewerAttachment (
                        NewerMessageID, FileName, FileSizeBytes, MimeType, FileHash, RelativePath, State, MediaRootID, Sha256)
                    VALUES ($message, $name, $size, $mime, $hash, $path, $state, $root, $sha);
                    SELECT last_insert_rowid();
                    """;
                command.Parameters.AddWithValue("$message", messageID);
                command.Parameters.AddWithValue("$name", (object?)a.FileName ?? DBNull.Value);
                command.Parameters.AddWithValue("$size", (object?)a.FileSizeBytes ?? DBNull.Value);
                command.Parameters.AddWithValue("$mime", (object?)a.MimeType ?? DBNull.Value);
                command.Parameters.AddWithValue("$hash", (object?)a.FileHash ?? DBNull.Value);
                command.Parameters.AddWithValue("$path", (object?)a.RelativePath ?? DBNull.Value);
                command.Parameters.AddWithValue("$state", a.State);
                command.Parameters.AddWithValue("$root", a.State == NewerMessagesDatabase.StateRestored ? rootID! : DBNull.Value);
                command.Parameters.AddWithValue("$sha", (object?)a.Sha256 ?? DBNull.Value);

                attachmentByKey[(m.FromMe, m.KeyID)] = (long)command.ExecuteScalar()!;
                command.Parameters.Clear();
            }

            // Low-quality links, set in this same transaction on this run's own new rows only. The full copy is in
            // the same direction (the link rule requires it) and may be of this run or an earlier one; a link whose
            // low-quality side is an earlier row is never written (it would change a committed row).
            var links = 0;

            foreach (var m in messages.Where(m => m.Attachment?.LowQualityCopyOfKeyID is not null))
            {
                var fullKey = (m.FromMe, m.Attachment!.LowQualityCopyOfKeyID!);

                if (!attachmentByKey.TryGetValue(fullKey, out var full))
                {
                    command.CommandText =
                        """
                        SELECT a.NewerAttachmentID FROM NewerAttachment AS a
                        INNER JOIN NewerMessage AS n ON n.NewerMessageID = a.NewerMessageID
                        WHERE n.KeyID = $key AND n.FromMe = $fromMe;
                        """;
                    command.Parameters.AddWithValue("$key", fullKey.Item2);
                    command.Parameters.AddWithValue("$fromMe", m.FromMe ? 1 : 0);
                    full = command.ExecuteScalar() is long found
                        ? found
                        : throw new ArgumentException("A low-quality link names a message with no imported attachment.");
                    command.Parameters.Clear();
                }

                command.CommandText = "UPDATE NewerAttachment SET LowQualityCopyOfAttachmentID = $full WHERE NewerAttachmentID = $id;";
                command.Parameters.AddWithValue("$full", full);
                command.Parameters.AddWithValue("$id", attachmentByKey[(m.FromMe, m.KeyID)]);
                command.ExecuteNonQuery();
                command.Parameters.Clear();
                links++;
            }

            return new NewerMessagesWriteResult(false, created, runID, messages.Count, attachments.Count, links);
        }

        private static (bool FromMe, string KeyID)? LastMessage(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT LastFromMe, LastKeyID FROM RefreshRun WHERE LastKeyID IS NOT NULL ORDER BY RunID DESC LIMIT 1;";

            using var reader = command.ExecuteReader();

            return reader.Read() ? (reader.GetInt64(0) != 0, reader.GetString(1)) : null;
        }

        private static bool Exists(SqliteConnection connection, SqliteTransaction transaction, bool fromMe, string key)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM NewerMessage WHERE FromMe = $fromMe AND KeyID = $key;";
            command.Parameters.AddWithValue("$fromMe", fromMe ? 1 : 0);
            command.Parameters.AddWithValue("$key", key);

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        }

        private static long Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql, (string Name, object Value) parameter)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }
}
