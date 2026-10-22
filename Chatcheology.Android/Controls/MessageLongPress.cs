using Chatcheology.Mobile.Conversation;
using AView = Android.Views.View;

namespace Chatcheology.Android.Controls
{
    /// <summary>
    /// A long press on a message bubble (CH-34). Only a message with text to share answers it
    /// (<see cref="MessageRow.CanShareText"/>); the conversation page offers Share.
    /// </summary>
    /// <remarks>
    /// Android's own long click on the bubble's view and on its text's view: MAUI has no long-press
    /// gesture of its own, and a text with tappable links takes its own touches, so both are watched.
    /// </remarks>
    internal static class MessageLongPress
    {
        /// <summary>Raised with the message when a bubble with text to share is long-pressed.</summary>
        internal static event EventHandler<MessageRow>? Pressed;

        /// <summary>Watches <paramref name="element"/>'s Android view, whichever view it gets, for a long press.</summary>
        internal static void Watch(VisualElement element)
        {
            void OnLongClick(object? sender, AView.LongClickEventArgs e)
            {
                if (element.BindingContext is MessageRow { CanShareText: true } row)
                {
                    e.Handled = true;
                    Pressed?.Invoke(element, row);
                }
                else
                {
                    e.Handled = false;
                }
            }

            element.HandlerChanging += (_, e) =>
            {
                if (e.OldHandler?.PlatformView is AView old)
                {
                    old.LongClick -= OnLongClick;
                }
            };

            element.HandlerChanged += (_, _) =>
            {
                if (element.Handler?.PlatformView is AView view)
                {
                    view.LongClick += OnLongClick;
                }
            };
        }
    }

    /// <summary>A message's bubble, which answers a long press (<see cref="MessageLongPress"/>).</summary>
    public sealed class MessageBubble : Border
    {
        /// <summary>A bubble.</summary>
        public MessageBubble()
        {
            MessageLongPress.Watch(this);
        }
    }
}
