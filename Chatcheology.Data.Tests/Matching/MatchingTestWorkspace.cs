using System.Globalization;
using Chatcheology.Data.Tests.Workspace;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Matching
{
    /// <summary>
    /// A synthetic schema-v2 workspace built row by row for the matching tests.
    /// </summary>
    /// <remarks>
    /// Every value here is fictional: two invented participants, invented hashes, and media paths
    /// that name nothing real. No recovery path, real file name, real hash or real chat content
    /// appears in this project.
    /// <para>
    /// Rows are inserted with direct SQL rather than through the import and inventory services,
    /// because these tests need media shapes those services would never produce on demand — one
    /// payload surviving on three sources, a copy dated the day before a message, a source whose
    /// files record no direction at all.
    /// </para>
    /// </remarks>
    internal sealed class MatchingTestWorkspace : IDisposable
    {
        /// <summary>The one conversation these tests analyse.</summary>
        internal const long ConversationID = 1;

        /// <summary>The participant a test names as the local, exporting user.</summary>
        internal const long LocalParticipantID = 1;

        /// <summary>The other participant in the conversation.</summary>
        internal const long OtherParticipantID = 2;

        /// <summary>A second conversation, for proving a participant of it is rejected here.</summary>
        internal const long OtherConversationID = 2;

        /// <summary>The participant who belongs only to that second conversation.</summary>
        internal const long OutsiderParticipantID = 3;

        private const string ImportedDateTimeUtcText = "2026-08-17T16:00:00.0000000Z";

        private readonly TemporaryWorkspaceDatabase _workspace = new();

        private SqliteConnection? _connection;
        private int _sequenceNumber;
        private int _fileNumber;

        internal MatchingTestWorkspace()
        {
            WorkspaceDatabase.Initialise(_workspace.DatabasePath);

            _connection = WorkspaceDatabase.OpenConnection(_workspace.DatabasePath);

            Execute(
                $"""
                INSERT INTO ImportSource (SourceType, DisplayName, ImportedDateTimeUtc)
                VALUES ('SyntheticExport', 'Synthetic export', '{ImportedDateTimeUtcText}');

                INSERT INTO Conversation (Title, CreatedDateTimeUtc)
                VALUES ('Alex and Sam', '{ImportedDateTimeUtcText}');

                INSERT INTO Conversation (Title, CreatedDateTimeUtc)
                VALUES ('Alex and Robin', '{ImportedDateTimeUtcText}');

                INSERT INTO Participant (DisplayName) VALUES ('Alex');
                INSERT INTO Participant (DisplayName) VALUES ('Sam');
                INSERT INTO Participant (DisplayName) VALUES ('Robin');

                INSERT INTO ConversationParticipant (ConversationID, ParticipantID)
                VALUES ({ConversationID}, {LocalParticipantID});

                INSERT INTO ConversationParticipant (ConversationID, ParticipantID)
                VALUES ({ConversationID}, {OtherParticipantID});

                INSERT INTO ConversationParticipant (ConversationID, ParticipantID)
                VALUES ({OtherConversationID}, {OutsiderParticipantID});
                """);
        }

        /// <summary>The workspace path to hand to the service under test.</summary>
        internal string DatabasePath => _workspace.DatabasePath;

        /// <summary>The directory holding the workspace, so a test can look beside the file.</summary>
        internal string DirectoryPath => _workspace.DirectoryPath;

        /// <summary>
        /// Adds a message and the one unresolved attachment hanging off it.
        /// </summary>
        /// <param name="messageDate">The message's local calendar date.</param>
        /// <param name="senderParticipantID">
        /// The sender, or null for a system message that carries none.
        /// </param>
        /// <returns>The attachment's identifier.</returns>
        internal long AddMediaAttachment(
            DateOnly messageDate,
            long? senderParticipantID = LocalParticipantID,
            long conversationID = ConversationID,
            string time = "14:03:00")
        {
            var messageID = AddMessage(messageDate, senderParticipantID, conversationID, time);

            Execute(
                $"""
                INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus)
                VALUES ({messageID}, 1, 'Unresolved');
                """);

            return ScalarLong("SELECT MAX(AttachmentID) FROM Attachment;");
        }

        /// <summary>Adds a message with no attachment.</summary>
        internal long AddMessage(
            DateOnly messageDate,
            long? senderParticipantID = LocalParticipantID,
            long conversationID = ConversationID,
            string time = "14:03:00")
        {
            _sequenceNumber++;

            var messageType = senderParticipantID is null ? "System" : "User";
            var sender = senderParticipantID?.ToString(CultureInfo.InvariantCulture) ?? "NULL";
            var content = senderParticipantID is null ? "System notice" : "<Media omitted>";

            Execute(
                $"""
                INSERT INTO Message (
                    ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                    SenderParticipantID, MessageType, MessageContent, RawContent,
                    SourceLineStart, SourceLineEnd)
                VALUES (
                    {conversationID}, 1, {_sequenceNumber}, '{FormatDate(messageDate)}T{time}',
                    {sender}, '{messageType}', '{content}', 'raw',
                    {_sequenceNumber}, {_sequenceNumber});
                """);

            return ScalarLong("SELECT MAX(MessageID) FROM Message;");
        }

        /// <summary>Writes a message timestamp exactly as given, however malformed.</summary>
        internal long AddMessageWithRawTimestamp(string storedTimestamp, bool withAttachment = true)
        {
            _sequenceNumber++;

            Execute(
                $"""
                INSERT INTO Message (
                    ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                    SenderParticipantID, MessageType, MessageContent, RawContent,
                    SourceLineStart, SourceLineEnd)
                VALUES (
                    {ConversationID}, 1, {_sequenceNumber}, '{storedTimestamp}',
                    {LocalParticipantID}, 'User', '<Media omitted>', 'raw',
                    {_sequenceNumber}, {_sequenceNumber});
                """);

            var messageID = ScalarLong("SELECT MAX(MessageID) FROM Message;");

            if (withAttachment)
            {
                Execute(
                    $"""
                    INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus)
                    VALUES ({messageID}, 1, 'Unresolved');
                    """);
            }

            return messageID;
        }

        /// <summary>Registers a media source. Its root names nothing real.</summary>
        internal long AddMediaSource(string displayName = "Synthetic source")
        {
            Execute(
                $"""
                INSERT INTO MediaSource (DisplayName, SourceType, RootPath, ImportedDateTimeUtc)
                VALUES ('{displayName}', 'SyntheticMediaDirectory', 'MediaRoot/{displayName}',
                        '{ImportedDateTimeUtcText}');
                """);

            return ScalarLong("SELECT MAX(MediaSourceID) FROM MediaSource;");
        }

        /// <summary>Adds a unique payload.</summary>
        internal long AddMediaAsset(
            string sha256, string mediaType = "Image", long sizeBytes = 1024)
        {
            Execute(
                $"""
                INSERT INTO MediaAsset (SHA256, MediaType, SizeBytes)
                VALUES ('{sha256}', '{mediaType}', {sizeBytes});
                """);

            return ScalarLong("SELECT MAX(MediaAssetID) FROM MediaAsset;");
        }

        /// <summary>
        /// Adds one physical copy of <paramref name="mediaAssetID"/> and links it to that asset.
        /// </summary>
        /// <param name="fileDate">The naming-derived date, or null for a copy carrying none.</param>
        /// <param name="isSent">
        /// <c>true</c> beneath a <c>Sent</c> directory, <c>false</c> for a source with
        /// <c>Sent</c> structure where this copy is not beneath it, null where the source records
        /// no direction at all.
        /// </param>
        /// <param name="storedSHA256">
        /// What the file row records, when a test needs it to differ from the asset's. An empty
        /// string writes a null hash: a file discovery has found but hashing has not yet reached.
        /// </param>
        internal long AddMediaFile(
            long mediaSourceID,
            long? mediaAssetID,
            string assetSHA256,
            DateOnly? fileDate = null,
            bool? isSent = null,
            string mediaType = "Image",
            long sizeBytes = 1024,
            string? storedSHA256 = null,
            bool link = true)
        {
            _fileNumber++;

            var hash = storedSHA256 is null
                ? $"'{assetSHA256}'"
                : storedSHA256.Length == 0 ? "NULL" : $"'{storedSHA256}'";

            var storedFileDate = fileDate is { } date ? $"'{FormatDate(date)}'" : "NULL";
            var storedIsSent = isSent is { } sent ? (sent ? "1" : "0") : "NULL";

            Execute(
                $"""
                INSERT INTO MediaFile (
                    MediaSourceID, RelativePath, FileName, Extension, SizeBytes, SHA256,
                    MediaType, FileDate, IsSent)
                VALUES (
                    {mediaSourceID}, 'folder/file-{_fileNumber}.bin', 'file-{_fileNumber}.bin',
                    '.bin', {sizeBytes}, {hash}, '{mediaType}', {storedFileDate}, {storedIsSent});
                """);

            var mediaFileID = ScalarLong("SELECT MAX(MediaFileID) FROM MediaFile;");

            if (link && mediaAssetID is { } assetID)
            {
                Execute(
                    $"""
                    INSERT INTO MediaAssetFile (MediaAssetID, MediaFileID)
                    VALUES ({assetID}, {mediaFileID});
                    """);
            }

            return mediaFileID;
        }

        /// <summary>
        /// Adds an asset with one dated copy on <paramref name="fileDate"/>, the common shape.
        /// </summary>
        internal long AddAssetWithCopy(
            long mediaSourceID,
            string sha256,
            DateOnly? fileDate,
            bool? isSent = null,
            string mediaType = "Image",
            long sizeBytes = 1024)
        {
            var mediaAssetID = AddMediaAsset(sha256, mediaType, sizeBytes);

            AddMediaFile(
                mediaSourceID, mediaAssetID, sha256, fileDate, isSent, mediaType, sizeBytes);

            return mediaAssetID;
        }

        /// <summary>
        /// Runs SQL with foreign keys turned off, for building a state the schema forbids.
        /// </summary>
        /// <remarks>
        /// SQLite enforces foreign keys per connection, so a workspace written by a tool with them
        /// disabled can hold a link to an asset that does not exist. That is exactly the state the
        /// analysis has to refuse, and this is the only way to produce it.
        /// </remarks>
        internal void ExecuteWithoutForeignKeys(string sql)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                ForeignKeys = false,
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        /// <summary>Runs SQL against the building connection.</summary>
        internal void Execute(string sql)
        {
            using var command = RequireConnection().CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        /// <summary>Reads one integer from the building connection.</summary>
        internal long ScalarLong(string sql)
        {
            using var command = RequireConnection().CreateCommand();
            command.CommandText = sql;

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        /// <summary>Reads one integer through a fresh read-only connection.</summary>
        internal long ScalarLongReadOnly(string sql)
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Closes the building connection and clears the pool, so the file can be hashed, copied or
        /// deleted without a live handle on it.
        /// </summary>
        internal void CloseBuildingConnection()
        {
            _connection?.Dispose();
            _connection = null;

            SqliteConnection.ClearAllPools();
        }

        /// <summary>How the workspace writes a calendar date.</summary>
        internal static string FormatDate(DateOnly date) =>
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public void Dispose()
        {
            _connection?.Dispose();
            _connection = null;

            _workspace.Dispose();
        }

        private SqliteConnection RequireConnection() =>
            _connection
            ?? throw new InvalidOperationException(
                "The building connection has been closed. Build the workspace before closing it.");
    }
}
