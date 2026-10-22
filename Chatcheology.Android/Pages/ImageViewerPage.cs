using Chatcheology.Android.Controls;
using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// One picture full screen, with pinch zoom, panning and double-tap. If it does not decode, it says
    /// so and offers to open the file in another app.
    /// </summary>
    internal sealed class ImageViewerPage : ContentPage
    {
        internal ImageViewerPage(AppState state, string path, string mimeType)
        {
            Title = "Photo";
            BackgroundColor = Colors.Black;

            var image = new ZoomImage();
            var failed = new VerticalStackLayout
            {
                IsVisible = false,
                Spacing = 12,
                Padding = 24,
                VerticalOptions = LayoutOptions.Center,
                Children = { new Label { Text = AttachmentCard.CannotPreviewHeading, TextColor = Colors.White, HorizontalTextAlignment = TextAlignment.Center } },
            };

            image.Decoded += (_, ok) =>
            {
                image.IsVisible = ok;
                failed.IsVisible = !ok;
            };

            var open = new ToolbarItem("Open in another app", null, async () =>
                await ConversationPage.OpenInAnotherAppAsync(this, state, path, mimeType));
            ToolbarItems.Add(open);

            var button = new Button { Text = "Open in another app" };
            button.Clicked += async (_, _) => await ConversationPage.OpenInAnotherAppAsync(this, state, path, mimeType);
            failed.Children.Add(button);

            Content = new Grid { Children = { image, failed } };

            image.Path = path;
        }
    }
}
