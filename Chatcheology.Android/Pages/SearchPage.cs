using System.Diagnostics;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Search;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// Search in message text: ignoring case and accents, newest first, each hit with its date, time,
    /// sender and the text around the match highlighted; the total when the list is capped. Tapping a hit
    /// opens the conversation at that message, briefly highlighted.
    /// </summary>
    /// <remarks>The index is built in memory the first time, in the background; nothing is written.</remarks>
    internal sealed class SearchPage : ContentPage
    {
        private readonly ArchiveSession _session;
        private readonly ConversationPage _conversation;
        private readonly SearchBar _query = new() { Placeholder = "Search messages" };
        private readonly Label _summary = new() { FontSize = 13, Margin = new Thickness(16, 4) };
        private readonly ActivityIndicator _working = new() { IsVisible = false, IsRunning = false };
        private readonly CollectionView _results = new() { SelectionMode = SelectionMode.Single };
        private ConversationSearch? _index;
        private CancellationTokenSource? _searching;
        private bool _opening;
        private bool _firstSearch = true;

        internal SearchPage(ArchiveSession session, ConversationPage conversation)
        {
            _session = session;
            _conversation = conversation;

            Title = "Search";
            _summary.TextColor = (Color)Application.Current!.Resources["MutedText"];

            _results.ItemTemplate = new DataTemplate(() => new HitView());
            _results.SelectionChanged += OnSelected;
            _query.SearchButtonPressed += (_, _) => _ = SearchAsync();

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
            layout.Add(_summary, 0, 1);
            layout.Add(_working, 0, 2);
            layout.Add(_results, 0, 3);

            Content = layout;
        }

        /// <inheritdoc />
        protected override async void OnAppearing()
        {
            base.OnAppearing();

            _opening = false;
            _results.SelectedItem = null;

            if (_index is not null)
            {
                return;
            }

            _query.Focus();
            _summary.Text = "Preparing search…";
            _working.IsVisible = _working.IsRunning = true;

            var watch = Stopwatch.StartNew();

            try
            {
                _index = await _session.GetSearchAsync();
                AppState.LogTiming("search-index", watch.Elapsed, $"indexed={_index.Count}");
                _summary.Text = string.Empty;
            }
            catch (Exception exception) when (exception is ArchiveFormatException or SqliteException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                _summary.Text = "Search is not available: the archive could not be read.";
            }
            finally
            {
                _working.IsVisible = _working.IsRunning = false;
            }

            if (!string.IsNullOrWhiteSpace(_query.Text))
            {
                await SearchAsync();
            }
        }

        private async Task SearchAsync()
        {
            if (_index is not { } index)
            {
                return;
            }

            _searching?.Cancel();
            var cancel = _searching = new CancellationTokenSource();
            var query = _query.Text ?? string.Empty;

            _working.IsVisible = _working.IsRunning = true;
            var watch = Stopwatch.StartNew();

            try
            {
                var results = await Task.Run(() => index.Find(query, ConversationSearch.DefaultMaximumHits, cancel.Token), cancel.Token);

                AppState.LogTiming(_firstSearch ? "search-first" : "search", watch.Elapsed, $"matches={results.TotalMatches}");
                _firstSearch = false;

                _results.ItemsSource = results.Hits;
                _summary.Text = query.Trim().Length == 0 ? string.Empty : results.Summary;
            }
            catch (OperationCanceledException)
            {
                // A newer search replaced it.
            }
            finally
            {
                if (ReferenceEquals(_searching, cancel))
                {
                    _working.IsVisible = _working.IsRunning = false;
                }
            }
        }

        private async void OnSelected(object? sender, SelectionChangedEventArgs e)
        {
            if (_opening || e.CurrentSelection.FirstOrDefault() is not SearchHit hit)
            {
                return;
            }

            _opening = true;
            await Navigation.PopAsync();
            await _conversation.OpenAtMessageAsync(hit.SequenceNumber);
        }

        /// <summary>One hit: date, time and sender, then the snippet with every match highlighted.</summary>
        private sealed class HitView : ContentView
        {
            private readonly Label _header = new() { FontSize = 12 };
            private readonly Label _snippet = new() { FontSize = 15, TextColor = Colors.Black, MaxLines = 3 };

            public HitView()
            {
                var resources = Application.Current!.Resources;
                _header.TextColor = (Color)resources["MutedText"];

                Content = new VerticalStackLayout
                {
                    Padding = new Thickness(16, 8),
                    Spacing = 2,
                    Children = { _header, _snippet },
                };
            }

            protected override void OnBindingContextChanged()
            {
                base.OnBindingContextChanged();

                if (BindingContext is not SearchHit hit)
                {
                    return;
                }

                _header.Text = $"{hit.DateText} · {hit.TimeText} · {hit.SenderText}";

                var highlight = (Color)Application.Current!.Resources["Highlight"];
                var formatted = new FormattedString();
                var at = 0;

                foreach (var range in hit.Highlights.OrderBy(range => range.Start))
                {
                    if (range.Start < at || range.Start + range.Length > hit.Snippet.Length)
                    {
                        continue;
                    }

                    if (range.Start > at)
                    {
                        formatted.Spans.Add(new Span { Text = hit.Snippet[at..range.Start] });
                    }

                    formatted.Spans.Add(new Span
                    {
                        Text = hit.Snippet.Substring(range.Start, range.Length),
                        FontAttributes = FontAttributes.Bold,
                        BackgroundColor = highlight,
                    });

                    at = range.Start + range.Length;
                }

                if (at < hit.Snippet.Length)
                {
                    formatted.Spans.Add(new Span { Text = hit.Snippet[at..] });
                }

                _snippet.FormattedText = formatted;
            }
        }
    }
}
