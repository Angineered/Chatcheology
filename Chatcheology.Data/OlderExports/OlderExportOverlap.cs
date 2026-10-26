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
        /// Compares <paramref name="inner"/> with <paramref name="outer"/> in both directions: every
        /// inner line not found exactly in the outer export, and every outer line within the inner
        /// export's period not found exactly in the inner export. Each unmatched line is sorted into
        /// one category and one line kind.
        /// </summary>
        /// <remarks>
        /// Each unmatched line is put in the first category that applies (see
        /// <see cref="OverlapCategory"/>), so the categories add up to the not-found count. "Same
        /// timestamp and sender" looks at every line of the other export with that timestamp and
        /// sender, matched or not. Timestamp and sender together fix the type (only system lines
        /// have no sender), so the type needs no category of its own.
        /// <para>
        /// For lines with no counterpart at the same timestamp and sender, the clock-shift hints
        /// look for a line by the same sender within one minute, and for an identical line within
        /// 60 and 120 minutes, in the whole other export.
        /// </para>
        /// <para>
        /// The reverse direction compares the outer lines whose timestamp lies within the inner
        /// export's first-to-last period (inclusive) against the whole inner export.
        /// </para>
        /// </remarks>
        public static OlderExportOverlapBreakdown Analyse(
            IReadOnlyList<ParsedMessage> inner,
            IReadOnlyList<ParsedMessage> outer)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(outer);

            var forward = AnalyseDirection(inner, outer);

            var outerInPeriod = new List<ParsedMessage>();

            if (inner.Count > 0)
            {
                var first = inner.Min(message => message.MessageDateTime);
                var last = inner.Max(message => message.MessageDateTime);

                outerInPeriod.AddRange(outer.Where(message =>
                    message.MessageDateTime >= first && message.MessageDateTime <= last));
            }

            var reverse = AnalyseDirection(outerInPeriod, inner);

            return new OlderExportOverlapBreakdown
            {
                InnerCount = inner.Count,
                NotFoundCount = forward.NotFoundCount,
                FileAttachedWhereOuterHasMediaOmittedCount = forward.Count(OverlapCategory.FileAttachedVersusMediaOmitted),
                RepeatedMoreOftenThanInOuterCount = forward.Count(OverlapCategory.RepeatedMoreOften),
                WhitespaceOnlyDifferenceCount = forward.Count(OverlapCategory.WhitespaceOnly),
                OtherContentDifferenceCount = forward.Count(OverlapCategory.OtherContent),
                NoOuterLineAtTimestampAndSenderCount = forward.Count(OverlapCategory.NoLineAtTimestampAndSender),
                OuterInInnerPeriodCount = reverse.ComparedCount,
                OuterInInnerPeriodNotFoundInInnerCount = reverse.NotFoundCount,
                Forward = forward,
                Reverse = reverse,
            };
        }

        /// <summary>
        /// The lines of <paramref name="compared"/> with no exact counterpart in
        /// <paramref name="other"/>, sorted, with clock-shift hints.
        /// </summary>
        private static OlderExportOverlapDirection AnalyseDirection(
            IReadOnlyList<ParsedMessage> compared,
            IReadOnlyList<ParsedMessage> other)
        {
            var notFound = FindNotFound(compared, other);

            var otherByTimeAndSender = other.ToLookup(message => (message.MessageDateTime, message.Sender));
            var otherBySender = other.ToLookup(message => message.Sender);

            var unmatched = new List<OverlapUnmatchedLine>(notFound.Count);
            int withinOneMinute = 0, identicalWithinSixty = 0, identicalWithinOneHundredTwenty = 0;

            foreach (var message in notFound)
            {
                var candidates = otherByTimeAndSender[(message.MessageDateTime, message.Sender)].ToList();
                var category = Categorise(message, candidates);

                if (category == OverlapCategory.NoLineAtTimestampAndSender)
                {
                    var sameSender = otherBySender[message.Sender].ToList();

                    if (sameSender.Any(candidate => IsWithin(candidate, message, TimeSpan.FromMinutes(1))))
                    {
                        withinOneMinute++;
                    }

                    var identical = sameSender
                        .Where(candidate => LineKey.Of(candidate) with { Timestamp = default }
                            == LineKey.Of(message) with { Timestamp = default })
                        .ToList();

                    if (identical.Any(candidate => IsWithin(candidate, message, TimeSpan.FromMinutes(60))))
                    {
                        identicalWithinSixty++;
                    }

                    if (identical.Any(candidate => IsWithin(candidate, message, TimeSpan.FromMinutes(120))))
                    {
                        identicalWithinOneHundredTwenty++;
                    }
                }

                unmatched.Add(new OverlapUnmatchedLine(
                    message.SourceLineStart, category, OlderExportOverlapDirection.KindOf(message)));
            }

            return new OlderExportOverlapDirection
            {
                ComparedCount = compared.Count,
                Unmatched = unmatched,
                SameSenderWithinOneMinuteCount = withinOneMinute,
                IdenticalWithinSixtyMinutesCount = identicalWithinSixty,
                IdenticalWithinOneHundredTwentyMinutesCount = identicalWithinOneHundredTwenty,
            };
        }

        private static OverlapCategory Categorise(ParsedMessage message, List<ParsedMessage> candidates)
        {
            if (candidates.Count == 0)
            {
                return OverlapCategory.NoLineAtTimestampAndSender;
            }

            if ((message.AttachedFileName is not null && candidates.Any(candidate => candidate.IsMediaPlaceholder))
                || (message.IsMediaPlaceholder && candidates.Any(candidate => candidate.AttachedFileName is not null)))
            {
                return OverlapCategory.FileAttachedVersusMediaOmitted;
            }

            if (candidates.Any(candidate => string.Equals(
                    candidate.MessageContent, message.MessageContent, StringComparison.Ordinal)))
            {
                return OverlapCategory.RepeatedMoreOften;
            }

            if (candidates.Any(candidate => string.Equals(
                    TrimLines(candidate.MessageContent), TrimLines(message.MessageContent), StringComparison.Ordinal)))
            {
                return OverlapCategory.WhitespaceOnly;
            }

            return OverlapCategory.OtherContent;
        }

        /// <summary>Whether the two timestamps are at most <paramref name="limit"/> apart.</summary>
        private static bool IsWithin(ParsedMessage left, ParsedMessage right, TimeSpan limit) =>
            (left.MessageDateTime - right.MessageDateTime).Duration() <= limit;

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
