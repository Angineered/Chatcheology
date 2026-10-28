using System.IO;
using System.Security.Cryptography;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Personal;
using Chatcheology.Desktop.Services;
using Chatcheology.Desktop.ViewModels;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// The Memories area and its form (CH-42 step 4), against a synthetic workspace and a memories file
    /// beside it: the real store, the real read-only message reader, invented names and bytes.
    /// </summary>
    public sealed class MemoriesViewModelTests : IDisposable
    {
        /// <summary>A 1×1 PNG.</summary>
        internal static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        private static readonly DateTime Day = new(2026, 4, 6);

        private readonly ArchiveTestWorkspace _workspace = new();
        private readonly List<string> _createAsked = [];
        private readonly List<string> _deleteAsked = [];

        private readonly List<string> _deleteCollectionAsked = [];
        private readonly List<string> _tidyAsked = [];

        private bool _confirmCreate = true;
        private bool _confirmDelete = true;
        private bool _confirmTidy = true;
        private bool _confirmCover;
        private bool _withCoverDialog = true;
        private readonly List<string> _coverAsked = [];
        private string? _image;
        private IReadOnlyList<string>? _images;

        public void Dispose() => _workspace.Dispose();

        private string MemoriesPath => MemoriesDatabase.PathBeside(_workspace.DatabasePath);

        // ---------------------------------------------------------------------------------------
        // The file and collections.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public async Task TheAreaNeedsAnOpenConversation()
        {
            var viewModel = new MainViewModel(() => null);

            Assert.False(viewModel.Memories.OpenCommand.CanExecute(null));

            var opened = await OpenedAsync();
            Assert.True(opened.Memories.OpenCommand.CanExecute(null));

            opened.CloseSession();
            Assert.False(opened.Memories.IsOpen);
            Assert.False(opened.Memories.OpenCommand.CanExecute(null));
        }

        [Fact]
        public async Task WithNoFile_TheFirstCollectionCreatesItOnlyAfterConfirmation()
        {
            var memories = await MemoriesAsync();

            Assert.True(memories.IsOpen);
            Assert.False(memories.FileExists);
            Assert.StartsWith("No memories file beside this workspace yet.", memories.StatusText, StringComparison.Ordinal);

            _confirmCreate = false;
            memories.CollectionName = "Holidays";
            await Run(memories.AddCollectionCommand, memories);

            Assert.Single(_createAsked);
            Assert.Equal("Nothing was created.", memories.StatusText);
            Assert.False(File.Exists(MemoriesPath));

            _confirmCreate = true;
            await Run(memories.AddCollectionCommand, memories);

            Assert.Equal(2, _createAsked.Count);
            Assert.True(File.Exists(MemoriesPath));
            Assert.True(memories.FileExists);
            Assert.Equal("Holidays", Assert.Single(memories.Collections).Name);
            Assert.Equal("Holidays", memories.SelectedCollection?.Name);
            Assert.Equal("Collection added.", memories.StatusText);

            // Once it exists, nobody is asked again.
            memories.CollectionName = "Birthdays";
            await Run(memories.AddCollectionCommand, memories);
            Assert.Equal(2, _createAsked.Count);
            Assert.Equal(["Birthdays", "Holidays"], memories.Collections.Select(c => c.Name));
        }

        [Fact]
        public async Task CollectionNames_AreCheckedNextToTheField()
        {
            var memories = await WithCollectionAsync("Holidays");

            memories.CollectionName = "  ";
            await Run(memories.AddCollectionCommand, memories);
            Assert.Equal("Give the collection a name.", memories.CollectionNameError);

            memories.CollectionName = "holidays";
            await Run(memories.AddCollectionCommand, memories);
            Assert.Equal("You already have a collection with that name.", memories.CollectionNameError);

            memories.CollectionName = "Trips";
            Assert.Equal(string.Empty, memories.CollectionNameError);

            await Run(memories.RenameCollectionCommand, memories);
            Assert.Equal("Trips", Assert.Single(memories.Collections).Name);
            Assert.Equal("Collection renamed.", memories.StatusText);
        }

        [Fact]
        public async Task ImportedCollections_AreListedReadOnly_ByNameAndCount()
        {
            var memories = await WithCollectionAsync("Holidays");
            AddImported();

            await memories.RefreshAsync(null, null);

            var imported = Assert.Single(memories.ImportedCollections);
            Assert.Equal("Shared gaming: 1 entry (imported, read-only)", imported.Text);
            Assert.Equal("Holidays", Assert.Single(memories.Collections).Name);
        }

        [Fact]
        public async Task NothingIsWritten_WhileAnExportOrDecisionHoldsTheArea()
        {
            var blocked = true;
            var memories = new MemoriesViewModel(
                Dialogs(), () => new MemoriesContext(_workspace.DatabasePath, 1), () => blocked);

            await Run(memories.OpenCommand, memories);
            memories.CollectionName = "Holidays";

            Assert.False(memories.AddCollectionCommand.CanExecute(null));

            blocked = false;
            Assert.True(memories.AddCollectionCommand.CanExecute(null));
        }

        // ---------------------------------------------------------------------------------------
        // The form.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public async Task TheForm_ShowsEachProblemNextToItsField_AndWritesNothing()
        {
            var memories = await WithCollectionAsync("Holidays");

            memories.NewEntryCommand.Execute(null);
            var form = Assert.IsType<MemoryEntryFormViewModel>(memories.Form);
            Assert.True(form.IsNew);
            Assert.Equal("Holidays", form.SelectedCollection?.Name);
            Assert.False(memories.IsListShown);

            await Run(form.SaveCommand, form);

            Assert.Equal("Give the memory a title.", form.TitleError);
            Assert.Equal("Choose the date it starts.", form.StartError);
            Assert.Equal("Say where this memory comes from.", form.SourceError);

            form.Title = "Beach week";
            Assert.Equal(string.Empty, form.TitleError);

            form.Source = "My own memory";
            form.StartDate = new DateTime(2018, 1, 9);
            form.EndDate = new DateTime(2018, 1, 2);
            await Run(form.SaveCommand, form);

            Assert.Equal("The end cannot be before the start.", form.EndError);

            // At Month precision the same dates are one month: fine.
            form.Precision = PersonalDatePrecision.Month;
            Assert.Equal(string.Empty, form.EndError);

            form.CancelCommand.Execute(null);
            Assert.Null(memories.Form);
            Assert.Equal(0, Count("SELECT COUNT(*) FROM Entry;"));
        }

        [Fact]
        public async Task TheMessagePicker_ListsThatDaysMessages_AndAddsALink()
        {
            var memories = await WithCollectionAsync("Holidays");
            memories.NewEntryCommand.Execute(null);
            var form = memories.Form!;

            form.PickerDate = Day.AddDays(1);
            await form.PickerLoad;
            Assert.Empty(form.PickerMessages);
            Assert.Equal("0 messages and 0 memories on this day.", form.PickerText);
            Assert.False(form.AddMessageLinkCommand.CanExecute(null));

            form.PickerDate = Day;
            await form.PickerLoad;

            Assert.Equal([1, 2, 3, 4, 5], form.PickerMessages.Select(m => m.Message.SequenceNumber));
            Assert.Equal("09:00 Ada Fictional: " + ArchiveTestWorkspace.TextMarker + " before", form.PickerMessages[0].Text);

            form.SelectedPickerMessage = form.PickerMessages[1];
            form.AddMessageLinkCommand.Execute(null);
            form.AddMessageLinkCommand.Execute(null);

            var link = Assert.Single(form.Links);
            Assert.Equal(PersonalLinkTarget.ToMessage(2), link.Target);
            Assert.StartsWith("Message 2: 6 April 2026, 09:01, Ada Fictional: ", link.Text, StringComparison.Ordinal);
            Assert.Equal("That link is already there.", form.LinksError);

            link.RemoveCommand.Execute(null);
            Assert.Empty(form.Links);
        }

        [Fact]
        public async Task TheEntryPicker_ListsThatDaysMemories_ButNotTheOneBeingEdited()
        {
            var memories = await WithCollectionAsync("Holidays");
            var first = await SaveAsync(memories, "First", new DateTime(2018, 1, 2));
            var second = await SaveAsync(memories, "Second", new DateTime(2018, 1, 2));

            memories.SelectedEntry = memories.Entries.Single(e => e.EntryID == second);
            await Run(memories.EditEntryCommand, memories);
            var form = memories.Form!;

            form.PickerDate = new DateTime(2018, 1, 2);
            await form.PickerLoad;

            var row = Assert.Single(form.PickerEntries);
            Assert.Equal(first, row.Entry.EntryID);
            Assert.Equal("First, 2 January 2018 (Holidays)", row.Text);

            form.SelectedPickerEntry = row;
            form.AddEntryLinkCommand.Execute(null);
            await Run(form.SaveCommand, form);
            await memories.AfterSave;

            Assert.Equal($"{second}|{first}", Text("SELECT FromEntryID || '|' || ToEntryID FROM EntryLink;"));
        }

        [Fact]
        public async Task ChoosingAnImage_ShowsIt_AndAWrongFileIsRefusedNextToTheField()
        {
            var memories = await WithCollectionAsync("Holidays");
            memories.NewEntryCommand.Execute(null);
            var form = memories.Form!;

            _image = Chosen("notes.png", "not an image"u8.ToArray());
            await Run(form.ChooseImageCommand, form);

            Assert.Equal("notes.png: The chosen file is not a PNG, JPEG, GIF or WebP image.", form.ImageError);
            Assert.False(form.HasImage);

            _image = Chosen("beach.png", Png);
            await Run(form.ChooseImageCommand, form);

            Assert.Equal(string.Empty, form.ImageError);
            Assert.True(form.HasImage);
            Assert.NotNull(form.ImagePreview);
            Assert.Equal("New image: 1 KB", form.ImageText);
            Assert.True(form.RemoveImageCommand.CanExecute(null));

            form.RemoveImageCommand.Execute(null);
            Assert.False(form.HasImage);
            Assert.Null(form.ImagePreview);

            // Declining the dialog changes nothing.
            _image = null;
            await Run(form.ChooseImageCommand, form);
            Assert.False(form.HasImage);
        }

        // ---------------------------------------------------------------------------------------
        // Several photos (CH-46).
        // ---------------------------------------------------------------------------------------

        [Fact]
        public async Task AddingSeveralPhotos_AddsTheGoodOnes_AndNamesEachFailedFile()
        {
            var memories = await WithCollectionAsync("Holidays");
            memories.NewEntryCommand.Execute(null);
            var form = memories.Form!;

            _images =
            [
                Chosen("beach.png", Png),
                Chosen("notes.png", "not an image"u8.ToArray()),
                Chosen("dunes.png", MemoryLayersTests.Png(3)),
                Chosen("copy.png", Png),
            ];
            await Run(form.ChooseImageCommand, form);

            Assert.Equal(2, form.Photos.Count);
            Assert.True(form.Photos[0].IsCover);
            Assert.False(form.Photos[0].CanEditCaption);
            Assert.True(form.Photos[1].CanEditCaption);
            Assert.True(form.HasExtraPhotos);
            Assert.Equal(
                "notes.png: The chosen file is not a PNG, JPEG, GIF or WebP image.\ncopy.png: that photo is already in this memory.",
                form.ImageError);
            Assert.StartsWith("New image: 1 KB (beach.png)", form.ImageText, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Photos_CanBeReorderedCaptionedRemovedAndMadeTheCover_AndSaveInOneGo()
        {
            var memories = await WithCollectionAsync("Holidays");
            memories.NewEntryCommand.Execute(null);
            var form = memories.Form!;
            form.Title = "Beach week";
            form.Source = "My own memory";
            form.StartDate = new DateTime(2018, 1, 2);

            var a = MemoryLayersTests.Png(2);
            var b = MemoryLayersTests.Png(3);
            var c = MemoryLayersTests.Png(4);
            _images = [Chosen("a.png", a), Chosen("b.png", b), Chosen("c.png", c)];
            await Run(form.ChooseImageCommand, form);

            // c becomes the cover; a moves down below b; b gets a caption; then a is removed and re-added.
            var photoA = form.Photos[0];
            var photoB = form.Photos[1];
            var photoC = form.Photos[2];

            Assert.False(photoA.MoveUpCommand.CanExecute(null));
            Assert.False(photoA.MakeCoverCommand.CanExecute(null));
            photoC.MakeCoverCommand.Execute(null);
            Assert.Equal([photoC, photoA, photoB], form.Photos);
            Assert.True(photoC.IsCover);
            Assert.False(photoA.IsCover);

            photoA.MoveDownCommand.Execute(null);
            Assert.Equal([photoC, photoB, photoA], form.Photos);
            Assert.False(photoA.MoveDownCommand.CanExecute(null));

            photoB.Caption = "Invented dunes";
            photoA.Caption = "  ";

            await Run(form.SaveCommand, form);
            await memories.AfterSave;

            Assert.Equal(Sha(c), Text("SELECT ImageSha256 FROM Entry;"));
            Assert.Equal(
                $"1|{Sha(b)}|Invented dunes;2|{Sha(a)}|-",
                Text("SELECT group_concat(Ordinal || '|' || Sha256 || '|' || ifnull(Caption, '-'), ';') FROM (SELECT * FROM EntryImage ORDER BY Ordinal);"));

            // Edit: the photos come back in order with their captions; removing the cover makes the next one the cover.
            await Run(memories.EditEntryCommand, memories);
            form = memories.Form!;

            Assert.Equal(3, form.Photos.Count);
            Assert.Equal(["", "Invented dunes", ""], form.Photos.Select(p => p.Caption));
            Assert.All(form.Photos, p => Assert.Equal("Current image", p.Text));

            // Removing the cover would make the captioned photo the cover: asked first; declined, nothing changes.
            var cover = form.Photos[0];
            cover.RemoveCommand.Execute(null);
            Assert.Equal(3, form.Photos.Count);
            Assert.Same(cover, form.Photos[0]);

            _confirmCover = true;
            cover.RemoveCommand.Execute(null);
            Assert.True(form.Photos[0].IsCover);
            Assert.Equal(string.Empty, form.Photos[0].Caption);
            Assert.Equal(2, _coverAsked.Count);
            Assert.Contains("\"Invented dunes\" will be removed", _coverAsked[1], StringComparison.Ordinal);

            await Run(form.SaveCommand, form);
            await memories.AfterSave;

            Assert.Equal(Sha(b), Text("SELECT ImageSha256 FROM Entry;"));
            Assert.Equal($"1|{Sha(a)}", Text("SELECT group_concat(Ordinal || '|' || Sha256, ';') FROM EntryImage;"));

            // Remove all: no cover, no extra photos; the stored files stay.
            await Run(memories.EditEntryCommand, memories);
            form = memories.Form!;
            form.RemoveImageCommand.Execute(null);
            Assert.False(form.HasImage);
            await Run(form.SaveCommand, form);
            await memories.AfterSave;

            Assert.Equal(string.Empty, Text("SELECT ImageSha256 FROM Entry;"));
            Assert.Equal(0, Count("SELECT COUNT(*) FROM EntryImage;"));
            Assert.Equal(3, Directory.GetFiles(MemoriesDatabase.MediaFolderFor(MemoriesPath)).Length);
        }

        [Fact]
        public async Task MakingACaptionedPhotoTheCover_AsksFirst_AndOnlyThenDropsTheCaption()
        {
            var memories = await WithCollectionAsync("Holidays");
            memories.NewEntryCommand.Execute(null);
            var form = memories.Form!;

            _images = [Chosen("a.png", MemoryLayersTests.Png(2)), Chosen("b.png", MemoryLayersTests.Png(3)), Chosen("c.png", MemoryLayersTests.Png(4))];
            await Run(form.ChooseImageCommand, form);
            var (a, b, c) = (form.Photos[0], form.Photos[1], form.Photos[2]);
            b.Caption = "Invented dunes";

            // Declined (make cover, move up into first place, move the cover down): nothing changes.
            _confirmCover = false;
            b.MakeCoverCommand.Execute(null);
            b.MoveUpCommand.Execute(null);
            a.MoveDownCommand.Execute(null);
            Assert.Equal([a, b, c], form.Photos);
            Assert.Equal("Invented dunes", b.Caption);
            Assert.Equal(3, _coverAsked.Count);
            Assert.Equal("The cover has no caption, so \"Invented dunes\" will be removed. Make it the cover?", _coverAsked[0]);

            // A photo without a caption needs no question.
            c.MakeCoverCommand.Execute(null);
            Assert.Equal([c, a, b], form.Photos);
            Assert.Equal(3, _coverAsked.Count);

            // Accepted: it becomes the cover and its caption is gone, so nothing is lost silently on save.
            _confirmCover = true;
            b.MakeCoverCommand.Execute(null);

            Assert.Equal([b, c, a], form.Photos);
            Assert.True(b.IsCover);
            Assert.Equal(string.Empty, b.Caption);
            Assert.Equal(4, _coverAsked.Count);
        }

        [Fact]
        public async Task WithNoCoverDialog_ACaptionedPhotoNeverBecomesTheCover()
        {
            _withCoverDialog = false;
            var memories = await WithCollectionAsync("Holidays");
            memories.NewEntryCommand.Execute(null);
            var form = memories.Form!;

            _images = [Chosen("a.png", MemoryLayersTests.Png(2)), Chosen("b.png", MemoryLayersTests.Png(3))];
            await Run(form.ChooseImageCommand, form);
            var second = form.Photos[1];
            second.Caption = "Invented dunes";

            second.MakeCoverCommand.Execute(null);
            form.Photos[0].RemoveCommand.Execute(null);

            Assert.Equal(2, form.Photos.Count);
            Assert.Same(second, form.Photos[1]);
            Assert.Equal("Invented dunes", second.Caption);
        }

        // ---------------------------------------------------------------------------------------
        // Deleting a collection and tidying up images (CH-46).
        // ---------------------------------------------------------------------------------------


        [Fact]
        public async Task AnEmptyCollection_IsDeletedAfterASimpleConfirmation()
        {
            var memories = await WithCollectionAsync("Holidays");

            _confirmDelete = false;
            await Run(memories.DeleteCollectionCommand, memories);

            Assert.Equal("Delete the empty collection \"Holidays\"?", Assert.Single(_deleteCollectionAsked));
            Assert.Equal("Nothing was deleted.", memories.StatusText);
            Assert.Single(memories.Collections);

            _confirmDelete = true;
            await Run(memories.DeleteCollectionCommand, memories);

            Assert.Empty(memories.Collections);
            Assert.Null(memories.SelectedCollection);
            Assert.Equal("Collection deleted.", memories.StatusText);
            Assert.False(memories.DeleteCollectionCommand.CanExecute(null));
        }

        [Fact]
        public async Task ACollectionWithMemories_SaysHowManyItRemoves_AndImportedOnesHaveNoDeleteAction()
        {
            var memories = await WithCollectionAsync("Holidays");
            await SaveAsync(memories, "First", new DateTime(2018, 1, 2), linkTo: 3);
            await SaveAsync(memories, "Second", new DateTime(2018, 1, 3));
            AddImported();
            await memories.RefreshAsync(null, null);

            // Only personal collections are selectable for it; the imported one is a read-only line.
            Assert.All(memories.Collections, c => Assert.NotEqual("Shared gaming", c.Name));
            Assert.Single(memories.ImportedCollections);

            memories.SelectedCollection = memories.Collections.Single(c => c.Name == "Holidays");
            await Run(memories.DeleteCollectionCommand, memories);

            Assert.Equal(
                "Delete the collection \"Holidays\" and the 2 memories in it, with their links and photos? " +
                "The image files stay in the memories folder. This cannot be undone.",
                Assert.Single(_deleteCollectionAsked));
            Assert.Equal("Collection deleted, with 2 memories and 1 link.", memories.StatusText);
            Assert.Equal(0, Count("SELECT COUNT(*) FROM Entry WHERE CollectionID IN (SELECT CollectionID FROM Collection WHERE Kind = 'Personal');"));
            Assert.Equal(1, Count("SELECT COUNT(*) FROM Collection;"));
        }

        [Fact]
        public async Task TidyUp_ListsCountAndSize_AndDeletesOnlyAfterConfirmation()
        {
            var memories = await WithCollectionAsync("Holidays");
            var media = MemoriesDatabase.MediaFolderFor(MemoriesPath);
            Directory.CreateDirectory(media);
            var stray = MemoryLayersTests.Png(5);
            var strayPath = Path.Combine(media, Sha(stray) + ".png");
            File.WriteAllBytes(strayPath, stray);
            File.WriteAllText(Path.Combine(media, "readme.txt"), "synthetic");

            _confirmTidy = false;
            await Run(memories.TidyImagesCommand, memories);

            var question = Assert.Single(_tidyAsked);
            Assert.StartsWith("1 image (1 KB) in the memories folder is not used by any memory, game image or trophy icon. Delete it?", question, StringComparison.Ordinal);
            Assert.Contains("1 other file or folder in the folder is not named like a stored image and is never touched.", question, StringComparison.Ordinal);
            Assert.Equal("Nothing was deleted.", memories.StatusText);
            Assert.True(File.Exists(strayPath));

            _confirmTidy = true;
            await Run(memories.TidyImagesCommand, memories);

            Assert.Equal("Deleted 1 unused image (1 KB).", memories.StatusText);
            Assert.False(File.Exists(strayPath));
            Assert.True(File.Exists(Path.Combine(media, "readme.txt")));

            await Run(memories.TidyImagesCommand, memories);
            Assert.StartsWith("No unused images: 0 images are in use.", memories.StatusText, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TidyUp_NeedsAMemoriesFile()
        {
            var memories = await MemoriesAsync();

            Assert.False(memories.FileExists);
            Assert.False(memories.TidyImagesCommand.CanExecute(null));
        }

        [Fact]
        public async Task SaveEditAndDelete_RoundTheList_AndKeepTheEntryIDAndLinks()

        {
            var memories = await WithCollectionAsync("Holidays");
            memories.NewEntryCommand.Execute(null);
            var form = memories.Form!;

            form.Title = "Beach week";
            form.Source = "My own memory";
            form.Summary = "Invented days by the sea.";
            form.StartDate = new DateTime(2018, 1, 2);
            form.EndDate = new DateTime(2018, 1, 9);
            _image = Chosen("beach.png", Png);
            await Run(form.ChooseImageCommand, form);
            form.PickerDate = Day;
            await form.PickerLoad;
            form.SelectedPickerMessage = form.PickerMessages[4];
            form.AddMessageLinkCommand.Execute(null);

            await Run(form.SaveCommand, form);
            await memories.AfterSave;

            Assert.Null(memories.Form);
            Assert.Equal("Memory saved.", memories.StatusText);
            var entry = Assert.Single(memories.Entries);
            Assert.Equal(entry, memories.SelectedEntry);
            Assert.Equal("Beach week", entry.Title);
            Assert.Equal("2 January 2018 to 9 January 2018", entry.DateText);
            Assert.Equal("Holidays (1)", memories.SelectedCollection!.DisplayText);

            // Edit: the form opens with what was saved.
            await Run(memories.EditEntryCommand, memories);
            form = memories.Form!;

            Assert.False(form.IsNew);
            Assert.Equal("Beach week", form.Title);
            Assert.Equal(new DateTime(2018, 1, 9), form.EndDate);
            Assert.Equal("Current image", form.ImageText);
            Assert.NotNull(form.ImagePreview);
            Assert.StartsWith("Message 5: 6 April 2026, 09:04, Bo Fictional: ", Assert.Single(form.Links).Text, StringComparison.Ordinal);

            var linkID = Text("SELECT EntryLinkID FROM EntryLink;");

            form.Title = "Beach fortnight";
            form.Precision = PersonalDatePrecision.Month;
            await Run(form.SaveCommand, form);
            await memories.AfterSave;

            Assert.Equal(entry.EntryID, Assert.Single(memories.Entries).EntryID);
            Assert.Equal("January 2018", memories.Entries[0].DateText);
            Assert.Equal(linkID, Text("SELECT EntryLinkID FROM EntryLink;"));
            Assert.Equal(Sha(Png), Text("SELECT ImageSha256 FROM Entry;"));

            // Delete: declined, then confirmed.
            _confirmDelete = false;
            await Run(memories.DeleteEntryCommand, memories);
            Assert.Equal("Nothing was deleted.", memories.StatusText);
            Assert.Single(memories.Entries);

            _confirmDelete = true;
            await Run(memories.DeleteEntryCommand, memories);

            Assert.Equal(2, _deleteAsked.Count);
            Assert.Contains("Beach fortnight", _deleteAsked[1], StringComparison.Ordinal);
            Assert.Equal("Memory deleted, with 1 link.", memories.StatusText);
            Assert.Empty(memories.Entries);
            Assert.Equal(0, Count("SELECT COUNT(*) FROM EntryLink;"));
        }

        [Fact]
        public async Task TheWorkspace_IsNeverWritten()
        {
            SqliteConnection.ClearAllPools();
            var before = SHA256.HashData(File.ReadAllBytes(_workspace.DatabasePath));

            var viewModel = await OpenedAsync();
            var memories = viewModel.Memories;
            await Run(memories.OpenCommand, memories);
            memories.CollectionName = "Holidays";
            await Run(memories.AddCollectionCommand, memories);
            await SaveAsync(memories, "Beach week", new DateTime(2018, 1, 2), linkTo: 3);

            viewModel.CloseSession();
            SqliteConnection.ClearAllPools();

            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(_workspace.DatabasePath)));
        }

        [Fact]
        public async Task ChangingTheConversationOrClosing_ClosesTheArea()
        {
            var viewModel = await OpenedAsync();
            await Run(viewModel.Memories.OpenCommand, viewModel.Memories);

            Assert.True(viewModel.Memories.IsOpen);

            viewModel.SelectedConversation = null;

            Assert.False(viewModel.Memories.IsOpen);
            Assert.Empty(viewModel.Memories.Collections);
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        internal MemoriesDialogs Dialogs() => new()
        {
            ChooseImage = () => _image,
            ChooseImages = () => _images ?? (_image is { } one ? [one] : []),
            ConfirmCoverCaption = _withCoverDialog
                ? text =>
                {
                    _coverAsked.Add(text);
                    return _confirmCover;
                }
                : null,
            ConfirmDeleteCollection = text =>
            {
                _deleteCollectionAsked.Add(text);
                return _confirmDelete;
            },
            ConfirmTidyImages = text =>
            {
                _tidyAsked.Add(text);
                return _confirmTidy;
            },
            ConfirmCreateFile = text =>
            {
                _createAsked.Add(text);
                return _confirmCreate;
            },
            ConfirmDelete = text =>
            {
                _deleteAsked.Add(text);
                return _confirmDelete;
            },
        };

        internal async Task<MainViewModel> OpenedAsync()
        {
            var viewModel = new MainViewModel(
                () => _workspace.DatabasePath, ArchiveDialogs.None, new ArchiveOperations(), Dialogs());

            viewModel.OpenWorkspaceCommand.Execute(null);

            await WaitUntil(() => viewModel.Messages.Count > 0 && !viewModel.IsBusy);

            return viewModel;
        }

        private async Task<MemoriesViewModel> MemoriesAsync()
        {
            var viewModel = await OpenedAsync();
            await Run(viewModel.Memories.OpenCommand, viewModel.Memories);

            return viewModel.Memories;
        }

        private async Task<MemoriesViewModel> WithCollectionAsync(string name)
        {
            var memories = await MemoriesAsync();
            memories.CollectionName = name;
            await Run(memories.AddCollectionCommand, memories);

            return memories;
        }

        /// <summary>Saves a memory through the form; returns its identifier.</summary>
        internal static async Task<long> SaveAsync(MemoriesViewModel memories, string title, DateTime start, int? linkTo = null)
        {
            memories.NewEntryCommand.Execute(null);
            var form = memories.Form!;

            form.Title = title;
            form.Source = "My own memory";
            form.StartDate = start;

            if (linkTo is { } sequence)
            {
                form.PickerDate = Day;
                await form.PickerLoad;
                form.SelectedPickerMessage = form.PickerMessages.Single(m => m.Message.SequenceNumber == sequence);
                form.AddMessageLinkCommand.Execute(null);
            }

            await Run(form.SaveCommand, form);
            Assert.Equal(string.Empty, form.FormError);
            await memories.AfterSave;

            return memories.Entries.Single(e => e.Title == title).EntryID;
        }

        /// <summary>Runs a command and waits until it has finished (it can execute again, or its owner is idle).</summary>
        internal static async Task Run(AsyncRelayCommand command, object owner)
        {
            Assert.True(command.CanExecute(null), "The command cannot run.");

            command.Execute(null);

            await WaitUntil(() => owner switch
            {
                MemoriesViewModel memories => !memories.IsBusy && command.CanExecuteWhenIdle(),
                MemoryEntryFormViewModel form => !form.IsSaving && command.CanExecuteWhenIdle(),
                _ => true,
            });
        }

        internal static async Task WaitUntil(Func<bool> condition, int timeoutMilliseconds = 30_000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);

            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(10);
            }

            Assert.Fail("The view-model did not reach the expected state in time.");
        }

        private string Chosen(string name, byte[] bytes)
        {
            var folder = Path.Combine(_workspace.Root, "chosen");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, bytes);

            return path;
        }

        private void AddImported()
        {
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
        }

        private long Count(string sql) => long.Parse(Text(sql), System.Globalization.CultureInfo.InvariantCulture);

        private string Text(string sql)
        {
            using var connection = MemoriesDatabase.OpenReadOnlyConnection(MemoriesPath);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            connection.Dispose();
            SqliteConnection.ClearAllPools();

            return value;
        }

        internal static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    internal static class AsyncRelayCommandTestExtensions
    {
        /// <summary>Whether the command has finished running (its own running flag is down).</summary>
        internal static bool CanExecuteWhenIdle(this AsyncRelayCommand command) => !command.IsRunning;
    }
}
