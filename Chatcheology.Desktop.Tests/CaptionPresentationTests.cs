using System.IO;
using Chatcheology.Archive;
using Chatcheology.Core.Text;
using Chatcheology.Data.ArchiveExport;
using Chatcheology.Data.MsgstoreFacts;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Viewer;
using Chatcheology.Desktop.Services;
using Chatcheology.Desktop.ViewModels;
using Chatcheology.Mobile.Conversation;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// Picture captions (CH-71) on the desktop, read from the workspace's <c>AttachmentCaptionFact</c>: what a card
    /// shows under it, that the viewer's page read carries them, and the whole path from a synthetic workspace with
    /// captions through an archive v4 export, its self-check and the phone's own presentation.
    /// </summary>
    /// <remarks>Invented text only.</remarks>
    public sealed class CaptionPresentationTests
    {
        private static readonly DateTime Recorded = new(2026, 11, 8, 8, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void ACard_ShowsItsCaptionAsStored_WithFollowableAddresses()
        {
            var card = Card(1, "Invented caption with www.example.org/one\nand a second line");

            Assert.True(card.HasCaption);
            Assert.Equal("Invented caption with www.example.org/one\nand a second line", card.Caption);
            Assert.Equal(
                MessageTextScanner.Scan(card.Caption).Select(s => (s.Kind, s.Text, s.Target)),
                card.CaptionSpans.Select(s => (s.Kind, s.Text, s.Target)));
            Assert.Equal(card.Caption, string.Concat(card.CaptionSpans.Select(s => s.Text)));
            Assert.Contains(card.CaptionSpans, span => span.Target is not null);

            // The state the card shows is untouched by a caption.
            Assert.Equal("Media not yet matched", card.HeadlineText);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ACard_WithNoCaption_ShowsNone(string? caption)
        {
            var card = Card(1, caption);

            Assert.False(card.HasCaption);
            Assert.Equal(string.Empty, card.Caption);
            Assert.Empty(card.CaptionSpans);
        }

        [Fact]
        public async Task TheViewersPageRead_CarriesTheWorkspacesCaptions()
        {
            using var workspace = new ArchiveTestWorkspace();

            AddCaptions(workspace, (workspace.RestoredAttachmentID, "Invented whiteboard caption"), (workspace.UnmatchedAttachmentID, "Invented caption on an unmatched picture"));

            using var session = WorkspaceSession.Open(workspace.DatabasePath);

            var loaded = await session.LoadPageAsync(1, PageDirection.First, 1, 50, CancellationToken.None);

            Assert.Equal(2, loaded.Captions.Count);
            Assert.Equal("Invented whiteboard caption", loaded.Captions[workspace.RestoredAttachmentID].CaptionText);
            Assert.Equal("Invented caption on an unmatched picture", loaded.Captions[workspace.UnmatchedAttachmentID].CaptionText);
            Assert.False(loaded.Captions.ContainsKey(workspace.MissingAttachmentID));
        }

        [Fact]
        public void AnExportWithCaptions_PassesItsChecks_AndThePhoneShowsTheCaptions()
        {
            using var workspace = new ArchiveTestWorkspace();

            AddCaptions(workspace, (workspace.RestoredAttachmentID, "Invented caption see www.example.org/two"), (workspace.MissingAttachmentID, "Invented missing caption"));

            var output = Path.Combine(workspace.ExportsFolder, "captions archive");

            var exported = new ArchiveExporter().Export(new ArchiveExportRequest
            {
                WorkspacePath = workspace.DatabasePath,
                ConversationID = 1,
                LocalParticipantID = 1,
                OutputPath = output,
                ExpectResolved = 1,
                ExpectMissingReviewed = 1,
            });

            Assert.Equal(2, exported.Counts.Captions);
            Assert.True(ArchiveFormat.IsArchiveId(exported.ArchiveId));

            var validation = ArchiveValidator.Validate(output);
            Assert.True(validation.Passed, string.Join(", ", validation.FirstProblems));

            var comparison = new ArchiveWorkspaceComparer().Compare(new ArchiveComparisonRequest
            {
                WorkspacePath = workspace.DatabasePath,
                ConversationID = 1,
                LocalParticipantID = 1,
                ArchivePath = output,
            });

            Assert.True(comparison.Passed);
            Assert.Equal(2, comparison.Captions);

            // The phone's presentation of the exported archive.
            using var reader = ArchiveReader.Open(output);
            Assert.Equal(exported.ArchiveId, reader.Info.ArchiveId);

            var timeline = new ConversationTimeline(reader, 1);
            timeline.OpenAt(1);

            while (timeline.ReadLater() is { } page)
            {
                timeline.Append(page);
            }

            var cards = timeline.Rows.OfType<MessageRow>().SelectMany(m => m.Attachments).ToDictionary(c => c.AttachmentID);

            Assert.Equal("Invented caption see www.example.org/two", cards[workspace.RestoredAttachmentID].Caption);
            Assert.True(cards[workspace.RestoredAttachmentID].CaptionHasLinks);
            Assert.Equal("Invented missing caption", cards[workspace.MissingAttachmentID].Caption);
            Assert.False(cards[workspace.UnmatchedAttachmentID].HasCaption);

            SqliteConnection.ClearAllPools();
        }

        private static void AddCaptions(ArchiveTestWorkspace workspace, params (long AttachmentID, string Caption)[] captions)
        {
            SqliteConnection.ClearAllPools();

            AttachmentCaptionFactWriter.InsertBatch(
                workspace.DatabasePath,
                "synthetic-caption-batch",
                captions.Select(c => new AttachmentCaptionFactRow(c.AttachmentID, c.Caption, FromPartner: false)).ToList(),
                Recorded);

            SqliteConnection.ClearAllPools();
        }

        private static AttachmentViewModel Card(long attachmentID, string? caption) =>
            new(
                new ConversationAttachment { AttachmentID = attachmentID, Ordinal = 1 },
                new Phase7ReconstructionState
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
                },
                media: null,
                fact: null,
                lowQualityCopyCount: 0,
                caption: caption);
    }
}
