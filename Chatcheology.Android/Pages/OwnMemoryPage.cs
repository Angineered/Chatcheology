using Chatcheology.Android.Controls;
using Chatcheology.Archive;
using Chatcheology.Mobile.OwnMemories;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// Adds or edits one of the phone owner's own memories (CH-54 step 2): start date, optional end date, Day | Month,
    /// title, summary, Every year (only Day without an end; otherwise off with a short reason), photos with captions
    /// (the first is the cover; Up / Down / Remove) and up to ten links. The fields and rules are
    /// <see cref="OwnMemoryForm"/>; Save shows every problem at once, beside its field, and writes nothing then.
    /// </summary>
    /// <remarks>
    /// <para>Photos come from the system photo picker and are stored at once without their location
    /// (<see cref="OwnPhotoPicker"/>); the form holds them in an <see cref="OwnPhotoLease"/> until it closes, so a delete
    /// elsewhere cannot take them (step 1a gap 6). A photo added and then not saved is swept later as unused.</para>
    /// <para>Back with unsaved changes asks first. Nothing typed here is logged.</para>
    /// </remarks>
    internal sealed class OwnMemoryPage : ContentPage
    {
        private static readonly DateTime Earliest = new(1900, 1, 1);
        private static readonly DateTime Latest = new(2200, 12, 31);

        private readonly OwnMemoryStore _store;
        private readonly OwnMemoryForm _form;
        private readonly Func<OwnMemory, Task> _saved;
        private readonly OwnPhotoLease _lease;

        // No MaxLength on the text fields: a cut paste would pass silently; the shared rules report what is too long.
        private readonly Entry _title = new() { Placeholder = "Title" };
        private readonly Label _titleError = FormParts.Error();
        private readonly Button _day = PrecisionButton("Day");
        private readonly Button _month = PrecisionButton("Month");
        private readonly DatePicker _start = new() { MinimumDate = Earliest, MaximumDate = Latest, HorizontalOptions = LayoutOptions.Start };
        private readonly Label _startError = FormParts.Error();
        private readonly Switch _hasEnd = new() { VerticalOptions = LayoutOptions.Center };
        private readonly DatePicker _end = new() { MinimumDate = Earliest, MaximumDate = Latest, HorizontalOptions = LayoutOptions.Start };
        private readonly Label _endError = FormParts.Error();
        private readonly Switch _everyYear = new() { VerticalOptions = LayoutOptions.Center };
        private readonly Label _everyYearReason = new Label { FontSize = 12, IsVisible = false }.TextIn("MutedText");
        private readonly Label _everyYearError = FormParts.Error();
        private readonly Editor _summary = new() { Placeholder = "Summary (optional)", AutoSize = EditorAutoSizeOption.TextChanges };
        private readonly Label _summaryError = FormParts.Error();
        private readonly VerticalStackLayout _photoRows = new() { Spacing = 10 };
        private readonly Label _photosError = FormParts.Error();
        private readonly Button _addPhoto = new() { Text = "Add a photo", HorizontalOptions = LayoutOptions.Start };
        private readonly ActivityIndicator _addingPhoto = new() { IsVisible = false, IsRunning = false, HorizontalOptions = LayoutOptions.Start };
        private readonly LinkRowsEditor _linkEditor;
        private readonly Label _linksError = FormParts.Error();
        private readonly Label _memoryError = FormParts.Error();
        private readonly Button _save = new() { Text = "Save" };
        private bool _loading;
        private bool _saving;
        private bool _done;
        private bool _leaving;

        /// <param name="store">The own store.</param>
        /// <param name="form">A new form (with its start day) or one over the memory edited.</param>
        /// <param name="saved">Called with the stored memory after a save; the page then closes.</param>
        internal OwnMemoryPage(OwnMemoryStore store, OwnMemoryForm form, Func<OwnMemory, Task> saved)
        {
            _store = store;
            _form = form;
            _saved = saved;
            _lease = store.LeasePhotos();

            Title = form.IsNew ? "Add a memory" : "Edit memory";

            foreach (var photo in form.Photos)
            {
                _lease.TryHold(photo.FileName);
            }

            _loading = true;
            _title.Text = form.Title;
            _summary.Text = form.Summary;
            _start.Date = (form.Start ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue);
            _hasEnd.IsToggled = form.End is not null;
            _end.Date = (form.End ?? form.Start ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue);
            _everyYear.IsToggled = form.EveryYear;
            _loading = false;

            _hasEnd.Themed(Switch.OnColorProperty, "Primary");
            _everyYear.Themed(Switch.OnColorProperty, "Primary");
            SemanticProperties.SetDescription(_title, "Title");
            SemanticProperties.SetDescription(_summary, "Summary");
            SemanticProperties.SetDescription(_start, "Start date");
            SemanticProperties.SetDescription(_end, "End date");
            SemanticProperties.SetDescription(_hasEnd, "Has an end date");
            SemanticProperties.SetDescription(_everyYear, "Every year");

            _title.TextChanged += (_, e) => _form.Title = e.NewTextValue ?? string.Empty;
            _summary.TextChanged += (_, e) => _form.Summary = e.NewTextValue;
            _start.DateSelected += (_, _) => ReadDates();
            _end.DateSelected += (_, _) => ReadDates();
            _hasEnd.Toggled += (_, _) => ReadDates();
            _everyYear.Toggled += (_, e) =>
            {
                if (!_loading)
                {
                    _form.EveryYear = e.Value;
                    ShowEveryYear();
                }
            };
            _day.Clicked += (_, _) => ChoosePrecision(MemoryDatePrecision.Day);
            _month.Clicked += (_, _) => ChoosePrecision(MemoryDatePrecision.Month);
            _addPhoto.Clicked += async (_, _) => await AddPhotoAsync();
            _save.Clicked += async (_, _) => await SaveAsync();

            _linkEditor = new LinkRowsEditor(form.Links, () => { });

            var endRow = SwitchRow("Has an end date", _hasEnd);
            var everyYearRow = SwitchRow("Every year (shown on On this day)", _everyYear);

            Content = new ScrollView
            {
                Content = new VerticalStackLayout
                {
                    Padding = 16,
                    Spacing = 8,
                    Children =
                    {
                        FormParts.Caption("Title"),
                        _title,
                        _titleError,

                        FormParts.Caption("Date"),
                        new HorizontalStackLayout { Spacing = 8, Children = { _day, _month } },
                        _start,
                        _startError,
                        endRow,
                        _end,
                        _endError,
                        everyYearRow,
                        _everyYearReason,
                        _everyYearError,

                        FormParts.Caption("Summary"),
                        _summary,
                        _summaryError,

                        FormParts.Caption("Photos (the first is the cover)"),
                        _photoRows,
                        _photosError,
                        new HorizontalStackLayout { Spacing = 12, Children = { _addPhoto, _addingPhoto } },

                        FormParts.Caption("Links (optional)"),
                        _linkEditor,
                        _linksError,

                        _memoryError,
                        _save,
                    },
                },
            };

            ShowPrecision();
            ShowDates();
            ShowEveryYear();
            RebuildPhotos();
        }

        /// <summary>Back with unsaved changes asks first; otherwise it closes as usual.</summary>
        protected override bool OnBackButtonPressed()
        {
            if (_done || _leaving || !_form.HasChanges)
            {
                return base.OnBackButtonPressed();
            }

            Dispatcher.Dispatch(async () => await LeaveAfterAskingAsync());
            return true;
        }

        /// <inheritdoc />
        protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
        {
            base.OnNavigatedFrom(args);

            if (!Navigation.NavigationStack.Contains(this))
            {
                // Closed: the photos it held may go now (unsaved ones are swept later as unused).
                _lease.Dispose();
            }
        }

        private async Task LeaveAfterAskingAsync()
        {
            try
            {
                if (await DisplayAlertAsync(
                        _form.IsNew ? "Discard this memory?" : "Discard your changes?",
                        "What you entered here is not saved.",
                        "Discard",
                        "Keep editing"))
                {
                    _leaving = true;
                    await Navigation.PopAsync();
                }
            }
            catch (Exception)
            {
                // A back handler: nothing here may end the app.
            }
        }

        private void ChoosePrecision(MemoryDatePrecision precision)
        {
            _form.Precision = precision;
            ShowPrecision();
            ShowDates();
            ShowEveryYear();
        }

        /// <summary>The date pickers as the form has them: Month shows only month and year.</summary>
        private void ShowPrecision()
        {
            foreach (var (button, precision) in new[] { (_day, MemoryDatePrecision.Day), (_month, MemoryDatePrecision.Month) })
            {
                var chosen = _form.Precision == precision;

                button.FontAttributes = chosen ? FontAttributes.Bold : FontAttributes.None;
                button.Themed(Button.TextColorProperty, chosen ? "SenderText" : "MutedText");
                button.Themed(Button.BorderColorProperty, chosen ? "SenderText" : "MutedText");
                SemanticProperties.SetDescription(button, precision + (chosen ? ", selected" : string.Empty));
            }

            var format = _form.Precision == MemoryDatePrecision.Month ? "MMMM yyyy" : "d MMMM yyyy";
            _start.Format = format;
            _end.Format = format;
        }

        private void ReadDates()
        {
            if (_loading)
            {
                return;
            }

            _form.Start = _start.Date is { } start ? DateOnly.FromDateTime(start) : null;
            _form.End = _hasEnd.IsToggled && _end.Date is { } end ? DateOnly.FromDateTime(end) : null;
            ShowDates();
            ShowEveryYear();
        }

        private void ShowDates() => _end.IsVisible = _hasEnd.IsToggled;

        /// <summary>Every year: off and disabled with its reason unless Day without an end.</summary>
        private void ShowEveryYear()
        {
            _loading = true;
            _everyYear.IsToggled = _form.EveryYear;
            _loading = false;

            _everyYear.IsEnabled = _form.CanEveryYear;
            FormParts.Show(_everyYearReason, _form.EveryYearReason);
        }

        private async Task AddPhotoAsync()
        {
            if (_addingPhoto.IsRunning)
            {
                return;
            }

            _addPhoto.IsEnabled = false;
            _addingPhoto.IsVisible = _addingPhoto.IsRunning = true;
            FormParts.Show(_photosError, null);

            try
            {
                var picked = await OwnPhotoPicker.PickAsync(_store);

                if (picked is null)
                {
                    return;
                }

                if (picked.FileName is not { } name)
                {
                    FormParts.Show(_photosError, picked.Message);
                    return;
                }

                // Held before it is shown, so a delete elsewhere cannot take it from the form.
                if (!_lease.TryHold(name))
                {
                    FormParts.Show(_photosError, "This photo couldn't be added. Try again.");
                    return;
                }

                if (!_form.AddPhoto(name, Path.Combine(_store.PhotosFolder, name)))
                {
                    FormParts.Show(_photosError, "This photo is already in the memory.");
                    return;
                }

                RebuildPhotos();
            }
            catch (Exception)
            {
                // A button handler: nothing here may end the app.
                FormParts.Show(_photosError, "This photo couldn't be added.");
            }
            finally
            {
                _addingPhoto.IsVisible = _addingPhoto.IsRunning = false;
                _addPhoto.IsEnabled = true;
            }
        }

        /// <summary>One row per photo, in order: the thumbnail (the first marked Cover), its caption, Up, Down and Remove.</summary>
        private void RebuildPhotos()
        {
            _photoRows.Children.Clear();

            for (var index = 0; index < _form.Photos.Count; index++)
            {
                var photo = _form.Photos[index];
                var at = index;

                var thumbnail = new DecodedImage { WidthRequest = 72, HeightRequest = 72, FailedText = string.Empty, Path = photo.Path };
                var caption = new Entry { Placeholder = "Caption (optional)", Text = photo.Caption };
                SemanticProperties.SetDescription(caption, $"Caption of photo {at + 1}");
                caption.TextChanged += (_, e) => photo.Caption = e.NewTextValue;

                var up = FormParts.SmallButton("Up", $"Move photo {at + 1} up");
                var down = FormParts.SmallButton("Down", $"Move photo {at + 1} down");
                var remove = FormParts.SmallButton("Remove", $"Remove photo {at + 1}");
                up.IsEnabled = at > 0;
                down.IsEnabled = at < _form.Photos.Count - 1;
                up.Clicked += (_, _) =>
                {
                    _form.MovePhoto(at, at - 1);
                    RebuildPhotos();
                };
                down.Clicked += (_, _) =>
                {
                    _form.MovePhoto(at, at + 1);
                    RebuildPhotos();
                };
                remove.Clicked += (_, _) =>
                {
                    _form.RemovePhoto(at);
                    RebuildPhotos();
                };

                var side = new VerticalStackLayout
                {
                    Spacing = 2,
                    Children = { caption, new HorizontalStackLayout { Spacing = 6, Children = { up, down, remove } } },
                };

                if (at == 0)
                {
                    side.Children.Insert(0, new Label { Text = "Cover", FontSize = 12, FontAttributes = FontAttributes.Bold }.TextIn("SenderText"));
                }

                var row = new Grid
                {
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
                    ColumnSpacing = 10,
                };

                row.Add(thumbnail, 0);
                row.Add(side, 1);
                _photoRows.Add(row);
            }
        }

        private async Task SaveAsync()
        {
            if (_saving || _done)
            {
                return;
            }

            _saving = true;
            _save.IsEnabled = false;

            try
            {
                var check = _form.Check();
                ShowErrors(check.Errors, check.LinkRowErrors);

                if (check.Draft is not { } draft)
                {
                    return;
                }

                var id = _form.Id;
                OwnMemory stored;

                try
                {
                    stored = await Task.Run(() => id is { } existing ? _store.Update(existing, draft) : _store.Add(draft));
                }
                catch (OwnMemoryValidationException refused)
                {
                    // The store's own checks (a photo gone, the memory deleted meanwhile): shown like the form's.
                    ShowErrors(refused.Errors, check.LinkRowErrors);
                    return;
                }
                catch (Exception exception) when (exception is OwnMemoriesStoreException or SqliteException or IOException or UnauthorizedAccessException)
                {
                    FormParts.Show(_memoryError, "This memory could not be saved. Try again.");
                    return;
                }

                AppState.LogTiming("own-memory-saved", TimeSpan.Zero, $"new={(id is null ? "yes" : "no")} photos={draft.Photos.Count} links={draft.Links.Count}");

                _done = true;
                await _saved(stored);
            }
            catch (Exception)
            {
                // A button handler: nothing here may end the app.
                FormParts.Show(_memoryError, "This memory could not be saved. Try again.");
            }
            finally
            {
                _saving = false;
                _save.IsEnabled = !_done;
            }
        }

        private void ShowErrors(IReadOnlyDictionary<OwnMemoryField, string> errors, IReadOnlyDictionary<int, string> linkRows)
        {
            FormParts.Show(_titleError, errors.GetValueOrDefault(OwnMemoryField.Title));
            FormParts.Show(_startError, errors.GetValueOrDefault(OwnMemoryField.Start));
            FormParts.Show(_endError, errors.GetValueOrDefault(OwnMemoryField.End));
            FormParts.Show(_everyYearError, errors.GetValueOrDefault(OwnMemoryField.EveryYear));
            FormParts.Show(_summaryError, errors.GetValueOrDefault(OwnMemoryField.Summary));
            FormParts.Show(_photosError, errors.GetValueOrDefault(OwnMemoryField.Photos));
            FormParts.Show(_linksError, errors.GetValueOrDefault(OwnMemoryField.Links));
            FormParts.Show(_memoryError, errors.GetValueOrDefault(OwnMemoryField.Memory));
            _linkEditor.ShowErrors(linkRows);
        }

        private static Grid SwitchRow(string text, Switch toggle)
        {
            var row = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                ColumnSpacing = 12,
            };

            row.Add(new Label { Text = text, FontSize = 15, VerticalOptions = LayoutOptions.Center }, 0);
            row.Add(toggle, 1);

            return row;
        }

        private static Button PrecisionButton(string text) => new()
        {
            Text = text,
            FontSize = 14,
            Padding = new Thickness(16, 0),
            HeightRequest = 40,
            MinimumWidthRequest = 48,
            CornerRadius = 18,
            BorderWidth = 1,
            BackgroundColor = Colors.Transparent,
        };
    }
}
