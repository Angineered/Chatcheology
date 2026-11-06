using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Memories;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// Adds or edits one of the phone's own noted dates (CH-47). The fields follow the shared
    /// <see cref="NotedDateRules"/>; a broken rule is shown beside its field and nothing is saved. Gallery links
    /// (CH-48): up to ten, ordered, each an address and an optional label, checked with the shared
    /// <see cref="ExternalLinkRules"/>; every problem shows on its row at once.
    /// </summary>
    internal sealed class NotedDatePage : ContentPage
    {
        private readonly ArchiveSession _session;
        private readonly string? _id;
        private readonly Action _saved;
        private readonly string _newId = Guid.NewGuid().ToString("N");
        private bool _saving;
        private readonly Entry _title = new() { Placeholder = "Title", MaxLength = NotedDateRules.MaxTitleLength };
        private readonly Picker _month = new() { Title = "Month" };
        private readonly Entry _day = new() { Placeholder = "Day", Keyboard = Keyboard.Numeric, MaxLength = 2 };
        private readonly Entry _year = new() { Placeholder = "Year (optional)", Keyboard = Keyboard.Numeric, MaxLength = 4 };
        private readonly Editor _note = new() { Placeholder = "Note (optional)", AutoSize = EditorAutoSizeOption.TextChanges, MaxLength = NotedDateRules.MaxNoteLength };
        private readonly Label _titleError = Error();
        private readonly Label _dateError = Error();
        private readonly Label _yearError = Error();
        private readonly Label _noteError = Error();
        private readonly List<LinkRow> _links = [];
        private readonly VerticalStackLayout _linkRows = new() { Spacing = 8 };
        private readonly Label _linksError = Error();
        private readonly Button _addLink = new() { Text = "Add a link", HorizontalOptions = LayoutOptions.Start };

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

                foreach (var link in existing.Galleries)
                {
                    _links.Add(new LinkRow(link.Url, link.Label));
                }
            }

            _addLink.Clicked += (_, _) =>
            {
                if (_links.Count < ExternalLinkRules.MaxLinksPerOwner)
                {
                    _links.Add(new LinkRow(null, null));
                    RebuildLinks();
                    _links[^1].Url.Focus();
                }
            };
            RebuildLinks();

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
                        new Label { Text = "Links (optional)", FontSize = 13 }.TextIn("MutedText"),
                        _linkRows,
                        _linksError,
                        _addLink,
                        new Label { Text = GalleryLinks.OwnLinksNote, FontSize = 12 }.TextIn("MutedText"),
                        save,
                    },
                },
            };
        }

        private async void OnSaveClicked(object? sender, EventArgs e)
        {
            if (_saving)
            {
                return;
            }

            _saving = true;
            var saved = false;

            try
            {
                var today = OnThisDay.TodayFrom(TimeProvider.System);
                var month = _month.SelectedIndex + 1;
                var dayText = _day.Text?.Trim();
                var yearText = _year.Text?.Trim();

                // Text that is not a number is the field's own problem; 0 fails the rule for the field.
                var day = int.TryParse(dayText, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : 0;
                int? year = string.IsNullOrEmpty(yearText) ? null : int.TryParse(yearText, NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : 0;

                var links = _links.Select(row => ((string?)row.Url.Text, (string?)row.Label.Text)).ToList();
                var result = _session.SaveOwnNotedDate(_id ?? _newId, _title.Text, month, day, year, _note.Text, links, today);
                var errors = result.Errors;

                _titleError.Text = errors.GetValueOrDefault(NotedDateField.Title);
                _dateError.Text = errors.GetValueOrDefault(NotedDateField.Date);
                _yearError.Text = errors.GetValueOrDefault(NotedDateField.Year);
                _noteError.Text = errors.GetValueOrDefault(NotedDateField.Note);
                _linksError.Text = result.Links.CountError;

                for (var index = 0; index < _links.Count; index++)
                {
                    _links[index].Error.Text = result.Links.RowErrors.GetValueOrDefault(index);
                }

                if (result.Saved)
                {
                    saved = true;
                    _saved();
                    await Navigation.PopAsync();
                }
            }
            catch (Exception)
            {
                // A button handler: nothing here may end the app.
                _titleError.Text = "That could not be saved.";
            }
            finally
            {
                // Stays set once saved (the page is closing); a refused or failed save can be tried again.
                _saving = saved;
            }
        }

        /// <summary>
        /// Lays out the link rows (CH-48) in order, each with its address, optional label, move up / down and
        /// remove; the typed text stays with its row. Add is offered until there are ten.
        /// </summary>
        private void RebuildLinks()
        {
            // The rows' fields are reused: take them out of their old layouts before they are placed again.
            foreach (var old in _linkRows.Children.OfType<Layout>())
            {
                old.Children.Clear();
            }

            _linkRows.Children.Clear();

            for (var index = 0; index < _links.Count; index++)
            {
                var row = _links[index];
                var at = index;

                var up = SmallButton("Up", "Move link up");
                var down = SmallButton("Down", "Move link down");
                var remove = SmallButton("Remove", "Remove link");
                up.IsEnabled = at > 0;
                down.IsEnabled = at < _links.Count - 1;
                up.Clicked += (_, _) => Move(at, at - 1);
                down.Clicked += (_, _) => Move(at, at + 1);
                remove.Clicked += (_, _) =>
                {
                    _links.RemoveAt(at);
                    RebuildLinks();
                };

                var buttons = new HorizontalStackLayout { Spacing = 6, Children = { up, down, remove } };

                _linkRows.Add(new VerticalStackLayout
                {
                    Spacing = 2,
                    Children = { row.Url, row.Label, row.Error, buttons },
                });
            }

            _addLink.IsEnabled = _links.Count < ExternalLinkRules.MaxLinksPerOwner;
        }

        private void Move(int from, int to)
        {
            if (to < 0 || to >= _links.Count)
            {
                return;
            }

            (_links[from], _links[to]) = (_links[to], _links[from]);
            RebuildLinks();
        }

        private static Button SmallButton(string text, string description)
        {
            var button = new Button { Text = text, Padding = new Thickness(10, 0), HeightRequest = 36 };
            SemanticProperties.SetDescription(button, description);

            return button;
        }

        private static Label Error() => new Label { FontSize = 13, TextColor = Color.FromArgb("#B3261E") };

        /// <summary>One edited gallery link: its address, its optional label and its problem.</summary>
        private sealed class LinkRow
        {
            internal LinkRow(string? url, string? label)
            {
                Url.Text = url;
                Label.Text = label;
            }

            internal Entry Url { get; } = new()
            {
                Placeholder = "https://…",
                Keyboard = Keyboard.Url,

                // No MaxLength: Android would silently cut a longer paste, and a cut link usually still passes
                // the rules. The shared rule reports a link that is too long instead; the label likewise.
            };

            internal Entry Label { get; } = new()
            {
                Placeholder = "Label (optional)",
            };

            internal Label Error { get; } = NotedDatePage.Error();
        }
    }
}
