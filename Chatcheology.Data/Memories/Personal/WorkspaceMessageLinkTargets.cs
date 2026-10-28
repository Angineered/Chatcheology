using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Memories.Personal
{
    /// <summary>
    /// The messages of one workspace conversation, for message links: read through a short read-only
    /// connection per call (SQLite's own <c>Mode=ReadOnly</c>), so nothing here can write the workspace.
    /// </summary>
    public sealed class WorkspaceMessageLinkTargets : IMessageLinkTargets
    {
        /// <summary>The longest text preview the picker shows.</summary>
        public const int PreviewLength = 120;

        /// <summary>The most messages one day's picker lists.</summary>
        public const int MaxMessagesPerDay = 2000;

        private readonly string _workspacePath;
        private readonly long _conversationID;

        /// <summary>Reads conversation <paramref name="conversationID"/> of the workspace at <paramref name="workspacePath"/>.</summary>
        public WorkspaceMessageLinkTargets(string workspacePath, long conversationID)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

            _workspacePath = workspacePath;
            _conversationID = conversationID;
        }

        private const string Select =
            """
            SELECT m.SequenceNumber, m.MessageDateTimeLocal, p.DisplayName, m.MessageContent
            FROM Message m LEFT JOIN Participant p ON p.ParticipantID = m.SenderParticipantID
            """;

        /// <inheritdoc/>
        public bool Exists(int sequenceNumber) => Read(sequenceNumber) is not null;

        /// <inheritdoc/>
        public MessageLinkCandidate? Read(int sequenceNumber) => Query(command =>
        {
            command.CommandText = Select + " WHERE m.ConversationID = $c AND m.SequenceNumber = $s;";
            command.Parameters.AddWithValue("$c", _conversationID);
            command.Parameters.AddWithValue("$s", sequenceNumber);

            return Rows(command).FirstOrDefault();
        });

        /// <inheritdoc/>
        public IReadOnlyList<MessageLinkCandidate> ReadDay(DateOnly day) => Query(command =>
        {
            command.CommandText = Select +
                " WHERE m.ConversationID = $c AND m.MessageDateTimeLocal >= $from AND m.MessageDateTimeLocal < $to" +
                " ORDER BY m.SequenceNumber LIMIT $limit;";
            command.Parameters.AddWithValue("$c", _conversationID);
            command.Parameters.AddWithValue("$from", WorkspaceDateFormats.FormatMessageDateTimeLocal(day.ToDateTime(TimeOnly.MinValue)));
            command.Parameters.AddWithValue("$to", WorkspaceDateFormats.FormatMessageDateTimeLocal(day.AddDays(1).ToDateTime(TimeOnly.MinValue)));
            command.Parameters.AddWithValue("$limit", MaxMessagesPerDay);

            return (IReadOnlyList<MessageLinkCandidate>)Rows(command);
        });

        private static List<MessageLinkCandidate> Rows(SqliteCommand command)
        {
            var rows = new List<MessageLinkCandidate>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                if (!WorkspaceDateFormats.TryParseMessageDateTimeLocal(reader.GetString(1), out var time))
                {
                    throw new FormatException("A message time in the workspace is not in its stored form.");
                }

                rows.Add(new MessageLinkCandidate(
                    reader.GetInt32(0),
                    time,
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    Preview(reader.GetString(3))));
            }

            return rows;
        }

        private static string Preview(string text)
        {
            var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

            return line.Length <= PreviewLength
                ? line
                : line[..PreviewLength].TrimEnd() + "…";
        }

        private T Query<T>(Func<SqliteCommand, T> query)
        {
            var connection = WorkspaceDatabase.OpenReadOnlyConnection(_workspacePath);

            try
            {
                using var command = connection.CreateCommand();

                return query(command);
            }
            finally
            {
                connection.Dispose();
                SqliteConnection.ClearPool(connection);
            }
        }
    }
}
