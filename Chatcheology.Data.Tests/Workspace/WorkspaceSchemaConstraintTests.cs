using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Workspace.WorkspaceTestData;

namespace Chatcheology.Data.Tests.Workspace
{
    /// <summary>
    /// Tests the schema's own guarantees, by writing to the workspace directly rather than through
    /// the importer.
    /// </summary>
    /// <remarks>
    /// These go around the importer deliberately. The point is that the database refuses bad data
    /// whatever writes it, so a later phase — or a bug in a future importer — cannot quietly store a
    /// row the schema was supposed to make impossible.
    /// <para>
    /// Only decisions this project made are tested. SQLite's own correctness is not.
    /// </para>
    /// </remarks>
    public class WorkspaceSchemaConstraintTests
    {
        [Fact]
        public void ForeignKeysAreEnforcedRatherThanMerelyReportedAsEnabled()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var participantID = InsertParticipant(connection, "Alex");

            // ConversationID 9999 does not exist.
            var exception = Assert.Throws<SqliteException>(() => Execute(
                connection,
                $"INSERT INTO ConversationParticipant (ConversationID, ParticipantID) " +
                $"VALUES (9999, {participantID});"));

            Assert.Contains("FOREIGN KEY", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <remarks>
        /// The reason the sender relationship is a composite foreign key into
        /// <c>ConversationParticipant</c> rather than a plain reference to <c>Participant</c>.
        /// </remarks>
        [Fact]
        public void Message_SenderBelongingOnlyToAnotherConversation_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var importSourceID = InsertImportSource(connection);
            var conversationA = InsertConversation(connection, "Conversation A");
            var conversationB = InsertConversation(connection, "Conversation B");

            var participantID = InsertParticipant(connection, "Alex");
            InsertConversationParticipant(connection, conversationB, participantID);

            var exception = Assert.Throws<SqliteException>(() => InsertMessage(
                connection,
                conversationA,
                importSourceID,
                new MessageRow { SenderParticipantID = participantID }));

            Assert.Contains("FOREIGN KEY", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Message_SenderBelongingToTheSameConversation_IsAccepted()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var arrangement = ArrangeConversationWithParticipant(connection);

            InsertMessage(
                connection,
                arrangement.ConversationID,
                arrangement.ImportSourceID,
                new MessageRow { SenderParticipantID = arrangement.ParticipantID });

            Assert.Equal(1, CountRows(connection, "Message"));
        }

        [Fact]
        public void Message_DuplicateConversationAndSequenceNumber_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var arrangement = ArrangeConversationWithParticipant(connection);

            var row = new MessageRow
            {
                SequenceNumber = 1,
                SenderParticipantID = arrangement.ParticipantID,
            };

            InsertMessage(connection, arrangement.ConversationID, arrangement.ImportSourceID, row);

            Assert.Throws<SqliteException>(() => InsertMessage(
                connection, arrangement.ConversationID, arrangement.ImportSourceID, row));
        }

        /// <remarks>
        /// The same sequence number in a different conversation is legitimate: sequence numbers are
        /// per-conversation source ordering, not workspace-wide identifiers.
        /// </remarks>
        [Fact]
        public void Message_SameSequenceNumberInADifferentConversation_IsAccepted()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var first = ArrangeConversationWithParticipant(connection, "Conversation A", "Alex");
            var second = ArrangeConversationWithParticipant(connection, "Conversation B", "Sam");

            InsertMessage(
                connection,
                first.ConversationID,
                first.ImportSourceID,
                new MessageRow { SequenceNumber = 1, SenderParticipantID = first.ParticipantID });

            InsertMessage(
                connection,
                second.ConversationID,
                second.ImportSourceID,
                new MessageRow { SequenceNumber = 1, SenderParticipantID = second.ParticipantID });

            Assert.Equal(2, CountRows(connection, "Message"));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Message_SequenceNumberNotPositive_IsRejected(int sequenceNumber)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var arrangement = ArrangeConversationWithParticipant(connection);

            Assert.Throws<SqliteException>(() => InsertMessage(
                connection,
                arrangement.ConversationID,
                arrangement.ImportSourceID,
                new MessageRow
                {
                    SequenceNumber = sequenceNumber,
                    SenderParticipantID = arrangement.ParticipantID,
                }));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Message_SourceLineStartNotPositive_IsRejected(int sourceLineStart)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var arrangement = ArrangeConversationWithParticipant(connection);

            Assert.Throws<SqliteException>(() => InsertMessage(
                connection,
                arrangement.ConversationID,
                arrangement.ImportSourceID,
                new MessageRow
                {
                    SenderParticipantID = arrangement.ParticipantID,
                    SourceLineStart = sourceLineStart,
                    SourceLineEnd = 10,
                }));
        }

        [Fact]
        public void Message_SourceLineEndBeforeSourceLineStart_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var arrangement = ArrangeConversationWithParticipant(connection);

            Assert.Throws<SqliteException>(() => InsertMessage(
                connection,
                arrangement.ConversationID,
                arrangement.ImportSourceID,
                new MessageRow
                {
                    SenderParticipantID = arrangement.ParticipantID,
                    SourceLineStart = 10,
                    SourceLineEnd = 9,
                }));
        }

        [Fact]
        public void Message_UserTypeWithNullSender_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var arrangement = ArrangeConversationWithParticipant(connection);

            Assert.Throws<SqliteException>(() => InsertMessage(
                connection,
                arrangement.ConversationID,
                arrangement.ImportSourceID,
                new MessageRow { MessageType = "User", SenderParticipantID = null }));
        }

        [Fact]
        public void Message_SystemTypeWithSender_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var arrangement = ArrangeConversationWithParticipant(connection);

            Assert.Throws<SqliteException>(() => InsertMessage(
                connection,
                arrangement.ConversationID,
                arrangement.ImportSourceID,
                new MessageRow
                {
                    MessageType = "System",
                    SenderParticipantID = arrangement.ParticipantID,
                }));
        }

        /// <remarks>
        /// Because <c>SenderParticipantID</c> is null, SQLite's MATCH SIMPLE semantics leave the
        /// composite sender foreign key satisfied without any participant relationship existing.
        /// </remarks>
        [Fact]
        public void Message_SystemTypeWithNullSender_IsAccepted()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var arrangement = ArrangeConversationWithParticipant(connection);

            InsertMessage(
                connection,
                arrangement.ConversationID,
                arrangement.ImportSourceID,
                new MessageRow { MessageType = "System", SenderParticipantID = null });

            Assert.Equal(1, CountRows(connection, "Message"));
        }

        [Theory]
        [InlineData("user")]
        [InlineData("SYSTEM")]
        [InlineData("Unknown")]
        [InlineData("")]
        public void Message_UnsupportedMessageTypeText_IsRejected(string messageType)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var arrangement = ArrangeConversationWithParticipant(connection);

            Assert.Throws<SqliteException>(() => InsertMessage(
                connection,
                arrangement.ConversationID,
                arrangement.ImportSourceID,
                new MessageRow
                {
                    MessageType = messageType,
                    SenderParticipantID = arrangement.ParticipantID,
                }));
        }

        [Fact]
        public void Message_EmptyMessageContent_IsAccepted()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var arrangement = ArrangeConversationWithParticipant(connection);

            // The supported export format allows an empty message body, so the schema must not
            // reject one.
            InsertMessage(
                connection,
                arrangement.ConversationID,
                arrangement.ImportSourceID,
                new MessageRow
                {
                    SenderParticipantID = arrangement.ParticipantID,
                    MessageContent = string.Empty,
                });

            Assert.Equal(1, CountRows(connection, "Message"));
        }

        [Fact]
        public void Participant_DuplicateDisplayName_IsAccepted()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            // Two unrelated conversations may legitimately contain the same display name, so
            // DisplayName is deliberately not unique.
            InsertParticipant(connection, "Alex");
            InsertParticipant(connection, "Alex");

            Assert.Equal(2, CountRows(connection, "Participant"));
        }

        [Fact]
        public void ConversationParticipant_DuplicateRelationship_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var conversationID = InsertConversation(connection);
            var participantID = InsertParticipant(connection, "Alex");

            InsertConversationParticipant(connection, conversationID, participantID);

            Assert.Throws<SqliteException>(
                () => InsertConversationParticipant(connection, conversationID, participantID));
        }

        /// <remarks>
        /// Phase 1 adds no cascade. A parent still referenced by a message must not be deletable,
        /// which is SQLite's default restrictive behaviour.
        /// </remarks>
        [Fact]
        public void Conversation_DeletionWhileStillReferenced_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var arrangement = ArrangeConversationWithParticipant(connection);

            InsertMessage(
                connection,
                arrangement.ConversationID,
                arrangement.ImportSourceID,
                new MessageRow { SenderParticipantID = arrangement.ParticipantID });

            Assert.Throws<SqliteException>(() => Execute(
                connection,
                $"DELETE FROM Conversation WHERE ConversationID = {arrangement.ConversationID};"));
        }

        private static SqliteConnection Initialise(TemporaryWorkspaceDatabase workspace)
        {
            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            return WorkspaceDatabase.OpenConnection(workspace.DatabasePath);
        }

        private static Arrangement ArrangeConversationWithParticipant(
            SqliteConnection connection,
            string conversationTitle = ConversationTitle,
            string displayName = "Alex")
        {
            var importSourceID = InsertImportSource(connection);
            var conversationID = InsertConversation(connection, conversationTitle);
            var participantID = InsertParticipant(connection, displayName);

            InsertConversationParticipant(connection, conversationID, participantID);

            return new Arrangement(importSourceID, conversationID, participantID);
        }

        private static long InsertImportSource(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO ImportSource (SourceType, DisplayName, ImportedDateTimeUtc)
                VALUES ($sourceType, $displayName, $importedDateTimeUtc)
                RETURNING ImportSourceID;
                """;

            command.Parameters.AddWithValue("$sourceType", SourceType);
            command.Parameters.AddWithValue("$displayName", SourceDisplayName);
            command.Parameters.AddWithValue("$importedDateTimeUtc", ImportedDateTimeUtcText);

            return (long)command.ExecuteScalar()!;
        }

        private static long InsertConversation(
            SqliteConnection connection,
            string title = ConversationTitle)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO Conversation (Title, CreatedDateTimeUtc)
                VALUES ($title, $createdDateTimeUtc)
                RETURNING ConversationID;
                """;

            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$createdDateTimeUtc", ImportedDateTimeUtcText);

            return (long)command.ExecuteScalar()!;
        }

        private static long InsertParticipant(SqliteConnection connection, string displayName)
        {
            using var command = connection.CreateCommand();
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
            long conversationID,
            long participantID)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO ConversationParticipant (ConversationID, ParticipantID)
                VALUES ($conversationID, $participantID);
                """;

            command.Parameters.AddWithValue("$conversationID", conversationID);
            command.Parameters.AddWithValue("$participantID", participantID);

            command.ExecuteNonQuery();
        }

        private static void InsertMessage(
            SqliteConnection connection,
            long conversationID,
            long importSourceID,
            MessageRow row)
        {
            using var command = connection.CreateCommand();
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
            command.Parameters.AddWithValue("$sequenceNumber", row.SequenceNumber);
            command.Parameters.AddWithValue("$messageDateTimeLocal", row.MessageDateTimeLocal);
            command.Parameters.AddWithValue(
                "$senderParticipantID", row.SenderParticipantID ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$messageType", row.MessageType);
            command.Parameters.AddWithValue("$messageContent", row.MessageContent);
            command.Parameters.AddWithValue("$rawContent", row.RawContent);
            command.Parameters.AddWithValue("$sourceLineStart", row.SourceLineStart);
            command.Parameters.AddWithValue("$sourceLineEnd", row.SourceLineEnd);

            command.ExecuteNonQuery();
        }

        private readonly record struct Arrangement(
            long ImportSourceID,
            long ConversationID,
            long ParticipantID);

        /// <summary>
        /// A valid message row whose fields individual tests override one at a time, so each test
        /// shows only the thing it is about.
        /// </summary>
        private sealed class MessageRow
        {
            internal int SequenceNumber { get; init; } = 1;

            internal string MessageDateTimeLocal { get; init; } = MessageDateTimeText;

            internal long? SenderParticipantID { get; init; }

            internal string MessageType { get; init; } = "User";

            internal string MessageContent { get; init; } = "Hi Sam";

            internal string RawContent { get; init; } = "2026/01/05, 14:03 - Alex: Hi Sam";

            internal int SourceLineStart { get; init; } = 1;

            internal int SourceLineEnd { get; init; } = 1;
        }
    }
}
