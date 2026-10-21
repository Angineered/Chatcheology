using AndroidX.RecyclerView.Widget;
using Chatcheology.Android.Conversation;
using Chatcheology.Mobile.Conversation;
using Microsoft.Maui.Handlers;

namespace Chatcheology.Android.Controls
{
    /// <summary>
    /// The conversation as a native Android RecyclerView, hosted in a MAUI page (the trial's second
    /// candidate).
    /// </summary>
    internal sealed class NativeConversationList : View
    {
        internal NativeConversationList(ConversationTimeline timeline, ConversationPager pager)
        {
            Timeline = timeline;
            Pager = pager;
        }

        /// <summary>The rows to show.</summary>
        internal ConversationTimeline Timeline { get; }

        /// <summary>What loads more as the reader scrolls.</summary>
        internal ConversationPager Pager { get; }

        /// <summary>The row the list opens at, kept fully on screen; -1 for none.</summary>
        internal int InitialRow { get; set; } = -1;
    }

    /// <summary>Creates the RecyclerView and wires it to the timeline and pager.</summary>
    internal sealed class NativeConversationListHandler : ViewHandler<NativeConversationList, RecyclerView>
    {
        private static readonly PropertyMapper<NativeConversationList, NativeConversationListHandler> Mapper = new(ViewMapper);

        private ConversationAdapter? _adapter;
        private VisibleRangeListener? _listener;

        public NativeConversationListHandler()
            : base(Mapper)
        {
        }

        /// <inheritdoc />
        protected override RecyclerView CreatePlatformView()
        {
            var list = new RecyclerView(Context);
            list.SetLayoutManager(new LinearLayoutManager(Context));

            // No insert animation: the chat app shows a loaded page at once, and an animation would
            // move rows on screen while it ran.
            list.SetItemAnimator(null);

            return list;
        }

        /// <inheritdoc />
        protected override void ConnectHandler(RecyclerView platformView)
        {
            base.ConnectHandler(platformView);

            _adapter = new ConversationAdapter(Context, VirtualView.Timeline.Rows);
            platformView.SetAdapter(_adapter);

            _listener = new VisibleRangeListener(VirtualView.Pager);
            platformView.AddOnScrollListener(_listener);

            VirtualView.Pager.List = platformView;

            if (VirtualView.InitialRow >= 0)
            {
                platformView.ScrollToPosition(VirtualView.InitialRow);
            }
        }

        /// <inheritdoc />
        protected override void DisconnectHandler(RecyclerView platformView)
        {
            VirtualView.Pager.List = null;

            if (_listener is not null)
            {
                platformView.RemoveOnScrollListener(_listener);
                _listener = null;
            }

            platformView.SetAdapter(null);
            _adapter?.Detach();
            _adapter = null;

            base.DisconnectHandler(platformView);
        }

        private sealed class VisibleRangeListener(ConversationPager pager) : RecyclerView.OnScrollListener
        {
            public override void OnScrolled(RecyclerView recyclerView, int dx, int dy)
            {
                if (recyclerView.GetLayoutManager() is LinearLayoutManager layout)
                {
                    pager.OnVisibleRange(layout.FindFirstVisibleItemPosition(), layout.FindLastVisibleItemPosition());
                }
            }
        }
    }
}
