using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Microsoft.Maui.Handlers;
using MauiView = Microsoft.Maui.Controls.View;

namespace Chatcheology.Android.Controls
{
    /// <summary>
    /// A picture shown full screen with pinch zoom, pan and double-tap. Decoded no smaller than it is
    /// shown and at most twice the screen's size, so zooming in stays sharp without a full-size bitmap.
    /// </summary>
    public sealed class ZoomImage : MauiView
    {
        /// <summary>The picture file.</summary>
        public static readonly BindableProperty PathProperty =
            BindableProperty.Create(nameof(Path), typeof(string), typeof(ZoomImage));

        /// <summary>The picture file, or <see langword="null"/>.</summary>
        public string? Path
        {
            get => (string?)GetValue(PathProperty);
            set => SetValue(PathProperty, value);
        }

        /// <summary>Raised on the UI thread with whether the picture decoded.</summary>
        public event EventHandler<bool>? Decoded;

        internal void RaiseDecoded(bool ok) => Decoded?.Invoke(this, ok);
    }

    /// <summary>Draws a <see cref="ZoomImage"/> with <see cref="ZoomImageView"/>.</summary>
    internal sealed class ZoomImageHandler : ViewHandler<ZoomImage, ZoomImageView>
    {
        public static readonly IPropertyMapper<ZoomImage, ZoomImageHandler> Mapper =
            new PropertyMapper<ZoomImage, ZoomImageHandler>(ViewMapper)
            {
                [ZoomImage.PathProperty.PropertyName] = MapPath,
            };

        public ZoomImageHandler()
            : base(Mapper)
        {
        }

        protected override ZoomImageView CreatePlatformView() => new(Context);

        private int _generation;

        /// <summary>Stops a decode under way from landing and releases the picture: the page is closing.</summary>
        protected override void DisconnectHandler(ZoomImageView platformView)
        {
            _generation++;
            platformView.SetPicture(null);
            base.DisconnectHandler(platformView);
        }

        private static void MapPath(ZoomImageHandler handler, ZoomImage view) => handler.Load(view);

        private void Load(ZoomImage view)
        {
            // A newer path, or the page closing, makes any decode still running stale.
            var generation = ++_generation;
            var path = view.Path;
            var platform = PlatformView;

            if (string.IsNullOrEmpty(path))
            {
                platform.SetPicture(null);
                return;
            }

            var metrics = Context.Resources?.DisplayMetrics;
            var screenWidth = Math.Max(1, metrics?.WidthPixels ?? 1080);
            var screenHeight = Math.Max(1, metrics?.HeightPixels ?? 1920);

            Task.Run(() => ImageDecoding.DecodeToFitScreen(path, screenWidth, screenHeight))
                .ContinueWith(
                    decoded =>
                    {
                        var bitmap = decoded.IsCompletedSuccessfully ? decoded.Result : null;

                        platform.Post(() =>
                        {
                            if (generation != _generation)
                            {
                                // Stale: nobody will show it.
                                bitmap?.Recycle();
                                return;
                            }

                            platform.SetPicture(bitmap);
                            view.RaiseDecoded(bitmap is not null);
                        });
                    },
                    TaskScheduler.Default);
        }
    }

    /// <summary>An image view with pinch zoom (1x to 8x), panning when zoomed, and double-tap to zoom.</summary>
    internal sealed class ZoomImageView : ImageView
    {
        private const float MaximumZoom = 8f;

        private readonly Matrix _matrix = new();
        private readonly ScaleGestureDetector _scale;
        private readonly GestureDetector _gestures;
        private float _baseScale = 1f;
        private float _zoom = 1f;

        public ZoomImageView(Context context)
            : base(context)
        {
            SetScaleType(ScaleType.Matrix);
            SetBackgroundColor(global::Android.Graphics.Color.Black);

            _scale = new ScaleGestureDetector(context, new ScaleListener(this));
            _gestures = new GestureDetector(context, new GestureListener(this));
        }

        private Bitmap? _picture;

        /// <summary>Shows <paramref name="bitmap"/>, which the view now owns, and releases the one before.</summary>
        internal void SetPicture(Bitmap? bitmap)
        {
            var previous = _picture;
            _picture = bitmap;

            SetImageBitmap(bitmap);

            if (previous is not null && !ReferenceEquals(previous, bitmap))
            {
                previous.Recycle();
            }

            _zoom = 1f;
            Reset();
        }

        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e is null)
            {
                return false;
            }

            _scale.OnTouchEvent(e);
            _gestures.OnTouchEvent(e);

            return true;
        }

        protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
        {
            base.OnSizeChanged(w, h, oldw, oldh);
            Reset();
        }

        /// <summary>Fits the whole picture in the view, centred.</summary>
        private void Reset()
        {
            if (Drawable is not { } drawable || Width == 0 || Height == 0)
            {
                return;
            }

            var dw = drawable.IntrinsicWidth;
            var dh = drawable.IntrinsicHeight;

            _baseScale = Math.Min((float)Width / dw, (float)Height / dh);
            _zoom = 1f;

            _matrix.Reset();
            _matrix.PostScale(_baseScale, _baseScale);
            _matrix.PostTranslate((Width - dw * _baseScale) / 2, (Height - dh * _baseScale) / 2);
            ImageMatrix = _matrix;
        }

        private void ZoomBy(float factor, float focusX, float focusY)
        {
            var target = Math.Clamp(_zoom * factor, 1f, MaximumZoom);
            factor = target / _zoom;
            _zoom = target;

            _matrix.PostScale(factor, factor, focusX, focusY);
            Clamp();
        }

        private void PanBy(float dx, float dy)
        {
            _matrix.PostTranslate(dx, dy);
            Clamp();
        }

        /// <summary>Keeps the picture on screen: centred along an edge smaller than the view, never past its edges otherwise.</summary>
        private void Clamp()
        {
            if (Drawable is not { } drawable)
            {
                return;
            }

            var rect = new global::Android.Graphics.RectF(0, 0, drawable.IntrinsicWidth, drawable.IntrinsicHeight);
            _matrix.MapRect(rect);

            float dx = 0, dy = 0;

            if (rect.Width() <= Width)
            {
                dx = (Width - rect.Width()) / 2 - rect.Left;
            }
            else if (rect.Left > 0)
            {
                dx = -rect.Left;
            }
            else if (rect.Right < Width)
            {
                dx = Width - rect.Right;
            }

            if (rect.Height() <= Height)
            {
                dy = (Height - rect.Height()) / 2 - rect.Top;
            }
            else if (rect.Top > 0)
            {
                dy = -rect.Top;
            }
            else if (rect.Bottom < Height)
            {
                dy = Height - rect.Bottom;
            }

            _matrix.PostTranslate(dx, dy);
            ImageMatrix = _matrix;
        }

        private sealed class ScaleListener(ZoomImageView view) : ScaleGestureDetector.SimpleOnScaleGestureListener
        {
            public override bool OnScale(ScaleGestureDetector detector)
            {
                view.ZoomBy(detector.ScaleFactor, detector.FocusX, detector.FocusY);
                return true;
            }
        }

        private sealed class GestureListener(ZoomImageView view) : GestureDetector.SimpleOnGestureListener
        {
            public override bool OnDown(MotionEvent e) => true;

            public override bool OnScroll(MotionEvent? e1, MotionEvent e2, float distanceX, float distanceY)
            {
                if (view._zoom > 1f)
                {
                    view.PanBy(-distanceX, -distanceY);
                }

                return true;
            }

            public override bool OnDoubleTap(MotionEvent e)
            {
                if (view._zoom > 1f)
                {
                    view.Reset();
                }
                else
                {
                    view.ZoomBy(2.5f, e.GetX(), e.GetY());
                }

                return true;
            }
        }
    }
}
