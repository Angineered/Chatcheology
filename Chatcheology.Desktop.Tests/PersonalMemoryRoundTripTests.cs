using System.IO;
using System.Text;
using Chatcheology.Archive;
using Chatcheology.Data.Memories;
using Chatcheology.Data.OlderExports;
using Chatcheology.Desktop.Services;
using Chatcheology.Desktop.ViewModels;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Memories;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// CH-42 step 4 round trip: a personal memory made in the desktop form (image and a message link),
    /// exported by the existing exporter, and read by the phone's memory index as a card at its date with
    /// the image and the link re-mapped onto the archive's timeline. Invented data only.
    /// </summary>
    public sealed class PersonalMemoryRoundTripTests : IDisposable
    {
        private readonly ArchiveTestWorkspace _workspace = new();

        public void Dispose() => _workspace.Dispose();

        [Fact]
        public async Task AMemoryFromTheForm_ReachesThePhone_WithItsImageAndRemappedLink()
        {
            // Two older messages come before the workspace's five, so workspace message 5 is archive message 7.
            AddOlderExport();

            var viewModel = new MainViewModel(
                () => _workspace.DatabasePath,
                new ArchiveDialogs
                {
                    ConfirmExport = _ => true,
                    ChooseExportParent = () => _workspace.ExportsFolder,
                    ChooseArchiveFolder = () => null,
                    OpenFolder = _ => true,
                },
                new ArchiveOperations(),
                new MemoriesDialogs
                {
                    ChooseImage = () => Chosen(),
                    ConfirmCreateFile = _ => true,
                    ConfirmDelete = _ => false,
                });

            viewModel.OpenWorkspaceCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(() => viewModel.Messages.Count > 0 && !viewModel.IsBusy);
            viewModel.LocalParticipant = viewModel.Participants[0];

            // The form: a collection, then a memory with an image and a link to workspace message 5.
            var memories = viewModel.Memories;
            await MemoriesViewModelTests.Run(memories.OpenCommand, memories);
            memories.CollectionName = "Holidays";
            await MemoriesViewModelTests.Run(memories.AddCollectionCommand, memories);

            memories.NewEntryCommand.Execute(null);
            var form = memories.Form!;
            form.Title = "Beach week";
            form.Source = "My own memory";
            form.Summary = "Invented days by the sea.";
            form.StartDate = new DateTime(2026, 4, 6);
            form.EndDate = new DateTime(2026, 4, 12);
            await MemoriesViewModelTests.Run(form.ChooseImageCommand, form);
            form.PickerDate = new DateTime(2026, 4, 6);
            await form.PickerLoad;
            form.SelectedPickerMessage = form.PickerMessages.Single(m => m.Message.SequenceNumber == 5);
            form.AddMessageLinkCommand.Execute(null);
            await MemoriesViewModelTests.Run(form.SaveCommand, form);
            await memories.AfterSave;

            var entryID = Assert.Single(memories.Entries).EntryID;
            memories.CloseCommand.Execute(null);

            // The existing export, from the same viewer; its self-check and comparison must pass.
            viewModel.ExportArchiveCommand.Execute(null);
            await MemoriesViewModelTests.WaitUntil(
                () => !viewModel.Archive.IsRunning && viewModel.Archive.Outcome != ArchiveOutcome.None, 60_000);

            Assert.Equal(ArchiveOutcome.Passed, viewModel.Archive.Outcome);
            viewModel.CloseSession();
            SqliteConnection.ClearAllPools();

            var folder = Assert.Single(Directory.GetDirectories(_workspace.ExportsFolder));

            // The phone.
            using var reader = ArchiveReader.Open(folder);
            var index = MemoryIndex.Build(reader);

            var collection = Assert.Single(index.Collections);
            Assert.Equal(ArchiveMemoryVocabulary.KindPersonal, collection.Kind);
            Assert.True(collection.ShownByDefault);

            var placement = Assert.Single(index.Layout(new MemoryView([collection.CollectionID])).Placements);
            Assert.Equal(entryID, placement.Key.EntryID);
            Assert.Equal(new DateTime(2026, 4, 6), placement.TimeLocal);

            // The card sits before the day's first message: the two older messages come first.
            Assert.Equal(2, placement.After);

            var card = Assert.IsType<EntryCardRow>(placement.CreateRow());
            Assert.Equal("Beach week", card.Title);
            Assert.Equal(
                DateSeparatorRow.DayText(new DateOnly(2026, 4, 6)) + " to " + DateSeparatorRow.DayText(new DateOnly(2026, 4, 12)),
                card.DateText);
            Assert.Equal("Invented days by the sea.", card.Summary);
            Assert.True(card.HasImage);
            Assert.Equal(MemoriesViewModelTests.Png, File.ReadAllBytes(card.ImagePath!));
            Assert.StartsWith(folder, card.ImagePath!, StringComparison.OrdinalIgnoreCase);

            var details = index.Details(placement.Key)!;
            var link = Assert.Single(details.Links);
            Assert.Equal(7, link.SequenceNumber);
            Assert.Equal("A message on " + DateSeparatorRow.DayText(new DateOnly(2026, 4, 6)) + ", 09:04", link.Text);
        }

        private string Chosen()
        {
            var folder = Path.Combine(_workspace.Root, "chosen");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "beach.png");
            File.WriteAllBytes(path, MemoriesViewModelTests.Png);

            return path;
        }

        /// <summary>A shown older export of two invented messages, a year before the workspace's.</summary>
        private void AddOlderExport()
        {
            var folder = Path.Combine(_workspace.Root, "older-export");
            Directory.CreateDirectory(folder);
            File.WriteAllText(
                Path.Combine(folder, "WhatsApp Chat with Bo.txt"),
                "01/03/2025, 10:00 - Ada Example: invented older line\n01/03/2025, 10:05 - Bo Example: invented older reply\n",
                new UTF8Encoding(false));

            var path = OlderExportsDatabase.PathBeside(_workspace.DatabasePath);
            OlderExportsDatabase.Create(path, new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc));

            new OlderExportImporter().Import(path, new OlderExportImportRequest
            {
                Export = OlderExportSource.Load(folder),
                Roles = new Dictionary<string, OlderExportRole>
                {
                    ["Ada Example"] = OlderExportRole.Local,
                    ["Bo Example"] = OlderExportRole.Other,
                },
                Position = 1,
                Shown = true,
                ImportedDateTimeUtc = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc),
            });

            SqliteConnection.ClearAllPools();
        }
    }
}
