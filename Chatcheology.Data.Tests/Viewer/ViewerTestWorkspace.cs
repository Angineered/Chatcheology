using System.Globalization;
using Chatcheology.Data.Tests.Matching;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Viewer
{
    /// <summary>
    /// A synthetic workspace shaped for the conversation viewer: messages with real text, chosen
    /// timestamps, and as many attachments per message as a test needs.
    /// </summary>
    /// <remarks>
    /// Built on <see cref="MatchingTestWorkspace"/>, which already creates the schema, two
    /// conversations, three fictional participants and every media-inventory helper. Only the
    /// message writing is its own, because the matching fixtures deliberately give every user
    /// message the media-placeholder text and the viewer needs messages that can be told apart.
    /// <para>
    /// This owns the sequence numbering, so tests here do not call
    /// <see cref="MatchingTestWorkspace.AddMessage"/> or
    /// <see cref="MatchingTestWorkspace.AddMediaAttachment"/> — two counters over one unique
    /// constraint would collide.
    /// </para>
    /// <para>
    /// Everything is invented. No real participant, path, hash, file name or archive figure appears
    /// anywhere in this project.
    /// </para>
    /// </remarks>
    internal sealed class ViewerTestWorkspace : IDisposable
    {
        /// <summary>The conversation most viewer tests browse.</summary>
        internal const long ConversationID = MatchingTestWorkspace.ConversationID;

        /// <summary>A second conversation, for proving scoping.</summary>
        internal const long OtherConversationID = MatchingTestWorkspace.OtherConversationID;

        /// <summary>One participant of <see cref="ConversationID"/>.</summary>
        internal const long FirstParticipantID = MatchingTestWorkspace.LocalParticipantID;

        /// <summary>The other participant of <see cref="ConversationID"/>.</summary>
        internal const long SecondParticipantID = MatchingTestWorkspace.OtherParticipantID;

        /// <summary>The participant who belongs only to <see cref="OtherConversationID"/>.</summary>
        internal const long OtherConversationParticipantID =
            MatchingTestWorkspace.OutsiderParticipantID;

        private readonly MatchingTestWorkspace _workspace = new();

        private int _sequenceNumber;

        /// <summary>The workspace the viewer readers are pointed at.</summary>
        internal string DatabasePath => _workspace.DatabasePath;

        /// <summary>The directory holding the workspace.</summary>
        internal string DirectoryPath => _workspace.DirectoryPath;

        /// <summary>The underlying fixture, for its media-inventory helpers.</summary>
        internal MatchingTestWorkspace Workspace => _workspace;

        /// <summary>The sequence number the most recent message was written at.</summary>
        internal int LastSequenceNumber => _sequenceNumber;

        // -----------------------------------------------------------------------------------
        // Building.
        // -----------------------------------------------------------------------------------

        /// <summary>Adds one message from a participant.</summary>
        /// <returns>The message identifier.</returns>
        internal long AddMessage(
            string content,
            DateOnly date,
            string time = "09:00:00",
            long senderParticipantID = FirstParticipantID,
            long conversationID = ConversationID) =>
            Insert(conversationID, date, time, senderParticipantID, "User", content);

        /// <summary>Adds one senderless system message.</summary>
        internal long AddSystemMessage(
            string content,
            DateOnly date,
            string time = "09:00:00",
            long conversationID = ConversationID) =>
            Insert(conversationID, date, time, senderParticipantID: null, "System", content);

        /// <summary>Adds one message written with the timestamp text exactly as given.</summary>
        /// <remarks>
        /// For the cases where the point is the stored text itself — an out-of-order reading, or a
        /// spelling this build does not write.
        /// </remarks>
        internal long AddMessageWithStoredTimestamp(
            string storedTimestamp,
            string content = "Message",
            long senderParticipantID = FirstParticipantID,
            long conversationID = ConversationID)
        {
            _sequenceNumber++;

            _workspace.Execute(
                $"""
                INSERT INTO Message (
                    ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                    SenderParticipantID, MessageType, MessageContent, RawContent,
                    SourceLineStart, SourceLineEnd)
                VALUES (
                    {conversationID}, 1, {_sequenceNumber}, '{storedTimestamp}',
                    {senderParticipantID}, 'User', '{content}', 'raw',
                    {_sequenceNumber}, {_sequenceNumber});
                """);

            return _workspace.ScalarLong("SELECT MAX(MessageID) FROM Message;");
        }

        /// <summary>
        /// Adds <paramref name="count"/> attachments to a message, at ordinals 1..count.
        /// </summary>
        /// <returns>The attachment identifiers, in ordinal order.</returns>
        internal IReadOnlyList<long> AddAttachments(
            long messageID, int count = 1, long? resolvedMediaAssetID = null)
        {
            var identifiers = new List<long>(count);

            for (var ordinal = 1; ordinal <= count; ordinal++)
            {
                var status = resolvedMediaAssetID is null ? "Unresolved" : "Resolved";

                var asset = resolvedMediaAssetID is { } resolved
                    ? resolved.ToString(CultureInfo.InvariantCulture)
                    : "NULL";

                _workspace.Execute(
                    $"""
                    INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus, ResolvedMediaAssetID)
                    VALUES ({messageID}, {ordinal}, '{status}', {asset});
                    """);

                identifiers.Add(_workspace.ScalarLong("SELECT MAX(AttachmentID) FROM Attachment;"));
            }

            return identifiers;
        }

        /// <summary>Adds a run of plain messages one minute apart on one date.</summary>
        /// <returns>The message identifiers, in sequence order.</returns>
        internal IReadOnlyList<long> AddMessages(
            int count, DateOnly date, long conversationID = ConversationID)
        {
            var identifiers = new List<long>(count);

            for (var index = 0; index < count; index++)
            {
                var sender = index % 2 == 0 ? FirstParticipantID : SecondParticipantID;

                if (conversationID == OtherConversationID)
                {
                    sender = OtherConversationParticipantID;
                }

                identifiers.Add(
                    AddMessage(
                        $"Message {(index + 1).ToString(CultureInfo.InvariantCulture)}",
                        date,
                        Minute(index),
                        sender,
                        conversationID));
            }

            return identifiers;
        }

        /// <summary>A time within one day, for a run of messages that stays in order.</summary>
        internal static string Minute(int index) =>
            $"{(index / 60).ToString("D2", CultureInfo.InvariantCulture)}:" +
            $"{(index % 60).ToString("D2", CultureInfo.InvariantCulture)}:00";

        // -----------------------------------------------------------------------------------
        // Reading back.
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Closes the building connection and opens the workspace the way the viewer does.
        /// </summary>
        /// <remarks>
        /// The building connection is closed first because a viewer never has one: everything a
        /// test asserts after this call is asserted against a workspace nothing can write to.
        /// </remarks>
        internal SqliteConnection OpenReadOnly()
        {
            _workspace.CloseBuildingConnection();

            return WorkspaceDatabase.OpenReadOnlyConnection(DatabasePath);
        }

        /// <summary>Runs SQL against the building connection.</summary>
        internal void Execute(string sql) => _workspace.Execute(sql);

        /// <summary>Reads one integer through a fresh read-only connection.</summary>
        internal long ScalarLongReadOnly(string sql) => _workspace.ScalarLongReadOnly(sql);

        public void Dispose() => _workspace.Dispose();

        private long Insert(
            long conversationID,
            DateOnly date,
            string time,
            long? senderParticipantID,
            string messageType,
            string content)
        {
            _sequenceNumber++;

            var sender = senderParticipantID?.ToString(CultureInfo.InvariantCulture) ?? "NULL";

            _workspace.Execute(
                $"""
                INSERT INTO Message (
                    ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                    SenderParticipantID, MessageType, MessageContent, RawContent,
                    SourceLineStart, SourceLineEnd)
                VALUES (
                    {conversationID}, 1, {_sequenceNumber},
                    '{MatchingTestWorkspace.FormatDate(date)}T{time}',
                    {sender}, '{messageType}', '{content}', 'raw',
                    {_sequenceNumber}, {_sequenceNumber});
                """);

            return _workspace.ScalarLong("SELECT MAX(MessageID) FROM Message;");
        }
    }
}
