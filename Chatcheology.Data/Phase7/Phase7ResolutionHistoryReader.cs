using System.Globalization;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// Reads an attachment's append-only resolution history, and the state replaying it produces.
    /// </summary>
    /// <remarks>
    /// <para><strong>Read-only.</strong> Every statement here is a <c>SELECT</c>, and a structural test
    /// asserts it.</para>
    /// <para><strong>Ordered by identifier, always explicitly.</strong> Both history statements state
    /// <c>ORDER BY ... AttachmentResolutionEventID ASC</c> in the text, and a structural test asserts
    /// that every read statement naming the resolution table does so and that no ordering clause
    /// anywhere in Stage 7.2 mentions <c>OccurredAtUtc</c>. Timestamps are descriptive; a clock cannot
    /// be allowed to change what a history means.</para>
    /// <para>
    /// The transaction-carrying overloads exist so the resolution service can replay inside its own
    /// write transaction, which is where a transition's pre-state has to be established. The public
    /// overloads are for caller-owned read connections, and pass no transaction.
    /// </para>
    /// </remarks>
    public sealed class Phase7ResolutionHistoryReader
    {
        /// <summary>
        /// One attachment's complete resolution history, ascending by event identifier.
        /// </summary>
        /// <returns>
        /// Every event ever recorded for the attachment. An empty list means no resolution decision has
        /// ever been recorded for it, which is a fact rather than an absence of information.
        /// </returns>
        public IReadOnlyList<Phase7ResolutionHistoryEntry> ReadHistory(
            SqliteConnection connection, long attachmentID)
        {
            ArgumentNullException.ThrowIfNull(connection);

            return ReadHistory(connection, null, attachmentID);
        }

        /// <summary>
        /// The state one attachment's history replays to.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The stored history is impossible. Nothing is normalised or repaired.
        /// </exception>
        public Phase7ResolutionProjection ReplayProjection(
            SqliteConnection connection, long attachmentID)
        {
            ArgumentNullException.ThrowIfNull(connection);

            return Phase7ResolutionReplay.Apply(ReadHistory(connection, null, attachmentID));
        }

        internal static IReadOnlyList<Phase7ResolutionHistoryEntry> ReadHistory(
            SqliteConnection connection, SqliteTransaction? transaction, long attachmentID)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = AttachmentHistoryStatement;
            command.Parameters.AddWithValue("$attachmentID", attachmentID);

            var history = new List<Phase7ResolutionHistoryEntry>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                history.Add(Read(reader));
            }

            return history;
        }

        /// <summary>
        /// Every attachment's history in one pass, grouped by attachment and ascending by event
        /// identifier within each group.
        /// </summary>
        /// <remarks>
        /// The workspace-wide verifier's input. One statement rather than one per attachment: replaying
        /// a whole workspace attachment by attachment would issue of the order of one query per
        /// decided attachment, which is exactly the shape the frozen performance rule forbids.
        /// <para>
        /// The composite ordering preserves event order within each attachment, which is all replay
        /// needs — it never compares identifiers across attachments.
        /// </para>
        /// </remarks>
        internal static IReadOnlyList<Phase7ResolutionHistoryEntry> ReadAllHistory(
            SqliteConnection connection, SqliteTransaction? transaction = null)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = AllHistoryStatement;

            var history = new List<Phase7ResolutionHistoryEntry>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                history.Add(Read(reader));
            }

            return history;
        }

        private static Phase7ResolutionHistoryEntry Read(SqliteDataReader reader) =>
            new()
            {
                AttachmentResolutionEventID = reader.GetInt64(0),
                AttachmentID = reader.GetInt64(1),
                EventType = Phase7DecisionText.ParseResolutionEventType(reader.GetString(2)),
                MediaAssetID = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                IndependentBasisType = reader.IsDBNull(4)
                    ? null
                    : Phase7DecisionText.ParseIndependentBasisType(reader.GetString(4)),
                IndependentBasisDescription = reader.IsDBNull(5) ? null : reader.GetString(5),
                RevocationReason = reader.IsDBNull(6) ? null : reader.GetString(6),
                SupersedesEventID = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                SourceRunID = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                ActorLabel = reader.IsDBNull(9) ? null : reader.GetString(9),
                OccurredAtUtc = DateTime.ParseExact(
                    reader.GetString(10),
                    WorkspaceDatabase.UtcTimestampFormat,
                    CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind),
            };

        /// <summary>
        /// Every statement this reader issues, for the structural tests that assert none of them writes
        /// and that each orders by event identifier.
        /// </summary>
        internal static IReadOnlyList<string> Statements =>
        [
            AttachmentHistoryStatement,
            AllHistoryStatement,
        ];

        private const string AttachmentHistoryStatement =
            """
            SELECT
                AttachmentResolutionEventID,
                AttachmentID,
                EventType,
                MediaAssetID,
                IndependentBasisType,
                IndependentBasisDescription,
                RevocationReason,
                SupersedesEventID,
                SourceRunID,
                ActorLabel,
                OccurredAtUtc
            FROM AttachmentResolutionEvent
            WHERE AttachmentID = $attachmentID
            ORDER BY AttachmentResolutionEventID ASC;
            """;

        private const string AllHistoryStatement =
            """
            SELECT
                AttachmentResolutionEventID,
                AttachmentID,
                EventType,
                MediaAssetID,
                IndependentBasisType,
                IndependentBasisDescription,
                RevocationReason,
                SupersedesEventID,
                SourceRunID,
                ActorLabel,
                OccurredAtUtc
            FROM AttachmentResolutionEvent
            ORDER BY AttachmentID, AttachmentResolutionEventID ASC;
            """;
    }
}
