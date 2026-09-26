using Chatcheology.Data.Media;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Viewer;
using Chatcheology.Desktop.Services;
using Chatcheology.Desktop.ViewModels;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// What the review panel will and will not let the operator do, and when it writes.
    /// </summary>
    /// <remarks>
    /// <para>Every test here uses an executor that records the decision it is handed without running
    /// it, so what is pinned is the panel's own behaviour: which actions are available, what must be
    /// typed first, and — the property that matters most — that looking at a candidate writes
    /// nothing.</para>
    /// <para>Whether the resulting request is one Phase 7 accepts is pinned separately, against the
    /// real services, in the data layer's decision-integration tests.</para>
    /// </remarks>
    public class ReviewCommandValidationTests
    {
        // -----------------------------------------------------------------------------------
        // Nothing writes by accident.
        // -----------------------------------------------------------------------------------

        /// <remarks>
        /// <strong>The rule the write-facing milestone rests on.</strong> Selecting a candidate is
        /// looking at one. It loads a preview and enables actions; it records nothing.
        /// </remarks>
        [Fact]
        public void SelectingACandidate_WritesNothing()
        {
            var panel = Open(out var executor, candidates: 3);

            foreach (var candidate in panel.Candidates)
            {
                panel.SelectedCandidate = candidate;
            }

            panel.SelectedCandidate = null;

            Assert.Equal(0, executor.Invocations);
        }

        [Fact]
        public void ArmingADecision_WritesNothingUntilItIsCommitted()
        {
            var panel = Open(out var executor, candidates: 2);

            panel.SelectedCandidate = panel.Candidates[0];
            panel.BeginDisproveCommand.Execute(null);
            panel.DisproofReason = "The recovered copy shows a different room.";

            Assert.Equal(PendingDecision.Disprove, panel.Pending);
            Assert.Equal(0, executor.Invocations);
        }

        [Fact]
        public void ChangingTheSelectedCandidate_StandsDownAnArmedDecision()
        {
            var panel = Open(out var executor, candidates: 2);

            panel.SelectedCandidate = panel.Candidates[0];
            panel.BeginDisproveCommand.Execute(null);
            panel.DisproofReason = "Ruled out.";

            panel.SelectedCandidate = panel.Candidates[1];

            Assert.Equal(PendingDecision.None, panel.Pending);
            Assert.False(panel.CommitCommand.CanExecute(null));
            Assert.Equal(0, executor.Invocations);
        }

        // -----------------------------------------------------------------------------------
        // The consequence is stated before the action that causes it.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void ArmingADisproof_StatesThatThereIsNoUndoBeforeTheWrite()
        {
            var panel = Open(out var executor, candidates: 2);

            panel.SelectedCandidate = panel.Candidates[0];

            Assert.Equal(string.Empty, panel.PendingWarningText);

            panel.BeginDisproveCommand.Execute(null);

            Assert.Equal(AttachmentReviewViewModel.DisproofWarning, panel.PendingWarningText);
            Assert.Contains("no undo", panel.PendingWarningText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, executor.Invocations);
        }

        [Fact]
        public void ArmingAConfirmation_StatesThatRankingIsNotABasis()
        {
            var panel = Open(out _, candidates: 2);

            panel.SelectedCandidate = panel.Candidates[0];
            panel.BeginConfirmCommand.Execute(null);

            Assert.Equal(AttachmentReviewViewModel.ConfirmWarning, panel.PendingWarningText);
            Assert.Contains("not by themselves an independent basis", panel.PendingWarningText);
        }

        [Fact]
        public void ArmingARevocation_StatesThatHistoryIsKept()
        {
            var panel = Open(out _, candidates: 1, resolvedAsset: 1, effectiveEventID: 10);

            panel.BeginRevokeCommand.Execute(null);

            Assert.Equal(AttachmentReviewViewModel.RevokeWarning, panel.PendingWarningText);
            Assert.Contains("append-only history", panel.PendingWarningText);
        }

        // -----------------------------------------------------------------------------------
        // Required input.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void ADisproof_CannotBeCommittedWithoutAStatedReason()
        {
            var panel = Open(out var executor, candidates: 2);

            panel.SelectedCandidate = panel.Candidates[0];
            panel.BeginDisproveCommand.Execute(null);

            Assert.False(panel.CommitCommand.CanExecute(null));

            panel.DisproofReason = "   ";
            Assert.False(panel.CommitCommand.CanExecute(null));

            panel.DisproofReason = "The recovered copy shows a different room.";
            Assert.True(panel.CommitCommand.CanExecute(null));

            Assert.Equal(0, executor.Invocations);
        }

        /// <remarks>
        /// Both halves are required, and neither is ever chosen or written for the operator. There
        /// is no default basis type and no pre-filled description anywhere in the panel.
        /// </remarks>
        [Fact]
        public void AConfirmation_NeedsBothABasisTypeAndADescriptionTheOperatorSupplied()
        {
            var panel = Open(out _, candidates: 2);

            panel.SelectedCandidate = panel.Candidates[0];
            panel.BeginConfirmCommand.Execute(null);

            Assert.Null(panel.BasisType);
            Assert.Equal(string.Empty, panel.BasisDescription);
            Assert.False(panel.CommitCommand.CanExecute(null));

            panel.BasisDescription = "Recognised the whiteboard from that meeting.";
            Assert.False(panel.CommitCommand.CanExecute(null));

            panel.BasisType = Phase7IndependentBasisType.HumanContentOrContextInspection;
            Assert.True(panel.CommitCommand.CanExecute(null));

            panel.BasisDescription = "  ";
            Assert.False(panel.CommitCommand.CanExecute(null));
        }

        [Fact]
        public void ARevocation_CannotBeCommittedWithoutAStatedReason()
        {
            var panel = Open(out _, candidates: 1, resolvedAsset: 1, effectiveEventID: 10);

            panel.BeginRevokeCommand.Execute(null);

            Assert.False(panel.CommitCommand.CanExecute(null));

            panel.RevocationReason = "Looked again and it does not match.";
            Assert.True(panel.CommitCommand.CanExecute(null));
        }

        /// <remarks>
        /// A review's notes are optional, so it is the one decision that can be committed with the
        /// text boxes untouched.
        /// </remarks>
        [Fact]
        public void AReview_MayBeCommittedWithNoNotes()
        {
            var panel = Open(out _, candidates: 1);

            panel.BeginReviewCommand.Execute(null);

            Assert.Equal(string.Empty, panel.ReviewNotes);
            Assert.True(panel.CommitCommand.CanExecute(null));
        }

        // -----------------------------------------------------------------------------------
        // Which actions apply.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void WithNoCandidateSelected_TheCandidateActionsAreUnavailable()
        {
            var panel = Open(out _, candidates: 2);

            Assert.False(panel.BeginDisproveCommand.CanExecute(null));
            Assert.False(panel.BeginConfirmCommand.CanExecute(null));
            Assert.True(panel.BeginReviewCommand.CanExecute(null));
        }

        [Fact]
        public void WithNoCompletedEvidenceRun_ReviewIsUnavailable()
        {
            var panel = Open(out _, candidates: 0, runID: null);

            Assert.False(panel.BeginReviewCommand.CanExecute(null));
            Assert.Empty(panel.Candidates);
        }

        [Fact]
        public void AnAlreadyDisprovenCandidate_CannotBeDisprovenAgain()
        {
            var panel = Open(out _, candidates: 2, disprovenIndex: 0);

            panel.SelectedCandidate = panel.Candidates[0];
            Assert.False(panel.BeginDisproveCommand.CanExecute(null));

            panel.SelectedCandidate = panel.Candidates[1];
            Assert.True(panel.BeginDisproveCommand.CanExecute(null));
        }

        /// <remarks>
        /// A disproven candidate is still shown, still selectable and still confirmable. Being ruled
        /// out is a recorded finding, not a removal, and the panel must not quietly shorten the list
        /// it is asking a person to judge.
        /// </remarks>
        [Fact]
        public void ADisprovenCandidate_RemainsVisibleAndSelectable()
        {
            var panel = Open(out _, candidates: 3, disprovenIndex: 1);

            Assert.Equal(3, panel.Candidates.Count);
            Assert.True(panel.Candidates[1].IsIndependentlyDisproven);

            panel.SelectedCandidate = panel.Candidates[1];

            Assert.True(panel.BeginConfirmCommand.CanExecute(null));
        }

        /// <remarks>
        /// <strong>No arrangement of candidates makes a confirmation valid on its own.</strong> With
        /// every other candidate disproven, the one that remains still requires a basis type and a
        /// description, and nothing arms or commits itself.
        /// </remarks>
        [Fact]
        public void WithEveryOtherCandidateDisproven_TheLastOneStillNeedsAnIndependentBasis()
        {
            var panel = Open(out var executor, candidates: 2, disprovenIndex: 0);

            panel.SelectedCandidate = panel.Candidates[1];
            panel.BeginConfirmCommand.Execute(null);

            Assert.False(panel.CommitCommand.CanExecute(null));
            Assert.Null(panel.BasisType);
            Assert.Equal(string.Empty, panel.BasisDescription);
            Assert.Equal(0, executor.Invocations);
        }

        [Fact]
        public void OnAnUnresolvedAttachment_ReplaceAndRevokeAreUnavailable()
        {
            var panel = Open(out _, candidates: 2);

            panel.SelectedCandidate = panel.Candidates[0];

            Assert.False(panel.BeginReplaceCommand.CanExecute(null));
            Assert.False(panel.BeginRevokeCommand.CanExecute(null));
            Assert.True(panel.BeginConfirmCommand.CanExecute(null));
        }

        [Fact]
        public void OnAResolvedAttachment_ConfirmIsUnavailableAndReplaceRequiresADifferentAsset()
        {
            var panel = Open(
                out _, candidates: 2, resolvedAsset: 1, effectiveEventID: 10);

            panel.SelectedCandidate = panel.Candidates.Single(c => c.MediaAssetID == 1);

            Assert.False(panel.BeginConfirmCommand.CanExecute(null));

            // Replacing an asset with itself would replace nothing, and Phase 7 refuses it. The
            // panel does not offer it either, while leaving Phase 7 as the enforcement.
            Assert.False(panel.BeginReplaceCommand.CanExecute(null));

            panel.SelectedCandidate = panel.Candidates.Single(c => c.MediaAssetID == 2);

            Assert.True(panel.BeginReplaceCommand.CanExecute(null));
            Assert.True(panel.BeginRevokeCommand.CanExecute(null));
        }

        // -----------------------------------------------------------------------------------
        // Committing, and what happens afterwards.
        // -----------------------------------------------------------------------------------

        [Fact]
        public async Task CommittingADecision_HandsExactlyOneWriteToTheShell()
        {
            var panel = Open(out var executor, candidates: 2);

            panel.SelectedCandidate = panel.Candidates[0];
            panel.BeginDisproveCommand.Execute(null);
            panel.DisproofReason = "Ruled out.";

            await Commit(panel);

            Assert.Equal(1, executor.Invocations);
            Assert.Equal(PendingDecision.None, panel.Pending);
            Assert.Equal("Recorded.", panel.StatusText);
        }

        /// <remarks>
        /// A refusal is shown as Phase 7 worded it, and nothing is attempted again. Phase 7's
        /// messages say what was left unchanged, which is what the operator needs to read.
        /// </remarks>
        [Fact]
        public async Task ARefusedDecision_ShowsThePhase7MessageAndIsNotRetried()
        {
            const string Refusal =
                "The run is no longer this attachment's current evidence run. Nothing has been " +
                "committed and the workspace is unchanged.";

            var panel = Open(out var executor, candidates: 2);

            executor.Failure = new InvalidOperationException(Refusal);

            panel.BeginReviewCommand.Execute(null);

            await Commit(panel);

            Assert.Equal(1, executor.Invocations);
            Assert.Equal(Refusal, panel.StatusText);
            Assert.Equal(PendingDecision.None, panel.Pending);
        }

        // -----------------------------------------------------------------------------------
        // Building a panel.
        // -----------------------------------------------------------------------------------

        private static AttachmentReviewViewModel Open(
            out RecordingExecutor executor,
            int candidates,
            long? runID = 7,
            int? disprovenIndex = null,
            long? resolvedAsset = null,
            long? effectiveEventID = null)
        {
            var recorder = new RecordingExecutor();
            executor = recorder;

            var panel = new AttachmentReviewViewModel(() => "workspace.db", recorder.ExecuteAsync);

            panel.Show(BuildReview(candidates, runID, disprovenIndex, resolvedAsset, effectiveEventID));

            return panel;
        }

        private static LoadedReview BuildReview(
            int candidateCount,
            long? runID,
            int? disprovenIndex,
            long? resolvedAsset,
            long? effectiveEventID)
        {
            var candidates = new List<AttachmentReviewCandidate>();

            for (var index = 0; index < candidateCount; index++)
            {
                candidates.Add(new AttachmentReviewCandidate
                {
                    AttachmentCandidateID = 100 + index,
                    MediaAssetID = index + 1,
                    RunID = runID ?? 0,
                    TierIndex = 1,
                    Direction = Phase7DirectionSupport.Unavailable,
                    OrderSupport = Phase7OrderSupport.Unavailable,
                    IsIndependentlyDisproven = index == disprovenIndex,
                    MediaType = MediaType.Image,
                    SizeBytes = 1024,
                    DurationMS = null,
                    Width = null,
                    Height = null,
                });
            }

            var state = new AttachmentReviewState
            {
                AttachmentID = 42,
                Ordinal = 1,
                Reconstruction = BuildReconstruction(resolvedAsset),
                CurrentEvidenceRunID = runID,
                Candidates = candidates,
                TierCount = candidateCount == 0 ? 0 : 1,
                HasCurrentReviewedInsufficientEvidence = false,
                ResolutionHistory = [],
                CurrentEffectiveEventID = effectiveEventID,
            };

            return new LoadedReview(state, new Dictionary<long, MediaAvailability>());
        }

        private static Phase7ReconstructionState BuildReconstruction(long? resolvedAsset) =>
            new()
            {
                AttachmentID = 42,
                Status = resolvedAsset is null
                    ? Phase7ResolutionStatus.Unresolved
                    : Phase7ResolutionStatus.Resolved,

                ResolvedAsset = resolvedAsset is { } asset
                    ? new Phase7ResolvedAssetDescriptor
                    {
                        MediaAssetID = asset,
                        MediaType = "Image",
                        SizeBytes = 1024,
                        DurationMS = null,
                        Width = null,
                        Height = null,
                    }
                    : null,

                PlaceholderReason = resolvedAsset is null
                    ? Phase7UnresolvedPlaceholderReason.CandidateSetUnranked
                    : null,

                EvidenceRunID = null,
                ExactDateCandidateCount = null,
                AdjacentDateContextCount = null,
                IsWithinDirectionImpossibleGroup = null,
                CandidateTierCount = null,
                HasCurrentReviewedInsufficientEvidence = false,
                AllCurrentCandidatesIndependentlyDisproven = false,
            };

        private static async Task Commit(AttachmentReviewViewModel panel)
        {
            panel.CommitCommand.Execute(null);

            // The command is fire-and-forget by nature; the recorder completes synchronously, so one
            // turn of the scheduler is enough for the continuation to run.
            await Task.Yield();
        }

        /// <summary>
        /// Stands in for the shell: records the decision it is handed and does not run it.
        /// </summary>
        private sealed class RecordingExecutor
        {
            internal int Invocations { get; private set; }

            internal Exception? Failure { get; set; }

            internal Task<Exception?> ExecuteAsync(Action write)
            {
                ArgumentNullException.ThrowIfNull(write);

                Invocations++;

                return Task.FromResult(Failure);
            }
        }
    }
}
