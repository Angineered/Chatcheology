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
        private readonly ZoomImage _image = new();

        internal ImageViewerPage(AppState state, string path, string mimeType)
        {
            Title = "Photo";
            BackgroundColor = Colors.Black;

            var failed = new VerticalStackLayout
            {
                IsVisible = false,
                Spacing = 12,
                Padding = 24,
                VerticalOptions = LayoutOptions.Center,
                Children = { new Label { Text = AttachmentCard.CannotPreviewHeading, TextColor = Colors.White, HorizontalTextAlignment = TextAlignment.Center } },
            };

            _image.Decoded += (_, ok) =>
            {
                _image.IsVisible = ok;
                failed.IsVisible = !ok;
            };

            ToolbarItems.Add(new ToolbarItem("Open in another app", null, async () =>
                await ConversationPage.OpenInAnotherAppAsync(this, state, path, mimeType)));

            var button = new Button { Text = "Open in another app" };
            button.Clicked += async (_, _) => await ConversationPage.OpenInAnotherAppAsync(this, state, path, mimeType);
            failed.Children.Add(button);

            Content = new Grid { Children = { _image, failed } };

            _image.Path = path;
        }

        /// <inheritdoc />
        protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
        {
            base.OnNavigatedFrom(args);

            // Closed (not just covered): release the full-screen bitmap now rather than at the next
            // garbage collection, and let any decode still running land nowhere.
            if (!Navigation.NavigationStack.Contains(this))
            {
                _image.Handler?.DisconnectHandler();
            }
        }
    }
}
