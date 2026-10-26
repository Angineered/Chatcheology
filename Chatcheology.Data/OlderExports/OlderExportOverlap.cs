using Chatcheology.Core.Models;

namespace Chatcheology.Data.OlderExports
{
    /// <summary>
    /// The D2 overlap check: how many logical messages of one export do not occur in another.
    /// </summary>
    /// <remarks>
    /// Two messages are the same line when their timestamp, type, sender and
    /// <see cref="ParsedMessage.MessageContent"/> are all exactly equal (ordinal; the content has
    /// only had the U+200E and U+200F direction marks removed by the parser). Sequence numbers and
    /// line numbers are ignored, because the two files number their lines independently.
    /// <para>
    /// Compared as multisets: if a line occurs twice in X and once in Y, one of the two counts as
    /// not found. Only the count leaves this type; nothing is merged and no line is reported.
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

            var available = new Dictionary<LineKey, int>();

            foreach (var message in outer)
            {
                var key = LineKey.Of(message);
                available[key] = available.GetValueOrDefault(key) + 1;
            }

            var notFound = 0;

            foreach (var message in inner)
            {
                var key = LineKey.Of(message);

                if (available.TryGetValue(key, out var remaining) && remaining > 0)
                {
                    available[key] = remaining - 1;
                }
                else
                {
                    notFound++;
                }
            }

            return notFound;
        }

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
