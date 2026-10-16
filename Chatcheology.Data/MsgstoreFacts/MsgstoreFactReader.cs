using System.Globalization;
using System.Text;
using Chatcheology.Data.Viewer;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.MsgstoreFacts
{
    /// <summary>
    /// Reads msgstore facts for a set of attachments. Read-only: it issues only <c>SELECT</c>
    /// statements and works on a read-only connection.
    /// </summary>
    /// <remarks>
    /// Two statements per read, whatever the number of attachments: the identifiers travel as one
    /// JSON array parameter, so there is no per-attachment query and no parameter-count limit.
    /// </remarks>
    public sealed class MsgstoreFactReader
    {
        /// <summary>
        /// The facts of every attachment on <paramref name="page"/>, and the low-quality copies
        /// recorded against them.
        /// </summary>
        public MsgstoreFactSet ReadForPage(SqliteConnection connection, ConversationPage page)
        {
            ArgumentNullException.ThrowIfNull(page);

            return Read(
                connection,
                page.Messages
                    .SelectMany(message => message.Attachments)
                    .Select(attachment => attachment.AttachmentID)
                    .ToList());
        }

        /// <summary>
        /// The facts of each of <paramref name="attachmentIDs"/> that has one, and for each of them
        /// the attachments recorded as its low-quality copies.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The workspace is not at the current schema version.
        /// </exception>
        public MsgstoreFactSet Read(SqliteConnection connection, IReadOnlyCollection<long> attachmentIDs)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(attachmentIDs);

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(connection, "a msgstore fact read");

            if (attachmentIDs.Count == 0)
            {
                return MsgstoreFactSet.Empty;
            }

            var json = Json(attachmentIDs);
            var facts = new Dictionary<long, AttachmentMsgstoreFact>();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = FactsStatement;
                command.Parameters.AddWithValue("$ids", json);

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    var fact = new AttachmentMsgstoreFact
                    {
                        AttachmentID = reader.GetInt64(0),
                        IsForwarded = reader.GetInt64(1) == 1,
                        ForwardScore = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                        LowQualityCopyOfAttachmentID = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                        SourceBatch = reader.GetString(4),
                        RecordedDateTimeUtc = DateTime.Parse(
                            reader.GetString(5),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind),
                    };

                    facts[fact.AttachmentID] = fact;
                }
            }

            var copies = new Dictionary<long, List<long>>();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = CopiesStatement;
                command.Parameters.AddWithValue("$ids", json);

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    var target = reader.GetInt64(0);

                    if (!copies.TryGetValue(target, out var list))
                    {
                        list = [];
                        copies[target] = list;
                    }

                    list.Add(reader.GetInt64(1));
                }
            }

            return new MsgstoreFactSet
            {
                Facts = facts,
                LowQualityCopiesByFullCopy = copies.ToDictionary(
                    pair => pair.Key, pair => (IReadOnlyList<long>)pair.Value),
            };
        }

        /// <summary>
        /// Whether the workspace records any attachment as a low-quality copy of another.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The workspace is not at the current schema version.
        /// </exception>
        public bool HasAnyLowQualityCopy(SqliteConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(connection, "a msgstore fact read");

            using var command = connection.CreateCommand();
            command.CommandText = AnyCopyStatement;

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }

        /// <summary>Every statement this reader issues, for tests that assert none of them writes.</summary>
        internal static IReadOnlyList<string> Statements =>
            [FactsStatement, CopiesStatement, AnyCopyStatement];

        private const string AnyCopyStatement =
            """
            SELECT EXISTS (
                SELECT 1 FROM AttachmentMsgstoreFact
                WHERE LowQualityCopyOfAttachmentID IS NOT NULL);
            """;

        private const string FactsStatement =
            """
            SELECT f.AttachmentID, f.IsForwarded, f.ForwardScore, f.LowQualityCopyOfAttachmentID,
                   f.SourceBatch, f.RecordedDateTimeUtc
            FROM AttachmentMsgstoreFact AS f
            WHERE f.AttachmentID IN (SELECT value FROM json_each($ids))
            ORDER BY f.AttachmentID;
            """;

        private const string CopiesStatement =
            """
            SELECT f.LowQualityCopyOfAttachmentID, f.AttachmentID
            FROM AttachmentMsgstoreFact AS f
            WHERE f.LowQualityCopyOfAttachmentID IN (SELECT value FROM json_each($ids))
            ORDER BY f.LowQualityCopyOfAttachmentID, f.AttachmentID;
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
