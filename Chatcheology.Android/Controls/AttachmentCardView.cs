using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Android.Controls
{
    /// <summary>
    /// One attachment in the conversation: a picture at its final size, or a card with a heading, plus
    /// the low-quality label or note when one applies. Tapping a picture shows it full screen; tapping a
    /// restored file (or a picture that would not decode) opens it in another app.
    /// </summary>
    /// <remarks>
    /// <para>The picture's height is set from the recorded size (capped by <see cref="AttachmentCard.HeightFor"/>)
    /// before the file is decoded, and a picture that fails to decode shows its heading in the same box,
    /// so the row never changes height and the list has nothing to correct.</para>
    /// <para>A restored video (CH-40) shows its preview frame with a play mark and its duration, at the
    /// same fixed height; a tap still opens it in another app. When no frame can be read the card stays as
    /// it was, and is shown directly from then on.</para>
    /// <para>A restored audio file (CH-36) gets a play / pause button and a progress bar; it plays in the
    /// app, one at a time (<see cref="AudioPlayback"/>). A tap elsewhere on the card still opens it in
    /// another app.</para>
    /// </remarks>
    public sealed class AttachmentCardView : ContentView
    {
        /// <summary>The width a picture or card is shown at, in device-independent units.</summary>
        internal const double ImageWidth = 240;

        /// <summary>Raised when a card is tapped; the conversation page decides what to do.</summary>
        internal static event EventHandler<AttachmentTapped>? Tapped;

        /// <summary>Raised when an audio file cannot be played in the app; the page offers another app.</summary>
        internal static event EventHandler<AttachmentCard>? PlayFailed;

        private readonly DecodedImage _image = new() { WidthRequest = ImageWidth };
        private readonly Label _heading = new() { FontSize = 14 };
        private readonly Border _card;
        private readonly VerticalStackLayout _cardContent;
        private readonly Label _extra = new() { FontSize = 12, WidthRequest = ImageWidth };
        private readonly VerticalStackLayout _stack;

        private readonly DecodedImage _frame = new() { WidthRequest = ImageWidth };
        private readonly Label _frameDuration = new()
        {
            FontSize = 12,
            TextColor = Colors.White,
            BackgroundColor = Color.FromRgba(0, 0, 0, 0.55),
            Padding = new Thickness(6, 2),
            Margin = new Thickness(6),
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.End,
        };

        private readonly Grid _video;

        private readonly Button _play = new() { Text = "▶", FontSize = 14, Padding = 0, WidthRequest = 40, HeightRequest = 40, CornerRadius = 20, TextColor = Colors.White };
        private readonly ProgressBar _progress = new() { VerticalOptions = LayoutOptions.Center };
        private readonly Grid _audio;

        /// <summary>A card view, filled from its binding context.</summary>
        public AttachmentCardView()
        {
            _heading.TextIn("MutedText");
            _extra.TextIn("MutedText");

            _cardContent = new VerticalStackLayout { Spacing = 6, Children = { _heading } };

            _card = new Border
            {
                StrokeThickness = 0,
                Padding = new Thickness(10, 8),
                WidthRequest = ImageWidth,
                Content = _cardContent,
            }.Themed(BackgroundColorProperty, "CardBackground");

            // The video's preview: the frame, a play mark in the middle, the duration in a corner.
            var mark = new Border
            {
                WidthRequest = 48,
                HeightRequest = 48,
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 24 },
                BackgroundColor = Color.FromRgba(0, 0, 0, 0.5),
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Content = new Label { Text = "▶", FontSize = 20, TextColor = Colors.White, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, Margin = new Thickness(3, 0, 0, 0) },
                InputTransparent = true,
            };

            _video = new Grid { WidthRequest = ImageWidth, Children = { _frame, mark, _frameDuration } };
            _frame.Failed += (_, _) => ShowVideoCard();

            // The audio controls under the heading: play / pause, then the progress.
            _play.Themed(BackgroundColorProperty, "Primary");
            _progress.Themed(ProgressBar.ProgressColorProperty, "Primary");
            _play.Clicked += (_, _) => OnPlayClicked();

            _audio = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
                ColumnSpacing = 10,
            };

            _audio.Add(_play, 0);
            _audio.Add(_progress, 1);

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

            // The card takes its own touches: a long press on it is the bubble's long press (CH-38).
            MessageLongPress.Watch(this);

            // Only while the view is on a screen, so a closed page's views are not kept by the player.
            HandlerChanged += (_, _) =>
            {
                AudioPlayback.Changed -= OnPlaybackChanged;

                if (Handler is not null)
                {
                    AudioPlayback.Changed += OnPlaybackChanged;
                }
            };
        }

        private void OnPlaybackChanged(object? sender, EventArgs e) => ShowPlayback();

        /// <inheritdoc />
        protected override void OnBindingContextChanged()
        {
            base.OnBindingContextChanged();

            _stack.Children.Clear();
            _cardContent.Children.Remove(_audio);
            _frame.Path = null;
            _frame.VideoKey = null;

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
            else if (card.IsVideo && card.FullPath is { } video && card.MediaSha256 is { } sha && !VideoFrames.IsKnownWithoutFrame(sha))
            {
                _image.Path = null;
                _video.HeightRequest = _frame.HeightRequest = card.HeightFor(ImageWidth);
                _frameDuration.Text = card.DurationText;
                _frameDuration.IsVisible = card.DurationText.Length > 0;
                _frame.VideoKey = sha;
                _frame.Path = video;
                _stack.Children.Add(_video);
            }
            else
            {
                _image.Path = null;
                _heading.Text = card.Heading;

                if (card.IsAudio && card.FullPath is not null)
                {
                    _cardContent.Children.Add(_audio);
                    ShowPlayback();
                }

                _stack.Children.Add(_card);
            }

            var extra = card.LowQualityLabel;

            if (extra.Length > 0)
            {
                _extra.Text = extra;
                _stack.Children.Add(_extra);
            }
        }

        /// <summary>No frame could be read: the video's card, as before CH-40.</summary>
        private void ShowVideoCard()
        {
            if (BindingContext is not AttachmentCard { IsVideo: true } card || !_stack.Children.Contains(_video))
            {
                return;
            }

            var index = _stack.Children.IndexOf(_video);
            _stack.Children.RemoveAt(index);
            _heading.Text = card.Heading;
            _stack.Children.Insert(index, _card);
        }

        private void OnPlayClicked()
        {
            if (BindingContext is not AttachmentCard { IsAudio: true } card)
            {
                return;
            }

            if (!AudioPlayback.Toggle(card))
            {
                PlayFailed?.Invoke(this, card);
            }
        }

        /// <summary>The button and the bar show the player's state when this card is the one playing.</summary>
        private void ShowPlayback()
        {
            if (BindingContext is not AttachmentCard { IsAudio: true } card)
            {
                return;
            }

            var current = ReferenceEquals(AudioPlayback.Current, card);
            var playing = current && AudioPlayback.IsPlaying;

            _play.Text = playing ? "❚❚" : "▶";
            SemanticProperties.SetDescription(_play, playing ? "Pause" : "Play");
            _progress.Progress = current ? AudioPlayback.Progress : 0;
        }
    }

    /// <summary>A tapped attachment card.</summary>
    /// <param name="Card">The card.</param>
    /// <param name="DecodeFailed">True for a picture that would not decode: it is opened in another app instead.</param>
    internal sealed record AttachmentTapped(AttachmentCard Card, bool DecodeFailed);
}
