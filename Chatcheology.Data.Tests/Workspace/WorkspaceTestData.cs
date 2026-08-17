using System.Globalization;
using Chatcheology.Core.Models;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Workspace
{
    /// <summary>
    /// Synthetic fixtures and small query helpers shared by the workspace tests.
    /// </summary>
    /// <remarks>
    /// Every value here is fictional. No real export, participant name, file name, path or media is
    /// referenced anywhere in this project.
    /// <para>
    /// The export text is assembled from an explicit array of lines rather than a multiline literal,
    /// so the physical line numbers the tests assert on are visible in the fixture itself and cannot
    /// be changed by the line endings this file happens to be stored with.
    /// </para>
    /// </remarks>
    internal static class WorkspaceTestData
    {
        internal const string SourceType = "WhatsAppAndroidTextExport";

        internal const string SourceDisplayName = "Synthetic Android export";

        internal const string OriginalFileName = "SyntheticChatAndroid.txt";

        /// <summary>A fictional hash. Nothing in this phase computes or verifies one.</summary>
        internal const string SHA256 =
            "0000000000000000000000000000000000000000000000000000000000000000";

        internal const string ConversationTitle = "Alex and Sam";

        /// <summary>
        /// A timezone identifier supplied as import metadata only. It is never applied to a message
        /// timestamp, and the tests assert exactly that.
        /// </summary>
        internal const string SourceTimeZoneID = "Africa/Johannesburg";

        /// <summary>
        /// The fixed UTC instant every test supplies as the import time.
        /// </summary>
        /// <remarks>
        /// Supplied rather than read from the clock, so the expected stored text can be asserted
        /// literally and no clock abstraction is needed.
        /// </remarks>
        internal static readonly DateTime ImportedDateTimeUtc =
            new(2026, 8, 17, 16, 0, 0, DateTimeKind.Utc);

        /// <summary>How <see cref="ImportedDateTimeUtc"/> must appear in the database.</summary>
        internal const string ImportedDateTimeUtcText = "2026-08-17T16:00:00.0000000Z";

        /// <summary>
        /// The default wall-clock timestamp for hand-built messages, matching the first fixture
        /// line. <see cref="DateTimeKind.Unspecified"/>, as the parser always produces.
        /// </summary>
        internal static readonly DateTime MessageDateTime = new(2026, 1, 5, 14, 3, 0);

        /// <summary>How <see cref="MessageDateTime"/> must appear in the database.</summary>
        internal const string MessageDateTimeText = "2026-01-05T14:03:00";

        /// <summary>
        /// Six physical lines producing five logical messages from two participants, one of them a
        /// media placeholder and one spanning two lines.
        /// </summary>
        private static readonly string[] SyntheticExportLines =
        [
            "2026/01/05, 14:03 - Alex: Hi Sam",           // line 1, message 1
            "2026/01/05, 14:03 - Sam: Hi Alex",           // line 2, message 2
            "2026/01/05, 14:04 - Alex: This message has", // line 3, message 3
            "a second line.",                             // line 4, message 3 continued
            "2026/01/05, 14:05 - Sam: <Media omitted>",   // line 5, message 4
            "2026/01/05, 14:06 - Alex: See you tomorrow", // line 6, message 5
        ];

        /// <summary>
        /// Three physical lines producing three logical messages, the middle one a WhatsApp system
        /// notice that carries no sender.
        /// </summary>
        private static readonly string[] SyntheticExportWithSystemMessageLines =
        [
            "2026/01/05, 14:03 - Alex: Hi Sam",
            "2026/01/05, 14:04 - Messages and calls are end-to-end encrypted.",
            "2026/01/05, 14:05 - Sam: Hi Alex",
        ];

        internal static string SyntheticExport => string.Join("\n", SyntheticExportLines);

        internal static string SyntheticExportWithSystemMessage =>
            string.Join("\n", SyntheticExportWithSystemMessageLines);

        /// <summary>
        /// Builds an otherwise-valid import request around <paramref name="messages"/>.
        /// </summary>
        internal static WorkspaceImportRequest CreateRequest(
            IReadOnlyList<ParsedMessage> messages,
            string? sourceTimeZoneID = SourceTimeZoneID,
            string? originalFileName = OriginalFileName,
            DateTime? importedDateTimeUtc = null) => new()
            {
                SourceType = SourceType,
                SourceDisplayName = SourceDisplayName,
                OriginalFileName = originalFileName,
                SHA256 = SHA256,
                ImportedDateTimeUtc = importedDateTimeUtc ?? ImportedDateTimeUtc,
                SourceTimeZoneID = sourceTimeZoneID,
                ConversationTitle = ConversationTitle,
                Messages = messages,
            };

        /// <summary>
        /// Builds a user message directly, for the cases a real export cannot produce — a duplicated
        /// sequence number, or a timestamp carrying the wrong <see cref="DateTimeKind"/>.
        /// </summary>
        internal static ParsedMessage CreateUserMessage(
            int sequenceNumber,
            string sender = "Alex",
            string content = "Hi Sam",
            DateTime? messageDateTime = null,
            int? sourceLineStart = null,
            int? sourceLineEnd = null)
        {
            var timestamp = messageDateTime ?? MessageDateTime;
            var start = sourceLineStart ?? sequenceNumber;

            return new ParsedMessage
            {
                SequenceNumber = sequenceNumber,
                MessageDateTime = timestamp,
                MessageType = MessageType.User,
                Sender = sender,
                MessageContent = content,
                RawContent = $"2026/01/05, 14:03 - {sender}: {content}",
                SourceLineStart = start,
                SourceLineEnd = sourceLineEnd ?? start,
            };
        }

        /// <summary>Builds a system message directly.</summary>
        internal static ParsedMessage CreateSystemMessage(
            int sequenceNumber,
            string content = "Messages and calls are end-to-end encrypted.",
            string? sender = null) => new()
            {
                SequenceNumber = sequenceNumber,
                MessageDateTime = MessageDateTime,
                MessageType = MessageType.System,
                Sender = sender,
                MessageContent = content,
                RawContent = $"2026/01/05, 14:03 - {content}",
                SourceLineStart = sequenceNumber,
                SourceLineEnd = sequenceNumber,
            };

        internal static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        internal static long ScalarLong(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        internal static string? ScalarText(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            return command.ExecuteScalar() as string;
        }

        /// <summary>
        /// Reads a text value that the test requires to be present, so callers can assert on a
        /// non-nullable string.
        /// </summary>
        internal static string ScalarRequiredText(SqliteConnection connection, string sql) =>
            ScalarText(connection, sql)
            ?? throw new InvalidOperationException($"Expected a non-null text value from: {sql}");

        internal static long CountRows(SqliteConnection connection, string tableName) =>
            ScalarLong(connection, $"SELECT COUNT(*) FROM {tableName};");

        internal static bool TableExists(SqliteConnection connection, string tableName) =>
            ScalarLong(
                connection,
                $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{tableName}';") == 1;

        internal static bool ColumnExists(
            SqliteConnection connection,
            string tableName,
            string columnName) =>
            ScalarLong(
                connection,
                $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name = '{columnName}';") == 1;
    }
}
