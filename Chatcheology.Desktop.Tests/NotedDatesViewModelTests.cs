using System.IO;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Personal;
using Chatcheology.Desktop.ViewModels;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// The Noted dates section of the Memories area (CH-47 step 1b): list order, add, edit, delete, the
    /// form's validation, the first-create confirmation, and the area's busy and closing behaviour. Invented
    /// titles and dates, a synthetic workspace, the real store.
    /// </summary>
    public sealed class NotedDatesViewModelTests : IDisposable
    {
        private static readonly DateTime Now = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

        private readonly ArchiveTestWorkspace _workspace = new();
        private readonly List<string> _createAsked = [];
        private readonly List<string> _deleteAsked = [];
        private bool _confirmCreate = true;
        private bool _confirmDelete = true;
        private bool _blocked;

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            _workspace.Dispose();
        }

        private string MemoriesPath => MemoriesDatabase.PathBeside(_workspace.DatabasePath);

        private NotedDateStore Store => NotedDateStore.Beside(_workspace.DatabasePath);

        private MemoriesDialogs Dialogs() => new()
        {
            ChooseImage = () => null,
            ConfirmDelete = _ => false,
            ConfirmCreateFile = text =>
            {
                _createAsked.Add(text);
                return _confirmCreate;
            },
            ConfirmDeleteNotedDate = text =>
            {
                _deleteAsked.Add(text);
                return _confirmDelete;
            },
        };

        private async Task<MemoriesViewModel> OpenAsync()
        {
            var memories = new MemoriesViewModel(
                Dialogs(), () => new MemoriesContext(_workspace.DatabasePath, 1), () => _blocked, utcNow: () => Now);

            await MemoriesViewModelTests.Run(memories.OpenCommand, memories);

            return memories;
        }

        private static NotedDateDraft Draft(string title, int month, int day, int? year = null, string? note = null) =>
            new() { Title = title, Month = month, Day = day, Year = year, Note = note };

        private void Seed(params NotedDateDraft[] drafts)
        {
            var first = true;

            foreach (var draft in drafts)
            {
                Store.Add(draft, first, Now);
                first = false;
            }

            SqliteConnection.ClearAllPools();
        }

        private static async Task FillAsync(NotedDateFormViewModel form, string title, string day, int month, string year = "", string note = "")
        {
            form.Title = title;
            form.DayText = day;
            form.SelectedMonth = form.Months[month - 1];
            form.YearText = year;
            form.Note = note;
            await Task.CompletedTask;
        }

        private static async Task SaveAsync(MemoriesViewModel memories)
        {
            var form = memories.NotedForm!;
            Assert.True(form.SaveCommand.CanExecute(null), "Save is not enabled.");

            form.SaveCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => !form.IsSaving && !form.SaveCommand.IsRunning);
            await memories.AfterNotedSave;
            await MemoriesViewModelTests.WaitUntil(() => !memories.IsBusy);
        }

        [Fact]
        public async Task TheList_IsByMonthThenDay_WithTheYearWhenSet()
        {
            Seed(
                Draft("Winter holiday", 12, 25),
                Draft("Our anniversary", 2, 14, 2019),
                Draft("Pet adopted", 2, 3, 2021, "A small invented note."),
                Draft("Another one", 2, 14));

            var memories = await OpenAsync();

            Assert.Equal(
                ["3 Feb: Pet adopted, since 2021", "14 Feb: Another one", "14 Feb: Our anniversary, since 2019", "25 Dec: Winter holiday"],
                memories.NotedDates.Select(d => d.DisplayText));
            Assert.Equal("14 Feb", memories.NotedDates[1].DateText);
            Assert.Equal(string.Empty, memories.NotedDates[1].SinceText);
            Assert.Equal("since 2019", memories.NotedDates[2].SinceText);
        }

        [Fact]
        public async Task WithNoFile_TheListIsEmpty_AndNothingIsCreatedByOpening()
        {
            var memories = await OpenAsync();

            Assert.Empty(memories.NotedDates);
            Assert.False(File.Exists(MemoriesPath));
            Assert.True(memories.NewNotedDateCommand.CanExecute(null));
        }

        [Fact]
        public async Task TheFirstNotedDate_AsksBeforeCreatingTheFile_AndCancelCreatesNothing()
        {
            var memories = await OpenAsync();

            memories.NewNotedDateCommand.Execute(null);
            var form = Assert.IsType<NotedDateFormViewModel>(memories.NotedForm);
            Assert.True(form.IsNew);
            Assert.Equal("New noted date", form.Heading);
            Assert.False(memories.IsListShown);

            await FillAsync(form, "Our anniversary", "14", 2, "2019");

            _confirmCreate = false;
            form.SaveCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => !form.SaveCommand.IsRunning);

            Assert.Single(_createAsked);
            Assert.Equal("Nothing was created.", form.FormError);
            Assert.False(File.Exists(MemoriesPath));
            Assert.Same(form, memories.NotedForm);
            Assert.False(memories.IsWriting);

            // Cancel: the form closes and still nothing exists.
            form.CancelCommand.Execute(null);
            Assert.Null(memories.NotedForm);
            Assert.True(memories.IsListShown);
            Assert.False(File.Exists(MemoriesPath));

            // Now say yes.
            _confirmCreate = true;
            memories.NewNotedDateCommand.Execute(null);
            await FillAsync(memories.NotedForm!, "Our anniversary", "14", 2, "2019");
            await SaveAsync(memories);

            Assert.Equal(2, _createAsked.Count);
            Assert.True(File.Exists(MemoriesPath));
            Assert.Equal("14 Feb: Our anniversary, since 2019", Assert.Single(memories.NotedDates).DisplayText);
            Assert.Equal("Noted date saved.", memories.StatusText);
            Assert.Null(memories.NotedForm);

            // Once the file exists, nobody is asked again.
            memories.NewNotedDateCommand.Execute(null);
            await FillAsync(memories.NotedForm!, "Second", "1", 1);
            await SaveAsync(memories);
            Assert.Equal(2, _createAsked.Count);
            Assert.Equal(2, memories.NotedDates.Count);
        }

        [Fact]
        public async Task EditAndDelete_ChangeTheSelectedDate()
        {
            Seed(Draft("Our anniversary", 2, 14, 2019, "First note."), Draft("Birthday", 6, 1));
            var memories = await OpenAsync();

            memories.SelectedNotedDate = memories.NotedDates[0];
            memories.EditNotedDateCommand.Execute(null);

            var form = Assert.IsType<NotedDateFormViewModel>(memories.NotedForm);
            Assert.False(form.IsNew);
            Assert.Equal("Edit noted date", form.Heading);
            Assert.Equal("Our anniversary", form.Title);
            Assert.Equal("14", form.DayText);
            Assert.Equal("February", form.SelectedMonth?.Name);
            Assert.Equal("2019", form.YearText);
            Assert.Equal("First note.", form.Note);

            form.Title = "Our big day";
            form.DayText = "15";
            form.YearText = string.Empty;
            await SaveAsync(memories);

            var edited = memories.NotedDates[0];
            Assert.Equal("15 Feb: Our big day", edited.DisplayText);
            Assert.Equal(edited.NotedDateID, memories.SelectedNotedDate?.NotedDateID);
            Assert.Equal(2, memories.NotedDates.Count);

            // Delete asks; declining keeps it.
            _confirmDelete = false;
            await RunAsync(memories.DeleteNotedDateCommand, memories);
            Assert.Equal("Nothing was deleted.", memories.StatusText);
            Assert.Equal(2, memories.NotedDates.Count);
            Assert.Contains("Our big day", Assert.Single(_deleteAsked), StringComparison.Ordinal);

            _confirmDelete = true;
            await RunAsync(memories.DeleteNotedDateCommand, memories);

            Assert.Equal("Noted date deleted.", memories.StatusText);
            Assert.Equal("1 Jun: Birthday", Assert.Single(memories.NotedDates).DisplayText);
            Assert.Null(memories.SelectedNotedDate);
            Assert.Single(Store.List());
        }

        private static async Task RunAsync(AsyncRelayCommand command, MemoriesViewModel memories) =>
            await MemoriesViewModelTests.Run(command, memories);

        [Fact]
        public async Task TheForm_ShowsEachProblemOnItsField_AndSaveStaysDisabled()
        {
            var memories = await OpenAsync();
            memories.NewNotedDateCommand.Execute(null);
            var form = memories.NotedForm!;

            // Untouched: nothing is shown, but Save is not available.
            Assert.Equal(string.Empty, form.TitleError);
            Assert.Equal(string.Empty, form.DateError);
            Assert.False(form.SaveCommand.CanExecute(null));

            form.Title = "   ";
            Assert.Equal("Give the date a title.", form.TitleError);

            form.Title = new string('x', 81);
            Assert.Equal("Keep the title to 80 characters.", form.TitleError);

            form.Title = "Fine";
            Assert.Equal(string.Empty, form.TitleError);

            form.DayText = "31";
            Assert.Equal("Choose a month.", form.DateError);

            form.SelectedMonth = form.Months[3];
            Assert.Equal("That day is not in that month.", form.DateError);

            form.DayText = "abc";
            Assert.Equal("That day is not in that month.", form.DateError);

            form.DayText = "29";
            form.SelectedMonth = form.Months[1];
            Assert.Equal(string.Empty, form.DateError);
            Assert.True(form.SaveCommand.CanExecute(null));

            form.YearText = "2019";
            Assert.Equal("29 February is only in a leap year: choose a leap year or leave the year out.", form.DateError);
            Assert.False(form.SaveCommand.CanExecute(null));

            form.YearText = "2020";
            Assert.Equal(string.Empty, form.DateError);

            form.YearText = "1800";
            Assert.Equal("The year must be from 1900 to 2026, or left out.", form.YearError);

            form.YearText = "next year";
            Assert.Equal("Type the year as a number, or leave it out.", form.YearError);
            Assert.False(form.SaveCommand.CanExecute(null));

            form.YearText = string.Empty;
            Assert.Equal(string.Empty, form.YearError);

            form.Note = new string('n', 501);
            Assert.Equal("Keep the note to 500 characters.", form.NoteError);
            Assert.False(form.SaveCommand.CanExecute(null));

            // A blank note is just no note.
            form.Note = "   ";
            Assert.Equal(string.Empty, form.NoteError);
            Assert.True(form.SaveCommand.CanExecute(null));

            form.CancelCommand.Execute(null);
            Assert.False(File.Exists(MemoriesPath));
        }

        [Fact]
        public async Task ADateDeletedElsewhere_IsReportedOnTheForm_AndNothingIsWritten()
        {
            Seed(Draft("Short lived", 5, 5));
            var memories = await OpenAsync();

            memories.SelectedNotedDate = memories.NotedDates[0];
            memories.EditNotedDateCommand.Execute(null);
            var form = memories.NotedForm!;

            Store.Delete(memories.NotedDates[0].NotedDateID);
            SqliteConnection.ClearAllPools();

            form.Title = "Still editing";
            form.SaveCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => !form.IsSaving && !form.SaveCommand.IsRunning);

            Assert.Equal("That date is no longer there. Nothing was written.", form.FormError);
            Assert.Same(form, memories.NotedForm);
        }

        [Fact]
        public async Task NothingIsWritten_WhileBlocked_OrWhileAMemoryFormIsOpen()
        {
            Seed(Draft("Seeded", 1, 1));
            var memories = await OpenAsync();
            memories.SelectedNotedDate = memories.NotedDates[0];

            Assert.True(memories.NewNotedDateCommand.CanExecute(null));
            Assert.True(memories.EditNotedDateCommand.CanExecute(null));
            Assert.True(memories.DeleteNotedDateCommand.CanExecute(null));

            _blocked = true;
            memories.RaiseCommandStates();
            Assert.False(memories.NewNotedDateCommand.CanExecute(null));
            Assert.False(memories.EditNotedDateCommand.CanExecute(null));
            Assert.False(memories.DeleteNotedDateCommand.CanExecute(null));

            // An open form's Save is held too.
            _blocked = false;
            memories.EditNotedDateCommand.Execute(null);
            var form = memories.NotedForm!;
            form.Title = "Changed";
            Assert.True(form.SaveCommand.CanExecute(null));
            _blocked = true;
            Assert.False(form.SaveCommand.CanExecute(null));

            // While the form is open the list's commands wait, and so do the collection commands.
            _blocked = false;
            memories.RaiseCommandStates();
            Assert.False(memories.NewNotedDateCommand.CanExecute(null));
            Assert.False(memories.EditNotedDateCommand.CanExecute(null));
            Assert.False(memories.DeleteNotedDateCommand.CanExecute(null));
            Assert.False(memories.AddCollectionCommand.CanExecute(null));
        }

        [Fact]
        public async Task ClosingTheArea_DropsTheListAndTheForm_AndALateFormResultIsIgnored()
        {
            Seed(Draft("Seeded", 1, 1));
            var memories = await OpenAsync();

            memories.NewNotedDateCommand.Execute(null);
            var form = memories.NotedForm!;
            await FillAsync(form, "Late", "2", 2);

            memories.Close();

            Assert.Null(memories.NotedForm);
            Assert.Empty(memories.NotedDates);
            Assert.Null(memories.SelectedNotedDate);
            Assert.False(memories.IsOpen);

            // The old form reports back after the area was closed and reopened: nothing happens to the new one.
            await MemoriesViewModelTests.Run(memories.OpenCommand, memories);
            Assert.Single(memories.NotedDates);

            await SaveFormAsync(form);

            Assert.Null(memories.NotedForm);
            Assert.Single(memories.NotedDates);
            Assert.Equal(string.Empty, memories.StatusText);
        }

        private static async Task SaveFormAsync(NotedDateFormViewModel form)
        {
            form.SaveCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => !form.IsSaving && !form.SaveCommand.IsRunning);
        }

        [Fact]
        public async Task TheArea_CountsANotedSaveAsWriting_SoTheShellHoldsOtherWorkOff()
        {
            Seed(Draft("Seeded", 1, 1));
            var memories = await OpenAsync();

            memories.NewNotedDateCommand.Execute(null);
            await FillAsync(memories.NotedForm!, "Another", "2", 2);

            var sawWriting = false;
            memories.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MemoriesViewModel.IsWriting) && memories.IsWriting)
                {
                    sawWriting = true;
                }
            };

            await SaveAsync(memories);

            Assert.True(sawWriting);
            Assert.False(memories.IsWriting);
            Assert.Equal(2, memories.NotedDates.Count);
        }
    }
}
