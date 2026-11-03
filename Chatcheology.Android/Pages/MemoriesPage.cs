using System.Diagnostics;
using Chatcheology.Android.Controls;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Memories;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// The Memories list (CH-56): every memory the chat would show with every layer and filter on, newest
    /// first under year headings, whatever Settings says. Chips pick All, Memories (the personal collections)
    /// or one imported collection; the search box matches titles only. A tap opens the memory's details,
    /// read-only, with Show in chat when there is somewhere to go.
    /// </summary>
    /// <remarks>
    /// <para>Built once per session in the background from the memories read at open
    /// (<see cref="ArchiveSession.GetMemoryListAsync"/>); nothing is read from the archive here, and nothing is
    /// written to Settings. The chip and search are not stored: they reset when the page is opened again.</para>
    /// <para>The list is bound the first time only, and again on a chip or search change; Back from the details
    /// keeps the chip, the search and the scroll position.</para>
    /// </remarks>
    internal sealed class MemoriesPage : ContentPage
    {
        /// <summary>How long after the last keystroke the list is filtered again.</summary>
        private const int SearchDelayMilliseconds = 200;

        private readonly ArchiveSession _session;
        private readonly ConversationPage _conversation;
        private readonly SearchBar _query = new() { Placeholder = "Search memory titles" };
        private readonly HorizontalStackLayout _chips = new() { Spacing = 8, Padding = new Thickness(12, 4) };
        private readonly ScrollView _chipRow;
        private readonly CollectionView _list = new() { SelectionMode = SelectionMode.Single };
        private readonly Label _status = new() { FontSize = 14, Margin = new Thickness(16), HorizontalTextAlignment = TextAlignment.Center };
        private readonly ActivityIndicator _working = new() { IsVisible = false, IsRunning = false, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        private readonly List<(MemoryListChip Chip, Button Button)> _chipButtons = [];
        private MemoryList? _memories;
        private string _chip = MemoryList.AllChipId;
        private CancellationTokenSource? _typing;
        private bool _loading;
        private bool _opening;

        internal MemoriesPage(ArchiveSession session, ConversationPage conversation)
        {
            _session = session;
            _conversation = conversation;

            Title = "Memories";
            _status.TextIn("MutedText");
            _status.IsVisible = false;
            _query.IsVisible = false;

            _chipRow = new ScrollView { Orientation = ScrollOrientation.Horizontal, HorizontalScrollBarVisibility = ScrollBarVisibility.Never, Content = _chips, IsVisible = false };

            _list.ItemTemplate = new MemoryTemplates();
            _list.SelectionChanged += OnSelected;
            _query.TextChanged += (_, _) => _ = FilterSoonAsync();
            _query.SearchButtonPressed += (_, _) =>
            {
                // The search key filters now; the pending keystroke filter would bind (and scroll) again.
                _typing?.Cancel();
                _typing = null;
                Bind();
            };

            var layout = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star),
                },
            };

            layout.Add(_query, 0, 0);
            layout.Add(_chipRow, 0, 1);
            layout.Add(_status, 0, 2);
            layout.Add(_list, 0, 3);
            layout.Add(_working, 0, 3);

            Content = layout;
        }

        /// <inheritdoc />
        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // Back from the details: a second tap may open again, and the same row can be tapped again.
            _opening = false;
            _list.SelectedItem = null;

            if (_memories is not null || _loading)
            {
                return;
            }

            _loading = true;
            _working.IsVisible = _working.IsRunning = true;

            var watch = Stopwatch.StartNew();

            try
            {
                var memories = await _session.GetMemoryListAsync();
                AppState.LogTiming(
                    "memories",
                    watch.Elapsed,
                    $"rows={memories.Count} personal={memories.PersonalCount} imported={memories.ImportedCount} chips={memories.Chips.Count}");

                _memories = memories;
                ShowChips(memories);
                _query.IsVisible = !memories.IsEmpty;
                Bind();
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                // Nothing is read here, so only the session closing under the page can stop the build.
                _status.Text = MemoryList.NotReadText;
                _status.IsVisible = true;
            }
            finally
            {
                _loading = false;
                _working.IsVisible = _working.IsRunning = false;
            }
        }

        /// <summary>One button per chip, the chosen one filled; none when nothing is listed.</summary>
        private void ShowChips(MemoryList memories)
        {
            _chips.Children.Clear();
            _chipButtons.Clear();

            foreach (var chip in memories.Chips)
            {
                var button = new Button { Text = chip.Name, Padding = new Thickness(14, 0), HeightRequest = 36, CornerRadius = 18 };

                button.Clicked += (_, _) =>
                {
                    if (_chip == chip.Id)
                    {
                        return;
                    }

                    _chip = chip.Id;
                    MarkChosenChip();
                    Bind();
                };

                _chipButtons.Add((chip, button));
                _chips.Children.Add(button);
            }

            _chipRow.IsVisible = _chipButtons.Count > 0;
            MarkChosenChip();
        }

        private void MarkChosenChip()
        {
            foreach (var (chip, button) in _chipButtons)
            {
                var chosen = chip.Id == _chip;

                button.Themed(BackgroundColorProperty, chosen ? "Primary" : "ButtonBackground");

                if (chosen)
                {
                    button.SetAppThemeColor(Button.TextColorProperty, Colors.White, Colors.White);
                }
                else
                {
                    button.Themed(Button.TextColorProperty, "BodyText");
                }

                button.FontAttributes = chosen ? FontAttributes.Bold : FontAttributes.None;
                SemanticProperties.SetDescription(button, chosen ? chip.Name + ", chosen" : "Show " + chip.Name);
            }
        }

        /// <summary>Filters again 200 ms after the last keystroke.</summary>
        private async Task FilterSoonAsync()
        {
            _typing?.Cancel();
            var typing = _typing = new CancellationTokenSource();

            try
            {
                await Task.Delay(SearchDelayMilliseconds, typing.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ReferenceEquals(_typing, typing))
            {
                Bind();
            }
        }

        /// <summary>The rows for the chosen chip and search, or the empty line.</summary>
        private void Bind()
        {
            if (_memories is not { } memories)
            {
                return;
            }

            var rows = memories.Rows(_chip, _query.Text);
            var empty = memories.EmptyText(rows);

            _status.Text = empty;
            _status.IsVisible = empty.Length > 0;
            _list.ItemsSource = rows;
        }

        private async void OnSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is not MemoryListItem item)
            {
                // A year heading opens nothing and must not stay selected.
                if (e.CurrentSelection.Count > 0)
                {
                    _list.SelectedItem = null;
                }

                return;
            }

            if (_opening)
            {
                return;
            }

            _opening = true;

            try
            {
                await _conversation.OpenMemoryDetailsAsync(item.Key);
            }
            catch (Exception)
            {
                // The conversation reports its own failures; nothing else may end the app.
            }

            // Nothing was opened (the page is still on top): the row can be tapped again.
            if (Navigation.NavigationStack.LastOrDefault() == this)
            {
                _opening = false;
                _list.SelectedItem = null;
            }
        }

        /// <summary>Picks a row's template: a year heading or a memory.</summary>
        private sealed class MemoryTemplates : DataTemplateSelector
        {
            private readonly DataTemplate _year = new(() =>
            {
                var label = new Label { FontSize = 15, FontAttributes = FontAttributes.Bold, Padding = new Thickness(16, 14, 16, 4) };
                label.SetBinding(Label.TextProperty, nameof(MemoryListYearRow.Text));
                return label;
            });

            private readonly DataTemplate _memory = new(() => new MemoryItemView());

            protected override DataTemplate OnSelectTemplate(object item, BindableObject container) =>
                item is MemoryListYearRow ? _year : _memory;
        }

        /// <summary>
        /// One memory: the cover thumbnail when there is one (hidden otherwise, so every memory row recycles as
        /// one view), the title, the plain line and the muted line.
        /// </summary>
        private sealed class MemoryItemView : ContentView
        {
            private const double Thumbnail = 48;

            private readonly DecodedImage _image = new() { WidthRequest = Thumbnail, HeightRequest = Thumbnail, FailedText = string.Empty, VerticalOptions = LayoutOptions.Center };
            private readonly Label _title = new() { FontSize = 15, FontAttributes = FontAttributes.Bold, MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation };
            private readonly Label _line = new() { FontSize = 13 };
            private readonly Label _muted = new() { FontSize = 12 };
            private readonly Grid _grid;

            public MemoryItemView()
            {
                _muted.TextIn("MutedText");

                var text = new VerticalStackLayout
                {
                    Spacing = 1,
                    VerticalOptions = LayoutOptions.Center,
                    Children = { _title, _line, _muted },
                };

                _grid = new Grid
                {
                    Padding = new Thickness(16, 8),
                    ColumnSpacing = 12,
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
                };

                _grid.Add(_image, 0);
                _grid.Add(text, 1);

                Content = _grid;
            }

            protected override void OnBindingContextChanged()
            {
                base.OnBindingContextChanged();

                if (BindingContext is not MemoryListItem item)
                {
                    _image.Path = null;
                    return;
                }

                _title.Text = item.Title;
                _line.Text = item.Line;
                _line.IsVisible = item.HasLine;
                _muted.Text = item.MutedLine;
                _muted.IsVisible = item.HasMutedLine;
                _image.IsVisible = item.HasImage;
                _image.Path = item.ImagePath;
                _grid.ColumnSpacing = item.HasImage ? 12 : 0;
            }
        }
    }
}
