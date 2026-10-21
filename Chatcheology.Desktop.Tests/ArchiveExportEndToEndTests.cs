using System.IO;
using Chatcheology.Archive;
using Chatcheology.Data.ArchiveExport;
using Chatcheology.Desktop.Services;
using Chatcheology.Desktop.ViewModels;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// The whole export from the viewer (CH-18), against a synthetic workspace with the real exporter,
    /// validator and comparer: count, confirm, export, check, and what the operator is shown.
    /// </summary>
    public sealed class ArchiveExportEndToEndTests : IDisposable
    {
        private readonly ArchiveTestWorkspace _workspace = new();
        private readonly List<ArchiveExportConfirmation> _confirmations = [];
        private readonly List<string> _opened = [];

        private Action? _onConfirm;
        private string? _parent;
        private string? _archiveFolder;

        public ArchiveExportEndToEndTests()
        {
            _parent = _workspace.ExportsFolder;
        }

        public void Dispose() => _workspace.Dispose();

        [Fact]
        public async Task ExportingTheConversation_WritesAnArchiveThatPassesBothChecks()
        {
            var viewModel = await OpenedAsync(new ArchiveOperations());

            viewModel.ExportArchiveCommand.Execute(null);

            await FinishedAsync(viewModel);

            // What the operator was asked: the counts the workspace holds, and who is "me".
            var confirmation = Assert.Single(_confirmations);
            Assert.Equal(new ArchiveExportPreview(5, 1, 1, 1, 1, ArchiveTestWorkspace.PayloadBytes), confirmation.Preview);

            Assert.Equal(ArchiveOutcome.Passed, viewModel.Archive.Outcome);
            Assert.Equal("Archive exported and checked: passed.", viewModel.Archive.HeadlineText);
            Assert.Contains(viewModel.Archive.Lines, line => line.StartsWith("Self-check: passed", StringComparison.Ordinal));
            Assert.Contains(viewModel.Archive.Lines, line => line.StartsWith("Workspace comparison: passed", StringComparison.Ordinal));

            // One new folder, named by the fixed prefix, holding a complete archive.
            var folder = Assert.Single(Directory.GetDirectories(_workspace.ExportsFolder));
            Assert.StartsWith(ArchiveViewModel.FolderNamePrefix, Path.GetFileName(folder), StringComparison.Ordinal);
            Assert.False(Directory.Exists(folder + ArchiveExporter.PartialSuffix));

            var hash = ArchiveHashing.Sha256OfFile(Path.Combine(folder, ArchiveContents.FileName));
            Assert.Equal("CONTENTS.sha256 hash: " + hash, viewModel.Archive.ContentsHashText);

            viewModel.Archive.OpenFolderCommand.Execute(null);
            Assert.Equal([folder], _opened);

            ArchiveExportViewModelTests.AssertNothingPrivateShown(viewModel, _workspace.Root);

            // The same archive, checked again on its own.
            _archiveFolder = folder;
            viewModel.ValidateArchiveCommand.Execute(null);

            await WaitUntil(() => !viewModel.Archive.IsRunning && viewModel.Archive.HeadlineText == "Archive checked: passed.");

            Assert.Equal(ArchiveOutcome.Passed, viewModel.Archive.Outcome);
            Assert.Equal("CONTENTS.sha256 hash: " + hash, viewModel.Archive.ContentsHashText);

            viewModel.CloseSession();
        }

        [Fact]
        public async Task ADamagedArchive_FailsValidation_WithKindsAndCountsOnly()
        {
            var viewModel = await OpenedAsync(new ArchiveOperations());

            viewModel.ExportArchiveCommand.Execute(null);
            await FinishedAsync(viewModel);

            var folder = Assert.Single(Directory.GetDirectories(_workspace.ExportsFolder));
            var media = Assert.Single(Directory.GetFiles(Path.Combine(folder, ArchiveFormat.MediaFolderName)));
            File.WriteAllText(media, "altered bytes");

            _archiveFolder = folder;
            viewModel.ValidateArchiveCommand.Execute(null);

            await WaitUntil(() => !viewModel.Archive.IsRunning && viewModel.Archive.Outcome == ArchiveOutcome.Failed);

            Assert.Equal("Archive checked: failed.", viewModel.Archive.HeadlineText);
            Assert.Contains("  Contents file changed: 1", viewModel.Archive.Lines);
            Assert.Contains("  Media file differs: 1", viewModel.Archive.Lines);

            ArchiveExportViewModelTests.AssertNothingPrivateShown(viewModel, _workspace.Root, Path.GetFileName(media));

            viewModel.CloseSession();
        }

        /// <remarks>
        /// The counts guard (option A): the counts shown are passed to the exporter, so a workspace
        /// that changes between the confirmation and the export is refused and nothing is written.
        /// </remarks>
        [Fact]
        public async Task AWorkspaceThatChangesAfterTheCountsAreShown_IsRefused_AndNothingIsWritten()
        {
            var viewModel = await OpenedAsync(new ArchiveOperations());

            // Another program records a review while the operator reads the dialog.
            _onConfirm = () => _workspace.Review(_workspace.UnmatchedAttachmentID);

            viewModel.ExportArchiveCommand.Execute(null);

            await FinishedAsync(viewModel);

            Assert.Equal(ArchiveOutcome.Failed, viewModel.Archive.Outcome);
            Assert.Equal("Export refused. Nothing was written.", viewModel.Archive.HeadlineText);
            Assert.Contains(viewModel.Archive.Lines, line => line.Contains("ExpectMissingReviewed", StringComparison.Ordinal));
            Assert.Empty(_workspace.ExportsContent());
            Assert.False(viewModel.Archive.CanOpenFolder);

            ArchiveExportViewModelTests.AssertNothingPrivateShown(viewModel, _workspace.Root);

            viewModel.CloseSession();
        }

        [Fact]
        public async Task CancellingDuringTheExport_LeavesNothingBehind()
        {
            MainViewModel? viewModel = null;

            // The real export, with the operator pressing Cancel once the media are being copied: the
            // partial folder exists by then, and must be gone afterwards.
            var operations = new CancellingOperations(() => viewModel!.Archive.CancelCommand.Execute(null));

            viewModel = await OpenedAsync(operations);

            viewModel.ExportArchiveCommand.Execute(null);

            await FinishedAsync(viewModel);

            Assert.True(operations.SawPartialFolder, "The cancel came before anything was written.");
            Assert.Equal(ArchiveOutcome.Cancelled, viewModel.Archive.Outcome);
            Assert.Empty(_workspace.ExportsContent());

            // And the viewer can decide again.
            Assert.False(viewModel.Review.AreDecisionsSuspended);

            viewModel.CloseSession();
        }

        [Fact]
        public async Task AnOutputInsideARepository_IsRefused_WithoutNamingThePath()
        {
            var repository = Path.Combine(_workspace.ExportsFolder, "some-repository");
            Directory.CreateDirectory(Path.Combine(repository, ".git"));
            _parent = repository;

            var viewModel = await OpenedAsync(new ArchiveOperations());

            viewModel.ExportArchiveCommand.Execute(null);

            await FinishedAsync(viewModel);

            Assert.Equal("Export refused. Nothing was written.", viewModel.Archive.HeadlineText);
            Assert.Contains(viewModel.Archive.Lines, line => line.Contains("repository", StringComparison.Ordinal));
            Assert.Equal([".git"], Directory.GetFileSystemEntries(repository).Select(Path.GetFileName));

            ArchiveExportViewModelTests.AssertNothingPrivateShown(viewModel, _workspace.Root, "some-repository");

            viewModel.CloseSession();
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private async Task<MainViewModel> OpenedAsync(IArchiveOperations operations)
        {
            var viewModel = new MainViewModel(
                () => _workspace.DatabasePath,
                new ArchiveDialogs
                {
                    ConfirmExport = confirmation =>
                    {
                        _confirmations.Add(confirmation);
                        _onConfirm?.Invoke();

                        return true;
                    },
                    ChooseExportParent = () => _parent,
                    ChooseArchiveFolder = () => _archiveFolder,
                    OpenFolder = path =>
                    {
                        _opened.Add(path);

                        return true;
                    },
                },
                operations);

            viewModel.OpenWorkspaceCommand.Execute(null);

            await WaitUntil(() => viewModel.Messages.Count > 0 && !viewModel.IsBusy);

            viewModel.LocalParticipant = viewModel.Participants[0];

            return viewModel;
        }

        private static Task FinishedAsync(MainViewModel viewModel) =>
            WaitUntil(() => !viewModel.Archive.IsRunning && viewModel.Archive.Outcome != ArchiveOutcome.None);

        private static async Task WaitUntil(Func<bool> condition, int timeoutMilliseconds = 30_000)
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
        /// The real operations, with the operator's Cancel pressed as soon as media copying reports.
        /// </summary>
        private sealed class CancellingOperations(Action pressCancel) : IArchiveOperations
        {
            private readonly ArchiveOperations _real = new();

            internal bool SawPartialFolder { get; private set; }

            public ArchiveExportPreview Preview(
                string workspacePath, long conversationID, long localParticipantID, CancellationToken cancellationToken) =>
                _real.Preview(workspacePath, conversationID, localParticipantID, cancellationToken);

            public ArchiveExportResult Export(
                ArchiveExportRequest request,
                IProgress<ArchiveExportProgress> progress,
                CancellationToken cancellationToken) =>
                _real.Export(
                    request,
                    new Immediate(report =>
                    {
                        progress.Report(report);

                        if (report.Stage == ArchiveExportStage.CopyingMedia)
                        {
                            SawPartialFolder = Directory.Exists(request.OutputPath + ArchiveExporter.PartialSuffix);
                            pressCancel();
                        }
                    }),
                    cancellationToken);

            public ArchiveCheckReport Check(string archiveFolder, ArchiveComparisonTarget? target) =>
                _real.Check(archiveFolder, target);
        }

        private sealed class Immediate(Action<ArchiveExportProgress> report) : IProgress<ArchiveExportProgress>
        {
            public void Report(ArchiveExportProgress value) => report(value);
        }
    }
}
