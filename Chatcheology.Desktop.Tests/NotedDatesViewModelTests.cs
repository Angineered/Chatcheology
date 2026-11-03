using System.IO;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Personal;
using Chatcheology.Desktop.ViewModels;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// The Noted dates section of the Memories area (CH-47 step 1b, reduced by CH-53): list order, edit, delete, the
    /// form's validation, no add, the move to yearly memories with its confirmation, the section gone when none
    /// remain, and the area's busy and closing behaviour. Invented titles and dates, a synthetic workspace, the real
    /// store.
    /// </summary>
    public sealed class NotedDatesViewModelTests : IDisposable
    {
        private static readonly DateTime Now = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

        private readonly ArchiveTestWorkspace _workspace = new();
        private readonly List<string> _deleteAsked = [];
        private readonly List<string> _moveAsked = [];
        private bool _confirmDelete = true;
        private bool _confirmMove = true;
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
            ConfirmCreateFile = _ => true,
            ConfirmDeleteNotedDate = text =>
            {
                _deleteAsked.Add(text);
                return _confirmDelete;
            },
            ConfirmMoveNotedDates = text =>
            {
                _moveAsked.Add(text);
                return _confirmMove;
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

        private static async Task SaveAsync(MemoriesViewModel memories)
        {
            var form = memories.NotedForm!;
            Assert.True(form.SaveCommand.CanExecute(null), "Save is not enabled.");

            form.SaveCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => !form.IsSaving && !form.SaveCommand.IsRunning);
            await memories.AfterNotedSave;
            await MemoriesViewModelTests.WaitUntil(() => !memories.IsBusy);
        }

        private static async Task RunAsync(AsyncRelayCommand command, MemoriesViewModel memories) =>
            await MemoriesViewModelTests.Run(command, memories);

        private static NotedDateFormViewModel Edit(MemoriesViewModel memories, int index = 0)
        {
            memories.SelectedNotedDate = memories.NotedDates[index];
            memories.EditNotedDateCommand.Execute(null);

            return memories.NotedForm!;
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

            Assert.True(memories.HasNotedDates);
            Assert.Equal(2, memories.NotedDatesWithYear);
            Assert.Contains("2 noted dates have no year and cannot be moved", memories.NotedDatesText, StringComparison.Ordinal);
        }

        [Fact]
        public async Task WithNoNotedDates_TheSectionIsGone_AndThereIsNoAdd()
        {
            var memories = await OpenAsync();

            Assert.Empty(memories.NotedDates);
            Assert.False(memories.HasNotedDates);
            Assert.False(memories.MoveNotedDatesCommand.CanExecute(null));
            Assert.False(File.Exists(MemoriesPath));
            Assert.Null(typeof(MemoriesViewModel).GetProperty("NewNotedDateCommand"));
        }

        [Fact]
        public async Task EditAndDelete_ChangeTheSelectedDate_AndTheLastDeleteRemovesTheSection()
        {
            Seed(Draft("Our anniversary", 2, 14, 2019, "First note."), Draft("Birthday", 6, 1));
            var memories = await OpenAsync();

            var form = Edit(memories);
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
            Assert.Equal("Noted date saved.", memories.StatusText);

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

            memories.SelectedNotedDate = memories.NotedDates[0];
            await RunAsync(memories.DeleteNotedDateCommand, memories);

            Assert.Empty(memories.NotedDates);
            Assert.False(memories.HasNotedDates);
        }

        [Fact]
        public async Task SavingAnIncompleteForm_ShowsEveryProblem_AndWritesNothing()
        {
            Seed(Draft("Our anniversary", 2, 14, 2019));
            var memories = await OpenAsync();
            var form = Edit(memories);

            form.Title = string.Empty;
            form.SelectedMonth = null;
            Assert.True(form.SaveCommand.CanExecute(null));

            form.SaveCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => !form.IsSaving && !form.SaveCommand.IsRunning);

            Assert.Equal("Give the date a title.", form.TitleError);
            Assert.Equal("Choose a month.", form.DateError);
            Assert.Same(form, memories.NotedForm);
            Assert.Equal("Our anniversary", Assert.Single(Store.List()).Title);
        }

        [Fact]
        public async Task TheForm_ShowsEachProblemOnItsField_AndSaveStaysAvailable()
        {
            Seed(Draft("Our anniversary", 2, 14));
            var memories = await OpenAsync();
            var form = Edit(memories);

            // As opened: nothing is shown, Save is available.
            Assert.Equal(string.Empty, form.TitleError);
            Assert.Equal(string.Empty, form.DateError);
            Assert.True(form.SaveCommand.CanExecute(null));

            form.Title = "   ";
            Assert.Equal("Give the date a title.", form.TitleError);

            form.Title = new string('x', 81);
            Assert.Equal("Keep the title to 80 characters.", form.TitleError);

            form.Title = "Fine";
            Assert.Equal(string.Empty, form.TitleError);

            form.SelectedMonth = null;
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
            Assert.True(form.SaveCommand.CanExecute(null));

            form.YearText = "2020";
            Assert.Equal(string.Empty, form.DateError);

            form.YearText = "1800";
            Assert.Equal("The year must be from 1900 to 2026, or left out.", form.YearError);

            form.YearText = "next year";
            Assert.Equal("Type the year as a number, or leave it out.", form.YearError);
            Assert.True(form.SaveCommand.CanExecute(null));

            form.YearText = string.Empty;
            Assert.Equal(string.Empty, form.YearError);

            form.Note = new string('n', 501);
            Assert.Equal("Keep the note to 500 characters.", form.NoteError);
            Assert.True(form.SaveCommand.CanExecute(null));

            // A blank note is just no note.
            form.Note = "   ";
            Assert.Equal(string.Empty, form.NoteError);
            Assert.True(form.SaveCommand.CanExecute(null));

            form.CancelCommand.Execute(null);
            Assert.Equal("Our anniversary", Assert.Single(Store.List()).Title);
        }

        [Fact]
        public async Task ADateDeletedElsewhere_IsReportedOnTheForm_AndNothingIsWritten()
        {
            Seed(Draft("Short lived", 5, 5));
            var memories = await OpenAsync();
            var form = Edit(memories);

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
            Seed(Draft("Seeded", 1, 1, 2019));
            var memories = await OpenAsync();
            memories.SelectedNotedDate = memories.NotedDates[0];

            Assert.True(memories.MoveNotedDatesCommand.CanExecute(null));
            Assert.True(memories.EditNotedDateCommand.CanExecute(null));
            Assert.True(memories.DeleteNotedDateCommand.CanExecute(null));

            _blocked = true;
            memories.RaiseCommandStates();
            Assert.False(memories.MoveNotedDatesCommand.CanExecute(null));
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
            Assert.False(memories.MoveNotedDatesCommand.CanExecute(null));
            Assert.False(memories.EditNotedDateCommand.CanExecute(null));
            Assert.False(memories.DeleteNotedDateCommand.CanExecute(null));
            Assert.False(memories.AddCollectionCommand.CanExecute(null));
        }

        [Fact]
        public async Task ClosingTheArea_DropsTheListAndTheForm_AndALateFormResultIsIgnored()
        {
            Seed(Draft("Seeded", 1, 1));
            var memories = await OpenAsync();

            var form = Edit(memories);
            form.Title = "Late";

            memories.Close();

            Assert.Null(memories.NotedForm);
            Assert.Empty(memories.NotedDates);
            Assert.False(memories.HasNotedDates);
            Assert.Null(memories.SelectedNotedDate);
            Assert.False(memories.IsOpen);

            // The old form reports back after the area was closed and reopened: nothing happens to the new one.
            await MemoriesViewModelTests.Run(memories.OpenCommand, memories);
            Assert.Single(memories.NotedDates);

            form.SaveCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => !form.IsSaving && !form.SaveCommand.IsRunning);

            Assert.Null(memories.NotedForm);
            Assert.Equal("Seeded", Assert.Single(memories.NotedDates).Title);
            Assert.Equal(string.Empty, memories.StatusText);
        }

        [Fact]
        public async Task TheArea_CountsANotedSaveAsWriting_SoTheShellHoldsOtherWorkOff()
        {
            Seed(Draft("Seeded", 1, 1));
            var memories = await OpenAsync();
            Edit(memories).Title = "Another";

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
            Assert.Equal("Another", Assert.Single(memories.NotedDates).Title);
        }

        // ---------------------------------------------------------------------------------------
        // Move noted dates to memories (CH-53).
        // ---------------------------------------------------------------------------------------

        [Fact]
        public async Task TheMove_AsksWithTheCount_AndDecliningMovesNothing()
        {
            Seed(Draft("Our anniversary", 2, 14, 2019), Draft("Year end", 12, 31, 2000), Draft("Birthday", 8, 2));
            var memories = await OpenAsync();
            var before = File.ReadAllBytes(MemoriesPath);
            SqliteConnection.ClearAllPools();

            _confirmMove = false;
            await RunAsync(memories.MoveNotedDatesCommand, memories);

            var question = Assert.Single(_moveAsked);
            Assert.StartsWith("Move 2 noted dates to memories?", question, StringComparison.Ordinal);
            Assert.Contains("\"Noted dates\"", question, StringComparison.Ordinal);
            Assert.Contains("memories.db.before-CH-53 (or the next free name after it)", question, StringComparison.Ordinal);
            Assert.Contains("1 noted date has no year and stays here.", question, StringComparison.Ordinal);
            Assert.Equal("Nothing was moved.", memories.StatusText);
            Assert.Equal(before, File.ReadAllBytes(MemoriesPath));
            Assert.Equal(3, memories.NotedDates.Count);
        }

        [Fact]
        public async Task TheMove_MakesYearlyMemories_AndTheSectionKeepsOnlyTheYearlessDate()
        {
            Seed(Draft("Our anniversary", 2, 14, 2019), Draft("Year end", 12, 31, 2000), Draft("Birthday", 8, 2));
            var memories = await OpenAsync();
            var changed = 0;
            memories.FileChanged += (_, _) => changed++;

            await RunAsync(memories.MoveNotedDatesCommand, memories);

            Assert.Equal("Moved 2 noted dates to memories. The copy from before is memories.db.before-CH-53. 1 noted date has no year and stayed.", memories.StatusText);
            Assert.Equal(1, changed);
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(MemoriesPath)!, NotedDateStore.MoveBackupFileName)));

            // The new collection is selected, with the two yearly memories.
            var collection = Assert.Single(memories.Collections);
            Assert.Equal("Noted dates", collection.Name);
            Assert.Same(collection, memories.SelectedCollection);
            Assert.Equal(["Year end", "Our anniversary"], memories.Entries.Select(e => e.Title));
            Assert.All(memories.Entries, e => Assert.True(e.EveryYear));

            // Only the yearless date is left: no move possible, the section still shows for edit and delete.
            Assert.Equal("Birthday", Assert.Single(memories.NotedDates).Title);
            Assert.True(memories.HasNotedDates);
            Assert.Equal(0, memories.NotedDatesWithYear);
            Assert.False(memories.MoveNotedDatesCommand.CanExecute(null));
            Assert.Contains("1 noted date has no year", memories.NotedDatesText, StringComparison.Ordinal);

            // Given a year, it can be moved too; then the section is gone. The first copy stays; the second move
            // makes the next one.
            var form = Edit(memories);
            form.YearText = "2001";
            await SaveAsync(memories);
            Assert.True(memories.MoveNotedDatesCommand.CanExecute(null));

            await RunAsync(memories.MoveNotedDatesCommand, memories);

            Assert.Equal("Moved 1 noted date to memories. The copy from before is memories.db.before-CH-53-2.", memories.StatusText);
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(MemoriesPath)!, NotedDateStore.MoveBackupFileName)));
            Assert.Empty(memories.NotedDates);
            Assert.False(memories.HasNotedDates);
            Assert.Equal(3, Assert.Single(memories.Collections).Entries.Count);
        }
    }
}
