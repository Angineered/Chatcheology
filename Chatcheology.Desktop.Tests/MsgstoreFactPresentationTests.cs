using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Chatcheology.Core.Models;
using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Viewer;
using Chatcheology.Data.Workspace;
using Chatcheology.Desktop.Services;
using Chatcheology.Desktop.ViewModels;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// CH-8 and CH-10 in the viewer: the forwarded labels, hiding WhatsApp's low-quality copies,
    /// the toggle that shows them, and a workspace with no facts looking exactly as before.
    /// </summary>
    /// <remarks>
    /// The first half builds view-models in memory from synthetic values; the second half runs the
    /// real window over a synthetic workspace whose facts were written through the real writer.
    /// </remarks>
    public sealed class MsgstoreFactPresentationTests
    {
        // -------------------------------------------------------------------------------------
        // View-models, in memory.
        // -------------------------------------------------------------------------------------

        [Fact]
        public void WithNoFact_TheCardAndMessageAreUnchanged()
        {
            var card = Card(1, fact: null);
            var message = Message(card);

            Assert.True(card.IsShown);
            Assert.False(card.IsForwarded);
            Assert.False(card.IsLowQualityCopy);
            Assert.False(card.HasLowQualityCopyLabel);
            Assert.False(card.HasLowQualityCopyNote);
            Assert.Equal("Media not yet matched", card.HeadlineText);

            Assert.True(message.IsShown);
            Assert.False(message.HasForwardedLabel);
            Assert.Equal(string.Empty, message.ForwardedText);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(1)]
        [InlineData(4)]
        public void AForwardedAttachment_LabelsItsMessageForwarded(int? score)
        {
            var message = Message(Card(1, Fact(1, forwarded: true, score)));

            Assert.True(message.HasForwardedLabel);
            Assert.Equal("Forwarded", message.ForwardedText);
        }

        [Theory]
        [InlineData(5)]
        [InlineData(120)]
        public void AForwardScoreOfFiveOrMore_LabelsItsMessageForwardedManyTimes(int score)
        {
            var message = Message(Card(1, Fact(1, forwarded: true, score)));

            Assert.Equal("Forwarded many times", message.ForwardedText);
        }

        [Fact]
        public void AScoreWithoutTheForwardedMark_LabelsNothing()
        {
            var message = Message(Card(1, Fact(1, forwarded: false, 9)));

            Assert.False(message.HasForwardedLabel);
        }

        [Fact]
        public void ALowQualityCopy_IsHiddenByDefaultAndShownWithItsLabelWhenToggled()
        {
            var copy = Card(1, Fact(1, forwarded: false, null, copyOf: 2));
            var message = Message(copy);

            Assert.True(copy.IsLowQualityCopy);
            Assert.False(copy.IsShown);
            Assert.False(copy.HasLowQualityCopyLabel);

            // A placeholder-only message whose only attachment is a hidden copy has nothing left.
            Assert.False(message.IsShown);

            message.SetShowLowQualityCopies(true);

            Assert.True(copy.IsShown);
            Assert.True(copy.HasLowQualityCopyLabel);
            Assert.True(message.IsShown);

            message.SetShowLowQualityCopies(false);

            Assert.False(copy.IsShown);
            Assert.False(message.IsShown);
        }

        [Fact]
        public void HidingACopy_KeepsItsOwnPhase7State()
        {
            var copy = Card(1, Fact(1, forwarded: false, null, copyOf: 2));

            Assert.False(copy.IsShown);
            Assert.True(copy.IsUnresolved);
            Assert.Equal("Media not yet matched", copy.HeadlineText);
            Assert.Equal(1, copy.AttachmentID);
        }

        [Fact]
        public void AMessageWithTextKeepsItsBubbleWhenItsOnlyAttachmentIsAHiddenCopy()
        {
            var copy = Card(1, Fact(1, forwarded: false, null, copyOf: 2));
            var message = Message(copy, content: "A caption");

            Assert.False(copy.IsShown);
            Assert.True(message.IsShown);
        }

        [Fact]
        public void TheFullCopy_SaysItsCopyIsHiddenOnlyWhileItIs()
        {
            var full = Card(2, fact: null, lowQualityCopies: 1);

            Assert.True(full.IsShown);
            Assert.True(full.HasLowQualityCopyNote);
            Assert.Equal("+ WhatsApp low-quality copy (hidden)", full.LowQualityCopyNoteText);

            full.ShowLowQualityCopies = true;

            Assert.False(full.HasLowQualityCopyNote);
            Assert.True(full.IsShown);

            var twice = Card(3, fact: null, lowQualityCopies: 2);

            Assert.Equal("+ 2 WhatsApp low-quality copies (hidden)", twice.LowQualityCopyNoteText);
        }

        // -------------------------------------------------------------------------------------
        // The real window over a synthetic workspace.
        // -------------------------------------------------------------------------------------

        [Fact]
        public void TheViewer_HidesTheCopyLabelsForwardsAndTogglesWithoutMovingThePage() =>
            WithViewer(withFacts: true, (window, viewModel) =>
            {
                Assert.True(viewModel.HasLowQualityCopies);
                Assert.False(viewModel.ShowLowQualityCopies);
                Assert.Equal(Visibility.Visible, window.ShowLowQualityCopiesToggle.Visibility);

                var copyMessage = MessageWith(viewModel, SyntheticWorkspace.CopyAttachmentID);
                var fullMessage = MessageWith(viewModel, SyntheticWorkspace.FullAttachmentID);
                var forwarded = MessageWith(viewModel, SyntheticWorkspace.ForwardedAttachmentID);
                var many = MessageWith(viewModel, SyntheticWorkspace.ForwardedManyAttachmentID);
                var plain = MessageWith(viewModel, SyntheticWorkspace.PlainAttachmentID);

                Assert.False(copyMessage.IsShown);
                Assert.True(fullMessage.Attachments[0].HasLowQualityCopyNote);
                Assert.Equal("Forwarded", forwarded.ForwardedText);
                Assert.Equal("Forwarded many times", many.ForwardedText);
                Assert.False(plain.HasForwardedLabel);
                Assert.True(plain.IsShown);

                var moved = 0;
                viewModel.MovedToPage += (_, _) => moved++;

                var range = viewModel.RangeText;

                viewModel.ShowLowQualityCopies = true;
                Settle(window);

                Assert.True(copyMessage.IsShown);
                Assert.True(copyMessage.Attachments[0].HasLowQualityCopyLabel);
                Assert.False(fullMessage.Attachments[0].HasLowQualityCopyNote);

                // Presentation only: not a move, not a reload.
                Assert.Equal(0, moved);
                Assert.Equal(range, viewModel.RangeText);

                // The hidden copy keeps its own Phase 7 state and still opens its review panel.
                var opened = viewModel.OpenReviewAsync(SyntheticWorkspace.CopyAttachmentID);
                PumpUntil(() => opened.IsCompleted);

                Assert.True(viewModel.Review.IsOpen, viewModel.StatusText);
                Assert.Equal(SyntheticWorkspace.CopyAttachmentID, viewModel.Review.AttachmentID);
            });

        [Fact]
        public void TheViewer_OverAWorkspaceWithNoFacts_LooksAsBefore() =>
            WithViewer(withFacts: false, (window, viewModel) =>
            {
                Assert.False(viewModel.HasLowQualityCopies);
                Assert.Equal(Visibility.Collapsed, window.ShowLowQualityCopiesToggle.Visibility);

                foreach (var message in viewModel.Messages)
                {
                    Assert.True(message.IsShown);
                    Assert.False(message.HasForwardedLabel);

                    foreach (var attachment in message.Attachments)
                    {
                        Assert.True(attachment.IsShown);
                        Assert.False(attachment.HasLowQualityCopyLabel);
                        Assert.False(attachment.HasLowQualityCopyNote);
                    }
                }
            });

        [Fact]
        public void TheViewer_RefusesAVersionThreeWorkspaceAndLeavesItUnchanged()
        {
            using var workspace = new SyntheticWorkspace(withFacts: false);

            using (var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    DROP INDEX IX_AttachmentMsgstoreFact_SourceBatch;
                    DROP INDEX IX_AttachmentMsgstoreFact_LowQualityCopyOfAttachmentID;
                    DROP TABLE AttachmentMsgstoreFact;
                    PRAGMA user_version = 3;
                    """;
                command.ExecuteNonQuery();
            }

            var before = WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath);

            var exception = Assert.Throws<InvalidOperationException>(
                () => WorkspaceSession.Open(workspace.DatabasePath));

            Assert.Contains("schema version 3", exception.Message, StringComparison.Ordinal);
            Assert.Equal(before, WorkspaceCheckpoint.ComputeFileSha256(workspace.DatabasePath));
        }

        // -------------------------------------------------------------------------------------
        // Harness.
        // -------------------------------------------------------------------------------------

        private static MessageViewModel MessageWith(MainViewModel viewModel, long attachmentID) =>
            viewModel.Messages.Single(
                message => message.Attachments.Any(a => a.AttachmentID == attachmentID));

        private static void WithViewer(bool withFacts, Action<MainWindow, MainViewModel> body) =>
            StaRunner.Run(() =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

                using var workspace = new SyntheticWorkspace(withFacts);

                var window = new MainWindow(() => workspace.DatabasePath)
                {
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                };

                try
                {
                    window.Show();

                    var viewModel = (MainViewModel)window.DataContext;

                    viewModel.OpenWorkspaceCommand.Execute(null);

                    PumpUntil(() => viewModel.Messages.Count > 0 && !viewModel.IsBusy);
                    Settle(window);

                    body(window, viewModel);
                }
                finally
                {
                    window.Close();
                    SqliteConnection.ClearAllPools();
                }
            });

        private static void Settle(Window window)
        {
            for (var pass = 0; pass < 4; pass++)
            {
                window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, () => { });
            }
        }

        private static void PumpUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);

            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("The viewer did not reach the expected state in time.");
                }

                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () => { });
                Thread.Sleep(10);
            }
        }

        private static AttachmentViewModel Card(
            long attachmentID, AttachmentMsgstoreFact? fact, int lowQualityCopies = 0) =>
            new(
                new ConversationAttachment { AttachmentID = attachmentID, Ordinal = 1 },
                Unresolved(attachmentID),
                media: null,
                fact,
                lowQualityCopies);

        private static MessageViewModel Message(
            AttachmentViewModel attachment, string? content = null) =>
            new(
                new ConversationMessage
                {
                    MessageID = 1,
                    ConversationID = 1,
                    SequenceNumber = 1,
                    MessageDateTimeLocal = new DateTime(2026, 3, 2, 9, 0, 0),
                    SenderParticipantID = 1,
                    SenderDisplayName = "Ada",
                    MessageType = MessageType.User,
                    MessageContent = content ?? ParsedMessage.MediaPlaceholderContent,
                    Attachments = [new ConversationAttachment { AttachmentID = attachment.AttachmentID, Ordinal = 1 }],
                },
                dateSeparator: null,
                Brushes.Black,
                [attachment]);

        private static AttachmentMsgstoreFact Fact(
            long attachmentID, bool forwarded, int? score, long? copyOf = null) =>
            new()
            {
                AttachmentID = attachmentID,
                IsForwarded = forwarded,
                ForwardScore = score,
                LowQualityCopyOfAttachmentID = copyOf,
                SourceBatch = "synthetic",
                RecordedDateTimeUtc = new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc),
            };

        private static Phase7ReconstructionState Unresolved(long attachmentID) =>
            new()
            {
                AttachmentID = attachmentID,
                Status = Phase7ResolutionStatus.Unresolved,
                ResolvedAsset = null,
                PlaceholderReason = Phase7UnresolvedPlaceholderReason.NeverEvaluated,
                EvidenceRunID = null,
                ExactDateCandidateCount = null,
                AdjacentDateContextCount = null,
                IsWithinDirectionImpossibleGroup = null,
                CandidateTierCount = null,
                HasCurrentReviewedInsufficientEvidence = false,
                AllCurrentCandidatesIndependentlyDisproven = false,
            };

        /// <summary>
        /// A synthetic workspace: two invented participants, six media-placeholder messages with one
        /// unresolved attachment each and, optionally, one batch of invented msgstore facts.
        /// </summary>
        private sealed class SyntheticWorkspace : IDisposable
        {
            internal const long ForwardedAttachmentID = 1;
            internal const long ForwardedManyAttachmentID = 2;
            internal const long CopyAttachmentID = 3;
            internal const long FullAttachmentID = 4;
            internal const long PlainAttachmentID = 5;

            private const string Stamp = "2026-04-01T00:00:00.0000000Z";

            private readonly string _directoryPath;

            internal SyntheticWorkspace(bool withFacts)
            {
                _directoryPath = Path.Combine(
                    Path.GetTempPath(),
                    "Chatcheology.Desktop.Tests",
                    Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(_directoryPath);

                DatabasePath = Path.Combine(_directoryPath, "workspace.db");

                WorkspaceDatabase.Initialise(DatabasePath);

                using (var connection = WorkspaceDatabase.OpenConnection(DatabasePath))
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        $"""
                        INSERT INTO ImportSource (SourceType, DisplayName, ImportedDateTimeUtc)
                        VALUES ('SyntheticExport', 'Synthetic export', '{Stamp}');

                        INSERT INTO Conversation (Title, CreatedDateTimeUtc)
                        VALUES ('Ada and Bo', '{Stamp}');

                        INSERT INTO Participant (DisplayName) VALUES ('Ada');
                        INSERT INTO Participant (DisplayName) VALUES ('Bo');

                        INSERT INTO ConversationParticipant (ConversationID, ParticipantID) VALUES (1, 1);
                        INSERT INTO ConversationParticipant (ConversationID, ParticipantID) VALUES (1, 2);

                        WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 6)
                        INSERT INTO Message (
                            ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                            SenderParticipantID, MessageType, MessageContent, RawContent,
                            SourceLineStart, SourceLineEnd)
                        SELECT 1, 1, i, '2026-03-02T09:0' || i || ':00', 1 + (i % 2), 'User',
                               $placeholder, 'raw', i, i
                        FROM n;

                        INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus)
                        SELECT MessageID, 1, 'Unresolved' FROM Message WHERE SequenceNumber <= 5;
                        """;
                    command.Parameters.AddWithValue("$placeholder", ParsedMessage.MediaPlaceholderContent);

                    command.ExecuteNonQuery();
                }

                if (withFacts)
                {
                    MsgstoreFactWriter.InsertBatch(
                        DatabasePath,
                        "synthetic-batch",
                        [
                            new MsgstoreFactRow(ForwardedAttachmentID, true, 1, null),
                            new MsgstoreFactRow(ForwardedManyAttachmentID, true, 127, null),
                            new MsgstoreFactRow(CopyAttachmentID, false, null, FullAttachmentID),
                        ],
                        new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc));
                }
            }

            internal string DatabasePath { get; }

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
