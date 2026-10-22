using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Mobile.Search
{
    /// <summary>One place a match was found, in the text it is reported in.</summary>
    /// <param name="Start">Where the match begins.</param>
    /// <param name="Length">How long the match is, in that text (it may differ from the query's length).</param>
    public readonly record struct MatchRange(int Start, int Length);

    /// <summary>One message that matches a search. No more of the message than the snippet.</summary>
    /// <param name="SequenceNumber">The message, to open the conversation at.</param>
    /// <param name="DateText">Its day, as separators show it.</param>
    /// <param name="TimeText">Its time, as bubbles show it.</param>
    /// <param name="SenderText">Its sender, or "System".</param>
    /// <param name="Snippet">The text around the first match, on one line, with "…" where it was cut.</param>
    /// <param name="Highlights">Every match inside the snippet, as offsets into <paramref name="Snippet"/>.</param>
    /// <param name="Matches">Every match in the whole message text, as offsets into it.</param>
    public sealed record SearchHit(
        int SequenceNumber,
        string DateText,
        string TimeText,
        string SenderText,
        string Snippet,
        IReadOnlyList<MatchRange> Highlights,
        IReadOnlyList<MatchRange> Matches);

    /// <summary>The hits of one search, newest first, and how many messages matched in all.</summary>
    /// <param name="Hits">At most the number asked for, newest first.</param>
    /// <param name="TotalMatches">Every message that matched.</param>
    public sealed record SearchResults(IReadOnlyList<SearchHit> Hits, int TotalMatches)
    {
        /// <summary>No results.</summary>
        public static SearchResults None { get; } = new([], 0);
    }

    /// <summary>
    /// An in-memory index of the conversation's message text, searched ignoring case and accents.
    /// </summary>
    /// <remarks>
    /// <para>Built once from <see cref="ArchiveReader.ReadAllPages"/>; nothing is written anywhere. Every
    /// message with text is in it, system messages included; a message that is only the export's media
    /// placeholder is not. File names, sender names alone and media are not searched.</para>
    /// <para>Matching is a plain substring with <see cref="CompareInfo.IndexOf(ReadOnlySpan{char}, ReadOnlySpan{char}, CompareOptions, out int)"/>
    /// and <see cref="CompareOptions.IgnoreCase"/> | <see cref="CompareOptions.IgnoreNonSpace"/>, which
    /// also gives each match's length in the text, so highlights stay right where folding changes
    /// lengths. The invariant culture's rules are used, so results do not depend on the phone's
    /// language. No ranking: results are newest first, the reverse of chat order.</para>
    /// </remarks>
    public sealed class ConversationSearch
    {
        /// <summary>How many hits a search returns at most unless asked otherwise.</summary>
        public const int DefaultMaximumHits = 500;

        /// <summary>Characters of context kept before the first match in a snippet.</summary>
        public const int ContextBefore = 30;

        /// <summary>The longest a snippet is, before "…" marks are added.</summary>
        public const int SnippetLength = 120;

        private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

        private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

        private readonly List<Entry> _entries;
        private readonly IReadOnlyDictionary<long, string> _names;

        private ConversationSearch(List<Entry> entries, IReadOnlyDictionary<long, string> names)
        {
            _entries = entries;
            _names = names;
        }

        /// <summary>How many messages are in the index.</summary>
        public int Count => _entries.Count;

        /// <summary>Builds the index by reading the whole conversation through <paramref name="reader"/>.</summary>
        public static ConversationSearch Build(ArchiveReader reader, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(reader);

            var names = reader.ReadParticipants().ToDictionary(p => p.ParticipantID, p => p.DisplayName);
            var entries = new List<Entry>();

            foreach (var page in reader.ReadAllPages())
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var message in page.Messages)
                {
                    if (message.Text.Length > 0 && !MessageRow.IsPlaceholder(message.Text))
                    {
                        entries.Add(new Entry(message.SequenceNumber, message.DateTimeLocal, message.SenderParticipantID, message.Text));
                    }
                }
            }

            return new ConversationSearch(entries, names);
        }

        /// <summary>Builds the index with a reader of its own on <paramref name="archiveFolder"/>, so it can run beside the page reads.</summary>
        public static ConversationSearch Build(string archiveFolder, CancellationToken cancellationToken = default)
        {
            using var reader = ArchiveReader.Open(archiveFolder);

            return Build(reader, cancellationToken);
        }

        /// <summary>
        /// The messages whose text contains <paramref name="query"/> ignoring case and accents, newest first.
        /// Leading and trailing spaces of the query are ignored; an empty query finds nothing.
        /// </summary>
        public SearchResults Find(string query, int maximumHits = DefaultMaximumHits, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            ArgumentOutOfRangeException.ThrowIfNegative(maximumHits);

            var needle = query.Trim();

            if (needle.Length == 0)
            {
                return SearchResults.None;
            }

            var hits = new List<SearchHit>();
            var total = 0;

            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                if ((i & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var entry = _entries[i];
                var matches = FindAll(entry.Text, needle);

                if (matches.Count == 0)
                {
                    continue;
                }

                total++;

                if (hits.Count < maximumHits)
                {
                    hits.Add(Hit(entry, matches));
                }
            }

            return new SearchResults(hits, total);
        }

        /// <summary>Every non-overlapping match of <paramref name="needle"/> in <paramref name="text"/>.</summary>
        public static IReadOnlyList<MatchRange> FindAll(string text, string needle)
        {
            ArgumentNullException.ThrowIfNull(text);
            ArgumentNullException.ThrowIfNull(needle);

            var matches = new List<MatchRange>();

            if (needle.Length == 0)
            {
                return matches;
            }

            var start = 0;

            while (start < text.Length)
            {
                var found = Compare.IndexOf(text.AsSpan(start), needle.AsSpan(), Options, out var length);

                if (found < 0)
                {
                    break;
                }

                matches.Add(new MatchRange(start + found, length));

                // An empty match (ignorable characters only) still moves on, so the loop always ends.
                start += found + Math.Max(length, 1);
            }

            return matches;
        }

        private SearchHit Hit(Entry entry, IReadOnlyList<MatchRange> matches)
        {
            var (snippet, start, end) = Snippet(entry.Text, matches[0].Start);
            var shift = start > 0 ? 1 : 0;

            // Only matches wholly inside the cut text; offsets moved past the leading "…".
            var highlights = matches
                .Where(match => match.Start >= start && match.Start + match.Length <= end)
                .Select(match => new MatchRange(match.Start - start + shift, match.Length))
                .ToList();

            var sender = entry.SenderParticipantID is { } id
                ? _names.TryGetValue(id, out var name) ? name : "Unknown sender"
                : "System";

            return new SearchHit(
                entry.SequenceNumber,
                DateSeparatorRow.DayText(DateOnly.FromDateTime(entry.DateTimeLocal)),
                MessageRow.TimeTextOf(entry.DateTimeLocal),
                sender,
                snippet,
                highlights,
                matches);
        }

        /// <summary>
        /// Up to <see cref="SnippetLength"/> characters of <paramref name="text"/> starting
        /// <see cref="ContextBefore"/> before <paramref name="matchStart"/>, line breaks as spaces (one
        /// for one, so offsets hold), never splitting a surrogate pair, with "…" at a cut end.
        /// </summary>
        /// <returns>The snippet, and where in the text its body starts and ends.</returns>
        private static (string Snippet, int Start, int End) Snippet(string text, int matchStart)
        {
            var start = Math.Max(0, matchStart - ContextBefore);
            var end = Math.Min(text.Length, start + SnippetLength);

            if (start > 0 && char.IsLowSurrogate(text[start]))
            {
                start--;
            }

            if (end < text.Length && end > 0 && char.IsHighSurrogate(text[end - 1]))
            {
                end++;
            }

            var body = text[start..end].Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            return ((start > 0 ? "…" : string.Empty) + body + (end < text.Length ? "…" : string.Empty), start, end);
        }

        private sealed record Entry(int SequenceNumber, DateTime DateTimeLocal, long? SenderParticipantID, string Text);
    }
}
