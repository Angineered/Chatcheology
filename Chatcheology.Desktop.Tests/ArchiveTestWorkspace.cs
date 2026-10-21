using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// A synthetic workspace an archive can be exported from (CH-18): one Restored attachment with a
    /// real file on disk, one reviewed as Missing, one Unmatched.
    /// </summary>
    /// <remarks>
    /// <para>The decisions go through the real Phase 7 services: a completed evidence run, a
    /// confirmation and a "reviewed, insufficient evidence" review. The media source's root and the
    /// folder archives are written into are private temporary folders, outside the repository, removed
    /// on dispose.</para>
    /// <para>Everything is invented: fictional participants, text and bytes. No real name, path, hash
    /// or archive figure appears in this project.</para>
    /// </remarks>
    internal sealed class ArchiveTestWorkspace : IDisposable
    {
        /// <summary>A word in both participants' names, so a test can look for any name at all.</summary>
        internal const string NameMarker = "Fictional";

        /// <summary>A phrase in the messages' text, so a test can look for message text.</summary>
        internal const string TextMarker = "Synthetic chatter";

        private const string Stamp = "2026-04-01T00:00:00.0000000Z";

        private static readonly byte[] Payload = Encoding.UTF8.GetBytes("synthetic payload one");

        private readonly string _root;

        internal ArchiveTestWorkspace()
        {
            _root = Path.Combine(Path.GetTempPath(), "Chatcheology.Desktop.Tests", Guid.NewGuid().ToString("N"));

            MediaRoot = Path.Combine(_root, "media-source");
            ExportsFolder = Path.Combine(_root, "exports");

            Directory.CreateDirectory(MediaRoot);
            Directory.CreateDirectory(ExportsFolder);

            DatabasePath = Path.Combine(_root, "workspace.db");

            WorkspaceDatabase.Initialise(DatabasePath);

            Build();

            RestoredAttachmentID = Scalar("SELECT AttachmentID FROM Attachment WHERE MessageID = 2;");
            MissingAttachmentID = Scalar("SELECT AttachmentID FROM Attachment WHERE MessageID = 3;");
            UnmatchedAttachmentID = Scalar("SELECT AttachmentID FROM Attachment WHERE MessageID = 4;");

            SqliteConnection.ClearAllPools();

            RunID = new Phase7EvidenceSnapshotService().Run(
                new Phase7RunRequest
                {
                    DatabasePath = DatabasePath,
                    ConversationID = 1,
                    LocalParticipantID = 1,
                }).RunID;

            new Phase7ResolutionService().Confirm(
                new Phase7ConfirmRequest
                {
                    DatabasePath = DatabasePath,
                    AttachmentID = RestoredAttachmentID,
                    MediaAssetID = 1,
                    IndependentBasisType = Phase7IndependentBasisType.HumanContentOrContextInspection,
                    IndependentBasisDescription = "Recognised the invented whiteboard from the invented meeting.",
                    SourceRunID = null,
                    ActorLabel = null,
                });

            Review(MissingAttachmentID);
        }

        /// <summary>The workspace file.</summary>
        internal string DatabasePath { get; }

        /// <summary>The recorded root of the media source; its one copy is a real file.</summary>
        internal string MediaRoot { get; }

        /// <summary>A folder outside the repository that archives may be written into.</summary>
        internal string ExportsFolder { get; }

        /// <summary>Everything this fixture created lives under here.</summary>
        internal string Root => _root;

        internal long RunID { get; }

        internal long RestoredAttachmentID { get; }

        internal long MissingAttachmentID { get; }

        internal long UnmatchedAttachmentID { get; }

        /// <summary>The one payload's size, which is the media total the confirmation shows.</summary>
        internal static int PayloadBytes => Payload.Length;

        /// <summary>Records a "reviewed, insufficient evidence" review through Phase 7.</summary>
        internal void Review(long attachmentID)
        {
            SqliteConnection.ClearAllPools();

            new Phase7ReviewService().Record(
                new Phase7ReviewRequest
                {
                    DatabasePath = DatabasePath,
                    AttachmentID = attachmentID,
                    SourceRunID = RunID,
                    ReviewNotes = null,
                    ActorLabel = null,
                });
        }

        /// <summary>Every entry beneath the exports folder, as relative paths.</summary>
        internal List<string> ExportsContent() =>
            Directory.EnumerateFileSystemEntries(ExportsFolder, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(ExportsFolder, path))
                .Order(StringComparer.Ordinal)
                .ToList();

        private void Build()
        {
            var hash = Convert.ToHexString(SHA256.HashData(Payload));
            var size = Payload.Length.ToString(CultureInfo.InvariantCulture);
            var root = MediaRoot.Replace("'", "''", StringComparison.Ordinal);

            Execute(
                $"""
                INSERT INTO ImportSource (SourceType, DisplayName, ImportedDateTimeUtc)
                VALUES ('SyntheticExport', 'Synthetic export', '{Stamp}');

                INSERT INTO Conversation (Title, CreatedDateTimeUtc)
                VALUES ('Ada and Bo', '{Stamp}');

                INSERT INTO Participant (DisplayName) VALUES ('Ada {NameMarker}');
                INSERT INTO Participant (DisplayName) VALUES ('Bo {NameMarker}');

                INSERT INTO ConversationParticipant (ConversationID, ParticipantID) VALUES (1, 1);
                INSERT INTO ConversationParticipant (ConversationID, ParticipantID) VALUES (1, 2);

                INSERT INTO MediaSource (DisplayName, SourceType, RootPath, ImportedDateTimeUtc)
                VALUES ('Synthetic store', 'SyntheticMediaDirectory', '{root}', '{Stamp}');

                INSERT INTO Message (
                    ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                    SenderParticipantID, MessageType, MessageContent, RawContent,
                    SourceLineStart, SourceLineEnd)
                VALUES (1, 1, 1, '2026-04-06T09:00:00', 1, 'User', '{TextMarker} before', 'raw', 1, 1);

                INSERT INTO Message (
                    ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                    SenderParticipantID, MessageType, MessageContent, RawContent,
                    SourceLineStart, SourceLineEnd)
                VALUES (1, 1, 2, '2026-04-06T09:01:00', 1, 'User', '<Media omitted>', 'raw', 2, 2);

                INSERT INTO Message (
                    ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                    SenderParticipantID, MessageType, MessageContent, RawContent,
                    SourceLineStart, SourceLineEnd)
                VALUES (1, 1, 3, '2026-04-06T09:02:00', 1, 'User', '<Media omitted>', 'raw', 3, 3);

                INSERT INTO Message (
                    ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                    SenderParticipantID, MessageType, MessageContent, RawContent,
                    SourceLineStart, SourceLineEnd)
                VALUES (1, 1, 4, '2026-04-06T09:03:00', 1, 'User', '<Media omitted>', 'raw', 4, 4);

                INSERT INTO Message (
                    ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                    SenderParticipantID, MessageType, MessageContent, RawContent,
                    SourceLineStart, SourceLineEnd)
                VALUES (1, 1, 5, '2026-04-06T09:04:00', 2, 'User', '{TextMarker} after', 'raw', 5, 5);

                INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus) VALUES (2, 1, 'Unresolved');
                INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus) VALUES (3, 1, 'Unresolved');
                INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus) VALUES (4, 1, 'Unresolved');

                INSERT INTO MediaAsset (SHA256, MediaType, SizeBytes) VALUES ('{hash}', 'Image', {size});

                INSERT INTO MediaFile (
                    MediaSourceID, RelativePath, FileName, Extension, SizeBytes, SHA256,
                    MediaType, FileDate, IsSent)
                VALUES (1, 'Sent/IMG-20260406-WA0001.jpg', 'IMG-20260406-WA0001.jpg', '.jpg', {size},
                        '{hash}', 'Image', '2026-04-06', 1);

                INSERT INTO MediaAssetFile (MediaAssetID, MediaFileID) VALUES (1, 1);
                """);

            var file = Path.Combine(MediaRoot, "Sent", "IMG-20260406-WA0001.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, Payload);
        }

        private void Execute(string sql)
        {
            using var connection = WorkspaceDatabase.OpenConnection(DatabasePath);
            using var command = connection.CreateCommand();

            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private long Scalar(string sql)
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(DatabasePath);
            using var command = connection.CreateCommand();

            command.CommandText = sql;

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A leaked handle shows up as a test failure elsewhere, not here.
            }
        }
    }
}
