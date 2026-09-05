using System.Text.RegularExpressions;

namespace Chatcheology.Core.Text
{
    /// <summary>
    /// Finds the web and email addresses in a message, leaving everything else as text.
    /// </summary>
    /// <remarks>
    /// <para><strong>Deliberately narrow.</strong> Three shapes are recognised: an <c>http</c> or
    /// <c>https</c> address, a bare <c>www.</c> address, and an email address. Nothing else is —
    /// not another URI scheme, not a phone number, not a hashtag, a mention, a location or any
    /// markdown. Every one of those would be this viewer deciding what a piece of somebody's
    /// message meant, and a reader that shows what was written does not decide that.</para>
    /// <para><strong>Nothing is fetched.</strong> This looks at characters. No address is resolved,
    /// contacted, validated against a network or previewed, whether it is followed or not.</para>
    /// <para><strong>The message survives intact.</strong> Concatenating every span's
    /// <see cref="MessageTextSpan.Text"/> in order reproduces the input exactly, character for
    /// character, line breaks and all. Punctuation that a sentence happened to put after an address
    /// is returned as text, because it belongs to the sentence rather than the address.</para>
    /// </remarks>
    public static partial class MessageTextScanner
    {
        /// <summary>
        /// Splits <paramref name="messageText"/> into ordinary text and followable addresses.
        /// </summary>
        /// <returns>
        /// The runs in order. A message containing no address is one <see cref="MessageSpanKind.Text"/>
        /// span; an empty message is no spans at all.
        /// </returns>
        public static IReadOnlyList<MessageTextSpan> Scan(string messageText)
        {
            ArgumentNullException.ThrowIfNull(messageText);

            if (messageText.Length == 0)
            {
                return [];
            }

            var spans = new List<MessageTextSpan>();
            var consumed = 0;

            foreach (Match match in AddressPattern().Matches(messageText))
            {
                var (text, kind) = Classify(match);

                // Everything the match found may turn out to be punctuation once trimmed, or to
                // leave behind something no operating system could be handed. Either way it stays
                // as ordinary text rather than becoming a link that cannot be followed.
                if (text.Length == 0 || TargetOf(kind, text) is not { } target)
                {
                    continue;
                }

                if (match.Index > consumed)
                {
                    spans.Add(Plain(messageText[consumed..match.Index]));
                }

                spans.Add(new MessageTextSpan
                {
                    Kind = kind,
                    Text = text,
                    Target = target,
                });

                consumed = match.Index + text.Length;
            }

            if (consumed < messageText.Length)
            {
                spans.Add(Plain(messageText[consumed..]));
            }

            return spans;
        }

        private static MessageTextSpan Plain(string text) => new()
        {
            Kind = MessageSpanKind.Text,
            Text = text,
            Target = null,
        };

        /// <summary>
        /// Where following <paramref name="text"/> leads, or <see langword="null"/> when it leads
        /// nowhere a machine could go.
        /// </summary>
        /// <remarks>
        /// A bare <c>www.</c> address is given the <c>https</c> scheme it was written without,
        /// which is the scheme a browser would supply anyway, and an email address becomes a
        /// <c>mailto:</c> address; neither can be handed to an operating system as it stands.
        /// <para>
        /// The result is then checked as an absolute address of one of the three schemes this
        /// viewer follows. Trimming a sentence's punctuation can leave a fragment that matched the
        /// shape of an address without being one — <c>http://</c> with its host eaten by a full
        /// stop — and a fragment is text, not a link.
        /// </para>
        /// </remarks>
        private static string? TargetOf(MessageSpanKind kind, string text)
        {
            var target = kind switch
            {
                MessageSpanKind.WebLink =>
                    text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                        ? "https://" + text
                        : text,

                MessageSpanKind.EmailAddress => "mailto:" + text,

                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };

            return IsFollowable(target) ? target : null;
        }

        /// <summary>
        /// Whether <paramref name="target"/> is an absolute address of a scheme this viewer follows.
        /// </summary>
        /// <remarks>
        /// The three schemes are named here as well as in the pattern. The pattern is what decides
        /// whether a run of characters looks like an address; this is what decides whether the
        /// result may be handed to the machine, and the two are kept separate so a later change to
        /// one cannot widen the other by accident.
        /// </remarks>
        public static bool IsFollowable(string target) =>
            Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps
                || uri.Scheme == Uri.UriSchemeHttp
                || uri.Scheme == Uri.UriSchemeMailto)
            && uri.OriginalString.Length > uri.Scheme.Length + 1;

        private static (string Text, MessageSpanKind Kind) Classify(Match match)
        {
            if (match.Groups[EmailGroup].Success)
            {
                // The pattern's own trailing letters cannot be punctuation, so there is nothing to
                // trim from an email address.
                return (match.Value, MessageSpanKind.EmailAddress);
            }

            return (TrimTrailingPunctuation(match.Value), MessageSpanKind.WebLink);
        }

        /// <summary>
        /// Gives back the punctuation a sentence put after an address.
        /// </summary>
        /// <remarks>
        /// A message ending "see www.example.com." means the full stop to end the sentence, not to
        /// be part of the address. Closing brackets and quotes are returned only when the address
        /// does not open them itself, so a link that genuinely contains a bracketed path keeps it.
        /// </remarks>
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

                if (ClosingFor(last) is { } opening
                    && Occurrences(value, opening, end) < Occurrences(value, last, end))
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

        /// <summary>Punctuation that ends a sentence rather than an address.</summary>
        private const string SentencePunctuation = ".,;:!?\"'“”‘’…";

        private const string EmailGroup = "email";

        /// <remarks>
        /// <para>The alternatives are ordered so a scheme wins over the <c>www.</c> and email shapes
        /// inside it: the regex takes the leftmost match, and at the start of
        /// <c>https://www.example.com</c> the scheme alternative is the one that matches.</para>
        /// <para>An address runs to the next whitespace or angle bracket. That is what separates one
        /// from the text around it, and it is why trailing punctuation has to be given back
        /// afterwards rather than excluded here — a full stop is a legitimate character inside a
        /// path as well as at the end of a sentence, and only its position says which it is.</para>
        /// <para>The email shape requires a dot in the domain and a two-letter-or-longer ending, so
        /// the ending cannot be punctuation and nothing has to be trimmed from it.</para>
        /// </remarks>
        [GeneratedRegex(
            @"https?://[^\s<>]+" +
            @"|www\.[^\s<>]+" +
            @"|(?<email>[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex AddressPattern();
    }
}
