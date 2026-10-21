using System.IO;
using Chatcheology.Archive;
using Chatcheology.Data.ArchiveExport;
using Chatcheology.Desktop.Services;
using Chatcheology.Desktop.ViewModels;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// The export and validate commands (CH-18), with the archive work behind a seam: when they are
    /// offered, what the operator is asked, what is passed on, and what is shown.
    /// </summary>
    /// <remarks>
    /// The workspace is a real synthetic one, because the commands depend on a real open session and
    /// on a real review panel; only the exporting and checking are stood in for.
    /// </remarks>
    public sealed class ArchiveExportViewModelTests : IDisposable
    {
        private const string SecretPath = @"C:\synthetic-private-folder\someone's photo.jpg";

        private readonly ArchiveTestWorkspace _workspace = new();
        private readonly FakeOperations _operations = new();
        private readonly List<ArchiveExportConfirmation> _confirmations = [];

        private bool _confirm = true;
        private string? _parent;
        private string? _archiveFolder;

        public ArchiveExportViewModelTests()
        {
            _parent = _workspace.ExportsFolder;
        }

        public void Dispose() => _workspace.Dispose();

        // ---------------------------------------------------------------------------------------
        // Enablement.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public async Task Export_IsOfferedOnlyWithAWorkspaceAndConversationOpen()
        {
            var viewModel = Create();
            var raised = 0;
            viewModel.ExportArchiveCommand.CanExecuteChanged += (_, _) => raised++;

            Assert.False(viewModel.ExportArchiveCommand.CanExecute(null));
            Assert.True(viewModel.ValidateArchiveCommand.CanExecute(null));

            await OpenAsync(viewModel);

            Assert.True(viewModel.ExportArchiveCommand.CanExecute(null));
            Assert.True(raised > 0, "The export button was never told its availability changed.");

            viewModel.CloseSession();

            Assert.False(viewModel.ExportArchiveCommand.CanExecute(null));
        }

        [Fact]
        public async Task WhileAnExportRuns_NeitherCommandNorOpeningAnotherWorkspaceIsOffered()
        {
            var viewModel = await OpenedAsync();
            var gate = new TaskCompletionSource();

            _operations.OnExport = (request, _, _) =>
            {
                gate.Task.Wait();

                return Result();
            };

            viewModel.ExportArchiveCommand.Execute(null);

            await WaitUntil(() => _operations.Requests.Count == 1);

            Assert.True(viewModel.Archive.IsRunning);
            Assert.False(viewModel.ExportArchiveCommand.CanExecute(null));
            Assert.False(viewModel.ValidateArchiveCommand.CanExecute(null));
            Assert.False(viewModel.OpenWorkspaceCommand.CanExecute(null));
            Assert.True(viewModel.Archive.CancelCommand.CanExecute(null));

            gate.SetResult();

            await FinishedAsync(viewModel);

            Assert.True(viewModel.ExportArchiveCommand.CanExecute(null));
            Assert.True(viewModel.ValidateArchiveCommand.CanExecute(null));
            Assert.True(viewModel.OpenWorkspaceCommand.CanExecute(null));
            Assert.False(viewModel.Archive.CancelCommand.CanExecute(null));

            viewModel.CloseSession();
        }

        // ---------------------------------------------------------------------------------------
        // Confirming.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public async Task TheConfirmation_ShowsTheCountsAndMe_AndThoseCountsBecomeTheExpectations()
        {
            var viewModel = await OpenedAsync();

            _operations.PreviewResult = new ArchiveExportPreview(12, 7, 3, 2, 6, 5 * 1024 * 1024);

            viewModel.ExportArchiveCommand.Execute(null);

            await FinishedAsync(viewModel);

            var confirmation = Assert.Single(_confirmations);
            Assert.Equal(_operations.PreviewResult, confirmation.Preview);
            Assert.Equal("Ada " + ArchiveTestWorkspace.NameMarker, confirmation.LocalParticipantName);

            foreach (var expected in new[]
                     {
                         "Messages: 12", "Restored: 7", "Missing: 3", "Unmatched: 2", "Media files: 6",
                         "5.0 MB", "\u201Cme\u201D: Ada " + ArchiveTestWorkspace.NameMarker,
                     })
            {
                Assert.Contains(expected, confirmation.Text, StringComparison.Ordinal);
            }

            // The counts shown, and only those, are what the export must find.
            var request = Assert.Single(_operations.Requests);
            Assert.Equal(7, request.ExpectResolved);
            Assert.Equal(3, request.ExpectMissingReviewed);
            Assert.Equal(1, request.ConversationID);
            Assert.Equal(1, request.LocalParticipantID);
            Assert.Equal(_workspace.DatabasePath, request.WorkspacePath);

            // A new folder, named by fixed words and the time, under the folder chosen.
            Assert.Equal(_workspace.ExportsFolder, Path.GetDirectoryName(request.OutputPath));
            Assert.StartsWith(ArchiveViewModel.FolderNamePrefix, Path.GetFileName(request.OutputPath), StringComparison.Ordinal);

            Assert.Equal(ArchiveOutcome.Passed, viewModel.Archive.Outcome);

            viewModel.CloseSession();
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public async Task StandingDownAtTheConfirmationOrTheFolder_ExportsNothing(bool confirm, bool chooseFolder)
        {
            var viewModel = await OpenedAsync();

            _confirm = confirm;
            _parent = chooseFolder ? _workspace.ExportsFolder : null;

            viewModel.ExportArchiveCommand.Execute(null);

            await WaitUntil(() => _operations.Previews == 1 && !viewModel.Archive.IsRunning);

            Assert.Empty(_operations.Requests);
            Assert.False(viewModel.Archive.IsOpen);
            Assert.Equal(ArchiveViewModel.NotStartedText, viewModel.StatusText);
            Assert.True(viewModel.ExportArchiveCommand.CanExecute(null));

            viewModel.CloseSession();
        }

        [Fact]
        public async Task WithNobodyShownAsMe_TheExportSaysSoAndCountsNothing()
        {
            var viewModel = Create();
            await OpenAsync(viewModel);

            viewModel.ExportArchiveCommand.Execute(null);

            await WaitUntil(() => viewModel.ExportArchiveCommand.CanExecute(null));

            Assert.Equal(0, _operations.Previews);
            Assert.Empty(_confirmations);
            Assert.Contains("Show as", viewModel.StatusText, StringComparison.Ordinal);

            viewModel.CloseSession();
        }

        // ---------------------------------------------------------------------------------------
        // Running.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public async Task Progress_ShowsTheStageAndHowFarThroughItIs()
        {
            var viewModel = await OpenedAsync();
            var gate = new TaskCompletionSource();

            _operations.OnExport = (_, progress, _) =>
            {
                progress.Report(new ArchiveExportProgress(ArchiveExportStage.CopyingMedia, 2, 4));
                gate.Task.Wait();

                return Result();
            };

            viewModel.ExportArchiveCommand.Execute(null);

            await WaitUntil(() => viewModel.Archive.ProgressText == "Copying media: 2 of 4");

            Assert.Equal(2, viewModel.Archive.ProgressValue);
            Assert.Equal(4, viewModel.Archive.ProgressMaximum);
            Assert.False(viewModel.Archive.IsProgressIndeterminate);

            gate.SetResult();

            await FinishedAsync(viewModel);

            Assert.Equal(string.Empty, viewModel.Archive.ProgressText);

            viewModel.CloseSession();
        }

        [Fact]
        public async Task Cancel_StopsTheExport_AndSaysNothingWasWritten()
        {
            var viewModel = await OpenedAsync();

            _operations.OnExport = (_, _, token) =>
            {
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                token.ThrowIfCancellationRequested();

                return Result();
            };

            viewModel.ExportArchiveCommand.Execute(null);

            await WaitUntil(() => _operations.Requests.Count == 1);

            viewModel.Archive.CancelCommand.Execute(null);

            await FinishedAsync(viewModel);

            Assert.Equal(ArchiveOutcome.Cancelled, viewModel.Archive.Outcome);
            Assert.Contains("No archive folder was written", viewModel.Archive.HeadlineText, StringComparison.Ordinal);
            Assert.Equal(0, _operations.Checks);
            Assert.False(viewModel.Archive.CanOpenFolder);

            viewModel.CloseSession();
        }

        // ---------------------------------------------------------------------------------------
        // Decisions while exporting.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DecisionCommands_AreDisabledWhileExporting_AndComeBackAfter(bool exportFails)
        {
            var viewModel = await OpenedAsync();

            await viewModel.OpenReviewAsync(_workspace.UnmatchedAttachmentID);

            var review = viewModel.Review;
            review.SelectedCandidate = review.Candidates.FirstOrDefault();

            Assert.True(review.BeginReviewCommand.CanExecute(null));

            var told = 0;
            review.BeginReviewCommand.CanExecuteChanged += (_, _) => told++;

            var gate = new TaskCompletionSource();

            _operations.OnExport = (_, _, _) =>
            {
                gate.Task.Wait();

                return exportFails
                    ? throw new ArchiveExportStoppedException("A fixed phrase. No archive folder was written.")
                    : Result();
            };

            viewModel.ExportArchiveCommand.Execute(null);

            await WaitUntil(() => _operations.Requests.Count == 1);

            Assert.True(review.AreDecisionsSuspended);
            Assert.True(told > 0, "The decision buttons were never told they were held off.");
            Assert.False(review.BeginReviewCommand.CanExecute(null));
            Assert.False(review.BeginDisproveCommand.CanExecute(null));
            Assert.False(review.BeginConfirmCommand.CanExecute(null));
            Assert.False(review.BeginReplaceCommand.CanExecute(null));
            Assert.False(review.BeginRevokeCommand.CanExecute(null));
            Assert.False(review.CommitCommand.CanExecute(null));

            gate.SetResult();

            await FinishedAsync(viewModel);

            Assert.Equal(exportFails ? ArchiveOutcome.Failed : ArchiveOutcome.Passed, viewModel.Archive.Outcome);
            Assert.False(review.AreDecisionsSuspended);
            Assert.True(review.BeginReviewCommand.CanExecute(null));

            viewModel.CloseSession();
        }

        [Fact]
        public async Task ADecisionArmedBeforeTheExport_CannotBeCommittedDuringIt()
        {
            var viewModel = await OpenedAsync();

            await viewModel.OpenReviewAsync(_workspace.UnmatchedAttachmentID);

            viewModel.Review.BeginReviewCommand.Execute(null);
            Assert.True(viewModel.Review.CommitCommand.CanExecute(null));

            var gate = new TaskCompletionSource();
            _operations.OnExport = (_, _, _) =>
            {
                gate.Task.Wait();

                return Result();
            };

            viewModel.ExportArchiveCommand.Execute(null);

            await WaitUntil(() => _operations.Requests.Count == 1);

            Assert.False(viewModel.Review.CommitCommand.CanExecute(null));

            gate.SetResult();

            await FinishedAsync(viewModel);

            Assert.True(viewModel.Review.CommitCommand.CanExecute(null));

            viewModel.CloseSession();
        }

        // ---------------------------------------------------------------------------------------
        // What is shown.
        // ---------------------------------------------------------------------------------------

        public static TheoryData<Exception, string> Failures => new()
        {
            { new ArchiveExportRefusedException("The OutputPath is inside a repository. Nothing was written."), "Export refused" },
            { new ArchiveExportStoppedException("The export could not complete (IOException).", new IOException(SecretPath)), "Export stopped" },
            { new IOException(SecretPath), "could not complete" },
            { new UnauthorizedAccessException(SecretPath), "could not complete" },
        };

        [Theory]
        [MemberData(nameof(Failures))]
        public async Task RefusalsAndStops_AreShown_WithoutAPathOrAName(Exception failure, string headline)
        {
            var viewModel = await OpenedAsync();

            _operations.OnExport = (_, _, _) => throw failure;

            viewModel.ExportArchiveCommand.Execute(null);

            await FinishedAsync(viewModel);

            Assert.Equal(ArchiveOutcome.Failed, viewModel.Archive.Outcome);
            Assert.Contains(headline, viewModel.Archive.HeadlineText, StringComparison.Ordinal);

            AssertNothingPrivateShown(viewModel);

            if (failure is not (ArchiveExportRefusedException or ArchiveExportStoppedException))
            {
                // Named by its type alone.
                Assert.Contains(viewModel.Archive.Lines, line => line.Contains(failure.GetType().Name, StringComparison.Ordinal));
            }

            viewModel.CloseSession();
        }

        [Fact]
        public async Task AFailedCheck_ShowsCountsAndKindsOnly()
        {
            var viewModel = await OpenedAsync();

            _operations.CheckResult = Report(
                passed: false,
                comparison: Comparison(ArchiveMismatchKind.MessageTextDiffers, 3));

            viewModel.ExportArchiveCommand.Execute(null);

            await FinishedAsync(viewModel);

            Assert.Equal(ArchiveOutcome.Failed, viewModel.Archive.Outcome);
            Assert.Contains("did not pass", viewModel.Archive.HeadlineText, StringComparison.Ordinal);
            Assert.Contains("  Contents file changed: 2", viewModel.Archive.Lines);
            Assert.Contains("  Message text differs: 3", viewModel.Archive.Lines);
            Assert.Contains(viewModel.Archive.Lines, line => line.StartsWith("Self-check: failed. Contents 2 problems", StringComparison.Ordinal));

            AssertNothingPrivateShown(viewModel);

            viewModel.CloseSession();
        }

        [Fact]
        public async Task ASuccessfulExport_ShowsTheHash_AndOpensTheFolderThroughTheSeam()
        {
            var opened = new List<string>();

            var viewModel = Create(opened.Add);
            await OpenAsync(viewModel);
            viewModel.LocalParticipant = viewModel.Participants[0];

            viewModel.ExportArchiveCommand.Execute(null);

            await FinishedAsync(viewModel);

            Assert.Equal("CONTENTS.sha256 hash: " + new string('a', 64), viewModel.Archive.ContentsHashText);
            Assert.True(viewModel.Archive.OpenFolderCommand.CanExecute(null));

            viewModel.Archive.OpenFolderCommand.Execute(null);

            Assert.Equal([_operations.Requests[0].OutputPath], opened);

            // The check compared the archive with the open conversation.
            Assert.Equal(1, _operations.LastTarget?.ConversationID);

            viewModel.Archive.CloseCommand.Execute(null);

            Assert.False(viewModel.Archive.IsOpen);

            viewModel.CloseSession();
        }

        // ---------------------------------------------------------------------------------------
        // Validate only.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public async Task ValidateOnly_WithNoWorkspace_RunsTheSelfCheckAlone()
        {
            var viewModel = Create();
            _archiveFolder = _workspace.ExportsFolder;

            _operations.CheckResult = Report(passed: true, comparison: null) with
            {
                ComparisonNote = "Not compared with a workspace: no conversation is open.",
            };

            viewModel.ValidateArchiveCommand.Execute(null);

            await FinishedAsync(viewModel);

            Assert.Equal(1, _operations.Checks);
            Assert.Null(_operations.LastTarget);
            Assert.Empty(_operations.Requests);
            Assert.Equal(ArchiveOutcome.PassedNotCompared, viewModel.Archive.Outcome);
            Assert.Contains("Not compared with a workspace: no conversation is open.", viewModel.Archive.Lines);
            Assert.True(viewModel.Archive.CanOpenFolder);
        }

        [Fact]
        public async Task ValidateOnly_WithAConversationOpen_ComparesAgainstIt()
        {
            var viewModel = await OpenedAsync();
            _archiveFolder = _workspace.ExportsFolder;

            viewModel.ValidateArchiveCommand.Execute(null);

            await FinishedAsync(viewModel);

            Assert.Equal(1, _operations.LastTarget?.ConversationID);
            Assert.Equal(_workspace.DatabasePath, _operations.LastTarget?.WorkspacePath);
            Assert.Equal(ArchiveOutcome.Passed, viewModel.Archive.Outcome);
            Assert.Equal("Archive checked: passed.", viewModel.Archive.HeadlineText);

            viewModel.CloseSession();
        }

        [Fact]
        public async Task ValidateOnly_StandingDownAtTheFolder_ChecksNothing()
        {
            var viewModel = Create();
            _archiveFolder = null;

            viewModel.ValidateArchiveCommand.Execute(null);

            await WaitUntil(() => viewModel.ValidateArchiveCommand.CanExecute(null));

            Assert.Equal(0, _operations.Checks);
            Assert.False(viewModel.Archive.IsOpen);
        }

        // ---------------------------------------------------------------------------------------
        // Review round: closing, cancel, decisions in flight, leftovers.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public async Task WhileTheExportWrites_TheWindowMayNotClose_AndCancelIsOffered()
        {
            var viewModel = await OpenedAsync();
            var gate = new TaskCompletionSource();

            Assert.True(viewModel.Archive.CanCloseNow);

            _operations.OnExport = (_, _, _) =>
            {
                gate.Task.Wait();

                return Result();
            };

            viewModel.ExportArchiveCommand.Execute(null);

            await WaitUntil(() => _operations.Requests.Count == 1);

            Assert.False(viewModel.Archive.CanCloseNow);
            Assert.True(viewModel.Archive.CanCancel);

            gate.SetResult();

            await FinishedAsync(viewModel);

            Assert.True(viewModel.Archive.CanCloseNow);

            viewModel.CloseSession();
        }

        [Fact]
        public async Task DuringTheCheckAfterAnExport_CancelIsNotOffered_AndTheWindowMayClose()
        {
            var viewModel = await OpenedAsync();
            var gate = new TaskCompletionSource();

            _operations.OnCheck = () => gate.Task.Wait();

            viewModel.ExportArchiveCommand.Execute(null);

            await WaitUntil(() => _operations.Checks == 1);

            Assert.True(viewModel.Archive.IsRunning);
            Assert.False(viewModel.Archive.CanCancel);
            Assert.False(viewModel.Archive.CancelCommand.CanExecute(null));
            Assert.True(viewModel.Archive.CanCloseNow);

            gate.SetResult();

            await FinishedAsync(viewModel);

            viewModel.CloseSession();
        }

        [Fact]
        public async Task DuringValidateOnly_CancelIsNotOffered_AndTheWindowMayClose()
        {
            var viewModel = Create();
            var gate = new TaskCompletionSource();

            _archiveFolder = _workspace.ExportsFolder;
            _operations.OnCheck = () => gate.Task.Wait();

            viewModel.ValidateArchiveCommand.Execute(null);

            await WaitUntil(() => _operations.Checks == 1);

            Assert.True(viewModel.Archive.IsRunning);
            Assert.False(viewModel.Archive.CanCancel);
            Assert.True(viewModel.Archive.CanCloseNow);

            gate.SetResult();

            await FinishedAsync(viewModel);
        }

        [Fact]
        public async Task WhileAnExportRuns_ADecisionReachingTheShell_IsRefusedAndNotWritten()
        {
            var viewModel = await OpenedAsync();
            var gate = new TaskCompletionSource();
            var wrote = false;

            _operations.OnExport = (_, _, _) =>
            {
                gate.Task.Wait();

                return Result();
            };

            viewModel.ExportArchiveCommand.Execute(null);

            await WaitUntil(() => _operations.Requests.Count == 1);

            var refusal = await viewModel.ExecuteDecisionAsync(() => wrote = true);

            Assert.IsType<InvalidOperationException>(refusal);
            Assert.Contains("archive export or check is running", refusal!.Message, StringComparison.Ordinal);
            Assert.False(wrote);

            gate.SetResult();

            await FinishedAsync(viewModel);

            viewModel.CloseSession();
        }

        [Fact]
        public async Task WhileADecisionIsInFlight_NeitherArchiveCommandIsOffered()
        {
            var viewModel = await OpenedAsync();
            var gate = new ManualResetEventSlim();
            var told = 0;

            viewModel.ExportArchiveCommand.CanExecuteChanged += (_, _) => told++;

            var decision = viewModel.ExecuteDecisionAsync(() => gate.Wait(TimeSpan.FromSeconds(30)));

            await WaitUntil(() => viewModel.IsBusy);

            Assert.False(viewModel.ExportArchiveCommand.CanExecute(null));
            Assert.False(viewModel.ValidateArchiveCommand.CanExecute(null));
            Assert.True(told > 0, "The export button was never told a decision was in flight.");

            gate.Set();

            Assert.Null(await decision);

            await WaitUntil(() => !viewModel.IsBusy);

            Assert.True(viewModel.ExportArchiveCommand.CanExecute(null));
            Assert.True(viewModel.ValidateArchiveCommand.CanExecute(null));

            viewModel.CloseSession();
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public async Task AStopThatLeavesThePartialFolder_SaysSoPlainly(bool cancelled, bool leftBehind)
        {
            var viewModel = await OpenedAsync();

            _operations.OnExport = (request, _, _) =>
            {
                if (leftBehind)
                {
                    Directory.CreateDirectory(request.OutputPath + ArchiveExporter.PartialSuffix);
                }

                throw cancelled
                    ? new OperationCanceledException()
                    : new ArchiveExportStoppedException("A fixed phrase. No archive folder was written.");
            };

            viewModel.ExportArchiveCommand.Execute(null);

            await FinishedAsync(viewModel);

            Assert.Equal(cancelled ? ArchiveOutcome.Cancelled : ArchiveOutcome.Failed, viewModel.Archive.Outcome);
            Assert.Equal(leftBehind, viewModel.Archive.Lines.Contains(ArchiveViewModel.PartialLeftText));

            if (cancelled)
            {
                Assert.Equal(
                    leftBehind,
                    viewModel.Archive.HeadlineText.Contains("incomplete folder was left", StringComparison.Ordinal));
            }

            AssertNothingPrivateShown(viewModel);

            viewModel.CloseSession();
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private MainViewModel Create(Action<string>? opened = null) =>
            new(
                () => _workspace.DatabasePath,
                new ArchiveDialogs
                {
                    ConfirmExport = confirmation =>
                    {
                        _confirmations.Add(confirmation);

                        return _confirm;
                    },
                    ChooseExportParent = () => _parent,
                    ChooseArchiveFolder = () => _archiveFolder,
                    OpenFolder = path =>
                    {
                        opened?.Invoke(path);

                        return true;
                    },
                },
                _operations);

        private static async Task OpenAsync(MainViewModel viewModel)
        {
            viewModel.OpenWorkspaceCommand.Execute(null);

            await WaitUntil(() => viewModel.Messages.Count > 0 && !viewModel.IsBusy);
        }

        private async Task<MainViewModel> OpenedAsync()
        {
            var viewModel = Create();

            await OpenAsync(viewModel);

            viewModel.LocalParticipant = viewModel.Participants[0];

            return viewModel;
        }

        private static Task FinishedAsync(MainViewModel viewModel) =>
            WaitUntil(() => !viewModel.Archive.IsRunning && viewModel.Archive.Outcome != ArchiveOutcome.None);

        /// <summary>Nothing on the panel or the status line names a path, a participant or message text.</summary>
        internal static void AssertNothingPrivateShown(MainViewModel viewModel, params string[] alsoPrivate)
        {
            var shown = viewModel.Archive.Lines
                .Append(viewModel.Archive.HeadlineText)
                .Append(viewModel.Archive.ProgressText)
                .Append(viewModel.StatusText)
                .ToList();

            var secrets = new[]
                {
                    "synthetic-private-folder", ":\\", ArchiveTestWorkspace.NameMarker, ArchiveTestWorkspace.TextMarker,
                    Path.GetTempPath(),
                }
                .Concat(alsoPrivate);

            foreach (var line in shown)
            {
                foreach (var secret in secrets)
                {
                    Assert.DoesNotContain(secret, line, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        private static ArchiveExportResult Result() => new()
        {
            Counts = new ArchiveCounts(2, 5, 3, 1, 1, 1, 1),
            ContentsSha256 = new string('a', 64),
            DifferentCopies = 0,
            UnreadableCopies = 0,
            FromLaterCopy = 0,
            LowQualityLinksOutsideConversation = 0,
        };

        private static ArchiveCheckReport Report(bool passed, ArchiveComparisonResult? comparison)
        {
            var problems = Enum.GetValues<ArchiveProblemKind>().ToDictionary(kind => kind, _ => 0);

            if (!passed)
            {
                problems[ArchiveProblemKind.ContentsFileChanged] = 2;
            }

            return new ArchiveCheckReport
            {
                Validation = new ArchiveValidationResult
                {
                    Checks = Enum.GetValues<ArchiveCheck>()
                        .Select(check => new ArchiveCheckOutcome
                        {
                            Check = check,
                            Ran = true,
                            Examined = 1,
                            Problems = !passed && check == ArchiveCheck.Contents ? 2 : 0,
                        })
                        .ToList(),
                    ProblemCounts = problems,
                    FirstProblems = [],
                    Contents = new ArchiveContentsCheck(),
                },
                Counts = new ArchiveCounts(2, 5, 3, 1, 1, 1, 1),
                ContentsSha256 = new string('b', 64),
                Comparison = comparison,
            };
        }

        private static ArchiveComparisonResult Comparison(ArchiveMismatchKind? kind = null, int count = 0)
        {
            var mismatches = Enum.GetValues<ArchiveMismatchKind>().ToDictionary(k => k, _ => 0);

            if (kind is { } differing)
            {
                mismatches[differing] = count;
            }

            return new ArchiveComparisonResult
            {
                ParticipantsCompared = 2,
                MessagesCompared = 5,
                AttachmentsCompared = 3,
                Restored = 1,
                Missing = 1,
                Unmatched = 1,
                MismatchCounts = mismatches,
                FirstMismatches = [],
            };
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

        /// <summary>Stands in for counting, exporting and checking. Records what it was asked.</summary>
        private sealed class FakeOperations : IArchiveOperations
        {
            internal ArchiveExportPreview PreviewResult { get; set; } = new(5, 1, 1, 1, 1, 21);

            internal ArchiveCheckReport CheckResult { get; set; } = Report(passed: true, comparison: Comparison());

            internal Func<ArchiveExportRequest, IProgress<ArchiveExportProgress>, CancellationToken, ArchiveExportResult>? OnExport { get; set; }

            internal List<ArchiveExportRequest> Requests { get; } = [];

            internal int Previews { get; private set; }

            internal int Checks { get; private set; }

            internal ArchiveComparisonTarget? LastTarget { get; private set; }

            public ArchiveExportPreview Preview(
                string workspacePath, long conversationID, long localParticipantID, CancellationToken cancellationToken)
            {
                Previews++;

                return PreviewResult;
            }

            public ArchiveExportResult Export(
                ArchiveExportRequest request,
                IProgress<ArchiveExportProgress> progress,
                CancellationToken cancellationToken)
            {
                lock (Requests)
                {
                    Requests.Add(request);
                }

                return OnExport is { } export ? export(request, progress, cancellationToken) : Result();
            }

            public ArchiveCheckReport Check(string archiveFolder, ArchiveComparisonTarget? target)
            {
                Checks++;
                LastTarget = target;

                OnCheck?.Invoke();

                return CheckResult;
            }

            internal Action? OnCheck { get; set; }
        }
    }
}
