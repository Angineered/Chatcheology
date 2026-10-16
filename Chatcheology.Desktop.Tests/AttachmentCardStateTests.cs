using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Chatcheology.Data.Media;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Viewer;
using Chatcheology.Desktop.Services;
using Chatcheology.Desktop.ViewModels;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// That an attachment reviewed as insufficient evidence reads as "media missing", and that
    /// every other state keeps its wording and carries no missing flag.
    /// </summary>
    /// <remarks>
    /// The states are built in memory from synthetic values; nothing here opens a workspace.
    /// </remarks>
    public class AttachmentCardStateTests
    {
        [Fact]
        public void ReviewedInsufficientEvidence_ReadsAsMediaMissing()
        {
            var card = Card(Unresolved(Phase7UnresolvedPlaceholderReason.ReviewedInsufficientEvidence));

            Assert.Equal("Media missing", card.HeadlineText);
            Assert.Equal("Reviewed: no file was recovered for this attachment.", card.DetailText);
            Assert.True(card.IsReviewedMissing);
            Assert.True(card.IsUnresolved);
            Assert.False(card.IsResolvedAndAvailable);
            Assert.False(card.IsResolvedAndUnavailable);
            Assert.Null(card.MediaAssetID);
        }

        [Theory]
        [InlineData(Phase7UnresolvedPlaceholderReason.NeverEvaluated,
            "no completed run has evaluated this attachment")]
        [InlineData(Phase7UnresolvedPlaceholderReason.NoCandidates,
            "evaluated, with no candidate and no adjacent-date context")]
        [InlineData(Phase7UnresolvedPlaceholderReason.NoExactDateCandidates,
            "evaluated, with no exact-date candidate")]
        [InlineData(Phase7UnresolvedPlaceholderReason.CandidateSetUnranked,
            "candidates recorded, and the frozen order does not discriminate between them")]
        [InlineData(Phase7UnresolvedPlaceholderReason.CandidateSetRanked,
            "candidates recorded, and the frozen order separates them into tiers")]
        [InlineData(Phase7UnresolvedPlaceholderReason.ImpossibleGroupFlagged,
            "the attachment's group is flagged impossible")]
        [InlineData(Phase7UnresolvedPlaceholderReason.ReviewedAllCandidatesIndependentlyDisproven,
            "every candidate of the current run carries an independent disproof")]
        public void EveryOtherUnresolvedReason_KeepsTheNotYetMatchedCard(
            Phase7UnresolvedPlaceholderReason reason, string detail)
        {
            var card = Card(Unresolved(reason));

            Assert.Equal("Media not yet matched", card.HeadlineText);
            Assert.Equal(detail, card.DetailText);
            Assert.True(card.IsUnresolved);
            Assert.False(card.IsReviewedMissing);
        }

        [Fact]
        public void AnUnresolvedStateWithNoReason_KeepsTheNotYetMatchedCard()
        {
            var card = Card(Unresolved(null));

            Assert.Equal("Media not yet matched", card.HeadlineText);
            Assert.Equal("unresolved", card.DetailText);
            Assert.False(card.IsReviewedMissing);
        }

        [Fact]
        public void ResolvedWithNoReachableCopy_IsUnchanged()
        {
            var card = Card(Resolved(), media: null);

            Assert.Equal("Media resolved — file currently unavailable", card.HeadlineText);
            Assert.Equal("The workspace records no local copy of this asset.", card.DetailText);
            Assert.True(card.IsResolvedAndUnavailable);
            Assert.False(card.IsUnresolved);
            Assert.False(card.IsReviewedMissing);
        }

        [Fact]
        public void ResolvedWithAReachableCopy_IsUnchanged()
        {
            var location = new MediaAssetLocation
            {
                MediaAssetID = 7,
                MediaFileID = 70,
                MediaSourceID = 1,
                MediaSourceDisplayName = "Synthetic source",
                RootPath = @"C:\synthetic",
                RelativePath = "voice.opus",
                FileName = "voice.opus",
                Extension = ".opus",
                MediaType = MediaType.Audio,
                SizeBytes = 2048,
                FullPath = @"C:\synthetic\voice.opus",
            };

            var card = Card(Resolved(), new MediaAvailability(1, location));

            Assert.Equal("Media resolved", card.HeadlineText);
            Assert.True(card.IsResolvedAndAvailable);
            Assert.False(card.IsUnresolved);
            Assert.False(card.IsReviewedMissing);
        }

        [Fact]
        public void NoState_IsUnchanged()
        {
            var card = Card(null);

            Assert.Equal("Attachment state unavailable", card.HeadlineText);
            Assert.False(card.IsReviewedMissing);
            Assert.False(card.IsUnresolved);
        }

        /// <remarks>
        /// The window's own attachment template, laid out off screen: the dashed outline is shown
        /// for a reviewed-missing card and nowhere else.
        /// </remarks>
        [Fact]
        public void TheCardTemplate_ShowsTheDashedOutlineOnlyForMediaMissing()
        {
            StaRunner.Run(() =>
            {
                Assert.Equal(Visibility.Visible, OutlineVisibility(
                    Card(Unresolved(Phase7UnresolvedPlaceholderReason.ReviewedInsufficientEvidence))));

                Assert.Equal(Visibility.Collapsed, OutlineVisibility(
                    Card(Unresolved(Phase7UnresolvedPlaceholderReason.NoCandidates))));

                Assert.Equal(Visibility.Collapsed, OutlineVisibility(Card(Resolved())));
            });
        }

        private static Visibility OutlineVisibility(AttachmentViewModel card)
        {
            var window = new MainWindow
            {
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };

            try
            {
                var presenter = new ContentPresenter
                {
                    Content = card,
                    ContentTemplate = (DataTemplate)window.FindResource("AttachmentTemplate"),
                };

                window.Content = presenter;
                window.Show();
                window.UpdateLayout();

                var outline = Find<Rectangle>(presenter)
                    ?? throw new InvalidOperationException("The card has no outline rectangle.");

                Assert.NotNull(outline.StrokeDashArray);

                return outline.Visibility;
            }
            finally
            {
                window.Close();
            }
        }

        private static T? Find<T>(DependencyObject root) where T : DependencyObject
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);

                if (child is T match)
                {
                    return match;
                }

                if (Find<T>(child) is { } nested)
                {
                    return nested;
                }
            }

            return null;
        }

        private static AttachmentViewModel Card(
            Phase7ReconstructionState? state, MediaAvailability? media = null) =>
            new(new ConversationAttachment { AttachmentID = 42, Ordinal = 0 }, state, media);

        private static Phase7ReconstructionState Unresolved(Phase7UnresolvedPlaceholderReason? reason) =>
            new()
            {
                AttachmentID = 42,
                Status = Phase7ResolutionStatus.Unresolved,
                ResolvedAsset = null,
                PlaceholderReason = reason,
                EvidenceRunID = null,
                ExactDateCandidateCount = null,
                AdjacentDateContextCount = null,
                IsWithinDirectionImpossibleGroup = null,
                CandidateTierCount = null,
                HasCurrentReviewedInsufficientEvidence =
                    reason == Phase7UnresolvedPlaceholderReason.ReviewedInsufficientEvidence,
                AllCurrentCandidatesIndependentlyDisproven = false,
            };

        private static Phase7ReconstructionState Resolved() =>
            new()
            {
                AttachmentID = 42,
                Status = Phase7ResolutionStatus.Resolved,
                ResolvedAsset = new Phase7ResolvedAssetDescriptor
                {
                    MediaAssetID = 7,
                    MediaType = "Audio",
                    SizeBytes = 2048,
                    DurationMS = null,
                    Width = null,
                    Height = null,
                },
                PlaceholderReason = null,
                EvidenceRunID = null,
                ExactDateCandidateCount = null,
                AdjacentDateContextCount = null,
                IsWithinDirectionImpossibleGroup = null,
                CandidateTierCount = null,
                HasCurrentReviewedInsufficientEvidence = false,
                AllCurrentCandidatesIndependentlyDisproven = false,
            };
    }
}
