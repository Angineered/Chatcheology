using Chatcheology.Core.Importing;
using Chatcheology.Core.Models;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Workspace.WorkspaceTestData;

namespace Chatcheology.Data.Tests.Workspace
{
    /// <summary>
    /// Proves the whole path: synthetic export text, through the real parser, into a fresh workspace
    /// database, then read back out of SQLite.
    /// </summary>
    /// <remarks>
    /// The export text is an inline synthetic fixture rather than a file, so this project does not
    /// depend on another test project's output directory and the core parser fixture does not have to
    /// change to serve persistence tests.
    /// <para>
    /// No real export, participant or media is involved. The real archive is not imported anywhere in
    /// this phase.
    /// </para>
    /// </remarks>
    public class ParserToWorkspaceTests
    {
        [Fact]
        public void SyntheticExport_StoresTheExpectedRowCounts()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            ImportSyntheticExport(workspace);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(1, CountRows(connection, "ImportSource"));
            Assert.Equal(1, CountRows(connection, "Conversation"));
            Assert.Equal(2, CountRows(connection, "Participant"));
            Assert.Equal(2, CountRows(connection, "ConversationParticipant"));
            Assert.Equal(5, CountRows(connection, "Message"));
        }

        [Fact]
        public void SyntheticExport_StoresSequenceNumbersOneThroughFiveInSourceOrder()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            ImportSyntheticExport(workspace);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            var rows = ReadMessages(connection);

            Assert.Equal(
                new[] { 1, 2, 3, 4, 5 },
                rows.Select(row => row.SequenceNumber).ToArray());
        }

        [Fact]
        public void SyntheticExport_PreservesSourceLineRanges()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            ImportSyntheticExport(workspace);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            var rows = ReadMessages(connection);

            // Message 3 spans fixture lines 3 and 4; every other message is one line.
            Assert.Equal(
                new[] { (1, 1), (2, 2), (3, 4), (5, 5), (6, 6) },
                rows.Select(row => (row.SourceLineStart, row.SourceLineEnd)).ToArray());
        }

        [Fact]
        public void SyntheticExport_AttributesEveryMessageToTheRightConversationParticipant()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            var result = ImportSyntheticExport(workspace);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            // Joined through ConversationParticipant, so this also demonstrates that every sender is
            // resolved via its membership of this conversation rather than via Participant alone.
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT m.SequenceNumber, p.DisplayName
                FROM Message m
                JOIN ConversationParticipant cp
                    ON cp.ConversationID = m.ConversationID
                    AND cp.ParticipantID = m.SenderParticipantID
                JOIN Participant p
                    ON p.ParticipantID = cp.ParticipantID
                WHERE m.ConversationID = $conversationID
                ORDER BY m.SequenceNumber;
                """;

            command.Parameters.AddWithValue("$conversationID", result.ConversationID);

            var senders = new List<(long SequenceNumber, string DisplayName)>();

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    senders.Add((reader.GetInt64(0), reader.GetString(1)));
                }
            }

            Assert.Equal(
                new[]
                {
                    (1L, "Alex"),
                    (2L, "Sam"),
                    (3L, "Alex"),
                    (4L, "Sam"),
                    (5L, "Alex"),
                },
                senders.ToArray());
        }

        [Fact]
        public void SyntheticExport_StoresMessageTypeAsReadableTextForEveryMessage()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            ImportSyntheticExport(workspace);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            var rows = ReadMessages(connection);

            Assert.All(rows, row => Assert.Equal("User", row.MessageType));

            // Never the enum's numeric value.
            Assert.Equal(
                0,
                ScalarLong(connection, "SELECT COUNT(*) FROM Message WHERE MessageType IN ('0', '1');"));
        }

        [Fact]
        public void SyntheticExport_StoresEveryTimestampAsLocalWallClockText()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            ImportSyntheticExport(workspace);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            var rows = ReadMessages(connection);

            Assert.Equal(
                new[]
                {
                    "2026-01-05T14:03:00",
                    "2026-01-05T14:03:00",
                    "2026-01-05T14:04:00",
                    "2026-01-05T14:05:00",
                    "2026-01-05T14:06:00",
                },
                rows.Select(row => row.MessageDateTimeLocal).ToArray());

            // The import supplied Africa/Johannesburg as source context, and it stayed metadata.
            Assert.Equal(
                SourceTimeZoneID,
                ScalarText(connection, "SELECT SourceTimeZoneID FROM ImportSource;"));

            Assert.All(
                rows,
                row => Assert.DoesNotContain("Z", row.MessageDateTimeLocal, StringComparison.Ordinal));
            Assert.All(
                rows,
                row => Assert.DoesNotContain("+", row.MessageDateTimeLocal, StringComparison.Ordinal));
        }

        [Fact]
        public void SyntheticExport_KeepsTheMediaPlaceholderAsMessageContent()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            ImportSyntheticExport(workspace);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM Message WHERE MessageContent = $mediaPlaceholderContent;";
            command.Parameters.AddWithValue(
                "$mediaPlaceholderContent", ParsedMessage.MediaPlaceholderContent);

            // Counted from the content itself. There is no IsMediaPlaceholder column to drift from
            // it, and no Attachment table in this phase.
            Assert.Equal(1L, (long)command.ExecuteScalar()!);

            Assert.False(ColumnExists(connection, "Message", "IsMediaPlaceholder"));
            Assert.False(TableExists(connection, "Attachment"));

            var rows = ReadMessages(connection);

            Assert.Equal(ParsedMessage.MediaPlaceholderContent, rows[3].MessageContent);
        }

        [Fact]
        public void SyntheticExport_PreservesMultilineMessageContentAndRawContent()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            ImportSyntheticExport(workspace);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            var multiline = ReadMessages(connection)[2];

            Assert.Equal("This message has\na second line.", multiline.MessageContent);
            Assert.Equal(
                "2026/01/05, 14:04 - Alex: This message has\na second line.",
                multiline.RawContent);
        }

        [Fact]
        public void SyntheticExportWithSystemMessage_StoresNoSenderAndCreatesNoExtraParticipant()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            ImportExport(workspace, SyntheticExportWithSystemMessage);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            var rows = ReadMessages(connection);

            Assert.Equal(3, rows.Count);
            Assert.Equal(new[] { "User", "System", "User" }, rows.Select(row => row.MessageType).ToArray());

            var systemMessage = rows[1];

            Assert.Equal("System", systemMessage.MessageType);
            Assert.Null(systemMessage.SenderParticipantID);
            Assert.Equal(
                "Messages and calls are end-to-end encrypted.",
                systemMessage.MessageContent);

            // Only the two participant senders exist; the system notice created nobody.
            Assert.Equal(2, CountRows(connection, "Participant"));
            Assert.Equal(2, CountRows(connection, "ConversationParticipant"));
        }

        private static WorkspaceImportResult ImportSyntheticExport(
            TemporaryWorkspaceDatabase workspace) =>
            ImportExport(workspace, SyntheticExport);

        private static WorkspaceImportResult ImportExport(
            TemporaryWorkspaceDatabase workspace,
            string exportText)
        {
            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            var parser = new WhatsAppAndroidChatParser();

            using var reader = new StringReader(exportText);
            var messages = parser.Parse(reader);

            return new WorkspaceImporter().Import(
                workspace.DatabasePath, CreateRequest(messages));
        }

        private static List<MessageRow> ReadMessages(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    SequenceNumber,
                    MessageDateTimeLocal,
                    SenderParticipantID,
                    MessageType,
                    MessageContent,
                    RawContent,
                    SourceLineStart,
                    SourceLineEnd
                FROM Message
                ORDER BY SequenceNumber;
                """;

            using var reader = command.ExecuteReader();

            var rows = new List<MessageRow>();

            while (reader.Read())
            {
                rows.Add(new MessageRow
                {
                    SequenceNumber = reader.GetInt32(0),
                    MessageDateTimeLocal = reader.GetString(1),
                    SenderParticipantID = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    MessageType = reader.GetString(3),
                    MessageContent = reader.GetString(4),
                    RawContent = reader.GetString(5),
                    SourceLineStart = reader.GetInt32(6),
                    SourceLineEnd = reader.GetInt32(7),
                });
            }

            return rows;
        }

        /// <summary>A message as it was actually stored.</summary>
        private sealed class MessageRow
        {
            internal required int SequenceNumber { get; init; }

            internal required string MessageDateTimeLocal { get; init; }

            internal required long? SenderParticipantID { get; init; }

            internal required string MessageType { get; init; }

            internal required string MessageContent { get; init; }

            internal required string RawContent { get; init; }

            internal required int SourceLineStart { get; init; }

            internal required int SourceLineEnd { get; init; }
        }
    }
}
