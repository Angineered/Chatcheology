using System.Globalization;

namespace Chatcheology.Archive
{
    /// <summary>What an external link (CH-48) belongs to.</summary>
    public enum ExternalLinkOwnerKind
    {
        /// <summary>A personal entry (never an imported one).</summary>
        Entry,

        /// <summary>A noted date.</summary>
        NotedDate,
    }

    /// <summary>
    /// One external link (CH-48): a web address for photos kept elsewhere, such as a shared album, on a
    /// personal entry or a noted date. Chatcheology never loads it; a viewer hands it to another app only
    /// after asking. Carried in a version 2 archive's <c>ExternalLink</c> table exactly as the memories
    /// file holds it.
    /// </summary>
    public sealed class ArchiveExternalLink
    {
        /// <summary>The link, with the identifier the memories file gave it.</summary>
        public required long ExternalLinkID { get; init; }

        /// <summary>Whether it belongs to an entry or a noted date.</summary>
        public required ExternalLinkOwnerKind OwnerKind { get; init; }

        /// <summary>The entry's or noted date's identifier.</summary>
        public required long OwnerID { get; init; }

        /// <summary>Its place among its owner's links: 1, 2, ... without a gap.</summary>
        public required int Ordinal { get; init; }

        /// <summary>The address: an absolute <c>https</c> link, at most <see cref="ExternalLinkRules.MaxUrlLength"/> characters.</summary>
        public required string Url { get; init; }

        /// <summary>A short label, or <see langword="null"/> (a viewer then shows the host).</summary>
        public required string? Label { get; init; }
    }

    /// <summary>The fields of an external link a rule names.</summary>
    public enum ExternalLinkField
    {
        /// <summary>The address.</summary>
        Url,

        /// <summary>The label.</summary>
        Label,

        /// <summary>The number of links of one owner.</summary>
        Count,
    }

    /// <summary>The first rule a list of links breaks.</summary>
    /// <param name="Index">The bad link's index in the list (0-based), or <see langword="null"/> for the count.</param>
    /// <param name="Field">What is wrong.</param>
    /// <param name="Message">The message to show; it never repeats the link.</param>
    public sealed record ExternalLinkError(int? Index, ExternalLinkField Field, string Message);

    /// <summary>
    /// The rules of external links (CH-48), in one place: the desktop store refuses a save that breaks one,
    /// the archive writer refuses such a row, the archive validator counts each, and the phone and desktop
    /// show the host through <see cref="DisplayHost"/>.
    /// </summary>
    /// <remarks>
    /// <para>Address: an absolute <c>https</c> link, starting with <c>https://</c>, with a host and no
    /// user-info part (<c>https://a@b</c> is refused: it disguises the real host), no space or control
    /// character, at most 2,000 characters. Relative, fragment-only, <c>http</c>, <c>javascript:</c>,
    /// <c>file:</c>, <c>intent:</c>, <c>content:</c> and every other scheme are refused.</para>
    /// <para>Label: optional; trimmed, 1 to 60 characters; blank means none.</para>
    /// <para>At most 10 links per owner, ordered 1..n.</para>
    /// <para>Lengths are counted in UTF-16 code units (<see cref="string.Length"/>), so a text that passes
    /// here also passes the schemas' <c>length()</c> checks, which count characters.</para>
    /// </remarks>
    public static class ExternalLinkRules
    {
        /// <summary>The longest address kept.</summary>
        public const int MaxUrlLength = 2000;

        /// <summary>The longest label kept.</summary>
        public const int MaxLabelLength = 60;

        /// <summary>The most links one entry or noted date has.</summary>
        public const int MaxLinksPerOwner = 10;

        /// <summary>The stored spelling of <see cref="ExternalLinkOwnerKind.Entry"/>.</summary>
        public const string OwnerEntry = "Entry";

        /// <summary>The stored spelling of <see cref="ExternalLinkOwnerKind.NotedDate"/>.</summary>
        public const string OwnerNotedDate = "NotedDate";

        private const string Prefix = "https://";

        /// <summary>
        /// The <c>ExternalLink</c> table's <c>CHECK</c> clauses, shared by the memories file and the archive so
        /// the two state the same rules: the owner kind, an ordinal from 1, the scheme prefix and the lengths.
        /// The rest (a parsable host, no user-info part, no space, the count per owner, the owner itself) is
        /// the store's, the writer's and the validator's to check.
        /// </summary>
        /// <remarks>
        /// The prefix is checked as <c>https:</c> then <c>//</c> so the schema text itself never holds a whole
        /// address prefix: an archive with no links then holds no <c>https://</c> anywhere (a CH-42 test checks this).
        /// </remarks>
        public const string TableChecks =
            """
            CHECK (OwnerKind IN ('Entry', 'NotedDate')),
                CHECK (typeof(OwnerID) = 'integer'),
                CHECK (typeof(Ordinal) = 'integer' AND Ordinal >= 1),
                CHECK (typeof(Url) = 'text' AND length(Url) > 8 AND length(Url) <= 2000
                    AND lower(substr(Url, 1, 6)) = 'https:' AND substr(Url, 7, 2) = '//'),
                CHECK (Label IS NULL OR (typeof(Label) = 'text' AND length(trim(Label)) > 0 AND length(Label) <= 60))
            """;

        /// <summary>The stored spelling of an owner kind.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Not an owner kind of the format.</exception>
        public static string OwnerKindText(ExternalLinkOwnerKind kind) => kind switch
        {
            ExternalLinkOwnerKind.Entry => OwnerEntry,
            ExternalLinkOwnerKind.NotedDate => OwnerNotedDate,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an external link owner of the format."),
        };

        /// <summary>Reads a stored owner kind; false for anything else.</summary>
        public static bool TryParseOwnerKind(string? text, out ExternalLinkOwnerKind kind)
        {
            switch (text)
            {
                case OwnerEntry:
                    kind = ExternalLinkOwnerKind.Entry;
                    return true;
                case OwnerNotedDate:
                    kind = ExternalLinkOwnerKind.NotedDate;
                    return true;
                default:
                    kind = default;
                    return false;
            }
        }

        /// <summary>An address as stored: trimmed (null stays null).</summary>
        public static string? NormaliseUrl(string? url) => url?.Trim();

        /// <summary>A label as stored: trimmed, and null when empty.</summary>
        public static string? NormaliseLabel(string? label) => label?.Trim() is { Length: > 0 } text ? text : null;

        /// <summary>The address's problem, or null. Checks the address as it would be stored (see <see cref="NormaliseUrl"/>).</summary>
        public static string? UrlError(string? url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return "Enter the link.";
            }

            if (url.Length > MaxUrlLength)
            {
                return $"Keep the link to {MaxUrlLength.ToString("N0", CultureInfo.CurrentCulture)} characters.";
            }

            if (url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                return "A link cannot contain spaces or line breaks.";
            }

            if (!url.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return "Only links starting with https:// can be kept.";
            }

            // The authority is everything between "https://" and the first '/', '?' or '#'. An '@' in it means
            // a user-info part, which can make "https://photos.google.com@example.org" look like another host.
            var rest = url[Prefix.Length..];
            var end = rest.IndexOfAny(['/', '?', '#', '\\']);
            var authority = end < 0 ? rest : rest[..end];

            if (authority.Contains('@', StringComparison.Ordinal))
            {
                return "A link with a name or password before the address (an @ before the host) cannot be kept.";
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                || string.IsNullOrEmpty(uri.Host)
                || uri.UserInfo.Length > 0)
            {
                return "That is not a complete https link.";
            }

            return null;
        }

        /// <summary>The label's problem, or null. Checks the label as it would be stored (see <see cref="NormaliseLabel"/>).</summary>
        public static string? LabelError(string? label)
        {
            if (label is null)
            {
                return null;
            }

            var trimmed = label.Trim();

            if (trimmed.Length == 0)
            {
                return "A label cannot be blank; leave it out instead.";
            }

            return trimmed.Length > MaxLabelLength
                ? $"Keep the label to {MaxLabelLength.ToString(CultureInfo.CurrentCulture)} characters."
                : null;
        }

        /// <summary>The problem with an owner's number of links, or null.</summary>
        public static string? CountError(int count) =>
            count > MaxLinksPerOwner
                ? $"Keep to {MaxLinksPerOwner.ToString(CultureInfo.CurrentCulture)} links."
                : null;

        /// <summary>
        /// The first rule a whole ordered list of links breaks, as it would be stored (each address and label
        /// normalised first), or null when the list may be saved. The count comes first, then each link in order,
        /// address before label.
        /// </summary>
        public static ExternalLinkError? Validate(IReadOnlyList<(string? Url, string? Label)> links)
        {
            ArgumentNullException.ThrowIfNull(links);

            if (CountError(links.Count) is { } countError)
            {
                return new ExternalLinkError(null, ExternalLinkField.Count, countError);
            }

            for (var index = 0; index < links.Count; index++)
            {
                if (UrlError(NormaliseUrl(links[index].Url)) is { } urlError)
                {
                    return new ExternalLinkError(index, ExternalLinkField.Url, urlError);
                }

                if (LabelError(NormaliseLabel(links[index].Label)) is { } labelError)
                {
                    return new ExternalLinkError(index, ExternalLinkField.Label, labelError);
                }
            }

            return null;
        }

        /// <summary>
        /// The host to show for a link: its IDN host in punycode (<see cref="Uri.IdnHost"/>), lower-cased, so
        /// look-alike letters cannot pass for another host. <see langword="null"/> when the address breaks a rule.
        /// </summary>
        public static string? DisplayHost(string? url)
        {
            var stored = NormaliseUrl(url);

            return UrlError(stored) is null && Uri.TryCreate(stored, UriKind.Absolute, out var uri)
                ? uri.IdnHost.ToLowerInvariant()
                : null;
        }
    }
}
