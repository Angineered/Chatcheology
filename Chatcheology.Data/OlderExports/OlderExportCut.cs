using Chatcheology.Core.Models;

namespace Chatcheology.Data.OlderExports
{
    /// <summary>
    /// The revised D2 cut between an inner export (shown for its whole period) and the outer
    /// export that contains it: the outer is shown only from the message after the one line that
    /// exactly matches the inner export's last message.
    /// </summary>
    /// <remarks>
    /// "Exactly matches" means equal timestamp, type, sender and content (ordinal, as in
    /// <see cref="OlderExportOverlap"/>). The inner export's last message is its last in source
    /// order. The match must occur exactly once in the outer export; any other count is reported
    /// and the cut is not made. Outer lines up to and including the match are evidence and are not
    /// shown. Nothing is merged line by line.
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

        /// <summary>Outer messages after the match (0 when there is no cut).</summary>
        public required int ShownCount { get; init; }

        /// <summary>The earliest shown outer timestamp, or null when nothing is shown.</summary>
        public required DateTime? ShownFirst { get; init; }

        /// <summary>The latest shown outer timestamp, or null when nothing is shown.</summary>
        public required DateTime? ShownLast { get; init; }

        /// <summary>Whether the cut was found exactly once.</summary>
        public bool IsFound => MatchCount == 1;

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

            var matches = outer.Where(message =>
                    message.MessageDateTime == last.MessageDateTime
                    && message.MessageType == last.MessageType
                    && string.Equals(message.Sender, last.Sender, StringComparison.Ordinal)
                    && string.Equals(message.MessageContent, last.MessageContent, StringComparison.Ordinal))
                .ToList();

            if (matches.Count != 1)
            {
                return new OlderExportCut
                {
                    MatchCount = matches.Count,
                    ShownFromSequenceNumber = null,
                    HiddenCount = 0,
                    ShownCount = 0,
                    ShownFirst = null,
                    ShownLast = null,
                };
            }

            var shownFrom = matches[0].SequenceNumber + 1;
            var shown = outer.Where(message => message.SequenceNumber >= shownFrom).ToList();

            return new OlderExportCut
            {
                MatchCount = 1,
                ShownFromSequenceNumber = shownFrom,
                HiddenCount = outer.Count - shown.Count,
                ShownCount = shown.Count,
                ShownFirst = shown.Count == 0 ? null : shown.Min(message => message.MessageDateTime),
                ShownLast = shown.Count == 0 ? null : shown.Max(message => message.MessageDateTime),
            };
        }
    }
}
