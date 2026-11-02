using System.Collections.ObjectModel;
using System.Globalization;
using Chatcheology.Data.Memories.Personal;

namespace Chatcheology.Desktop.ViewModels
{
    /// <summary>
    /// The Noted dates section of the Memories area (CH-47, reduced by CH-53): while any noted date remains, a
    /// list by month then day with edit and delete (no add), and "Move noted dates to memories", which turns
    /// every one with a year into a yearly memory. When none remain the section is gone. They share the area's
    /// busy, blocked and closing behaviour: not while an export, check or decision runs, held while a read or
    /// write is in flight, and a result that arrives after the area was closed or reopened is dropped.
    /// </summary>
    public sealed partial class MemoriesViewModel
    {
        private NotedDateRow? _selectedNotedDate;
        private NotedDateFormViewModel? _notedForm;

        /// <summary>
        /// Moves every noted date with a year to memories (CH-53), after a confirmation with the count. Only
        /// while at least one noted date has a year.
        /// </summary>
        public AsyncRelayCommand MoveNotedDatesCommand { get; }

        /// <summary>Whether any noted date remains: the section (list, edit, delete, move) shows only then.</summary>
        public bool HasNotedDates => NotedDates.Count > 0;

        /// <summary>How many remaining noted dates have a year (and so can be moved).</summary>
        public int NotedDatesWithYear => NotedDates.Count(d => d.Year is not null);

        /// <summary>
        /// A line under the section: what the move does, and what to do with a noted date that has no year.
        /// </summary>
        public string NotedDatesText
        {
            get
            {
                var withYear = NotedDatesWithYear;
                var without = NotedDates.Count - withYear;
                var text = "Noted dates are now yearly memories. Move them to keep them on On this day.";

                return without == 0
                    ? text
                    : text + " " + (without == 1 ? "1 noted date has" : without.ToString(CultureInfo.CurrentCulture) + " noted dates have") +
                      " no year and cannot be moved: give it a year, or delete it.";
            }
        }

        /// <summary>Opens the selected noted date in the form.</summary>
        public RelayCommand EditNotedDateCommand { get; }

        /// <summary>Deletes the selected noted date after confirmation.</summary>
        public AsyncRelayCommand DeleteNotedDateCommand { get; }

        /// <summary>The noted dates, by month then day.</summary>
        public ObservableCollection<NotedDateRow> NotedDates { get; } = [];

        /// <summary>The selected noted date.</summary>
        public NotedDateRow? SelectedNotedDate
        {
            get => _selectedNotedDate;
            set
            {
                if (SetProperty(ref _selectedNotedDate, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        /// <summary>The open noted-date form, or null.</summary>
        public NotedDateFormViewModel? NotedForm
        {
            get => _notedForm;
            private set
            {
                if (_notedForm is not null)
                {
                    _notedForm.PropertyChanged -= OnNotedFormPropertyChanged;
                }

                if (value is not null)
                {
                    value.PropertyChanged += OnNotedFormPropertyChanged;
                }

                if (SetProperty(ref _notedForm, value))
                {
                    OnPropertyChanged(nameof(IsNotedFormOpen));
                    OnPropertyChanged(nameof(IsListShown));
                    RaiseCommandStates();
                }
            }
        }

        /// <summary>Whether the noted-date form is showing.</summary>
        public bool IsNotedFormOpen => NotedForm is not null;

        /// <summary>The list's re-read after the last noted-date save (for tests to wait on).</summary>
        internal Task AfterNotedSave { get; private set; } = Task.CompletedTask;

        private void OnNotedFormPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(NotedDateFormViewModel.IsSaving))
            {
                CloseCommand.RaiseCanExecuteChanged();
            }
        }

        private void ShowNotedDates(IReadOnlyList<NotedDate> dates, IReadOnlyDictionary<long, IReadOnlyList<ExternalLink>> galleries, long? keep)
        {
            NotedDates.Clear();

            foreach (var date in dates)
            {
                NotedDates.Add(new NotedDateRow(date, galleries.GetValueOrDefault(date.NotedDateID)));
            }

            OnNotedDatesChanged();
            _selectedNotedDate = null;
            SelectedNotedDate = NotedDates.FirstOrDefault(d => d.NotedDateID == keep);
        }

        private void OnNotedDatesChanged()
        {
            OnPropertyChanged(nameof(HasNotedDates));
            OnPropertyChanged(nameof(NotedDatesWithYear));
            OnPropertyChanged(nameof(NotedDatesText));
            MoveNotedDatesCommand.RaiseCanExecuteChanged();
        }

        private void EditNotedDate()
        {
            if (SelectedNotedDate is { } row)
            {
                OpenNotedForm(row);
            }
        }

        private void OpenNotedForm(NotedDateRow existing)
        {
            if (_backend is not { } backend)
            {
                return;
            }

            StatusText = string.Empty;
            var generation = _generation;

            NotedForm = new NotedDateFormViewModel(
                backend.NotedDates,
                backend.ExternalLinks,
                _dialogs,
                existing,
                _blocked,
                _utcNow,
                id => OnNotedFormClosed(generation, id),
                OnSaving);
        }

        private void OnNotedFormClosed(int generation, long? savedID)
        {
            // Closed or reopened meanwhile: what the form reports belongs to a context that is gone.
            if (generation != _generation)
            {
                return;
            }

            NotedForm = null;

            if (savedID is { } id)
            {
                AfterNotedSave = RefreshAfterNotedSaveAsync(generation, id);
            }
        }

        private async Task RefreshAfterNotedSaveAsync(int generation, long id)
        {
            await RefreshAsync(null, null).ConfigureAwait(true);

            if (generation != _generation)
            {
                return;
            }

            SelectedNotedDate = NotedDates.FirstOrDefault(d => d.NotedDateID == id);
            StatusText = "Noted date saved.";
        }


        private async Task MoveNotedDatesAsync()
        {
            if (_backend is not { } backend)
            {
                return;
            }

            var all = NotedDates.Count;
            var withYear = NotedDatesWithYear;
            var without = all - withYear;

            var question =
                "Move " + (withYear == 1 ? "1 noted date" : withYear.ToString(CultureInfo.CurrentCulture) + " noted dates") +
                " to memories? Each becomes a yearly memory, with its note and gallery links, in your collection \"" +
                NotedDateStore.MovedCollectionName + "\". A copy of the memories file is kept first beside it, as " +
                NotedDateStore.MoveBackupFileName + " (or the next free name after it)." +
                (without == 0
                    ? string.Empty
                    : " " + (without == 1 ? "1 noted date has" : without.ToString(CultureInfo.CurrentCulture) + " noted dates have") +
                      " no year and stays here.");

            if (!_dialogs.ConfirmMoveNotedDates(question))
            {
                StatusText = "Nothing was moved.";
                return;
            }

            await WriteAsync(
                () => backend.NotedDates.MoveToMemories(all),
                result => (
                    "Moved " + (result.Moved == 1 ? "1 noted date" : result.Moved.ToString(CultureInfo.CurrentCulture) + " noted dates") +
                    " to memories. The copy from before is " + result.BackupFileName + "." +
                    (result.LeftWithoutYear == 0
                        ? string.Empty
                        : " " + (result.LeftWithoutYear == 1
                            ? "1 noted date has"
                            : result.LeftWithoutYear.ToString(CultureInfo.CurrentCulture) + " noted dates have") +
                          " no year and stayed."),
                    result.CollectionID,
                    null)).ConfigureAwait(true);
        }

        private async Task DeleteNotedDateAsync()
        {
            if (_backend is not { } backend || SelectedNotedDate is not { } row)
            {
                return;
            }

            if (!_dialogs.ConfirmDeleteNotedDate($"Delete the noted date \"{row.Title}\" ({row.DateText})? This cannot be undone."))
            {
                StatusText = "Nothing was deleted.";
                return;
            }

            var generation = _generation;
            ChangeBusy(+1);

            try
            {
                await Task.Run(() => backend.NotedDates.Delete(row.NotedDateID)).ConfigureAwait(true);

                if (generation != _generation)
                {
                    return;
                }

                await RefreshAsync(null, null).ConfigureAwait(true);

                if (generation == _generation)
                {
                    StatusText = "Noted date deleted.";
                }
            }
            catch (PersonalMemoryValidationException exception)
            {
                if (generation == _generation)
                {
                    StatusText = string.Join(" ", exception.Errors.Values) + " Nothing was written.";
                }
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                if (generation == _generation)
                {
                    StatusText = Describe(exception, "The noted date could not be deleted. Nothing was written.");
                }
            }
            finally
            {
                ChangeBusy(-1);
            }
        }
    }
}
