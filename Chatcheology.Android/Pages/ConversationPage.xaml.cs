using AndroidX.RecyclerView.Widget;
using Chatcheology.Android.Conversation;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Android.Pages
{
    /// <summary>The conversation in MAUI's CollectionView.</summary>
    /// <remarks>
    /// Opens where the reader last was (the sequence number at the top of the screen, stored per
    /// archive), or at the newest messages the first time. The position is stored as she scrolls.
    /// </remarks>
    public partial class ConversationPage : ContentPage
    {
        private readonly ArchiveSession _session;
        private readonly ConversationTimeline _timeline;
        private readonly ConversationPager _pager;
        private bool _opened;
        private int? _savedPosition;

        /// <summary>
        /// A page over an open session, which the caller opens off the UI thread. The page owns the
        /// session from here on and closes it when it is closed.
        /// </summary>
        internal ConversationPage(ArchiveSession session)
        {
            InitializeComponent();

            _session = session;
            _timeline = session.CreateTimeline();
            _pager = new ConversationPager(_timeline, "CollectionView");

            Title = session.Title;
            List.ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepItemsInView;

            List.HandlerChanged += (_, _) => _pager.List = List.Handler?.PlatformView as RecyclerView;
            List.Scrolled += (_, e) =>
            {
                _pager.OnVisibleRange(e.FirstVisibleItemIndex, e.LastVisibleItemIndex);
                SavePosition(e.FirstVisibleItemIndex);
            };

#if DEBUG
            // The trial's controlled step: a page added above with no scrolling under way, so a
            // screenshot before and after shows whether anything on screen moved.
            ToolbarItems.Add(new ToolbarItem("Top", null, () => List.ScrollTo(0, position: ScrollToPosition.Start, animate: false)));
            ToolbarItems.Add(new ToolbarItem("Load earlier", null, () => _ = LoadEarlierNow()));
#endif
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

            OpenedAt opened;

            try
            {
                // Built before the list is bound, so nothing is observing the rows yet.
                opened = await _pager.OpenAsync(() => _session.OpenTimeline(_timeline));
            }
            catch (Exception exception) when (exception is ArchiveFormatException or SqliteException)
            {
                List.EmptyView = "This archive could not be read.";
                return;
            }

            List.ItemsSource = _timeline.Rows;

            if (opened.RowIndex >= 0)
            {
                List.ScrollTo(
                    opened.RowIndex,
                    position: opened.AtLatest ? ScrollToPosition.End : ScrollToPosition.Start,
                    animate: false);
            }

            // Let the first placement settle before scrolling may load more or move the stored position.
            await Task.Delay(500);
            _pager.Ready = true;
        }

        /// <inheritdoc />
        protected override async void OnNavigatedFrom(NavigatedFromEventArgs args)
        {
            base.OnNavigatedFrom(args);

            if (Navigation.NavigationStack.Contains(this))
            {
                return;
            }

            // A page read may still be running on the reader's connection: let it finish first.
            await _pager.StopAsync();
            _session.Dispose();
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

#if DEBUG
        private async Task LoadEarlierNow()
        {
            (List.Handler?.PlatformView as RecyclerView)?.StopScroll();
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
