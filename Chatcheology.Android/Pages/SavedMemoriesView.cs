using System.Diagnostics;
using Chatcheology.Android.Controls;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Memories;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// The Memories tab and each imported collection's tab of the Saved screen (CH-56, CH-59): every memory the chat
    /// would show with every layer and filter on, newest first under year headings, whatever Settings says. The
    /// search box matches titles only. A tap opens the memory's details, read-only, with Show in chat when there is
    /// somewhere to go.
    /// </summary>
    /// <remarks>
    /// <para>Built once per session in the background from the memories read at open
    /// (<see cref="ArchiveSession.GetMemoryListAsync"/>); nothing is read from the archive here, and nothing is
    /// written to Settings. One view serves every memory tab: each tab's search and first visible row are kept in its
    /// <see cref="SavedTabState"/> while the screen is open.</para>
    /// <para>The rows are bound when a tab is shown and on a search change only; Back from the details keeps the
    /// tab, the search and the scroll position.</para>
    /// </remarks>
    internal sealed class SavedMemoriesView : ContentView
    {
        /// <summary>How long after the last keystroke the list is filtered again.</summary>
        private const int SearchDelayMilliseconds = 200;

        private readonly ArchiveSession _session;
        private readonly ConversationPage _conversation;
        private readonly Page _page;
        private readonly SearchBar _query = new() { Placeholder = "Search memory titles" };
        private readonly CollectionView _list = new() { SelectionMode = SelectionMode.Single };
        private readonly Label _status = new() { FontSize = 14, Margin = new Thickness(16), HorizontalTextAlignment = TextAlignment.Center };
        private readonly ActivityIndicator _working = new() { IsVisible = false, IsRunning = false, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        private MemoryList? _memories;
        private SavedTab? _tab;
        private SavedTabState? _state;
        private IReadOnlyList<MemoryListRow> _rows = [];
        private CancellationTokenSource? _typing;
        private int _first;
        private bool _settingQuery;
        private bool _opening;

        internal SavedMemoriesView(ArchiveSession session, ConversationPage conversation, Page page)
        {
            _session = session;
            _conversation = conversation;
            _page = page;

            _status.TextIn("MutedText");
            _status.IsVisible = false;
            _query.IsVisible = false;

            _list.ItemTemplate = new MemoryTemplates();
            _list.SelectionChanged += OnSelected;
            _list.Scrolled += (_, e) => _first = Math.Max(0, e.FirstVisibleItemIndex);
            _query.TextChanged += (_, _) =>
            {
                if (!_settingQuery)
                {
                    _ = FilterSoonAsync();
                }
            };
            _query.SearchButtonPressed += (_, _) =>
            {
                // The search key filters now; the pending keystroke filter would bind (and scroll) again.
                _typing?.Cancel();
                _typing = null;
                BindSearch();
            };

            var layout = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star),
                },
            };

            layout.Add(_query, 0, 0);
            layout.Add(_status, 0, 1);
            layout.Add(_list, 0, 2);
            layout.Add(_working, 0, 2);

            Content = layout;
        }

        /// <summary>The screen is back on top (from a memory's details): a row can be opened again.</summary>
        internal void Reopened()
        {
            _opening = false;
            _list.SelectedItem = null;
        }

        /// <summary>
        /// Shows <paramref name="tab"/> with the search and first visible row <paramref name="state"/> keeps; the list is
        /// built the first time.
        /// </summary>
        internal async Task ShowAsync(SavedTab tab, SavedTabState state)
        {
            _tab = tab;
            _state = state;
            _typing?.Cancel();
            _typing = null;

            SetQuery(state.Search);

            if (_memories is null)
            {
                _rows = [];
                _list.ItemsSource = _rows;
                _status.IsVisible = false;
                _query.IsVisible = false;
                _working.IsVisible = _working.IsRunning = true;

                var watch = Stopwatch.StartNew();

                try
                {
                    var memories = await _session.GetMemoryListAsync();
                    AppState.LogTiming(
                        "memories",
                        watch.Elapsed,
                        $"rows={memories.Count} personal={memories.PersonalCount} imported={memories.ImportedCount} tab={tab.LogName}");

                    _memories = memories;
                }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
                {
                    // Nothing is read here, so only the session closing under the page can stop the build.
                    _status.Text = MemoryList.NotReadText;
                    _status.IsVisible = true;
                    return;
                }
                finally
                {
                    _working.IsVisible = _working.IsRunning = false;
                }

                // Another tab was chosen while the list was built: that one is shown instead.
                if (!ReferenceEquals(_state, state))
                {
                    return;
                }
            }

            Bind(state);
        }

        /// <summary>The tab is left: its search and first visible row are kept.</summary>
        internal void Hide()
        {
            _typing?.Cancel();
            _typing = null;
            Remember();
            _state = null;
            _tab = null;
        }

        private void Remember()
        {
            if (_state is not { } state)
            {
                return;
            }

            state.Search = _query.Text ?? string.Empty;

            if (_rows.Count > 0)
            {
                var index = Math.Min(_first, _rows.Count - 1);
                state.Remember(SavedTabs.KeyOf(_rows[index]), index);
            }
            else
            {
                state.Remember(null, 0);
            }
        }

        private void SetQuery(string text)
        {
            _settingQuery = true;

            try
            {
                _query.Text = text;
            }
            finally
            {
                _settingQuery = false;
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
                BindSearch();
            }
        }

        /// <summary>A new search: its rows from the top.</summary>
        private void BindSearch()
        {
            if (_state is { } state)
            {
                state.Search = _query.Text ?? string.Empty;
                state.Remember(null, 0);
            }

            Bind(restore: null);
        }

        /// <summary>
        /// The rows for the shown tab and search, or its empty line: at the row <paramref name="restore"/> keeps, or
        /// from the top.
        /// </summary>
        private void Bind(SavedTabState? restore)
        {
            if (_memories is not { } memories || _tab is not { } tab)
            {
                return;
            }

            var rows = memories.Rows(tab.Id, _query.Text);
            var empty = memories.EmptyText(tab.Id, rows);
            var scrollTo = Math.Max(0, restore?.RestoreIndex(rows.Select(SavedTabs.KeyOf).ToList()) ?? 0);

            _rows = rows;
            _first = Math.Max(0, scrollTo);
            _query.IsVisible = memories.CountIn(tab.Id) > 0;
            _status.Text = empty;
            _status.IsVisible = empty.Length > 0;
            _list.ItemsSource = rows;

            if (scrollTo > 0)
            {
                Dispatcher.Dispatch(() => _list.ScrollTo(scrollTo, position: ScrollToPosition.Start, animate: false));
            }
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

            // Nothing was opened (the screen is still on top): the row can be tapped again.
            if (_page.Navigation.NavigationStack.LastOrDefault() == _page)
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
