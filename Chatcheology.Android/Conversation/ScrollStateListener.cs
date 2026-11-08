using AndroidX.RecyclerView.Widget;

namespace Chatcheology.Android.Conversation
{
    /// <summary>
    /// Reports the list's scroll state (idle, dragged by the reader, settling) for the floating date (CH-65).
    /// MAUI's <c>Scrolled</c> event does not say whether the reader's finger moved the list or code did;
    /// the state does: code placing the list never reports a drag.
    /// </summary>
    /// <remarks>
    /// Never throws into Android. <see cref="Detach"/> drops the callback, so a listener left on a list after
    /// the page closed calls nothing.
    /// </remarks>
    internal sealed class ScrollStateListener : RecyclerView.OnScrollListener
    {
        private Action<int>? _changed;

        internal ScrollStateListener(Action<int> changed)
        {
            _changed = changed;
        }

        /// <summary>Stops reporting; later state changes call nothing.</summary>
        internal void Detach() => _changed = null;

        /// <inheritdoc />
        public override void OnScrollStateChanged(RecyclerView recyclerView, int newState)
        {
            try
            {
                _changed?.Invoke(newState);
            }
            catch (Exception)
            {
                // A scroll callback: nothing here may end the app.
            }
        }
    }
}
