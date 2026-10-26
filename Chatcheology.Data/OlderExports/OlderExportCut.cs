using Chatcheology.Core.Models;

namespace Chatcheology.Data.OlderExports
{
    /// <summary>
    /// The revised D2 cut between an inner export (shown for its whole period) and the outer
    /// export that contains it: the outer is shown only from the message after the one line that
    /// exactly matches the inner export's last message.
    /// </summary>
    /// <remarks>
    /// "Exactly matches" means equal timestamp, type, sender and content, compared ordinally on
    /// <see cref="ParsedMessage.MessageContent"/>, from which the parser has removed only the
    /// U+200E and U+200F direction marks (as in <see cref="OlderExportOverlap"/>). The inner
    /// export's last message is its last in source order. The match must occur exactly once in the
    /// outer export; any other count is reported and the cut is not made. Outer lines up to and
    /// including the match are evidence and are not shown. Nothing is merged line by line.
    /// <para>
    /// The cut orders the two exports only if the shown part repeats nothing of the inner export,
    /// so <see cref="ShownDuplicatingInnerCount"/> counts shown outer lines that exactly equal any
    /// inner line. It must be 0 for the cut to be usable: a duplicate would be shown twice.
    /// </para>
    /// </remarks>
    public sealed class OlderExportCut
    {
        /// <summary>How many outer lines exactly match the inner export's last message.</summary>
        public required int MatchCount { get; init; }

        /// <summary>
        /// The first outer <see cref="ParsedMessage.SequenceNumber"/> that is shown, or null when
        /// <see cref="MatchCount"/> is not 1. One past the outer's last message when the match is
        /// the outer's last line, so that nothing is shown.
        /// </summary>
        public required int? ShownFromSequenceNumber { get; init; }

        /// <summary>Outer messages up to and including the match (0 when there is no cut).</summary>
        public required int HiddenCount { get; init; }

        /// <summary>
        /// Of the hidden messages, those dated within the inner export's period (first to last
        /// timestamp inclusive): the inner export shows that time.
        /// </summary>
        public required int HiddenWithinInnerPeriodCount { get; init; }

        /// <summary>
        /// Of the hidden messages, those dated before the inner export's first message. These are
        /// shown nowhere.
        /// </summary>
        public required int HiddenBeforeInnerPeriodCount { get; init; }

        /// <summary>
        /// Of the hidden messages, those dated after the inner export's last message (out of
        /// order in the outer export). These are shown nowhere either.
        /// </summary>
        public required int HiddenAfterInnerPeriodCount { get; init; }

        /// <summary>Outer messages after the match (0 when there is no cut).</summary>
        public required int ShownCount { get; init; }

        /// <summary>
        /// Shown outer messages that exactly equal some inner message. Must be 0.
        /// </summary>
        public required int ShownDuplicatingInnerCount { get; init; }

        /// <summary>The earliest shown outer timestamp, or null when nothing is shown.</summary>
        public required DateTime? ShownFirst { get; init; }

        /// <summary>The latest shown outer timestamp, or null when nothing is shown.</summary>
        public required DateTime? ShownLast { get; init; }

        /// <summary>Whether the cut was found exactly once.</summary>
        public bool IsFound => MatchCount == 1;

        /// <summary>Whether the cut was found exactly once and its shown part repeats no inner line.</summary>
        public bool IsUsable => IsFound && ShownDuplicatingInnerCount == 0;

        /// <summary>The messages of <paramref name="outer"/> that the cut shows.</summary>
        public IReadOnlyList<ParsedMessage> ShownMessages(IReadOnlyList<ParsedMessage> outer)
        {
            ArgumentNullException.ThrowIfNull(outer);

            return ShownFromSequenceNumber is { } from
                ? outer.Where(message => message.SequenceNumber >= from).ToList()
                : [];
        }

        /// <summary>
        /// Finds the cut of <paramref name="outer"/> after <paramref name="inner"/>'s last message.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="inner"/> has no messages.</exception>
        public static OlderExportCut Find(
            IReadOnlyList<ParsedMessage> inner,
            IReadOnlyList<ParsedMessage> outer)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(outer);

            if (inner.Count == 0)
            {
                throw new ArgumentException(
                    "The inner export has no messages, so it has no last line to cut after.",
                    nameof(inner));
            }

            var last = inner.MaxBy(message => message.SequenceNumber)!;
            var lastKey = Key(last);

            var matches = outer.Where(message => Key(message) == lastKey).ToList();

            if (matches.Count != 1)
            {
                return new OlderExportCut
                {
                    MatchCount = matches.Count,
                    ShownFromSequenceNumber = null,
                    HiddenCount = 0,
                    HiddenWithinInnerPeriodCount = 0,
                    HiddenBeforeInnerPeriodCount = 0,
                    HiddenAfterInnerPeriodCount = 0,
                    ShownCount = 0,
                    ShownDuplicatingInnerCount = 0,
                    ShownFirst = null,
                    ShownLast = null,
                };
            }

            var shownFrom = matches[0].SequenceNumber + 1;
            var shown = outer.Where(message => message.SequenceNumber >= shownFrom).ToList();
            var hidden = outer.Where(message => message.SequenceNumber < shownFrom).ToList();

            var innerFirst = inner.Min(message => message.MessageDateTime);
            var innerLast = inner.Max(message => message.MessageDateTime);
            var innerKeys = inner.Select(Key).ToHashSet();

            return new OlderExportCut
            {
                MatchCount = 1,
                ShownFromSequenceNumber = shownFrom,
                HiddenCount = hidden.Count,
                HiddenWithinInnerPeriodCount = hidden.Count(message =>
                    message.MessageDateTime >= innerFirst && message.MessageDateTime <= innerLast),
                HiddenBeforeInnerPeriodCount = hidden.Count(message => message.MessageDateTime < innerFirst),
                HiddenAfterInnerPeriodCount = hidden.Count(message => message.MessageDateTime > innerLast),
                ShownCount = shown.Count,
                ShownDuplicatingInnerCount = shown.Count(message => innerKeys.Contains(Key(message))),
                ShownFirst = shown.Count == 0 ? null : shown.Min(message => message.MessageDateTime),
                ShownLast = shown.Count == 0 ? null : shown.Max(message => message.MessageDateTime),
            };
        }

        /// <summary>The compared fields; tuple equality compares the strings ordinally.</summary>
        private static (DateTime, MessageType, string?, string) Key(ParsedMessage message) =>
            (message.MessageDateTime, message.MessageType, message.Sender, message.MessageContent);
    }
}
