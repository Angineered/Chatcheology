#if DEBUG
using AndroidX.RecyclerView.Widget;
using Chatcheology.Mobile.Conversation;
using AndroidLog = global::Android.Util.Log;
using AndroidView = global::Android.Views.View;

namespace Chatcheology.Android.Conversation
{
    /// <summary>
    /// Debug builds only: logs, after a page is added above, where the message that was on screen
    /// went. Sequence numbers and pixel offsets only; no message text, names or file names.
    /// </summary>
    /// <remarks>
    /// Read with <c>adb logcat -s ChatcheologyTrial</c>. The anchor's top is read twice: just before
    /// the next frame is drawn (the list's own reaction to the insert), and 600 ms later (anything a
    /// list does after the fact, such as a delayed scroll). A steady list shows the same top both
    /// times as before the insert. While the reader is scrolling, the list's own scroll in the same
    /// time is added back (<c>scrolled</c>), so <c>jump</c> is the movement the insert caused and
    /// nothing else: 0 for a steady list.
    /// </remarks>
    internal static class ScrollTrialLog
    {
        private const string Tag = "ChatcheologyTrial";

        internal static void AfterPrepend(
            string listName, RecyclerView? list, IList<ConversationRow> rows, ScrollAnchor? anchor, int added)
        {
            if (list is null || anchor is null)
            {
                AndroidLog.Info(Tag, $"list={listName} added={added} anchor=none");
                return;
            }

            var target = anchor.Index + added;
            var observer = list.ViewTreeObserver;

            if (observer is null)
            {
                return;
            }

            var scrolled = new ScrollCounter();
            list.AddOnScrollListener(scrolled);

            var listener = new OneShotPreDraw();
            listener.Action = () =>
            {
                list.ViewTreeObserver?.RemoveOnPreDrawListener(listener);
                AndroidLog.Info(Tag, Line(listName, "frame", list, rows, anchor, added, target, scrolled.Total));
            };

            observer.AddOnPreDrawListener(listener);

            list.PostDelayed(
                () =>
                {
                    list.RemoveOnScrollListener(scrolled);
                    AndroidLog.Info(Tag, Line(listName, "600ms", list, rows, anchor, added, target, scrolled.Total));
                },
                600);
        }

        private static string Line(
            string listName, string when, RecyclerView list, IList<ConversationRow> rows, ScrollAnchor anchor, int added, int target, int scrolled)
        {
            var holder = list.FindViewHolderForAdapterPosition(target);
            var now = ScrollAnchor.Capture(list, rows);

            var top = holder?.ItemView is AndroidView view ? view.Top.ToString(System.Globalization.CultureInfo.InvariantCulture) : "offscreen";
            var delta = holder?.ItemView is AndroidView moved ? (moved.Top - anchor.Top).ToString(System.Globalization.CultureInfo.InvariantCulture) : "n/a";

            // Scrolling towards the top has a negative dy and moves every row down by -dy.
            var jump = holder?.ItemView is AndroidView shifted ? (shifted.Top - anchor.Top + scrolled).ToString(System.Globalization.CultureInfo.InvariantCulture) : "offscreen";

            return $"list={listName} when={when} added={added} anchorSeq={anchor.SequenceNumber} " +
                   $"topBefore={anchor.Top} topAfter={top} delta={delta} scrolled={scrolled} jump={jump} firstSeqNow={now?.SequenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}";
        }

        private sealed class ScrollCounter : RecyclerView.OnScrollListener
        {
            internal int Total { get; private set; }

            public override void OnScrolled(RecyclerView recyclerView, int dx, int dy) => Total += dy;
        }

        private sealed class OneShotPreDraw : Java.Lang.Object, global::Android.Views.ViewTreeObserver.IOnPreDrawListener
        {
            internal Action? Action { get; set; }

            public bool OnPreDraw()
            {
                var action = Action;
                Action = null;
                action?.Invoke();

                return true;
            }
        }
    }
}
#endif
