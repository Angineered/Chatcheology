using System.Globalization;
using System.Text;
using Chatcheology.Data.Viewer;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.MsgstoreFacts
{
    /// <summary>
    /// Reads text-message msgstore facts for a set of messages (CH-22). Read-only: it issues only
    /// <c>SELECT</c> statements and works on a read-only connection.
    /// </summary>
    /// <remarks>
    /// One statement per read, whatever the number of messages: the identifiers travel as one JSON
    /// array parameter, so there is no per-message query and no parameter-count limit.
    /// </remarks>
    public sealed class MessageMsgstoreFactReader
    {
        /// <summary>The facts of every message on <paramref name="page"/> that has one.</summary>
        /// <exception cref="InvalidOperationException">
        /// The workspace is not at the current schema version.
        /// </exception>
        public IReadOnlyDictionary<long, MessageMsgstoreFact> ReadForPage(
            SqliteConnection connection, ConversationPage page)
        {
            ArgumentNullException.ThrowIfNull(page);

            return Read(connection, page.Messages.Select(message => message.MessageID).ToList());
        }

        /// <summary>The facts of each of <paramref name="messageIDs"/> that has one.</summary>
        /// <exception cref="InvalidOperationException">
        /// The workspace is not at the current schema version.
        /// </exception>
        public IReadOnlyDictionary<long, MessageMsgstoreFact> Read(
            SqliteConnection connection, IReadOnlyCollection<long> messageIDs)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(messageIDs);

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(
                connection, "a text message msgstore fact read");

            var facts = new Dictionary<long, MessageMsgstoreFact>();

            if (messageIDs.Count == 0)
            {
                return facts;
            }

            using var command = connection.CreateCommand();
            command.CommandText = FactsStatement;
            command.Parameters.AddWithValue("$ids", Json(messageIDs));

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var fact = new MessageMsgstoreFact
                {
                    MessageID = reader.GetInt64(0),
                    IsForwarded = reader.GetInt64(1) == 1,
                    ForwardScore = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    SourceBatch = reader.GetString(3),
                    RecordedDateTimeUtc = DateTime.Parse(
                        reader.GetString(4),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind),
                };

                facts[fact.MessageID] = fact;
            }

            return facts;
        }

        /// <summary>Every statement this reader issues, for tests that assert none of them writes.</summary>
        internal static IReadOnlyList<string> Statements => [FactsStatement];

        private const string FactsStatement =
            """
            SELECT f.MessageID, f.IsForwarded, f.ForwardScore, f.SourceBatch, f.RecordedDateTimeUtc
            FROM MessageMsgstoreFact AS f
            WHERE f.MessageID IN (SELECT value FROM json_each($ids))
            ORDER BY f.MessageID;
            """;

        private static string Json(IEnumerable<long> identifiers)
        {
            var builder = new StringBuilder("[");
            var first = true;

            foreach (var identifier in identifiers.Distinct())
            {
                if (!first)
                {
                    builder.Append(',');
                }

                builder.Append(identifier.ToString(CultureInfo.InvariantCulture));
                first = false;
            }

            return builder.Append(']').ToString();
        }
    }
}
