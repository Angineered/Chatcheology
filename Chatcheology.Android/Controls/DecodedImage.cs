using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Chatcheology.Mobile.Conversation;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using AndroidView = Android.Views.View;
using MauiView = Microsoft.Maui.Controls.View;

namespace Chatcheology.Android.Controls
{
    /// <summary>
    /// A restored picture in the conversation, decoded on the Android side at the size it is shown. A
    /// file that does not decode shows <see cref="AttachmentCard.CannotPreviewHeading"/> in the same box,
    /// so the row keeps its height either way.
    /// </summary>
    /// <remarks>
    /// With <see cref="VideoKey"/> set, <see cref="Path"/> is a video and the picture is its preview frame
    /// (CH-40, <see cref="VideoFrames"/>); when no frame can be read, <see cref="Failed"/> is raised and
    /// the owner shows the video's card instead.
    /// </remarks>
    public sealed class DecodedImage : MauiView
    {
        /// <summary>The picture file.</summary>
        public static readonly BindableProperty PathProperty =
            BindableProperty.Create(nameof(Path), typeof(string), typeof(DecodedImage));

        /// <summary>The picture file, or <see langword="null"/>.</summary>
        public string? Path
        {
            get => (string?)GetValue(PathProperty);
            set => SetValue(PathProperty, value);
        }

        /// <summary>Whether the current file failed to decode; a tap then opens it in another app.</summary>
        public bool HasFailed { get; internal set; }

        /// <summary>
        /// The media hash of a video whose preview frame is shown, or <see langword="null"/> for a picture.
        /// Set it before <see cref="Path"/>.
        /// </summary>
        public string? VideoKey { get; set; }

        /// <summary>
        /// What the box says when the picture will not decode; <see cref="AttachmentCard.CannotPreviewHeading"/>
        /// unless set (a small thumbnail sets a short mark). Set it before <see cref="Path"/>.
        /// </summary>
        public string FailedText { get; set; } = AttachmentCard.CannotPreviewHeading;

        /// <summary>Raised on the UI thread when the current file could not be decoded.</summary>
        public event EventHandler? Failed;

        internal void RaiseFailed() => Failed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Draws a <see cref="DecodedImage"/>: an image view, and the "Can't preview" heading when decoding fails.</summary>
    internal sealed class DecodedImageHandler : ViewHandler<DecodedImage, FrameLayout>
    {
        public static readonly IPropertyMapper<DecodedImage, DecodedImageHandler> Mapper =
            new PropertyMapper<DecodedImage, DecodedImageHandler>(ViewMapper)
            {
                [DecodedImage.PathProperty.PropertyName] = MapPath,
            };

        private ImageView? _image;
        private TextView? _failed;
        private int _generation;

        public DecodedImageHandler()
            : base(Mapper)
        {
        }

        protected override FrameLayout CreatePlatformView()
        {
            var context = Context;
            var frame = new FrameLayout(context);

            _image = new ImageView(context);
            _image.SetScaleType(ImageView.ScaleType.CenterCrop);
            frame.AddView(_image, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

            _failed = new TextView(context)
            {
                Text = AttachmentCard.CannotPreviewHeading,
                Gravity = GravityFlags.Center,
                Visibility = ViewStates.Invisible,
            };
            _failed.SetPadding(16, 16, 16, 16);
            frame.AddView(_failed, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

            Recolour(frame);

            return frame;
        }

        protected override void ConnectHandler(FrameLayout platformView)
        {
            base.ConnectHandler(platformView);

            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged += OnThemeChanged;
            }
        }

        protected override void DisconnectHandler(FrameLayout platformView)
        {
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnThemeChanged;
            }

            _generation++;
            _image?.SetImageBitmap(null);
            base.DisconnectHandler(platformView);
        }

        /// <summary>The phone switched between light and dark (CH-41): the box and its text follow.</summary>
        private void OnThemeChanged(object? sender, AppThemeChangedEventArgs e) => Recolour(PlatformView);

        private void Recolour(FrameLayout? frame)
        {
            frame?.SetBackgroundColor(Palette.Current("CardBackground").ToPlatform());
            _failed?.SetTextColor(Palette.Current("MutedText").ToPlatform());
        }

        private static void MapPath(DecodedImageHandler handler, DecodedImage view) => handler.Load(view.Path);

        private void Load(string? path)
        {
            var generation = ++_generation;
            VirtualView.HasFailed = false;

            _image?.SetImageBitmap(null);

            // Invisible, not gone: it keeps its measured place, so showing it needs no new layout pass,
            // which a fixed-size cell (the gallery's) would not give it.
            if (_failed is not null)
            {
                _failed.Visibility = ViewStates.Invisible;
            }

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            // The size it is shown at, in pixels; the view's requested size is set before the path.
            var density = Context.Resources?.DisplayMetrics?.Density ?? 1f;
            var width = Math.Max(1, (int)Math.Round(Math.Max(VirtualView.WidthRequest, 1) * density));
            var height = Math.Max(1, (int)Math.Round(Math.Max(VirtualView.HeightRequest, 1) * density));
            var frame = PlatformView;
            var videoKey = VirtualView.VideoKey;

            Task.Run(() => videoKey is null
                    ? ImageDecoding.DecodeToCover(path, width, height)
                    : VideoFrames.FrameToCover(path, videoKey, width, height)).ContinueWith(
                decoded =>
                {
                    var bitmap = decoded.IsCompletedSuccessfully ? decoded.Result : null;

                    frame.Post(() =>
                    {
                        if (generation != _generation || VirtualView is null)
                        {
                            return;
                        }

                        if (bitmap is not null)
                        {
                            _image?.SetImageBitmap(bitmap);
                        }
                        else
                        {
                            VirtualView.HasFailed = true;

                            if (_failed is not null && videoKey is null)
                            {
                                _failed.Text = VirtualView.FailedText;
                                _failed.Visibility = ViewStates.Visible;
                            }

                            VirtualView.RaiseFailed();
                        }
                    });
                },
                TaskScheduler.Default);
        }
    }
}
