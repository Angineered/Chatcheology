using System.Globalization;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.MsgstoreFacts
{
    /// <summary>
    /// Records and removes one batch of attachment captions in a workspace (CH-63, CH-71).
    /// </summary>
    /// <remarks>
    /// <para>Each call is one protocol under <see cref="WorkspaceExclusiveLock"/>: claim the
    /// workspace, check the schema version, validate, write, commit, all in one transaction. A
    /// refusal writes nothing.</para>
    /// <para>The rows are captions copied from WhatsApp's own database, not matching decisions. This
    /// writer touches <c>AttachmentCaptionFact</c> and nothing else; it never reads or writes a
    /// Phase 7 table.</para>
    /// <para>The caption is stored exactly as given: nothing is trimmed or normalised. An empty
    /// caption is refused; "no caption" is the absence of a row.</para>
    /// <para>A batch is all-or-nothing and is undone by <see cref="DeleteBatch"/>. A row is refused
    /// when its attachment already has a caption, from this batch or another: one attachment has one
    /// caption row, and replacing one is a delete of its batch followed by a new insert.</para>
    /// </remarks>
    public static class AttachmentCaptionFactWriter
    {
        /// <summary>
        /// Records <paramref name="rows"/> as batch <paramref name="sourceBatch"/>.
        /// </summary>
        /// <returns>The number of rows recorded.</returns>
        /// <exception cref="ArgumentException">
        /// The batch tag is blank, there are no rows, the recorded time is not UTC, or the rows
        /// contradict themselves (a repeated attachment, an empty caption). Nothing has been written.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The workspace is not at the current schema version, is claimed by another operation, or
        /// the rows contradict the workspace (an unknown attachment, an attachment that already has
        /// a caption). Nothing has been written.
        /// </exception>
        public static int InsertBatch(
            string databasePath,
            string sourceBatch,
            IReadOnlyList<AttachmentCaptionFactRow> rows,
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

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(connection, "a caption fact batch");

            using var transaction = connection.BeginTransaction();

            ValidateRowsAgainstWorkspace(connection, transaction, sourceBatch, rows);

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO AttachmentCaptionFact (
                        AttachmentID, CaptionText, FromPartner, SourceBatch, RecordedDateTimeUtc)
                    VALUES (
                        $attachmentID, $captionText, $fromPartner, $sourceBatch, $recorded);
                    """;

                var attachmentID = command.Parameters.Add("$attachmentID", SqliteType.Integer);
                var captionText = command.Parameters.Add("$captionText", SqliteType.Text);
                var fromPartner = command.Parameters.Add("$fromPartner", SqliteType.Integer);

                command.Parameters.AddWithValue("$sourceBatch", sourceBatch);
                command.Parameters.AddWithValue(
                    "$recorded",
                    recordedDateTimeUtc.ToString(
                        WorkspaceDatabase.UtcTimestampFormat, CultureInfo.InvariantCulture));

                foreach (var row in rows)
                {
                    attachmentID.Value = row.AttachmentID;
                    captionText.Value = row.CaptionText;
                    fromPartner.Value = row.FromPartner ? 1 : 0;

                    command.ExecuteNonQuery();
                }
            }

            transaction.Commit();

            return rows.Count;
        }

        /// <summary>
        /// Removes every caption recorded as batch <paramref name="sourceBatch"/>.
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

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(connection, "a caption fact batch removal");

            using var transaction = connection.BeginTransaction();

            int deleted;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    "DELETE FROM AttachmentCaptionFact WHERE SourceBatch = $sourceBatch COLLATE BINARY;";
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
        /// A path that holds nothing is refused rather than created: this writer records captions
        /// in an existing workspace and never creates one.
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

        private static void ValidateRowsAmongThemselves(IReadOnlyList<AttachmentCaptionFactRow> rows)
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

                // The text itself is never put in a message: it is personal content.
                if (string.IsNullOrEmpty(row.CaptionText))
                {
                    throw new ArgumentException(
                        $"Attachment {Text(row.AttachmentID)} carries an empty caption. No caption " +
                        $"is recorded by leaving the attachment out. Nothing has been written.",
                        nameof(rows));
                }
            }
        }

        private static void ValidateRowsAgainstWorkspace(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sourceBatch,
            IReadOnlyList<AttachmentCaptionFactRow> rows)
        {
            using var attachmentExists = Prepared(
                connection, transaction, "SELECT COUNT(*) FROM Attachment WHERE AttachmentID = $id;");

            using var existingFact = Prepared(
                connection,
                transaction,
                "SELECT SourceBatch FROM AttachmentCaptionFact WHERE AttachmentID = $id;");

            foreach (var row in rows)
            {
                attachmentExists.Parameters["$id"].Value = row.AttachmentID;

                if (Convert.ToInt64(attachmentExists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                {
                    throw new InvalidOperationException(
                        $"Attachment {Text(row.AttachmentID)} does not exist in the workspace. " +
                        $"Nothing has been written.");
                }

                existingFact.Parameters["$id"].Value = row.AttachmentID;

                if (existingFact.ExecuteScalar() is string existingBatch)
                {
                    throw new InvalidOperationException(
                        $"Attachment {Text(row.AttachmentID)} already has a caption from " +
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
