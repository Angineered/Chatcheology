using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Chatcheology.Archive;
using Chatcheology.Data.ArchiveExport;
using Chatcheology.Desktop.Services;

namespace Chatcheology.Desktop.ViewModels
{
    /// <summary>How the last archive export or check ended.</summary>
    public enum ArchiveOutcome
    {
        /// <summary>Nothing has ended yet, or something is running.</summary>
        None,

        /// <summary>The self-check and the workspace comparison both passed.</summary>
        Passed,

        /// <summary>The self-check passed; the archive was not compared with a workspace.</summary>
        PassedNotCompared,

        /// <summary>A check failed, or the export was refused or stopped.</summary>
        Failed,

        /// <summary>The operator cancelled. Nothing was written.</summary>
        Cancelled,
    }

    /// <summary>What the operator is asked to confirm before an export.</summary>
    /// <param name="Preview">The counts read from the open workspace.</param>
    /// <param name="LocalParticipantName">Who is shown as "me", which the archive records.</param>
    /// <param name="Text">The question, with the counts, as the dialog shows it.</param>
    public sealed record ArchiveExportConfirmation(
        ArchiveExportPreview Preview, string LocalParticipantName, string Text);

    /// <summary>
    /// The dialogs the archive work needs, supplied by the window: a view-model opens no window.
    /// </summary>
    public sealed class ArchiveDialogs
    {
        /// <summary>Declines everything. For a view-model built without a window.</summary>
        public static ArchiveDialogs None { get; } = new()
        {
            ConfirmExport = _ => false,
            ChooseExportParent = () => null,
            ChooseArchiveFolder = () => null,
            OpenFolder = _ => false,
        };

        /// <summary>Asks the operator to confirm the counts. True to export.</summary>
        public required Func<ArchiveExportConfirmation, bool> ConfirmExport { get; init; }

        /// <summary>Asks for the folder the new archive folder goes into, or null.</summary>
        public required Func<string?> ChooseExportParent { get; init; }

        /// <summary>Asks for an existing archive folder to check, or null.</summary>
        public required Func<string?> ChooseArchiveFolder { get; init; }

        /// <summary>Shows a folder to the operator. False when it could not be opened.</summary>
        public required Func<string, bool> OpenFolder { get; init; }
    }

    /// <summary>What the open workspace offers to export, or nothing when no conversation is open.</summary>
    internal sealed record ArchiveWorkspaceContext(
        string WorkspacePath,
        long ConversationID,
        string ConversationTitle,
        long? LocalParticipantID,
        string? LocalParticipantName);

    /// <summary>
    /// Exporting the open conversation to a portable archive, and checking archives (CH-18).
    /// </summary>
    /// <remarks>
    /// <para><strong>The counts the operator confirms are the counts the export must find.</strong>
    /// They are read through the exporter's own reading path, shown, and passed back as
    /// <c>ExpectResolved</c> and <c>ExpectMissingReviewed</c>; a workspace that changed in between is
    /// refused by the exporter, and the summary says so.</para>
    /// <para><strong>No decision while it runs.</strong> From the first count to the last check the
    /// shell holds decisions off (<see cref="RunningChanged"/>), so the workspace cannot change
    /// underneath what is being exported or compared.</para>
    /// <para><strong>Counts only on screen.</strong> What this shows comes from fixed phrases and from
    /// the Data and Archive layers' own messages, which are fixed phrases too. An unexpected failure is
    /// named by its type alone, because its own message may carry a path. The archive's folder path is
    /// held for the Open folder button and never shown.</para>
    /// </remarks>
    public sealed class ArchiveViewModel : ObservableObject
    {
        /// <summary>The start of every new archive folder's name; the local date and time follow.</summary>
        public const string FolderNamePrefix = "Chatcheology archive ";

        /// <summary>The status line after an export the operator stood down from before it began.</summary>
        public const string NotStartedText = "Export cancelled. Nothing was written.";

        private readonly IArchiveOperations _operations;
        private readonly ArchiveDialogs _dialogs;
        private readonly Func<ArchiveWorkspaceContext?> _context;
        private readonly Action<string> _reportStatus;

        private CancellationTokenSource? _cancellation;
        private string? _folderPath;

        private bool _isOpen;
        private bool _isRunning;
        private bool _canCancel;
        private string _headlineText = string.Empty;
        private string _progressText = string.Empty;
        private double _progressValue;
        private double _progressMaximum = 1;
        private bool _isProgressIndeterminate;
        private string _contentsHashText = string.Empty;
        private ArchiveOutcome _outcome;

        /// <param name="operations">Counting, exporting and checking; a seam for tests.</param>
        /// <param name="dialogs">The window's dialogs; a seam for tests.</param>
        /// <param name="context">What the viewer has open now, or null.</param>
        /// <param name="reportStatus">Says something in the window's status line.</param>
        internal ArchiveViewModel(
            IArchiveOperations operations,
            ArchiveDialogs dialogs,
            Func<ArchiveWorkspaceContext?> context,
            Action<string> reportStatus)
        {
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _reportStatus = reportStatus ?? throw new ArgumentNullException(nameof(reportStatus));

            ExportCommand = new AsyncRelayCommand(ExportAsync, () => !IsRunning && _context() is not null);
            ValidateCommand = new AsyncRelayCommand(ValidateAsync, () => !IsRunning);
            CancelCommand = new RelayCommand(Cancel, () => CanCancel);
            OpenFolderCommand = new RelayCommand(OpenFolder, () => CanOpenFolder);
            CloseCommand = new RelayCommand(Close, () => !IsRunning);
        }

        /// <summary>Raised when an export or check starts or ends.</summary>
        public event EventHandler? RunningChanged;

        // -----------------------------------------------------------------------------------
        // Bound state.
        // -----------------------------------------------------------------------------------

        /// <summary>Counts, confirms, exports and checks the open conversation.</summary>
        public AsyncRelayCommand ExportCommand { get; }

        /// <summary>Checks an existing archive folder.</summary>
        public AsyncRelayCommand ValidateCommand { get; }

        /// <summary>Stops a running export. Nothing is left behind.</summary>
        public RelayCommand CancelCommand { get; }

        /// <summary>Shows the archive folder.</summary>
        public RelayCommand OpenFolderCommand { get; }

        /// <summary>Puts the panel away.</summary>
        public RelayCommand CloseCommand { get; }

        /// <summary>The summary lines: counts, check outcomes, problem kinds. Never text or paths.</summary>
        public ObservableCollection<string> Lines { get; } = [];

        /// <summary>Whether the archive panel is showing.</summary>
        public bool IsOpen
        {
            get => _isOpen;
            private set => SetProperty(ref _isOpen, value);
        }

        /// <summary>Whether an export or a check is running.</summary>
        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    OnPropertyChanged(nameof(IsFinished));
                    OnPropertyChanged(nameof(CanOpenFolder));
                }
            }
        }

        /// <summary>Whether the panel shows an outcome rather than progress.</summary>
        public bool IsFinished => IsOpen && !IsRunning;

        /// <summary>Whether the running work can still be stopped without leaving anything.</summary>
        public bool CanCancel
        {
            get => _canCancel;
            private set
            {
                if (SetProperty(ref _canCancel, value))
                {
                    CancelCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>Whether there is an archive folder to show.</summary>
        public bool CanOpenFolder => !IsRunning && _folderPath is not null;

        /// <summary>What is happening, or how it ended.</summary>
        public string HeadlineText
        {
            get => _headlineText;
            private set => SetProperty(ref _headlineText, value);
        }

        /// <summary>The stage and how far through it, while running.</summary>
        public string ProgressText
        {
            get => _progressText;
            private set => SetProperty(ref _progressText, value);
        }

        /// <summary>Items done in the current stage.</summary>
        public double ProgressValue
        {
            get => _progressValue;
            private set => SetProperty(ref _progressValue, value);
        }

        /// <summary>Items in the current stage.</summary>
        public double ProgressMaximum
        {
            get => _progressMaximum;
            private set => SetProperty(ref _progressMaximum, value);
        }

        /// <summary>Whether the current stage has no count to show.</summary>
        public bool IsProgressIndeterminate
        {
            get => _isProgressIndeterminate;
            private set => SetProperty(ref _isProgressIndeterminate, value);
        }

        /// <summary>The archive's <c>CONTENTS.sha256</c> hash, or empty.</summary>
        public string ContentsHashText
        {
            get => _contentsHashText;
            private set
            {
                if (SetProperty(ref _contentsHashText, value))
                {
                    OnPropertyChanged(nameof(HasContentsHash));
                }
            }
        }

        /// <summary>Whether there is a hash to show.</summary>
        public bool HasContentsHash => ContentsHashText.Length > 0;

        /// <summary>How the last export or check ended.</summary>
        public ArchiveOutcome Outcome
        {
            get => _outcome;
            private set => SetProperty(ref _outcome, value);
        }

        /// <summary>Tells the buttons to ask again; the shell calls it when what is open changes.</summary>
        public void RaiseCommandStates()
        {
            ExportCommand.RaiseCanExecuteChanged();
            ValidateCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            OpenFolderCommand.RaiseCanExecuteChanged();
            CloseCommand.RaiseCanExecuteChanged();
        }

        // -----------------------------------------------------------------------------------
        // Exporting.
        // -----------------------------------------------------------------------------------

        /// <remarks>
        /// Count, confirm, choose, export, check — in that order, and each step only once. Standing
        /// down at the confirmation or the folder choice writes nothing and says so.
        /// </remarks>
        private async Task ExportAsync()
        {
            if (_context() is not { } context)
            {
                return;
            }

            if (context.LocalParticipantID is not { } localParticipantID)
            {
                _reportStatus(
                    "Choose who to show as “me” (Show as) before exporting: the archive records it.");

                return;
            }

            var cancellation = Begin("Counting what the archive will hold…", cancellable: true);

            try
            {
                var preview = await Task
                    .Run(
                        () => _operations.Preview(
                            context.WorkspacePath, context.ConversationID, localParticipantID, cancellation.Token),
                        cancellation.Token)
                    .ConfigureAwait(true);

                var confirmation = new ArchiveExportConfirmation(
                    preview,
                    context.LocalParticipantName ?? string.Empty,
                    ConfirmationText(preview, context.LocalParticipantName ?? string.Empty));

                SetStage("Waiting for your confirmation…", 0, 0);

                if (!_dialogs.ConfirmExport(confirmation)
                    || _dialogs.ChooseExportParent() is not { } parent
                    || string.IsNullOrWhiteSpace(parent))
                {
                    IsOpen = false;
                    _reportStatus(NotStartedText);

                    return;
                }

                var folderName = FolderName(DateTime.Now);
                var output = Path.Combine(parent, folderName);

                // The counts the operator was shown, and nothing else, are what the export must find.
                var request = new ArchiveExportRequest
                {
                    WorkspacePath = context.WorkspacePath,
                    ConversationID = context.ConversationID,
                    LocalParticipantID = localParticipantID,
                    OutputPath = output,
                    ExpectResolved = preview.Restored,
                    ExpectMissingReviewed = preview.Missing,
                };

                HeadlineText = "Exporting the archive…";
                SetStage("Starting…", 0, 0);

                var progress = new Progress<ArchiveExportProgress>(report => OnProgress(cancellation, report));

                var result = await Task
                    .Run(() => _operations.Export(request, progress, cancellation.Token), cancellation.Token)
                    .ConfigureAwait(true);

                // Past the rename the archive is complete. It is checked, never undone.
                CanCancel = false;
                _folderPath = output;

                HeadlineText = "Checking the archive…";
                SetStage("Running the self-check and the workspace comparison…", 0, 0);

                var report = await CheckAsync(
                        output,
                        new ArchiveComparisonTarget(
                            context.WorkspacePath, context.ConversationID, context.ConversationTitle))
                    .ConfigureAwait(true);

                ShowSummary(report, result, folderName);
            }
            catch (OperationCanceledException)
            {
                Finish(ArchiveOutcome.Cancelled, "Export cancelled. No archive folder was written.");
            }
            catch (ArchiveExportRefusedException exception)
            {
                // The exporter's refusals are fixed phrases naming the request option, never a path.
                Finish(ArchiveOutcome.Failed, "Export refused. Nothing was written.", exception.Message);
            }
            catch (ArchiveExportStoppedException exception)
            {
                Finish(ArchiveOutcome.Failed, "Export stopped. No archive folder was written.", exception.Message);
            }
            catch (Exception exception)
            {
                Finish(
                    ArchiveOutcome.Failed,
                    "The export could not complete.",
                    Unexpected(exception));
            }
            finally
            {
                End(cancellation);
            }
        }

        /// <remarks>
        /// <see cref="Progress{T}"/> posts its reports, so one can arrive after its stage, or the whole
        /// export, has finished. Only a report from the export still running is shown.
        /// </remarks>
        private void OnProgress(CancellationTokenSource run, ArchiveExportProgress report)
        {
            if (!ReferenceEquals(_cancellation, run) || !CanCancel)
            {
                return;
            }

            SetStage(StageText(report.Stage), report.Done, report.Total);
        }

        private void Cancel()
        {
            if (!CanCancel)
            {
                return;
            }

            CanCancel = false;
            ProgressText = "Cancelling…";

            _cancellation?.Cancel();
        }

        // -----------------------------------------------------------------------------------
        // Checking.
        // -----------------------------------------------------------------------------------

        /// <remarks>
        /// The self-check needs nothing but the folder. The workspace comparison is made when a
        /// conversation is open, against that conversation.
        /// </remarks>
        private async Task ValidateAsync()
        {
            if (_dialogs.ChooseArchiveFolder() is not { } folder || string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            var context = _context();
            var cancellation = Begin("Checking the archive…", cancellable: false);

            try
            {
                SetStage("Running the self-check…", 0, 0);

                var target = context is null
                    ? null
                    : new ArchiveComparisonTarget(
                        context.WorkspacePath, context.ConversationID, context.ConversationTitle);

                var report = await CheckAsync(folder, target).ConfigureAwait(true);

                _folderPath = report.Refusal is null ? folder : null;

                ShowSummary(report, exported: null, folderName: null);
            }
            finally
            {
                End(cancellation);
            }
        }

        /// <summary>Runs the checks off the dispatcher; an unexpected failure is reported, not thrown.</summary>
        private async Task<ArchiveCheckReport> CheckAsync(string folder, ArchiveComparisonTarget? target)
        {
            try
            {
                return await Task.Run(() => _operations.Check(folder, target)).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                return new ArchiveCheckReport
                {
                    Refusal = "The archive could not be checked. " + Unexpected(exception),
                };
            }
        }

        // -----------------------------------------------------------------------------------
        // The summary.
        // -----------------------------------------------------------------------------------

        private void ShowSummary(ArchiveCheckReport report, ArchiveExportResult? exported, string? folderName)
        {
            var lines = new List<string>();

            var compared = report.Comparison is not null;
            var passed = report.Validation?.Passed == true && report.Comparison?.Passed != false;

            var outcome = report.Refusal is not null || !passed
                ? ArchiveOutcome.Failed
                : compared ? ArchiveOutcome.Passed : ArchiveOutcome.PassedNotCompared;

            if (folderName is not null)
            {
                lines.Add($"Written to a new folder named “{folderName}” in the folder you chose.");
            }

            if (report.Refusal is not null)
            {
                lines.Add(report.Refusal);
            }

            if ((exported?.Counts ?? report.Counts) is { } counts)
            {
                lines.Add(
                    $"{Count(counts.Messages, "message")} · {Count(counts.Participants, "participant")} · " +
                    $"{Count(counts.Attachments, "attachment")}: {Text(counts.Restored)} Restored, " +
                    $"{Text(counts.Missing)} Missing, {Text(counts.Unmatched)} Unmatched · " +
                    $"{Count(counts.MediaFiles, "media file")}");
            }
            else if (report.Validation is not null)
            {
                lines.Add("The manifest could not be read, so no counts are shown.");
            }

            if (exported is not null)
            {
                AddMediaNotes(exported, lines);
            }

            if (report.Validation is { } validation)
            {
                AddValidation(validation, lines);
            }

            if (report.Comparison is { } comparison)
            {
                AddComparison(comparison, lines);
            }
            else if (report.ComparisonNote is not null)
            {
                lines.Add(report.ComparisonNote);
            }

            var headline = (exported is not null, outcome) switch
            {
                (true, ArchiveOutcome.Passed) => "Archive exported and checked: passed.",
                (true, ArchiveOutcome.PassedNotCompared) =>
                    "Archive exported. Its self-check passed; it was not compared with the workspace.",
                (true, _) => "Archive exported, but it did not pass its checks.",
                (false, ArchiveOutcome.Passed) => "Archive checked: passed.",
                (false, ArchiveOutcome.PassedNotCompared) =>
                    "Archive self-check passed. It was not compared with a workspace.",
                _ => "Archive checked: failed.",
            };

            Finish(outcome, headline, [.. lines]);

            ContentsHashText = (exported?.ContentsSha256 ?? report.ContentsSha256) is { } hash
                ? "CONTENTS.sha256 hash: " + hash
                : string.Empty;
        }

        private static void AddMediaNotes(ArchiveExportResult exported, List<string> lines)
        {
            if (exported.DifferentCopies + exported.UnreadableCopies + exported.FromLaterCopy > 0)
            {
                lines.Add(
                    $"Media copies passed over: {Text(exported.DifferentCopies)} with different bytes, " +
                    $"{Text(exported.UnreadableCopies)} not there or unreadable; " +
                    $"{Text(exported.FromLaterCopy)} taken from a later copy.");
            }

            if (exported.LowQualityLinksOutsideConversation > 0)
            {
                lines.Add(
                    $"Low-quality links left out because the full copy is in another conversation: " +
                    $"{Text(exported.LowQualityLinksOutsideConversation)}.");
            }
        }

        private static void AddValidation(ArchiveValidationResult validation, List<string> lines)
        {
            if (validation.Passed)
            {
                lines.Add(
                    $"Self-check: passed ({Count(validation.Checks.Count, "check")}, " +
                    $"{Count(validation.Contents.Listed, "file")} hashed).");

                return;
            }

            var failing = validation.Checks
                .Where(check => !check.Passed)
                .Select(check => check.Ran
                    ? $"{Words(check.Check.ToString())} {Count(check.Problems, "problem")}"
                    : $"{Words(check.Check.ToString())} not run");

            lines.Add("Self-check: failed. " + string.Join("; ", failing) + ".");

            foreach (var (kind, count) in validation.ProblemCounts.Where(pair => pair.Value > 0).OrderBy(pair => pair.Key))
            {
                lines.Add($"  {Words(kind.ToString())}: {Text(count)}");
            }
        }

        private static void AddComparison(ArchiveComparisonResult comparison, List<string> lines)
        {
            if (comparison.Passed)
            {
                lines.Add(
                    $"Workspace comparison: passed ({Count(comparison.MessagesCompared, "message")}, " +
                    $"{Count(comparison.AttachmentsCompared, "attachment")} compared).");

                return;
            }

            var total = comparison.MismatchCounts.Values.Sum();

            lines.Add($"Workspace comparison: failed, {Count(total, "difference")}.");

            foreach (var (kind, count) in comparison.MismatchCounts.Where(pair => pair.Value > 0).OrderBy(pair => pair.Key))
            {
                lines.Add($"  {Words(kind.ToString())}: {Text(count)}");
            }
        }

        // -----------------------------------------------------------------------------------
        // The panel.
        // -----------------------------------------------------------------------------------

        private CancellationTokenSource Begin(string headline, bool cancellable)
        {
            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _folderPath = null;

            Lines.Clear();
            ContentsHashText = string.Empty;
            Outcome = ArchiveOutcome.None;
            HeadlineText = headline;
            SetStage(string.Empty, 0, 0);

            IsOpen = true;
            IsRunning = true;
            CanCancel = cancellable;

            OnPropertyChanged(nameof(IsFinished));
            RunningChanged?.Invoke(this, EventArgs.Empty);
            RaiseCommandStates();

            return cancellation;
        }

        private void End(CancellationTokenSource cancellation)
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
            }

            cancellation.Dispose();

            CanCancel = false;
            IsRunning = false;

            OnPropertyChanged(nameof(IsFinished));
            OnPropertyChanged(nameof(CanOpenFolder));
            RunningChanged?.Invoke(this, EventArgs.Empty);
            RaiseCommandStates();
        }

        private void Finish(ArchiveOutcome outcome, string headline, params string[] lines)
        {
            Outcome = outcome;
            HeadlineText = headline;
            ProgressText = string.Empty;

            Lines.Clear();

            foreach (var line in lines)
            {
                Lines.Add(line);
            }

            _reportStatus(headline);
        }

        private void SetStage(string text, int done, int total)
        {
            IsProgressIndeterminate = total <= 0;
            ProgressMaximum = Math.Max(total, 1);
            ProgressValue = Math.Clamp(done, 0, Math.Max(total, 1));

            ProgressText = total > 0
                ? $"{text} {Text(done)} of {Text(total)}"
                : text;
        }

        private void OpenFolder()
        {
            if (_folderPath is { } folder && !_dialogs.OpenFolder(folder))
            {
                _reportStatus("The archive folder could not be opened.");
            }
        }

        private void Close()
        {
            if (IsRunning)
            {
                return;
            }

            IsOpen = false;
            Lines.Clear();
            ContentsHashText = string.Empty;
            Outcome = ArchiveOutcome.None;
            _folderPath = null;

            OnPropertyChanged(nameof(IsFinished));
            RaiseCommandStates();
        }

        // -----------------------------------------------------------------------------------
        // Wording.
        // -----------------------------------------------------------------------------------

        /// <summary>The new archive folder's name: fixed words and the local time, nothing else.</summary>
        internal static string FolderName(DateTime localTime) =>
            FolderNamePrefix + localTime.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);

        /// <summary>The confirmation: counts and who is "me", and what happens next.</summary>
        internal static string ConfirmationText(ArchiveExportPreview preview, string localParticipantName) =>
            $"""
            Export the selected conversation to a new archive folder?

            Messages: {Text(preview.Messages)}
            Restored: {Text(preview.Restored)}
            Missing: {Text(preview.Missing)}
            Unmatched: {Text(preview.Unmatched)}
            Media files: {Text(preview.MediaFiles)} ({Size(preview.MediaBytes)})

            Shown as “me”: {localParticipantName}

            The export checks these counts again and writes nothing if the workspace has changed. Choose OK, then the folder the new archive folder goes into.
            """;

        private static string StageText(ArchiveExportStage stage) => stage switch
        {
            ArchiveExportStage.ReadingMessages => "Reading messages:",
            ArchiveExportStage.CopyingMedia => "Copying media:",
            ArchiveExportStage.WritingArchive => "Writing the archive:",
            _ => "Working:",
        };

        /// <summary>An unexpected failure, named by its type: its own message may carry a path.</summary>
        private static string Unexpected(Exception exception) =>
            $"An unexpected {exception.GetType().Name} stopped it. Its details are not shown because they may " +
            "name a file.";

        /// <summary>An enum member's name as words: <c>ContentsFileChanged</c> is "Contents file changed".</summary>
        internal static string Words(string name)
        {
            var spaced = Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ");

            return spaced.Length == 0
                ? spaced
                : spaced[0] + spaced[1..].ToLower(CultureInfo.InvariantCulture);
        }

        internal static string Size(long bytes)
        {
            string[] units = ["KB", "MB", "GB", "TB"];

            if (bytes < 1024)
            {
                return bytes == 1 ? "1 byte" : $"{Text(bytes)} bytes";
            }

            var value = (double)bytes;
            var unit = -1;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value.ToString("N1", CultureInfo.CurrentCulture)} {units[unit]}";
        }

        private static string Count(int value, string noun) =>
            value == 1 ? $"1 {noun}" : $"{Text(value)} {noun}s";

        private static string Text(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
    }
}
