using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using Chatcheology.Data.Memories.Personal;
using Chatcheology.Desktop.Services;

namespace Chatcheology.Desktop.ViewModels
{
    /// <summary>One link in the form: what it points to, in words, and how to drop it.</summary>
    public sealed class MemoryLinkViewModel
    {
        internal MemoryLinkViewModel(PersonalLinkTarget target, string text, Action<MemoryLinkViewModel> remove)
        {
            Target = target;
            Text = text;
            RemoveCommand = new RelayCommand(() => remove(this));
        }

        /// <summary>The message or entry.</summary>
        public PersonalLinkTarget Target { get; }

        /// <summary>"Message 12: 5 April 2019, 09:00, Ada: …" or "Beach week, 2 January 2018 (Holidays)".</summary>
        public string Text { get; }

        /// <summary>Drops the link from the form (nothing is written until Save).</summary>
        public RelayCommand RemoveCommand { get; }
    }

    /// <summary>
    /// One photo in the form (CH-46): the first is the cover (shown on the card); the others are the
    /// extra photos, in order, each with an optional caption.
    /// </summary>
    public sealed class MemoryFormPhotoViewModel : ObservableObject
    {
        private readonly MemoryEntryFormViewModel _form;
        private string _caption;
        private bool _isCover;

        internal MemoryFormPhotoViewModel(
            MemoryEntryFormViewModel form, PersonalImageChoice choice, string? sha256, ImageSource? preview, string text, string? caption)
        {
            _form = form;
            Choice = choice;
            Sha256 = sha256;
            Preview = preview;
            Text = text;
            _caption = caption ?? string.Empty;

            MoveUpCommand = new RelayCommand(() => _form.Move(this, -1), () => _form.CanMove(this, -1));
            MoveDownCommand = new RelayCommand(() => _form.Move(this, +1), () => _form.CanMove(this, +1));
            MakeCoverCommand = new RelayCommand(() => _form.MakeCover(this), () => !IsCover && _form.CanEditPhotos);
            RemoveCommand = new RelayCommand(() => _form.RemovePhoto(this), () => _form.CanEditPhotos);
        }

        /// <summary>What the save carries: the stored photo by hash, or the chosen file.</summary>
        internal PersonalImageChoice Choice { get; }

        /// <summary>The photo's hash, when known (a stored photo, or a chosen file that was read).</summary>
        internal string? Sha256 { get; }

        /// <summary>The preview, or <see langword="null"/>.</summary>
        public ImageSource? Preview { get; }

        /// <summary>"Current image", "New image: 120 KB" (with the file name for an added one), or why there is no preview.</summary>
        public string Text { get; }

        /// <summary>The caption (kept for an extra photo; the cover has none).</summary>
        public string Caption
        {
            get => _caption;
            set => SetProperty(ref _caption, value ?? string.Empty);
        }

        /// <summary>Whether this is the cover (the first photo).</summary>
        public bool IsCover
        {
            get => _isCover;
            internal set
            {
                if (SetProperty(ref _isCover, value))
                {
                    OnPropertyChanged(nameof(CanEditCaption));
                    OnPropertyChanged(nameof(RoleText));
                }
            }
        }

        /// <summary>The cover carries no caption; every other photo can.</summary>
        public bool CanEditCaption => !IsCover;

        /// <summary>"Cover" or "Photo".</summary>
        public string RoleText => IsCover ? "Cover (shown on the card; no caption)" : "Photo";

        /// <summary>One place earlier.</summary>
        public RelayCommand MoveUpCommand { get; }

        /// <summary>One place later.</summary>
        public RelayCommand MoveDownCommand { get; }

        /// <summary>Moves it first: it becomes the cover.</summary>
        public RelayCommand MakeCoverCommand { get; }

        /// <summary>Drops it from the memory (the stored file stays).</summary>
        public RelayCommand RemoveCommand { get; }

        internal void RaiseCommandStates()
        {
            MoveUpCommand.RaiseCanExecuteChanged();
            MoveDownCommand.RaiseCanExecuteChanged();
            MakeCoverCommand.RaiseCanExecuteChanged();
            RemoveCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>A message in the picker.</summary>
    public sealed record MessagePickRow(MessageLinkCandidate Message)
    {
        /// <summary>"09:00 Ada: preview".</summary>
        public string Text => MemoryEntryFormViewModel.MessageLine(Message);
    }

    /// <summary>An entry in the picker.</summary>
    public sealed record EntryPickRow(EntryLinkCandidate Entry)
    {
        /// <summary>"Beach week, 2 January 2018 (Holidays)".</summary>
        public string Text => Entry.Title + ", " + Entry.DateText + " (" + Entry.CollectionName + ")";
    }

    /// <summary>
    /// The form for one personal memory: title, dates and precision, description, source, its photos, and
    /// links to messages and entries (CH-42 step 4; several photos CH-46).
    /// </summary>
    /// <remarks>
    /// Nothing is written until Save, and Save is one transaction in the memories file. Each field's
    /// problem is shown next to it. Chosen image files are only read (for the previews and on save). The
    /// first photo is the cover; the others follow in order, each with an optional caption. Adding several
    /// files at once checks each (type, 20 MB cap, not already in the memory); one that fails is named in
    /// the photos' message and the others are still added.
    /// </remarks>
    public sealed class MemoryEntryFormViewModel : ObservableObject
    {
        private readonly MemoriesBackend _backend;
        private readonly MemoriesDialogs _dialogs;
        private readonly Func<bool> _blocked;
        private readonly Action<long?, long?> _closed;

        /// <summary>
        /// Told when a save starts (true) and when it has ended (false), whatever became of the form
        /// meanwhile: the area counts saves still writing the memories file, not open forms.
        /// </summary>
        private readonly Action<bool> _saving;
        private readonly long? _entryID;

        private PersonalCollectionViewModel? _collection;
        private string _title = string.Empty;
        private string _source = string.Empty;
        private string _summary = string.Empty;
        private DateTime? _startDate;
        private DateTime? _endDate;
        private PersonalDatePrecision _precision = PersonalDatePrecision.Day;
        private DateTime? _pickerDate;
        private MessagePickRow? _selectedMessage;
        private EntryPickRow? _selectedEntry;
        private string _pickerText = "Pick a day to see its messages and memories.";
        private bool _isSaving;
        private bool _isChoosingImage;

        private string _collectionError = string.Empty;
        private string _titleError = string.Empty;
        private string _startError = string.Empty;
        private string _endError = string.Empty;
        private string _sourceError = string.Empty;
        private string _summaryError = string.Empty;
        private string _imageError = string.Empty;
        private string _linksError = string.Empty;
        private string _formError = string.Empty;

        internal MemoryEntryFormViewModel(
            MemoriesBackend backend,
            MemoriesDialogs dialogs,
            IReadOnlyList<PersonalCollectionViewModel> collections,
            long collectionID,
            PersonalEntryDetails? existing,
            IReadOnlyDictionary<int, MessageLinkCandidate?>? messages,
            Func<bool> blocked,
            Action<long?, long?> closed,
            Action<bool> saving)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _blocked = blocked ?? throw new ArgumentNullException(nameof(blocked));
            _closed = closed ?? throw new ArgumentNullException(nameof(closed));
            _saving = saving ?? throw new ArgumentNullException(nameof(saving));

            Collections = collections;
            _collection = collections.FirstOrDefault(c => c.CollectionID == collectionID);

            SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsSaving && !_isChoosingImage && !_blocked());
            CancelCommand = new RelayCommand(() => _closed(null, null), () => !IsSaving);
            ChooseImageCommand = new AsyncRelayCommand(ChooseImageAsync, () => !IsSaving);
            RemoveImageCommand = new RelayCommand(RemoveImage, () => !IsSaving && HasImage);
            Photos.CollectionChanged += (_, _) => OnPhotosChanged();
            AddMessageLinkCommand = new RelayCommand(AddMessageLink, () => !IsSaving && SelectedPickerMessage is not null);
            AddEntryLinkCommand = new RelayCommand(AddEntryLink, () => !IsSaving && SelectedPickerEntry is not null);

            if (existing is null)
            {
                return;
            }

            _entryID = existing.EntryID;
            _title = existing.Title;
            _source = existing.Source;
            _summary = existing.Summary ?? string.Empty;
            _precision = existing.Precision;
            _startDate = existing.Start.ToDateTime(TimeOnly.MinValue);
            _endDate = existing.End?.ToDateTime(TimeOnly.MinValue);

            if (existing.ImageSha256 is { } sha)
            {
                Photos.Add(new MemoryFormPhotoViewModel(
                    this,
                    PersonalImageChoice.Existing(sha),
                    sha,
                    existing.ImagePath is { } path ? MediaPresentation.TryLoadThumbnail(path) : null,
                    existing.ImagePath is null ? "The memory's image file is not in the memories folder." : "Current image",
                    null));
            }

            foreach (var photo in existing.ExtraPhotos)
            {
                Photos.Add(new MemoryFormPhotoViewModel(
                    this,
                    PersonalImageChoice.Existing(photo.Sha256),
                    photo.Sha256,
                    photo.Path is { } file ? MediaPresentation.TryLoadThumbnail(file) : null,
                    photo.Path is null ? "This photo's file is not in the memories folder." : "Current image",
                    photo.Caption));
            }

            foreach (var link in existing.Links)
            {
                Links.Add(new MemoryLinkViewModel(
                    link.Target,
                    link.Target.MessageSequenceNumber is { } sequence
                        ? MessageLinkText(sequence, messages?.GetValueOrDefault(sequence))
                        : link.EntryDescription ?? "A memory",
                    RemoveLink));
            }
        }

        // -----------------------------------------------------------------------------------
        // Commands.
        // -----------------------------------------------------------------------------------

        /// <summary>Checks and saves (one transaction), then closes the form.</summary>
        public AsyncRelayCommand SaveCommand { get; }

        /// <summary>Closes the form; nothing is written.</summary>
        public RelayCommand CancelCommand { get; }

        /// <summary>
        /// Asks for one or more image files and adds each that passes the checks as a photo (the files are
        /// only read). The first photo of a memory is its cover.
        /// </summary>
        public AsyncRelayCommand ChooseImageCommand { get; }

        /// <summary>Drops every photo from this memory (stored files stay).</summary>
        public RelayCommand RemoveImageCommand { get; }

        /// <summary>Adds a link to the picked message.</summary>
        public RelayCommand AddMessageLinkCommand { get; }

        /// <summary>Adds a link to the picked memory.</summary>
        public RelayCommand AddEntryLinkCommand { get; }

        // -----------------------------------------------------------------------------------
        // Fields.
        // -----------------------------------------------------------------------------------

        /// <summary>True for a new memory, false when editing.</summary>
        public bool IsNew => _entryID is null;

        /// <summary>"New memory" or "Edit memory".</summary>
        public string Heading => IsNew ? "New memory" : "Edit memory";

        /// <summary>The personal collections a memory can be in.</summary>
        public IReadOnlyList<PersonalCollectionViewModel> Collections { get; }

        /// <summary>The memory's collection.</summary>
        public PersonalCollectionViewModel? SelectedCollection
        {
            get => _collection;
            set
            {
                if (SetProperty(ref _collection, value))
                {
                    CollectionError = string.Empty;
                }
            }
        }

        /// <summary>Required.</summary>
        public string Title
        {
            get => _title;
            set
            {
                if (SetProperty(ref _title, value ?? string.Empty))
                {
                    TitleError = string.Empty;
                }
            }
        }

        /// <summary>Required: where the memory comes from.</summary>
        public string Source
        {
            get => _source;
            set
            {
                if (SetProperty(ref _source, value ?? string.Empty))
                {
                    SourceError = string.Empty;
                }
            }
        }

        /// <summary>Optional description.</summary>
        public string Summary
        {
            get => _summary;
            set
            {
                if (SetProperty(ref _summary, value ?? string.Empty))
                {
                    SummaryError = string.Empty;
                }
            }
        }

        /// <summary>Required start date (with Month precision only its month counts).</summary>
        public DateTime? StartDate
        {
            get => _startDate;
            set
            {
                if (SetProperty(ref _startDate, value?.Date))
                {
                    StartError = string.Empty;
                    EndError = string.Empty;
                }
            }
        }

        /// <summary>Optional end date, not before the start.</summary>
        public DateTime? EndDate
        {
            get => _endDate;
            set
            {
                if (SetProperty(ref _endDate, value?.Date))
                {
                    EndError = string.Empty;
                }
            }
        }

        /// <summary>Day or Month.</summary>
        public IReadOnlyList<PersonalDatePrecision> Precisions { get; } = [PersonalDatePrecision.Day, PersonalDatePrecision.Month];

        /// <summary>The chosen precision.</summary>
        public PersonalDatePrecision Precision
        {
            get => _precision;
            set
            {
                if (SetProperty(ref _precision, value))
                {
                    EndError = string.Empty;
                }
            }
        }

        /// <summary>
        /// The memory's photos in order: the first is the cover, the rest the extra photos (CH-46).
        /// </summary>
        public ObservableCollection<MemoryFormPhotoViewModel> Photos { get; } = [];

        /// <summary>The cover's preview, or null.</summary>
        public ImageSource? ImagePreview => Photos.FirstOrDefault()?.Preview;

        /// <summary>A line about the cover: "Current image", "New image: 120 KB", or empty.</summary>
        public string ImageText => Photos.FirstOrDefault()?.Text ?? string.Empty;

        /// <summary>Whether the memory will carry an image (a cover).</summary>
        public bool HasImage => Photos.Count > 0;

        /// <summary>The cover the save will carry.</summary>
        internal PersonalImageChoice Image => Photos.FirstOrDefault()?.Choice ?? PersonalImageChoice.None;

        /// <summary>Whether photos can be moved, made the cover or removed now.</summary>
        internal bool CanEditPhotos => !IsSaving && !_isChoosingImage;

        /// <summary>The memory's links.</summary>
        public ObservableCollection<MemoryLinkViewModel> Links { get; } = [];

        // -----------------------------------------------------------------------------------
        // Pickers.
        // -----------------------------------------------------------------------------------

        /// <summary>The day whose messages and memories the pickers list.</summary>
        public DateTime? PickerDate
        {
            get => _pickerDate;
            set
            {
                if (SetProperty(ref _pickerDate, value?.Date))
                {
                    PickerLoad = LoadPickerAsync(_pickerDate);
                }
            }
        }

        /// <summary>The picked day's messages of the open conversation.</summary>
        public ObservableCollection<MessagePickRow> PickerMessages { get; } = [];

        /// <summary>The memories (any collection) starting on the picked day.</summary>
        public ObservableCollection<EntryPickRow> PickerEntries { get; } = [];

        /// <summary>The message to link.</summary>
        public MessagePickRow? SelectedPickerMessage
        {
            get => _selectedMessage;
            set
            {
                if (SetProperty(ref _selectedMessage, value))
                {
                    AddMessageLinkCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>The memory to link.</summary>
        public EntryPickRow? SelectedPickerEntry
        {
            get => _selectedEntry;
            set
            {
                if (SetProperty(ref _selectedEntry, value))
                {
                    AddEntryLinkCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>What the pickers found: counts, or why nothing is listed.</summary>
        public string PickerText
        {
            get => _pickerText;
            private set => SetProperty(ref _pickerText, value);
        }

        /// <summary>The pickers' last load (for tests to wait on).</summary>
        internal Task PickerLoad { get; private set; } = Task.CompletedTask;

        // -----------------------------------------------------------------------------------
        // Validation messages, next to their fields. Empty = none.
        // -----------------------------------------------------------------------------------

        /// <summary>The collection's problem.</summary>
        public string CollectionError { get => _collectionError; private set => SetProperty(ref _collectionError, value); }

        /// <summary>The title's problem.</summary>
        public string TitleError { get => _titleError; private set => SetProperty(ref _titleError, value); }

        /// <summary>The start's problem.</summary>
        public string StartError { get => _startError; private set => SetProperty(ref _startError, value); }

        /// <summary>The end's problem.</summary>
        public string EndError { get => _endError; private set => SetProperty(ref _endError, value); }

        /// <summary>The source's problem.</summary>
        public string SourceError { get => _sourceError; private set => SetProperty(ref _sourceError, value); }

        /// <summary>The description's problem.</summary>
        public string SummaryError { get => _summaryError; private set => SetProperty(ref _summaryError, value); }

        /// <summary>The image's problem.</summary>
        public string ImageError { get => _imageError; private set => SetProperty(ref _imageError, value); }

        /// <summary>The links' problem.</summary>
        public string LinksError { get => _linksError; private set => SetProperty(ref _linksError, value); }

        /// <summary>A problem with the memory as a whole, or with saving it.</summary>
        public string FormError { get => _formError; private set => SetProperty(ref _formError, value); }

        /// <summary>Whether a save is running.</summary>
        public bool IsSaving
        {
            get => _isSaving;
            private set
            {
                if (SetProperty(ref _isSaving, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        /// <summary>Asks every command again.</summary>
        public void RaiseCommandStates()
        {
            SaveCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            ChooseImageCommand.RaiseCanExecuteChanged();
            RemoveImageCommand.RaiseCanExecuteChanged();
            AddMessageLinkCommand.RaiseCanExecuteChanged();
            AddEntryLinkCommand.RaiseCanExecuteChanged();

            foreach (var photo in Photos)
            {
                photo.RaiseCommandStates();
            }
        }

        // -----------------------------------------------------------------------------------
        // Actions.
        // -----------------------------------------------------------------------------------

        /// <summary>The draft as the fields stand.</summary>
        internal PersonalEntryDraft Draft() => new()
        {
            CollectionID = SelectedCollection?.CollectionID ?? 0,
            Title = Title,
            Start = StartDate is { } start ? DateOnly.FromDateTime(start) : null,
            End = EndDate is { } end ? DateOnly.FromDateTime(end) : null,
            Precision = Precision,
            Summary = Summary,
            Source = Source,
            Image = Image,
            ExtraPhotos = Photos.Skip(1).Select(p => new PersonalPhotoDraft(p.Choice, p.Caption)).ToList(),
            Links = Links.Select(l => l.Target).ToList(),
        };

        private async Task SaveAsync()
        {
            ClearErrors();

            var draft = Draft();
            var errors = new Dictionary<string, string>(PersonalMemoryRules.Validate(draft));

            if (SelectedCollection is null)
            {
                errors[PersonalFields.Collection] = "Choose one of your own collections.";
            }

            if (errors.Count > 0)
            {
                ShowErrors(errors);
                return;
            }

            IsSaving = true;
            _saving(true);

            long? saved = null;

            try
            {
                var store = _backend.Store;
                saved = _entryID is { } existing
                    ? await Task.Run(() =>
                    {
                        store.UpdateEntry(existing, draft);
                        return existing;
                    }).ConfigureAwait(true)
                    : await Task.Run(() => store.AddEntry(draft)).ConfigureAwait(true);
            }
            catch (PersonalMemoryValidationException exception)
            {
                ShowErrors(exception.Errors);
            }
            catch (Exception exception) when (MemoriesViewModel.IsExpected(exception))
            {
                FormError = MemoriesViewModel.Describe(exception, "The memory could not be saved. Nothing was written.");
            }
            finally
            {
                // The write has ended (committed or rolled back) before anyone is told.
                IsSaving = false;
                _saving(false);
            }

            if (saved is { } id)
            {
                _closed(id, draft.CollectionID);
            }
        }

        private async Task ChooseImageAsync()
        {
            var paths = _dialogs.ChooseImageFiles();

            if (paths.Count == 0)
            {
                return;
            }

            _isChoosingImage = true;
            RaiseCommandStates();
            ImageError = string.Empty;

            try
            {
                var max = _backend.Store.MaxImageBytes;

                // Each file is read and checked on its own: one that fails is named, the others are added.
                var read = await Task.Run(() => paths
                    .Select(path =>
                    {
                        var file = PersonalImageFile.Read(path, max, out var problem);
                        return (Path: path, File: file, Error: problem, Preview: file is null ? null : MediaPresentation.TryLoadThumbnail(path));
                    })
                    .ToList()).ConfigureAwait(true);

                var problems = new List<string>();

                foreach (var (path, file, error, preview) in read)
                {
                    var name = System.IO.Path.GetFileName(path);

                    if (file is null)
                    {
                        problems.Add(name + ": " + (error ?? "The chosen file could not be used."));
                        continue;
                    }

                    if (Photos.Any(p => p.Sha256 == file.Sha256))
                    {
                        problems.Add(name + ": that photo is already in this memory.");
                        continue;
                    }

                    Photos.Add(new MemoryFormPhotoViewModel(
                        this,
                        PersonalImageChoice.FromFile(path),
                        file.Sha256,
                        preview,
                        "New image: " + Size(file.SizeBytes) + (preview is null ? " (no preview)" : string.Empty) +
                        (paths.Count > 1 ? " (" + name + ")" : string.Empty),
                        null));
                }

                ImageError = string.Join("\n", problems);
            }
            finally
            {
                _isChoosingImage = false;
                RaiseCommandStates();
            }
        }

        private void RemoveImage()
        {
            Photos.Clear();
            ImageError = string.Empty;
        }

        /// <summary>Whether <paramref name="photo"/> can move by <paramref name="offset"/> places.</summary>
        internal bool CanMove(MemoryFormPhotoViewModel photo, int offset)
        {
            var index = Photos.IndexOf(photo);

            return CanEditPhotos && index >= 0 && index + offset >= 0 && index + offset < Photos.Count;
        }

        /// <summary>Moves <paramref name="photo"/> by <paramref name="offset"/> places; moved first, it is the cover.</summary>
        internal void Move(MemoryFormPhotoViewModel photo, int offset)
        {
            if (!CanMove(photo, offset))
            {
                return;
            }

            var index = Photos.IndexOf(photo);

            // Whichever photo ends up first becomes the cover and loses its caption: asked first.
            var newCover = index + offset == 0 ? photo : index == 0 ? Photos[1] : null;

            if (newCover is not null && !AcceptAsCover(newCover))
            {
                return;
            }

            Photos.Move(index, index + offset);
        }

        /// <summary>Makes <paramref name="photo"/> the cover: it moves first, and the others keep their order.</summary>
        internal void MakeCover(MemoryFormPhotoViewModel photo)
        {
            var index = Photos.IndexOf(photo);

            if (CanEditPhotos && index > 0 && AcceptAsCover(photo))
            {
                Photos.Move(index, 0);
            }
        }

        /// <summary>Drops <paramref name="photo"/>; when it was the cover, the next one becomes the cover.</summary>
        internal void RemovePhoto(MemoryFormPhotoViewModel photo)
        {
            var index = Photos.IndexOf(photo);

            if (!CanEditPhotos || index < 0)
            {
                return;
            }

            if (index == 0 && Photos.Count > 1 && !AcceptAsCover(Photos[1]))
            {
                return;
            }

            Photos.Remove(photo);
            ImageError = string.Empty;
        }

        /// <summary>
        /// Whether <paramref name="photo"/> may become the cover. The cover has no caption, so a photo with
        /// one asks first; on yes its caption is dropped. Declined, or with no dialog, nothing changes.
        /// </summary>
        private bool AcceptAsCover(MemoryFormPhotoViewModel photo)
        {
            var caption = photo.Caption.Trim();

            if (caption.Length == 0)
            {
                return true;
            }

            if (_dialogs.ConfirmCoverCaption is not { } confirm
                || !confirm($"The cover has no caption, so \"{caption}\" will be removed. Make it the cover?"))
            {
                return false;
            }

            photo.Caption = string.Empty;
            return true;
        }

        private void OnPhotosChanged()
        {
            for (var index = 0; index < Photos.Count; index++)
            {
                Photos[index].IsCover = index == 0;
            }

            OnPropertyChanged(nameof(HasImage));
            OnPropertyChanged(nameof(ImagePreview));
            OnPropertyChanged(nameof(ImageText));
            OnPropertyChanged(nameof(HasExtraPhotos));
            RaiseCommandStates();
        }

        /// <summary>Whether there are photos after the cover.</summary>
        public bool HasExtraPhotos => Photos.Count > 1;


        private async Task LoadPickerAsync(DateTime? date)
        {
            PickerMessages.Clear();
            PickerEntries.Clear();
            SelectedPickerMessage = null;
            SelectedPickerEntry = null;

            if (date is not { } picked)
            {
                PickerText = "Pick a day to see its messages and memories.";
                return;
            }

            var day = DateOnly.FromDateTime(picked);
            PickerText = "Reading…";

            try
            {
                var (messages, entries) = await Task.Run(() =>
                    (_backend.Messages.ReadDay(day), _backend.Store.ReadEntriesOn(day))).ConfigureAwait(true);

                // A newer day was picked meanwhile: its own load fills the lists.
                if (PickerDate != date)
                {
                    return;
                }

                foreach (var message in messages)
                {
                    PickerMessages.Add(new MessagePickRow(message));
                }

                foreach (var entry in entries.Where(e => e.EntryID != _entryID))
                {
                    PickerEntries.Add(new EntryPickRow(entry));
                }

                PickerText = Count(PickerMessages.Count, "message") + " and " + Count(PickerEntries.Count, "memory", "memories") + " on this day.";
            }
            catch (Exception exception) when (MemoriesViewModel.IsExpected(exception))
            {
                if (PickerDate == date)
                {
                    PickerText = MemoriesViewModel.Describe(exception, "That day could not be read.");
                }
            }
        }

        private void AddMessageLink()
        {
            if (SelectedPickerMessage is not { } row)
            {
                return;
            }

            AddLink(PersonalLinkTarget.ToMessage(row.Message.SequenceNumber), MessageLinkText(row.Message.SequenceNumber, row.Message));
        }

        private void AddEntryLink()
        {
            if (SelectedPickerEntry is not { } row)
            {
                return;
            }

            AddLink(PersonalLinkTarget.ToEntry(row.Entry.EntryID), row.Text);
        }

        private void AddLink(PersonalLinkTarget target, string text)
        {
            LinksError = string.Empty;

            if (Links.Any(l => l.Target == target))
            {
                LinksError = "That link is already there.";
                return;
            }

            Links.Add(new MemoryLinkViewModel(target, text, RemoveLink));
        }

        private void RemoveLink(MemoryLinkViewModel link)
        {
            if (!IsSaving)
            {
                Links.Remove(link);
                LinksError = string.Empty;
            }
        }

        private void ClearErrors()
        {
            CollectionError = TitleError = StartError = EndError = SourceError = SummaryError = ImageError = LinksError = FormError = string.Empty;
        }

        private void ShowErrors(IReadOnlyDictionary<string, string> errors)
        {
            foreach (var (field, message) in errors)
            {
                switch (field)
                {
                    case PersonalFields.Collection: CollectionError = message; break;
                    case PersonalFields.Title: TitleError = message; break;
                    case PersonalFields.Start: StartError = message; break;
                    case PersonalFields.End: EndError = message; break;
                    case PersonalFields.Source: SourceError = message; break;
                    case PersonalFields.Summary: SummaryError = message; break;
                    case PersonalFields.Image: ImageError = message; break;
                    case PersonalFields.Links: LinksError = message; break;
                    default: FormError = string.IsNullOrEmpty(FormError) ? message : FormError + " " + message; break;
                }
            }
        }

        /// <summary>A message as a link line shows it.</summary>
        internal static string MessageLinkText(int sequence, MessageLinkCandidate? message) =>
            "Message " + sequence.ToString(CultureInfo.CurrentCulture) + ": " +
            (message is null
                ? "not in the open conversation"
                : message.TimeLocal.ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture) + ", " +
                  (message.SenderName ?? "System") + ": " + message.Preview);

        /// <summary>"09:00 Ada: preview".</summary>
        internal static string MessageLine(MessageLinkCandidate message) =>
            message.TimeLocal.ToString("HH:mm", CultureInfo.CurrentCulture) + " " +
            (message.SenderName ?? "System") + ": " + message.Preview;

        private static string Count(int value, string noun, string? plural = null) =>
            value == 1 ? "1 " + noun : value.ToString(CultureInfo.CurrentCulture) + " " + (plural ?? noun + "s");

        private static string Size(long bytes) =>
            bytes >= 1024 * 1024
                ? (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.CurrentCulture) + " MB"
                : Math.Max(1, bytes / 1024).ToString(CultureInfo.CurrentCulture) + " KB";
    }
}
