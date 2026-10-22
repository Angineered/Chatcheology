using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Android.Controls
{
    /// <summary>
    /// One attachment in the CollectionView: an image at its final size, or a card with a heading, plus
    /// the low-quality label or note when one applies.
    /// </summary>
    /// <remarks>
    /// The image's height is set from the recorded size (capped by <see cref="AttachmentCard.HeightFor"/>)
    /// before the file is decoded, so the row never changes height when the picture arrives and the list
    /// has nothing to correct. Tapping, full screen and opening in another app come in step 3.
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

            var resources = Application.Current!.Resources;
            var muted = (Color)resources["MutedText"];

            View main = card.Kind == AttachmentCardKind.Image && card.FullPath is { } path
                ? new Image
                {
                    Source = ImageSource.FromFile(path),
                    WidthRequest = ImageWidth,
                    HeightRequest = card.HeightFor(ImageWidth),
                    Aspect = Aspect.AspectFill,
                }
                : new Border
                {
                    BackgroundColor = (Color)resources["CardBackground"],
                    StrokeThickness = 0,
                    Padding = new Thickness(10, 8),
                    WidthRequest = ImageWidth,
                    Content = new Label { Text = card.Heading, FontSize = 14, TextColor = muted },
                };

            var extra = card.LowQualityLabel.Length > 0 ? card.LowQualityLabel : card.LowQualityNote;

            Content = extra.Length == 0
                ? main
                : new VerticalStackLayout
                {
                    Spacing = 2,
                    Children = { main, new Label { Text = extra, FontSize = 12, TextColor = muted, WidthRequest = ImageWidth } },
                };
        }
    }
}
