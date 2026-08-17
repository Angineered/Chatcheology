using System.Globalization;
using Chatcheology.Core.Models;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Workspace
{
    /// <summary>
    /// Stores already-parsed messages into a workspace database as one conversation.
    /// </summary>
    /// <remarks>
    /// One call is one atomic import. Every row it creates — the import source, the conversation,
    /// the participants, their conversation memberships and the messages — is written inside a
    /// single transaction, so a constraint failure anywhere leaves no trace of the attempt and
    /// leaves earlier imports untouched.
    /// <para>
    /// The importer preserves what it is given. It does not renumber messages, reorder them, trim
    /// sender names, convert timestamps or drop anything to make an import succeed. Input that
    /// cannot be stored faithfully is rejected instead.
    /// </para>
    /// <para>
    /// Exception messages identify a message by its <see cref="ParsedMessage.SequenceNumber"/> and
    /// never quote message content or sender names, matching the parser's rule that a diagnostic
    /// must not leak the source it was reading.
    /// </para>
    /// </remarks>
    public sealed class WorkspaceImporter
    {
        /// <summary>
        /// How a source message's local wall-clock timestamp is stored.
        /// </summary>
        /// <remarks>
        /// The separators are quoted so that <c>-</c> and <c>:</c> are written literally rather than
        /// as culture-dependent separator placeholders, the same way the parser pins its own
        /// timestamp format.
        /// <para>
        /// Deliberately carries no <c>Z</c> and no offset. It records a wall-clock reading exactly
        /// as the export wrote it, and it sorts correctly as text. The supported export format has
        /// minute precision, so the seconds component is always <c>00</c>.
        /// </para>
        /// </remarks>
        private const string LocalTimestampFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss";

        /// <summary>
        /// How workspace metadata timestamps are stored: the round-trip format, which on a
        /// <see cref="DateTimeKind.Utc"/> value ends in <c>Z</c> and reads back as UTC.
        /// </summary>
        /// <remarks>
        /// Deliberately a different format from <see cref="LocalTimestampFormat"/>. These two kinds
        /// of timestamp mean different things, and storing them identically would invite treating a
        /// wall-clock reading as an instant.
        /// </remarks>
        private const string UtcTimestampFormat = "O";

        /// <summary>The stored text for <see cref="MessageType.User"/>.</summary>
        private const string UserMessageTypeText = "User";

        /// <summary>The stored text for <see cref="MessageType.System"/>.</summary>
        private const string SystemMessageTypeText = "System";

        /// <summary>
        /// Imports <paramref name="request"/> into the workspace database at
        /// <paramref name="databasePath"/> as a single new conversation.
        /// </summary>
        /// <param name="databasePath">
        /// An already-initialised workspace database, supplied by the caller. No location is assumed
        /// here, and this method does not create or upgrade the schema.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// The request cannot be stored faithfully: required metadata is missing,
        /// <see cref="WorkspaceImportRequest.Messages"/> is empty,
        /// <see cref="WorkspaceImportRequest.ImportedDateTimeUtc"/> is not UTC,
        /// <see cref="WorkspaceImportRequest.OriginalFileName"/> carries path information, or a
        /// message's timestamp or sender contradicts its type.
        /// </exception>
        /// <exception cref="SqliteException">
        /// The database rejected the import — a duplicate sequence number within the conversation,
        /// for example. The whole import is rolled back.
        /// </exception>
        public WorkspaceImportResult Import(string databasePath, WorkspaceImportRequest request)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            ArgumentNullException.ThrowIfNull(request);

            // Validated before anything is opened, so a request that could never be stored does not
            // start a transaction.
            ValidateRequest(request);

            using var connection = WorkspaceDatabase.OpenConnection(databasePath);
            using var transaction = connection.BeginTransaction();

            var importSourceID = InsertImportSource(connection, transaction, request);
            var conversationID = InsertConversation(connection, transaction, request);

            var participantIDsBySender =
                InsertParticipants(connection, transaction, conversationID, request.Messages);

            InsertMessages(
                connection,
                transaction,
                conversationID,
                importSourceID,
                participantIDsBySender,
                request.Messages);

            // Anything thrown above leaves this uncalled, and disposing an uncommitted transaction
            // rolls it back. The exception is deliberately not caught: a failed import must be
            // visible to the caller, not silently reported as an empty one.
            transaction.Commit();

            return new WorkspaceImportResult
            {
                ImportSourceID = importSourceID,
                ConversationID = conversationID,
                ParticipantCount = participantIDsBySender.Count,
                MessageCount = request.Messages.Count,
            };
        }

        private static void ValidateRequest(WorkspaceImportRequest request)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceType, nameof(request.SourceType));
            ArgumentException.ThrowIfNullOrWhiteSpace(
                request.SourceDisplayName, nameof(request.SourceDisplayName));
            ArgumentException.ThrowIfNullOrWhiteSpace(
                request.ConversationTitle, nameof(request.ConversationTitle));
            ArgumentNullException.ThrowIfNull(request.Messages, nameof(request.Messages));

            if (request.Messages.Count == 0)
            {
                throw new ArgumentException(
                    "The import contains no messages. An import creates a conversation and an " +
                    "import source, so an empty message collection is rejected rather than " +
                    "committed as a conversation with nothing in it. Parsing an empty or " +
                    "whitespace-only export still legitimately yields no messages; it is importing " +
                    "that result that is refused.",
                    nameof(request));
            }

            if (request.ImportedDateTimeUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    $"ImportedDateTimeUtc has DateTimeKind.{request.ImportedDateTimeUtc.Kind}, but " +
                    $"workspace metadata records a real instant and must be DateTimeKind.Utc. The " +
                    $"value is not converted, because guessing which instant a non-UTC value meant " +
                    $"would silently record the wrong import time.",
                    nameof(request));
            }

            if (request.OriginalFileName is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(
                    request.OriginalFileName, nameof(request.OriginalFileName));

                if (Path.GetFileName(request.OriginalFileName) != request.OriginalFileName)
                {
                    throw new ArgumentException(
                        "OriginalFileName must be a file name only. It carries directory or volume " +
                        "information, and a workspace must not record the filesystem layout of the " +
                        "machine it was built on.",
                        nameof(request));
                }
            }

            foreach (var message in request.Messages)
            {
                ValidateMessage(message);
            }
        }

        private static void ValidateMessage(ParsedMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            if (message.MessageDateTime.Kind != DateTimeKind.Unspecified)
            {
                throw new ArgumentException(
                    $"Message {message.SequenceNumber}: MessageDateTime has " +
                    $"DateTimeKind.{message.MessageDateTime.Kind}, but a source message timestamp is " +
                    $"a local wall-clock reading and must be DateTimeKind.Unspecified. It is " +
                    $"rejected rather than converted, because attaching or stripping timezone " +
                    $"meaning here would silently change what the export said.",
                    nameof(message));
            }

            switch (message.MessageType)
            {
                case MessageType.User when string.IsNullOrEmpty(message.Sender):
                    throw new ArgumentException(
                        $"Message {message.SequenceNumber}: a User message carries no sender, so it " +
                        $"cannot be attributed to a participant.",
                        nameof(message));

                case MessageType.System when message.Sender is not null:
                    throw new ArgumentException(
                        $"Message {message.SequenceNumber}: a System message carries a sender. " +
                        $"System messages are not participant-authored and must not create or " +
                        $"reference a participant.",
                        nameof(message));
            }
        }

        private static long InsertImportSource(
            SqliteConnection connection,
            SqliteTransaction transaction,
            WorkspaceImportRequest request)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO ImportSource (
                    SourceType,
                    DisplayName,
                    OriginalFileName,
                    SHA256,
                    ImportedDateTimeUtc,
                    SourceTimeZoneID)
                VALUES (
                    $sourceType,
                    $displayName,
                    $originalFileName,
                    $sha256,
                    $importedDateTimeUtc,
                    $sourceTimeZoneID)
                RETURNING ImportSourceID;
                """;

            command.Parameters.AddWithValue("$sourceType", request.SourceType);
            command.Parameters.AddWithValue("$displayName", request.SourceDisplayName);
            command.Parameters.AddWithValue("$originalFileName", ToDatabaseValue(request.OriginalFileName));
            command.Parameters.AddWithValue("$sha256", ToDatabaseValue(request.SHA256));
            command.Parameters.AddWithValue("$importedDateTimeUtc", FormatUtc(request.ImportedDateTimeUtc));

            // Stored exactly as supplied, including null. Never resolved or applied to a timestamp.
            command.Parameters.AddWithValue("$sourceTimeZoneID", ToDatabaseValue(request.SourceTimeZoneID));

            return (long)command.ExecuteScalar()!;
        }

        private static long InsertConversation(
            SqliteConnection connection,
            SqliteTransaction transaction,
            WorkspaceImportRequest request)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO Conversation (Title, CreatedDateTimeUtc)
                VALUES ($title, $createdDateTimeUtc)
                RETURNING ConversationID;
                """;

            command.Parameters.AddWithValue("$title", request.ConversationTitle);

            // The same instant as the import source's, by design: one import operation, one
            // timestamp. This records when the workspace row was created, not when the chat began.
            command.Parameters.AddWithValue(
                "$createdDateTimeUtc", FormatUtc(request.ImportedDateTimeUtc));

            return (long)command.ExecuteScalar()!;
        }

        /// <summary>
        /// Creates one participant per distinct user-message sender in this import, links each to
        /// the conversation, and returns the sender-to-identifier map.
        /// </summary>
        /// <remarks>
        /// Senders are distinguished with ordinal comparison and stored exactly as the export wrote
        /// them, so two names differing only in case or in an invisible character remain two
        /// participants rather than being merged on a guess. Identity resolution across imports and
        /// conversations does not exist yet.
        /// <para>
        /// System messages create no participant.
        /// </para>
        /// </remarks>
        private static Dictionary<string, long> InsertParticipants(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long conversationID,
            IReadOnlyList<ParsedMessage> messages)
        {
            var participantIDsBySender = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach (var message in messages)
            {
                if (message.MessageType != MessageType.User)
                {
                    continue;
                }

                // Guaranteed non-null for a User message by ValidateMessage.
                var sender = message.Sender!;

                if (participantIDsBySender.ContainsKey(sender))
                {
                    continue;
                }

                var participantID = InsertParticipant(connection, transaction, sender);
                InsertConversationParticipant(connection, transaction, conversationID, participantID);

                participantIDsBySender.Add(sender, participantID);
            }

            return participantIDsBySender;
        }

        private static long InsertParticipant(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string displayName)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO Participant (DisplayName)
                VALUES ($displayName)
                RETURNING ParticipantID;
                """;

            command.Parameters.AddWithValue("$displayName", displayName);

            return (long)command.ExecuteScalar()!;
        }

        private static void InsertConversationParticipant(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long conversationID,
            long participantID)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO ConversationParticipant (ConversationID, ParticipantID)
                VALUES ($conversationID, $participantID);
                """;

            command.Parameters.AddWithValue("$conversationID", conversationID);
            command.Parameters.AddWithValue("$participantID", participantID);

            command.ExecuteNonQuery();
        }

        /// <remarks>
        /// One command is prepared and its parameters rebound per message, rather than building a
        /// new command per row.
        /// </remarks>
        private static void InsertMessages(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long conversationID,
            long importSourceID,
            Dictionary<string, long> participantIDsBySender,
            IReadOnlyList<ParsedMessage> messages)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO Message (
                    ConversationID,
                    ImportSourceID,
                    SequenceNumber,
                    MessageDateTimeLocal,
                    SenderParticipantID,
                    MessageType,
                    MessageContent,
                    RawContent,
                    SourceLineStart,
                    SourceLineEnd)
                VALUES (
                    $conversationID,
                    $importSourceID,
                    $sequenceNumber,
                    $messageDateTimeLocal,
                    $senderParticipantID,
                    $messageType,
                    $messageContent,
                    $rawContent,
                    $sourceLineStart,
                    $sourceLineEnd);
                """;

            command.Parameters.AddWithValue("$conversationID", conversationID);
            command.Parameters.AddWithValue("$importSourceID", importSourceID);

            var sequenceNumber = command.Parameters.Add("$sequenceNumber", SqliteType.Integer);
            var messageDateTimeLocal = command.Parameters.Add("$messageDateTimeLocal", SqliteType.Text);
            var senderParticipantID = command.Parameters.Add("$senderParticipantID", SqliteType.Integer);
            var messageType = command.Parameters.Add("$messageType", SqliteType.Text);
            var messageContent = command.Parameters.Add("$messageContent", SqliteType.Text);
            var rawContent = command.Parameters.Add("$rawContent", SqliteType.Text);
            var sourceLineStart = command.Parameters.Add("$sourceLineStart", SqliteType.Integer);
            var sourceLineEnd = command.Parameters.Add("$sourceLineEnd", SqliteType.Integer);

            command.Prepare();

            foreach (var message in messages)
            {
                // Persisted exactly as the parser produced it. The unique constraint on
                // (ConversationID, SequenceNumber) therefore validates the source ordering itself
                // rather than validating numbering this importer invented.
                sequenceNumber.Value = message.SequenceNumber;

                messageDateTimeLocal.Value = FormatLocalWallClock(message.MessageDateTime);

                senderParticipantID.Value = message.MessageType == MessageType.User
                    ? participantIDsBySender[message.Sender!]
                    : DBNull.Value;

                messageType.Value = FormatMessageType(message.MessageType);
                messageContent.Value = message.MessageContent;
                rawContent.Value = message.RawContent;
                sourceLineStart.Value = message.SourceLineStart;
                sourceLineEnd.Value = message.SourceLineEnd;

                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Formats a source message timestamp as the local wall-clock text the workspace stores.
        /// </summary>
        private static string FormatLocalWallClock(DateTime messageDateTime) =>
            messageDateTime.ToString(LocalTimestampFormat, CultureInfo.InvariantCulture);

        /// <summary>
        /// Formats a workspace metadata timestamp as round-trippable UTC text.
        /// </summary>
        private static string FormatUtc(DateTime dateTimeUtc) =>
            dateTimeUtc.ToString(UtcTimestampFormat, CultureInfo.InvariantCulture);

        /// <summary>
        /// Maps <see cref="MessageType"/> to the text the database stores.
        /// </summary>
        /// <remarks>
        /// Mapped explicitly rather than by <see cref="Enum.ToString()"/>, so the stored values
        /// depend on neither the enum's numeric ordering nor its member names. Renaming a member
        /// then becomes a compile-time decision about the database rather than a silent change to
        /// data already written.
        /// </remarks>
        private static string FormatMessageType(MessageType messageType) => messageType switch
        {
            MessageType.User => UserMessageTypeText,
            MessageType.System => SystemMessageTypeText,
            _ => throw new ArgumentOutOfRangeException(
                nameof(messageType),
                messageType,
                "There is no stored representation for this message type."),
        };

        /// <summary>
        /// Converts an optional value to what the parameter should carry, so that a null stays null
        /// in the database instead of becoming an empty string.
        /// </summary>
        private static object ToDatabaseValue(string? value) => value ?? (object)DBNull.Value;
    }
}
