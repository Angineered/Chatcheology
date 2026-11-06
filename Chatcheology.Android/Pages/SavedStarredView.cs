using System.Diagnostics;
using Chatcheology.Android.Controls;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Gallery;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// The Starred tab of the Saved screen (CH-38, CH-59): starred messages and photos, newest first, each with its
    /// date, time, sender and a text snippet or a thumbnail. A message opens the chat at it; a photo opens full
    /// screen, swiping through the starred photos.
    /// </summary>
    /// <remarks>
    /// Read afresh each time the tab is chosen and each time the screen reappears with it chosen (a star may have
    /// been changed in the viewer), with a reader of its own (<see cref="ArchiveSession.ReadStarredAsync"/>); the
    /// first visible row is kept (<see cref="SavedTabState"/>), best effort when it was unstarred. Stars are kept in
    /// the app's settings, never in the archive.
    /// </remarks>
    internal sealed class SavedStarredView : ContentView
    {
        private readonly AppState _state;
        private readonly ArchiveSession _session;
        private readonly ConversationPage _conversation;
        private readonly Page _page;
        private readonly CollectionView _list = new() { SelectionMode = SelectionMode.Single };
        private readonly Label _status = new() { FontSize = 14, Margin = new Thickness(16), HorizontalTextAlignment = TextAlignment.Center };
        private IReadOnlyList<StarredEntry> _entries = [];
        private SavedTabState? _shown;
        private int _first;
        private int _reading;
        private bool _opening;

        internal SavedStarredView(AppState state, ArchiveSession session, ConversationPage conversation, Page page)
        {
            _state = state;
            _session = session;
            _conversation = conversation;
            _page = page;

            _status.TextIn("MutedText");
            _status.IsVisible = false;

            _list.ItemTemplate = new DataTemplate(() => new EntryView());
            _list.SelectionChanged += OnSelected;
            _list.Scrolled += (_, e) => _first = Math.Max(0, e.FirstVisibleItemIndex);

            var layout = new Grid
            {
                RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
            };

            layout.Add(_status, 0, 0);
            layout.Add(_list, 0, 1);

            Content = layout;
        }

        /// <summary>The screen is back on top (from the chat's viewer, or the photo viewer): a row can be opened again.</summary>
        internal void Reopened()
        {
            _opening = false;
            _list.SelectedItem = null;
        }

        /// <summary>Keeps the first visible row in <see cref="_shown"/>, when the tab is shown.</summary>
        internal void Remember()
        {
            if (_shown is { } state && _entries.Count > 0)
            {
                var index = Math.Min(_first, _entries.Count - 1);
                state.Remember(SavedTabs.KeyOf(_entries[index]), index);
            }
        }

        /// <summary>Reads the stars again and shows them at the row <paramref name="state"/> keeps.</summary>
        internal async Task ShowAsync(SavedTabState state)
        {
            _shown = state;
            var reading = ++_reading;
            var watch = Stopwatch.StartNew();

            try
            {
                var entries = await _session.ReadStarredAsync();

                // A newer read, or another tab chosen meanwhile, wins.
                if (reading != _reading || !ReferenceEquals(_shown, state))
                {
                    return;
                }

                AppState.LogTiming("starred", watch.Elapsed, $"entries={entries.Count} tab=starred");

                _entries = entries;
                _status.Text = entries.Count == 0 ? SavedTabs.NoStarsText : string.Empty;
                _status.IsVisible = entries.Count == 0;
                _list.ItemsSource = entries;

                var index = state.RestoreIndex(entries.Select(SavedTabs.KeyOf).ToList());
                _first = Math.Max(0, index);

                if (index > 0)
                {
                    Dispatcher.Dispatch(() => _list.ScrollTo(index, position: ScrollToPosition.Start, animate: false));
                }
            }
            catch (Exception exception) when (exception is ArchiveFormatException or SqliteException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                if (reading == _reading)
                {
                    _status.Text = SavedTabs.StarsNotReadText;
                    _status.IsVisible = true;
                }
            }
        }

        /// <summary>The tab is left: its row is kept, and a read still under way is not shown.</summary>
        internal void Hide()
        {
            Remember();
            _shown = null;
            _reading++;
        }

        private async void OnSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (_opening || e.CurrentSelection.FirstOrDefault() is not StarredEntry entry)
            {
                return;
            }

            _opening = true;

            try
            {
                if (entry.IsPhoto && entry.Thumbnail is { } photo)
                {
                    var photos = StarredList.Photos(_entries);
                    var index = Math.Max(0, photos.ToList().IndexOf(photo));

                    await _page.Navigation.PushAsync(new ImageViewerPage(
                        _state,
                        new PhotoBrowser(photos, index),
                        _session.Stars,
                        card => _conversation.ShowMessageAsync(card.SequenceNumber)));
                    return;
                }

                await _conversation.ShowMessageAsync(entry.SequenceNumber);
            }
            catch (Exception)
            {
                // The conversation reports its own read failures; nothing else may end the app.
            }
        }

        /// <summary>One entry: a small thumbnail when there is a photo, then date, time and sender, and the snippet.</summary>
        private sealed class EntryView : ContentView
        {
            private const double Thumbnail = 56;

            private readonly DecodedImage _image = new() { WidthRequest = Thumbnail, HeightRequest = Thumbnail, FailedText = "⊘" };
            private readonly Label _header = new() { FontSize = 12 };
            private readonly Label _snippet = new() { FontSize = 15, MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation };
            private readonly Label _star = new() { Text = "★", FontSize = 14 };

            public EntryView()
            {
                _header.TextIn("MutedText");
                _star.TextIn("Star");

                var text = new VerticalStackLayout
                {
                    Spacing = 2,
                    VerticalOptions = LayoutOptions.Center,
                    Children = { new HorizontalStackLayout { Spacing = 6, Children = { _star, _header } }, _snippet },
                };

                var grid = new Grid
                {
                    Padding = new Thickness(16, 8),
                    ColumnSpacing = 12,
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
                };

                grid.Add(_image, 0);
                grid.Add(text, 1);

                Content = grid;
            }

            protected override void OnBindingContextChanged()
            {
                base.OnBindingContextChanged();

                if (BindingContext is not StarredEntry entry)
                {
                    _image.Path = null;
                    return;
                }

                _header.Text = $"{entry.DateText} · {entry.TimeText} · {entry.SenderText}";
                _snippet.Text = entry.IsPhoto ? "Photo" : entry.Snippet;
                _image.IsVisible = entry.Thumbnail is not null;
                _image.Path = entry.Thumbnail?.FullPath;
            }
        }
    }
}
