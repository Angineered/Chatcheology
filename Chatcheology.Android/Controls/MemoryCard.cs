using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Android.Controls
{
    /// <summary>
    /// The frame of a memory row (CH-42): a tap opens its details. No long press: a memory is never shared
    /// or starred.
    /// </summary>
    public sealed class MemoryCard : Border
    {
        /// <summary>A memory row's frame.</summary>
        public MemoryCard()
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                if (BindingContext is MemoryRow row)
                {
                    Tapped?.Invoke(this, row);
                }
            };

            GestureRecognizers.Add(tap);
        }

        /// <summary>Raised with the row when a memory row is tapped.</summary>
        internal static event EventHandler<MemoryRow>? Tapped;
    }
}
