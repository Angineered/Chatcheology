using System.Collections.ObjectModel;
using System.Globalization;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Viewer;
using Chatcheology.Desktop.Services;

namespace Chatcheology.Desktop.ViewModels
{
    /// <summary>
    /// Which decision the operator has armed, and not yet committed.
    /// </summary>
    public enum PendingDecision
    {
        /// <summary>None. Nothing is armed and nothing will be written.</summary>
        None,

        /// <summary>Record that the current evidence was reviewed and found insufficient.</summary>
        Review,

        /// <summary>Record an independent disproof against the selected candidate row.</summary>
        Disprove,

        /// <summary>Resolve the attachment to the selected candidate's asset.</summary>
        Confirm,

        /// <summary>Change the resolved asset to the selected candidate's.</summary>
        Replace,

        /// <summary>Return the attachment to unresolved.</summary>
        Revoke,
    }

    /// <summary>
    /// The review panel: one attachment's evidence, its candidates, and the decisions a person may
    /// record about it.
    /// </summary>
    /// <remarks>
    /// <para><strong>Every decision is armed and then committed.</strong> Arming states the
    /// consequence — what will be written, and what cannot be undone — before the button that writes
    /// it exists. Nothing here writes on selection, on opening media, on navigating, or on pressing
    /// Enter in a text box.</para>
    /// <para><strong>It validates its own inputs and nothing else.</strong> A reason or a basis
    /// description must be present before the commit is enabled, because asking Phase 7 to refuse an
    /// obviously empty request is a poor way to tell somebody they left a field blank. Every rule
    /// that decides whether a decision is <em>legitimate</em> stays in Phase 7, and none of them is
    /// restated here.</para>
    /// <para><strong>Ranking never becomes permission.</strong> No command's availability depends on
    /// a tier, on a support value, on how many candidates remain, or on how many have been disproven.
    /// Confirm requires a basis type the operator chose and a description the operator wrote, and
    /// there is no path that supplies either.</para>
    /// </remarks>
    public sealed class AttachmentReviewViewModel : ObservableObject
    {
        /// <summary>What the operator is told before a disproof is written.</summary>
        public const string DisproofWarning =
            "This records an append-only independent disproof for this evidence snapshot. " +
            "There is no undo action for this candidate row.";

        /// <summary>What the operator is told before a confirmation or replacement is written.</summary>
        public const string ConfirmWarning =
            "Candidate tier, direction/order support, uniqueness, filenames and the fact that other " +
            "candidates were disproven are not by themselves an independent basis for confirmation.";

        /// <summary>What the operator is told before a revocation is written.</summary>
        public const string RevokeWarning =
            "Revocation returns this attachment to unresolved. The previous resolution remains in " +
            "append-only history and is not deleted.";

        /// <summary>What the operator is told before a review is recorded.</summary>
        public const string ReviewWarning =
            "This records that the current evidence was reviewed and found insufficient. The " +
            "attachment stays unresolved.";

        private readonly Func<string> _databasePath;
        private readonly Func<Action, Task<Exception?>> _executeDecision;

        private AttachmentReviewState? _state;
        private CandidateViewModel? _selectedCandidate;
        private PendingDecision _pending;
        private Phase7IndependentBasisType? _basisType;
        private string _basisDescription = string.Empty;
        private string _disproofReason = string.Empty;
        private string _revocationReason = string.Empty;
        private string _reviewNotes = string.Empty;
        private string _statusText = string.Empty;
        private string _headlineText = string.Empty;
        private string _evidenceText = string.Empty;
        private bool _isBusy;
        private bool _isOpen;
        private bool _areDecisionsSuspended;

        /// <summary>
        /// Creates the panel.
        /// </summary>
        /// <param name="databasePath">The open workspace's path, read when a request is built.</param>
        /// <param name="executeDecision">
        /// Runs one Phase 7 call with the viewer quiesced, and returns its refusal or null. The panel
        /// supplies the call; the shell owns the session, the coordination and the refresh.
        /// </param>
        public AttachmentReviewViewModel(
            Func<string> databasePath, Func<Action, Task<Exception?>> executeDecision)
        {
            _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));

            _executeDecision = executeDecision
                ?? throw new ArgumentNullException(nameof(executeDecision));

            BeginReviewCommand = new RelayCommand(
                () => Arm(PendingDecision.Review, ReviewWarning), () => CanBeginReview);

            BeginDisproveCommand = new RelayCommand(
                () => Arm(PendingDecision.Disprove, DisproofWarning), () => CanBeginDisprove);

            BeginConfirmCommand = new RelayCommand(
                () => Arm(PendingDecision.Confirm, ConfirmWarning), () => CanBeginConfirm);

            BeginReplaceCommand = new RelayCommand(
                () => Arm(PendingDecision.Replace, ConfirmWarning), () => CanBeginReplace);

            BeginRevokeCommand = new RelayCommand(
                () => Arm(PendingDecision.Revoke, RevokeWarning), () => CanBeginRevoke);

            CancelPendingCommand = new RelayCommand(Disarm, () => Pending != PendingDecision.None);

            CommitCommand = new AsyncRelayCommand(CommitAsync, () => CanCommit);
        }

        // -----------------------------------------------------------------------------------
        // Bound state.
        // -----------------------------------------------------------------------------------

        /// <summary>The current candidates, every one of them.</summary>
        public ObservableCollection<CandidateViewModel> Candidates { get; } = [];

        /// <summary>The attachment's resolution history, in Phase 7's order.</summary>
        public ObservableCollection<ResolutionEventViewModel> History { get; } = [];

        /// <summary>The two basis types Phase 7 accepts, for the operator to choose between.</summary>
        public IReadOnlyList<Phase7IndependentBasisType> BasisTypes { get; } =
        [
            Phase7IndependentBasisType.HumanContentOrContextInspection,
            Phase7IndependentBasisType.ExternalProvenanceOrGroundTruth,
        ];

        /// <summary>Arms a review.</summary>
        public RelayCommand BeginReviewCommand { get; }

        /// <summary>Arms a disproof of the selected candidate.</summary>
        public RelayCommand BeginDisproveCommand { get; }

        /// <summary>Arms a confirmation of the selected candidate.</summary>
        public RelayCommand BeginConfirmCommand { get; }

        /// <summary>Arms a replacement with the selected candidate.</summary>
        public RelayCommand BeginReplaceCommand { get; }

        /// <summary>Arms a revocation.</summary>
        public RelayCommand BeginRevokeCommand { get; }

        /// <summary>Stands back down from an armed decision.</summary>
        public RelayCommand CancelPendingCommand { get; }

        /// <summary>Writes the armed decision. The one action that reaches Phase 7.</summary>
        public AsyncRelayCommand CommitCommand { get; }

        /// <summary>Whether the panel is showing an attachment.</summary>
        public bool IsOpen
        {
            get => _isOpen;
            private set => SetProperty(ref _isOpen, value);
        }

        /// <summary>The attachment under review, or <see langword="null"/>.</summary>
        public long? AttachmentID => _state?.AttachmentID;

        /// <summary>Whether Phase 7 currently records an asset for this attachment.</summary>
        public bool IsResolved => _state?.IsResolved == true;

        /// <summary>The attachment's headline state, in words.</summary>
        public string HeadlineText
        {
            get => _headlineText;
            private set => SetProperty(ref _headlineText, value);
        }

        /// <summary>The evidence summary line.</summary>
        public string EvidenceText
        {
            get => _evidenceText;
            private set => SetProperty(ref _evidenceText, value);
        }

        /// <summary>What just happened, or why a decision was refused.</summary>
        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        /// <summary>Whether a decision is in flight.</summary>
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        /// <summary>
        /// Whether decisions are held off because an archive export or check is running (CH-18).
        /// </summary>
        /// <remarks>
        /// Set by the shell for the whole run, from the first count to the last check, so the workspace
        /// cannot change underneath what is being exported or compared. Nothing armed is stood down;
        /// it simply cannot be armed or committed until the run ends.
        /// </remarks>
        public bool AreDecisionsSuspended
        {
            get => _areDecisionsSuspended;
            internal set
            {
                if (SetProperty(ref _areDecisionsSuspended, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        /// <summary>
        /// The candidate being inspected.
        /// </summary>
        /// <remarks>
        /// Selecting one loads its preview and enables the actions that name a candidate.
        /// <strong>It writes nothing.</strong>
        /// </remarks>
        public CandidateViewModel? SelectedCandidate
        {
            get => _selectedCandidate;
            set
            {
                if (!SetProperty(ref _selectedCandidate, value))
                {
                    return;
                }

                foreach (var candidate in Candidates)
                {
                    candidate.IsSelected = ReferenceEquals(candidate, value);
                }

                // Changing which candidate is being looked at stands down anything armed against
                // the previous one, so a reason typed for one candidate cannot be committed against
                // another.
                Disarm();

                // And the actions that name a candidate have to be told their availability changed.
                // Disarm() only raises when something was actually armed, so on the ordinary path —
                // nothing armed, operator picks a candidate — it raises nothing at all, and a button
                // is told rather than asked: these commands are not wired to WPF's
                // CommandManager.RequerySuggested, so nothing else would ever ask again.
                RaiseCommandStates();
            }
        }

        /// <summary>Which decision is armed.</summary>
        public PendingDecision Pending
        {
            get => _pending;
            private set
            {
                if (SetProperty(ref _pending, value))
                {
                    OnPropertyChanged(nameof(HasPendingDecision));
                    OnPropertyChanged(nameof(NeedsBasis));
                    OnPropertyChanged(nameof(NeedsDisproofReason));
                    OnPropertyChanged(nameof(NeedsRevocationReason));
                    OnPropertyChanged(nameof(NeedsReviewNotes));
                    RaiseCommandStates();
                }
            }
        }

        /// <summary>Whether a decision is armed and awaiting its final action.</summary>
        public bool HasPendingDecision => Pending != PendingDecision.None;

        /// <summary>What the armed decision will do, stated before it is done.</summary>
        public string PendingWarningText
        {
            get;
            private set => SetProperty(ref field, value);
        } = string.Empty;

        /// <summary>The basis type the operator chose. Never chosen for them.</summary>
        public Phase7IndependentBasisType? BasisType
        {
            get => _basisType;
            set
            {
                if (SetProperty(ref _basisType, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        /// <summary>The independent basis the operator wrote. Never pre-filled.</summary>
        public string BasisDescription
        {
            get => _basisDescription;
            set
            {
                if (SetProperty(ref _basisDescription, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        /// <summary>Why this candidate is ruled out.</summary>
        public string DisproofReason
        {
            get => _disproofReason;
            set
            {
                if (SetProperty(ref _disproofReason, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        /// <summary>Why the resolution is being revoked.</summary>
        public string RevocationReason
        {
            get => _revocationReason;
            set
            {
                if (SetProperty(ref _revocationReason, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        /// <summary>Optional notes recorded with a review.</summary>
        public string ReviewNotes
        {
            get => _reviewNotes;
            set => SetProperty(ref _reviewNotes, value);
        }

        /// <summary>Whether a review has already been recorded against the current evidence.</summary>
        public bool HasCurrentReview => _state?.HasCurrentReviewedInsufficientEvidence == true;

        /// <summary>Whether this attachment has any resolution history to show.</summary>
        public bool HasHistory => History.Count > 0;

        /// <summary>Whether the armed decision needs an independent basis.</summary>
        public bool NeedsBasis =>
            Pending is PendingDecision.Confirm or PendingDecision.Replace;

        /// <summary>Whether the armed decision needs a disproof reason.</summary>
        public bool NeedsDisproofReason => Pending == PendingDecision.Disprove;

        /// <summary>Whether the armed decision needs a revocation reason.</summary>
        public bool NeedsRevocationReason => Pending == PendingDecision.Revoke;

        /// <summary>Whether the armed decision offers optional notes.</summary>
        public bool NeedsReviewNotes => Pending == PendingDecision.Review;

        // -----------------------------------------------------------------------------------
        // Command availability.
        // -----------------------------------------------------------------------------------

        private bool Idle =>
            !IsBusy && !AreDecisionsSuspended && Pending == PendingDecision.None && _state is not null;

        private bool CanBeginReview => Idle && _state!.CurrentEvidenceRunID is not null;

        private bool CanBeginDisprove =>
            Idle
            && SelectedCandidate is { IsIndependentlyDisproven: false };

        private bool CanBeginConfirm => Idle && !IsResolved && SelectedCandidate is not null;

        private bool CanBeginReplace =>
            Idle
            && IsResolved
            && SelectedCandidate is { } candidate
            && candidate.MediaAssetID != _state!.Reconstruction.ResolvedAsset?.MediaAssetID;

        private bool CanBeginRevoke =>
            Idle && IsResolved && _state!.CurrentEffectiveEventID is not null;

        private bool CanCommit => !IsBusy && !AreDecisionsSuspended && Pending switch
        {
            PendingDecision.Review => true,
            PendingDecision.Disprove => Stated(DisproofReason),
            PendingDecision.Confirm or PendingDecision.Replace =>
                BasisType is not null && Stated(BasisDescription),
            PendingDecision.Revoke => Stated(RevocationReason),
            _ => false,
        };

        private static bool Stated(string value) => !string.IsNullOrWhiteSpace(value);

        // -----------------------------------------------------------------------------------
        // Loading.
        // -----------------------------------------------------------------------------------

        /// <summary>Shows one attachment's review state.</summary>
        internal void Show(LoadedReview review)
        {
            ArgumentNullException.ThrowIfNull(review);

            var previouslySelected = SelectedCandidate?.AttachmentCandidateID;

            _state = review.State;
            _selectedCandidate = null;

            Candidates.Clear();

            foreach (var candidate in review.State.Candidates)
            {
                review.Media.TryGetValue(candidate.MediaAssetID, out var media);

                Candidates.Add(new CandidateViewModel(candidate, review.State.TierCount, media));
            }

            History.Clear();

            foreach (var entry in review.State.ResolutionHistory)
            {
                History.Add(new ResolutionEventViewModel(entry));
            }

            HeadlineText = Headline(review.State);
            EvidenceText = Evidence(review.State);

            IsOpen = true;

            Disarm();

            // Restored by candidate row identity, never by object identity: a refresh rebuilds every
            // view-model, and a re-read after a write may not contain the same rows at all.
            SelectedCandidate = previouslySelected is { } identifier
                ? Candidates.FirstOrDefault(c => c.AttachmentCandidateID == identifier)
                : null;

            OnPropertyChanged(nameof(AttachmentID));
            OnPropertyChanged(nameof(IsResolved));
            OnPropertyChanged(nameof(HasCurrentReview));
            OnPropertyChanged(nameof(HasHistory));
            RaiseCommandStates();
        }

        /// <summary>Closes the panel without touching the conversation behind it.</summary>
        public void Close()
        {
            _state = null;
            _selectedCandidate = null;

            Candidates.Clear();
            History.Clear();

            IsOpen = false;
            StatusText = string.Empty;

            Disarm();
            RaiseCommandStates();

            OnPropertyChanged(nameof(AttachmentID));
            OnPropertyChanged(nameof(IsResolved));
            OnPropertyChanged(nameof(HasCurrentReview));
            OnPropertyChanged(nameof(HasHistory));
            OnPropertyChanged(nameof(SelectedCandidate));
        }

        // -----------------------------------------------------------------------------------
        // Arming and committing.
        // -----------------------------------------------------------------------------------

        private void Arm(PendingDecision decision, string warning)
        {
            PendingWarningText = warning;
            StatusText = string.Empty;
            Pending = decision;
        }

        private void Disarm()
        {
            PendingWarningText = string.Empty;
            Pending = PendingDecision.None;
        }

        /// <remarks>
        /// Builds the request, hands exactly one Phase 7 call to the shell, and says what came back.
        /// It does not refresh: the shell re-reads persisted state and calls
        /// <see cref="Show"/> again, so what appears afterwards was read from the workspace rather
        /// than assumed from the request.
        /// </remarks>
        private async Task CommitAsync()
        {
            if (!CanCommit || _state is null)
            {
                return;
            }

            var write = BuildWrite(_state, Pending);

            if (write is null)
            {
                return;
            }

            IsBusy = true;

            try
            {
                var failure = await _executeDecision(write).ConfigureAwait(true);

                StatusText = failure is null
                    ? "Recorded."
                    : failure.Message;
            }
            finally
            {
                IsBusy = false;
                Disarm();
            }
        }

        /// <remarks>
        /// <para>The two meanings of a source run are kept apart here, because Phase 7 keeps them
        /// apart. A review must name the attachment's <em>current</em> run and is refused otherwise.
        /// A confirmation or replacement cites the candidate's run as truthful provenance, which
        /// Phase 7 checks for existence and coverage but deliberately not for currency — it is a
        /// record of what was on screen, never the reason the decision is valid. A revocation cites
        /// nothing, because it rests on no snapshot.</para>
        /// <para>Empty optional text is sent as null rather than as an empty string: Phase 7 refuses
        /// whitespace-only optional values, and an untouched notes box is an absence rather than a
        /// blank.</para>
        /// </remarks>
        private Action? BuildWrite(AttachmentReviewState state, PendingDecision decision)
        {
            var path = _databasePath();

            switch (decision)
            {
                case PendingDecision.Review when state.CurrentEvidenceRunID is { } runID:
                {
                    var request = new Phase7ReviewRequest
                    {
                        DatabasePath = path,
                        AttachmentID = state.AttachmentID,
                        SourceRunID = runID,
                        ReviewNotes = Stated(ReviewNotes) ? ReviewNotes.Trim() : null,
                        ActorLabel = null,
                    };

                    return () => new Phase7ReviewService().Record(request);
                }

                case PendingDecision.Disprove when SelectedCandidate is { } candidate:
                {
                    var request = new Phase7CandidateDispositionRequest
                    {
                        DatabasePath = path,

                        // The candidate row, never a rebuilt attachment/run/asset tuple. Phase 7
                        // reads the row's own identity, which is what makes it unspoofable.
                        AttachmentCandidateID = candidate.AttachmentCandidateID,

                        Reason = DisproofReason.Trim(),
                        ActorLabel = null,
                    };

                    return () => new Phase7CandidateDispositionService().Record(request);
                }

                case PendingDecision.Confirm
                    when SelectedCandidate is { } candidate && BasisType is { } basis:
                {
                    var request = new Phase7ConfirmRequest
                    {
                        DatabasePath = path,
                        AttachmentID = state.AttachmentID,
                        MediaAssetID = candidate.MediaAssetID,
                        IndependentBasisType = basis,
                        IndependentBasisDescription = BasisDescription.Trim(),
                        SourceRunID = candidate.RunID,
                        ActorLabel = null,
                    };

                    return () => new Phase7ResolutionService().Confirm(request);
                }

                case PendingDecision.Replace
                    when SelectedCandidate is { } candidate
                        && BasisType is { } basis
                        && state.CurrentEffectiveEventID is { } effective:
                {
                    var request = new Phase7ConfirmReplacementRequest
                    {
                        DatabasePath = path,
                        AttachmentID = state.AttachmentID,
                        MediaAssetID = candidate.MediaAssetID,
                        SupersedesEventID = effective,
                        IndependentBasisType = basis,
                        IndependentBasisDescription = BasisDescription.Trim(),
                        SourceRunID = candidate.RunID,
                        ActorLabel = null,
                    };

                    return () => new Phase7ResolutionService().ConfirmReplacement(request);
                }

                case PendingDecision.Revoke when state.CurrentEffectiveEventID is { } effective:
                {
                    var request = new Phase7RevokeRequest
                    {
                        DatabasePath = path,
                        AttachmentID = state.AttachmentID,
                        SupersedesEventID = effective,
                        RevocationReason = RevocationReason.Trim(),
                        SourceRunID = null,
                        ActorLabel = null,
                    };

                    return () => new Phase7ResolutionService().Revoke(request);
                }

                default:
                    return null;
            }
        }

        // -----------------------------------------------------------------------------------
        // Wording.
        // -----------------------------------------------------------------------------------

        private static string Headline(AttachmentReviewState state) =>
            state.IsResolved
                ? "Resolved media"
                : "Media not yet matched";

        /// <remarks>
        /// Counts and reasons, and no judgement. The candidate count is a count, not a shortlist
        /// length, and "one candidate" is deliberately not phrased as though it were close to an
        /// answer.
        /// </remarks>
        private static string Evidence(AttachmentReviewState state)
        {
            if (state.CurrentEvidenceRunID is not { } runID)
            {
                return "No completed evidence run has evaluated this attachment.";
            }

            var parts = new List<string>
            {
                $"evidence run {Text(runID)}",
                Count(state.Candidates.Count, "current candidate"),
            };

            var disproven = state.Candidates.Count(candidate => candidate.IsIndependentlyDisproven);

            if (disproven > 0)
            {
                parts.Add($"{Text(disproven)} independently disproven");
            }

            if (state.Reconstruction.AdjacentDateContextCount is { } adjacent and > 0)
            {
                parts.Add($"{Count(adjacent, "adjacent-date context row")} (context only)");
            }

            if (state.HasCurrentReviewedInsufficientEvidence)
            {
                parts.Add("reviewed — insufficient evidence");
            }

            return string.Join(" · ", parts);
        }

        private void RaiseCommandStates()
        {
            BeginReviewCommand.RaiseCanExecuteChanged();
            BeginDisproveCommand.RaiseCanExecuteChanged();
            BeginConfirmCommand.RaiseCanExecuteChanged();
            BeginReplaceCommand.RaiseCanExecuteChanged();
            BeginRevokeCommand.RaiseCanExecuteChanged();
            CancelPendingCommand.RaiseCanExecuteChanged();
            CommitCommand.RaiseCanExecuteChanged();
        }

        private static string Count(int value, string noun) =>
            value == 1 ? $"1 {noun}" : $"{Text(value)} {noun}s";

        private static string Text(long value) => value.ToString(CultureInfo.CurrentCulture);
    }
}
