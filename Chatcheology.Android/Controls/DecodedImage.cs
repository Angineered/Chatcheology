using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Presentation;
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

        /// <summary>
        /// Where the picture's saved thumbnail is kept (CH-66, the gallery's cells), or <see langword="null"/> for none: the
        /// saved copy is read first, and made after the first decode. Set it before <see cref="Path"/>.
        /// </summary>
        public SavedThumbnail? Saved { get; set; }

        /// <summary>Raised on the UI thread when the current file could not be decoded.</summary>
        public event EventHandler? Failed;

        internal void RaiseFailed() => Failed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A picture's place in the saved thumbnails (CH-66): the store, the archive and the photo; the size comes from the view.</summary>
    /// <param name="Store">The saved thumbnails.</param>
    /// <param name="ContentsSha256">The archive's identity.</param>
    /// <param name="AttachmentID">The photo's attachment.</param>
    /// <param name="MediaSha256">The photo's media hash.</param>
    /// <param name="IsWanted">
    /// Whether a new thumbnail may still be saved: false once the page showing it closed (its archive may be removed next,
    /// and a save must not make its folder again). Read on the decode worker.
    /// </param>
    public sealed record SavedThumbnail(ThumbnailStore Store, string ContentsSha256, long AttachmentID, string MediaSha256, Func<bool> IsWanted)
    {
        /// <summary>The key at <paramref name="width"/> by <paramref name="height"/> pixels.</summary>
        public ThumbnailKey KeyAt(int width, int height) => new(ContentsSha256, AttachmentID, MediaSha256, width, height);
    }

    /// <summary>Draws a <see cref="DecodedImage"/>: an image view, and the "Can't preview" heading when decoding fails.</summary>
    internal sealed class DecodedImageHandler : ViewHandler<DecodedImage, FrameLayout>
    {
        public static readonly IPropertyMapper<DecodedImage, DecodedImageHandler> Mapper =
            new PropertyMapper<DecodedImage, DecodedImageHandler>(ViewMapper)
            {
                [DecodedImage.PathProperty.PropertyName] = MapPath,
            };

        // Every decoded picture's decodes, a few at a time, newest first (CH-66 scroll follow-up).
        private static readonly DecodeQueue Decodes = new(DecodeQueue.WorkersFor(Environment.ProcessorCount));

        private PictureView? _image;
        private TextView? _failed;
        private FrameLayout? _frame;
        private bool _subscribed;
        private int _generation;

        public DecodedImageHandler()
            : base(Mapper)
        {
        }

        protected override FrameLayout CreatePlatformView()
        {
            var context = Context;
            var frame = new FrameLayout(context);

            _image = new PictureView(context);
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

            _frame = platformView;

            // The phone's mode may have changed while the view was off screen.
            Recolour(platformView);
            Subscribe();
        }

        protected override void DisconnectHandler(FrameLayout platformView)
        {
            Unsubscribe();
            _frame = null;

            _generation++;

            if (IsAlive(_image))
            {
                _image!.SetPicture(null);
            }

            base.DisconnectHandler(platformView);
        }

        private void Subscribe()
        {
            if (!_subscribed && Application.Current is { } app)
            {
                app.RequestedThemeChanged += OnThemeChanged;
                _subscribed = true;
            }
        }

        private void Unsubscribe()
        {
            if (_subscribed && Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnThemeChanged;
            }

            _subscribed = false;
        }

        /// <summary>The phone switched between light and dark (CH-41): the box and its text follow.</summary>
        /// <remarks>
        /// Never throws (CH-60): the event is raised through MAUI's weak event manager, which turns an
        /// exception here into a fatal one. A handler in a CollectionView cell is not always disconnected
        /// when its page closes, so it may be called after its views are gone; it then unsubscribes itself.
        /// It reads the frame from its own field, not <see cref="ViewHandler{TVirtualView, TPlatformView}.PlatformView"/>,
        /// whose getter throws when there is none.
        /// </remarks>
        private void OnThemeChanged(object? sender, AppThemeChangedEventArgs e)
        {
            if (((IElementHandler)this).VirtualView is null || !IsAlive(_frame))
            {
                Unsubscribe();
                return;
            }

            try
            {
                Recolour(_frame);
            }
            catch (Exception exception)
            {
                // The last line only; the type alone, as the Release log rule asks.
                Unsubscribe();
                global::Android.Util.Log.Warn(AppState.LogTag, $"decoded-image recolour failed: {exception.GetType().Name}");
            }
        }

        private void Recolour(FrameLayout? frame)
        {
            if (IsAlive(frame))
            {
                frame!.SetBackgroundColor(Palette.Current("CardBackground").ToPlatform());
            }

            if (IsAlive(_failed))
            {
                _failed!.SetTextColor(Palette.Current("MutedText").ToPlatform());
            }
        }

        /// <summary>Whether <paramref name="view"/> is set and its Java object has not been disposed.</summary>
        private static bool IsAlive(AndroidView? view) => view is not null && view.Handle != IntPtr.Zero;

        private static void MapPath(DecodedImageHandler handler, DecodedImage view) => handler.Load(view.Path);

        private void Load(string? path)
        {
            var generation = ++_generation;
            VirtualView.HasFailed = false;

            _image?.SetPicture(null);

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
            var saved = videoKey is null ? VirtualView.Saved : null;

            // Decoded before and still cached (a cell scrolled back to): drawn now, with no worker trip and
            // no empty frame in between (CH-66 scroll follow-up). A picture's thumbnail only; a video's frame
            // is cached under its stored frame, not this path.
            if (videoKey is null && ImageDecoding.CachedCover(path, width, height) is { } cached)
            {
                _image?.SetPicture(cached);
                return;
            }

            Decodes.Post(() =>
            {
                // Bound again or disconnected before its turn: the decode is no longer wanted.
                if (generation != Volatile.Read(ref _generation))
                {
                    return;
                }

                Bitmap? bitmap = null;
                var fromPhoto = false;

                try
                {
                    // The saved thumbnail first (CH-66): a small file, read instead of the whole photo.
                    if (saved?.KeyAt(width, height) is { } key && saved.Store.Find(key) is { } savedPath)
                    {
                        bitmap = ImageDecoding.DecodeSavedCover(savedPath, path, width, height);

                        if (bitmap is null)
                        {
                            // A saved file that does not decode is dropped and made again below.
                            saved.Store.Remove(key);
                        }
                    }

                    if (bitmap is null && videoKey is null)
                    {
                        // A degraded decode (a fallback was taken) is shown but never saved: missing is better than wrong.
                        bitmap = ImageDecoding.DecodeToCover(path, width, height, out var degraded);
                        fromPhoto = bitmap is not null && !degraded;
                    }
                    else if (bitmap is null)
                    {
                        bitmap = VideoFrames.FrameToCover(path, videoKey!, width, height);
                    }
                }
                catch (Exception)
                {
                    bitmap = null;
                    fromPhoto = false;
                }

                PostResult(frame, generation, videoKey, bitmap);

                // Saved after it is shown, on this worker; a failure only means the photo is decoded again next time.
                if (fromPhoto && saved is not null && bitmap is not null && saved.IsWanted())
                {
                    var shown = bitmap;
                    saved.Store.Save(saved.KeyAt(width, height), stream => ImageDecoding.WriteCoverJpeg(shown, width, height, stream));
                }
            });
        }

        /// <summary>Shows a decoded picture, or the failure, on the UI thread, unless the view was bound again meanwhile.</summary>
        private void PostResult(FrameLayout frame, int generation, string? videoKey, Bitmap? bitmap)
        {
            frame.Post(() =>
            {
                if (generation != _generation || ((IElementHandler)this).VirtualView is null)
                {
                    return;
                }

                if (bitmap is not null)
                {
                    _image?.SetPicture(bitmap);
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
        }
    }

    /// <summary>
    /// The picture of a <see cref="DecodedImage"/>: an image view that fills its frame, so a new picture never
    /// changes its size, and setting one asks for a redraw only, not a new layout (CH-66 scroll follow-up).
    /// </summary>
    /// <remarks>
    /// A plain image view asks its parents for a new layout whenever its picture's size changes, which is every
    /// time a thumbnail arrives (from none to one). In a scrolling list that request reaches the list itself,
    /// which then lays out and re-measures every row on screen in the next frame; with thumbnails arriving as
    /// the reader scrolls, that was most frames. The frame's own size comes from the page (the requested size),
    /// never from the picture, so the request changed nothing. Before the view's first layout the request is
    /// passed on as usual.
    /// </remarks>
    internal sealed class PictureView : ImageView
    {
        private bool _settingPicture;

        public PictureView(Context context)
            : base(context)
        {
        }

        // Used by Android when it re-creates the managed side of an existing view.
        private PictureView(IntPtr handle, global::Android.Runtime.JniHandleOwnership transfer)
            : base(handle, transfer)
        {
        }

        /// <summary>Shows <paramref name="bitmap"/> (or nothing) with a redraw only, once the view has its size.</summary>
        internal void SetPicture(Bitmap? bitmap)
        {
            _settingPicture = IsLaidOut && Width > 0 && Height > 0;

            try
            {
                SetImageBitmap(bitmap);
            }
            finally
            {
                _settingPicture = false;
            }
        }

        public override void RequestLayout()
        {
            if (!_settingPicture)
            {
                base.RequestLayout();
            }
        }
    }
}
