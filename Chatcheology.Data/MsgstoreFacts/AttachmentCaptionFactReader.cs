using System.Globalization;
using System.Text;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.MsgstoreFacts
{
    /// <summary>
    /// Reads attachment captions for a set of attachments (CH-63, CH-71). Read-only: it issues only
    /// <c>SELECT</c> statements and works on a read-only connection.
    /// </summary>
    /// <remarks>
    /// One statement per read, whatever the number of attachments: the identifiers travel as one
    /// JSON array parameter, so there is no per-attachment query and no parameter-count limit.
    /// </remarks>
    public sealed class AttachmentCaptionFactReader
    {
        /// <summary>The caption of each of <paramref name="attachmentIDs"/> that has one.</summary>
        /// <exception cref="InvalidOperationException">
        /// The workspace is not at the current schema version.
        /// </exception>
        public IReadOnlyDictionary<long, AttachmentCaptionFact> Read(
            SqliteConnection connection, IReadOnlyCollection<long> attachmentIDs)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(attachmentIDs);

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(connection, "a caption fact read");

            var facts = new Dictionary<long, AttachmentCaptionFact>();

            if (attachmentIDs.Count == 0)
            {
                return facts;
            }

            using var command = connection.CreateCommand();
            command.CommandText = FactsStatement;
            command.Parameters.AddWithValue("$ids", Json(attachmentIDs));

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var fact = new AttachmentCaptionFact
                {
                    AttachmentID = reader.GetInt64(0),
                    CaptionText = reader.GetString(1),
                    FromPartner = reader.GetInt64(2) == 1,
                    SourceBatch = reader.GetString(3),
                    RecordedDateTimeUtc = DateTime.Parse(
                        reader.GetString(4),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind),
                };

                facts[fact.AttachmentID] = fact;
            }

            return facts;
        }

        /// <summary>Every statement this reader issues, for tests that assert none of them writes.</summary>
        internal static IReadOnlyList<string> Statements => [FactsStatement];

        private const string FactsStatement =
            """
            SELECT f.AttachmentID, f.CaptionText, f.FromPartner, f.SourceBatch, f.RecordedDateTimeUtc
            FROM AttachmentCaptionFact AS f
            WHERE f.AttachmentID IN (SELECT value FROM json_each($ids))
            ORDER BY f.AttachmentID;
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
