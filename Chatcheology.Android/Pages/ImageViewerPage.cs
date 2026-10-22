using Chatcheology.Android.Controls;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Presentation;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// One picture full screen, with pinch zoom, panning and double-tap. If it does not decode, it says
    /// so and offers to open the file in another app.
    /// </summary>
    /// <remarks>
    /// Two small icons in the top corner (CH-34): Share hands this one file to the share sheet, and Info
    /// shows the archive's details of the photo in a panel at the bottom; a tap on the panel closes it.
    /// </remarks>
    internal sealed class ImageViewerPage : ContentPage
    {
        private readonly ZoomImage _image = new();
        private readonly Border _info;

        internal ImageViewerPage(AppState state, AttachmentCard card)
        {
            var path = card.FullPath!;
            var mimeType = card.MimeType ?? MediaMimeTypes.Fallback;

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

            _info = InfoPanel(card.Details);

            var share = new CornerIcon(CornerGlyph.Share, "Share");
            share.Tapped += async (_, _) =>
            {
                switch (ExternalOpener.ShareFile(path, mimeType, state.Library.ArchivesFolder))
                {
                    case OpenResult.NoApp:
                        await DisplayAlertAsync("Share", "No app on this phone can receive this file.", "OK");
                        break;
                    case OpenResult.NotAllowed:
                        await DisplayAlertAsync("Share", "This file cannot be shared.", "OK");
                        break;
                }
            };

            var info = new CornerIcon(CornerGlyph.Info, "Info");
            info.Tapped += (_, _) => _info.IsVisible = !_info.IsVisible;

            var corner = new HorizontalStackLayout
            {
                Spacing = 0,
                Padding = 4,
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Start,
                Children = { share, info },
            };

            Content = new Grid { Children = { _image, failed, corner, _info } };

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

        /// <summary>The Info panel: one line per detail the archive holds, hidden until Info is tapped.</summary>
        private static Border InfoPanel(IReadOnlyList<MediaDetail> details)
        {
            var grid = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
                ColumnSpacing = 16,
                RowSpacing = 6,
            };

            for (var row = 0; row < details.Count; row++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                grid.Add(new Label { Text = details[row].Label, FontSize = 13, TextColor = Colors.White.WithAlpha(0.65f) }, 0, row);
                grid.Add(new Label { Text = details[row].Value, FontSize = 14, TextColor = Colors.White, LineBreakMode = LineBreakMode.CharacterWrap }, 1, row);
            }

            var panel = new Border
            {
                IsVisible = false,
                BackgroundColor = Color.FromRgba(0, 0, 0, 0.8),
                StrokeThickness = 0,
                Padding = new Thickness(16, 14),
                VerticalOptions = LayoutOptions.End,
                Content = grid,
            };

            var close = new TapGestureRecognizer();
            close.Tapped += (_, _) => panel.IsVisible = false;
            panel.GestureRecognizers.Add(close);

            return panel;
        }
    }
}
