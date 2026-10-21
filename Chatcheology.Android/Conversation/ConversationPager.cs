using AndroidX.RecyclerView.Widget;
using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Android.Conversation
{
    /// <summary>
    /// Loads the next page in either direction when the list nears an end, one page at a time.
    /// </summary>
    /// <remarks>
    /// The page is read on a worker thread and applied on the UI thread as one range insert. The CH-26
    /// trial found both lists (MAUI's CollectionView, and a native RecyclerView, which is what the
    /// CollectionView is underneath) keep the message on screen exactly in place through such an
    /// insert, so nothing here moves the list.
    /// </remarks>
    internal sealed class ConversationPager
    {
        private readonly ConversationTimeline _timeline;
        private readonly string _listName;
        private bool _loading;

        internal ConversationPager(ConversationTimeline timeline, string listName)
        {
            _timeline = timeline;
            _listName = listName;
        }

        /// <summary>The list's Android view, once its handler is connected.</summary>
        internal RecyclerView? List { get; set; }

        /// <summary>Whether loading may start; off until the list has been placed at its first position.</summary>
        internal bool Ready { get; set; }

        /// <summary>Loads the page before the loaded rows, unless one is loading or there is none.</summary>
        internal async Task LoadEarlierAsync()
        {
            if (!Ready || _loading || !_timeline.HasEarlier)
            {
                return;
            }

            _loading = true;

            try
            {
                var page = await Task.Run(_timeline.ReadEarlier);

                if (page is null)
                {
                    return;
                }

#if DEBUG
                var anchor = ScrollAnchor.Capture(List, _timeline.Rows);
                var added = _timeline.Prepend(page);
                ScrollTrialLog.AfterPrepend(_listName, List, _timeline.Rows, anchor, added);
#else
                _timeline.Prepend(page);
#endif
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>Loads the page after the loaded rows, unless one is loading or there is none.</summary>
        internal async Task LoadLaterAsync()
        {
            if (!Ready || _loading || !_timeline.HasLater)
            {
                return;
            }

            _loading = true;

            try
            {
                if (await Task.Run(_timeline.ReadLater) is { } page)
                {
                    _timeline.Append(page);
                }
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>Asks for a page when the visible rows come within a few rows of either end.</summary>
        internal void OnVisibleRange(int first, int last)
        {
            const int Margin = 15;

            if (first >= 0 && first <= Margin)
            {
                _ = LoadEarlierAsync();
            }
            else if (last >= 0 && last >= _timeline.Rows.Count - 1 - Margin)
            {
                _ = LoadLaterAsync();
            }
        }
    }

#if DEBUG
    /// <summary>Debug builds only: the first message row on screen and its top edge, in the list's pixels.</summary>
    internal sealed record ScrollAnchor(int Index, int SequenceNumber, int Top)
    {
        /// <summary>The topmost message row with any part on screen, or <see langword="null"/>.</summary>
        internal static ScrollAnchor? Capture(RecyclerView? list, IList<ConversationRow> rows)
        {
            if (list is null)
            {
                return null;
            }

            ScrollAnchor? anchor = null;

            for (var i = 0; i < list.ChildCount; i++)
            {
                var child = list.GetChildAt(i);

                if (child is null || child.Bottom <= list.PaddingTop)
                {
                    continue;
                }

                var index = list.GetChildAdapterPosition(child);

                if (index < 0 || index >= rows.Count || rows[index] is not MessageRow message)
                {
                    continue;
                }

                if (anchor is null || child.Top < anchor.Top)
                {
                    anchor = new ScrollAnchor(index, message.SequenceNumber, child.Top);
                }
            }

            return anchor;
        }
    }
#endif
}
