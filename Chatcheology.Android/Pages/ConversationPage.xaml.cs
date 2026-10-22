using System.Diagnostics;
using AndroidX.RecyclerView.Widget;
using Chatcheology.Android.Controls;
using Chatcheology.Android.Conversation;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Presentation;
using Microsoft.Data.Sqlite;
using Microsoft.Maui.Platform;

namespace Chatcheology.Android.Pages
{
    /// <summary>The conversation in MAUI's CollectionView.</summary>
    /// <remarks>
    /// <para>Opens where the reader last was (the sequence number at the top of the screen, stored per
    /// archive), or fully at the newest message the first time. The position is stored as she scrolls.</para>
    /// <para>Everything that reads the archive runs off the UI thread, one read at a time: the pager's
    /// pages, and the reopen for a date jump, a search hit or a change of "me", which builds a new
    /// timeline on a worker thread and swaps it in only when it is ready.</para>
    /// </remarks>
    public partial class ConversationPage : ContentPage
    {
        /// <summary>How long a message opened from search stays highlighted.</summary>
        private const int HighlightMilliseconds = 1600;

        private readonly AppState _state;
        private readonly ArchiveSession _session;
        private readonly Stopwatch? _openWatch;
        private ConversationTimeline _timeline;
        private ConversationPager _pager;
        private OpenedAt _openedAt;
        private readonly ArchiveReadGate _gate = new();
        private readonly ReadTurn _turn;
        private RecyclerView? _recycler;
        private bool _opened;
        private int? _savedPosition;
        private DayCalendar? _calendar;

        /// <summary>Whether the page has begun closing its archive: nothing more is read.</summary>
        private bool _closed => _gate.IsClosed;

        /// <summary>
        /// A page over a conversation the caller opened off the UI thread. The page owns the session from
        /// here on and closes it when it is closed.
        /// </summary>
        internal ConversationPage(AppState state, OpenedConversation opened, Stopwatch? openWatch = null)
        {
            InitializeComponent();

            _state = state;
            _session = opened.Session;
            _timeline = opened.Timeline;
            _openedAt = opened.At;
            _openWatch = openWatch;
            _pager = new ConversationPager(_timeline);
            _turn = new ReadTurn(_gate);

            Title = _session.Title;
            List.ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepItemsInView;

            List.HandlerChanged += (_, _) =>
            {
                _recycler = List.Handler?.PlatformView as RecyclerView;
                _pager.List = _recycler;
            };

            List.Scrolled += (_, e) =>
            {
                _pager.OnVisibleRange(e.FirstVisibleItemIndex, e.LastVisibleItemIndex);
                SavePosition(e.FirstVisibleItemIndex);
            };

            if (_session.CanPickDate)
            {
                ToolbarItems.Add(new ToolbarItem("Date", null, () => _ = PickDateAsync()));
            }

            if (_session.CanSearch)
            {
                ToolbarItems.Add(new ToolbarItem("Search", null, () => _ = OpenSearchAsync()));
            }

            ToolbarItems.Add(new ToolbarItem("Settings", null, () => _ = OpenSettingsAsync()));

#if DEBUG
            // The trial's controlled step: a page added above with no scrolling under way, so a
            // screenshot before and after shows whether anything on screen moved.
            ToolbarItems.Add(new ToolbarItem("Top", null, () => List.ScrollTo(0, position: ScrollToPosition.Start, animate: false)) { Order = ToolbarItemOrder.Secondary });
            ToolbarItems.Add(new ToolbarItem("Load earlier", null, () => _ = LoadEarlierNow()) { Order = ToolbarItemOrder.Secondary });
#endif

            AttachmentCardView.Tapped += OnAttachmentTapped;
            MessageText.LinkTapped += OnLinkTapped;
            MessageLongPress.Pressed += OnMessageLongPressed;
        }

        /// <inheritdoc />
        protected override async void OnAppearing()
        {
            base.OnAppearing();

            if (_opened)
            {
                return;
            }

            _opened = true;

            if (_session.IsEmpty)
            {
                List.EmptyView = "This archive has no messages.";
                return;
            }

            await BindAsync(_timeline, _openedAt, highlightSequenceNumber: null);

            if (_openWatch is { } watch)
            {
                AppState.LogTiming("open-shown", watch.Elapsed, $"messages={_session.MessageCount} rows={_timeline.Rows.Count}");
            }
        }

        /// <inheritdoc />
        protected override async void OnNavigatedFrom(NavigatedFromEventArgs args)
        {
            base.OnNavigatedFrom(args);

            if (Navigation.NavigationStack.Contains(this))
            {
                return;
            }

            await CloseAsync();
        }

        /// <summary>
        /// Stops every read, waits for any read already started, then closes the archive. No read starts
        /// once this has begun (<see cref="ArchiveReadGate"/>). Safe to call twice.
        /// </summary>
        private async Task CloseAsync()
        {
            if (_closed)
            {
                return;
            }

            AttachmentCardView.Tapped -= OnAttachmentTapped;
            MessageText.LinkTapped -= OnLinkTapped;
            MessageLongPress.Pressed -= OnMessageLongPressed;

            try
            {
                await _gate.CloseAsync(() => _pager.StopAsync(), _session.Dispose);
            }
            catch (Exception)
            {
                // The gate closes the session whatever stopping the pager did; nothing to report here.
            }
        }

        // -----------------------------------------------------------------------------------------
        // Placing the list.
        // -----------------------------------------------------------------------------------------

        /// <summary>Binds a loaded timeline and places it: at the bottom for the newest, otherwise the row at the top.</summary>
        private async Task BindAsync(ConversationTimeline timeline, OpenedAt at, int? highlightSequenceNumber)
        {
            _timeline = timeline;
            _pager = new ConversationPager(timeline) { List = _recycler };
            _savedPosition = null;

            List.ItemsSource = timeline.Rows;

            if (at.RowIndex >= 0)
            {
                await PlaceAsync(at);
            }

            _pager.Ready = true;

            // Where the list now is counts as where she last was, even before she scrolls (a date
            // jump or a search hit followed by closing the app reopens there).
            if (_recycler?.GetLayoutManager() is LinearLayoutManager placed)
            {
                SavePosition(placed.FindFirstVisibleItemPosition());
            }

            if (highlightSequenceNumber is { } sequence && timeline.IndexOfMessage(sequence) is var row and >= 0
                && timeline.Rows[row] is MessageRow message)
            {
                Highlight(message);
            }
        }

        /// <summary>
        /// Puts the row in place once the list has laid it out. The newest message is placed fully on
        /// screen: rows are measured as they are drawn, so the end is checked again for a few frames
        /// and the list moved down until it cannot go further.
        /// </summary>
        private async Task PlaceAsync(OpenedAt at)
        {
            // OnAppearing can come before the list has its Android view, or before that view is laid out
            // with the new rows: wait for both (two seconds at most).
            var waited = Stopwatch.StartNew();

            while ((List.Handler?.PlatformView is not RecyclerView view || !view.IsLaidOut || view.Height == 0
                    || (view.GetAdapter()?.ItemCount ?? 0) <= at.RowIndex)
                   && waited.ElapsedMilliseconds < 2000)
            {
                await Task.Delay(16);
            }

            if (List.Handler?.PlatformView is not RecyclerView recycler)
            {
                List.ScrollTo(at.RowIndex, position: at.AtLatest ? ScrollToPosition.End : ScrollToPosition.Start, animate: false);
                return;
            }

            _recycler = recycler;
            _pager.List = recycler;

            await NextFrameAsync(recycler);

            if (recycler.GetLayoutManager() is LinearLayoutManager layout)
            {
                if (!at.AtLatest)
                {
                    layout.ScrollToPositionWithOffset(at.RowIndex, 0);
                    await NextFrameAsync(recycler);
                    return;
                }

                for (var attempt = 0; attempt < 12; attempt++)
                {
                    layout.ScrollToPosition(at.RowIndex);
                    await NextFrameAsync(recycler);

                    if (!recycler.CanScrollVertically(1))
                    {
                        // Two quiet frames in a row: the last row is measured and fully shown.
                        await NextFrameAsync(recycler);

                        if (!recycler.CanScrollVertically(1))
                        {
                            break;
                        }
                    }

                    recycler.ScrollBy(0, Math.Max(recycler.Height, 1));
                    await NextFrameAsync(recycler);
                }
            }
        }

        /// <summary>Waits for the list's next frame (at most half a second).</summary>
        private static async Task NextFrameAsync(RecyclerView view)
        {
            var done = new TaskCompletionSource();
            view.PostOnAnimation(new Java.Lang.Runnable(() => done.TrySetResult()));

            await Task.WhenAny(done.Task, Task.Delay(500));
        }

        /// <summary>
        /// Briefly highlights <paramref name="message"/>. The flag is on the message itself and the
        /// bubble's template follows it, so a page inserted above or a recycled view cannot flash
        /// another message.
        /// </summary>
        private static async void Highlight(MessageRow message)
        {
            message.IsHighlighted = true;
            await Task.Delay(HighlightMilliseconds);
            message.IsHighlighted = false;
        }

        /// <summary>Stores the first message at or below the top row as the reader's position.</summary>
        private void SavePosition(int firstVisible)
        {
            if (!_pager.Ready || firstVisible < 0)
            {
                return;
            }

            for (var i = firstVisible; i < _timeline.Rows.Count; i++)
            {
                if (_timeline.Rows[i] is MessageRow message)
                {
                    if (_savedPosition != message.SequenceNumber)
                    {
                        _savedPosition = message.SequenceNumber;
                        _session.SavePosition(message.SequenceNumber);
                    }

                    return;
                }
            }
        }

        /// <summary>The sequence number of the first message on screen, or the stored position.</summary>
        private int? CurrentSequenceNumber()
        {
            if (_recycler?.GetLayoutManager() is LinearLayoutManager layout && layout.FindFirstVisibleItemPosition() is var first and >= 0)
            {
                for (var i = first; i < _timeline.Rows.Count; i++)
                {
                    if (_timeline.Rows[i] is MessageRow message)
                    {
                        return message.SequenceNumber;
                    }
                }
            }

            return _session.Position;
        }

        // -----------------------------------------------------------------------------------------
        // Reopening: date jump, search hit, change of "me".
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Builds a new timeline off the UI thread with <paramref name="open"/> (which returns the row to
        /// place at the top, or -1) and swaps it in. The pager is stopped first and the read runs through
        /// the gate, so only one read uses the reader at a time and none runs once the page is closing.
        /// Never throws: its callers are tap handlers.
        /// </summary>
        private async Task ReopenAsync(Func<ConversationTimeline, int> open, int? highlightSequenceNumber)
        {
            if (!_turn.TryBegin())
            {
                return;
            }

            Busy.IsVisible = Busy.IsRunning = true;

            try
            {
                GatedRead<(ConversationTimeline Timeline, int Row)> read;

                try
                {
                    read = await _gate.RunAsync(
                        () => _pager.StopAsync(),
                        () =>
                        {
                            var timeline = _session.CreateTimeline();
                            return (timeline, open(timeline));
                        });
                }
                catch (Exception) when (!_closed)
                {
                    await DisplayAlertAsync("Conversation", "That part of the archive could not be read.", "OK");
                    read = default;
                }

                if (_closed)
                {
                    return;
                }

                if (!read.Ran || read.Value.Row < 0)
                {
                    // Nothing to show there: carry on with what is loaded.
                    _pager = new ConversationPager(_timeline) { List = _recycler, Ready = true };
                    return;
                }

                await BindAsync(read.Value.Timeline, new OpenedAt(read.Value.Row, AtLatest: false), highlightSequenceNumber);
            }
            catch (Exception)
            {
                // A failure after the read (or a read failing as the page closed): what is shown stays.
            }
            finally
            {
                Busy.IsVisible = Busy.IsRunning = false;

                // "Me" changed again while this ran: rebuild once more, so the latest choice wins.
                if (_turn.End())
                {
                    _ = OnMeChangedAsync();
                }
            }
        }

        /// <summary>Opens the conversation at <paramref name="sequenceNumber"/> and highlights it (a search hit).</summary>
        internal Task OpenAtMessageAsync(int sequenceNumber) =>
            ReopenAsync(timeline => timeline.OpenAt(sequenceNumber), sequenceNumber);

        private async Task PickDateAsync()
        {
            if (_turn.IsBusy || _closed)
            {
                return;
            }

            if (_calendar is null)
            {
                if (!_turn.TryBegin())
                {
                    return;
                }

                Busy.IsVisible = Busy.IsRunning = true;

                try
                {
                    // Between page reads only: the pager is paused while the days are read.
                    var read = await _gate.RunAsync(() => _pager.StopAsync(), () => new DayCalendar(_session.ReadDays()));

                    if (!read.Ran || _closed)
                    {
                        return;
                    }

                    _calendar = read.Value;
                }
                catch (Exception) when (!_closed)
                {
                    await DisplayAlertAsync("Go to date", "The days could not be read.", "OK");
                    return;
                }
                catch (Exception)
                {
                    return;
                }
                finally
                {
                    Busy.IsVisible = Busy.IsRunning = false;

                    if (!_closed)
                    {
                        _pager = new ConversationPager(_timeline) { List = _recycler, Ready = true };
                    }

                    // "Me" changed while the days were read (CH-29): rebuild now, through the gate like
                    // every read, after the pager is back so the rebuild stops this one.
                    if (_turn.End())
                    {
                        _ = OnMeChangedAsync();
                    }
                }
            }

            var near = CurrentSequenceNumber() is { } sequence && _timeline.IndexOfMessage(sequence) is var row and >= 0
                ? _timeline.Rows[row].Day
                : DateOnly.FromDateTime(DateTime.Today);

            await Navigation.PushAsync(new DatePickerPage(_calendar!, near, day => ReopenAsync(
                timeline =>
                {
                    // The day's separator heads the screen, with its first message below it.
                    var first = timeline.OpenAtDay(day);
                    return first > 0 && timeline.Rows[first - 1] is DateSeparatorRow ? first - 1 : first;
                },
                null)));
        }

        private async Task OpenSearchAsync()
        {
            if (_closed)
            {
                return;
            }

            await Navigation.PushAsync(new SearchPage(_session, this));
        }

        private async Task OpenSettingsAsync()
        {
            if (_closed)
            {
                return;
            }

            await Navigation.PushAsync(new SettingsPage(_session, OnMeChangedAsync, RemoveArchiveAsync));
        }

        /// <summary>
        /// "Me" changed in Settings: the bubbles are rebuilt at the same place. A change made while a
        /// rebuild is under way is not dropped: it is rebuilt again when that one ends.
        /// </summary>
        private Task OnMeChangedAsync()
        {
            if (!_turn.MeChanged())
            {
                return Task.CompletedTask;
            }

            var at = CurrentSequenceNumber();

            return ReopenAsync(
                timeline => at is { } sequence ? timeline.OpenAt(sequence) : timeline.OpenAtLatest(),
                null);
        }

        /// <summary>Settings &gt; Remove archive, confirmed: closes the archive, deletes the app's copy, back to the start.</summary>
        private async Task RemoveArchiveAsync()
        {
            var archive = _session.Archive;

            await CloseAsync();

            try
            {
                await Task.Run(() => _state.Library.Remove(archive, _state.Settings));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await DisplayAlertAsync("Remove archive", "The archive could not be removed completely. Try again from the start screen.", "OK");
            }

            await Navigation.PopToRootAsync();
        }

        // -----------------------------------------------------------------------------------------
        // Taps.
        // -----------------------------------------------------------------------------------------

        private async void OnAttachmentTapped(object? sender, AttachmentTapped tapped)
        {
            var card = tapped.Card;

            if (_closed || card.FullPath is not { } path)
            {
                return;
            }

            if (card.Kind == AttachmentCardKind.Image && !tapped.DecodeFailed)
            {
                await Navigation.PushAsync(new ImageViewerPage(_state, card));
                return;
            }

            await OpenInAnotherAppAsync(this, _state, path, card.MimeType ?? MediaMimeTypes.Fallback);
        }

        private async void OnLinkTapped(object? sender, string target)
        {
            if (_closed)
            {
                return;
            }

            if (ExternalOpener.OpenAddress(target) == OpenResult.NoApp)
            {
                await DisplayAlertAsync("Link", "No app on this phone can open this link.", "OK");
            }
        }

        /// <summary>
        /// A long press on a message with text (CH-34): Share offers the text only, no date or sender, to
        /// the share sheet. Nothing leaves the app until she picks Share and then an app.
        /// </summary>
        private async void OnMessageLongPressed(object? sender, MessageRow message)
        {
            if (_closed || Navigation.NavigationStack.LastOrDefault() != this || message.ShareText is not { } text)
            {
                return;
            }

            try
            {
                if (await DisplayActionSheetAsync(null, "Cancel", null, "Share") != "Share")
                {
                    return;
                }

                if (ExternalOpener.ShareText(text) == OpenResult.NoApp)
                {
                    await DisplayAlertAsync("Share", "No app on this phone can receive text.", "OK");
                }
            }
            catch (Exception)
            {
                // A tap handler: nothing here may end the app.
            }
        }

        /// <summary>Opens a restored file in another app, or says why it could not.</summary>
        internal static async Task OpenInAnotherAppAsync(Page page, AppState state, string path, string mimeType)
        {
            switch (ExternalOpener.OpenFile(path, mimeType, state.Library.ArchivesFolder))
            {
                case OpenResult.NoApp:
                    await page.DisplayAlertAsync("Open", "No app on this phone can open this file.", "OK");
                    break;
                case OpenResult.NotAllowed:
                    await page.DisplayAlertAsync("Open", "This file cannot be opened.", "OK");
                    break;
            }
        }

#if DEBUG
        private async Task LoadEarlierNow()
        {
            _recycler?.StopScroll();
            await _pager.LoadEarlierAsync();
        }
#endif
    }

    /// <summary>Picks a row's template: separator, my message, theirs, or a system line.</summary>
    public sealed class RowTemplateSelector : DataTemplateSelector
    {
        /// <summary>A date separator.</summary>
        public DataTemplate Separator { get; set; } = new();

        /// <summary>A message from someone else, on the left.</summary>
        public DataTemplate Theirs { get; set; } = new();

        /// <summary>A message from me, on the right.</summary>
        public DataTemplate Mine { get; set; } = new();

        /// <summary>A system line, centred.</summary>
        public DataTemplate System { get; set; } = new();

        /// <inheritdoc />
        protected override DataTemplate OnSelectTemplate(object item, BindableObject container) => item switch
        {
            DateSeparatorRow => Separator,
            MessageRow { IsSystem: true } => System,
            MessageRow { IsMine: true } => Mine,
            _ => Theirs,
        };
    }
}
