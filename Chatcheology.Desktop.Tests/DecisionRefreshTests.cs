using System.Globalization;
using System.IO;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Viewer;
using Chatcheology.Data.Workspace;
using Chatcheology.Desktop.ViewModels;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// What the whole shell does around one decision, from the operator's click to what they see
    /// afterwards.
    /// </summary>
    /// <remarks>
    /// <para>The end-to-end path, against a synthetic workspace with a real Phase 7 evidence run and
    /// a real Phase 7 write: quiesce, close, decide, reopen, restore, re-read.</para>
    /// <para>Two of these pin mistakes that are easy to make and quiet when made — restoring the
    /// conversation and the "Show as" choice by object identity rather than by identifier, which
    /// fails silently because reopening produces new instances.</para>
    /// </remarks>
    public class DecisionRefreshTests
    {
        [Fact]
        public async Task ARecordedDecision_KeepsThePlaceAndShowsStateReadBackFromDisk()
        {
            using var workspace = new EvidenceWorkspace();

            var viewModel = new MainViewModel(() => workspace.DatabasePath);

            viewModel.OpenWorkspaceCommand.Execute(null);

            await WaitUntil(() => viewModel.Messages.Count > 0);

            Assert.Equal(2, viewModel.Conversations.Count);
            Assert.Equal(2, viewModel.Participants.Count);

            // A session-only presentation choice, and a page position, both of which must survive.
            viewModel.LocalParticipant = viewModel.Participants[1];

            var conversationID = viewModel.SelectedConversation!.ConversationID;
            var participantID = viewModel.LocalParticipant!.ParticipantID;
            var rangeText = viewModel.RangeText;

            await viewModel.OpenReviewAsync(workspace.AttachmentID);

            Assert.True(viewModel.Review.IsOpen);
            Assert.NotEmpty(viewModel.Review.Candidates);
            Assert.False(viewModel.Review.HasCurrentReview);

            viewModel.Review.BeginReviewCommand.Execute(null);

            Assert.Equal(PendingDecision.Review, viewModel.Review.Pending);

            viewModel.Review.CommitCommand.Execute(null);

            await WaitUntil(() => viewModel.Review.StatusText.Length > 0);

            // Recorded, and recorded once.
            Assert.Equal("Recorded.", viewModel.Review.StatusText);
            Assert.Equal(1, workspace.ReviewEventCount());

            // What is shown now was read back from the workspace, not assumed from the request.
            Assert.True(viewModel.Review.HasCurrentReview);

            // The place survived, matched by identifier rather than by object identity: reopening
            // produced new DTO instances, and a reference comparison would have blanked both of
            // these without saying so.
            Assert.Equal(conversationID, viewModel.SelectedConversation!.ConversationID);
            Assert.Equal(participantID, viewModel.LocalParticipant!.ParticipantID);
            Assert.Equal(rangeText, viewModel.RangeText);
            Assert.Equal(workspace.AttachmentID, viewModel.Review.AttachmentID);

            viewModel.CloseSession();
        }

        /// <remarks>
        /// A refusal leaves the workspace unchanged and the viewer usable. Nothing is retried, and
        /// the operator is told what Phase 7 said.
        /// </remarks>
        [Fact]
        public async Task ARefusedDecision_LeavesTheWorkspaceUnchangedAndTheViewerUsable()
        {
            using var workspace = new EvidenceWorkspace();

            var viewModel = new MainViewModel(() => workspace.DatabasePath);

            viewModel.OpenWorkspaceCommand.Execute(null);

            await WaitUntil(() => viewModel.Messages.Count > 0);

            viewModel.LocalParticipant = viewModel.Participants[1];

            await viewModel.OpenReviewAsync(workspace.AttachmentID);

            var candidate = viewModel.Review.Candidates[0];

            viewModel.Review.SelectedCandidate = candidate;
            viewModel.Review.BeginDisproveCommand.Execute(null);
            viewModel.Review.DisproofReason = "The recovered copy shows a different room.";

            // A second evidence run supersedes the candidate row the panel is holding, so Phase 7
            // refuses the disproof: the row it names is no longer current.
            workspace.RunEvidence();

            viewModel.Review.CommitCommand.Execute(null);

            await WaitUntil(() => viewModel.Review.StatusText.Length > 0);

            Assert.Contains("current", viewModel.Review.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, workspace.DispositionEventCount());

            // Still usable, still in the same place, and the panel now shows the newer evidence.
            Assert.True(viewModel.Review.IsOpen);
            Assert.NotEmpty(viewModel.Messages);
            Assert.Equal(workspace.AttachmentID, viewModel.Review.AttachmentID);
            Assert.NotNull(viewModel.LocalParticipant);

            viewModel.CloseSession();
        }

        private static async Task WaitUntil(Func<bool> condition, int timeoutMilliseconds = 15_000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);

            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(20);
            }

            Assert.Fail("The view-model did not reach the expected state in time.");
        }

        /// <summary>
        /// A synthetic workspace with one attachment, two same-date candidates and a completed
        /// Phase 7 evidence run.
        /// </summary>
        /// <remarks>
        /// Everything is invented: fictional participants, fictional hashes, and names that name
        /// nothing real. No recovery path, real file name or archive figure appears in this project.
        /// </remarks>
        private sealed class EvidenceWorkspace : IDisposable
        {
            private const string Stamp = "2026-04-01T00:00:00.0000000Z";

            private readonly string _directoryPath;

            internal EvidenceWorkspace()
            {
                _directoryPath = Path.Combine(
                    Path.GetTempPath(),
                    "Chatcheology.Desktop.Tests",
                    Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(_directoryPath);

                DatabasePath = Path.Combine(_directoryPath, "workspace.db");

                WorkspaceDatabase.Initialise(DatabasePath);

                Build();

                AttachmentID = Scalar("SELECT MIN(AttachmentID) FROM Attachment;");

                RunEvidence();
            }

            internal string DatabasePath { get; }

            internal long AttachmentID { get; }

            /// <summary>Runs one Phase 7 evidence snapshot over the synthetic conversation.</summary>
            internal void RunEvidence()
            {
                SqliteConnection.ClearAllPools();

                new Phase7EvidenceSnapshotService().Run(
                    new Phase7RunRequest
                    {
                        DatabasePath = DatabasePath,
                        ConversationID = 1,
                        LocalParticipantID = 1,
                    });
            }

            internal long ReviewEventCount() =>
                Scalar("SELECT COUNT(*) FROM AttachmentReviewEvent;");

            internal long DispositionEventCount() =>
                Scalar("SELECT COUNT(*) FROM AttachmentCandidateDispositionEvent;");

            private void Build()
            {
                Execute(
                    $"""
                    INSERT INTO ImportSource (SourceType, DisplayName, ImportedDateTimeUtc)
                    VALUES ('SyntheticExport', 'Synthetic export', '{Stamp}');

                    INSERT INTO Conversation (Title, CreatedDateTimeUtc)
                    VALUES ('Ada and Bo', '{Stamp}');

                    INSERT INTO Conversation (Title, CreatedDateTimeUtc)
                    VALUES ('Ada and Cy', '{Stamp}');

                    INSERT INTO Participant (DisplayName) VALUES ('Ada');
                    INSERT INTO Participant (DisplayName) VALUES ('Bo');
                    INSERT INTO Participant (DisplayName) VALUES ('Cy');

                    INSERT INTO ConversationParticipant (ConversationID, ParticipantID) VALUES (1, 1);
                    INSERT INTO ConversationParticipant (ConversationID, ParticipantID) VALUES (1, 2);
                    INSERT INTO ConversationParticipant (ConversationID, ParticipantID) VALUES (2, 3);

                    INSERT INTO MediaSource (DisplayName, SourceType, RootPath, ImportedDateTimeUtc)
                    VALUES ('Synthetic store', 'SyntheticMediaDirectory', 'MediaRoot', '{Stamp}');

                    INSERT INTO Message (
                        ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                        SenderParticipantID, MessageType, MessageContent, RawContent,
                        SourceLineStart, SourceLineEnd)
                    VALUES (1, 1, 1, '2026-04-06T09:00:00', 1, 'User', 'Before', 'raw', 1, 1);

                    INSERT INTO Message (
                        ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                        SenderParticipantID, MessageType, MessageContent, RawContent,
                        SourceLineStart, SourceLineEnd)
                    VALUES (1, 1, 2, '2026-04-06T09:01:00', 1, 'User', '<Media omitted>', 'raw', 2, 2);

                    INSERT INTO Message (
                        ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                        SenderParticipantID, MessageType, MessageContent, RawContent,
                        SourceLineStart, SourceLineEnd)
                    VALUES (1, 1, 3, '2026-04-06T09:02:00', 2, 'User', 'After', 'raw', 3, 3);

                    INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus)
                    VALUES (2, 1, 'Unresolved');
                    """);

                AddCandidateAsset(1, "0001");
                AddCandidateAsset(2, "0002");
            }

            /// <summary>One hashed asset with one dated, marker-named copy the run can see.</summary>
            private void AddCandidateAsset(int number, string token)
            {
                var hash = number.ToString("X4", CultureInfo.InvariantCulture).PadLeft(64, '0');
                var name = $"IMG-20260406-WA{token}.jpg";

                Execute(
                    $"""
                    INSERT INTO MediaAsset (SHA256, MediaType, SizeBytes)
                    VALUES ('{hash}', 'Image', 1024);

                    INSERT INTO MediaFile (
                        MediaSourceID, RelativePath, FileName, Extension, SizeBytes, SHA256,
                        MediaType, FileDate, IsSent)
                    VALUES (1, 'Sent/{name}', '{name}', '.jpg', 1024, '{hash}',
                            'Image', '2026-04-06', 1);

                    INSERT INTO MediaAssetFile (MediaAssetID, MediaFileID)
                    VALUES ({number}, {number});
                    """);
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
                    Directory.Delete(_directoryPath, recursive: true);
                }
                catch (IOException)
                {
                    // A leaked handle shows up as a test failure elsewhere, not here.
                }
            }
        }
    }
}
