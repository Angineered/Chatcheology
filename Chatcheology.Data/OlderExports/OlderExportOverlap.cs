using Chatcheology.Core.Models;

namespace Chatcheology.Data.OlderExports
{
    /// <summary>
    /// The D2 overlap check: how many logical messages of one export do not occur in another, and
    /// an aggregate breakdown of why.
    /// </summary>
    /// <remarks>
    /// Two messages are the same line when their timestamp, type, sender and
    /// <see cref="ParsedMessage.MessageContent"/> are all exactly equal (ordinal; the content has
    /// only had the U+200E and U+200F direction marks removed by the parser). Sequence numbers and
    /// line numbers are ignored, because the two files number their lines independently.
    /// <para>
    /// Compared as multisets: if a line occurs twice in X and once in Y, one of the two counts as
    /// not found. Only counts leave this type; nothing is merged and no line is reported.
    /// </para>
    /// </remarks>
    public static class OlderExportOverlap
    {
        /// <summary>
        /// The number of messages in <paramref name="inner"/> with no exact counterpart in
        /// <paramref name="outer"/>. Zero means every line of <paramref name="inner"/> is in
        /// <paramref name="outer"/>.
        /// </summary>
        public static int CountNotFoundIn(
            IReadOnlyList<ParsedMessage> inner,
            IReadOnlyList<ParsedMessage> outer)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(outer);

            return FindNotFound(inner, outer).Count;
        }

        /// <summary>
        /// Compares <paramref name="inner"/> with <paramref name="outer"/> and sorts every inner
        /// line not found exactly into one category, and counts the reverse direction within the
        /// inner export's period.
        /// </summary>
        /// <remarks>
        /// Each unmatched inner line is put in the first category that applies, so the categories
        /// add up to <see cref="OlderExportOverlapBreakdown.NotFoundCount"/>. "Same timestamp and
        /// sender" looks at every outer line with that timestamp and sender, matched or not:
        /// <list type="number">
        /// <item>The inner line is a <c>(file attached)</c> line and an outer line at the same
        /// timestamp and sender is <c>&lt;Media omitted&gt;</c>.</item>
        /// <item>An outer line has exactly the same content, but the inner export repeats the line
        /// more often than the outer does.</item>
        /// <item>An outer line at the same timestamp and sender has content equal once leading and
        /// trailing whitespace is removed from each line and from the whole.</item>
        /// <item>Outer lines at the same timestamp and sender exist, but all differ otherwise.</item>
        /// <item>No outer line has the same timestamp and sender.</item>
        /// </list>
        /// Timestamp and sender together fix the type (only system lines have no sender), so the
        /// type needs no category of its own.
        /// </remarks>
        public static OlderExportOverlapBreakdown Analyse(
            IReadOnlyList<ParsedMessage> inner,
            IReadOnlyList<ParsedMessage> outer)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(outer);

            var notFound = FindNotFound(inner, outer);

            var outerByTimeAndSender = outer.ToLookup(message => (message.MessageDateTime, message.Sender));

            int fileAttached = 0, repeated = 0, whitespace = 0, otherContent = 0, noLine = 0;

            foreach (var message in notFound)
            {
                var candidates = outerByTimeAndSender[(message.MessageDateTime, message.Sender)].ToList();

                if (candidates.Count == 0)
                {
                    noLine++;
                }
                else if (message.AttachedFileName is not null
                    && candidates.Any(candidate => candidate.IsMediaPlaceholder))
                {
                    fileAttached++;
                }
                else if (candidates.Any(candidate => string.Equals(
                    candidate.MessageContent, message.MessageContent, StringComparison.Ordinal)))
                {
                    repeated++;
                }
                else if (candidates.Any(candidate => string.Equals(
                    TrimLines(candidate.MessageContent), TrimLines(message.MessageContent), StringComparison.Ordinal)))
                {
                    whitespace++;
                }
                else
                {
                    otherContent++;
                }
            }

            // The inner export's period, first to last timestamp inclusive.
            var outerInPeriod = new List<ParsedMessage>();

            if (inner.Count > 0)
            {
                var first = inner.Min(message => message.MessageDateTime);
                var last = inner.Max(message => message.MessageDateTime);

                outerInPeriod.AddRange(outer.Where(message =>
                    message.MessageDateTime >= first && message.MessageDateTime <= last));
            }

            return new OlderExportOverlapBreakdown
            {
                InnerCount = inner.Count,
                NotFoundCount = notFound.Count,
                FileAttachedWhereOuterHasMediaOmittedCount = fileAttached,
                RepeatedMoreOftenThanInOuterCount = repeated,
                WhitespaceOnlyDifferenceCount = whitespace,
                OtherContentDifferenceCount = otherContent,
                NoOuterLineAtTimestampAndSenderCount = noLine,
                OuterInInnerPeriodCount = outerInPeriod.Count,
                OuterInInnerPeriodNotFoundInInnerCount = FindNotFound(outerInPeriod, inner).Count,
            };
        }

        /// <summary>
        /// The messages of <paramref name="inner"/> left over after each is paired with an
        /// identical, not yet paired message of <paramref name="outer"/>.
        /// </summary>
        private static List<ParsedMessage> FindNotFound(
            IReadOnlyList<ParsedMessage> inner,
            IReadOnlyList<ParsedMessage> outer)
        {
            var available = new Dictionary<LineKey, int>();

            foreach (var message in outer)
            {
                var key = LineKey.Of(message);
                available[key] = available.GetValueOrDefault(key) + 1;
            }

            var notFound = new List<ParsedMessage>();

            foreach (var message in inner)
            {
                var key = LineKey.Of(message);

                if (available.TryGetValue(key, out var remaining) && remaining > 0)
                {
                    available[key] = remaining - 1;
                }
                else
                {
                    notFound.Add(message);
                }
            }

            return notFound;
        }

        /// <summary>
        /// The content with leading and trailing whitespace removed from every line and from the
        /// whole, for the whitespace-only category. Nothing else is changed.
        /// </summary>
        private static string TrimLines(string content) =>
            string.Join(
                ParsedMessage.LineSeparator,
                content.Split(ParsedMessage.LineSeparator).Select(line => line.Trim()))
            .Trim();

        /// <summary>
        /// The compared fields. A record struct of strings compares them with ordinal equality.
        /// </summary>
        private readonly record struct LineKey(
            DateTime Timestamp,
            MessageType Type,
            string? Sender,
            string Content)
        {
            internal static LineKey Of(ParsedMessage message) => new(
                message.MessageDateTime,
                message.MessageType,
                message.Sender,
                message.MessageContent);
        }
    }
}
