using AndroidX.RecyclerView.Widget;
using Chatcheology.Android.Conversation;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Android.Pages
{
    /// <summary>The conversation in MAUI's CollectionView.</summary>
    public partial class ConversationPage : ContentPage
    {
        private readonly ArchiveReader _reader;
        private readonly ConversationTimeline _timeline;
        private readonly ConversationPager _pager;
        private bool _opened;

        /// <summary>
        /// A page over an open reader and its timeline, which the caller builds off the UI thread. The
        /// page owns the reader from here on and closes it when it is closed.
        /// </summary>
        internal ConversationPage(ArchiveReader reader, ConversationTimeline timeline, ItemsUpdatingScrollMode mode)
        {
            InitializeComponent();

            _reader = reader;
            _timeline = timeline;
            _pager = new ConversationPager(_timeline, $"CollectionView-{mode}");

            Title = reader.Info.ConversationTitle;
            List.ItemsUpdatingScrollMode = mode;

            List.HandlerChanged += (_, _) => _pager.List = List.Handler?.PlatformView as RecyclerView;
            List.Scrolled += (_, e) => _pager.OnVisibleRange(e.FirstVisibleItemIndex, e.LastVisibleItemIndex);

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

            int last;

            try
            {
                // Built before the list is bound, so nothing is observing the rows yet.
                last = await _pager.OpenAtLatestAsync();
            }
            catch (Exception exception) when (exception is ArchiveFormatException or SqliteException)
            {
                List.EmptyView = "This archive could not be read.";
                return;
            }

            List.ItemsSource = _timeline.Rows;

            if (last >= 0)
            {
                List.ScrollTo(last, position: ScrollToPosition.End, animate: false);
            }

            // Let the first placement settle before scrolling may load more.
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
            _reader.Dispose();
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
