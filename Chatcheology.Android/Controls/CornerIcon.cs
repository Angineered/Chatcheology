namespace Chatcheology.Android.Controls
{
    /// <summary>Which glyph a <see cref="CornerIcon"/> draws.</summary>
    internal enum CornerGlyph
    {
        /// <summary>Three linked dots: share.</summary>
        Share,

        /// <summary>An "i" in a circle: info.</summary>
        Info,

        /// <summary>A star: outlined, or filled when <see cref="CornerIcon.IsOn"/> (CH-38).</summary>
        Star,
    }

    /// <summary>
    /// A small, low-contrast icon for a corner of the full-screen viewer, drawn in code so no image
    /// resource is needed. The touch area is larger than the glyph.
    /// </summary>
    internal sealed class CornerIcon : GraphicsView
    {
        /// <summary>The touch area's side, in device-independent units.</summary>
        private const double Size = 44;

        private readonly GlyphDrawable _drawable;

        /// <summary>Raised when the icon is tapped.</summary>
        internal event EventHandler? Tapped;

        internal CornerIcon(CornerGlyph glyph, string description)
        {
            _drawable = new GlyphDrawable(glyph);
            Drawable = _drawable;
            WidthRequest = Size;
            HeightRequest = Size;
            BackgroundColor = Colors.Transparent;
            SemanticProperties.SetDescription(this, description);

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => Tapped?.Invoke(this, EventArgs.Empty);
            GestureRecognizers.Add(tap);
        }

        /// <summary>Whether a <see cref="CornerGlyph.Star"/> is filled: the photo is starred.</summary>
        internal bool IsOn
        {
            get => _drawable.IsOn;
            set
            {
                if (_drawable.IsOn != value)
                {
                    _drawable.IsOn = value;
                    Invalidate();
                }
            }
        }

        private sealed class GlyphDrawable(CornerGlyph glyph) : IDrawable
        {
            public bool IsOn { get; set; }

            public void Draw(ICanvas canvas, RectF dirtyRect)
            {
                // A 20-unit glyph in the middle of the touch area, white at low opacity over the photo.
                var cx = dirtyRect.Center.X;
                var cy = dirtyRect.Center.Y;

                canvas.StrokeColor = Colors.White.WithAlpha(0.6f);
                canvas.FillColor = Colors.White.WithAlpha(0.6f);
                canvas.StrokeSize = 1.8f;

                if (glyph == CornerGlyph.Star)
                {
                    var star = new PathF();

                    for (var i = 0; i < 10; i++)
                    {
                        var radius = i % 2 == 0 ? 10f : 4.2f;
                        var angle = -Math.PI / 2 + i * Math.PI / 5;
                        var point = new PointF(cx + (float)(radius * Math.Cos(angle)), cy + (float)(radius * Math.Sin(angle)));

                        if (i == 0)
                        {
                            star.MoveTo(point);
                        }
                        else
                        {
                            star.LineTo(point);
                        }
                    }

                    star.Close();

                    if (IsOn)
                    {
                        // Starred: filled, in the star colour, fully opaque so it reads as on.
                        canvas.FillColor = Color.FromArgb("#F2C14E");
                        canvas.FillPath(star);
                    }
                    else
                    {
                        canvas.DrawPath(star);
                    }

                    return;
                }

                if (glyph == CornerGlyph.Info)
                {
                    canvas.DrawCircle(cx, cy, 10);
                    canvas.FillCircle(cx, cy - 5, 1.4f);
                    canvas.DrawLine(cx, cy - 1.5f, cx, cy + 5.5f);
                    return;
                }

                PointF top = new(cx + 6, cy - 7), left = new(cx - 6, cy), bottom = new(cx + 6, cy + 7);

                canvas.DrawLine(left, top);
                canvas.DrawLine(left, bottom);
                canvas.FillCircle(top, 3);
                canvas.FillCircle(left, 3);
                canvas.FillCircle(bottom, 3);
            }
        }
    }
}
