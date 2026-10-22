using Chatcheology.Android.Controls;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Gallery;
using Chatcheology.Mobile.Presentation;

namespace Chatcheology.Android.Pages
{
    /// <summary>
    /// One picture full screen, with pinch zoom, panning and double-tap. If it does not decode, it says
    /// so and offers to open the file in another app.
    /// </summary>
    /// <remarks>
    /// <para>Small icons in the top corner (CH-34, CH-38): Share hands this one file to the share sheet,
    /// Star stars or unstars the photo (kept in the app's settings, never in the archive), and Info shows
    /// the archive's details of the photo in a panel at the bottom; a tap on the panel closes it.</para>
    /// <para>Opened from the gallery or the Starred list (CH-37), a swipe left or right, at 1x, moves to
    /// the next or previous photo of that list, and Show in chat opens the conversation at its message.</para>
    /// </remarks>
    internal sealed class ImageViewerPage : ContentPage
    {
        private readonly AppState _state;
        private readonly PhotoBrowser _browser;
        private readonly Stars _stars;
        private readonly Func<AttachmentCard, Task>? _showInChat;
        private readonly ZoomImage _image = new();
        private readonly VerticalStackLayout _failed;
        private readonly Border _info;
        private readonly CornerIcon _star;
        private bool _leaving;

        /// <summary>A viewer over <paramref name="browser"/>'s photos, at its current one.</summary>
        /// <param name="state">The app.</param>
        /// <param name="browser">The photos a swipe moves through; one photo when opened from the chat.</param>
        /// <param name="stars">The archive's stars.</param>
        /// <param name="showInChat">Opens the chat at a photo's message, or <see langword="null"/> for no Show in chat.</param>
        internal ImageViewerPage(AppState state, PhotoBrowser browser, Stars stars, Func<AttachmentCard, Task>? showInChat)
        {
            _state = state;
            _browser = browser;
            _stars = stars;
            _showInChat = showInChat;

            BackgroundColor = Colors.Black;

            _failed = new VerticalStackLayout
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
                _failed.IsVisible = !ok;
            };

            _image.Swiped += (_, direction) => Move(direction);

            // A picture that does not decode takes no touches: a swipe over the message moves on too.
            var swipeLeft = new SwipeGestureRecognizer { Direction = SwipeDirection.Left };
            swipeLeft.Swiped += (_, _) => Move(+1);
            var swipeRight = new SwipeGestureRecognizer { Direction = SwipeDirection.Right };
            swipeRight.Swiped += (_, _) => Move(-1);
            _failed.GestureRecognizers.Add(swipeLeft);
            _failed.GestureRecognizers.Add(swipeRight);

            if (showInChat is not null)
            {
                ToolbarItems.Add(new ToolbarItem("Show in chat", null, async () => await ShowInChatAsync()));
            }

            // Beside Show in chat it goes to the overflow menu, so the title keeps its room.
            ToolbarItems.Add(new ToolbarItem("Open in another app", null, async () => await OpenCurrentAsync())
            {
                Order = showInChat is null ? ToolbarItemOrder.Primary : ToolbarItemOrder.Secondary,
            });

            var button = new Button { Text = "Open in another app" };
            button.Clicked += async (_, _) => await OpenCurrentAsync();
            _failed.Children.Add(button);

            _info = new Border
            {
                IsVisible = false,
                BackgroundColor = Color.FromRgba(0, 0, 0, 0.8),
                StrokeThickness = 0,
                Padding = new Thickness(16, 14),
                VerticalOptions = LayoutOptions.End,
            };

            var close = new TapGestureRecognizer();
            close.Tapped += (_, _) => _info.IsVisible = false;
            _info.GestureRecognizers.Add(close);

            var share = new CornerIcon(CornerGlyph.Share, "Share");
            share.Tapped += async (_, _) => await ShareCurrentAsync();

            _star = new CornerIcon(CornerGlyph.Star, "Star");
            _star.Tapped += (_, _) => ToggleStar();

            var info = new CornerIcon(CornerGlyph.Info, "Info");
            info.Tapped += (_, _) => _info.IsVisible = !_info.IsVisible;

            var corner = new HorizontalStackLayout
            {
                Spacing = 0,
                Padding = 4,
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Start,
                Children = { share, _star, info },
            };

            Content = new Grid { Children = { _image, _failed, corner, _info } };

            ShowCurrent();
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

        /// <summary>Shows the browser's current photo: picture, title, star and Info.</summary>
        private void ShowCurrent()
        {
            var card = _browser.Current;

            Title = _browser.Photos.Count > 1
                ? $"Photo {_browser.Index + 1} of {_browser.Photos.Count}"
                : "Photo";

            _info.Content = InfoGrid(card.Details);
            _star.IsOn = card.IsStarred;
            SemanticProperties.SetDescription(_star, card.IsStarred ? "Unstar" : "Star");

            _failed.IsVisible = false;
            _image.IsVisible = true;
            _image.Path = card.FullPath;
        }

        private void Move(int direction)
        {
            if (_leaving || !(direction > 0 ? _browser.Next() : _browser.Previous()))
            {
                return;
            }

            ShowCurrent();
        }

        private void ToggleStar()
        {
            var card = _browser.Current;

            _stars.SetAttachment(card.SequenceNumber, card.AttachmentID, !card.IsStarred);
            _stars.Apply(card);

            _star.IsOn = card.IsStarred;
            SemanticProperties.SetDescription(_star, card.IsStarred ? "Unstar" : "Star");
        }

        private async Task ShowInChatAsync()
        {
            if (_leaving || _showInChat is null)
            {
                return;
            }

            _leaving = true;

            try
            {
                await _showInChat(_browser.Current);
            }
            catch (Exception)
            {
                // The conversation reports its own read failures; nothing else may end the app.
            }
            finally
            {
                // When the chat did not take over (the page was closing, say), this page carries on.
                _leaving = false;
            }
        }

        private async Task OpenCurrentAsync()
        {
            var card = _browser.Current;

            if (card.FullPath is { } path)
            {
                await ConversationPage.OpenInAnotherAppAsync(this, _state, path, card.MimeType ?? MediaMimeTypes.Fallback);
            }
        }

        private async Task ShareCurrentAsync()
        {
            var card = _browser.Current;

            if (card.FullPath is not { } path)
            {
                return;
            }

            switch (ExternalOpener.ShareFile(path, card.MimeType ?? MediaMimeTypes.Fallback, _state.Library.ArchivesFolder))
            {
                case OpenResult.NoApp:
                    await DisplayAlertAsync("Share", "No app on this phone can receive this file.", "OK");
                    break;
                case OpenResult.NotAllowed:
                    await DisplayAlertAsync("Share", "This file cannot be shared.", "OK");
                    break;
            }
        }

        /// <summary>The Info panel's lines: one per detail the archive holds.</summary>
        private static Grid InfoGrid(IReadOnlyList<MediaDetail> details)
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

            return grid;
        }
    }
}
