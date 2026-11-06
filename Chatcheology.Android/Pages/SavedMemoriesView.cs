using System.Collections.ObjectModel;
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
    /// <para>An imported collection's tab has a Date | Title switch (CH-59 step 3): Title lists one heading per memory
    /// (<see cref="MemoryTitleGroups"/>), closed at first; a tap opens or closes it in place. The switch and the open
    /// headings are kept in the tab's <see cref="SavedTabState"/> while the screen is open, never in Settings.</para>
    /// </remarks>
    internal sealed class SavedMemoriesView : ContentView
    {
        /// <summary>How long after the last keystroke the list is filtered again.</summary>
        private const int SearchDelayMilliseconds = 200;

        /// <summary>The smallest touch target, in dp.</summary>
        private const double TouchHeight = 48;

        /// <summary>Where a memory heading's title starts, in dp; the rows under it start there too.</summary>
        private const double HeadingTextStart = 48;

        private readonly MemoryTemplates _templates = new();

        private readonly ArchiveSession _session;
        private readonly ConversationPage _conversation;
        private readonly Page _page;
        private readonly Button _byDate = GroupButton(MemoryListGrouping.Date);
        private readonly Button _byTitle = GroupButton(MemoryListGrouping.Title);
        private readonly HorizontalStackLayout _groupBar;
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

            var groupLabel = new Label { Text = "Group by", FontSize = 13, VerticalOptions = LayoutOptions.Center, Margin = new Thickness(0, 0, 4, 0) };
            groupLabel.TextIn("MutedText");

            _groupBar = new HorizontalStackLayout
            {
                Spacing = 8,
                Padding = new Thickness(16, 4, 16, 0),
                IsVisible = false,
                Children = { groupLabel, _byDate, _byTitle },
            };

            _byDate.Clicked += (_, _) => ChooseGrouping(MemoryListGrouping.Date);
            _byTitle.Clicked += (_, _) => ChooseGrouping(MemoryListGrouping.Title);

            _list.ItemTemplate = _templates;
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
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star),
                },
            };

            layout.Add(_groupBar, 0, 0);
            layout.Add(_query, 0, 1);
            layout.Add(_status, 0, 2);
            layout.Add(_list, 0, 3);
            layout.Add(_working, 0, 3);

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
            LogGroups();
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
            if (_memories is not { } memories || _tab is not { } tab || _state is not { } state)
            {
                return;
            }

            var grouped = IsGroupedByTitle(tab, state);
            var rows = grouped
                ? MemoryTitleGroups.Rows(memories, tab.Id, _query.Text, state.Expanded)
                : memories.Rows(tab.Id, _query.Text);
            var empty = memories.EmptyText(tab.Id, rows);
            var scrollTo = Math.Max(0, restore?.RestoreIndex(rows.Select(SavedTabs.KeyOf).ToList()) ?? 0);
            var listed = memories.CountIn(tab.Id) > 0;

            // Grouped rows change in place when a heading opens or closes, so the scroll position stays.
            _rows = grouped ? new ObservableCollection<MemoryListRow>(rows) : rows;
            _first = Math.Max(0, scrollTo);
            _query.IsVisible = listed;
            _groupBar.IsVisible = listed && tab.Kind == SavedTabKind.Collection;
            MarkGrouping(state.GroupBy);
            _status.Text = empty;
            _status.IsVisible = empty.Length > 0;
            _templates.Grouped = grouped;
            _list.ItemsSource = _rows;

            if (scrollTo > 0)
            {
                Dispatcher.Dispatch(() => _list.ScrollTo(scrollTo, position: ScrollToPosition.Start, animate: false));
            }
        }

        private static bool IsGroupedByTitle(SavedTab tab, SavedTabState state) =>
            tab.Kind == SavedTabKind.Collection && state.GroupBy == MemoryListGrouping.Title;

        private static Button GroupButton(MemoryListGrouping grouping) => new()
        {
            Text = MemoryTitleGroups.SwitchText(grouping),
            FontSize = 14,
            Padding = new Thickness(16, 0),
            HeightRequest = TouchHeight,
            MinimumHeightRequest = TouchHeight,
            MinimumWidthRequest = TouchHeight,
            CornerRadius = 18,
            BorderWidth = 1,
            BackgroundColor = Colors.Transparent,
        };

        /// <summary>The chosen side of the switch bold and in the accent colour; screen readers hear which is chosen.</summary>
        private void MarkGrouping(MemoryListGrouping chosen)
        {
            foreach (var (button, grouping) in new[] { (_byDate, MemoryListGrouping.Date), (_byTitle, MemoryListGrouping.Title) })
            {
                var selected = grouping == chosen;

                button.FontAttributes = selected ? FontAttributes.Bold : FontAttributes.None;
                button.Themed(Button.TextColorProperty, selected ? "SenderText" : "MutedText");
                button.Themed(Button.BorderColorProperty, selected ? "SenderText" : "MutedText");
                SemanticProperties.SetDescription(button, MemoryTitleGroups.SwitchScreenReaderName(grouping, selected));
            }
        }

        /// <summary>Date or Title chosen on a collection tab: its rows from the top, the open headings kept.</summary>
        private void ChooseGrouping(MemoryListGrouping grouping)
        {
            if (_state is not { } state || _tab is not { Kind: SavedTabKind.Collection })
            {
                return;
            }

            state.Search = _query.Text ?? string.Empty;

            if (!state.SetGroupBy(grouping))
            {
                return;
            }

            Bind(restore: null);
            LogGroups();
        }

        /// <summary>Release log for Title: counts only, never a title.</summary>
        private void LogGroups()
        {
            if (_tab is { } tab && _state is { } state && IsGroupedByTitle(tab, state))
            {
                AppState.LogTiming(
                    "memories-title",
                    TimeSpan.Zero,
                    $"groups={_rows.OfType<MemoryListTitleRow>().Count()} open={state.Expanded.Count} tab={tab.LogName}");
            }
        }

        /// <summary>Opens or closes a memory's heading in place: its rows inserted after it, or taken away.</summary>
        private void ToggleHeading(MemoryListTitleRow heading)
        {
            if (_memories is not { } memories || _tab is not { } tab || _state is not { } state
                || _rows is not ObservableCollection<MemoryListRow> rows)
            {
                return;
            }

            var index = -1;

            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i] is MemoryListTitleRow row && string.Equals(row.Title, heading.Title, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                return;
            }

            var open = state.Toggle(heading.Title);
            rows[index] = heading with { IsExpanded = open };

            if (open)
            {
                var items = MemoryTitleGroups.Groups(memories, tab.Id, _query.Text)
                    .FirstOrDefault(group => string.Equals(group.Row.Title, heading.Title, StringComparison.Ordinal)).Items ?? [];

                for (var i = 0; i < items.Count; i++)
                {
                    rows.Insert(index + 1 + i, items[i]);
                }
            }
            else
            {
                // The heading's rows follow it up to the next heading.
                while (index + 1 < rows.Count && rows[index + 1] is MemoryListItem)
                {
                    rows.RemoveAt(index + 1);
                }
            }
        }

        private async void OnSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (e.CurrentSelection.FirstOrDefault() is MemoryListTitleRow heading)
            {
                // A memory's heading opens or closes in place and must not stay selected.
                _list.SelectedItem = null;
                ToggleHeading(heading);
                return;
            }

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

        /// <summary>
        /// Picks a row's template: a year heading, a memory's heading (grouped by title), or a memory (indented under
        /// its heading when grouped).
        /// </summary>
        private sealed class MemoryTemplates : DataTemplateSelector
        {
            /// <summary>Whether the bound rows are grouped by title.</summary>
            internal bool Grouped { get; set; }

            private readonly DataTemplate _year = new(() =>
            {
                var label = new Label { FontSize = 15, FontAttributes = FontAttributes.Bold, Padding = new Thickness(16, 14, 16, 4) };
                label.SetBinding(Label.TextProperty, nameof(MemoryListYearRow.Text));
                return label;
            });

            private readonly DataTemplate _memory = new(() => new MemoryItemView(indent: 0));

            private readonly DataTemplate _indented = new(() => new MemoryItemView(indent: HeadingTextStart));

            private readonly DataTemplate _title = new(() => new TitleHeadingView());

            protected override DataTemplate OnSelectTemplate(object item, BindableObject container) => item switch
            {
                MemoryListYearRow => _year,
                MemoryListTitleRow => _title,
                _ => Grouped ? _indented : _memory,
            };
        }

        /// <summary>
        /// One memory's heading when grouped by title: an open or closed mark, the title and "N sessions · Mon YYYY -
        /// Mon YYYY". At least 48 dp tall; screen readers hear the title, the line and expanded or collapsed.
        /// </summary>
        private sealed class TitleHeadingView : ContentView
        {
            private readonly Label _mark = new() { FontSize = 16, VerticalOptions = LayoutOptions.Center, WidthRequest = HeadingTextStart - 16 - 8 };
            private readonly Label _title = new() { FontSize = 15, FontAttributes = FontAttributes.Bold, MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation };
            private readonly Label _summary = new() { FontSize = 13 };

            public TitleHeadingView()
            {
                _summary.TextIn("MutedText");

                // The mark is drawn only; the heading's own description says expanded or collapsed.
                AutomationProperties.SetIsInAccessibleTree(_mark, false);

                var text = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center, Children = { _title, _summary } };

                var grid = new Grid
                {
                    Padding = new Thickness(16, 8),
                    ColumnSpacing = 8,
                    MinimumHeightRequest = TouchHeight,
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
                };

                grid.Add(_mark, 0);
                grid.Add(text, 1);

                Content = grid;
            }

            protected override void OnBindingContextChanged()
            {
                base.OnBindingContextChanged();

                if (BindingContext is not MemoryListTitleRow heading)
                {
                    return;
                }

                _mark.Text = heading.Mark;
                _title.Text = heading.Title;
                _summary.Text = heading.Summary;
                SemanticProperties.SetDescription(this, heading.ScreenReaderText);
            }
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

            public MemoryItemView(double indent)
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
                    Padding = new Thickness(Math.Max(16, indent), 8, 16, 8),
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
