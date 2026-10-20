using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Chatcheology.Archive;
using Chatcheology.Data.ArchiveExport;
using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Tests.Matching;
using Chatcheology.Data.Tests.Phase7;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.ArchiveExport
{
    /// <summary>
    /// A synthetic workspace with real media files on disk, shaped for the archive exporter.
    /// </summary>
    /// <remarks>
    /// <para>Built on <see cref="Phase7DecisionFixture"/>, so attachments are resolved, revoked and
    /// reviewed through the real Phase 7 services, and a Missing attachment really carries a current
    /// "reviewed, insufficient evidence" review of a real run.</para>
    /// <para>The media source's root is a private temporary folder, beside a separate folder the
    /// archives are written into. Both are outside the repository and removed on dispose.</para>
    /// <para>Building happens in two phases, because a Phase 7 run closes the building connection:
    /// first messages, attachments and payloads; then <see cref="RunEvidence"/>; then the decisions
    /// and msgstore facts, which open their own connections.</para>
    /// <para>Everything is invented: fictional participants, text and bytes. No real name, path, hash
    /// or archive figure appears in this project.</para>
    /// </remarks>
    internal sealed class ArchiveExportFixture : IDisposable
    {
        internal static readonly DateOnly Day = Phase7TestFixture.FirstDate;

        private readonly Phase7DecisionFixture _decisions = new();
        private readonly string _root;
        private readonly long _sourceID;

        private int _minute;

        internal ArchiveExportFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "Chatcheology.Data.Tests", Guid.NewGuid().ToString("N"));

            MediaRoot = Path.Combine(_root, "media-source");
            ExportsFolder = Path.Combine(_root, "exports");

            Directory.CreateDirectory(MediaRoot);
            Directory.CreateDirectory(ExportsFolder);

            Workspace.Execute(
                $"""
                INSERT INTO MediaSource (DisplayName, SourceType, RootPath, ImportedDateTimeUtc)
                VALUES ('Synthetic files', 'SyntheticMediaDirectory', '{MediaRoot.Replace("'", "''")}',
                        '2026-08-17T16:00:00.0000000Z');
                """);

            _sourceID = Workspace.ScalarLong("SELECT MAX(MediaSourceID) FROM MediaSource;");
        }

        internal Phase7DecisionFixture Decisions => _decisions;

        internal MatchingTestWorkspace Workspace => _decisions.Evidence.Workspace;

        internal string DatabasePath => _decisions.DatabasePath;

        /// <summary>The recorded root of the media source whose copies are real files.</summary>
        internal string MediaRoot { get; }

        /// <summary>A folder outside the repository that archives may be written into.</summary>
        internal string ExportsFolder { get; }

        /// <summary>A new output path in <see cref="ExportsFolder"/>.</summary>
        internal string Output(string name = "Alex and Sam archive") => Path.Combine(ExportsFolder, name);

        // ---------------------------------------------------------------------------------------
        // Phase 1: messages, attachments and payloads.
        // ---------------------------------------------------------------------------------------

        /// <summary>A text message with no attachment.</summary>
        internal long AddText(string text, long? sender = MatchingTestWorkspace.LocalParticipantID, DateOnly? day = null)
        {
            var messageID = Workspace.AddMessage(day ?? Day, sender, time: NextTime());

            Workspace.Execute(
                $"UPDATE Message SET MessageContent = '{text.Replace("'", "''")}' WHERE MessageID = {messageID};");

            return messageID;
        }

        /// <summary>A message carrying one attachment.</summary>
        /// <returns>The attachment.</returns>
        internal long AddPhoto(long sender = MatchingTestWorkspace.LocalParticipantID, DateOnly? day = null) =>
            Workspace.AddMediaAttachment(day ?? Day, sender, time: NextTime());

        /// <summary>A second attachment on the message carrying <paramref name="attachmentID"/>.</summary>
        internal long AddSecondAttachment(long attachmentID)
        {
            Workspace.Execute(
                $"""
                INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus)
                SELECT MessageID, 2, 'Unresolved' FROM Attachment WHERE AttachmentID = {attachmentID};
                """);

            return Workspace.ScalarLong("SELECT MAX(AttachmentID) FROM Attachment;");
        }

        /// <summary>How a recorded copy of a payload exists on disk.</summary>
        internal enum Copy
        {
            /// <summary>The file is there with the payload's bytes.</summary>
            Good,

            /// <summary>The file is recorded but not there.</summary>
            Absent,

            /// <summary>The file is there with different bytes.</summary>
            WrongBytes,
        }

        /// <summary>
        /// A payload with one recorded copy per entry of <paramref name="copies"/>, in recording order.
        /// </summary>
        /// <returns>The asset, its SHA-256 (lower-case hex) and its bytes.</returns>
        internal (long AssetID, string Sha256, byte[] Bytes) AddPayload(
            string content, params (string FileName, Copy State)[] copies)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var sha = Convert.ToHexString(SHA256.HashData(bytes));

            // The workspace's own convention is upper-case hex; the archive's is lower-case.
            var assetID = Workspace.AddMediaAsset(sha, "Image", bytes.Length);

            foreach (var (fileName, state) in copies)
            {
                var fileID = Workspace.AddMediaFile(
                    _sourceID,
                    assetID,
                    sha,
                    sizeBytes: bytes.Length,
                    fileName: fileName,
                    extension: Path.GetExtension(fileName));

                var relative = ScalarText($"SELECT RelativePath FROM MediaFile WHERE MediaFileID = {fileID};");
                var full = Path.Combine(MediaRoot, relative.Replace('/', Path.DirectorySeparatorChar));

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);

                switch (state)
                {
                    case Copy.Good:
                        File.WriteAllBytes(full, bytes);
                        break;

                    case Copy.WrongBytes:
                        File.WriteAllBytes(full, Encoding.UTF8.GetBytes(content + " but altered"));
                        break;
                }
            }

            return (assetID, sha.ToLowerInvariant(), bytes);
        }

        /// <summary>Records the import's export hash and time zone.</summary>
        internal void SetImportSource(string sha256, string timeZoneID) =>
            Workspace.Execute(
                $"UPDATE ImportSource SET SHA256 = '{sha256}', SourceTimeZoneID = '{timeZoneID}';");

        // ---------------------------------------------------------------------------------------
        // Phase 2: evidence, decisions and facts.
        // ---------------------------------------------------------------------------------------

        /// <summary>One Phase 7 run over the conversation. Closes the building connection.</summary>
        internal long RunEvidence() => _decisions.Evidence.Run().RunID;

        internal Phase7ResolutionTransitionResult Confirm(
            long attachmentID,
            long assetID,
            string basis = Phase7DecisionFixture.HumanBasis,
            Phase7IndependentBasisType type = Phase7IndependentBasisType.HumanContentOrContextInspection,
            string? actor = "Reviewer A") =>
            _decisions.Confirm(attachmentID, assetID, type, basis, actorLabel: actor);

        internal void Review(long attachmentID, long runID) => _decisions.Review(attachmentID, runID);

        /// <summary>Records text-message msgstore facts (CH-22) through the real writer.</summary>
        internal void MessageFacts(params MessageMsgstoreFactRow[] rows) =>
            MessageMsgstoreFactWriter.InsertBatch(
                DatabasePath, "synthetic-message-batch", rows, new DateTime(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc));

        internal void Facts(params MsgstoreFactRow[] rows) =>
            MsgstoreFactWriter.InsertBatch(
                DatabasePath, "synthetic-batch", rows, new DateTime(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc));

        // ---------------------------------------------------------------------------------------
        // Exporting and reading back.
        // ---------------------------------------------------------------------------------------

        internal ArchiveExportRequest Request(
            string output, int expectResolved, int expectMissingReviewed, long? localParticipantID = null) =>
            new()
            {
                WorkspacePath = DatabasePath,
                ConversationID = MatchingTestWorkspace.ConversationID,
                LocalParticipantID = localParticipantID ?? MatchingTestWorkspace.LocalParticipantID,
                OutputPath = output,
                ExpectResolved = expectResolved,
                ExpectMissingReviewed = expectMissingReviewed,
            };

        /// <summary>Every entry beneath the exports folder, as relative paths.</summary>
        internal List<string> ExportsContent() =>
            Directory.EnumerateFileSystemEntries(ExportsFolder, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(ExportsFolder, p))
                .Order(StringComparer.Ordinal)
                .ToList();

        /// <summary>The workspace folder's files and their hashes, to prove an export changed nothing.</summary>
        internal List<string> WorkspaceFingerprint()
        {
            _decisions.Evidence.CloseBuilding();
            SqliteConnection.ClearAllPools();

            return Directory.GetFiles(_decisions.DirectoryPath)
                .Order(StringComparer.Ordinal)
                .Select(path => Path.GetFileName(path) + ":" + ArchiveHashing.Sha256OfFile(path))
                .ToList();
        }

        internal string ScalarText(string sql) => _decisions.Evidence.ScalarText(sql);

        public void Dispose()
        {
            _decisions.Dispose();
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private string NextTime()
        {
            var minute = _minute++;

            return $"{(9 + minute / 60).ToString("D2", CultureInfo.InvariantCulture)}:" +
                   $"{(minute % 60).ToString("D2", CultureInfo.InvariantCulture)}:00";
        }
    }
}
