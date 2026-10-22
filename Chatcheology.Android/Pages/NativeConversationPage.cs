using AndroidX.RecyclerView.Widget;
using Chatcheology.Android.Controls;
using Chatcheology.Android.Conversation;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Microsoft.Data.Sqlite;

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

        /// <summary>
        /// A page over an open reader and its timeline, which the caller builds off the UI thread. The
        /// page owns the reader from here on and closes it when it is closed.
        /// </summary>
        internal NativeConversationPage(ArchiveReader reader, ConversationTimeline timeline)
        {
            _reader = reader;
            _timeline = timeline;
            _pager = new ConversationPager(_timeline, "RecyclerView");
            _list = new NativeConversationList(_timeline, _pager);

            Title = reader.Info.ConversationTitle;
            BackgroundColor = (Color)Application.Current!.Resources["ChatBackground"];

#if DEBUG
            ToolbarItems.Add(new ToolbarItem("Top", null, () =>
                ((_list.Handler?.PlatformView as RecyclerView)?.GetLayoutManager() as LinearLayoutManager)?.ScrollToPositionWithOffset(0, 0)));
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

            int last;

            try
            {
                // The rows are complete before the list exists, so its adapter starts with them.
                last = await _pager.OpenAtLatestAsync();
            }
            catch (Exception exception) when (exception is ArchiveFormatException or SqliteException)
            {
                Content = new Label { Text = "This archive could not be read.", Margin = new Thickness(16) };
                return;
            }

            // Placed when its handler connects, before the first layout.
            _list.InitialRow = last;
            Content = _list;

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
    }
}
