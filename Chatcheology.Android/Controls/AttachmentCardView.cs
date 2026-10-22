using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Android.Controls
{
    /// <summary>
    /// One attachment in the conversation: a picture at its final size, or a card with a heading, plus
    /// the low-quality label or note when one applies. Tapping a picture shows it full screen; tapping a
    /// restored file (or a picture that would not decode) opens it in another app.
    /// </summary>
    /// <remarks>
    /// The picture's height is set from the recorded size (capped by <see cref="AttachmentCard.HeightFor"/>)
    /// before the file is decoded, and a picture that fails to decode shows its heading in the same box,
    /// so the row never changes height and the list has nothing to correct.
    /// </remarks>
    public sealed class AttachmentCardView : ContentView
    {
        /// <summary>The width a picture or card is shown at, in device-independent units.</summary>
        internal const double ImageWidth = 240;

        /// <summary>Raised when a card is tapped; the conversation page decides what to do.</summary>
        internal static event EventHandler<AttachmentTapped>? Tapped;

        private readonly DecodedImage _image = new() { WidthRequest = ImageWidth };
        private readonly Label _heading = new() { FontSize = 14 };
        private readonly Border _card;
        private readonly Label _extra = new() { FontSize = 12, WidthRequest = ImageWidth };
        private readonly VerticalStackLayout _stack;

        /// <summary>A card view, filled from its binding context.</summary>
        public AttachmentCardView()
        {
            _heading.TextIn("MutedText");
            _extra.TextIn("MutedText");

            _card = new Border
            {
                StrokeThickness = 0,
                Padding = new Thickness(10, 8),
                WidthRequest = ImageWidth,
                Content = _heading,
            }.Themed(BackgroundColorProperty, "CardBackground");

            _stack = new VerticalStackLayout { Spacing = 2 };
            Content = _stack;

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                if (BindingContext is AttachmentCard card)
                {
                    Tapped?.Invoke(this, new AttachmentTapped(card, card.Kind == AttachmentCardKind.Image && _image.HasFailed));
                }
            };

            GestureRecognizers.Add(tap);
        }

        /// <inheritdoc />
        protected override void OnBindingContextChanged()
        {
            base.OnBindingContextChanged();

            _stack.Children.Clear();

            if (BindingContext is not AttachmentCard card)
            {
                _image.Path = null;
                return;
            }

            if (card.Kind == AttachmentCardKind.Image && card.FullPath is { } path)
            {
                // The size first: the decode reads it when the path changes.
                _image.HeightRequest = card.HeightFor(ImageWidth);
                _image.Path = path;
                _stack.Children.Add(_image);
            }
            else
            {
                _image.Path = null;
                _heading.Text = card.Heading;
                _stack.Children.Add(_card);
            }

            var extra = card.LowQualityLabel.Length > 0 ? card.LowQualityLabel : card.LowQualityNote;

            if (extra.Length > 0)
            {
                _extra.Text = extra;
                _stack.Children.Add(_extra);
            }
        }
    }

    /// <summary>A tapped attachment card.</summary>
    /// <param name="Card">The card.</param>
    /// <param name="DecodeFailed">True for a picture that would not decode: it is opened in another app instead.</param>
    internal sealed record AttachmentTapped(AttachmentCard Card, bool DecodeFailed);
}
