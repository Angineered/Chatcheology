using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Android.Controls
{
    /// <summary>
    /// One attachment in the CollectionView: an image at its final size, or a card with a heading.
    /// </summary>
    /// <remarks>
    /// The image's height is set from the recorded size before the file is decoded, so the row never
    /// changes height when the picture arrives and the list has nothing to correct.
    /// </remarks>
    public sealed class AttachmentCardView : ContentView
    {
        /// <summary>The width an image is shown at, in device-independent units.</summary>
        internal const double ImageWidth = 240;

        /// <inheritdoc />
        protected override void OnBindingContextChanged()
        {
            base.OnBindingContextChanged();

            if (BindingContext is not AttachmentCard card)
            {
                Content = null;
                return;
            }

            Content = card.Kind == AttachmentCardKind.Image && card.FullPath is { } path
                ? new Image
                {
                    Source = ImageSource.FromFile(path),
                    WidthRequest = ImageWidth,
                    HeightRequest = card.HeightFor(ImageWidth),
                    Aspect = Aspect.AspectFill,
                }
                : new Border
                {
                    BackgroundColor = (Color)Application.Current!.Resources["CardBackground"],
                    StrokeThickness = 0,
                    Padding = new Thickness(10, 8),
                    WidthRequest = ImageWidth,
                    Content = new Label
                    {
                        Text = card.Heading,
                        FontSize = 14,
                        TextColor = (Color)Application.Current.Resources["MutedText"],
                    },
                };
        }
    }
}
