using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// Adds or edits one of the phone's own noted dates (CH-47). The fields follow the shared
    /// <see cref="NotedDateRules"/>; a broken rule is shown beside its field and nothing is saved.
    /// </summary>
    internal sealed class NotedDatePage : ContentPage
    {
        private readonly ArchiveSession _session;
        private readonly string? _id;
        private readonly Action _saved;
        private readonly Entry _title = new() { Placeholder = "Title", MaxLength = NotedDateRules.MaxTitleLength };
        private readonly Picker _month = new() { Title = "Month" };
        private readonly Entry _day = new() { Placeholder = "Day", Keyboard = Keyboard.Numeric, MaxLength = 2 };
        private readonly Entry _year = new() { Placeholder = "Year (optional)", Keyboard = Keyboard.Numeric, MaxLength = 4 };
        private readonly Editor _note = new() { Placeholder = "Note (optional)", AutoSize = EditorAutoSizeOption.TextChanges, MaxLength = NotedDateRules.MaxNoteLength };
        private readonly Label _titleError = Error();
        private readonly Label _dateError = Error();
        private readonly Label _yearError = Error();
        private readonly Label _noteError = Error();

        internal NotedDatePage(ArchiveSession session, NotedDateListing? existing, Action saved)
        {
            _session = session;
            _id = existing?.Id;
            _saved = saved;

            Title = existing is null ? "Add a noted date" : "Edit noted date";

            var names = CultureInfo.CurrentCulture.DateTimeFormat.MonthNames.Where(name => name.Length > 0).ToList();
            _month.ItemsSource = names;

            if (existing is not null)
            {
                _title.Text = existing.Title;
                _month.SelectedIndex = existing.Month - 1;
                _day.Text = existing.Day.ToString(CultureInfo.InvariantCulture);
                _year.Text = existing.Year?.ToString(CultureInfo.InvariantCulture);
                _note.Text = existing.Note;
            }

            var save = new Button { Text = "Save" };
            save.Clicked += OnSaveClicked;

            Content = new ScrollView
            {
                Content = new VerticalStackLayout
                {
                    Padding = 16,
                    Spacing = 8,
                    Children =
                    {
                        new Label { Text = "Title", FontSize = 13 }.TextIn("MutedText"),
                        _title,
                        _titleError,
                        new Label { Text = "Day and month", FontSize = 13 }.TextIn("MutedText"),
                        _month,
                        _day,
                        _dateError,
                        new Label { Text = "Year it started (leave out to show only the title)", FontSize = 13 }.TextIn("MutedText"),
                        _year,
                        _yearError,
                        new Label { Text = "Note", FontSize = 13 }.TextIn("MutedText"),
                        _note,
                        _noteError,
                        save,
                    },
                },
            };
        }

        private async void OnSaveClicked(object? sender, EventArgs e)
        {
            try
            {
                var today = OnThisDay.TodayFrom(TimeProvider.System);
                var month = _month.SelectedIndex + 1;
                var dayText = _day.Text?.Trim();
                var yearText = _year.Text?.Trim();

                // Text that is not a number is the field's own problem; 0 fails the rule for the field.
                var day = int.TryParse(dayText, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : 0;
                int? year = string.IsNullOrEmpty(yearText) ? null : int.TryParse(yearText, NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : 0;

                var errors = _session.SaveOwnNotedDate(_id, _title.Text, month, day, year, _note.Text, today);

                _titleError.Text = errors.GetValueOrDefault(NotedDateField.Title);
                _dateError.Text = errors.GetValueOrDefault(NotedDateField.Date);
                _yearError.Text = errors.GetValueOrDefault(NotedDateField.Year);
                _noteError.Text = errors.GetValueOrDefault(NotedDateField.Note);

                if (errors.Count == 0)
                {
                    _saved();
                    await Navigation.PopAsync();
                }
            }
            catch (Exception)
            {
                // A button handler: nothing here may end the app.
                _titleError.Text = "That could not be saved.";
            }
        }

        private static Label Error() => new Label { FontSize = 13, TextColor = Color.FromArgb("#B3261E") };
    }
}
