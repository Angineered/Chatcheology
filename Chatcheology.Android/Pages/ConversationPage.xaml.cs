using AndroidX.RecyclerView.Widget;
using Chatcheology.Android.Conversation;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Android.Pages
{
    /// <summary>The conversation in MAUI's CollectionView.</summary>
    public partial class ConversationPage : ContentPage
    {
        private readonly ArchiveReader _reader;
        private readonly ConversationTimeline _timeline;
        private readonly ConversationPager _pager;
        private bool _opened;

        internal ConversationPage(ArchiveReader reader, ItemsUpdatingScrollMode mode)
        {
            InitializeComponent();

            _reader = reader;
            _timeline = new ConversationTimeline(reader, reader.Info.LocalParticipantID);
            _pager = new ConversationPager(_timeline, $"CollectionView-{mode}");

            Title = reader.Info.ConversationTitle;
            List.ItemsUpdatingScrollMode = mode;

            List.HandlerChanged += (_, _) => _pager.List = List.Handler?.PlatformView as RecyclerView;
            List.Scrolled += (_, e) => _pager.OnVisibleRange(e.FirstVisibleItemIndex, e.LastVisibleItemIndex);

#if DEBUG
            // The trial's controlled step: a page added above with no scrolling under way, so a
            // screenshot before and after shows whether anything on screen moved.
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

            // Built before the list is bound, so nothing is observing the rows yet.
            var last = await Task.Run(_timeline.OpenAtLatest);

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
        protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
        {
            base.OnNavigatedFrom(args);

            if (Navigation.NavigationStack.Contains(this))
            {
                return;
            }

            _pager.Ready = false;
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
