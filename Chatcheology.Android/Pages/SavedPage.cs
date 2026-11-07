using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// The Saved screen (CH-59), opened from the bookmark icon: a row of tabs, Starred | Memories | one per imported
    /// collection (<see cref="SavedTabs"/>), over the chosen tab's list. A tap switches tabs (no swipe: swiping
    /// belongs to the photo viewer); the chosen tab is underlined in the accent colour.
    /// </summary>
    /// <remarks>
    /// <para>Opens on the tab last chosen for this open archive. Each tab keeps its first visible row and search while
    /// the screen is open, so Back from a memory's details or the photo viewer finds it as it was; Back from here
    /// returns to the chat, and Show in chat from any tab closes the screen and jumps.</para>
    /// <para>The Starred tab is read again when chosen and when the screen reappears with it chosen (a star can change
    /// in the photo viewer); the memory tabs share one list, built once per session and again after an own memory
    /// changed (CH-54).</para>
    /// <para>On the Memories tab a "+" (Add a memory, CH-54) opens the form for a memory of the phone owner's own,
    /// starting today.</para>
    /// </remarks>
    internal sealed class SavedPage : ContentPage
    {
        /// <summary>The smallest touch target, in dp.</summary>
        private const double TabHeight = 48;

        private readonly SavedTabs _tabs;
        private readonly SavedStarredView _starred;
        private readonly SavedMemoriesView _memories;
        private readonly ScrollView _tabScroll;
        private readonly List<(SavedTab Tab, Button Button, BoxView Underline, View Holder)> _tabViews = [];
        private readonly ToolbarItem? _add;
        private bool _appeared;

        internal SavedPage(AppState state, ArchiveSession session, ConversationPage conversation)
        {
            Title = "Saved";

            if (session.HasOwnStore)
            {
                _add = new ToolbarItem("Add a memory", "toolbar_add.png", () => _ = conversation.AddOwnMemoryAsync(OnThisDay.TodayFrom(TimeProvider.System)));
            }

            _tabs = session.CreateSavedTabs();
            _starred = new SavedStarredView(state, session, conversation, this) { IsVisible = false };
            _memories = new SavedMemoriesView(session, conversation, this) { IsVisible = false };

            var row = new HorizontalStackLayout { Padding = new Thickness(4, 0) };

            foreach (var tab in _tabs.Tabs)
            {
                var button = new Button
                {
                    Text = tab.Name,
                    FontSize = 15,
                    Padding = new Thickness(16, 0),
                    HeightRequest = TabHeight,
                    MinimumHeightRequest = TabHeight,
                    MinimumWidthRequest = TabHeight,
                    CornerRadius = 0,
                    BorderWidth = 0,
                    BackgroundColor = Colors.Transparent,
                };

                var underline = new BoxView { HeightRequest = 3, VerticalOptions = LayoutOptions.End, Margin = new Thickness(8, 0) };
                underline.Themed(BoxView.ColorProperty, "SenderText");

                var holder = new Grid { Children = { button, underline } };

                button.Clicked += (_, _) => _ = ChooseAsync(tab);

                _tabViews.Add((tab, button, underline, holder));
                row.Children.Add(holder);
            }

            _tabScroll = new ScrollView
            {
                Orientation = ScrollOrientation.Horizontal,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
                Content = row,
            };

            var divider = new BoxView { HeightRequest = 1, Opacity = 0.3 };
            divider.Themed(BoxView.ColorProperty, "MutedText");

            var layout = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star),
                },
            };

            layout.Add(_tabScroll, 0, 0);
            layout.Add(divider, 0, 1);
            layout.Add(_starred, 0, 2);
            layout.Add(_memories, 0, 2);

            Content = layout;
            MarkChosen();
        }

        /// <inheritdoc />
        protected override async void OnAppearing()
        {
            base.OnAppearing();

            _starred.Reopened();
            _memories.Reopened();

            try
            {
                if (!_appeared)
                {
                    _appeared = true;
                    await ShowChosenAsync();
                    return;
                }

                // Back from the photo viewer or a memory's details: a star may have changed there.
                if (_tabs.Selected.Kind == SavedTabKind.Starred)
                {
                    _starred.Remember();
                    await _starred.ShowAsync(_tabs.StateOf(_tabs.Selected));
                }
                else if (_memories.IsStale)
                {
                    // CH-54: an own memory was added, edited or deleted meanwhile.
                    await _memories.ReloadAsync();
                }
            }
            catch (Exception)
            {
                // Each tab reports its own read failures; nothing else may end the app.
            }
        }

        private async Task ChooseAsync(SavedTab tab)
        {
            var leaving = _tabs.Selected;

            if (!_tabs.Select(tab.Id))
            {
                return;
            }

            if (leaving.Kind == SavedTabKind.Starred)
            {
                _starred.Hide();
            }
            else
            {
                _memories.Hide();
            }

            MarkChosen();

            try
            {
                await ShowChosenAsync();
            }
            catch (Exception)
            {
                // Each tab reports its own read failures; nothing else may end the app.
            }
        }

        private async Task ShowChosenAsync()
        {
            var tab = _tabs.Selected;
            var starred = tab.Kind == SavedTabKind.Starred;

            _starred.IsVisible = starred;
            _memories.IsVisible = !starred;

            if (_tabViews.FirstOrDefault(view => view.Tab == tab) is { Holder: { } holder })
            {
                // A wide row keeps the chosen tab in view.
                Dispatcher.Dispatch(() => _ = _tabScroll.ScrollToAsync(holder, ScrollToPosition.MakeVisible, animated: false));
            }

            if (starred)
            {
                await _starred.ShowAsync(_tabs.StateOf(tab));
            }
            else
            {
                await _memories.ShowAsync(tab, _tabs.StateOf(tab));
            }
        }

        /// <summary>The chosen tab underlined, bold and in the accent colour; screen readers hear which is chosen.</summary>
        private void MarkChosen()
        {
            // "+" belongs to the Memories tab only.
            if (_add is not null)
            {
                var memories = _tabs.Selected.Kind == SavedTabKind.Memories;

                if (memories && !ToolbarItems.Contains(_add))
                {
                    ToolbarItems.Add(_add);
                }
                else if (!memories)
                {
                    ToolbarItems.Remove(_add);
                }
            }

            foreach (var (tab, button, underline, _) in _tabViews)
            {
                var chosen = tab == _tabs.Selected;

                underline.IsVisible = chosen;
                button.FontAttributes = chosen ? FontAttributes.Bold : FontAttributes.None;
                button.Themed(Button.TextColorProperty, chosen ? "SenderText" : "MutedText");
                SemanticProperties.SetDescription(button, tab.ScreenReaderName(chosen));
            }
        }
    }
}
