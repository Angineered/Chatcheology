using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Chatcheology.Data.Memories.Personal;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Desktop.ViewModels
{
    /// <summary>The dialogs the Memories area needs, supplied by the window: a view-model opens no window.</summary>
    public sealed class MemoriesDialogs
    {
        /// <summary>Declines everything. For a view-model built without a window.</summary>
        public static MemoriesDialogs None { get; } = new()
        {
            ChooseImage = () => null,
            ConfirmCreateFile = _ => false,
            ConfirmDelete = _ => false,
        };

        /// <summary>Asks for an image file to copy in, or null. The file is only read.</summary>
        public required Func<string?> ChooseImage { get; init; }

        /// <summary>Asks before the memories file is created beside the workspace. True to create it.</summary>
        public required Func<string, bool> ConfirmCreateFile { get; init; }

        /// <summary>Asks before a memory is deleted. True to delete it.</summary>
        public required Func<string, bool> ConfirmDelete { get; init; }
    }

    /// <summary>What the Memories area works on: the open workspace and its selected conversation.</summary>
    internal sealed record MemoriesContext(string WorkspacePath, long ConversationID);

    /// <summary>The memories file and the conversation's messages, for one context.</summary>
    internal sealed record MemoriesBackend(PersonalMemoryStore Store, IMessageLinkTargets Messages)
    {
        /// <summary>The real backend: the memories file beside the workspace, and the workspace read-only.</summary>
        internal static MemoriesBackend For(MemoriesContext context)
        {
            var messages = new WorkspaceMessageLinkTargets(context.WorkspacePath, context.ConversationID);

            return new MemoriesBackend(PersonalMemoryStore.Beside(context.WorkspacePath, messages), messages);
        }
    }

    /// <summary>One personal collection in the list, with its entries.</summary>
    public sealed class PersonalCollectionViewModel(PersonalCollectionSummary summary)
    {
        /// <summary>The collection.</summary>
        public long CollectionID { get; } = summary.CollectionID;

        /// <summary>Its name.</summary>
        public string Name { get; } = summary.Name;

        /// <summary>Its memories, date order.</summary>
        public IReadOnlyList<PersonalEntrySummary> Entries { get; } = summary.Entries;

        /// <summary>"Holidays (3)".</summary>
        public string DisplayText => Name + " (" + Entries.Count.ToString(CultureInfo.CurrentCulture) + ")";

        /// <inheritdoc/>
        public override string ToString() => Name;
    }

    /// <summary>One imported collection: read-only, by name and count.</summary>
    public sealed record ImportedCollectionRow(string Name, int EntryCount)
    {
        /// <summary>"Shared gaming: 523 entries (imported, read-only)".</summary>
        public string Text =>
            Name + ": " + (EntryCount == 1 ? "1 entry" : EntryCount.ToString(CultureInfo.CurrentCulture) + " entries") +
            " (imported, read-only)";
    }

    /// <summary>
    /// The Memories area (CH-42 step 4): personal collections and their memories, the form that adds and
    /// edits them, and the imported collections listed read-only.
    /// </summary>
    /// <remarks>
    /// <para><strong>Only the memories file is written.</strong> It sits beside the open workspace; the
    /// workspace itself is only read (message links). The file is created on the first save, and only
    /// after the operator confirmed.</para>
    /// <para><strong>Not while an archive is exported or checked, or a decision is in flight.</strong> The
    /// export reads the memories file, so no save changes it underneath.</para>
    /// <para><strong>One conversation.</strong> Message links name the selected conversation's messages; the
    /// area is closed when the workspace or the conversation changes.</para>
    /// </remarks>
    public sealed class MemoriesViewModel : ObservableObject
    {
        private readonly MemoriesDialogs _dialogs;
        private readonly Func<MemoriesContext?> _context;
        private readonly Func<bool> _blocked;
        private readonly Func<MemoriesContext, MemoriesBackend> _backendFor;
        private readonly Func<DateTime> _utcNow;

        private MemoriesBackend? _backend;
        private bool _isOpen;
        private bool _isBusy;
        private bool _fileExists;
        private string _statusText = string.Empty;
        private string _collectionName = string.Empty;
        private string _collectionNameError = string.Empty;
        private PersonalCollectionViewModel? _selectedCollection;
        private PersonalEntrySummary? _selectedEntry;
        private MemoryEntryFormViewModel? _form;

        /// <param name="dialogs">The window's dialogs.</param>
        /// <param name="context">The open workspace and conversation, or null.</param>
        /// <param name="blocked">Whether an export, a check or a decision is running (no write then).</param>
        /// <param name="backendFor">The memories file and messages for a context; a seam for tests.</param>
        /// <param name="utcNow">The clock for a new memories file.</param>
        internal MemoriesViewModel(
            MemoriesDialogs dialogs,
            Func<MemoriesContext?> context,
            Func<bool> blocked,
            Func<MemoriesContext, MemoriesBackend>? backendFor = null,
            Func<DateTime>? utcNow = null)
        {
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _blocked = blocked ?? throw new ArgumentNullException(nameof(blocked));
            _backendFor = backendFor ?? MemoriesBackend.For;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);

            OpenCommand = new AsyncRelayCommand(OpenAsync, () => _context() is not null);
            CloseCommand = new RelayCommand(Close, () => !IsBusy && Form?.IsSaving != true);
            AddCollectionCommand = new AsyncRelayCommand(AddCollectionAsync, CanWrite);
            RenameCollectionCommand = new AsyncRelayCommand(RenameCollectionAsync, () => CanWrite() && SelectedCollection is not null);
            NewEntryCommand = new RelayCommand(NewEntry, () => CanWrite() && SelectedCollection is not null);
            EditEntryCommand = new AsyncRelayCommand(EditEntryAsync, () => CanWrite() && SelectedEntry?.IsEditable == true);
            DeleteEntryCommand = new AsyncRelayCommand(DeleteEntryAsync, () => CanWrite() && SelectedEntry is not null);
        }

        // -----------------------------------------------------------------------------------
        // Bound state.
        // -----------------------------------------------------------------------------------

        /// <summary>Opens the area and reads the memories file.</summary>
        public AsyncRelayCommand OpenCommand { get; }

        /// <summary>Puts the area away. An unsaved form is dropped.</summary>
        public RelayCommand CloseCommand { get; }

        /// <summary>Adds a personal collection named <see cref="CollectionName"/>.</summary>
        public AsyncRelayCommand AddCollectionCommand { get; }

        /// <summary>Renames the selected collection to <see cref="CollectionName"/>.</summary>
        public AsyncRelayCommand RenameCollectionCommand { get; }

        /// <summary>Opens an empty form in the selected collection.</summary>
        public RelayCommand NewEntryCommand { get; }

        /// <summary>Opens the selected memory in the form.</summary>
        public AsyncRelayCommand EditEntryCommand { get; }

        /// <summary>Deletes the selected memory (after confirmation) and its links.</summary>
        public AsyncRelayCommand DeleteEntryCommand { get; }

        /// <summary>The personal collections.</summary>
        public ObservableCollection<PersonalCollectionViewModel> Collections { get; } = [];

        /// <summary>The imported collections, read-only.</summary>
        public ObservableCollection<ImportedCollectionRow> ImportedCollections { get; } = [];

        /// <summary>Whether the area is showing.</summary>
        public bool IsOpen
        {
            get => _isOpen;
            private set => SetProperty(ref _isOpen, value);
        }

        /// <summary>Whether a read or a write of the list is running.</summary>
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(IsWriting));
                    RaiseCommandStates();
                }
            }
        }

        /// <summary>
        /// Whether the area is reading or writing the memories file (the list, or a save from the form).
        /// The shell holds archive export and checking off meanwhile.
        /// </summary>
        public bool IsWriting => IsBusy || Form?.IsSaving == true;

        /// <summary>Whether the memories file exists yet.</summary>
        public bool FileExists
        {
            get => _fileExists;
            private set => SetProperty(ref _fileExists, value);
        }

        /// <summary>What the last action did. Fixed phrases; never a path.</summary>
        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        /// <summary>The name typed for a new or renamed collection.</summary>
        public string CollectionName
        {
            get => _collectionName;
            set
            {
                if (SetProperty(ref _collectionName, value ?? string.Empty))
                {
                    CollectionNameError = string.Empty;
                }
            }
        }

        /// <summary>The collection name's problem, or empty.</summary>
        public string CollectionNameError
        {
            get => _collectionNameError;
            private set => SetProperty(ref _collectionNameError, value);
        }

        /// <summary>The collection whose memories are listed.</summary>
        public PersonalCollectionViewModel? SelectedCollection
        {
            get => _selectedCollection;
            set
            {
                if (SetProperty(ref _selectedCollection, value))
                {
                    CollectionName = value?.Name ?? string.Empty;
                    OnPropertyChanged(nameof(Entries));
                    SelectedEntry = null;
                    RaiseCommandStates();
                }
            }
        }

        /// <summary>The selected collection's memories.</summary>
        public IReadOnlyList<PersonalEntrySummary> Entries => SelectedCollection?.Entries ?? [];

        /// <summary>The selected memory.</summary>
        public PersonalEntrySummary? SelectedEntry
        {
            get => _selectedEntry;
            set
            {
                if (SetProperty(ref _selectedEntry, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        /// <summary>The open form, or null when the list is showing.</summary>
        public MemoryEntryFormViewModel? Form
        {
            get => _form;
            private set
            {
                if (_form is not null)
                {
                    _form.PropertyChanged -= OnFormPropertyChanged;
                }

                if (value is not null)
                {
                    value.PropertyChanged += OnFormPropertyChanged;
                }

                if (SetProperty(ref _form, value))
                {
                    OnPropertyChanged(nameof(IsWriting));
                    OnPropertyChanged(nameof(IsFormOpen));
                    OnPropertyChanged(nameof(IsListShown));
                    RaiseCommandStates();
                }
            }
        }

        private void OnFormPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MemoryEntryFormViewModel.IsSaving))
            {
                OnPropertyChanged(nameof(IsWriting));
                CloseCommand.RaiseCanExecuteChanged();
            }
        }

        /// <summary>Whether the form is showing.</summary>
        public bool IsFormOpen => Form is not null;

        /// <summary>Whether the list is showing.</summary>
        public bool IsListShown => Form is null;

        /// <summary>Asks every command again (the shell calls it when an export or decision starts or ends).</summary>
        public void RaiseCommandStates()
        {
            OpenCommand.RaiseCanExecuteChanged();
            CloseCommand.RaiseCanExecuteChanged();
            AddCollectionCommand.RaiseCanExecuteChanged();
            RenameCollectionCommand.RaiseCanExecuteChanged();
            NewEntryCommand.RaiseCanExecuteChanged();
            EditEntryCommand.RaiseCanExecuteChanged();
            DeleteEntryCommand.RaiseCanExecuteChanged();
            Form?.RaiseCommandStates();
        }

        /// <summary>Closes the area and forgets the workspace (the workspace or conversation changed).</summary>
        public void Close()
        {
            Form = null;
            IsOpen = false;
            _backend = null;
            Collections.Clear();
            ImportedCollections.Clear();
            _selectedCollection = null;
            OnPropertyChanged(nameof(SelectedCollection));
            OnPropertyChanged(nameof(Entries));
            SelectedEntry = null;
            CollectionName = string.Empty;
            FileExists = false;
            StatusText = string.Empty;
            RaiseCommandStates();
        }

        // -----------------------------------------------------------------------------------
        // Actions.
        // -----------------------------------------------------------------------------------

        private bool CanWrite() => IsOpen && _backend is not null && !IsBusy && Form is null && !_blocked();

        private async Task OpenAsync()
        {
            if (_context() is not { } context)
            {
                return;
            }

            _backend = _backendFor(context);
            IsOpen = true;

            await RefreshAsync(null, null).ConfigureAwait(true);
        }

        /// <summary>Reads the file again, keeping (or moving to) the given collection and memory.</summary>
        internal async Task RefreshAsync(long? collectionID, long? entryID)
        {
            if (_backend is not { } backend)
            {
                return;
            }

            collectionID ??= SelectedCollection?.CollectionID;
            entryID ??= SelectedEntry?.EntryID;

            IsBusy = true;

            try
            {
                var overview = await Task.Run(backend.Store.ReadOverview).ConfigureAwait(true);

                FileExists = overview.FileExists;
                Collections.Clear();

                foreach (var collection in overview.Personal)
                {
                    Collections.Add(new PersonalCollectionViewModel(collection));
                }

                ImportedCollections.Clear();

                foreach (var imported in overview.Imported)
                {
                    ImportedCollections.Add(new ImportedCollectionRow(imported.Name, imported.EntryCount));
                }

                _selectedCollection = null;
                SelectedCollection = Collections.FirstOrDefault(c => c.CollectionID == collectionID) ?? Collections.FirstOrDefault();
                SelectedEntry = Entries.FirstOrDefault(e => e.EntryID == entryID);

                if (!overview.FileExists)
                {
                    StatusText = "No memories file beside this workspace yet. Adding a collection creates it.";
                }
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                StatusText = Describe(exception, "The memories file could not be read.");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task AddCollectionAsync()
        {
            if (_backend is not { } backend)
            {
                return;
            }

            if (PersonalMemoryRules.CollectionNameError(CollectionName) is { } error)
            {
                CollectionNameError = error;
                return;
            }

            var create = false;

            if (!backend.Store.FileExists)
            {
                if (!_dialogs.ConfirmCreateFile(
                        "There is no memories file beside this workspace yet. Create it now, with this collection? " +
                        "The workspace itself is not changed."))
                {
                    StatusText = "Nothing was created.";
                    return;
                }

                create = true;
            }

            var name = CollectionName;
            var now = _utcNow();

            await WriteAsync(
                () => backend.Store.CreateCollection(name, create, now),
                id => ("Collection added.", id, null)).ConfigureAwait(true);
        }

        private async Task RenameCollectionAsync()
        {
            if (_backend is not { } backend || SelectedCollection is not { } collection)
            {
                return;
            }

            if (PersonalMemoryRules.CollectionNameError(CollectionName) is { } error)
            {
                CollectionNameError = error;
                return;
            }

            var name = CollectionName;

            await WriteAsync(
                () =>
                {
                    backend.Store.RenameCollection(collection.CollectionID, name);
                    return collection.CollectionID;
                },
                id => ("Collection renamed.", id, null)).ConfigureAwait(true);
        }

        private void NewEntry()
        {
            if (_backend is not { } backend || SelectedCollection is not { } collection)
            {
                return;
            }

            StatusText = string.Empty;
            Form = new MemoryEntryFormViewModel(backend, _dialogs, Collections.ToList(), collection.CollectionID, null, null, _blocked, OnFormClosed);
        }

        private async Task EditEntryAsync()
        {
            if (_backend is not { } backend || SelectedEntry is not { } entry)
            {
                return;
            }

            IsBusy = true;

            try
            {
                var (details, messages) = await Task.Run(() =>
                {
                    var read = backend.Store.ReadEntry(entry.EntryID);
                    var described = read.Links
                        .Where(l => l.Target.MessageSequenceNumber is not null)
                        .Select(l => l.Target.MessageSequenceNumber!.Value)
                        .Distinct()
                        .ToDictionary(s => s, backend.Messages.Read);

                    return (read, described);
                }).ConfigureAwait(true);

                StatusText = string.Empty;
                Form = new MemoryEntryFormViewModel(
                    backend, _dialogs, Collections.ToList(), details.CollectionID, details, messages, _blocked, OnFormClosed);
            }
            catch (PersonalMemoryValidationException exception)
            {
                StatusText = string.Join(" ", exception.Errors.Values);
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                StatusText = Describe(exception, "The memory could not be read.");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task DeleteEntryAsync()
        {
            if (_backend is not { } backend || SelectedEntry is not { } entry)
            {
                return;
            }

            if (!_dialogs.ConfirmDelete($"Delete the memory \"{entry.Title}\" and its links? This cannot be undone."))
            {
                StatusText = "Nothing was deleted.";
                return;
            }

            await WriteAsync(
                () => backend.Store.DeleteEntry(entry.EntryID),
                links => (links == 0
                    ? "Memory deleted."
                    : "Memory deleted, with " + (links == 1 ? "1 link." : links.ToString(CultureInfo.CurrentCulture) + " links."),
                    SelectedCollection?.CollectionID, null)).ConfigureAwait(true);
        }

        private async Task WriteAsync<T>(Func<T> write, Func<T, (string Status, long? CollectionID, long? EntryID)> done)
        {
            IsBusy = true;

            try
            {
                var result = await Task.Run(write).ConfigureAwait(true);
                var (status, collectionID, entryID) = done(result);

                IsBusy = false;
                await RefreshAsync(collectionID, entryID).ConfigureAwait(true);
                StatusText = status;
            }
            catch (PersonalMemoryValidationException exception)
            {
                if (exception.Errors.TryGetValue(PersonalFields.CollectionName, out var nameError))
                {
                    CollectionNameError = nameError;
                }

                StatusText = string.Join(" ", exception.Errors.Values) + " Nothing was written.";
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                StatusText = Describe(exception, "The change could not be saved. Nothing was written.");
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>The list's re-read after the last save from the form (for tests to wait on).</summary>
        internal Task AfterSave { get; private set; } = Task.CompletedTask;

        private void OnFormClosed(long? savedEntryID, long? collectionID)
        {
            Form = null;

            if (savedEntryID is { } id)
            {
                AfterSave = RefreshAfterSaveAsync(collectionID, id);
            }
        }

        private async Task RefreshAfterSaveAsync(long? collectionID, long entryID)
        {
            await RefreshAsync(collectionID, entryID).ConfigureAwait(true);
            StatusText = "Memory saved.";
        }

        internal static bool IsExpected(Exception exception) =>
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException
                or FormatException or ArgumentException;

        /// <summary>
        /// A failure in words: the Data layer's own fixed phrases for a refusal; otherwise a fixed phrase with
        /// the exception's type only, since its message may carry a path.
        /// </summary>
        internal static string Describe(Exception exception, string fallback) => exception switch
        {
            FileNotFoundException => "The memories file is not there any more. Nothing was written.",
            SqliteException { SqliteErrorCode: 5 or 6 } => "The memories file is busy: another program holds a lock on it. Nothing was written.",
            InvalidOperationException when exception.Message.Contains("memories file", StringComparison.Ordinal) => exception.Message,
            _ => fallback + " (" + exception.GetType().Name + ")",
        };
    }
}
