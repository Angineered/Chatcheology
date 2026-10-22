using System.Diagnostics;
using Chatcheology.Android.Controls;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Gallery;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// The Starred list (CH-38): starred messages and photos, newest first, each with its date, time,
    /// sender and a text snippet or a thumbnail. A message opens the chat at it; a photo opens full screen,
    /// swiping through the starred photos.
    /// </summary>
    /// <remarks>
    /// Read afresh each time the page appears (a star may have been changed in the viewer), with a reader
    /// of its own (<see cref="ArchiveSession.ReadStarredAsync"/>). Stars are kept in the app's settings,
    /// never in the archive.
    /// </remarks>
    internal sealed class StarredPage : ContentPage
    {
        private readonly AppState _state;
        private readonly ArchiveSession _session;
        private readonly ConversationPage _conversation;
        private readonly CollectionView _list = new() { SelectionMode = SelectionMode.Single };
        private readonly Label _status = new() { FontSize = 14, Margin = new Thickness(16) };
        private IReadOnlyList<StarredEntry> _entries = [];
        private bool _opening;

        internal StarredPage(AppState state, ArchiveSession session, ConversationPage conversation)
        {
            _state = state;
            _session = session;
            _conversation = conversation;

            Title = "Starred";
            _status.TextIn("MutedText");

            _list.ItemTemplate = new DataTemplate(() => new EntryView());
            _list.SelectionChanged += OnSelected;

            var layout = new Grid
            {
                RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
            };

            layout.Add(_status, 0, 0);
            layout.Add(_list, 0, 1);

            Content = layout;
        }

        /// <inheritdoc />
        protected override async void OnAppearing()
        {
            base.OnAppearing();

            _opening = false;
            _list.SelectedItem = null;

            var watch = Stopwatch.StartNew();

            try
            {
                _entries = await _session.ReadStarredAsync();
                AppState.LogTiming("starred", watch.Elapsed, $"entries={_entries.Count}");

                _status.Text = _entries.Count == 0
                    ? "Nothing is starred yet. Long-press a message, or tap the star on a photo, to star it."
                    : string.Empty;
                _status.IsVisible = _entries.Count == 0;
                _list.ItemsSource = _entries;
            }
            catch (Exception exception) when (exception is ArchiveFormatException or SqliteException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                _status.Text = "The starred items could not be read.";
                _status.IsVisible = true;
            }
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

                    await Navigation.PushAsync(new ImageViewerPage(
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
