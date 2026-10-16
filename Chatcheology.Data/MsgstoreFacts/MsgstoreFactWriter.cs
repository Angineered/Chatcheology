using System.Globalization;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.MsgstoreFacts
{
    /// <summary>
    /// Records and removes one batch of msgstore facts in a workspace.
    /// </summary>
    /// <remarks>
    /// <para>Each call is one protocol under <see cref="WorkspaceExclusiveLock"/>: claim the
    /// workspace, check the schema version, validate, write, commit, all in one transaction. A
    /// refusal writes nothing.</para>
    /// <para>The rows are facts copied from WhatsApp's own database, not matching decisions. This
    /// writer touches <c>AttachmentMsgstoreFact</c> and nothing else; it never reads or writes a
    /// Phase 7 table.</para>
    /// <para>A batch is all-or-nothing and is undone by <see cref="DeleteBatch"/>. A row is refused
    /// when its attachment already has a fact, from this batch or another: one attachment has one
    /// fact row, and replacing one is a delete of its batch followed by a new insert.</para>
    /// </remarks>
    public static class MsgstoreFactWriter
    {
        /// <summary>
        /// Records <paramref name="rows"/> as batch <paramref name="sourceBatch"/>.
        /// </summary>
        /// <returns>The number of rows recorded.</returns>
        /// <exception cref="ArgumentException">
        /// The batch tag is empty, there are no rows, or the rows contradict themselves (a repeated
        /// attachment, a self-link, a negative forward score, a chain). Nothing has been written.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The workspace is not at the current schema version, is claimed by another operation, or
        /// the rows contradict the workspace (an unknown attachment or target, an attachment that
        /// already has a fact, a chain through a stored row). Nothing has been written.
        /// </exception>
        public static int InsertBatch(
            string databasePath,
            string sourceBatch,
            IReadOnlyList<MsgstoreFactRow> rows,
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

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(connection, "a msgstore fact batch");

            using var transaction = connection.BeginTransaction();

            ValidateRowsAgainstWorkspace(connection, transaction, sourceBatch, rows);

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO AttachmentMsgstoreFact (
                        AttachmentID, IsForwarded, ForwardScore, LowQualityCopyOfAttachmentID,
                        SourceBatch, RecordedDateTimeUtc)
                    VALUES (
                        $attachmentID, $isForwarded, $forwardScore, $lowQualityCopyOf,
                        $sourceBatch, $recorded);
                    """;

                var attachmentID = command.Parameters.Add("$attachmentID", SqliteType.Integer);
                var isForwarded = command.Parameters.Add("$isForwarded", SqliteType.Integer);
                var forwardScore = command.Parameters.Add("$forwardScore", SqliteType.Integer);
                var lowQualityCopyOf = command.Parameters.Add("$lowQualityCopyOf", SqliteType.Integer);

                command.Parameters.AddWithValue("$sourceBatch", sourceBatch);
                command.Parameters.AddWithValue(
                    "$recorded",
                    recordedDateTimeUtc.ToString(
                        WorkspaceDatabase.UtcTimestampFormat, CultureInfo.InvariantCulture));

                foreach (var row in rows)
                {
                    attachmentID.Value = row.AttachmentID;
                    isForwarded.Value = row.IsForwarded ? 1 : 0;
                    forwardScore.Value = (object?)row.ForwardScore ?? DBNull.Value;
                    lowQualityCopyOf.Value = (object?)row.LowQualityCopyOfAttachmentID ?? DBNull.Value;

                    command.ExecuteNonQuery();
                }
            }

            transaction.Commit();

            return rows.Count;
        }

        /// <summary>
        /// Removes every row recorded as batch <paramref name="sourceBatch"/>.
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

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(connection, "a msgstore fact batch removal");

            using var transaction = connection.BeginTransaction();

            int deleted;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "DELETE FROM AttachmentMsgstoreFact WHERE SourceBatch = $sourceBatch COLLATE BINARY;";
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

        private static void ValidateRowsAmongThemselves(IReadOnlyList<MsgstoreFactRow> rows)
        {
            var seen = new HashSet<long>();

            foreach (var row in rows)
            {
                ArgumentNullException.ThrowIfNull(row, nameof(rows));

                if (!seen.Add(row.AttachmentID))
                {
                    throw new ArgumentException(
                        $"Attachment {Text(row.AttachmentID)} appears more than once in the batch. " +
                        $"Nothing has been written.",
                        nameof(rows));
                }

                if (row.LowQualityCopyOfAttachmentID == row.AttachmentID)
                {
                    throw new ArgumentException(
                        $"Attachment {Text(row.AttachmentID)} is recorded as a low-quality copy of " +
                        $"itself. Nothing has been written.",
                        nameof(rows));
                }

                if (row.ForwardScore is < 0)
                {
                    throw new ArgumentException(
                        $"Attachment {Text(row.AttachmentID)} carries a negative forward score. " +
                        $"Nothing has been written.",
                        nameof(rows));
                }
            }

            var copies = rows
                .Where(row => row.LowQualityCopyOfAttachmentID is not null)
                .Select(row => row.AttachmentID)
                .ToHashSet();

            foreach (var row in rows)
            {
                if (row.LowQualityCopyOfAttachmentID is { } target && copies.Contains(target))
                {
                    throw new ArgumentException(
                        $"Attachment {Text(row.AttachmentID)} is recorded as a low-quality copy of " +
                        $"attachment {Text(target)}, which the batch itself records as a low-quality " +
                        $"copy. Chains are not recorded. Nothing has been written.",
                        nameof(rows));
                }
            }
        }

        private static void ValidateRowsAgainstWorkspace(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sourceBatch,
            IReadOnlyList<MsgstoreFactRow> rows)
        {
            using var attachmentExists = Prepared(
                connection, transaction, "SELECT COUNT(*) FROM Attachment WHERE AttachmentID = $id;");

            using var existingFact = Prepared(
                connection,
                transaction,
                "SELECT SourceBatch FROM AttachmentMsgstoreFact WHERE AttachmentID = $id;");

            using var storedCopy = Prepared(
                connection,
                transaction,
                """
                SELECT COUNT(*) FROM AttachmentMsgstoreFact
                WHERE AttachmentID = $id AND LowQualityCopyOfAttachmentID IS NOT NULL;
                """);

            using var storedTarget = Prepared(
                connection,
                transaction,
                "SELECT COUNT(*) FROM AttachmentMsgstoreFact WHERE LowQualityCopyOfAttachmentID = $id;");

            foreach (var row in rows)
            {
                if (Count(attachmentExists, row.AttachmentID) == 0)
                {
                    throw new InvalidOperationException(
                        $"Attachment {Text(row.AttachmentID)} does not exist in the workspace. " +
                        $"Nothing has been written.");
                }

                existingFact.Parameters["$id"].Value = row.AttachmentID;

                if (existingFact.ExecuteScalar() is string existingBatch)
                {
                    throw new InvalidOperationException(
                        $"Attachment {Text(row.AttachmentID)} already has a msgstore fact from " +
                        (string.Equals(existingBatch, sourceBatch, StringComparison.Ordinal)
                            ? "this batch"
                            : "another batch") +
                        $". Remove that batch first to replace it. Nothing has been written.");
                }

                if (row.LowQualityCopyOfAttachmentID is not { } target)
                {
                    continue;
                }

                if (Count(attachmentExists, target) == 0)
                {
                    throw new InvalidOperationException(
                        $"Attachment {Text(row.AttachmentID)} is recorded as a low-quality copy of " +
                        $"attachment {Text(target)}, which does not exist in the workspace. Nothing " +
                        $"has been written.");
                }

                if (Count(storedCopy, target) > 0)
                {
                    throw new InvalidOperationException(
                        $"Attachment {Text(row.AttachmentID)} is recorded as a low-quality copy of " +
                        $"attachment {Text(target)}, which the workspace already records as a " +
                        $"low-quality copy. Chains are not recorded. Nothing has been written.");
                }

                if (Count(storedTarget, row.AttachmentID) > 0)
                {
                    throw new InvalidOperationException(
                        $"Attachment {Text(row.AttachmentID)} is recorded as a low-quality copy, but " +
                        $"the workspace already records another attachment as a low-quality copy " +
                        $"of it. Chains are not recorded. Nothing has been written.");
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

        private static long Count(SqliteCommand command, long id)
        {
            command.Parameters["$id"].Value = id;

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
