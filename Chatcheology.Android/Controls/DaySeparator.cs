using Chatcheology.Mobile.Conversation;
using AView = Android.Views.View;

namespace Chatcheology.Android.Controls
{
    /// <summary>
    /// A day's heading in the chat (CH-54 step 2): a long press offers "Add a memory on this day". Android's own long
    /// click on the heading's view, as <see cref="MessageLongPress"/> does for bubbles (MAUI has no long-press gesture).
    /// </summary>
    public sealed class DaySeparator : Border
    {
        /// <summary>A day heading.</summary>
        public DaySeparator()
        {
            HandlerChanging += (_, e) =>
            {
                if (e.OldHandler?.PlatformView is AView old)
                {
                    old.LongClick -= OnLongClick;
                }
            };

            HandlerChanged += (_, _) =>
            {
                if (Handler?.PlatformView is AView view)
                {
                    view.LongClick += OnLongClick;
                }
            };
        }

        /// <summary>Raised with the day's row when its heading is long-pressed.</summary>
        internal static event EventHandler<DateSeparatorRow>? Pressed;

        private void OnLongClick(object? sender, AView.LongClickEventArgs e)
        {
            if (BindingContext is DateSeparatorRow row && Pressed is { } pressed)
            {
                e.Handled = true;
                pressed(this, row);
            }
            else
            {
                e.Handled = false;
            }
        }
    }
}
