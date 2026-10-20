using System.Globalization;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.MsgstoreFacts
{
    /// <summary>
    /// Records and removes one batch of text-message msgstore facts in a workspace (CH-22).
    /// </summary>
    /// <remarks>
    /// <para>Each call is one protocol under <see cref="WorkspaceExclusiveLock"/>: claim the
    /// workspace, check the schema version, validate, write, commit, all in one transaction. A
    /// refusal writes nothing.</para>
    /// <para>The rows are facts copied from WhatsApp's own database, not matching decisions. This
    /// writer touches <c>MessageMsgstoreFact</c> and nothing else; it never reads or writes a
    /// Phase 7 table.</para>
    /// <para>Only a user message without an attachment row can carry a fact here. A caption's
    /// message has an attachment, and that attachment's fact already says whether it was
    /// forwarded, so captions are refused rather than recorded twice.</para>
    /// <para>A batch is all-or-nothing and is undone by <see cref="DeleteBatch"/>. A row is refused
    /// when its message already has a fact, from this batch or another: one message has one fact
    /// row, and replacing one is a delete of its batch followed by a new insert.</para>
    /// </remarks>
    public static class MessageMsgstoreFactWriter
    {
        private const string UserMessageType = "User";

        /// <summary>
        /// Records <paramref name="rows"/> as batch <paramref name="sourceBatch"/>.
        /// </summary>
        /// <returns>The number of rows recorded.</returns>
        /// <exception cref="ArgumentException">
        /// The batch tag is blank, there are no rows, the recorded time is not UTC, or the rows
        /// contradict themselves (a repeated message, a negative forward score). Nothing has been
        /// written.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The workspace is not at the current schema version, is claimed by another operation, or
        /// the rows contradict the workspace (an unknown message, a system message, a message with
        /// an attachment, a message that already has a fact). Nothing has been written.
        /// </exception>
        public static int InsertBatch(
            string databasePath,
            string sourceBatch,
            IReadOnlyList<MessageMsgstoreFactRow> rows,
            DateTime recordedDateTimeUtc)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            ValidateBatchTag(sourceBatch);
            ArgumentNullException.ThrowIfNull(rows);

            if (rows.Count == 0)
            {
                throw new ArgumentException(
                    "A batch must carry at least one row. Nothing has been written.", nameof(rows));
            }

            if (recordedDateTimeUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "The recorded time must be a UTC value. Nothing has been written.",
                    nameof(recordedDateTimeUtc));
            }

            ValidateRowsAmongThemselves(rows);

            using var claim = WorkspaceExclusiveLock.Acquire(databasePath);
            using var connection = OpenExisting(databasePath);

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(
                connection, "a text message msgstore fact batch");

            using var transaction = connection.BeginTransaction();

            ValidateRowsAgainstWorkspace(connection, transaction, sourceBatch, rows);

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO MessageMsgstoreFact (
                        MessageID, IsForwarded, ForwardScore, SourceBatch, RecordedDateTimeUtc)
                    VALUES (
                        $messageID, $isForwarded, $forwardScore, $sourceBatch, $recorded);
                    """;

                var messageID = command.Parameters.Add("$messageID", SqliteType.Integer);
                var isForwarded = command.Parameters.Add("$isForwarded", SqliteType.Integer);
                var forwardScore = command.Parameters.Add("$forwardScore", SqliteType.Integer);

                command.Parameters.AddWithValue("$sourceBatch", sourceBatch);
                command.Parameters.AddWithValue(
                    "$recorded",
                    recordedDateTimeUtc.ToString(
                        WorkspaceDatabase.UtcTimestampFormat, CultureInfo.InvariantCulture));

                foreach (var row in rows)
                {
                    messageID.Value = row.MessageID;
                    isForwarded.Value = row.IsForwarded ? 1 : 0;
                    forwardScore.Value = (object?)row.ForwardScore ?? DBNull.Value;

                    command.ExecuteNonQuery();
                }
            }

            transaction.Commit();

            return rows.Count;
        }

        /// <summary>
        /// Removes every text-message fact recorded as batch <paramref name="sourceBatch"/>.
        /// </summary>
        /// <returns>The number of rows removed; zero when the batch holds none.</returns>
        /// <exception cref="InvalidOperationException">
        /// The workspace is not at the current schema version or is claimed by another operation.
        /// Nothing has been written.
        /// </exception>
        public static int DeleteBatch(string databasePath, string sourceBatch)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            ValidateBatchTag(sourceBatch);

            using var claim = WorkspaceExclusiveLock.Acquire(databasePath);
            using var connection = OpenExisting(databasePath);

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(
                connection, "a text message msgstore fact batch removal");

            using var transaction = connection.BeginTransaction();

            int deleted;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "DELETE FROM MessageMsgstoreFact WHERE SourceBatch = $sourceBatch COLLATE BINARY;";
                command.Parameters.AddWithValue("$sourceBatch", sourceBatch);

                deleted = command.ExecuteNonQuery();
            }

            transaction.Commit();

            return deleted;
        }

        private static void ValidateBatchTag(string sourceBatch)
        {
            if (string.IsNullOrWhiteSpace(sourceBatch))
            {
                throw new ArgumentException(
                    "A batch tag is required and must not be blank. Nothing has been written.",
                    nameof(sourceBatch));
            }
        }

        /// <summary>
        /// A path that holds nothing is refused rather than created: this writer records facts in
        /// an existing workspace and never creates one.
        /// </summary>
        private static SqliteConnection OpenExisting(string databasePath)
        {
            if (!File.Exists(databasePath))
            {
                throw new InvalidOperationException(
                    "There is no workspace database at the supplied path. Nothing has been created " +
                    "or written.");
            }

            return WorkspaceDatabase.OpenConnection(databasePath);
        }

        private static void ValidateRowsAmongThemselves(IReadOnlyList<MessageMsgstoreFactRow> rows)
        {
            var seen = new HashSet<long>();

            foreach (var row in rows)
            {
                ArgumentNullException.ThrowIfNull(row, nameof(rows));

                if (!seen.Add(row.MessageID))
                {
                    throw new ArgumentException(
                        $"Message {Text(row.MessageID)} appears more than once in the batch. " +
                        $"Nothing has been written.",
                        nameof(rows));
                }

                if (row.ForwardScore is < 0)
                {
                    throw new ArgumentException(
                        $"Message {Text(row.MessageID)} carries a negative forward score. " +
                        $"Nothing has been written.",
                        nameof(rows));
                }
            }
        }

        private static void ValidateRowsAgainstWorkspace(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sourceBatch,
            IReadOnlyList<MessageMsgstoreFactRow> rows)
        {
            using var messageType = Prepared(
                connection, transaction, "SELECT MessageType FROM Message WHERE MessageID = $id;");

            using var attachmentCount = Prepared(
                connection, transaction, "SELECT COUNT(*) FROM Attachment WHERE MessageID = $id;");

            using var existingFact = Prepared(
                connection,
                transaction,
                "SELECT SourceBatch FROM MessageMsgstoreFact WHERE MessageID = $id;");

            foreach (var row in rows)
            {
                messageType.Parameters["$id"].Value = row.MessageID;

                if (messageType.ExecuteScalar() is not string type)
                {
                    throw new InvalidOperationException(
                        $"Message {Text(row.MessageID)} does not exist in the workspace. " +
                        $"Nothing has been written.");
                }

                if (!string.Equals(type, UserMessageType, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Message {Text(row.MessageID)} is a system message, which carries no " +
                        $"msgstore fact. Nothing has been written.");
                }

                attachmentCount.Parameters["$id"].Value = row.MessageID;

                if (Convert.ToInt64(attachmentCount.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                {
                    throw new InvalidOperationException(
                        $"Message {Text(row.MessageID)} has an attachment. Its msgstore facts " +
                        $"belong to the attachment, not to the text. Nothing has been written.");
                }

                existingFact.Parameters["$id"].Value = row.MessageID;

                if (existingFact.ExecuteScalar() is string existingBatch)
                {
                    throw new InvalidOperationException(
                        $"Message {Text(row.MessageID)} already has a msgstore fact from " +
                        (string.Equals(existingBatch, sourceBatch, StringComparison.Ordinal)
                            ? "this batch"
                            : "another batch") +
                        $". Remove that batch first to replace it. Nothing has been written.");
                }
            }
        }

        private static SqliteCommand Prepared(
            SqliteConnection connection, SqliteTransaction transaction, string sql)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.Add("$id", SqliteType.Integer);

            return command;
        }

        private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
