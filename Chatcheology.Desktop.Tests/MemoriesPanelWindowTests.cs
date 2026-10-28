using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Chatcheology.Data.Memories.Personal;
using Chatcheology.Desktop.ViewModels;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// The Memories panel in the real window (CH-42 step 4): it opens, shows the list and the form, and
    /// every binding it declares finds its property.
    /// </summary>
    public sealed class MemoriesPanelWindowTests
    {
        [Fact]
        public void ThePanelAndTheForm_BindWithoutErrors()
        {
            StaRunner.Run(() =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

                using var workspace = new ArchiveTestWorkspace();

                // A collection with one memory, made through the store before the window opens.
                var store = PersonalMemoryStore.Beside(
                    workspace.DatabasePath, new WorkspaceMessageLinkTargets(workspace.DatabasePath, 1));
                var holidays = store.CreateCollection("Holidays", true, new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc));
                store.AddEntry(new PersonalEntryDraft
                {
                    CollectionID = holidays,
                    Title = "Beach week",
                    Start = new DateOnly(2018, 1, 2),
                    Precision = PersonalDatePrecision.Day,
                    Source = "My own memory",
                    Links = [PersonalLinkTarget.ToMessage(1)],
                });
                SqliteConnection.ClearAllPools();

                var errors = new BindingErrors();
                PresentationTraceSources.Refresh();
                PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

                var window = new MainWindow(() => workspace.DatabasePath)
                {
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                };

                try
                {
                    window.Show();

                    var viewModel = (MainViewModel)window.DataContext;
                    viewModel.OpenWorkspaceCommand.Execute(null);
                    PumpUntil(() => viewModel.Messages.Count > 0 && !viewModel.IsBusy);

                    var memories = viewModel.Memories;
                    memories.OpenCommand.Execute(null);
                    PumpUntil(() => memories.IsOpen && !memories.IsBusy && memories.Entries.Count == 1);
                    Settle(window);

                    memories.SelectedEntry = memories.Entries[0];
                    memories.EditEntryCommand.Execute(null);
                    PumpUntil(() => memories.Form is not null);

                    memories.Form!.PickerDate = new DateTime(2026, 4, 6);
                    PumpUntil(() => memories.Form.PickerMessages.Count == 5);
                    Settle(window);

                    Assert.Single(memories.Form.Links);
                    Assert.Empty(errors.Messages);
                }
                finally
                {
                    PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
                    window.Close();
                    SqliteConnection.ClearAllPools();
                }
            });
        }

        private static void Settle(Window window)
        {
            for (var pass = 0; pass < 4; pass++)
            {
                window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, () => { });
            }
        }

        private static void PumpUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);

            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("The viewer did not reach the expected state in time.");
                }

                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () => { });
                Thread.Sleep(10);
            }
        }

        /// <summary>Binding errors that name one of the Memories area's view-models.</summary>
        private sealed class BindingErrors : TraceListener
        {
            private static readonly string[] Names =
            [
                nameof(MemoriesViewModel),
                nameof(MemoryEntryFormViewModel),
                nameof(PersonalCollectionViewModel),
                nameof(MemoryLinkViewModel),
                nameof(MessagePickRow),
                nameof(EntryPickRow),
                nameof(ImportedCollectionRow),
                nameof(PersonalEntrySummary),
            ];

            internal List<string> Messages { get; } = [];

            public override void Write(string? message)
            {
            }

            public override void WriteLine(string? message)
            {
                if (message is not null && Names.Any(name => message.Contains(name, StringComparison.Ordinal)))
                {
                    lock (Messages)
                    {
                        Messages.Add(message);
                    }
                }
            }
        }
    }
}
