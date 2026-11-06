using System.IO;
using Chatcheology.Archive;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Personal;
using Chatcheology.Desktop.ViewModels;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// Gallery links (CH-48 step B) in the memory form and the noted-date form: add, remove, reorder, every
    /// validation path, the ordered list that is saved, the failure after the item saved, and that imported
    /// memories have no editor. Invented titles and addresses, a synthetic workspace, the real stores.
    /// </summary>
    public sealed class GalleryLinksViewModelTests : IDisposable
    {
        private static readonly DateTime Now = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
        private const string One = "https://photos.example.org/album/one";
        private const string Two = "https://albums.example.net/two";
        private const string Three = "https://gallery.example.com/three";

        private readonly ArchiveTestWorkspace _workspace = new();

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            _workspace.Dispose();
        }

        private string MemoriesPath => MemoriesDatabase.PathBeside(_workspace.DatabasePath);

        private ExternalLinkStore Links => new(MemoriesPath);

        private async Task<MemoriesViewModel> OpenAsync()
        {
            var memories = new MemoriesViewModel(
                new MemoriesDialogs
                {
                    ChooseImage = () => null,
                    ConfirmDelete = _ => true,
                    ConfirmCreateFile = _ => true,
                    ConfirmDeleteNotedDate = _ => true,
                },
                () => new MemoriesContext(_workspace.DatabasePath, 1),
                () => false,
                utcNow: () => Now);

            await MemoriesViewModelTests.Run(memories.OpenCommand, memories);
            memories.CollectionName = "Holidays";
            await MemoriesViewModelTests.Run(memories.AddCollectionCommand, memories);

            return memories;
        }

        private static MemoryEntryFormViewModel NewForm(MemoriesViewModel memories, string title = "Beach week")
        {
            memories.NewEntryCommand.Execute(null);
            var form = memories.Form!;
            form.Title = title;
            form.Source = "My own memory";
            form.StartDate = new DateTime(2018, 1, 2);

            return form;
        }

        private static GalleryLinkRowViewModel AddRow(GalleryLinksEditor editor, string url, string label = "")
        {
            editor.AddCommand.Execute(null);
            var row = editor.Rows[^1];
            row.Url = url;
            row.Label = label;

            return row;
        }

        private static async Task SaveFormAsync(MemoriesViewModel memories, MemoryEntryFormViewModel form)
        {
            await MemoriesViewModelTests.Run(form.SaveCommand, form);
            await memories.AfterSave;
            await MemoriesViewModelTests.WaitUntil(() => !memories.IsBusy);
        }

        private void BlockLinkWrites(bool block)
        {
            using (var connection = MemoriesDatabase.OpenConnection(MemoriesPath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = block
                    ? "CREATE TRIGGER BlockLinks BEFORE INSERT ON ExternalLink BEGIN SELECT RAISE(ABORT, 'blocked'); END;"
                    : "DROP TRIGGER BlockLinks;";
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();
        }

        // ---- the editor ----

        [Fact]
        public async Task Rows_AreAdded_Removed_AndMoved_UpToTen()
        {
            var memories = await OpenAsync();
            var editor = NewForm(memories).GalleryLinks;

            Assert.Empty(editor.Rows);
            var first = AddRow(editor, One, "Day 1");
            var second = AddRow(editor, Two);
            var third = AddRow(editor, Three);

            Assert.False(first.MoveUpCommand.CanExecute(null));
            Assert.True(first.MoveDownCommand.CanExecute(null));
            Assert.False(third.MoveDownCommand.CanExecute(null));

            third.MoveUpCommand.Execute(null);
            Assert.Equal([first, third, second], editor.Rows);
            Assert.Equal("Link 2", third.Heading);

            first.MoveDownCommand.Execute(null);
            Assert.Equal([third, first, second], editor.Rows);

            first.RemoveCommand.Execute(null);
            Assert.Equal([third, second], editor.Rows);
            Assert.Equal("2 of 10 links", editor.CountText);

            while (editor.Rows.Count < ExternalLinkRules.MaxLinksPerOwner)
            {
                Assert.True(editor.AddCommand.CanExecute(null));
                editor.AddCommand.Execute(null);
            }

            Assert.False(editor.AddCommand.CanExecute(null));
            editor.AddCommand.Execute(null);
            Assert.Equal(10, editor.Rows.Count);
            Assert.Equal(GalleryLinksEditor.Notice, editor.NoticeText);
        }

        [Fact]
        public async Task ASaveAttempt_ShowsEveryProblemOnItsRow_AndWritesNothing()
        {
            var memories = await OpenAsync();
            var form = NewForm(memories, "Beach week");
            var editor = form.GalleryLinks;

            var blank = AddRow(editor, "   ");
            var http = AddRow(editor, "http://photos.example.org/a");
            var userInfo = AddRow(editor, "https://friend@photos.example.org/a");
            var space = AddRow(editor, "https://photos.example.org/a b");
            var tooLong = AddRow(editor, "https://photos.example.org/" + new string('a', 2000));
            var badLabel = AddRow(editor, One, new string('x', 61));
            var good = AddRow(editor, Two, "Fine");
            form.Title = string.Empty;

            await MemoriesViewModelTests.Run(form.SaveCommand, form);

            Assert.Equal("Enter the link.", blank.UrlError);
            Assert.Equal("Only links starting with https:// can be kept.", http.UrlError);
            Assert.NotEmpty(userInfo.UrlError);
            Assert.NotEmpty(space.UrlError);
            Assert.NotEmpty(tooLong.UrlError);
            Assert.Equal(string.Empty, badLabel.UrlError);
            Assert.NotEmpty(badLabel.LabelError);
            Assert.Equal(string.Empty, good.UrlError);
            Assert.Equal(string.Empty, good.LabelError);

            // The memory's own problem shows at the same time, and nothing was written.
            Assert.NotEmpty(form.TitleError);
            Assert.Same(form, memories.Form);
            Assert.Empty(memories.Entries);
            Assert.Empty(Links.ReadForEntry(1));

            // No error repeats an address.
            Assert.DoesNotContain(
                "example.org",
                blank.UrlError + http.UrlError + userInfo.UrlError + space.UrlError + tooLong.UrlError,
                StringComparison.Ordinal);

            // Editing a row clears that row's problem.
            http.Url = One;
            Assert.Equal(string.Empty, http.UrlError);
            Assert.NotEmpty(blank.UrlError);
        }

        [Fact]
        public async Task ABadLink_SavesNothing_EvenWhenTheMemoryIsOtherwiseValid()
        {
            var memories = await OpenAsync();
            var form = NewForm(memories);
            AddRow(form.GalleryLinks, "ftp://photos.example.org/a");

            await MemoriesViewModelTests.Run(form.SaveCommand, form);

            Assert.Empty(memories.Entries);
            Assert.Equal(string.Empty, form.LinksError);
            Assert.NotEmpty(form.GalleryLinks.Rows[0].UrlError);
        }

        [Fact]
        public void AStoreRefusal_IsShownOnItsRow_NeverOnTheMessageLinksList()
        {
            var editor = new GalleryLinksEditor([new ExternalLink(1, 1, One, null), new ExternalLink(2, 2, Two, null)], () => true);

            editor.ShowStoreError(new PersonalMemoryValidationException(
                PersonalFields.Links, "Link 2: Only links starting with https:// can be kept.") { LinkIndex = 1 });

            Assert.Equal(string.Empty, editor.Rows[0].UrlError);
            Assert.Equal("Only links starting with https:// can be kept.", editor.Rows[1].UrlError);

            editor.ShowStoreError(new PersonalMemoryValidationException(PersonalFields.Links, "Keep to 10 links."));
            Assert.Equal("Keep to 10 links.", editor.ListError);
        }

        // ---- saving a memory ----

        [Fact]
        public async Task Save_WritesTheOrderedList_AndEditingLoadsItBack()
        {
            var memories = await OpenAsync();
            var form = NewForm(memories);
            AddRow(form.GalleryLinks, "  " + One + "  ", " Day 1 ");
            AddRow(form.GalleryLinks, Two);
            AddRow(form.GalleryLinks, Three, "Lisa's album");
            form.GalleryLinks.Rows[2].MoveUpCommand.Execute(null);

            await SaveFormAsync(memories, form);

            Assert.Null(memories.Form);
            var id = memories.Entries.Single().EntryID;
            var saved = Links.ReadForEntry(id);
            Assert.Equal([One, Three, Two], saved.Select(l => l.Url));
            Assert.Equal(["Day 1", "Lisa's album", null], saved.Select(l => l.Label));
            Assert.Equal([1, 2, 3], saved.Select(l => l.Ordinal));

            memories.SelectedEntry = memories.Entries[0];
            await memories.SelectedEntryGalleryRead;
            Assert.Equal("3 links", memories.SelectedEntryGalleryText);

            await MemoriesViewModelTests.Run(memories.EditEntryCommand, memories);
            var edit = memories.Form!;
            Assert.Equal([One, Three, Two], edit.GalleryLinks.Rows.Select(r => r.Url));
            Assert.Equal("Day 1", edit.GalleryLinks.Rows[0].Label);

            // Remove two: the list is replaced.
            edit.GalleryLinks.Rows[2].RemoveCommand.Execute(null);
            edit.GalleryLinks.Rows[1].RemoveCommand.Execute(null);
            await SaveFormAsync(memories, edit);
            Assert.Equal([One], Links.ReadForEntry(id).Select(l => l.Url));

            memories.SelectedEntry = null;
            memories.SelectedEntry = memories.Entries[0];
            await memories.SelectedEntryGalleryRead;
            Assert.Equal("Link · photos.example.org", memories.SelectedEntryGalleryText);

            // Clear them all.
            await MemoriesViewModelTests.Run(memories.EditEntryCommand, memories);
            memories.Form!.GalleryLinks.Rows[0].RemoveCommand.Execute(null);
            await SaveFormAsync(memories, memories.Form);
            Assert.Empty(Links.ReadForEntry(id));
        }

        [Fact]
        public async Task AMemoryWithNoLinks_SavesAsBefore()
        {
            var memories = await OpenAsync();
            var form = NewForm(memories);
            await SaveFormAsync(memories, form);

            var id = Assert.Single(memories.Entries).EntryID;
            Assert.Empty(Links.ReadForEntry(id));
            Assert.Equal(string.Empty, memories.SelectedEntryGalleryText);
        }

        [Fact]
        public async Task AFailedLinkSave_AfterTheMemorySaved_IsShown_AndSavingAgainUpdatesThatMemory()
        {
            var memories = await OpenAsync();
            var form = NewForm(memories);
            AddRow(form.GalleryLinks, One);
            BlockLinkWrites(true);

            await MemoriesViewModelTests.Run(form.SaveCommand, form);

            Assert.Same(form, memories.Form);
            Assert.StartsWith("The memory was saved, but its links were not.", form.FormError, StringComparison.Ordinal);
            Assert.False(form.IsNew);

            // Fix it and save again: the same memory is updated, not duplicated.
            BlockLinkWrites(false);
            await SaveFormAsync(memories, form);

            Assert.Null(memories.Form);
            var id = Assert.Single(memories.Entries).EntryID;
            Assert.Equal([One], Links.ReadForEntry(id).Select(l => l.Url));
        }

        [Fact]
        public async Task CancelAfterAFailedLinkSave_KeepsTheMemory_AndRefreshesTheList()
        {
            var memories = await OpenAsync();
            var form = NewForm(memories);
            AddRow(form.GalleryLinks, One);
            BlockLinkWrites(true);
            await MemoriesViewModelTests.Run(form.SaveCommand, form);

            form.CancelCommand.Execute(null);
            await memories.AfterSave;
            await MemoriesViewModelTests.WaitUntil(() => !memories.IsBusy);

            Assert.Null(memories.Form);
            Assert.Equal("Beach week", Assert.Single(memories.Entries).Title);
        }

        [Fact]
        public async Task ImportedMemories_HaveNoEditorAndNoLinks()
        {
            var memories = await OpenAsync();

            using (var connection = MemoriesDatabase.OpenConnection(MemoriesPath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO Collection (Kind, Name, SourceDescription, ShownByDefault, PeriodGapMinutes)
                    VALUES ('Imported', 'Shared gaming', 'Synthetic shared activity', 0, 60);
                    INSERT INTO Entry (CollectionID, SourceKey, Title, StartLocal, DatePrecision, Source, DayLevel)
                    VALUES (last_insert_rowid(), 'setA|setB|2016-05-01', 'Synthetic Game', '2016-05-01T20:00:00', 'Second', 'PSN trophies', 'strong');
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();
            await memories.RefreshAsync(null, null);

            // The imported collection is only a read-only line: no collection to select, no entry to edit.
            Assert.Single(memories.ImportedCollections);
            Assert.DoesNotContain(memories.Collections, c => c.Name == "Shared gaming");
            Assert.Empty(memories.Entries);

            long imported;

            using (var connection = MemoriesDatabase.OpenReadOnlyConnection(MemoriesPath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT EntryID FROM Entry WHERE Title = 'Synthetic Game';";
                imported = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }

            SqliteConnection.ClearAllPools();
            Assert.Empty(Links.ReadForEntry(imported));
        }

        // ---- saving a noted date ----

        private static async Task SaveNotedAsync(MemoriesViewModel memories)
        {
            var form = memories.NotedForm!;
            form.SaveCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => !form.IsSaving && !form.SaveCommand.IsRunning);
            await memories.AfterNotedSave;
            await MemoriesViewModelTests.WaitUntil(() => !memories.IsBusy);
        }

        /// <summary>
        /// A noted date without links, made through the store (CH-53: the desktop no longer adds them), opened in the
        /// form for editing.
        /// </summary>
        private async Task<NotedDateFormViewModel> EditNotedAsync(MemoriesViewModel memories)
        {
            NotedDateStore.Beside(_workspace.DatabasePath).Add(new NotedDateDraft { Title = "Our anniversary", Month = 2, Day = 14 }, false, Now);
            SqliteConnection.ClearAllPools();
            await memories.RefreshAsync(null, null);

            memories.SelectedNotedDate = Assert.Single(memories.NotedDates);
            memories.EditNotedDateCommand.Execute(null);

            return memories.NotedForm!;
        }

        [Fact]
        public async Task ANotedDate_SavesItsOrderedLinks_ShowsThemInTheList_AndEditsThem()
        {
            var memories = await OpenAsync();
            var form = await EditNotedAsync(memories);
            AddRow(form.GalleryLinks, One, "Day 1");
            AddRow(form.GalleryLinks, Two);
            form.GalleryLinks.Rows[1].MoveUpCommand.Execute(null);

            await SaveNotedAsync(memories);

            var row = Assert.Single(memories.NotedDates);
            Assert.Equal([Two, One], Links.ReadForNotedDate(row.NotedDateID).Select(l => l.Url));
            Assert.Equal("2 links", row.GalleryText);

            memories.SelectedNotedDate = row;
            memories.EditNotedDateCommand.Execute(null);
            var edit = memories.NotedForm!;
            Assert.Equal([Two, One], edit.GalleryLinks.Rows.Select(r => r.Url));
            edit.GalleryLinks.Rows[0].RemoveCommand.Execute(null);
            await SaveNotedAsync(memories);

            Assert.Equal("Link · photos.example.org", Assert.Single(memories.NotedDates).GalleryText);
        }

        [Fact]
        public async Task ANotedDateWithABadLink_ShowsEveryProblem_AndWritesNothing()
        {
            var memories = await OpenAsync();
            var form = await EditNotedAsync(memories);
            var dateID = memories.NotedDates[0].NotedDateID;
            var bad = AddRow(form.GalleryLinks, "javascript:alert(1)");
            var badLabel = AddRow(form.GalleryLinks, One, "Day\n1");
            form.Title = string.Empty;

            form.SaveCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => !form.SaveCommand.IsRunning);

            Assert.NotEmpty(bad.UrlError);
            Assert.NotEmpty(badLabel.LabelError);
            Assert.NotEmpty(form.TitleError);
            Assert.Empty(Links.ReadForNotedDate(dateID));
            Assert.Equal("Our anniversary", Assert.Single(NotedDateStore.Beside(_workspace.DatabasePath).List()).Title);
            Assert.Same(form, memories.NotedForm);

            // The title fixed, the link still bad: still nothing written.
            form.Title = "Our anniversary";
            form.SaveCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => !form.SaveCommand.IsRunning);
            Assert.Empty(Links.ReadForNotedDate(dateID));
        }

        [Fact]
        public async Task AWhitespaceOnlyLabel_IsNoLabel_AndSaves()
        {
            var memories = await OpenAsync();
            var form = NewForm(memories);
            var row = AddRow(form.GalleryLinks, One, "   ");

            await SaveFormAsync(memories, form);

            Assert.Equal(string.Empty, row.LabelError);
            Assert.Null(memories.Form);
            var saved = Assert.Single(Links.ReadForEntry(memories.Entries.Single().EntryID));
            Assert.Null(saved.Label);
        }

        [Fact]
        public async Task AFailedLinkSave_AfterTheNotedDateSaved_IsShown_AndSavingAgainUpdatesThatDate()
        {
            var memories = await OpenAsync();
            var form = await EditNotedAsync(memories);
            form.Title = "Our big day";
            AddRow(form.GalleryLinks, One);
            BlockLinkWrites(true);

            form.SaveCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => !form.IsSaving && !form.SaveCommand.IsRunning);

            Assert.Same(form, memories.NotedForm);
            Assert.StartsWith("The noted date was saved, but its links were not.", form.FormError, StringComparison.Ordinal);
            Assert.False(form.IsNew);

            BlockLinkWrites(false);
            await SaveNotedAsync(memories);

            Assert.Null(memories.NotedForm);
            var saved = Assert.Single(memories.NotedDates);
            Assert.Equal("Our big day", saved.Title);
            Assert.Equal([One], Links.ReadForNotedDate(saved.NotedDateID).Select(l => l.Url));
        }

        [Fact]
        public void TheGalleryText_IsOneHost_OrACount()
        {
            Assert.Equal(string.Empty, GalleryWording.For([]));
            Assert.Equal("Link · photos.example.org", GalleryWording.For([new ExternalLink(1, 1, One, null)]));
            Assert.Equal("3 links", GalleryWording.For(
                [new ExternalLink(1, 1, One, null), new ExternalLink(2, 2, Two, null), new ExternalLink(3, 3, Three, null)]));
        }
    }
}
