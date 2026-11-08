using System.Collections.ObjectModel;
using System.Diagnostics;
using Chatcheology.Android.Controls;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Gallery;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// The photo gallery (CH-37): every restored photo, newest first, under month headings, three to a
    /// row as square thumbnails. A tap opens the photo full screen, where a swipe moves through the gallery
    /// and Show in chat opens the conversation at its message.
    /// </summary>
    /// <remarks>
    /// <para>Loaded in the background the first time it opens, newest months first, with a reader of its own
    /// (<see cref="ArchiveSession.GetGalleryLoad"/>, CH-66), and kept for the session. The newest batch is
    /// shown as soon as it is in; older months are added below it as they come, so a list read near the top
    /// does not move. Thumbnails are decoded at cell size through the chat's own decoder and cache.</para>
    /// <para>A photo tapped before every batch is in opens a viewer over the photos shown at that moment; once
    /// the gallery is complete the viewer has all of it.</para>
    /// </remarks>
    internal sealed class GalleryPage : ContentPage
    {
        /// <summary>Photos per row.</summary>
        internal const int Columns = 3;

        /// <summary>The gap between cells, in device-independent units.</summary>
        internal const double Gap = 2;

        private readonly AppState _state;
        private readonly ArchiveSession _session;
        private readonly ConversationPage _conversation;
        private readonly CollectionView _list = new() { SelectionMode = SelectionMode.None };
        private readonly Label _status = new() { FontSize = 14, Margin = new Thickness(16), HorizontalTextAlignment = TextAlignment.Center };
        private readonly ActivityIndicator _working = new() { IsVisible = false, IsRunning = false, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        private readonly CancellationTokenSource _closing = new();

        // The batches shown, in order, and their photos; changed only on the UI thread.
        private readonly List<GalleryBatch> _batches = [];
        private readonly List<AttachmentCard> _photos = [];
        private ObservableCollection<GalleryRow>? _rows;
        private bool _complete;
        private bool _started;
        private bool _closed;
        private double _cellSize;
        private bool _opening;

        internal GalleryPage(AppState state, ArchiveSession session, ConversationPage conversation)
        {
            _state = state;
            _session = session;
            _conversation = conversation;

            Title = "Photos";
            _status.TextIn("MutedText");

            _list.ItemTemplate = new GalleryTemplates(this);

            var layout = new Grid
            {
                RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
            };

            layout.Add(_status, 0, 0);
            layout.Add(_list, 0, 1);
            layout.Add(_working, 0, 1);

            Content = layout;
        }

        /// <summary>The side of one square cell, from the page's width.</summary>
        internal double CellSize => _cellSize;

        /// <inheritdoc />
        protected override void OnSizeAllocated(double width, double height)
        {
            base.OnSizeAllocated(width, height);

            var size = Math.Floor((width - Gap * (Columns + 1)) / Columns);

            if (!_closed && width > 0 && size > 0 && Math.Abs(size - _cellSize) >= 1)
            {
                _cellSize = size;
                Bind();
            }
        }

        /// <inheritdoc />
        protected override async void OnAppearing()
        {
            base.OnAppearing();

            _opening = false;

            if (_started || _closed)
            {
                return;
            }

            _started = true;

            try
            {
                await LoadAsync();
            }
            catch (Exception)
            {
                // LoadAsync reports its own failures; nothing else may end the app.
            }
        }

        /// <inheritdoc />
        protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
        {
            base.OnNavigatedFrom(args);

            // Covered by the viewer, the gallery keeps loading; only a closed page stops.
            if (_closed || Navigation.NavigationStack.Contains(this))
            {
                return;
            }

            _closed = true;

            // Stops this page's wait only: the session's load goes on and is kept for the next gallery.
            _closing.Cancel();
            _list.ItemsSource = null;
        }

        /// <summary>
        /// Shows the gallery's batches as they come, from the first: the status goes as soon as the newest is
        /// on screen. Every await is followed by a check that the page is still open.
        /// </summary>
        private async Task LoadAsync()
        {
            _status.Text = "Finding the photos…";
            _working.IsVisible = _working.IsRunning = true;

            var watch = Stopwatch.StartNew();

            try
            {
                var load = _session.GetGalleryLoad();
                var token = _closing.Token;

                while (await load.NextAsync(_batches.Count, token) is { } batch)
                {
                    if (_closed)
                    {
                        return;
                    }

                    Add(batch);

                    if (_batches.Count == 1)
                    {
                        AppState.LogTiming("gallery-first", watch.Elapsed, $"photos={batch.Photos.Count} months={batch.Months.Count}");
                        _status.Text = string.Empty;
                        _status.IsVisible = false;
                        _working.IsVisible = _working.IsRunning = false;
                    }

                    // Lets the list draw what it has before the next batch, even one already in.
                    await Task.Yield();

                    if (_closed)
                    {
                        return;
                    }
                }

                _complete = true;
                AppState.LogTiming("gallery", watch.Elapsed, $"photos={_photos.Count} months={_batches.Sum(b => b.Months.Count)} batches={_batches.Count}");

                if (_photos.Count == 0)
                {
                    _status.Text = "This archive has no restored photos.";
                    _status.IsVisible = true;
                }
            }
            catch (Exception) when (_closed)
            {
                // Closed while loading: nothing on this page is touched again.
            }
            catch (Exception)
            {
                // A batch that cannot be read fails the whole gallery, as before: nothing half-shown.
                _batches.Clear();
                _photos.Clear();
                _rows = null;
                _list.ItemsSource = null;
                _status.Text = "The photos could not be read.";
                _status.IsVisible = true;
            }
            finally
            {
                if (!_closed)
                {
                    _working.IsVisible = _working.IsRunning = false;
                }
            }
        }

        /// <summary>Adds a batch below the rows shown (stars read now, as they may have changed since it was built).</summary>
        private void Add(GalleryBatch batch)
        {
            foreach (var photo in batch.Photos)
            {
                _session.Stars.Apply(photo);
            }

            _batches.Add(batch);
            _photos.AddRange(batch.Photos);

            if (_rows is null)
            {
                Bind();
                return;
            }

            foreach (var row in batch.Rows(Columns))
            {
                _rows.Add(row);
            }
        }

        /// <summary>Binds every row shown so far afresh (first batch, or a new cell size).</summary>
        private void Bind()
        {
            if (_photos.Count > 0 && _cellSize > 0)
            {
                _rows = new ObservableCollection<GalleryRow>(_batches.SelectMany(batch => batch.Rows(Columns)));
                _list.ItemsSource = _rows;
            }
        }

        /// <summary>
        /// A cell was tapped: the photo full screen, swiping through the gallery, or through the photos shown
        /// so far while older ones are still loading.
        /// </summary>
        internal async Task OpenAsync(AttachmentCard photo)
        {
            if (_opening || _closed || _photos.FindIndex(shown => shown.AttachmentID == photo.AttachmentID) is not (var index and >= 0))
            {
                return;
            }

            _opening = true;

            try
            {
                IReadOnlyList<AttachmentCard> photos = _complete ? _photos : [.. _photos];

                await Navigation.PushAsync(new ImageViewerPage(
                    _state, new PhotoBrowser(photos, index), _session.Stars, ShowInChatAsync));
            }
            finally
            {
                _opening = false;
            }
        }

        /// <summary>Show in chat: back to the conversation, opened at the photo's message.</summary>
        private Task ShowInChatAsync(AttachmentCard photo) => _conversation.ShowMessageAsync(photo.SequenceNumber);

        /// <summary>Picks a row's template: a month heading or a row of photos.</summary>
        private sealed class GalleryTemplates(GalleryPage page) : DataTemplateSelector
        {
            private readonly DataTemplate _header = new(() =>
            {
                var label = new Label { FontSize = 15, FontAttributes = FontAttributes.Bold, Padding = new Thickness(12, 14, 12, 6) };
                label.SetBinding(Label.TextProperty, nameof(GalleryHeaderRow.Title));
                return label;
            });

            private readonly DataTemplate _photos = new(() => new PhotoRowView(page));

            protected override DataTemplate OnSelectTemplate(object item, BindableObject container) =>
                item is GalleryHeaderRow ? _header : _photos;
        }

        /// <summary>One row of up to three square cells.</summary>
        private sealed class PhotoRowView : ContentView
        {
            private readonly GalleryPage _page;
            private readonly HorizontalStackLayout _row = new() { Spacing = Gap, Padding = new Thickness(Gap, Gap / 2) };
            private readonly List<PhotoCell> _cells = [];

            public PhotoRowView(GalleryPage page)
            {
                _page = page;
                Content = _row;
            }

            protected override void OnBindingContextChanged()
            {
                base.OnBindingContextChanged();

                if (BindingContext is not GalleryPhotoRow row)
                {
                    foreach (var cell in _cells)
                    {
                        cell.Show(null, 0);
                    }

                    return;
                }

                while (_cells.Count < row.Photos.Count)
                {
                    var cell = new PhotoCell(_page);
                    _cells.Add(cell);
                    _row.Children.Add(cell);
                }

                for (var i = 0; i < _cells.Count; i++)
                {
                    _cells[i].Show(i < row.Photos.Count ? row.Photos[i] : null, _page.CellSize);
                }
            }
        }

        /// <summary>
        /// One square cell: the photo decoded at cell size ("Can't preview" in the same box when it will
        /// not), a small star when starred, and the low-quality label when one applies.
        /// </summary>
        private sealed class PhotoCell : Grid
        {
            private readonly DecodedImage _image = new();
            private readonly Label _star = new() { Text = "★", FontSize = 16, Margin = new Thickness(4, 2), HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Start, Shadow = new Shadow { Radius = 3, Opacity = 0.8f, Offset = new Point(0, 0), Brush = Colors.Black } };
            private readonly Label _lowQuality = new() { Text = AttachmentCard.LowQualityCopyLabel, FontSize = 10, TextColor = Colors.White, BackgroundColor = Color.FromRgba(0, 0, 0, 0.55), Padding = new Thickness(4, 1), HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.End };
            private AttachmentCard? _photo;

            public PhotoCell(GalleryPage page)
            {
                _star.TextIn("Star");
                _star.SetBinding(IsVisibleProperty, nameof(AttachmentCard.IsStarred));

                Children.Add(_image);
                Children.Add(_star);
                Children.Add(_lowQuality);

                var tap = new TapGestureRecognizer();
                tap.Tapped += async (_, _) =>
                {
                    try
                    {
                        if (_photo is { } photo)
                        {
                            await page.OpenAsync(photo);
                        }
                    }
                    catch (Exception)
                    {
                        // A viewer that cannot open (the page closing under it) must not end the app.
                    }
                };

                GestureRecognizers.Add(tap);
            }

            internal void Show(AttachmentCard? photo, double size)
            {
                _photo = photo;
                BindingContext = photo;
                IsVisible = photo is not null;

                if (photo is null)
                {
                    _image.Path = null;
                    return;
                }

                WidthRequest = HeightRequest = size;

                // The size first: the decode reads it when the path changes.
                _image.WidthRequest = _image.HeightRequest = size;
                _image.Path = photo.FullPath;
                _lowQuality.IsVisible = photo.LowQualityLabel.Length > 0;
            }
        }
    }
}
