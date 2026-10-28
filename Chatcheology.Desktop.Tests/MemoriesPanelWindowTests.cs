using System.Diagnostics;
using System.IO;
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
                var chosen = Path.Combine(workspace.Root, "chosen");
                Directory.CreateDirectory(chosen);
                File.WriteAllBytes(Path.Combine(chosen, "a.png"), MemoryLayersTests.Png(2));
                File.WriteAllBytes(Path.Combine(chosen, "b.png"), MemoryLayersTests.Png(3));
                store.AddEntry(new PersonalEntryDraft
                {
                    CollectionID = holidays,
                    Title = "Beach week",
                    Start = new DateOnly(2018, 1, 2),
                    Precision = PersonalDatePrecision.Day,
                    Source = "My own memory",
                    Links = [PersonalLinkTarget.ToMessage(1)],

                    // CH-46: a cover and one captioned extra photo, so the form lists both.
                    Image = PersonalImageChoice.FromFile(Path.Combine(chosen, "a.png")),
                    ExtraPhotos = [new PersonalPhotoDraft(PersonalImageChoice.FromFile(Path.Combine(chosen, "b.png")), "Invented dunes")],
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
                    Assert.Equal(2, memories.Form.Photos.Count);
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

        [Fact]
        public void TheNotedDatesList_AndItsForm_BindWithoutErrors()
        {
            StaRunner.Run(() =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

                using var workspace = new ArchiveTestWorkspace();

                var notedStore = NotedDateStore.Beside(workspace.DatabasePath);
                var now = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
                notedStore.Add(new NotedDateDraft { Title = "Our anniversary", Month = 2, Day = 14, Year = 2019, Note = "Invented." }, true, now);
                notedStore.Add(new NotedDateDraft { Title = "Winter day", Month = 12, Day = 25 }, false, now);
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
                    PumpUntil(() => memories.IsOpen && !memories.IsBusy && memories.NotedDates.Count == 2);
                    Settle(window);

                    memories.SelectedNotedDate = memories.NotedDates[0];
                    Settle(window);

                    memories.EditNotedDateCommand.Execute(null);
                    PumpUntil(() => memories.NotedForm is not null);

                    // Touch every field so each error binding is evaluated.
                    memories.NotedForm!.Title = string.Empty;
                    memories.NotedForm.DayText = "31";
                    memories.NotedForm.SelectedMonth = memories.NotedForm.Months[3];
                    memories.NotedForm.YearText = "x";
                    memories.NotedForm.Note = new string('n', 501);
                    Settle(window);

                    Assert.True(memories.NotedForm.SaveCommand.CanExecute(null));
                    memories.NotedForm.SaveCommand.Execute(null);
                    Settle(window);
                    Assert.Equal("14 Feb", memories.NotedDates[0].DateText);
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
                nameof(MemoryFormPhotoViewModel),
                nameof(NotedDateRow),
                nameof(NotedDateFormViewModel),
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
