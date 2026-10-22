namespace Chatcheology.Android.Controls
{
    /// <summary>Which glyph a <see cref="CornerIcon"/> draws.</summary>
    internal enum CornerGlyph
    {
        /// <summary>Three linked dots: share.</summary>
        Share,

        /// <summary>An "i" in a circle: info.</summary>
        Info,
    }

    /// <summary>
    /// A small, low-contrast icon for a corner of the full-screen viewer, drawn in code so no image
    /// resource is needed. The touch area is larger than the glyph.
    /// </summary>
    internal sealed class CornerIcon : GraphicsView
    {
        /// <summary>The touch area's side, in device-independent units.</summary>
        private const double Size = 44;

        /// <summary>Raised when the icon is tapped.</summary>
        internal event EventHandler? Tapped;

        internal CornerIcon(CornerGlyph glyph, string description)
        {
            Drawable = new GlyphDrawable(glyph);
            WidthRequest = Size;
            HeightRequest = Size;
            BackgroundColor = Colors.Transparent;
            SemanticProperties.SetDescription(this, description);

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => Tapped?.Invoke(this, EventArgs.Empty);
            GestureRecognizers.Add(tap);
        }

        private sealed class GlyphDrawable(CornerGlyph glyph) : IDrawable
        {
            public void Draw(ICanvas canvas, RectF dirtyRect)
            {
                // A 20-unit glyph in the middle of the touch area, white at low opacity over the photo.
                var cx = dirtyRect.Center.X;
                var cy = dirtyRect.Center.Y;

                canvas.StrokeColor = Colors.White.WithAlpha(0.6f);
                canvas.FillColor = Colors.White.WithAlpha(0.6f);
                canvas.StrokeSize = 1.8f;

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
