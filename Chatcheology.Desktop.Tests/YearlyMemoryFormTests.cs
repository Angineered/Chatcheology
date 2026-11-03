using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Personal;
using Chatcheology.Desktop.ViewModels;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// The memory form's Every year check box (CH-53): only for a memory dated to the day without an end date;
    /// choosing Month or an end clears it; it is saved and read back. Invented titles, a synthetic workspace, the
    /// real stores.
    /// </summary>
    public sealed class YearlyMemoryFormTests : IDisposable
    {
        private static readonly DateTime Now = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

        private readonly ArchiveTestWorkspace _workspace = new();

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            _workspace.Dispose();
        }

        private PersonalMemoryStore Store =>
            PersonalMemoryStore.Beside(_workspace.DatabasePath, new WorkspaceMessageLinkTargets(_workspace.DatabasePath, 1));

        private async Task<MemoriesViewModel> OpenAsync()
        {
            var memories = new MemoriesViewModel(
                new MemoriesDialogs { ChooseImage = () => null, ConfirmDelete = _ => false, ConfirmCreateFile = _ => true },
                () => new MemoriesContext(_workspace.DatabasePath, 1),
                () => false,
                utcNow: () => Now);

            await MemoriesViewModelTests.Run(memories.OpenCommand, memories);
            memories.CollectionName = "Holidays";
            await MemoriesViewModelTests.Run(memories.AddCollectionCommand, memories);

            return memories;
        }

        private static MemoryEntryFormViewModel NewForm(MemoriesViewModel memories)
        {
            memories.NewEntryCommand.Execute(null);
            var form = memories.Form!;
            form.Title = "Our anniversary";
            form.Source = "My own memory";
            form.StartDate = new DateTime(2016, 3, 14);

            return form;
        }

        private static async Task SaveAsync(MemoriesViewModel memories, MemoryEntryFormViewModel form)
        {
            await MemoriesViewModelTests.Run(form.SaveCommand, form);
            await memories.AfterSave;
            await MemoriesViewModelTests.WaitUntil(() => !memories.IsBusy);
        }

        [Fact]
        public async Task EveryYear_IsAvailableOnlyForADayWithoutAnEnd_AndMonthOrAnEndClearsIt()
        {
            var memories = await OpenAsync();
            var form = NewForm(memories);
            var changed = new List<string?>();
            form.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            Assert.True(form.IsEveryYearAvailable);
            Assert.False(form.EveryYear);
            Assert.Equal("Shown on On this day each year, with a notification on the phone.", form.EveryYearText);

            form.EveryYear = true;
            Assert.True(form.EveryYear);

            // Month clears it, and it cannot be ticked again while Month is chosen.
            form.Precision = PersonalDatePrecision.Month;
            Assert.False(form.IsEveryYearAvailable);
            Assert.False(form.EveryYear);
            Assert.Contains(nameof(MemoryEntryFormViewModel.EveryYear), changed);
            Assert.Contains(nameof(MemoryEntryFormViewModel.IsEveryYearAvailable), changed);

            form.EveryYear = true;
            Assert.False(form.EveryYear);

            // Back to Day it can be ticked; an end date clears it.
            form.Precision = PersonalDatePrecision.Day;
            Assert.True(form.IsEveryYearAvailable);
            Assert.False(form.EveryYear);
            form.EveryYear = true;
            form.EndDate = new DateTime(2016, 3, 20);
            Assert.False(form.IsEveryYearAvailable);
            Assert.False(form.EveryYear);

            form.EndDate = null;
            Assert.True(form.IsEveryYearAvailable);
            Assert.False(form.EveryYear);
            Assert.False(form.Draft().EveryYear);
        }

        [Fact]
        public async Task AYearlyMemory_IsSaved_ReadBackInTheForm_AndCanBeSwitchedOff()
        {
            var memories = await OpenAsync();
            var form = NewForm(memories);
            form.EveryYear = true;

            await SaveAsync(memories, form);

            var entry = Assert.Single(memories.Entries);
            Assert.True(entry.EveryYear);
            Assert.True(Store.ReadEntry(entry.EntryID).EveryYear);
            SqliteConnection.ClearAllPools();

            memories.SelectedEntry = entry;
            await MemoriesViewModelTests.Run(memories.EditEntryCommand, memories);
            var edit = memories.Form!;
            Assert.True(edit.EveryYear);
            Assert.True(edit.IsEveryYearAvailable);

            // Choosing Month clears it, so the save is allowed and the memory is no longer yearly.
            edit.Precision = PersonalDatePrecision.Month;
            await SaveAsync(memories, edit);

            Assert.False(Assert.Single(memories.Entries).EveryYear);
            Assert.Equal(MemoriesDatabase.PrecisionMonth, Assert.Single(memories.Entries).Precision);
        }

        [Fact]
        public async Task TheStore_RefusesWhatTheFormNeverSends_ByTheEveryYearField()
        {
            var memories = await OpenAsync();
            var form = NewForm(memories);
            form.EveryYear = true;

            // The form keeps the rule itself; a refused draft (as if the dates changed underneath) shows its message here.
            var draft = form.Draft() with { Precision = PersonalDatePrecision.Month };
            var refusal = Assert.Throws<PersonalMemoryValidationException>(() => Store.AddEntry(draft));
            Assert.Equal("Every year needs a memory dated to the day, not a month.", refusal.Errors[PersonalFields.EveryYear]);

            await SaveAsync(memories, form);
            Assert.Equal(string.Empty, form.EveryYearError);
            Assert.True(Assert.Single(memories.Entries).EveryYear);
        }
    }
}
