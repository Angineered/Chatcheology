using System.Globalization;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Viewer
{
    /// <summary>
    /// Reads conversations and bounded pages of their messages, for ordinary browsing.
    /// </summary>
    /// <remarks>
    /// <para><strong>Ordinary product reading, not evidence.</strong> This is the general "show me
    /// the conversation" reader. It deliberately does not live under the Phase 7 namespace, whose
    /// public surface is a narrow, asserted list of evidence and decision types; conversation
    /// browsing is neither.</para>
    /// <para><strong>Read-only.</strong> Every statement is a <c>SELECT</c>, and the connection is
    /// the caller's, so this works unchanged on one opened through
    /// <see cref="WorkspaceDatabase.OpenReadOnlyConnection"/> — which is how the viewer opens
    /// every workspace.</para>
    /// <para><strong>Order comes from <c>SequenceNumber</c>, never from timestamps.</strong>
    /// <c>UNIQUE (ConversationID, SequenceNumber)</c> is the authoritative conversation order and
    /// the only index that serves browsing. Paging is keyset paging on that key, so a page costs
    /// the same at either end of a long conversation; <c>OFFSET</c> would make a deep page cost the
    /// rows it skipped.</para>
    /// <para><strong>It says nothing about reconstruction.</strong> Attachments are returned as
    /// positions only. Whether one is resolved, to what, and why not, is
    /// <c>Phase7ReconstructionReader</c>'s question, and asking it twice is how two answers come to
    /// disagree.</para>
    /// </remarks>
    public sealed class ConversationReader
    {
        /// <summary>The page size the viewer uses unless told otherwise.</summary>
        public const int DefaultPageSize = 150;

        /// <summary>
        /// The largest page any caller may request.
        /// </summary>
        /// <remarks>
        /// A working-set bound, not a parameter-count guarantee. A page of this many messages may
        /// carry more attachments than that, because the schema permits several per message, so
        /// whoever batches attachment identifiers into another reader bounds that separately.
        /// </remarks>
        public const int MaximumPageSize = 500;

        /// <summary>
        /// Every conversation in the workspace, ordered by identifier.
        /// </summary>
        /// <remarks>
        /// Three statements whatever the workspace holds, and none of them reads a message body.
        /// A workspace with no conversation returns an empty list rather than throwing: nothing has
        /// gone wrong, there is simply nothing to open.
        /// </remarks>
        public IReadOnlyList<ConversationSummary> ReadConversations(SqliteConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);

            var participants = ReadParticipants(connection);
            var summaries = new List<ConversationSummary>();

            using var command = connection.CreateCommand();
            command.CommandText = ConversationsStatement;

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var conversationID = reader.GetInt64(0);

                summaries.Add(new ConversationSummary
                {
                    ConversationID = conversationID,
                    Title = reader.GetString(1),
                    MessageCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    FirstSequenceNumber = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    LastSequenceNumber = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    FirstMessageDateTimeLocal =
                        reader.IsDBNull(5) ? null : ReadStoredDateTime(reader, 5),
                    LastMessageDateTimeLocal =
                        reader.IsDBNull(6) ? null : ReadStoredDateTime(reader, 6),
                    Participants = participants.TryGetValue(conversationID, out var roster)
                        ? roster
                        : [],
                });
            }

            return summaries;
        }

        /// <summary>
        /// The first page of a conversation.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// There is no such conversation in this workspace.
        /// </exception>
        public ConversationPage ReadFirstPage(
            SqliteConnection connection, long conversationID, int pageSize) =>
            ReadPageFrom(connection, conversationID, FirstPossibleSequenceNumber, pageSize);

        /// <summary>
        /// The last page of a conversation.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// There is no such conversation in this workspace.
        /// </exception>
        public ConversationPage ReadLastPage(
            SqliteConnection connection, long conversationID, int pageSize)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ValidatePageSize(pageSize);
            RequireConversation(connection, conversationID);

            return BuildPage(
                connection,
                conversationID,
                ReadMessagesEndingAt(connection, conversationID, upperBound: null, pageSize));
        }

        /// <summary>
        /// The page beginning at <paramref name="startSequenceNumber"/>, or at the first message
        /// after it.
        /// </summary>
        /// <remarks>
        /// The start need not name a message. Jump-to-date hands back a sequence number that does,
        /// but a caller resuming from a remembered position deserves the nearest following message
        /// rather than an error.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// There is no such conversation in this workspace.
        /// </exception>
        public ConversationPage ReadPageFrom(
            SqliteConnection connection,
            long conversationID,
            int startSequenceNumber,
            int pageSize)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ValidateSequenceNumber(startSequenceNumber, nameof(startSequenceNumber));
            ValidatePageSize(pageSize);
            RequireConversation(connection, conversationID);

            using var command = connection.CreateCommand();
            command.CommandText = ForwardMessagesStatement;
            command.Parameters.AddWithValue("$conversationID", conversationID);
            command.Parameters.AddWithValue("$startSequenceNumber", startSequenceNumber);
            command.Parameters.AddWithValue("$pageSize", pageSize);

            return BuildPage(connection, conversationID, ReadMessageRows(command));
        }

        /// <summary>
        /// The page of messages immediately before <paramref name="beforeSequenceNumber"/>,
        /// returned ascending like every other page.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// There is no such conversation in this workspace.
        /// </exception>
        public ConversationPage ReadPageBefore(
            SqliteConnection connection,
            long conversationID,
            int beforeSequenceNumber,
            int pageSize)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ValidateSequenceNumber(beforeSequenceNumber, nameof(beforeSequenceNumber));
            ValidatePageSize(pageSize);
            RequireConversation(connection, conversationID);

            // Exclusive on the way in, inclusive on the way down. The column's own CHECK keeps
            // every stored sequence number above zero, so an upper bound of zero selects nothing
            // rather than wrapping into a negative comparison.
            var upperBound = beforeSequenceNumber - 1;

            return BuildPage(
                connection,
                conversationID,
                ReadMessagesEndingAt(connection, conversationID, upperBound, pageSize));
        }

        /// <summary>
        /// The sequence number of the first message in the conversation whose stored local
        /// wall-clock reading falls on or after <paramref name="localDate"/>, or
        /// <see langword="null"/> when no message does.
        /// </summary>
        /// <remarks>
        /// <para><strong>Navigation, not evidence.</strong> This finds somewhere to look. It
        /// establishes nothing about any message and is never a matching input.</para>
        /// <para><strong>Text comparison, deliberately.</strong> The stored form is fixed width and
        /// zero padded, so its <c>BINARY</c> text order is its chronological order, and a threshold
        /// built through <c>WorkspaceDateFormats</c> compares exactly. No row is parsed, and no
        /// second reading of the storage format exists to drift from the first.</para>
        /// <para><strong>No binary search.</strong> The answer is the smallest
        /// <c>SequenceNumber</c> among the qualifying messages, found by one bounded aggregate. A
        /// binary search over the sequence key would be faster and would assume the stored
        /// timestamps rise with it — which the schema does not promise, and which this project does
        /// not get to assume on a timestamp's behalf.</para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// There is no such conversation in this workspace.
        /// </exception>
        public int? FindFirstSequenceNumberOnOrAfter(
            SqliteConnection connection, long conversationID, DateOnly localDate)
        {
            ArgumentNullException.ThrowIfNull(connection);
            RequireConversation(connection, conversationID);

            var threshold = WorkspaceDateFormats.FormatMessageDateTimeLocal(
                localDate.ToDateTime(TimeOnly.MinValue));

            using var command = connection.CreateCommand();
            command.CommandText = FirstSequenceOnOrAfterStatement;
            command.Parameters.AddWithValue("$conversationID", conversationID);
            command.Parameters.AddWithValue("$threshold", threshold);

            var result = command.ExecuteScalar();

            return result is null or DBNull
                ? null
                : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }

        // -------------------------------------------------------------------------------------
        // Reading.
        // -------------------------------------------------------------------------------------

        private static Dictionary<long, List<ConversationParticipantSummary>> ReadParticipants(
            SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ParticipantsStatement;

            var byConversation = new Dictionary<long, List<ConversationParticipantSummary>>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var conversationID = reader.GetInt64(0);

                if (!byConversation.TryGetValue(conversationID, out var roster))
                {
                    roster = [];
                    byConversation[conversationID] = roster;
                }

                roster.Add(new ConversationParticipantSummary
                {
                    ParticipantID = reader.GetInt64(1),
                    DisplayName = reader.GetString(2),
                });
            }

            return byConversation;
        }

        /// <remarks>
        /// Walks backwards and reverses, so a previous page costs the same as a next page. Reading
        /// forwards from a computed start would have to know how many messages precede the bound,
        /// which is the count keyset paging exists to avoid.
        /// </remarks>
        private static List<MessageRow> ReadMessagesEndingAt(
            SqliteConnection connection, long conversationID, int? upperBound, int pageSize)
        {
            using var command = connection.CreateCommand();
            command.CommandText = BackwardMessagesStatement;
            command.Parameters.AddWithValue("$conversationID", conversationID);
            command.Parameters.AddWithValue(
                "$upperBound", upperBound ?? (object)int.MaxValue);
            command.Parameters.AddWithValue("$pageSize", pageSize);

            var rows = ReadMessageRows(command);
            rows.Reverse();

            return rows;
        }

        private static List<MessageRow> ReadMessageRows(SqliteCommand command)
        {
            var rows = new List<MessageRow>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                rows.Add(new MessageRow(
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    ReadStoredDateTime(reader, 2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    MessageTypeText.Parse(reader.GetString(5)),
                    reader.GetString(6)));
            }

            return rows;
        }

        /// <summary>
        /// Turns the message rows of a page into the page, attaching that page's attachments.
        /// </summary>
        private static ConversationPage BuildPage(
            SqliteConnection connection,
            long conversationID,
            List<MessageRow> rows)
        {
            if (rows.Count == 0)
            {
                return new ConversationPage
                {
                    ConversationID = conversationID,
                    Messages = [],
                    FirstSequenceNumber = null,
                    LastSequenceNumber = null,
                    HasEarlier = false,
                    HasLater = false,
                };
            }

            var first = rows[0].SequenceNumber;
            var last = rows[^1].SequenceNumber;

            var attachments = ReadAttachments(connection, conversationID, first, last);
            var (conversationFirst, conversationLast) = ReadBounds(connection, conversationID);

            var messages = new List<ConversationMessage>(rows.Count);

            foreach (var row in rows)
            {
                messages.Add(new ConversationMessage
                {
                    MessageID = row.MessageID,
                    ConversationID = conversationID,
                    SequenceNumber = row.SequenceNumber,
                    MessageDateTimeLocal = row.MessageDateTimeLocal,
                    SenderParticipantID = row.SenderParticipantID,
                    SenderDisplayName = row.SenderDisplayName,
                    MessageType = row.MessageType,
                    MessageContent = row.MessageContent,
                    Attachments = attachments.TryGetValue(row.MessageID, out var carried)
                        ? carried
                        : [],
                });
            }

            return new ConversationPage
            {
                ConversationID = conversationID,
                Messages = messages,
                FirstSequenceNumber = first,
                LastSequenceNumber = last,
                HasEarlier = conversationFirst is { } start && start < first,
                HasLater = conversationLast is { } end && end > last,
            };
        }

        /// <remarks>
        /// Bounded by the page's own sequence window rather than by its message identifiers, so the
        /// statement text is fixed and carries three parameters whatever the page holds.
        /// </remarks>
        private static Dictionary<long, List<ConversationAttachment>> ReadAttachments(
            SqliteConnection connection,
            long conversationID,
            int firstSequenceNumber,
            int lastSequenceNumber)
        {
            using var command = connection.CreateCommand();
            command.CommandText = AttachmentsStatement;
            command.Parameters.AddWithValue("$conversationID", conversationID);
            command.Parameters.AddWithValue("$firstSequenceNumber", firstSequenceNumber);
            command.Parameters.AddWithValue("$lastSequenceNumber", lastSequenceNumber);

            var byMessage = new Dictionary<long, List<ConversationAttachment>>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var messageID = reader.GetInt64(0);

                if (!byMessage.TryGetValue(messageID, out var carried))
                {
                    carried = [];
                    byMessage[messageID] = carried;
                }

                carried.Add(new ConversationAttachment
                {
                    AttachmentID = reader.GetInt64(1),
                    Ordinal = reader.GetInt32(2),
                });
            }

            return byMessage;
        }

        private static (int? First, int? Last) ReadBounds(
            SqliteConnection connection, long conversationID)
        {
            using var command = connection.CreateCommand();
            command.CommandText = BoundsStatement;
            command.Parameters.AddWithValue("$conversationID", conversationID);

            using var reader = command.ExecuteReader();

            if (!reader.Read() || reader.IsDBNull(0))
            {
                return (null, null);
            }

            return (reader.GetInt32(0), reader.GetInt32(1));
        }

        // -------------------------------------------------------------------------------------
        // Validation and plumbing.
        // -------------------------------------------------------------------------------------

        /// <summary>The smallest sequence number the column's own CHECK permits.</summary>
        private const int FirstPossibleSequenceNumber = 1;

        private static void ValidatePageSize(int pageSize)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaximumPageSize);
        }

        private static void ValidateSequenceNumber(int sequenceNumber, string parameterName) =>
            ArgumentOutOfRangeException.ThrowIfLessThan(
                sequenceNumber, FirstPossibleSequenceNumber, parameterName);

        /// <remarks>
        /// Checked rather than inferred from an empty result. A conversation that holds no message
        /// and a conversation identifier naming nothing both read as "no messages", and they are
        /// different facts: the first is a workspace state, the second is a caller mistake.
        /// </remarks>
        private static void RequireConversation(SqliteConnection connection, long conversationID)
        {
            using var command = connection.CreateCommand();
            command.CommandText = ConversationExistsStatement;
            command.Parameters.AddWithValue("$conversationID", conversationID);

            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1)
            {
                return;
            }

            throw new InvalidOperationException(
                $"There is no conversation {conversationID.ToString(CultureInfo.InvariantCulture)} " +
                $"in this workspace. Nothing has been read.");
        }

        /// <remarks>
        /// Refused rather than reinterpreted, for the reason the format's own owner gives: a date
        /// read under the wrong format is either an exception or, worse, a plausible wrong day.
        /// </remarks>
        private static DateTime ReadStoredDateTime(SqliteDataReader reader, int ordinal)
        {
            var stored = reader.GetString(ordinal);

            if (WorkspaceDateFormats.TryParseMessageDateTimeLocal(stored, out var value))
            {
                return value;
            }

            throw new InvalidOperationException(
                "A message carries a MessageDateTimeLocal that is not the local wall-clock format " +
                "this workspace writes. It is not reinterpreted under another format, because a " +
                "timestamp read the wrong way looks like an ordinary date. Nothing has been " +
                "displayed and the workspace is unchanged.");
        }

        private sealed record MessageRow(
            long MessageID,
            int SequenceNumber,
            DateTime MessageDateTimeLocal,
            long? SenderParticipantID,
            string? SenderDisplayName,
            Core.Models.MessageType MessageType,
            string MessageContent);

        // -------------------------------------------------------------------------------------
        // Statements.
        // -------------------------------------------------------------------------------------

        /// <remarks>
        /// The grouped subquery gives SQLite the per-conversation count and sequence bounds from
        /// one pass of <c>UNIQUE (ConversationID, SequenceNumber)</c>; the two joins back are then
        /// one index seek each for the boundary rows' timestamps. Correlating a subquery per
        /// message row would produce the same answer by reading the whole table twice.
        /// <para>
        /// Left joins throughout, so a conversation with no message is still listed — as one with
        /// no bounds and no dates.
        /// </para>
        /// </remarks>
        private const string ConversationsStatement =
            """
            SELECT
                c.ConversationID,
                c.Title,
                b.MessageCount,
                b.FirstSequenceNumber,
                b.LastSequenceNumber,
                f.MessageDateTimeLocal,
                l.MessageDateTimeLocal
            FROM Conversation AS c
            LEFT JOIN (
                SELECT
                    ConversationID,
                    COUNT(*)             AS MessageCount,
                    MIN(SequenceNumber)  AS FirstSequenceNumber,
                    MAX(SequenceNumber)  AS LastSequenceNumber
                FROM Message
                GROUP BY ConversationID
            ) AS b ON b.ConversationID = c.ConversationID
            LEFT JOIN Message AS f
                ON f.ConversationID = b.ConversationID
               AND f.SequenceNumber = b.FirstSequenceNumber
            LEFT JOIN Message AS l
                ON l.ConversationID = b.ConversationID
               AND l.SequenceNumber = b.LastSequenceNumber
            ORDER BY c.ConversationID;
            """;

        private const string ParticipantsStatement =
            """
            SELECT cp.ConversationID, p.ParticipantID, p.DisplayName
            FROM ConversationParticipant AS cp
            INNER JOIN Participant AS p ON p.ParticipantID = cp.ParticipantID
            ORDER BY cp.ConversationID, p.ParticipantID;
            """;

        /// <remarks>
        /// <c>LEFT JOIN</c>, because a system message has no sender and must still appear. The
        /// join needs no conversation predicate of its own: the composite foreign key
        /// <c>(ConversationID, SenderParticipantID)</c> already makes a sender a member of this
        /// conversation.
        /// </remarks>
        private const string ForwardMessagesStatement =
            """
            SELECT
                m.MessageID,
                m.SequenceNumber,
                m.MessageDateTimeLocal,
                m.SenderParticipantID,
                p.DisplayName,
                m.MessageType,
                m.MessageContent
            FROM Message AS m
            LEFT JOIN Participant AS p ON p.ParticipantID = m.SenderParticipantID
            WHERE m.ConversationID = $conversationID
              AND m.SequenceNumber >= $startSequenceNumber
            ORDER BY m.SequenceNumber
            LIMIT $pageSize;
            """;

        private const string BackwardMessagesStatement =
            """
            SELECT
                m.MessageID,
                m.SequenceNumber,
                m.MessageDateTimeLocal,
                m.SenderParticipantID,
                p.DisplayName,
                m.MessageType,
                m.MessageContent
            FROM Message AS m
            LEFT JOIN Participant AS p ON p.ParticipantID = m.SenderParticipantID
            WHERE m.ConversationID = $conversationID
              AND m.SequenceNumber <= $upperBound
            ORDER BY m.SequenceNumber DESC
            LIMIT $pageSize;
            """;

        /// <remarks>
        /// Ordered by message then ordinal then identifier, so the sequence a caller sees is total
        /// and repeatable even if a message ever carries two attachments at one ordinal, which the
        /// schema's unique constraint forbids but this ordering does not rely on.
        /// </remarks>
        private const string AttachmentsStatement =
            """
            SELECT a.MessageID, a.AttachmentID, a.Ordinal
            FROM Attachment AS a
            INNER JOIN Message AS m ON m.MessageID = a.MessageID
            WHERE m.ConversationID = $conversationID
              AND m.SequenceNumber >= $firstSequenceNumber
              AND m.SequenceNumber <= $lastSequenceNumber
            ORDER BY m.SequenceNumber, a.Ordinal, a.AttachmentID;
            """;

        private const string BoundsStatement =
            """
            SELECT MIN(SequenceNumber), MAX(SequenceNumber)
            FROM Message
            WHERE ConversationID = $conversationID;
            """;

        private const string FirstSequenceOnOrAfterStatement =
            """
            SELECT MIN(SequenceNumber)
            FROM Message
            WHERE ConversationID = $conversationID
              AND MessageDateTimeLocal >= $threshold;
            """;

        private const string ConversationExistsStatement =
            """
            SELECT COUNT(*) FROM Conversation WHERE ConversationID = $conversationID;
            """;
    }
}
