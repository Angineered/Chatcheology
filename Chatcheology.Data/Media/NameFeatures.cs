using System.Globalization;

namespace Chatcheology.Data.Media
{
    /// <summary>
    /// Purely syntactic questions that can be asked of a file name.
    /// </summary>
    /// <remarks>
    /// Each of these answers "does this text contain this shape?" and nothing else. None of them
    /// says what a name means, why it looks as it does, or where it came from, because the workspace
    /// records none of that and a census that guessed would be inventing its own evidence.
    /// <para>
    /// A run here means eight consecutive ASCII digits anywhere in the name, which may sit inside a
    /// longer digit group. That is deliberately looser than the committed date rule, whose eight
    /// digits must be followed immediately by the marker; these features exist to describe names the
    /// committed rule rejected.
    /// </para>
    /// </remarks>
    internal static class NameFeatures
    {
        private const int DateDigitCount = 8;

        private const string DateFormat = "yyyyMMdd";

        /// <summary>Whether eight consecutive ASCII digits appear anywhere.</summary>
        internal static bool ContainsEightDigitRun(string fileName) =>
            ScanEightDigitRuns(fileName, (_, _) => true);

        /// <summary>
        /// Whether eight consecutive ASCII digits appear immediately after a <c>-</c>.
        /// </summary>
        internal static bool ContainsHyphenPrefixedEightDigitRun(string fileName) =>
            ScanEightDigitRuns(fileName, (span, start) => start > 0 && span[start - 1] == '-');

        /// <summary>
        /// Whether eight consecutive ASCII digits appear that form a real calendar date.
        /// </summary>
        internal static bool ContainsValidCalendarDateRun(string fileName) =>
            ScanEightDigitRuns(
                fileName,
                (span, start) => DateOnly.TryParseExact(
                    span.Slice(start, DateDigitCount),
                    DateFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _));

        /// <summary>
        /// Whether the text <c>WA</c> appears followed immediately by at least one ASCII digit.
        /// </summary>
        internal static bool ContainsWAFollowedByDigits(string fileName)
        {
            ArgumentNullException.ThrowIfNull(fileName);

            var span = fileName.AsSpan();

            for (var index = 0; index + 2 < span.Length; index++)
            {
                if (span[index] == 'W'
                    && span[index + 1] == 'A'
                    && char.IsAsciiDigit(span[index + 2]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Walks every eight-digit window and asks <paramref name="matches"/> about it.
        /// </summary>
        private static bool ScanEightDigitRuns(
            string fileName, EightDigitRunPredicate matches)
        {
            ArgumentNullException.ThrowIfNull(fileName);

            var span = fileName.AsSpan();

            for (var start = 0; start + DateDigitCount <= span.Length; start++)
            {
                if (!WhatsAppNameMarker.IsAllAsciiDigits(span.Slice(start, DateDigitCount)))
                {
                    continue;
                }

                if (matches(span, start))
                {
                    return true;
                }
            }

            return false;
        }

        private delegate bool EightDigitRunPredicate(ReadOnlySpan<char> fileName, int start);
    }
}
