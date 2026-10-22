using System.Text.RegularExpressions;

namespace Chatcheology.Mobile.Presentation
{
    /// <summary>What a run of message text is, as far as presentation is concerned.</summary>
    /// <remarks>
    /// Three kinds and no more, the desktop's rule (<c>Chatcheology.Core.Text.MessageSpanKind</c>):
    /// phone numbers, hashtags, mentions and markdown would each be the viewer deciding what part of
    /// somebody's message meant.
    /// </remarks>
    public enum TextSpanKind
    {
        /// <summary>Ordinary text, shown exactly as written.</summary>
        Text,

        /// <summary>A web address the reader may follow by tapping it.</summary>
        WebLink,

        /// <summary>An email address the reader may write to by tapping it.</summary>
        EmailAddress,
    }

    /// <summary>One run of a message: plain text, or an address the reader may follow.</summary>
    /// <param name="Kind">What the run is.</param>
    /// <param name="Text">The message's own characters, unchanged.</param>
    /// <param name="Target">
    /// Where a tap leads: an absolute <c>http</c>, <c>https</c> or <c>mailto</c> address; <see langword="null"/>
    /// for plain text.
    /// </param>
    public sealed record TextSpan(TextSpanKind Kind, string Text, string? Target);

    /// <summary>
    /// Finds the web and email addresses in a message, leaving everything else as text. The same rule
    /// as the desktop's <c>Chatcheology.Core.Text.MessageTextScanner</c>, copied because this library
    /// does not reference Core.
    /// </summary>
    /// <remarks>
    /// <para>Three shapes: an <c>http</c>/<c>https</c> address, a bare <c>www.</c> address, and an email
    /// address. Nothing is fetched or resolved; an address is only followed when the reader taps it.</para>
    /// <para>Joining every span's <see cref="TextSpan.Text"/> in order reproduces the message exactly.
    /// Punctuation a sentence put after an address is returned as text.</para>
    /// </remarks>
    public static partial class LinkScanner
    {
        private const string SentencePunctuation = ".,;:!?\"'“”‘’…";

        private const string EmailGroup = "email";

        /// <summary>Splits <paramref name="messageText"/> into plain text and followable addresses.</summary>
        /// <returns>The runs in order; none for an empty message.</returns>
        public static IReadOnlyList<TextSpan> Scan(string messageText)
        {
            ArgumentNullException.ThrowIfNull(messageText);

            if (messageText.Length == 0)
            {
                return [];
            }

            var spans = new List<TextSpan>();
            var consumed = 0;

            foreach (Match match in AddressPattern().Matches(messageText))
            {
                var (text, kind) = Classify(match);

                if (text.Length == 0 || TargetOf(kind, text) is not { } target)
                {
                    continue;
                }

                if (match.Index > consumed)
                {
                    spans.Add(new TextSpan(TextSpanKind.Text, messageText[consumed..match.Index], null));
                }

                spans.Add(new TextSpan(kind, text, target));
                consumed = match.Index + text.Length;
            }

            if (consumed < messageText.Length)
            {
                spans.Add(new TextSpan(TextSpanKind.Text, messageText[consumed..], null));
            }

            return spans;
        }

        /// <summary>Whether <paramref name="target"/> is an absolute address of a scheme the viewer follows.</summary>
        public static bool IsFollowable(string target) =>
            Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps
                || uri.Scheme == Uri.UriSchemeHttp
                || uri.Scheme == Uri.UriSchemeMailto)
            && uri.OriginalString.Length > uri.Scheme.Length + 1;

        private static string? TargetOf(TextSpanKind kind, string text)
        {
            var target = kind switch
            {
                TextSpanKind.WebLink => text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + text : text,
                TextSpanKind.EmailAddress => "mailto:" + text,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };

            return IsFollowable(target) ? target : null;
        }

        private static (string Text, TextSpanKind Kind) Classify(Match match) =>
            match.Groups[EmailGroup].Success
                ? (match.Value, TextSpanKind.EmailAddress)
                : (TrimTrailingPunctuation(match.Value), TextSpanKind.WebLink);

        private static string TrimTrailingPunctuation(string value)
        {
            var end = value.Length;

            while (end > 0)
            {
                var last = value[end - 1];

                if (SentencePunctuation.Contains(last))
                {
                    end--;
                    continue;
                }

                if (ClosingFor(last) is { } opening && Occurrences(value, opening, end) < Occurrences(value, last, end))
                {
                    end--;
                    continue;
                }

                break;
            }

            return value[..end];
        }

        private static char? ClosingFor(char value) => value switch
        {
            ')' => '(',
            ']' => '[',
            '}' => '{',
            _ => null,
        };

        private static int Occurrences(string value, char character, int length)
        {
            var count = 0;

            for (var index = 0; index < length; index++)
            {
                if (value[index] == character)
                {
                    count++;
                }
            }

            return count;
        }

        [GeneratedRegex(
            @"https?://[^\s<>]+" +
            @"|www\.[^\s<>]+" +
            @"|(?<email>[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex AddressPattern();
    }
}
