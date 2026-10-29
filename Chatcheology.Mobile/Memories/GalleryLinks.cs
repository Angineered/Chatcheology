using System.Globalization;
using Chatcheology.Archive;

namespace Chatcheology.Mobile.Memories
{
    /// <summary>
    /// One external gallery link (CH-48) of a personal memory or a noted date, as the phone keeps it: the stored
    /// address and an optional label. The address is never shown in full and never loaded by the app.
    /// </summary>
    /// <param name="Url">The stored address.</param>
    /// <param name="Label">The label, or <see langword="null"/>.</param>
    public sealed record GalleryLink(string Url, string? Label)
    {
        /// <summary>
        /// The host to show (<see cref="ExternalLinkRules.DisplayHost"/>), or <see langword="null"/> when the
        /// address breaks a rule.
        /// </summary>
        public string? Host => ExternalLinkRules.DisplayHost(Url);

        /// <summary>What a list shows: the label, or the host when there is none.</summary>
        public string Text => Label ?? Host ?? GalleryLinks.UnusableText;
    }

    /// <summary>What happened when a gallery link was opened.</summary>
    public enum GalleryOpenResult
    {
        /// <summary>Another app was started with the link's launch form.</summary>
        Opened,

        /// <summary>The stored address broke a rule when checked again, or its host is not the one shown: nothing was started.</summary>
        Refused,

        /// <summary>No app on the phone opens it.</summary>
        NoApp,
    }

    /// <summary>
    /// The phone's rules for external gallery links (CH-48), all on top of the shared
    /// <see cref="ExternalLinkRules"/>: which links are kept, how a card names them, the words of the
    /// confirmation, and the check made immediately before a link is handed to another app.
    /// </summary>
    /// <remarks>
    /// The app never loads a link: no preview, no favicon, no check over the network. A link is handed to
    /// another app only after the person confirms, and only as <see cref="ExternalLinkRules.LaunchUri"/> of the
    /// stored address computed again at that moment, never the stored text itself.
    /// </remarks>
    public static class GalleryLinks
    {
        /// <summary>The confirmation's title.</summary>
        public const string ConfirmTitle = "Opens outside Chatcheology";

        /// <summary>The confirmation's accept button.</summary>
        public const string ConfirmOpen = "Open";

        /// <summary>The confirmation's cancel button.</summary>
        public const string ConfirmCancel = "Cancel";

        /// <summary>Said when the link is refused on the second check.</summary>
        public const string RefusedText = "This link cannot be opened.";

        /// <summary>Said when no app opens the link.</summary>
        public const string NoAppText = "No app on this phone can open this link.";

        /// <summary>The muted line under the phone's own link list.</summary>
        public const string OwnLinksNote =
            "Links open outside Chatcheology and can stop working. They are kept on this phone only, never in the archive.";

        /// <summary>A list's text for a link that breaks a rule (never shown for a link read through these rules).</summary>
        internal const string UnusableText = "Link";

        /// <summary>
        /// The links of one owner from the archive, by ordinal: a row that breaks a rule is left out (missing is
        /// better than wrong), and at most <see cref="ExternalLinkRules.MaxLinksPerOwner"/> are kept.
        /// </summary>
        public static IReadOnlyList<GalleryLink> FromArchive(IEnumerable<ArchiveExternalLink> links)
        {
            ArgumentNullException.ThrowIfNull(links);

            return links
                .OrderBy(link => link.Ordinal)
                .ThenBy(link => link.ExternalLinkID)
                .Select(link => Keep(link.Url, link.Label))
                .OfType<GalleryLink>()
                .Take(ExternalLinkRules.MaxLinksPerOwner)
                .ToList();
        }

        /// <summary>
        /// Every owner's links from the archive, grouped by owner, each list by ordinal as <see cref="FromArchive"/>.
        /// </summary>
        public static IReadOnlyDictionary<(ExternalLinkOwnerKind Kind, long OwnerID), IReadOnlyList<GalleryLink>> GroupFromArchive(
            IEnumerable<ArchiveExternalLink> links)
        {
            ArgumentNullException.ThrowIfNull(links);

            return links
                .GroupBy(link => (link.OwnerKind, link.OwnerID))
                .Select(group => (group.Key, Links: FromArchive(group)))
                .Where(pair => pair.Links.Count > 0)
                .ToDictionary(pair => pair.Key, pair => pair.Links);
        }

        /// <summary>
        /// A link as stored (address and label normalised) when it keeps every rule; otherwise
        /// <see langword="null"/>.
        /// </summary>
        public static GalleryLink? Keep(string? url, string? label)
        {
            var storedUrl = ExternalLinkRules.NormaliseUrl(url);
            var storedLabel = ExternalLinkRules.NormaliseLabel(label);

            return ExternalLinkRules.UrlError(storedUrl) is null
                   && ExternalLinkRules.DisplayHost(storedUrl) is not null
                   && ExternalLinkRules.LabelError(storedLabel) is null
                ? new GalleryLink(storedUrl!, storedLabel)
                : null;
        }

        /// <summary>
        /// The card's line: "Gallery · photos.google.com" for one link, "3 galleries" for several, empty for none.
        /// </summary>
        public static string Summary(IReadOnlyList<GalleryLink> links)
        {
            ArgumentNullException.ThrowIfNull(links);

            return links.Count switch
            {
                0 => string.Empty,
                1 => "Gallery · " + (links[0].Host ?? UnusableText),
                _ => links.Count.ToString(CultureInfo.CurrentCulture) + " galleries",
            };
        }

        /// <summary>
        /// The confirmation's message: the host only, or <see langword="null"/> when the stored address breaks a
        /// rule (nothing is offered then).
        /// </summary>
        public static string? ConfirmMessage(GalleryLink link)
        {
            ArgumentNullException.ThrowIfNull(link);

            return link.Host;
        }

        /// <summary>
        /// Checks the stored address again and, only when it still keeps every rule and its host is the one the
        /// confirmation showed, hands its launch form (<see cref="ExternalLinkRules.LaunchUri"/>) to
        /// <paramref name="start"/>. <paramref name="start"/> returns false when no app opens it.
        /// </summary>
        /// <param name="link">The link.</param>
        /// <param name="shownHost">The host the confirmation showed.</param>
        /// <param name="start">Starts another app with the launch form; false when there is none.</param>
        public static GalleryOpenResult Open(GalleryLink link, string shownHost, Func<string, bool> start)
        {
            ArgumentNullException.ThrowIfNull(link);
            ArgumentNullException.ThrowIfNull(start);

            if (ExternalLinkRules.LaunchUri(link.Url) is not { } launch
                || ExternalLinkRules.DisplayHost(link.Url) is not { } host
                || !string.Equals(host, shownHost, StringComparison.Ordinal))
            {
                return GalleryOpenResult.Refused;
            }

            return start(launch) ? GalleryOpenResult.Opened : GalleryOpenResult.NoApp;
        }

        /// <summary>What to say after <see cref="Open"/>, or <see langword="null"/> when another app opened it.</summary>
        public static string? ResultText(GalleryOpenResult result) => result switch
        {
            GalleryOpenResult.Opened => null,
            GalleryOpenResult.NoApp => NoAppText,
            _ => RefusedText,
        };

        /// <summary>The action sheet's choices for several links: "1. Day 1", "2. photos.google.com", ...</summary>
        public static IReadOnlyList<string> Choices(IReadOnlyList<GalleryLink> links)
        {
            ArgumentNullException.ThrowIfNull(links);

            return links.Select((link, index) => (index + 1).ToString(CultureInfo.CurrentCulture) + ". " + link.Text).ToList();
        }

        /// <summary>
        /// The problems of an edited list of links, every one at once (CH-47's rule that a save attempt shows every
        /// problem): rows with neither address nor label are left out first; then the count, and each row's
        /// address (or, when it is fine, its label) by the row's index in the edited list.
        /// </summary>
        /// <returns>The links to store (normalised, in order) when there is no problem.</returns>
        public static GalleryLinkCheck Check(IReadOnlyList<(string? Url, string? Label)> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);

            var errors = new Dictionary<int, string>();
            var kept = new List<(string? Url, string? Label)>();

            for (var index = 0; index < rows.Count; index++)
            {
                var (url, label) = rows[index];

                if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                kept.Add((url, label));

                var storedUrl = ExternalLinkRules.NormaliseUrl(url);

                if ((ExternalLinkRules.UrlError(storedUrl)
                     ?? ExternalLinkRules.LabelError(ExternalLinkRules.NormaliseLabel(label))) is { } error)
                {
                    errors[index] = error;
                }
            }

            var countError = ExternalLinkRules.CountError(kept.Count);

            // The shared rules are the last word: a list that passes here passes them too.
            if (errors.Count == 0 && countError is null && ExternalLinkRules.Validate(kept) is { } shared)
            {
                countError = shared.Message;
            }

            return errors.Count == 0 && countError is null
                ? new GalleryLinkCheck(errors, null, kept.Select(row => Keep(row.Url, row.Label)!).ToList())
                : new GalleryLinkCheck(errors, countError, []);
        }
    }

    /// <summary>The outcome of <see cref="GalleryLinks.Check"/>.</summary>
    /// <param name="RowErrors">Each bad row's problem, by its index in the edited list.</param>
    /// <param name="CountError">Too many links, or <see langword="null"/>.</param>
    /// <param name="Links">The links to store when there is no problem; otherwise empty.</param>
    public sealed record GalleryLinkCheck(IReadOnlyDictionary<int, string> RowErrors, string? CountError, IReadOnlyList<GalleryLink> Links)
    {
        /// <summary>Whether the list may be stored.</summary>
        public bool IsValid => RowErrors.Count == 0 && CountError is null;
    }
}
