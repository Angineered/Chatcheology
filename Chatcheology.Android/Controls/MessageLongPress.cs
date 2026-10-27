using Chatcheology.Mobile.Conversation;
using AView = Android.Views.View;

namespace Chatcheology.Android.Controls
{
    /// <summary>
    /// A long press on a message bubble (CH-34, CH-38). A message with anything to offer answers it
    /// (<see cref="MessageRow.HasActions"/>: Share for text, Star / Unstar for any message but a system
    /// line); the conversation page offers the actions.
    /// </summary>
    /// <remarks>
    /// Android's own long click on the bubble's view, on its text's view and on its attachment cards' views:
    /// MAUI has no long-press gesture of its own, and a text with tappable links or a card with a tap takes
    /// its own touches, so each is watched. A card's message is found through its parents.
    /// </remarks>
    internal static class MessageLongPress
    {
        /// <summary>Raised with the message when a bubble with actions is long-pressed.</summary>
        internal static event EventHandler<MessageRow>? Pressed;

        /// <summary>Watches <paramref name="element"/>'s Android view, whichever view it gets, for a long press.</summary>
        internal static void Watch(VisualElement element)
        {
            void OnLongClick(object? sender, AView.LongClickEventArgs e)
            {
                if (MessageOf(element) is { HasActions: true } row)
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

        /// <summary>The message <paramref name="element"/> belongs to: its own binding context, or its nearest parent's.</summary>
        private static MessageRow? MessageOf(Element element)
        {
            for (Element? at = element; at is not null; at = at.Parent)
            {
                if (at.BindingContext is MessageRow row)
                {
                    return row;
                }
            }

            return null;
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
