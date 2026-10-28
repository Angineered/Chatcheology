using System.Collections.ObjectModel;
using Chatcheology.Data.Memories.Personal;

namespace Chatcheology.Desktop.ViewModels
{
    /// <summary>
    /// The Noted dates section of the Memories area (CH-47): a list by month then day, and a form to add and
    /// edit them. They share the area's busy, blocked and closing behaviour: not while an export, check or
    /// decision runs, held while a read or write is in flight, and a result that arrives after the area was
    /// closed or reopened is dropped. Only the <c>NotedDate</c> table of the memories file is written.
    /// </summary>
    public sealed partial class MemoriesViewModel
    {
        private NotedDateRow? _selectedNotedDate;
        private NotedDateFormViewModel? _notedForm;

        /// <summary>Opens an empty noted-date form.</summary>
        public RelayCommand NewNotedDateCommand { get; }

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

        private void ShowNotedDates(IReadOnlyList<NotedDate> dates, long? keep)
        {
            NotedDates.Clear();

            foreach (var date in dates)
            {
                NotedDates.Add(new NotedDateRow(date));
            }

            _selectedNotedDate = null;
            SelectedNotedDate = NotedDates.FirstOrDefault(d => d.NotedDateID == keep);
        }

        private void NewNotedDate() => OpenNotedForm(null);

        private void EditNotedDate()
        {
            if (SelectedNotedDate is { } row)
            {
                OpenNotedForm(row);
            }
        }

        private void OpenNotedForm(NotedDateRow? existing)
        {
            if (_backend is not { } backend)
            {
                return;
            }

            StatusText = string.Empty;
            var generation = _generation;

            NotedForm = new NotedDateFormViewModel(
                backend.NotedDates,
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
