using System.Globalization;
using Chatcheology.Data.Memories.Personal;

namespace Chatcheology.Desktop.ViewModels
{
    /// <summary>One noted date in the list (CH-47): title, "14 Feb", and "since 2019" when a year is set.</summary>
    public sealed class NotedDateRow(NotedDate date)
    {
        /// <summary>Its identifier in the memories file.</summary>
        public long NotedDateID { get; } = date.NotedDateID;

        /// <summary>The title.</summary>
        public string Title { get; } = date.Title;

        /// <summary>The month, 1 to 12.</summary>
        public int Month { get; } = date.Month;

        /// <summary>The day of the month.</summary>
        public int Day { get; } = date.Day;

        /// <summary>The year it started, or null.</summary>
        public int? Year { get; } = date.Year;

        /// <summary>The note, or null.</summary>
        public string? Note { get; } = date.Note;

        /// <summary>"14 Feb": the day and the month's short English name.</summary>
        public string DateText =>
            Day.ToString(CultureInfo.InvariantCulture) + " " +
            CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(Month);

        /// <summary>"since 2019", or empty when there is no year.</summary>
        public string SinceText => Year is { } year ? "since " + year.ToString(CultureInfo.InvariantCulture) : string.Empty;

        /// <summary>"14 Feb: Our anniversary, since 2019".</summary>
        public string DisplayText => DateText + ": " + Title + (Year is null ? string.Empty : ", " + SinceText);

        /// <inheritdoc/>
        public override string ToString() => DisplayText;
    }

    /// <summary>A month in the form's month list.</summary>
    public sealed record MonthChoice(int Number, string Name)
    {
        /// <inheritdoc/>
        public override string ToString() => Name;
    }

    /// <summary>
    /// The form for one noted date (CH-47): title, day, month, optional year, optional note. Every rule is
    /// <see cref="NotedDateStore.Validate"/>'s, the same one the store and the archive use; a field's problem
    /// shows on it once it has been touched, and Save stays disabled while anything is wrong.
    /// </summary>
    public sealed class NotedDateFormViewModel : ObservableObject
    {
        private static readonly IReadOnlyList<MonthChoice> AllMonths =
        [
            .. Enumerable.Range(1, 12).Select(m => new MonthChoice(m, CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m))),
        ];

        private readonly NotedDateStore _store;
        private readonly MemoriesDialogs _dialogs;
        private readonly Func<bool> _blocked;
        private readonly Func<DateTime> _utcNow;
        private readonly Action<long?> _closed;
        private readonly Action<bool> _saving;
        private readonly long? _notedDateID;

        private string _title = string.Empty;
        private string _dayText = string.Empty;
        private MonthChoice? _month;
        private string _yearText = string.Empty;
        private string _note = string.Empty;
        private bool _titleTouched;
        private bool _dateTouched;
        private bool _yearTouched;
        private bool _noteTouched;
        private bool _isSaving;
        private string _formError = string.Empty;
        private IReadOnlyDictionary<string, string> _problems = new Dictionary<string, string>();

        internal NotedDateFormViewModel(
            NotedDateStore store,
            MemoriesDialogs dialogs,
            NotedDateRow? existing,
            Func<bool> blocked,
            Func<DateTime> utcNow,
            Action<long?> closed,
            Action<bool> saving)
        {
            _store = store;
            _dialogs = dialogs;
            _blocked = blocked;
            _utcNow = utcNow;
            _closed = closed;
            _saving = saving;

            SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsSaving && !_blocked() && !HasProblems);
            CancelCommand = new RelayCommand(() => _closed(null), () => !IsSaving);

            if (existing is not null)
            {
                _notedDateID = existing.NotedDateID;
                _title = existing.Title;
                _dayText = existing.Day.ToString(CultureInfo.InvariantCulture);
                _month = AllMonths[existing.Month - 1];
                _yearText = existing.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                _note = existing.Note ?? string.Empty;
            }

            Recheck();
        }

        /// <summary>Checks and saves, then closes the form. A first save asks before the file is created.</summary>
        public AsyncRelayCommand SaveCommand { get; }

        /// <summary>Closes the form; nothing is written.</summary>
        public RelayCommand CancelCommand { get; }

        /// <summary>True for a new noted date, false when editing.</summary>
        public bool IsNew => _notedDateID is null;

        /// <summary>"New noted date" or "Edit noted date".</summary>
        public string Heading => IsNew ? "New noted date" : "Edit noted date";

        /// <summary>January to December.</summary>
        public IReadOnlyList<MonthChoice> Months => AllMonths;

        /// <summary>Required, up to 80 characters.</summary>
        public string Title
        {
            get => _title;
            set
            {
                if (SetProperty(ref _title, value ?? string.Empty))
                {
                    _titleTouched = true;
                    Recheck();
                }
            }
        }

        /// <summary>Required: the day of the month, as typed.</summary>
        public string DayText
        {
            get => _dayText;
            set
            {
                if (SetProperty(ref _dayText, value ?? string.Empty))
                {
                    _dateTouched = true;
                    Recheck();
                }
            }
        }

        /// <summary>Required.</summary>
        public MonthChoice? SelectedMonth
        {
            get => _month;
            set
            {
                if (SetProperty(ref _month, value))
                {
                    _dateTouched = true;
                    Recheck();
                }
            }
        }

        /// <summary>Optional: the year it started, as typed.</summary>
        public string YearText
        {
            get => _yearText;
            set
            {
                if (SetProperty(ref _yearText, value ?? string.Empty))
                {
                    _yearTouched = true;

                    // 29 February depends on the year.
                    _dateTouched = true;
                    Recheck();
                }
            }
        }

        /// <summary>Optional note.</summary>
        public string Note
        {
            get => _note;
            set
            {
                if (SetProperty(ref _note, value ?? string.Empty))
                {
                    _noteTouched = true;
                    Recheck();
                }
            }
        }

        /// <summary>The title's problem, once touched; else empty.</summary>
        public string TitleError => _titleTouched ? Problem(PersonalFields.Title) : string.Empty;

        /// <summary>The day and month's problem, once touched; else empty.</summary>
        public string DateError => _dateTouched ? Problem(PersonalFields.Date) : string.Empty;

        /// <summary>The year's problem, once touched; else empty.</summary>
        public string YearError => _yearTouched ? Problem(PersonalFields.Year) : string.Empty;

        /// <summary>The note's problem, once touched; else empty.</summary>
        public string NoteError => _noteTouched ? Problem(PersonalFields.Note) : string.Empty;

        /// <summary>A problem that is not one field's (a refused or failed save), or empty.</summary>
        public string FormError
        {
            get => _formError;
            private set => SetProperty(ref _formError, value);
        }

        /// <summary>Whether any field breaks a rule right now (shown or not).</summary>
        public bool HasProblems => _problems.Count > 0;

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

        internal void RaiseCommandStates()
        {
            SaveCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }

        private string Problem(string field) => _problems.TryGetValue(field, out var text) ? text : string.Empty;

        /// <summary>The draft as typed, with the problems of text that is not a number.</summary>
        private (NotedDateDraft Draft, Dictionary<string, string> Own) Read()
        {
            var own = new Dictionary<string, string>();
            var day = 0;

            if (_dayText.Trim().Length > 0
                && int.TryParse(_dayText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedDay))
            {
                day = parsedDay;
            }

            int? year = null;

            if (_yearText.Trim().Length > 0)
            {
                if (int.TryParse(_yearText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedYear))
                {
                    year = parsedYear;
                }
                else
                {
                    own[PersonalFields.Year] = "Type the year as a number, or leave it out.";
                }
            }

            return (new NotedDateDraft
            {
                Title = _title,
                Month = _month?.Number ?? 0,
                Day = day,
                Year = year,
                Note = _note,
            }, own);
        }

        private void Recheck()
        {
            var (draft, own) = Read();
            var problems = new Dictionary<string, string>(NotedDateStore.Validate(draft, _utcNow()));

            foreach (var (field, text) in own)
            {
                problems[field] = text;
            }

            _problems = problems;

            if (FormError.Length > 0)
            {
                FormError = string.Empty;
            }

            OnPropertyChanged(nameof(TitleError));
            OnPropertyChanged(nameof(DateError));
            OnPropertyChanged(nameof(YearError));
            OnPropertyChanged(nameof(NoteError));
            OnPropertyChanged(nameof(HasProblems));
            SaveCommand.RaiseCanExecuteChanged();
        }

        private async Task SaveAsync()
        {
            if (HasProblems)
            {
                return;
            }

            var (draft, _) = Read();
            var create = false;

            if (IsNew && !_store.FileExists)
            {
                if (!_dialogs.ConfirmCreateFile(
                        "There is no memories file beside this workspace yet. Create it now, with this noted date? " +
                        "The workspace itself is not changed."))
                {
                    FormError = "Nothing was created.";
                    return;
                }

                create = true;
            }

            FormError = string.Empty;
            IsSaving = true;
            _saving(true);

            long? saved = null;

            try
            {
                var now = _utcNow();
                var store = _store;

                saved = _notedDateID is { } existing
                    ? await Task.Run(() =>
                    {
                        store.Update(existing, draft, now);
                        return existing;
                    }).ConfigureAwait(true)
                    : await Task.Run(() => store.Add(draft, create, now)).ConfigureAwait(true);
            }
            catch (PersonalMemoryValidationException exception)
            {
                ShowErrors(exception.Errors);
            }
            catch (Exception exception) when (MemoriesViewModel.IsExpected(exception))
            {
                FormError = MemoriesViewModel.Describe(exception, "The noted date could not be saved. Nothing was written.");
            }
            finally
            {
                IsSaving = false;
                _saving(false);
            }

            if (saved is { } id)
            {
                _closed(id);
            }
        }

        private void ShowErrors(IReadOnlyDictionary<string, string> errors)
        {
            _titleTouched = _dateTouched = _yearTouched = _noteTouched = true;

            var merged = new Dictionary<string, string>(_problems);
            var other = new List<string>();

            foreach (var (field, text) in errors)
            {
                if (field is PersonalFields.Title or PersonalFields.Date or PersonalFields.Year or PersonalFields.Note)
                {
                    merged[field] = text;
                }
                else
                {
                    other.Add(text);
                }
            }

            _problems = merged;
            OnPropertyChanged(nameof(TitleError));
            OnPropertyChanged(nameof(DateError));
            OnPropertyChanged(nameof(YearError));
            OnPropertyChanged(nameof(NoteError));
            OnPropertyChanged(nameof(HasProblems));
            FormError = string.Join(" ", other) is { Length: > 0 } joined ? joined + " Nothing was written." : string.Empty;
            SaveCommand.RaiseCanExecuteChanged();
        }
    }
}
