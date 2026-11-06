using System.Diagnostics;
using AndroidX.RecyclerView.Widget;
using Chatcheology.Android.Controls;
using Chatcheology.Android.Conversation;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Gallery;
using Chatcheology.Mobile.Memories;
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
    /// <para>The top bar (CH-37 to CH-39) has icons for Photos, Starred, the date picker and search,
    /// with Settings in the overflow menu; on a day with earlier years to remember, a discreet On this day
    /// icon reopens that card. Stars live in the app's settings; a toggle updates the loaded bubbles.</para>
    /// <para>Memory rows (CH-42) come with the timeline; a tap opens their details. A layer or filter
    /// switched in Settings rebuilds the timeline at the same place, like a change of "me", and the date
    /// picker's days are read again.</para>
    /// <para>An archive with memories has Memories in the overflow menu (CH-56): every memory in one list,
    /// whatever the layers say, each opening its details over the list.</para>
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
        private IReadOnlyList<DateOnly>? _days;
        private OnThisDay? _onThisDay;
        private ToolbarItem? _onThisDayIcon;
        private int _layersVersion;
        private readonly bool _showOnThisDay;

        /// <summary>Whether the page has begun closing its archive: nothing more is read.</summary>
        private bool _closed => _gate.IsClosed;

        /// <summary>
        /// A page over a conversation the caller opened off the UI thread. The page owns the session from
        /// here on and closes it when it is closed.
        /// </summary>
        /// <param name="state">The app's shared state.</param>
        /// <param name="opened">The opened conversation; the page owns its session.</param>
        /// <param name="openWatch">The open timing, for the log.</param>
        /// <param name="showOnThisDay">
        /// Opened from a noted-date notification (CH-47): the On this day card opens even when it was already shown
        /// today. An archive with no messages has no card.
        /// </param>
        internal ConversationPage(AppState state, OpenedConversation opened, Stopwatch? openWatch = null, bool showOnThisDay = false)
        {
            InitializeComponent();

            _state = state;
            _showOnThisDay = showOnThisDay;
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

            if (!_session.IsEmpty)
            {
                ToolbarItems.Add(new ToolbarItem("Photos", "toolbar_photos.png", () => _ = OpenGalleryAsync()));
                ToolbarItems.Add(new ToolbarItem("Starred", "toolbar_starred.png", () => _ = OpenStarredAsync()));
            }

            if (_session.CanPickDate)
            {
                ToolbarItems.Add(new ToolbarItem("Date", "toolbar_date.png", () => _ = PickDateAsync()));
            }

            if (_session.CanSearch)
            {
                ToolbarItems.Add(new ToolbarItem("Search", "toolbar_search.png", () => _ = OpenSearchAsync()));
            }

            // CH-56: every memory in one list, whatever the layer switches say.
            if (_session.Memories.HasMemories)
            {
                ToolbarItems.Add(new ToolbarItem("Memories", null, () => _ = OpenMemoriesAsync()) { Order = ToolbarItemOrder.Secondary });
            }

            ToolbarItems.Add(new ToolbarItem("Settings", null, () => _ = OpenSettingsAsync()) { Order = ToolbarItemOrder.Secondary });

            var close = new TapGestureRecognizer();
            close.Tapped += (_, _) => OnThisDayCard.IsVisible = false;
            OnThisDayClose.GestureRecognizers.Add(close);

#if DEBUG
            // The trial's controlled step: a page added above with no scrolling under way, so a
            // screenshot before and after shows whether anything on screen moved.
            ToolbarItems.Add(new ToolbarItem("Top", null, () => List.ScrollTo(0, position: ScrollToPosition.Start, animate: false)) { Order = ToolbarItemOrder.Secondary });
            ToolbarItems.Add(new ToolbarItem("Load earlier", null, () => _ = LoadEarlierNow()) { Order = ToolbarItemOrder.Secondary });
#endif

            AttachmentCardView.Tapped += OnAttachmentTapped;
            MemoryCard.Tapped += OnMemoryTapped;
            MessageText.LinkTapped += OnLinkTapped;
            MessageLongPress.Pressed += OnMessageLongPressed;
            AttachmentCardView.PlayFailed += OnPlayFailed;
            AudioPlayback.Failed += OnPlayFailed;
            _session.Stars.Changed += OnStarsChanged;
        }

        /// <inheritdoc />
        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            // Leaving the screen stops a voice note (CH-36), whether another page covers it or it closes.
            AudioPlayback.Stop();
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

            await BindAsync(_timeline, _openedAt, highlightSequenceNumber: null, highlightMemory: null);

            if (_openWatch is { } watch)
            {
                AppState.LogTiming("open-shown", watch.Elapsed, $"messages={_session.MessageCount} rows={_timeline.Rows.Count}");
            }

            await PrepareOnThisDayAsync();
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
            MemoryCard.Tapped -= OnMemoryTapped;
            MessageText.LinkTapped -= OnLinkTapped;
            MessageLongPress.Pressed -= OnMessageLongPressed;
            AttachmentCardView.PlayFailed -= OnPlayFailed;
            AudioPlayback.Failed -= OnPlayFailed;
            _session.Stars.Changed -= OnStarsChanged;
            AudioPlayback.Stop();

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
        private async Task BindAsync(ConversationTimeline timeline, OpenedAt at, int? highlightSequenceNumber, MemoryKey? highlightMemory)
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

            if (highlightMemory is { } key && timeline.IndexOfMemory(key) is var memoryRow and >= 0
                && timeline.Rows[memoryRow] is MemoryRow memory)
            {
                Highlight(memory);
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

        /// <summary>Briefly highlights a memory row opened from search or a link, as a message is.</summary>
        private static async void Highlight(MemoryRow memory)
        {
            memory.IsHighlighted = true;
            await Task.Delay(HighlightMilliseconds);
            memory.IsHighlighted = false;
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
        /// Never throws: its callers are tap handlers. True when the new timeline was put on screen; false
        /// when the read was refused (another read under way, the page closing) or found nothing there.
        /// </summary>
        private async Task<bool> ReopenAsync(Func<ConversationTimeline, int> open, int? highlightSequenceNumber, MemoryKey? highlightMemory = null)
        {
            if (!_turn.TryBegin())
            {
                return false;
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
                    return false;
                }

                if (!read.Ran || read.Value.Row < 0)
                {
                    // Nothing to show there: carry on with what is loaded.
                    _pager = new ConversationPager(_timeline) { List = _recycler, Ready = true };
                    return false;
                }

                await BindAsync(read.Value.Timeline, new OpenedAt(read.Value.Row, AtLatest: false), highlightSequenceNumber, highlightMemory);
                return true;
            }
            catch (Exception)
            {
                // A failure after the read (or a read failing as the page closed): what is shown stays.
                return false;
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

        /// <summary>
        /// Opens the conversation at a memory row and highlights it (a search hit or a link). True when the
        /// conversation was opened there.
        /// </summary>
        internal async Task<bool> OpenAtMemoryAsync(MemoryKey key)
        {
            if (_closed)
            {
                return false;
            }

            if (_session.Memories.Layout(_session.MemoryView).Find(key) is null)
            {
                await DisplayAlertAsync("Memories", "That memory is not shown now: its layer or filter is off in Settings.", "OK");
                return false;
            }

            return await ReopenAsync(timeline => timeline.OpenAtMemory(key), null, key);
        }

        private async Task PickDateAsync()
        {
            if (_turn.IsBusy || _closed)
            {
                return;
            }

            if (_calendar is null && !await ReadCalendarAsync(showBusy: true, reportFailure: true))
            {
                return;
            }

            var near = CurrentSequenceNumber() is { } sequence && _timeline.IndexOfMessage(sequence) is var row and >= 0
                ? _timeline.Rows[row].Day
                : OnThisDay.TodayFrom(TimeProvider.System);

            await Navigation.PushAsync(new DatePickerPage(_calendar!, near, GoToDayAsync, TimeProvider.System));
        }

        /// <summary>
        /// Opens the conversation at the first message on or after <paramref name="day"/>: the day's
        /// separator heads the screen, with its first message below it.
        /// </summary>
        private Task<bool> GoToDayAsync(DateOnly day) => ReopenAsync(
            timeline =>
            {
                var first = timeline.OpenAtDay(day);
                return first > 0 && timeline.Rows[first - 1] is DateSeparatorRow ? first - 1 : first;
            },
            null);

        /// <summary>
        /// Reads the days that have messages once, for the date picker and On this day. Between page reads
        /// only: the pager is paused while the days are read, through the gate like every read. False when
        /// it did not read (another read under way, the page closing, or a failure).
        /// </summary>
        private async Task<bool> ReadCalendarAsync(bool showBusy, bool reportFailure)
        {
            if (_calendar is not null)
            {
                return true;
            }

            if (!_turn.TryBegin())
            {
                return false;
            }

            Busy.IsVisible = Busy.IsRunning = showBusy;

            try
            {
                // The messages' days for On this day (message-only), and the picker's days with any
                // visible memory rows. A layer switched while this ran makes the picker's days stale.
                var version = _layersVersion;
                var read = await _gate.RunAsync(() => _pager.StopAsync(), () => (Messages: _session.ReadDays(), Calendar: _session.ReadCalendarDays()));

                if (!read.Ran || _closed || read.Value.Messages is not { } days || read.Value.Calendar is not { } calendarDays)
                {
                    return false;
                }

                _days = days;

                if (version == _layersVersion)
                {
                    _calendar = new DayCalendar(calendarDays);
                }

                return _calendar is not null;
            }
            catch (Exception) when (!_closed && reportFailure)
            {
                await DisplayAlertAsync("Go to date", "The days could not be read.", "OK");
                return false;
            }
            catch (Exception)
            {
                return false;
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

        // -----------------------------------------------------------------------------------------
        // On this day (CH-39).
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Finds the earlier years with messages on today's date (the phone's), shows the discreet icon when
        /// there are any, and opens the card by itself the first time today. Quietly does nothing when the
        /// days cannot be read now.
        /// </summary>
        private async Task PrepareOnThisDayAsync()
        {
            if (_session.IsEmpty || _closed || !await ReadCalendarAsync(showBusy: false, reportFailure: false) || _days is not { } days)
            {
                return;
            }

            _onThisDay = _session.OnThisDayFor(days, OnThisDay.TodayFrom(TimeProvider.System));
            UpdateOnThisDay(openByItself: true);

            if (_showOnThisDay && !OnThisDayCard.IsVisible)
            {
                ShowOnThisDayCard();
            }
        }

        /// <summary>Shows or hides the icon (and the card) as the setting and the years say.</summary>
        private void UpdateOnThisDay(bool openByItself)
        {
            if (_onThisDay is not { } onThisDay || _closed)
            {
                return;
            }

            if (onThisDay.ShowsIcon && _onThisDayIcon is null)
            {
                _onThisDayIcon = new ToolbarItem("On this day", "toolbar_onthisday.png", ShowOnThisDayCard);
                ToolbarItems.Insert(0, _onThisDayIcon);
            }
            else if (!onThisDay.ShowsIcon && _onThisDayIcon is not null)
            {
                ToolbarItems.Remove(_onThisDayIcon);
                _onThisDayIcon = null;
            }

            if (!onThisDay.ShowsIcon)
            {
                OnThisDayCard.IsVisible = false;
            }
            else if (openByItself && onThisDay.OpensByItself)
            {
                ShowOnThisDayCard();
            }
        }

        /// <summary>The card: an "On this day" heading and each year, a tap on which jumps to that day.</summary>
        private void ShowOnThisDayCard()
        {
            if (_onThisDay is not { ShowsIcon: true } onThisDay || _closed)
            {
                return;
            }

            ShowYearlyMemories(onThisDay);
            ShowNotedDates(onThisDay);
            OnThisDayYears.Children.Clear();
            OnThisDayYears.IsVisible = onThisDay.Years.Count > 0;

            if (onThisDay.Years.Count > 0)
            {
                OnThisDayYears.Children.Add(new Label { Text = "On this day", FontSize = 14, FontAttributes = FontAttributes.Bold, Padding = new Thickness(0, 4) });
            }

            // The years wrap in equal-width cells so they line up in columns across rows.
            var yearsFlow = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center };
            OnThisDayYears.Children.Add(yearsFlow);

            for (var i = 0; i < onThisDay.Years.Count; i++)
            {
                var year = onThisDay.Years[i];

                var link = new Label
                {
                    Text = year.Text,
                    FontSize = 14,
                    FontAttributes = FontAttributes.Bold,
                    TextDecorations = TextDecorations.Underline,
                    Padding = new Thickness(0, 4),
                    WidthRequest = 64,
                    HorizontalTextAlignment = TextAlignment.Start,
                }.TextIn("LinkText");

                SemanticProperties.SetDescription(link, "Go to " + DateSeparatorRow.DayText(year.Day));

                var tap = new TapGestureRecognizer();
                tap.Tapped += async (_, _) =>
                {
                    // The card goes only once the jump is made; a refused jump leaves it to tap again.
                    if (await GoToDayAsync(year.Day))
                    {
                        OnThisDayCard.IsVisible = false;
                    }
                };

                link.GestureRecognizers.Add(tap);
                yearsFlow.Children.Add(link);
            }

            OnThisDayCard.IsVisible = true;
            onThisDay.MarkShown();
        }

        /// <summary>
        /// Yearly memories (CH-53), one row each above the noted dates: the cover thumbnail when there is one, the
        /// title with "· N years" (the title alone in its own year) and the gallery chip. A tap on the thumbnail or
        /// title opens the memory's details.
        /// </summary>
        private void ShowYearlyMemories(OnThisDay onThisDay)
        {
            OnThisDayYearly.Children.Clear();
            OnThisDayYearly.IsVisible = onThisDay.Yearly.Count > 0;

            foreach (var yearly in onThisDay.Yearly)
            {
                var row = new Grid
                {
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
                    ColumnSpacing = yearly.HasImage ? 8 : 0,
                    Padding = new Thickness(0, 3),
                };

                // One recognizer per view: a recognizer belongs to the view it is added to.
                TapGestureRecognizer OpensDetails()
                {
                    var tap = new TapGestureRecognizer();
                    tap.Tapped += async (_, _) =>
                    {
                        try
                        {
                            await OnYearlyTappedAsync(yearly);
                        }
                        catch (Exception)
                        {
                            // A tap handler: nothing here may end the app.
                        }
                    };

                    return tap;
                }

                if (yearly.ImagePath is { } image)
                {
                    var thumbnail = new DecodedImage
                    {
                        WidthRequest = 40,
                        HeightRequest = 40,
                        FailedText = string.Empty,
                        VerticalOptions = LayoutOptions.Center,
                        Path = image,
                    };

                    thumbnail.GestureRecognizers.Add(OpensDetails());
                    row.Add(thumbnail, 0);
                }

                var title = new Label
                {
                    Text = yearly.Text,
                    FontSize = 14,
                    FontAttributes = FontAttributes.Bold,
                    TextDecorations = TextDecorations.Underline,
                    Padding = new Thickness(0, 2),
                }.TextIn("LinkText");

                SemanticProperties.SetDescription(title, "Open the memory " + yearly.Title);
                title.GestureRecognizers.Add(OpensDetails());

                var text = new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center, Children = { title } };

                // CH-48: the memory's gallery links, opened only after a confirmation.
                if (yearly.Galleries.Count > 0)
                {
                    text.Add(SettingsPage.GalleryLine(this, yearly.GalleryText, yearly.Galleries));
                }

                row.Add(text, 1);
                OnThisDayYearly.Children.Add(row);
            }
        }

        /// <summary>
        /// A yearly memory's row was tapped: its details (read-only, whatever its layer says), with Show in chat only
        /// when its own date has messages.
        /// </summary>
        private async Task OnYearlyTappedAsync(YearlyToday yearly)
        {
            if (_closed
                || Navigation.NavigationStack.LastOrDefault() != this
                || _session.MemoryDetails(new MemoryKey(yearly.EntryID, null)) is not { } details)
            {
                return;
            }

            Func<Task>? showInChat = yearly.ShowInChatDay is { } day ? () => ShowDayAsync(day) : null;

            await Navigation.PushAsync(new MemoryDetailsPage(details, _state.Library.ArchivesFolder, ShowMessageAsync, ShowEntryAsync, showInChat));
        }

        /// <summary>Show in chat from a yearly memory's details: back to this page, at that day; the card goes once there.</summary>
        private async Task ShowDayAsync(DateOnly day)
        {
            if (_closed)
            {
                return;
            }

            while (Navigation.NavigationStack.Count > 1 && Navigation.NavigationStack[^1] != this)
            {
                await Navigation.PopAsync(animated: false);
            }

            if (await GoToDayAsync(day))
            {
                OnThisDayCard.IsVisible = false;
            }
        }

        /// <summary>
        /// Noted dates (CH-47), one row each above the years: the title and "· N years"; a tap shows the note
        /// when there is one and goes to the noted year's day when it has messages.
        /// </summary>
        private void ShowNotedDates(OnThisDay onThisDay)
        {
            OnThisDayNoted.Children.Clear();
            OnThisDayNoted.IsVisible = onThisDay.Noted.Count > 0;

            foreach (var noted in onThisDay.Noted)
            {
                var tappable = noted.Note is not null || noted.JumpDay is not null;
                var row = new Label { Text = noted.Text, FontSize = 14, FontAttributes = FontAttributes.Bold, Padding = new Thickness(0, 4) };

                if (tappable)
                {
                    row.TextDecorations = TextDecorations.Underline;
                    row.TextIn("LinkText");
                }

                var tap = new TapGestureRecognizer();
                tap.Tapped += async (_, _) =>
                {
                    try
                    {
                        await OnNotedTappedAsync(noted);
                    }
                    catch (Exception)
                    {
                        // A tap handler: nothing here may end the app.
                    }
                };

                row.GestureRecognizers.Add(tap);
                OnThisDayNoted.Children.Add(row);

                // CH-48: the date's gallery links, opened only after a confirmation.
                if (noted.Galleries.Count > 0)
                {
                    OnThisDayNoted.Children.Add(SettingsPage.GalleryLine(this, noted.GalleryText, noted.Galleries));
                }
            }
        }

        private async Task OnNotedTappedAsync(NotedToday noted)
        {
            if (_closed)
            {
                return;
            }

            if (noted.Note is { } note)
            {
                if (noted.JumpDay is { } day)
                {
                    if (!await DisplayAlertAsync(noted.Title, note, "Go to " + DateSeparatorRow.DayText(day), "Close"))
                    {
                        return;
                    }
                }
                else
                {
                    await DisplayAlertAsync(noted.Title, note, "OK");
                    return;
                }
            }

            if (noted.JumpDay is { } jump && await GoToDayAsync(jump))
            {
                OnThisDayCard.IsVisible = false;
            }
        }

        /// <summary>
        /// On this day, or a noted date, was changed in Settings: today's noted dates are found again (an own
        /// date may have been added, edited or deleted) and the icon and an open card follow.
        /// </summary>
        private void OnThisDayChanged()
        {
            if (_days is { } days && !_closed)
            {
                _onThisDay = _session.OnThisDayFor(days, OnThisDay.TodayFrom(TimeProvider.System));
            }

            UpdateOnThisDay(openByItself: false);

            if (OnThisDayCard.IsVisible && _onThisDay is { ShowsIcon: true })
            {
                ShowOnThisDayCard();
            }
        }

        // -----------------------------------------------------------------------------------------
        // Gallery and stars (CH-37, CH-38).
        // -----------------------------------------------------------------------------------------

        private async Task OpenGalleryAsync()
        {
            if (_closed || Navigation.NavigationStack.LastOrDefault() != this)
            {
                return;
            }

            await Navigation.PushAsync(new GalleryPage(_state, _session, this));
        }

        private async Task OpenStarredAsync()
        {
            if (_closed || Navigation.NavigationStack.LastOrDefault() != this)
            {
                return;
            }

            await Navigation.PushAsync(new StarredPage(_state, _session, this));
        }

        private async Task OpenMemoriesAsync()
        {
            if (_closed || Navigation.NavigationStack.LastOrDefault() != this)
            {
                return;
            }

            await Navigation.PushAsync(new MemoriesPage(_session, this));
        }

        /// <summary>
        /// A row of the Memories list (CH-56) was tapped: its details (read-only, whatever its layer says), pushed
        /// over the list, with Show in chat when there is a target (<see cref="MemoryList.ShowInChat"/>).
        /// </summary>
        internal async Task OpenMemoryDetailsAsync(MemoryKey key)
        {
            if (_closed || _session.MemoryDetails(key) is not { } details)
            {
                return;
            }

            var list = await _session.GetMemoryListAsync();

            if (_closed)
            {
                return;
            }

            Func<Task>? showInChat = _session.MemoryShowInChat(list, key) is { } target ? () => ShowMemoryInChatAsync(target) : null;

            await Navigation.PushAsync(new MemoryDetailsPage(details, _state.Library.ArchivesFolder, ShowMessageAsync, ShowEntryAsync, showInChat));
        }

        /// <summary>
        /// Show in chat from a memory opened from the Memories list: back to this page, at the row when it is
        /// visible, otherwise at its day; the On this day card goes once there, whichever it was.
        /// </summary>
        private async Task ShowMemoryInChatAsync(MemoryShowTarget target)
        {
            if (_closed)
            {
                return;
            }

            while (Navigation.NavigationStack.Count > 1 && Navigation.NavigationStack[^1] != this)
            {
                await Navigation.PopAsync(animated: false);
            }

            var shown = target.Row is { } key
                ? await OpenAtMemoryAsync(key)
                : target.Day is { } day && await GoToDayAsync(day);

            if (shown)
            {
                OnThisDayCard.IsVisible = false;
            }
        }

        /// <summary>
        /// Show in chat, or a starred message tapped: back to this page, and the conversation opened at the
        /// message, briefly highlighted.
        /// </summary>
        internal async Task ShowMessageAsync(int sequenceNumber)
        {
            if (_closed)
            {
                return;
            }

            while (Navigation.NavigationStack.Count > 1 && Navigation.NavigationStack[^1] != this)
            {
                await Navigation.PopAsync(animated: false);
            }

            await OpenAtMessageAsync(sequenceNumber);
        }

        /// <summary>A star was added or removed: the loaded bubbles and a built gallery follow.</summary>
        private void OnStarsChanged(object? sender, EventArgs e)
        {
            if (_closed)
            {
                return;
            }

            _timeline.RefreshStars();

            if (_session.GalleryIfBuilt is { } gallery)
            {
                gallery.RefreshStars(_session.Stars);
            }
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

            await Navigation.PushAsync(new SettingsPage(_session, OnMeChangedAsync, RemoveArchiveAsync, OnThisDayChanged, OnLayersChangedAsync));
        }

        /// <summary>
        /// A memory layer or filter was switched in Settings (CH-42): the rows are rebuilt at the same place
        /// (the same path as a change of "me", so a switch made during a read is not dropped), and the date
        /// picker reads its days again.
        /// </summary>
        private Task OnLayersChangedAsync()
        {
            _layersVersion++;
            _calendar = null;

            return OnMeChangedAsync();
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

            // Its noted dates no longer notify (CH-47).
            NotedDateNotifier.Cancel(archive.Id);
            _ = NotedDateNotifier.UpdateAsync();

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
                await Navigation.PushAsync(new ImageViewerPage(_state, new PhotoBrowser([card], 0), _session.Stars, showInChat: null));
                return;
            }

            await OpenInAnotherAppAsync(this, _state, path, card.MimeType ?? MediaMimeTypes.Fallback);
        }

        /// <summary>An audio file the app's player cannot play: offered to another app instead.</summary>
        private async void OnPlayFailed(object? sender, AttachmentCard card)
        {
            if (_closed || Navigation.NavigationStack.LastOrDefault() != this || card.FullPath is not { } path)
            {
                return;
            }

            try
            {
                if (await DisplayAlertAsync("Play", "This file cannot be played here. Open it in another app?", "Open", "Cancel"))
                {
                    await OpenInAnotherAppAsync(this, _state, path, card.MimeType ?? MediaMimeTypes.Fallback);
                }
            }
            catch (Exception)
            {
                // A tap handler: nothing here may end the app.
            }
        }

        /// <summary>A memory row was tapped: its details, read from what the archive gave at open.</summary>
        private async void OnMemoryTapped(object? sender, MemoryRow row)
        {
            if (_closed || Navigation.NavigationStack.LastOrDefault() != this || _session.MemoryDetails(row.Key) is not { } details)
            {
                return;
            }

            try
            {
                await Navigation.PushAsync(new MemoryDetailsPage(details, _state.Library.ArchivesFolder, ShowMessageAsync, ShowEntryAsync));
            }
            catch (Exception)
            {
                // A tap handler: nothing here may end the app.
            }
        }

        /// <summary>A link to another entry: back to this page, at that entry's first visible row.</summary>
        private async Task ShowEntryAsync(long entryID)
        {
            if (_closed)
            {
                return;
            }

            if (_session.Memories.FirstRowOf(entryID, _session.MemoryView) is not { } key)
            {
                await DisplayAlertAsync("Memories", "That memory is not shown now: its layer or filter is off in Settings.", "OK");
                return;
            }

            while (Navigation.NavigationStack.Count > 1 && Navigation.NavigationStack[^1] != this)
            {
                await Navigation.PopAsync(animated: false);
            }

            await OpenAtMemoryAsync(key);
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
        /// A long press on a message: Share (CH-34) offers the text only, no date or sender, to the share
        /// sheet, and nothing leaves the app until she picks Share and then an app; Star / Unstar (CH-38)
        /// keeps the message in the Starred list, in the app's settings only.
        /// </summary>
        private async void OnMessageLongPressed(object? sender, MessageRow message)
        {
            if (_closed || Navigation.NavigationStack.LastOrDefault() != this || !message.HasActions)
            {
                return;
            }

            const string Share = "Share";
            var star = message.IsStarred ? "Unstar" : "Star";
            var actions = new List<string>(2);

            if (message.CanShareText)
            {
                actions.Add(Share);
            }

            if (message.CanStar)
            {
                actions.Add(star);
            }

            try
            {
                var chosen = await DisplayActionSheetAsync(null, "Cancel", null, [.. actions]);

                if (chosen == star && message.CanStar)
                {
                    _session.Stars.SetMessage(message.SequenceNumber, !message.IsStarred);
                    return;
                }

                if (chosen != Share || message.ShareText is not { } text)
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

    /// <summary>Picks a row's template: separator, my message, theirs, a system line, or a memory row.</summary>
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

        /// <summary>A PSN period card (CH-42).</summary>
        public DataTemplate PeriodCard { get; set; } = new();

        /// <summary>A "different times" line (CH-42).</summary>
        public DataTemplate DifferentTimes { get; set; } = new();

        /// <summary>A personal memory card (CH-42).</summary>
        public DataTemplate EntryCard { get; set; } = new();

        /// <inheritdoc />
        protected override DataTemplate OnSelectTemplate(object item, BindableObject container) => item switch
        {
            DateSeparatorRow => Separator,
            PeriodCardRow => PeriodCard,
            DifferentTimesRow => DifferentTimes,
            EntryCardRow => EntryCard,
            MessageRow { IsSystem: true } => System,
            MessageRow { IsMine: true } => Mine,
            _ => Theirs,
        };
    }
}
