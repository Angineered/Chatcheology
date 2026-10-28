using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using Chatcheology.Archive;
using Chatcheology.Data.Viewer;
using Chatcheology.Desktop.Services;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Memories;

namespace Chatcheology.Desktop.ViewModels
{
    /// <summary>
    /// The viewer: one open workspace, one conversation, one bounded page at a time.
    /// </summary>
    /// <remarks>
    /// <para><strong>Read-only, by construction.</strong> Every workspace is opened through
    /// <see cref="WorkspaceSession"/>, whose connection SQLite itself will not let anything write
    /// to. There is no confirm, replace, revoke or review action here; those belong to the phase
    /// that adds them deliberately, through the services that already exist for them.</para>
    /// <para><strong>Nothing about the operator is remembered</strong>, with one exception. The
    /// local-participant selection lives in this object and nowhere else — not in the workspace, not
    /// in a settings file, not in the registry — and goes when the workspace is closed. The exception
    /// is the memory layer switches and filters (CH-42 step 5), remembered per workspace in a small
    /// local settings file keyed by a hash of the workspace path (<see cref="LayerSettings"/>).</para>
    /// <para><strong>Memory layers (CH-42 step 5).</strong> With a layer on, the page also carries
    /// memory rows from the memories file beside the workspace, placed among the messages by the phone's
    /// own rules. They live in <see cref="Rows"/> beside the messages; <see cref="Messages"/> stays the
    /// page's messages only, and with every layer off <see cref="Rows"/> is exactly those messages.
    /// Selecting a memory row opens its details and never touches the review panel.</para>
    /// </remarks>
    public sealed class MainViewModel : ObservableObject
    {
        private readonly Func<string?> _chooseWorkspace;

        private readonly DecisionCoordinator _decisions = new();

        private WorkspaceSession? _session;
        private CancellationTokenSource? _navigation;

        /// <summary>
        /// The read currently in flight, so it can be awaited before the session is disposed.
        /// </summary>
        /// <remarks>
        /// Cancelling is not enough. <see cref="WorkspaceSession.Dispose"/> disposes the gate that
        /// serialises reads, and a read already past its cancellation check would be holding it. A
        /// decision waits for this task to finish before anything is closed.
        /// </remarks>
        private Task _outstandingRead = Task.CompletedTask;

        /// <summary>The memory layers' read of the message times (CH-42 step 5), awaited the same way.</summary>
        private Task _layersRead = Task.CompletedTask;

        private ConversationSummary? _selectedConversation;
        private ConversationParticipantSummary? _localParticipant;
        private ConversationPage? _page;
        private DateTime? _jumpDate;
        private string _statusText = "Open a Chatcheology workspace to begin.";
        private string _workspaceText = "No workspace open";
        private string _rangeText = string.Empty;
        private string _conversationTitle = string.Empty;
        private bool _isBusy;
        private bool _hasLowQualityCopies;
        private bool _showLowQualityCopies;
        private bool _showDetails;

        // Memory layers (CH-42 step 5).
        private readonly LayerSettings _layerSettings;
        private ConcurrentDictionary<string, ImageSource?> _images = new(StringComparer.OrdinalIgnoreCase);
        private LoadedMemoryLayers _memoryLayers = LoadedMemoryLayers.None;
        private MemoryLayout _layout = MemoryLayout.Empty;
        private ConversationMessageTimes? _messageTimes;
        private long? _messageTimesFor;
        private int _layersVersion;
        private int _composeVersion;
        private bool _isLayersOpen;
        private string _layersNote = string.Empty;
        private MemoryDetailsViewModel? _memoryDetails;
        private MemoryRowViewModel? _selectedMemoryRow;

        /// <summary>
        /// Creates the viewer.
        /// </summary>
        /// <param name="chooseWorkspace">
        /// Asks the operator for a workspace file, returning null if they decline. Supplied by the
        /// window, because a file dialog is a window's business rather than a view-model's.
        /// </param>
        public MainViewModel(Func<string?> chooseWorkspace)
            : this(chooseWorkspace, ArchiveDialogs.None, new ArchiveOperations(), MemoriesDialogs.None)
        {
        }

        /// <summary>
        /// Creates the viewer with the archive work's dialogs and operations (CH-18).
        /// </summary>
        /// <param name="chooseWorkspace">As for the public constructor.</param>
        /// <param name="archiveDialogs">The window's confirmation, folder and open-folder dialogs.</param>
        /// <param name="archiveOperations">Counting, exporting and checking; a seam for tests.</param>
        /// <param name="memoriesDialogs">The Memories area's dialogs (CH-42 step 4).</param>
        /// <param name="memoriesBackend">The memories file and messages for a context; a seam for tests.</param>
        /// <param name="layerSettings">Where layer choices are remembered (CH-42 step 5); the default file when null.</param>
        internal MainViewModel(
            Func<string?> chooseWorkspace,
            ArchiveDialogs archiveDialogs,
            IArchiveOperations archiveOperations,
            MemoriesDialogs? memoriesDialogs = null,
            Func<MemoriesContext, MemoriesBackend>? memoriesBackend = null,
            LayerSettings? layerSettings = null)
        {
            _chooseWorkspace = chooseWorkspace
                ?? throw new ArgumentNullException(nameof(chooseWorkspace));

            _layerSettings = layerSettings ?? new LayerSettings();

            Review = new AttachmentReviewViewModel(
                () => _session?.DatabasePath ?? string.Empty,
                ExecuteDecisionAsync);

            Archive = new ArchiveViewModel(
                archiveOperations,
                archiveDialogs,
                ArchiveContext,
                message => StatusText = message,
                () => IsBusy || Review.IsBusy || Memories?.IsWriting == true);

            // The Memories area writes only the memories file, and never while an export or a check
            // reads it, or while a decision is in flight.
            Memories = new MemoriesViewModel(
                memoriesDialogs ?? MemoriesDialogs.None,
                MemoriesContext,
                () => Archive.IsRunning || Review.IsBusy,
                memoriesBackend);

            Memories.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MemoriesViewModel.IsWriting))
                {
                    Archive.RaiseCommandStates();
                    OpenWorkspaceCommand?.RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(CanChangeConversation));
                }
            };

            // A save, rename or delete in the Memories area shows in the conversation at once.
            Memories.FileChanged += (_, _) => _ = ReloadLayersAsync();

            CloseMemoryDetailsCommand = new RelayCommand(CloseMemoryDetails, () => MemoryDetails is not null);
            EditMemoryCommand = new AsyncRelayCommand(
                EditMemoryAsync,
                () => MemoryDetails?.IsPersonal == true && !Archive.IsRunning && !Review.IsBusy);

            // A decision in flight holds the archive commands off, and they are told when it ends.
            Review.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AttachmentReviewViewModel.IsBusy))
                {
                    Archive.RaiseCommandStates();
                    Memories.RaiseCommandStates();
                    EditMemoryCommand?.RaiseCanExecuteChanged();
                }
            };

            // While an archive is exported or checked, no decision can change the workspace under it,
            // and no other workspace can be opened in its place.
            Archive.RunningChanged += (_, _) =>
            {
                Review.AreDecisionsSuspended = Archive.IsRunning;
                OpenWorkspaceCommand?.RaiseCanExecuteChanged();
                Memories.RaiseCommandStates();
                EditMemoryCommand?.RaiseCanExecuteChanged();
            };

            // Nor while the Memories area is writing the memories file beside this workspace.
            OpenWorkspaceCommand = new AsyncRelayCommand(
                OpenWorkspaceAsync, () => !Archive.IsRunning && !Memories.IsWriting);

            PreviousCommand = new AsyncRelayCommand(
                () => NavigateAsync(PageDirection.Before, _page?.FirstSequenceNumber ?? 1),
                () => _page?.HasEarlier == true);

            NextCommand = new AsyncRelayCommand(
                () => NavigateAsync(PageDirection.From, (_page?.LastSequenceNumber ?? 0) + 1),
                () => _page?.HasLater == true);

            JumpToDateCommand = new AsyncRelayCommand(
                JumpToDateAsync,
                () => _session is not null && _jumpDate is not null);
        }

        // -----------------------------------------------------------------------------------
        // Bound state.
        // -----------------------------------------------------------------------------------

        /// <summary>The conversations the open workspace holds.</summary>
        public ObservableCollection<ConversationSummary> Conversations { get; } = [];

        /// <summary>The participants of the selected conversation.</summary>
        public ObservableCollection<ConversationParticipantSummary> Participants { get; } = [];

        /// <summary>The messages of the page currently on screen.</summary>
        public ObservableCollection<MessageViewModel> Messages { get; } = [];

        /// <summary>
        /// What the conversation list shows: the page's messages and, with a memory layer on, its memory
        /// rows among them (CH-42 step 5). With every layer off, exactly <see cref="Messages"/>.
        /// </summary>
        public ObservableCollection<object> Rows { get; } = [];

        /// <summary>One switch per memory collection, with an imported one's filters (CH-42 step 5).</summary>
        public ObservableCollection<MemoryLayerViewModel> Layers { get; } = [];

        /// <summary>Whether the Layers control has anything to offer.</summary>
        public bool HasLayers => Layers.Count > 0 || LayersNote.Length > 0;

        /// <summary>Whether the Layers control is open.</summary>
        public bool IsLayersOpen
        {
            get => _isLayersOpen;
            set => SetProperty(ref _isLayersOpen, value);
        }

        /// <summary>
        /// Why a layer is not offered (an imported collection needs "Show as"), or why there are none (the
        /// memories file could not be read); empty otherwise.
        /// </summary>
        public string LayersNote
        {
            get => _layersNote;
            private set
            {
                if (SetProperty(ref _layersNote, value))
                {
                    OnPropertyChanged(nameof(HasLayersNote));
                    OnPropertyChanged(nameof(HasLayers));
                }
            }
        }

        /// <summary>Whether there is a note.</summary>
        public bool HasLayersNote => LayersNote.Length > 0;

        /// <summary>The selected memory row's details, or <see langword="null"/> when none is open.</summary>
        public MemoryDetailsViewModel? MemoryDetails
        {
            get => _memoryDetails;
            private set
            {
                if (SetProperty(ref _memoryDetails, value))
                {
                    OnPropertyChanged(nameof(IsMemoryDetailsOpen));
                    CloseMemoryDetailsCommand.RaiseCanExecuteChanged();
                    EditMemoryCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>Whether the memory details area is showing.</summary>
        public bool IsMemoryDetailsOpen => MemoryDetails is not null;

        /// <summary>Closes the memory details.</summary>
        public RelayCommand CloseMemoryDetailsCommand { get; }

        /// <summary>Opens the selected personal memory in the Memories area's form (CH-42 step 4).</summary>
        public AsyncRelayCommand EditMemoryCommand { get; }

        /// <summary>The memory layers as read; for tests.</summary>
        internal LoadedMemoryLayers MemoryLayers => _memoryLayers;

        /// <summary>The last reload of the memory layers (for tests to wait on).</summary>
        internal Task LayersLoad { get; private set; } = Task.CompletedTask;

        /// <summary>The review panel, open or closed.</summary>
        public AttachmentReviewViewModel Review { get; }

        /// <summary>The archive export and check panel (CH-18).</summary>
        public ArchiveViewModel Archive { get; }

        /// <summary>Personal memories beside the open workspace (CH-42 step 4).</summary>
        public MemoriesViewModel Memories { get; }

        /// <summary>Exports the selected conversation to a new portable archive folder.</summary>
        public AsyncRelayCommand ExportArchiveCommand => Archive.ExportCommand;

        /// <summary>Checks an existing archive folder.</summary>
        public AsyncRelayCommand ValidateArchiveCommand => Archive.ValidateCommand;

        /// <summary>
        /// Raised when the operator has moved to a different page — another conversation, the
        /// previous or next page, or a chosen date — and its messages are now in
        /// <see cref="Messages"/>.
        /// </summary>
        /// <remarks>
        /// The view starts such a page from its top (CH-12): the list's scroll offset belongs to
        /// the page it was measured on, and kept across a move it would land the operator somewhere
        /// in the new page other than the start they asked for. Not raised when the same page is
        /// rebuilt after a decision, nor by opening or closing the review panel, so the operator's
        /// place within the page survives both.
        /// </remarks>
        public event EventHandler? MovedToPage;

        /// <summary>Opens a workspace file.</summary>
        public AsyncRelayCommand OpenWorkspaceCommand { get; }

        /// <summary>Moves back one page.</summary>
        public AsyncRelayCommand PreviousCommand { get; }

        /// <summary>Moves forward one page.</summary>
        public AsyncRelayCommand NextCommand { get; }

        /// <summary>Moves to the first message on or after the chosen date.</summary>
        public AsyncRelayCommand JumpToDateCommand { get; }

        /// <summary>Whether another conversation may be chosen: not while the Memories area is writing.</summary>
        public bool CanChangeConversation => !Memories.IsWriting;

        /// <summary>The conversation being browsed.</summary>
        public ConversationSummary? SelectedConversation
        {
            get => _selectedConversation;
            set
            {
                // While the Memories area is writing, its message links name this conversation; the
                // choice is refused and the list told to show the current one again.
                if (Memories.IsWriting && !ReferenceEquals(value, _selectedConversation))
                {
                    OnPropertyChanged();
                    return;
                }

                if (!SetProperty(ref _selectedConversation, value))
                {
                    return;
                }

                ConversationTitle = value?.Title ?? string.Empty;

                // Message links name this conversation's messages, so the Memories area starts again.
                Memories.Close();

                Participants.Clear();

                foreach (var participant in value?.Participants ?? [])
                {
                    Participants.Add(participant);
                }

                // As the property would, without a second layer reload: the one below covers it.
                if (_localParticipant is not null)
                {
                    _localParticipant = null;
                    OnPropertyChanged(nameof(LocalParticipant));
                    ApplyAlignment();
                }

                ResetLayers();

                _ = NavigateAsync(PageDirection.First, 0);
                _ = ReloadLayersAsync();
            }
        }

        /// <summary>
        /// The participant the operator has said to show as themselves, for this session only.
        /// </summary>
        /// <remarks>
        /// Null is the state the viewer opens in, and it is not a gap to be filled: the workspace
        /// records no exporting participant, so until somebody says otherwise no message claims to
        /// be anybody's. This selection is never written anywhere.
        /// </remarks>
        public ConversationParticipantSummary? LocalParticipant
        {
            get => _localParticipant;
            set
            {
                if (SetProperty(ref _localParticipant, value))
                {
                    ApplyAlignment();

                    // Imported memory items name the participants by role: Local is this one.
                    _ = ReloadLayersAsync();
                }
            }
        }

        /// <summary>The date the jump control is set to.</summary>
        public DateTime? JumpDate
        {
            get => _jumpDate;
            set
            {
                if (SetProperty(ref _jumpDate, value))
                {
                    JumpToDateCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>The selected conversation's title.</summary>
        public string ConversationTitle
        {
            get => _conversationTitle;
            private set => SetProperty(ref _conversationTitle, value);
        }

        /// <summary>What just happened, or what to do next.</summary>
        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        /// <summary>The open workspace's path, or a note that there is none.</summary>
        public string WorkspaceText
        {
            get => _workspaceText;
            private set => SetProperty(ref _workspaceText, value);
        }

        /// <summary>Which part of the conversation is on screen.</summary>
        public string RangeText
        {
            get => _rangeText;
            private set => SetProperty(ref _rangeText, value);
        }

        /// <summary>
        /// Whether the open workspace records any low-quality copy (CH-10). The toolbar toggle is
        /// offered only then, so a workspace with no msgstore facts looks exactly as before.
        /// </summary>
        public bool HasLowQualityCopies
        {
            get => _hasLowQualityCopies;
            private set => SetProperty(ref _hasLowQualityCopies, value);
        }

        /// <summary>
        /// Whether low-quality copies are shown, for this session only. Off by default: WhatsApp
        /// does not show them either. Toggling is presentation and never a move, so the operator's
        /// place on the page is kept.
        /// </summary>
        public bool ShowLowQualityCopies
        {
            get => _showLowQualityCopies;
            set
            {
                if (!SetProperty(ref _showLowQualityCopies, value))
                {
                    return;
                }

                foreach (var message in Messages)
                {
                    message.SetShowLowQualityCopies(value);
                }
            }
        }

        /// <summary>
        /// Whether the attachment cards show their details, for this session only (CH-20). Off by
        /// default: a card then shows the media and only as much wording as it needs. Toggling is
        /// presentation and never a move, so the operator's place on the page is kept.
        /// </summary>
        public bool ShowDetails
        {
            get => _showDetails;
            set
            {
                if (!SetProperty(ref _showDetails, value))
                {
                    return;
                }

                foreach (var message in Messages)
                {
                    message.SetShowDetails(value);
                }
            }
        }

        /// <summary>Whether a read or a decision is in flight.</summary>
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    Archive.RaiseCommandStates();
                }
            }
        }

        // -----------------------------------------------------------------------------------
        // Opening.
        // -----------------------------------------------------------------------------------

        /// <remarks>
        /// A refused workspace leaves the previous one open and untouched. Nothing is created and
        /// nothing is migrated: a viewer pointed at an older workspace says so rather than
        /// quietly changing it into one it can read.
        /// </remarks>
        private async Task OpenWorkspaceAsync()
        {
            var path = _chooseWorkspace();

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            WorkspaceSession session;

            try
            {
                session = await Task.Run(() => WorkspaceSession.Open(path)).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                StatusText = exception.Message;

                return;
            }

            CloseSession();

            _session = session;
            WorkspaceText = session.DatabasePath;

            try
            {
                var conversations = await session
                    .ReadConversationsAsync(CancellationToken.None)
                    .ConfigureAwait(true);

                HasLowQualityCopies = await session
                    .HasAnyLowQualityCopyAsync(CancellationToken.None)
                    .ConfigureAwait(true);

                Conversations.Clear();

                foreach (var conversation in conversations)
                {
                    Conversations.Add(conversation);
                }

                JumpToDateCommand.RaiseCanExecuteChanged();

                if (Conversations.Count == 0)
                {
                    StatusText = "That workspace holds no conversation.";

                    return;
                }

                StatusText = Conversations.Count == 1
                    ? "Workspace opened read-only."
                    : $"Workspace opened read-only. {Text(Conversations.Count)} conversations.";

                SelectedConversation = Conversations[0];
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                StatusText = exception.Message;
            }
        }

        /// <summary>
        /// Says something in the status line, for a failure the window noticed rather than a read.
        /// </summary>
        public void ReportStatus(string message) => StatusText = message;

        /// <summary>Closes the open workspace, if there is one.</summary>
        public void CloseSession()
        {
            _navigation?.Cancel();
            _navigation = null;

            _session?.Dispose();
            _session = null;

            Review.Close();
            Memories.Close();

            Messages.Clear();
            Participants.Clear();
            Conversations.Clear();

            ResetLayers();
            Rows.Clear();

            _page = null;
            _selectedConversation = null;
            _localParticipant = null;

            HasLowQualityCopies = false;
            _showLowQualityCopies = false;
            OnPropertyChanged(nameof(ShowLowQualityCopies));
            _showDetails = false;
            OnPropertyChanged(nameof(ShowDetails));

            RangeText = string.Empty;
            ConversationTitle = string.Empty;
            WorkspaceText = "No workspace open";

            RaiseNavigationCanExecuteChanged();
        }

        // -----------------------------------------------------------------------------------
        // Navigating.
        // -----------------------------------------------------------------------------------

        private async Task JumpToDateAsync()
        {
            if (_session is null || SelectedConversation is null || JumpDate is not { } requested)
            {
                return;
            }

            var date = DateOnly.FromDateTime(requested);

            try
            {
                var start = await _session
                    .FindFirstSequenceNumberOnOrAfterAsync(
                        SelectedConversation.ConversationID, date, CancellationToken.None)
                    .ConfigureAwait(true);

                if (start is { } sequenceNumber)
                {
                    await NavigateAsync(PageDirection.From, sequenceNumber).ConfigureAwait(true);

                    return;
                }

                // Not a failure: the conversation simply ends before that date. The last page is
                // where the operator was asking to go, so that is where they are taken.
                await NavigateAsync(PageDirection.Last, 0).ConfigureAwait(true);

                StatusText =
                    $"No message falls on or after {date.ToString("d MMMM yyyy", Culture)}. " +
                    $"Showing the end of the conversation.";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                StatusText = exception.Message;
            }
        }

        /// <remarks>
        /// Each navigation cancels the one before it. Cancellation exists to discard a result
        /// nobody is waiting for any more; a page read is short, and nothing here depends on
        /// stopping a statement part way through.
        /// </remarks>
        /// <remarks>
        /// Retains the running read so a decision can wait for it. Every path that reads the
        /// workspace goes through here or through <see cref="OpenReviewAsync"/>, and both record
        /// what they started.
        /// </remarks>
        /// <param name="isMove">
        /// False only when the page on screen is being rebuilt in place, so the view keeps the
        /// operator's scroll position; see <see cref="MovedToPage"/>.
        /// </param>
        private Task NavigateAsync(PageDirection direction, int sequenceNumber, bool isMove = true)
        {
            var task = NavigateCoreAsync(direction, sequenceNumber, isMove);
            _outstandingRead = task;

            return task;
        }

        private async Task NavigateCoreAsync(
            PageDirection direction, int sequenceNumber, bool isMove)
        {
            if (_session is null || SelectedConversation is null)
            {
                return;
            }

            _navigation?.Cancel();
            _navigation?.Dispose();

            var navigation = new CancellationTokenSource();
            _navigation = navigation;

            IsBusy = true;

            try
            {
                var loaded = await _session
                    .LoadPageAsync(
                        SelectedConversation.ConversationID,
                        direction,
                        sequenceNumber,
                        ConversationReader.DefaultPageSize,
                        navigation.Token)
                    .ConfigureAwait(true);

                var brushes = BuildBrushes(SelectedConversation);
                var showLowQualityCopies = ShowLowQualityCopies;

                var layout = _layout;
                var images = _images;

                var (built, memoryRows) = await Task
                    .Run(
                        () => (Build(loaded, brushes, showLowQualityCopies), BuildMemoryRows(loaded.Page, layout, images)),
                        navigation.Token)
                    .ConfigureAwait(true);

                if (navigation.IsCancellationRequested)
                {
                    return;
                }

                _page = loaded.Page;

                Messages.Clear();

                foreach (var message in built)
                {
                    // The toggles may have moved while the page was being built.
                    message.SetShowLowQualityCopies(ShowLowQualityCopies);
                    message.SetShowDetails(ShowDetails);
                    Messages.Add(message);
                }

                ApplyAlignment();

                _composeVersion++;
                ComposeRows(memoryRows);

                // The layers changed while the page was read: place the current ones.
                if (!ReferenceEquals(layout, _layout))
                {
                    _ = ComposeAsync();
                }

                if (isMove)
                {
                    MovedToPage?.Invoke(this, EventArgs.Empty);
                }

                RangeText = DescribeRange(loaded.Page, SelectedConversation);
                StatusText = DescribePage(loaded);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a later navigation. The newer one owns the UI.
            }
            catch (Exception exception)
            {
                StatusText = exception.Message;
            }
            finally
            {
                if (ReferenceEquals(_navigation, navigation))
                {
                    IsBusy = false;
                    RaiseNavigationCanExecuteChanged();
                }
            }
        }

        // -----------------------------------------------------------------------------------
        // Reviewing and deciding.
        // -----------------------------------------------------------------------------------

        /// <summary>Opens the review panel on one attachment, without disturbing the page.</summary>
        /// <remarks>
        /// A read like any other. It records nothing and changes nothing: opening the panel on an
        /// attachment is looking at it.
        /// </remarks>
        public Task OpenReviewAsync(long attachmentID)
        {
            var task = OpenReviewCoreAsync(attachmentID);
            _outstandingRead = task;

            return task;
        }

        private async Task OpenReviewCoreAsync(long attachmentID)
        {
            if (_session is null)
            {
                return;
            }

            try
            {
                var review = await _session
                    .LoadReviewAsync(attachmentID, CancellationToken.None)
                    .ConfigureAwait(true);

                Review.Show(review);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                StatusText = exception.Message;
            }
        }

        /// <summary>Closes the review panel, leaving the conversation where it is.</summary>
        public void CloseReview() => Review.Close();

        /// <summary>
        /// Waits until no read can still be in flight.
        /// </summary>
        /// <remarks>
        /// Cancel, then <em>await</em>. The await is the part that matters: a read already inside
        /// the session holds the gate that <see cref="WorkspaceSession.Dispose"/> would dispose, and
        /// cancelling does not reach it there. A read that ends in cancellation or failure has still
        /// ended, which is all this needs to know.
        /// </remarks>
        private async Task QuiesceAsync()
        {
            _navigation?.Cancel();

            try
            {
                await _outstandingRead.ConfigureAwait(true);
            }
            catch
            {
                // Finished either way. Whatever it was, it is no longer holding anything.
            }

            try
            {
                await _layersRead.ConfigureAwait(true);
            }
            catch
            {
                // The same for the memory layers' read of the message times.
            }

            _outstandingRead = Task.CompletedTask;
            _layersRead = Task.CompletedTask;
        }

        /// <summary>
        /// Runs one Phase 7 decision, then rebuilds everything from what is now on disk.
        /// </summary>
        /// <returns>
        /// The refusal Phase 7 raised, or <see langword="null"/> when it recorded the decision.
        /// </returns>
        /// <remarks>
        /// <para>Nothing is mutated into the state the code expects. The position is remembered as
        /// identifiers, the session is closed, one decision is attempted, the session is reopened,
        /// and the panel and the conversation are rebuilt from a fresh read. What the operator sees
        /// afterwards came out of the workspace.</para>
        /// <para>The identifiers matter: reopening produces new DTO instances, so a conversation or
        /// participant restored by object identity would silently fail to match and the selection
        /// would blank. The session-only "Show as" choice survives a decision for the same
        /// reason.</para>
        /// <para>A refusal is reported and never retried. It usually means the workspace moved on,
        /// and the answer to that is to look again.</para>
        /// <para>Internal rather than private so the archive backstop can be tested directly: in the
        /// application only the review panel calls it.</para>
        /// </remarks>
        internal async Task<Exception?> ExecuteDecisionAsync(Action write)
        {
            if (_session is null)
            {
                return new InvalidOperationException("No workspace is open.");
            }

            // The panel's commands are already disabled; this is the backstop.
            if (Archive.IsRunning)
            {
                return new InvalidOperationException(
                    "An archive export or check is running. No decision was recorded.");
            }

            await QuiesceAsync().ConfigureAwait(true);

            var conversationID = SelectedConversation?.ConversationID;
            var anchor = _page?.FirstSequenceNumber;
            var attachmentID = Review.AttachmentID;
            var participantID = LocalParticipant?.ParticipantID;

            IsBusy = true;

            try
            {
                var outcome = await _decisions
                    .ExecuteAsync(_session, write, CancellationToken.None)
                    .ConfigureAwait(true);

                _session = outcome.Session;

                await RestoreAsync(conversationID, anchor, attachmentID, participantID)
                    .ConfigureAwait(true);

                StatusText = outcome.Succeeded
                    ? "Decision recorded."
                    : "The decision was not recorded.";

                return outcome.Failure;
            }
            catch (Exception exception)
            {
                // The workspace could not be reopened. There is no session to carry on with, so the
                // viewer closes down to a state it can describe rather than one it is guessing at.
                _session = null;
                CloseSession();

                StatusText =
                    $"The workspace could not be reopened after the decision: {exception.Message}";

                return exception;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <remarks>
        /// Every lookup is by identifier. The instances they match against were created by the read
        /// that just happened.
        /// </remarks>
        private async Task RestoreAsync(
            long? conversationID, int? anchor, long? attachmentID, long? participantID)
        {
            var conversations = await _session!
                .ReadConversationsAsync(CancellationToken.None)
                .ConfigureAwait(true);

            Conversations.Clear();

            foreach (var conversation in conversations)
            {
                Conversations.Add(conversation);
            }

            var selected = conversationID is { } identifier
                ? Conversations.FirstOrDefault(c => c.ConversationID == identifier)
                : Conversations.FirstOrDefault();

            // Assigned to the field rather than through the property, because the setter starts a
            // fresh navigation and clears the local-participant choice — both of which this restore
            // is in the middle of doing deliberately.
            _selectedConversation = selected;
            OnPropertyChanged(nameof(SelectedConversation));

            ConversationTitle = selected?.Title ?? string.Empty;

            Participants.Clear();

            foreach (var participant in selected?.Participants ?? [])
            {
                Participants.Add(participant);
            }

            _localParticipant = participantID is { } chosen
                ? Participants.FirstOrDefault(p => p.ParticipantID == chosen)
                : null;

            OnPropertyChanged(nameof(LocalParticipant));

            if (selected is null)
            {
                return;
            }

            // The same page, rebuilt from a fresh read: the operator stays where they were in it.
            await NavigateAsync(
                anchor is not null ? PageDirection.From : PageDirection.First,
                anchor ?? 0,
                isMove: false).ConfigureAwait(true);

            if (attachmentID is { } attachment)
            {
                await OpenReviewAsync(attachment).ConfigureAwait(true);
            }
        }

        // -----------------------------------------------------------------------------------
        // Memory layers (CH-42 step 5).
        // -----------------------------------------------------------------------------------

        /// <summary>Forgets the layers of the conversation that was open.</summary>
        private void ResetLayers()
        {
            _layersVersion++;
            _composeVersion++;
            _memoryLayers = LoadedMemoryLayers.None;
            _layout = MemoryLayout.Empty;
            _messageTimes = null;
            _messageTimesFor = null;
            _images = new ConcurrentDictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);
            _selectedMemoryRow = null;
            MemoryDetails = null;
            IsLayersOpen = false;
            Layers.Clear();
            LayersNote = string.Empty;
            OnPropertyChanged(nameof(HasLayers));
        }

        /// <summary>
        /// Reads the memories file again (the conversation, "Show as" or the file changed), then places
        /// the visible rows on the page. Read only; the workspace is read for the message times once per
        /// conversation, through the session like every other read.
        /// </summary>
        internal Task ReloadLayersAsync()
        {
            var task = ReloadLayersCoreAsync();
            LayersLoad = task;

            return task;
        }

        private async Task ReloadLayersCoreAsync()
        {
            if (_session is not { } session || SelectedConversation is not { } conversation)
            {
                return;
            }

            var version = ++_layersVersion;
            var local = LocalParticipant?.ParticipantID;

            try
            {
                if (_messageTimes is null || _messageTimesFor != conversation.ConversationID)
                {
                    var read = session.ReadMessageTimesAsync(conversation.ConversationID, CancellationToken.None);
                    Track(read);

                    var times = await read.ConfigureAwait(true);

                    if (version != _layersVersion)
                    {
                        return;
                    }

                    _messageTimes = times;
                    _messageTimesFor = conversation.ConversationID;
                }

                var messageTimes = _messageTimes;
                var path = session.DatabasePath;

                var loaded = await Task
                    .Run(() => MemoryLayerSource.Load(path, conversation, local, messageTimes))
                    .ConfigureAwait(true);

                if (version != _layersVersion)
                {
                    return;
                }

                _memoryLayers = loaded;
                _images = new ConcurrentDictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);

                BuildLayerChoices(path);

                // Open details follow the file: refreshed when the entry is still there, closed when not.
                if (MemoryDetails is { } open)
                {
                    ShowMemoryDetails(open.Key);
                }

                await ApplyLayoutAsync().ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not ObjectDisposedException)
            {
                if (version == _layersVersion)
                {
                    LayersNote = "The memory layers could not be read (" + exception.GetType().Name + ").";
                }
            }
            catch (Exception)
            {
                // The workspace closed under the read; a later reload owns the layers.
            }
        }

        /// <summary>The switches and filters, from the collections and the remembered choices.</summary>
        private void BuildLayerChoices(string workspacePath)
        {
            var choices = _layerSettings.Read(workspacePath);

            Layers.Clear();

            foreach (var collection in _memoryLayers.Index.Collections)
            {
                var shown = choices.Layers.TryGetValue(collection.CollectionID, out var chosen)
                    ? chosen
                    : collection.ShownByDefault;

                IReadOnlyList<(string, bool)> filters = MemoryIndex.HasFilters(collection)
                    ? MemoryFilters.All.Select(f => (f, !choices.FiltersOff.Contains((collection.CollectionID, f)))).ToList()
                    : [];

                Layers.Add(new MemoryLayerViewModel(collection.CollectionID, collection.Name, shown, filters, OnLayerChanged));
            }

            LayersNote = _memoryLayers.Problem
                         ?? (_memoryLayers.Unnamed.Count == 0
                             ? string.Empty
                             : string.Join(", ", _memoryLayers.Unnamed.Select(c => c.Name)) +
                               ": choose “Show as” to show it. Its items name the conversation's two participants.");

            OnPropertyChanged(nameof(HasLayers));
        }

        /// <summary>A switch or filter moved: remember it for this workspace and place the rows again.</summary>
        private void OnLayerChanged()
        {
            if (_session is { } session)
            {
                var defaults = _memoryLayers.Index.Collections.ToDictionary(c => c.CollectionID, c => c.ShownByDefault);
                var previous = _layerSettings.Read(session.DatabasePath);
                var layers = new Dictionary<long, bool>(previous.Layers);

                foreach (var layer in Layers)
                {
                    if (defaults.TryGetValue(layer.CollectionID, out var byDefault) && layer.IsShown == byDefault)
                    {
                        layers.Remove(layer.CollectionID);
                    }
                    else
                    {
                        layers[layer.CollectionID] = layer.IsShown;
                    }
                }

                var off = previous.FiltersOff
                    .Where(f => Layers.All(l => l.CollectionID != f.CollectionID))
                    .Concat(Layers.SelectMany(l => l.Filters).Where(f => !f.IsOn).Select(f => (f.CollectionID, f.Filter)))
                    .ToHashSet();

                _layerSettings.Write(session.DatabasePath, new LayerChoices(layers, off));
            }

            _ = ApplyLayoutAsync();
        }

        /// <summary>Which rows are visible now, from the switches and filters.</summary>
        internal MemoryView CurrentView() => new(
            Layers.Where(l => l.IsShown).Select(l => l.CollectionID),
            Layers.SelectMany(l => l.Filters).Where(f => !f.IsOn).Select(f => (f.CollectionID, f.Filter)));

        private Task ApplyLayoutAsync()
        {
            _layout = _memoryLayers.Index.Layout(CurrentView());

            return ComposeAsync();
        }

        /// <summary>Places the current layout's rows on the page on screen, without reading the workspace.</summary>
        private async Task ComposeAsync()
        {
            var version = ++_composeVersion;
            var page = _page;
            var layout = _layout;
            var images = _images;

            var memoryRows = page is null || layout.IsEmpty
                ? []
                : await Task.Run(() => BuildMemoryRows(page, layout, images)).ConfigureAwait(true);

            if (version != _composeVersion || !ReferenceEquals(page, _page))
            {
                return;
            }

            ComposeRows(memoryRows);
        }

        /// <summary>The memory rows of a page: those after its messages, and before the first for the page that starts the chat.</summary>
        private static List<MemoryRowViewModel> BuildMemoryRows(
            ConversationPage page, MemoryLayout layout, ConcurrentDictionary<string, ImageSource?> images)
        {
            if (layout.IsEmpty || page.FirstSequenceNumber is not { } first || page.LastSequenceNumber is not { } last)
            {
                return [];
            }

            return layout
                .Between(page.HasEarlier ? first : 0, last)
                .Select(placement => new MemoryRowViewModel(placement, images))
                .ToList();
        }

        /// <summary>
        /// Fills <see cref="Rows"/>: each memory row after the message it follows (rows before the first
        /// message first), and a date separator on whichever row begins a day. With no memory row this is
        /// the messages with the separators they were built with.
        /// </summary>
        private void ComposeRows(IReadOnlyList<MemoryRowViewModel> memoryRows)
        {
            var rows = new List<object>(Messages.Count + memoryRows.Count);
            DateOnly? day = null;
            var next = 0;

            void AddMemoriesUpTo(int sequenceNumber)
            {
                while (next < memoryRows.Count && memoryRows[next].After <= sequenceNumber)
                {
                    var row = memoryRows[next++];
                    row.SetDateSeparator(row.Day != day ? row.Day : null);
                    row.IsSelected = _selectedMemoryRow is { } selected && selected.Key == row.Key;
                    day = row.Day;
                    rows.Add(row);
                }
            }

            if (Messages.Count > 0)
            {
                AddMemoriesUpTo(Messages[0].SequenceNumber - 1);
            }

            foreach (var message in Messages)
            {
                message.SetDateSeparator(message.Day != day ? message.Day : null);
                day = message.Day;
                rows.Add(message);

                AddMemoriesUpTo(message.SequenceNumber);
            }

            Rows.Clear();

            foreach (var row in rows)
            {
                Rows.Add(row);
            }

            if (_selectedMemoryRow is { } current)
            {
                _selectedMemoryRow = rows.OfType<MemoryRowViewModel>().FirstOrDefault(r => r.Key == current.Key) ?? current;
            }
        }

        /// <summary>
        /// Selects a memory row and shows its details. Nothing else changes: no message is selected and
        /// the review panel stays as it is.
        /// </summary>
        public void SelectMemoryRow(MemoryRowViewModel row)
        {
            ArgumentNullException.ThrowIfNull(row);

            foreach (var other in Rows.OfType<MemoryRowViewModel>())
            {
                other.IsSelected = ReferenceEquals(other, row);
            }

            _selectedMemoryRow = row;
            ShowMemoryDetails(row.Key);
        }

        private void ShowMemoryDetails(MemoryKey key)
        {
            var index = _memoryLayers.Index;

            if (index.Details(key) is not { } details)
            {
                CloseMemoryDetails();

                return;
            }

            var isPersonal = index.CollectionOf(key.EntryID)?.Kind == ArchiveMemoryVocabulary.KindPersonal;
            MemoryDetails = new MemoryDetailsViewModel(details, isPersonal, _images);
        }

        private void CloseMemoryDetails()
        {
            foreach (var row in Rows.OfType<MemoryRowViewModel>())
            {
                row.IsSelected = false;
            }

            _selectedMemoryRow = null;
            MemoryDetails = null;
        }

        /// <summary>Opens the shown personal memory in the Memories area's form.</summary>
        private async Task EditMemoryAsync()
        {
            if (MemoryDetails is not { IsPersonal: true } details)
            {
                return;
            }

            await Memories.EditAsync(details.Key.EntryID).ConfigureAwait(true);
        }

        /// <summary>
        /// Follows a link in the details: a message link goes to the page that starts at that message; an
        /// entry link goes to that memory's row and shows its details, when its layer is on.
        /// </summary>
        public async Task FollowMemoryLinkAsync(MemoryLinkLine link)
        {
            ArgumentNullException.ThrowIfNull(link);

            if (link.SequenceNumber is { } sequence)
            {
                await NavigateAsync(PageDirection.From, sequence).ConfigureAwait(true);
                StatusText = "Showing the linked message.";

                return;
            }

            if (link.EntryID is not { } entryID)
            {
                return;
            }

            if (_memoryLayers.Index.FirstRowOf(entryID, CurrentView()) is not { } key
                || _layout.Find(key) is not { } placement)
            {
                StatusText = "The linked memory is in a layer that is switched off.";

                return;
            }

            await NavigateAsync(
                placement.After == 0 ? PageDirection.First : PageDirection.From,
                placement.After).ConfigureAwait(true);

            if (Rows.OfType<MemoryRowViewModel>().FirstOrDefault(r => r.Key == key) is { } row)
            {
                SelectMemoryRow(row);
            }
        }

        /// <summary>Records a read of the session so a decision waits for it before the session closes.</summary>
        private void Track(Task read) => _layersRead = read;

        // -----------------------------------------------------------------------------------
        // Building the page.
        // -----------------------------------------------------------------------------------

        /// <remarks>
        /// Runs off the dispatcher. Every image it decodes is frozen, which is what makes a
        /// bitmap built here safe to display on the thread that will draw it.
        /// </remarks>
        private static List<MessageViewModel> Build(
            LoadedPage loaded, IReadOnlyDictionary<long, Brush> brushes, bool showLowQualityCopies)
        {
            var built = new List<MessageViewModel>(loaded.Page.Messages.Count);

            DateOnly? previousDate = null;

            foreach (var message in loaded.Page.Messages)
            {
                var date = DateOnly.FromDateTime(message.MessageDateTimeLocal);

                var separator = previousDate is null || previousDate != date ? date : (DateOnly?)null;
                previousDate = date;

                var attachments = new List<AttachmentViewModel>(message.Attachments.Count);

                foreach (var attachment in message.Attachments)
                {
                    loaded.States.TryGetValue(attachment.AttachmentID, out var state);

                    var media = state?.ResolvedAsset is { } asset
                        && loaded.Media.TryGetValue(asset.MediaAssetID, out var availability)
                        ? availability
                        : null;

                    loaded.Facts.Facts.TryGetValue(attachment.AttachmentID, out var fact);

                    var copies = loaded.Facts.LowQualityCopiesByFullCopy.TryGetValue(
                        attachment.AttachmentID, out var copyIDs)
                        ? copyIDs.Count
                        : 0;

                    attachments.Add(
                        new AttachmentViewModel(attachment, state, media, fact, copies)
                        {
                            ShowLowQualityCopies = showLowQualityCopies,
                        });
                }

                loaded.MessageFacts.TryGetValue(message.MessageID, out var messageFact);

                built.Add(
                    new MessageViewModel(
                        message,
                        separator,
                        BrushFor(brushes, message.SenderParticipantID),
                        attachments,
                        messageFact));
            }

            return built;
        }

        /// <remarks>
        /// A colour per participant, by their position in the conversation's own roster. Stable
        /// across pages, and it claims nothing: it distinguishes senders without saying which of
        /// them is the person reading.
        /// </remarks>
        private static IReadOnlyDictionary<long, Brush> BuildBrushes(ConversationSummary conversation)
        {
            var brushes = new Dictionary<long, Brush>();

            for (var index = 0; index < conversation.Participants.Count; index++)
            {
                var brush = new SolidColorBrush(SenderColours[index % SenderColours.Length]);
                brush.Freeze();

                brushes[conversation.Participants[index].ParticipantID] = brush;
            }

            return brushes;
        }

        private static Brush BrushFor(
            IReadOnlyDictionary<long, Brush> brushes, long? participantID) =>
            participantID is { } identifier && brushes.TryGetValue(identifier, out var brush)
                ? brush
                : SystemBrush;

        private void ApplyAlignment()
        {
            var local = LocalParticipant?.ParticipantID;

            foreach (var message in Messages)
            {
                message.IsFromLocalParticipant =
                    local is not null && message.SenderParticipantID == local;
            }
        }

        private void RaiseNavigationCanExecuteChanged()
        {
            PreviousCommand.RaiseCanExecuteChanged();
            NextCommand.RaiseCanExecuteChanged();
            JumpToDateCommand.RaiseCanExecuteChanged();
            Archive.RaiseCommandStates();
            Memories.RaiseCommandStates();
        }

        /// <summary>What the Memories area works on: the open workspace and conversation, or nothing.</summary>
        private MemoriesContext? MemoriesContext() =>
            _session is { } session && SelectedConversation is { } conversation
                ? new MemoriesContext(session.DatabasePath, conversation.ConversationID)
                : null;

        /// <summary>What an export would take from: the open workspace and conversation, or nothing.</summary>
        private ArchiveWorkspaceContext? ArchiveContext() =>
            _session is { } session && SelectedConversation is { } conversation
                ? new ArchiveWorkspaceContext(
                    session.DatabasePath,
                    conversation.ConversationID,
                    conversation.Title,
                    LocalParticipant?.ParticipantID,
                    LocalParticipant?.DisplayName)
                : null;

        private static string DescribeRange(ConversationPage page, ConversationSummary conversation)
        {
            if (page.FirstSequenceNumber is not { } first || page.LastSequenceNumber is not { } last)
            {
                return "No messages";
            }

            return
                $"Messages {Text(first)}–{Text(last)} of " +
                $"{Text(conversation.MessageCount)}";
        }

        private static string DescribePage(LoadedPage loaded)
        {
            if (loaded.States.Count == 0)
            {
                return "Page loaded.";
            }

            var unresolved = loaded.States.Values.Count(
                state => state.ResolvedAsset is null);

            return
                $"Page loaded. {Count(loaded.States.Count, "attachment")}, " +
                $"{Text(unresolved)} unresolved.";
        }

        private static string Count(int value, string noun) =>
            value == 1 ? $"1 {noun}" : $"{Text(value)} {noun}s";

        private static string Text(long value) => value.ToString(Culture);

        private static CultureInfo Culture => CultureInfo.CurrentCulture;

        /// <summary>Colours for telling senders apart, in roster order.</summary>
        private static readonly Color[] SenderColours =
        [
            Color.FromRgb(0x2E, 0x5E, 0x8A),
            Color.FromRgb(0x1F, 0x6B, 0x4F),
            Color.FromRgb(0x7A, 0x3E, 0x6B),
            Color.FromRgb(0x8A, 0x5A, 0x1F),
            Color.FromRgb(0x3E, 0x4C, 0x7A),
        ];

        private static readonly Brush SystemBrush = CreateSystemBrush();

        private static Brush CreateSystemBrush()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A));
            brush.Freeze();

            return brush;
        }
    }
}
