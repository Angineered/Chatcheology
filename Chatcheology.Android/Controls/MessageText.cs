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
        /// <summary>Raised with the address when a link is tapped (in a message's text or a picture's caption).</summary>
        internal static event EventHandler<string>? LinkTapped;

        /// <summary>A text label, filled from its binding context (a message row).</summary>
        public MessageText()
        {
            FontSize = 15;
            Palette.Set(this, TextColorProperty, "BodyText");

            // A text with links takes its own touches: the long press for Share is watched here too.
            MessageLongPress.Watch(this);
        }

        /// <inheritdoc />
        protected override void OnBindingContextChanged()
        {
            base.OnBindingContextChanged();

            if (BindingContext is not MessageRow row || !row.HasText)
            {
                Fill(this, null, []);
                return;
            }

            Fill(this, row.Text, row.TextSpans);
        }

        /// <summary>
        /// Shows <paramref name="text"/> in <paramref name="label"/>: plain when it has no address, otherwise as
        /// spans whose addresses are underlined and raise <see cref="LinkTapped"/> on a tap. Hidden when empty.
        /// </summary>
        internal static void Fill(Label label, string? text, IReadOnlyList<TextSpan> spans)
        {
            if (string.IsNullOrEmpty(text))
            {
                label.FormattedText = null;
                label.Text = null;
                label.IsVisible = false;
                return;
            }

            label.IsVisible = true;

            if (!spans.Any(span => span.Kind != TextSpanKind.Text))
            {
                label.FormattedText = null;
                label.Text = text;
                return;
            }

            var formatted = new FormattedString();

            foreach (var span in spans)
            {
                var piece = new Span { Text = span.Text };

                if (span.Kind != TextSpanKind.Text && span.Target is { } target)
                {
                    Palette.Set(piece, Span.TextColorProperty, "LinkText");
                    piece.TextDecorations = TextDecorations.Underline;

                    var tap = new TapGestureRecognizer();
                    tap.Tapped += (_, _) => LinkTapped?.Invoke(label, target);
                    piece.GestureRecognizers.Add(tap);
                }

                formatted.Spans.Add(piece);
            }

            label.Text = null;
            label.FormattedText = formatted;
        }
    }

    /// <summary>
    /// A picture's caption under its card in the chat (CH-71), filled from its binding context (an attachment
    /// card). Drawn like message text: addresses underlined and opened only on a tap, everything else exactly as
    /// written. It sits outside the card, so a tap on it never opens the picture.
    /// </summary>
    public sealed class CaptionText : Label
    {
        /// <summary>A caption label.</summary>
        public CaptionText()
        {
            FontSize = 15;
            MaximumWidthRequest = AttachmentCardView.ImageWidth;
            LineBreakMode = LineBreakMode.WordWrap;
            Palette.Set(this, TextColorProperty, "BodyText");

            // The bubble's long press (Share, Star) works on the caption too.
            MessageLongPress.Watch(this);
        }

        /// <inheritdoc />
        protected override void OnBindingContextChanged()
        {
            base.OnBindingContextChanged();

            if (BindingContext is AttachmentCard { HasCaption: true } card)
            {
                MessageText.Fill(this, card.Caption, card.CaptionSpans);
            }
            else
            {
                MessageText.Fill(this, null, []);
            }
        }
    }
}
