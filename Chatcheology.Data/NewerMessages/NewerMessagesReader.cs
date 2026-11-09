using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.NewerMessages
{
    /// <summary>Reads a newer-messages file, read-only, in timeline order (CH-71 step 3).</summary>
    public static class NewerMessagesReader
    {
        /// <summary>The newer-messages file beside <paramref name="mainWorkspacePath"/>, or null when there is none.</summary>
        /// <exception cref="InvalidOperationException">A file is there but it is not a newer-messages file of this format.</exception>
        public static NewerMessagesContents? ReadBeside(string mainWorkspacePath)
        {
            var path = NewerMessagesDatabase.PathBeside(mainWorkspacePath);

            return File.Exists(path) ? Read(path) : null;
        }

        /// <exception cref="FileNotFoundException">There is no file.</exception>
        /// <exception cref="InvalidOperationException">It is not a newer-messages file of this format.</exception>
        public static NewerMessagesContents Read(string databasePath)
        {
            try
            {
                using var connection = NewerMessagesDatabase.OpenReadOnly(databasePath);

                var info = ReadInfo(connection, null);
                var roots = new Dictionary<long, string>();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT MediaRootID, RootPath FROM MediaRoot;";

                    using var reader = command.ExecuteReader();

                    while (reader.Read())
                    {
                        roots[reader.GetInt64(0)] = reader.GetString(1);
                    }
                }

                var runs = new List<NewerRefreshRun>();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        """
                        SELECT RunID, RunDateTimeUtc, ManifestSha256, MsgstoreSizeBytes, MsgstoreUserVersion,
                               MsgstoreSchemaSha256, HandOff, HandOffKeyID, MediaRootID, Messages, UserMessages,
                               SystemMessages, Placeholders, Attachments, Restored, Unmatched, LastKeyID, LastSortID
                        FROM RefreshRun ORDER BY RunID;
                        """;

                    using var reader = command.ExecuteReader();

                    while (reader.Read())
                    {
                        runs.Add(new NewerRefreshRun
                        {
                            RunID = reader.GetInt64(0),
                            RunDateTimeUtc = DateTime.ParseExact(reader.GetString(1), "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'",
                                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                            ManifestSha256 = reader.GetString(2),
                            MsgstoreSizeBytes = reader.GetInt64(3),
                            MsgstoreUserVersion = reader.GetInt64(4),
                            MsgstoreSchemaSha256 = reader.GetString(5),
                            HandOff = reader.GetString(6),
                            HandOffKeyID = reader.GetString(7),
                            MediaRootPath = reader.IsDBNull(8) ? null : roots[reader.GetInt64(8)],
                            Messages = reader.GetInt32(9),
                            UserMessages = reader.GetInt32(10),
                            SystemMessages = reader.GetInt32(11),
                            Placeholders = reader.GetInt32(12),
                            Attachments = reader.GetInt32(13),
                            Restored = reader.GetInt32(14),
                            Unmatched = reader.GetInt32(15),
                            LastKeyID = reader.IsDBNull(16) ? null : reader.GetString(16),
                            LastSortID = reader.IsDBNull(17) ? null : reader.GetInt64(17),
                        });
                    }
                }

                var messages = new List<NewerMessageRow>();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        """
                        SELECT m.NewerMessageID, m.RunID, m.KeyID, m.MsgstoreRowID, m.SortID, m.FromMe, m.SenderParticipantID,
                               m.TimestampUtcMs, m.DateTimeLocal, m.MessageType, m.Kind, m.PlaceholderKind, m.Text, m.Caption,
                               m.IsForwarded, m.ForwardScore,
                               a.NewerAttachmentID, a.FileName, a.FileSizeBytes, a.MimeType, a.FileHash, a.RelativePath,
                               a.State, a.MediaRootID, a.Sha256, a.LowQualityCopyOfAttachmentID,
                               (SELECT fm.KeyID FROM NewerAttachment AS fa
                                INNER JOIN NewerMessage AS fm ON fm.NewerMessageID = fa.NewerMessageID
                                WHERE fa.NewerAttachmentID = a.LowQualityCopyOfAttachmentID)
                        FROM NewerMessage AS m
                        LEFT JOIN NewerAttachment AS a ON a.NewerMessageID = m.NewerMessageID
                        ORDER BY m.NewerMessageID;
                        """;

                    using var reader = command.ExecuteReader();

                    while (reader.Read())
                    {
                        NewerAttachmentRow? attachment = null;

                        if (!reader.IsDBNull(16))
                        {
                            attachment = new NewerAttachmentRow
                            {
                                NewerAttachmentID = reader.GetInt64(16),
                                FileName = NullableString(reader, 17),
                                FileSizeBytes = reader.IsDBNull(18) ? null : reader.GetInt64(18),
                                MimeType = NullableString(reader, 19),
                                FileHash = NullableString(reader, 20),
                                RelativePath = NullableString(reader, 21),
                                State = reader.GetString(22),
                                MediaRootPath = reader.IsDBNull(23) ? null : roots[reader.GetInt64(23)],
                                Sha256 = NullableString(reader, 24),
                                LowQualityCopyOfAttachmentID = reader.IsDBNull(25) ? null : reader.GetInt64(25),
                                LowQualityCopyOfKeyID = NullableString(reader, 26),
                            };
                        }

                        messages.Add(new NewerMessageRow
                        {
                            NewerMessageID = reader.GetInt64(0),
                            RunID = reader.GetInt64(1),
                            KeyID = reader.GetString(2),
                            MsgstoreRowID = reader.GetInt64(3),
                            SortID = reader.GetInt64(4),
                            FromMe = reader.GetInt64(5) != 0,
                            SenderParticipantID = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                            TimestampUtcMs = reader.GetInt64(7),
                            DateTimeLocal = reader.GetString(8),
                            MessageType = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                            Kind = reader.GetString(10),
                            PlaceholderKind = NullableString(reader, 11),
                            Text = NullableString(reader, 12),
                            Caption = NullableString(reader, 13),
                            IsForwarded = reader.IsDBNull(14) ? null : reader.GetInt64(14) != 0,
                            ForwardScore = reader.IsDBNull(15) ? null : reader.GetInt32(15),
                            Attachment = attachment,
                        });
                    }
                }

                return new NewerMessagesContents(info, runs, messages);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        internal static NewerMessagesInfo ReadInfo(SqliteConnection connection, SqliteTransaction? transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT ConversationID, LocalParticipantID, OtherParticipantID, ChatRowID, CreatedDateTimeUtc
                FROM NewerMessagesInfo WHERE NewerMessagesInfoID = 1;
                """;

            using var reader = command.ExecuteReader();

            if (!reader.Read())
            {
                throw new InvalidOperationException("The newer-messages file has no marker row.");
            }

            return new NewerMessagesInfo(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                DateTime.ParseExact(reader.GetString(4), "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
        }

        private static string? NullableString(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
