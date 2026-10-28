using System.IO;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Personal;
using Chatcheology.Desktop.Services;
using Chatcheology.Desktop.ViewModels;
using Microsoft.Data.Sqlite;
using static Chatcheology.Desktop.Tests.MemoriesViewModelTests;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// The Memories area when its context changes under a running save or read (CH-42 step 4 review):
    /// the save keeps the shell held off until it ends, and nothing it or a late read reports reaches
    /// the closed or reopened area.
    /// </summary>
    public sealed class MemoriesContextChangeTests : IDisposable
    {
        private readonly ArchiveTestWorkspace _workspace = new();
        private readonly GatedMessages _messages;

        public MemoriesContextChangeTests()
        {
            _messages = new GatedMessages(new WorkspaceMessageLinkTargets(_workspace.DatabasePath, 1));
        }

        public void Dispose()
        {
            _messages.Release.Set();
            _workspace.Dispose();
        }

        [Fact]
        public async Task ASaveStillWriting_HoldsTheShellOff_UntilItEnds_AndItsResultIsDropped()
        {
            var viewModel = await OpenedAsync();
            viewModel.LocalParticipant = viewModel.Participants[0];
            var memories = viewModel.Memories;
            await Run(memories.OpenCommand, memories);
            memories.CollectionName = "Holidays";
            await Run(memories.AddCollectionCommand, memories);

            memories.NewEntryCommand.Execute(null);
            var form = memories.Form!;
            form.Title = "Beach week";
            form.Source = "My own memory";
            form.StartDate = new DateTime(2018, 1, 2);
            form.PickerDate = new DateTime(2026, 4, 6);
            await form.PickerLoad;
            form.SelectedPickerMessage = form.PickerMessages[1];
            form.AddMessageLinkCommand.Execute(null);

            Assert.True(viewModel.ExportArchiveCommand.CanExecute(null));

            // The save stops inside its link check, before it has written anything.
            _messages.GateExists = true;
            form.SaveCommand.Execute(null);
            Assert.True(_messages.Entered.Wait(TimeSpan.FromSeconds(20)));

            Assert.True(memories.IsWriting);
            Assert.False(viewModel.ExportArchiveCommand.CanExecute(null));
            Assert.False(viewModel.OpenWorkspaceCommand.CanExecute(null));
            Assert.False(viewModel.CanChangeConversation);

            // Choosing another conversation is refused while it writes.
            var conversation = viewModel.SelectedConversation;
            viewModel.SelectedConversation = null;
            Assert.Same(conversation, viewModel.SelectedConversation);
            Assert.True(memories.IsOpen);

            // Closing the area (as closing the window does) and opening it again does not end the save.
            memories.Close();
            Assert.Null(memories.Form);
            Assert.True(memories.IsWriting);
            Assert.False(viewModel.ExportArchiveCommand.CanExecute(null));

            memories.OpenCommand.Execute(null);
            await WaitUntil(() => memories.IsOpen && !memories.IsBusy);
            Assert.True(memories.IsWriting);

            memories.NewEntryCommand.Execute(null);
            var newer = memories.Form;
            Assert.NotNull(newer);

            _messages.Release.Set();
            await WaitUntil(() => !memories.IsWriting);

            // The old save committed, but what it reports is dropped: the newer form stays open.
            Assert.Same(newer, memories.Form);
            Assert.NotEqual("Memory saved.", memories.StatusText);
            Assert.Equal("Beach week", Text("SELECT Title FROM Entry;"));

            Assert.True(viewModel.ExportArchiveCommand.CanExecute(null));
            Assert.True(viewModel.OpenWorkspaceCommand.CanExecute(null));
            Assert.True(viewModel.CanChangeConversation);
        }

        [Fact]
        public async Task AnEditReadFinishingAfterClose_OpensNoForm()
        {
            var viewModel = await OpenedAsync();
            var memories = viewModel.Memories;
            await Run(memories.OpenCommand, memories);
            memories.CollectionName = "Holidays";
            await Run(memories.AddCollectionCommand, memories);
            await SaveAsync(memories, "Beach week", new DateTime(2018, 1, 2), linkTo: 2);

            memories.SelectedEntry = memories.Entries[0];

            // The edit's read stops while describing the linked message.
            _messages.GateRead = true;
            memories.EditEntryCommand.Execute(null);
            Assert.True(_messages.Entered.Wait(TimeSpan.FromSeconds(20)));

            memories.Close();
            memories.OpenCommand.Execute(null);

            _messages.Release.Set();
            await WaitUntil(() => memories.IsOpen && !memories.IsBusy);

            Assert.Null(memories.Form);
            Assert.True(memories.IsListShown);
            Assert.Equal("Beach week", Assert.Single(memories.Entries).Title);
        }

        [Fact]
        public void AMissingWorkspace_IsReportedAsTheWorkspace_NotTheMemoriesFile()
        {
            var targets = new WorkspaceMessageLinkTargets(Path.Combine(_workspace.Root, "gone", "workspace.db"), 1);

            var failure = Assert.Throws<WorkspaceMessagesUnavailableException>(() => targets.Exists(1));

            Assert.Equal(
                "The workspace file is not there any more, so its messages cannot be read. Nothing was written.",
                MemoriesViewModel.Describe(failure, "Fallback."));
            Assert.Equal(
                "The memories file is not there any more. Nothing was written.",
                MemoriesViewModel.Describe(new FileNotFoundException("x"), "Fallback."));
        }

        private async Task<MainViewModel> OpenedAsync()
        {
            var viewModel = new MainViewModel(
                () => _workspace.DatabasePath,
                new ArchiveDialogs
                {
                    ConfirmExport = _ => false,
                    ChooseExportParent = () => null,
                    ChooseArchiveFolder = () => null,
                    OpenFolder = _ => false,
                },
                new ArchiveOperations(),
                new MemoriesDialogs
                {
                    ChooseImage = () => null,
                    ConfirmCreateFile = _ => true,
                    ConfirmDelete = _ => false,
                },
                context => new MemoriesBackend(PersonalMemoryStore.Beside(context.WorkspacePath, _messages), _messages));

            viewModel.OpenWorkspaceCommand.Execute(null);
            await WaitUntil(() => viewModel.Messages.Count > 0 && !viewModel.IsBusy);

            return viewModel;
        }

        private string Text(string sql)
        {
            using var connection = MemoriesDatabase.OpenReadOnlyConnection(MemoriesDatabase.PathBeside(_workspace.DatabasePath));
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            connection.Dispose();
            SqliteConnection.ClearAllPools();

            return value;
        }

        /// <summary>The real messages, with a gate that stops one call until released.</summary>
        private sealed class GatedMessages(IMessageLinkTargets inner) : IMessageLinkTargets
        {
            internal volatile bool GateExists;
            internal volatile bool GateRead;

            internal ManualResetEventSlim Entered { get; } = new();

            internal ManualResetEventSlim Release { get; } = new();

            public bool Exists(int sequenceNumber)
            {
                if (GateExists)
                {
                    Hold();
                }

                return inner.Exists(sequenceNumber);
            }

            public MessageLinkCandidate? Read(int sequenceNumber)
            {
                if (GateRead)
                {
                    Hold();
                }

                return inner.Read(sequenceNumber);
            }

            public IReadOnlyList<MessageLinkCandidate> ReadDay(DateOnly day) => inner.ReadDay(day);

            private void Hold()
            {
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(30));
            }
        }
    }
}
