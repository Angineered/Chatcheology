using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Presentation;

namespace Chatcheology.Android.Controls
{
    /// <summary>
    /// A message's text. Web and email addresses are underlined and open only when tapped; everything
    /// else is shown exactly as written.
    /// </summary>
    public sealed class MessageText : Label
    {
        /// <summary>Raised with the address when a link is tapped.</summary>
        internal static event EventHandler<string>? LinkTapped;

        /// <summary>A text label, filled from its binding context (a message row).</summary>
        public MessageText()
        {
            FontSize = 15;
            TextColor = Colors.Black;
        }

        /// <inheritdoc />
        protected override void OnBindingContextChanged()
        {
            base.OnBindingContextChanged();

            if (BindingContext is not MessageRow row || !row.HasText)
            {
                FormattedText = null;
                Text = null;
                IsVisible = false;
                return;
            }

            IsVisible = true;

            if (!row.HasLinks)
            {
                FormattedText = null;
                Text = row.Text;
                return;
            }

            var formatted = new FormattedString();
            var linkColour = (Color)Application.Current!.Resources["LinkText"];

            foreach (var span in row.TextSpans)
            {
                var piece = new Span { Text = span.Text };

                if (span.Kind != TextSpanKind.Text && span.Target is { } target)
                {
                    piece.TextColor = linkColour;
                    piece.TextDecorations = TextDecorations.Underline;

                    var tap = new TapGestureRecognizer();
                    tap.Tapped += (_, _) => LinkTapped?.Invoke(this, target);
                    piece.GestureRecognizers.Add(tap);
                }

                formatted.Spans.Add(piece);
            }

            Text = null;
            FormattedText = formatted;
        }
    }
}
