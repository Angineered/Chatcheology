using AndroidX.RecyclerView.Widget;
using Chatcheology.Android.Controls;
using Chatcheology.Android.Conversation;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Android.Pages
{
    /// <summary>The conversation in a native Android RecyclerView.</summary>
    internal sealed class NativeConversationPage : ContentPage
    {
        private readonly ArchiveReader _reader;
        private readonly ConversationTimeline _timeline;
        private readonly ConversationPager _pager;
        private readonly NativeConversationList _list;
        private bool _opened;

        internal NativeConversationPage(ArchiveReader reader)
        {
            _reader = reader;
            _timeline = new ConversationTimeline(reader, reader.Info.LocalParticipantID);
            _pager = new ConversationPager(_timeline, "RecyclerView");
            _list = new NativeConversationList(_timeline, _pager);

            Title = reader.Info.ConversationTitle;
            BackgroundColor = (Color)Application.Current!.Resources["ChatBackground"];

#if DEBUG
            ToolbarItems.Add(new ToolbarItem("Load earlier", null, () =>
            {
                (_list.Handler?.PlatformView as RecyclerView)?.StopScroll();
                _ = _pager.LoadEarlierAsync();
            }));
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

            // The rows are complete before the list exists, so its adapter starts with them.
            var last = await Task.Run(_timeline.OpenAtLatest);

            // Placed when its handler connects, before the first layout.
            _list.InitialRow = last;
            Content = _list;

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
    }
}
