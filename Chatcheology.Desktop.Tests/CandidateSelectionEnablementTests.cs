using Chatcheology.Data.Media;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Viewer;
using Chatcheology.Desktop.Services;
using Chatcheology.Desktop.ViewModels;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// That selecting a candidate actually makes the candidate actions available.
    /// </summary>
    /// <remarks>
    /// <para>The operator's first real review found the panel showing three current candidates,
    /// selecting one, seeing it styled and its media appear — and the Confirm and Disprove buttons
    /// staying greyed out.</para>
    /// <para><strong>Why the existing tests missed it.</strong> They asked the command whether it
    /// could execute, and the predicate answered correctly. A button does not ask; it is told,
    /// through <see cref="System.Windows.Input.ICommand.CanExecuteChanged"/>, and nothing raised
    /// that event when the selection changed. These commands are not wired to WPF's
    /// <c>CommandManager.RequerySuggested</c>, so nothing else ever asked again either.</para>
    /// <para>Every test here therefore watches the notification as well as the predicate. A test
    /// that only called <c>CanExecute</c> would have passed against the broken build.</para>
    /// </remarks>
    public class CandidateSelectionEnablementTests
    {
        /// <remarks>
        /// The operator's path exactly: an unresolved attachment, three current candidates, nothing
        /// selected, then one selected.
        /// </remarks>
        [Fact]
        public void SelectingACandidate_MakesConfirmAndDisproveAvailable()
        {
            var panel = Open(out var executor, candidates: 3);

            using var watcher = new CommandWatcher(panel);

            // Nothing selected: the candidate actions do not apply.
            Assert.False(panel.BeginConfirmCommand.CanExecute(null));
            Assert.False(panel.BeginDisproveCommand.CanExecute(null));

            panel.SelectedCandidate = panel.Candidates[1];

            // The predicate now says yes …
            Assert.True(panel.BeginConfirmCommand.CanExecute(null));
            Assert.True(panel.BeginDisproveCommand.CanExecute(null));

            // … and, the part that actually reaches the buttons, so were they told.
            Assert.True(watcher.ConfirmRaised, "Confirm was never told its availability changed.");
            Assert.True(watcher.DisproveRaised, "Disprove was never told its availability changed.");

            // Still unresolved, so these two correctly do not apply.
            Assert.False(panel.BeginReplaceCommand.CanExecute(null));
            Assert.False(panel.BeginRevokeCommand.CanExecute(null));

            // And looking at a candidate recorded nothing.
            Assert.Equal(0, executor.Invocations);
        }

        /// <remarks>
        /// Arming still requires the deliberate action, and committing still requires the basis or
        /// the reason. Making the buttons reachable does not make a decision cheaper.
        /// </remarks>
        [Fact]
        public void OnceAvailable_TheActionsStillRequireTheirInput()
        {
            var panel = Open(out var executor, candidates: 3);

            panel.SelectedCandidate = panel.Candidates[0];

            panel.BeginConfirmCommand.Execute(null);

            Assert.Equal(PendingDecision.Confirm, panel.Pending);
            Assert.False(panel.CommitCommand.CanExecute(null));

            panel.BasisType = Phase7IndependentBasisType.HumanContentOrContextInspection;
            Assert.False(panel.CommitCommand.CanExecute(null));

            panel.BasisDescription = "Recognised the room from the surrounding conversation.";
            Assert.True(panel.CommitCommand.CanExecute(null));

            Assert.Equal(0, executor.Invocations);
        }

        /// <remarks>
        /// Moving between candidates keeps the actions available and keeps standing down whatever
        /// was armed against the previous one.
        /// </remarks>
        [Fact]
        public void MovingBetweenCandidates_KeepsTheActionsAvailableAndStandsDownTheArmedOne()
        {
            var panel = Open(out _, candidates: 3);

            panel.SelectedCandidate = panel.Candidates[0];
            panel.BeginDisproveCommand.Execute(null);
            panel.DisproofReason = "Ruled out.";

            using var watcher = new CommandWatcher(panel);

            panel.SelectedCandidate = panel.Candidates[2];

            Assert.Equal(PendingDecision.None, panel.Pending);
            Assert.True(panel.BeginConfirmCommand.CanExecute(null));
            Assert.True(panel.BeginDisproveCommand.CanExecute(null));
            Assert.True(watcher.ConfirmRaised);
        }

        /// <remarks>
        /// And clearing the selection takes them away again — told, not merely computed.
        /// </remarks>
        [Fact]
        public void ClearingTheSelection_TakesTheCandidateActionsAwayAgain()
        {
            var panel = Open(out _, candidates: 2);

            panel.SelectedCandidate = panel.Candidates[0];

            using var watcher = new CommandWatcher(panel);

            panel.SelectedCandidate = null;

            Assert.False(panel.BeginConfirmCommand.CanExecute(null));
            Assert.False(panel.BeginDisproveCommand.CanExecute(null));
            Assert.True(watcher.ConfirmRaised);
            Assert.True(watcher.DisproveRaised);
        }

        /// <remarks>
        /// The same notification defect, in a place the operator could not see it: after the panel
        /// closes there is no attachment, so nothing applies — and the buttons have to be told.
        /// </remarks>
        [Fact]
        public void ClosingThePanel_TakesEveryActionAway()
        {
            var panel = Open(out _, candidates: 2);

            panel.SelectedCandidate = panel.Candidates[0];

            using var watcher = new CommandWatcher(panel);

            panel.Close();

            Assert.False(panel.BeginConfirmCommand.CanExecute(null));
            Assert.False(panel.BeginDisproveCommand.CanExecute(null));
            Assert.False(panel.BeginReviewCommand.CanExecute(null));
            Assert.True(watcher.ConfirmRaised);
        }

        /// <remarks>
        /// On a resolved attachment the pairing is the other way round, and selection has to reach
        /// those two commands just the same.
        /// </remarks>
        [Fact]
        public void OnAResolvedAttachment_SelectingADifferentAssetMakesReplaceAvailable()
        {
            var panel = Open(out _, candidates: 2, resolvedAsset: 1, effectiveEventID: 10);

            using var watcher = new CommandWatcher(panel);

            panel.SelectedCandidate = panel.Candidates.Single(c => c.MediaAssetID == 2);

            Assert.True(panel.BeginReplaceCommand.CanExecute(null));
            Assert.False(panel.BeginConfirmCommand.CanExecute(null));
            Assert.True(watcher.ReplaceRaised);
        }

        // -----------------------------------------------------------------------------------
        // Fixtures.
        // -----------------------------------------------------------------------------------

        /// <summary>Records whether each command was told its availability changed.</summary>
        private sealed class CommandWatcher : IDisposable
        {
            private readonly AttachmentReviewViewModel _panel;

            internal CommandWatcher(AttachmentReviewViewModel panel)
            {
                _panel = panel;

                _panel.BeginConfirmCommand.CanExecuteChanged += OnConfirm;
                _panel.BeginDisproveCommand.CanExecuteChanged += OnDisprove;
                _panel.BeginReplaceCommand.CanExecuteChanged += OnReplace;
            }

            internal bool ConfirmRaised { get; private set; }

            internal bool DisproveRaised { get; private set; }

            internal bool ReplaceRaised { get; private set; }

            public void Dispose()
            {
                _panel.BeginConfirmCommand.CanExecuteChanged -= OnConfirm;
                _panel.BeginDisproveCommand.CanExecuteChanged -= OnDisprove;
                _panel.BeginReplaceCommand.CanExecuteChanged -= OnReplace;
            }

            private void OnConfirm(object? sender, EventArgs e) => ConfirmRaised = true;

            private void OnDisprove(object? sender, EventArgs e) => DisproveRaised = true;

            private void OnReplace(object? sender, EventArgs e) => ReplaceRaised = true;
        }

        private static AttachmentReviewViewModel Open(
            out RecordingExecutor executor,
            int candidates,
            long? resolvedAsset = null,
            long? effectiveEventID = null)
        {
            var recorder = new RecordingExecutor();
            executor = recorder;

            var panel = new AttachmentReviewViewModel(() => "workspace.db", recorder.ExecuteAsync);

            panel.Show(BuildReview(candidates, resolvedAsset, effectiveEventID));

            return panel;
        }

        private static LoadedReview BuildReview(
            int candidateCount, long? resolvedAsset, long? effectiveEventID)
        {
            var candidates = new List<AttachmentReviewCandidate>();

            for (var index = 0; index < candidateCount; index++)
            {
                candidates.Add(new AttachmentReviewCandidate
                {
                    AttachmentCandidateID = 100 + index,
                    MediaAssetID = index + 1,
                    RunID = 1,
                    TierIndex = 1,
                    Direction = Phase7DirectionSupport.Supportive,
                    OrderSupport = Phase7OrderSupport.Unavailable,
                    IsIndependentlyDisproven = false,
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

                Reconstruction = new Phase7ReconstructionState
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

                    EvidenceRunID = resolvedAsset is null ? 1 : null,
                    ExactDateCandidateCount = resolvedAsset is null ? candidateCount : null,
                    AdjacentDateContextCount = resolvedAsset is null ? 0 : null,
                    IsWithinDirectionImpossibleGroup = resolvedAsset is null ? false : null,
                    CandidateTierCount = resolvedAsset is null ? 1 : null,
                    HasCurrentReviewedInsufficientEvidence = false,
                    AllCurrentCandidatesIndependentlyDisproven = false,
                },

                CurrentEvidenceRunID = 1,
                Candidates = candidates,
                TierCount = candidateCount == 0 ? 0 : 1,
                HasCurrentReviewedInsufficientEvidence = false,
                ResolutionHistory = [],
                CurrentEffectiveEventID = effectiveEventID,
            };

            return new LoadedReview(state, new Dictionary<long, MediaAvailability>());
        }

        private sealed class RecordingExecutor
        {
            internal int Invocations { get; private set; }

            internal Task<Exception?> ExecuteAsync(Action write)
            {
                ArgumentNullException.ThrowIfNull(write);

                Invocations++;

                return Task.FromResult<Exception?>(null);
            }
        }
    }
}
