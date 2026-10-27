using System.Diagnostics;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Memories;
using Chatcheology.Mobile.Search;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// Search in message text: ignoring case and accents, newest first, each hit with its date, time,
    /// sender and the text around the match highlighted; the total when the list is capped. Tapping a hit
    /// opens the conversation at that message, briefly highlighted. With a memory layer on, the titles of
    /// its memories are searched too and listed first; tapping one opens its row (CH-42).
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
            _summary.TextIn("MutedText");

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
                var (results, memories) = await Task.Run(
                    () => (index.Find(query, ConversationSearch.DefaultMaximumHits, cancel.Token), _session.FindMemories(query)),
                    cancel.Token);

                AppState.LogTiming(_firstSearch ? "search-first" : "search", watch.Elapsed, $"matches={results.TotalMatches} memories={memories.Count}");
                _firstSearch = false;

                _results.ItemsSource = memories.Cast<object>().Concat(results.Hits).ToList();
                _summary.Text = query.Trim().Length == 0
                    ? string.Empty
                    : memories.Count switch
                    {
                        0 => results.Summary,
                        1 => "1 memory matches. " + results.Summary,
                        _ => $"{memories.Count.ToString(System.Globalization.CultureInfo.CurrentCulture)} memories match. " + results.Summary,
                    };
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
            if (_opening || e.CurrentSelection.FirstOrDefault() is not { } selected || selected is not (SearchHit or MemorySearchHit))
            {
                return;
            }

            _opening = true;

            try
            {
                await Navigation.PopAsync();

                if (selected is MemorySearchHit memory)
                {
                    await _conversation.OpenAtMemoryAsync(memory.Key);
                }
                else if (selected is SearchHit hit)
                {
                    await _conversation.OpenAtMessageAsync(hit.SequenceNumber);
                }
            }
            catch (Exception)
            {
                // The conversation reports its own read failures; nothing else may end the app.
            }
        }

        /// <summary>One hit: date, time and sender, then the snippet with every match highlighted.</summary>
        private sealed class HitView : ContentView
        {
            private readonly Label _header = new() { FontSize = 12 };
            private readonly Label _snippet = new() { FontSize = 15, MaxLines = 3 };

            public HitView()
            {
                _header.TextIn("MutedText");

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

                switch (BindingContext)
                {
                    case SearchHit hit:
                        _header.Text = $"{hit.DateText} · {hit.TimeText} · {hit.SenderText}";
                        _snippet.FormattedText = Highlighted(hit.Snippet, hit.Highlights);
                        break;

                    case MemorySearchHit memory:
                        _header.Text = $"{memory.DateText} · {memory.CollectionName} (memory)";
                        _snippet.FormattedText = Highlighted(memory.Title, memory.Highlights);
                        break;
                }
            }

            private static FormattedString Highlighted(string text, IReadOnlyList<MatchRange> highlights)
            {
                var formatted = new FormattedString();
                var at = 0;

                foreach (var range in highlights.OrderBy(range => range.Start))
                {
                    if (range.Start < at || range.Start + range.Length > text.Length)
                    {
                        continue;
                    }

                    if (range.Start > at)
                    {
                        formatted.Spans.Add(new Span { Text = text[at..range.Start] });
                    }

                    formatted.Spans.Add(new Span
                    {
                        Text = text.Substring(range.Start, range.Length),
                        FontAttributes = FontAttributes.Bold,
                    }.Themed(Span.BackgroundColorProperty, "Highlight"));

                    at = range.Start + range.Length;
                }

                if (at < text.Length)
                {
                    formatted.Spans.Add(new Span { Text = text[at..] });
                }

                return formatted;
            }
        }
    }
}
