using System.Globalization;

namespace Chatcheology.Data.Media
{
    /// <summary>
    /// Finds the <c>-YYYYMMDD-WA</c> marker in a file name and reports where the suffix after it
    /// begins.
    /// </summary>
    /// <remarks>
    /// A deliberate mirror of the scan inside
    /// <see cref="MediaClassification.DeriveFileDate(string, string)"/>, which reports only the date
    /// it found and not the position it found it at. The census needs that position, and the Phase 5
    /// classifier is left untouched rather than being widened to expose it.
    /// <para>
    /// Mirroring a rule is a drift risk taken knowingly. It is contained by tests that run this
    /// locator and the committed classifier over the same names — including the committed
    /// classifier's own cases — and require them to agree. If either ever changes alone, those tests
    /// fail rather than the two quietly disagreeing about which eight digits are a date.
    /// </para>
    /// <para>
    /// The rule, character for character with the committed one: the eight digits must be preceded
    /// by <c>-</c>, so a name beginning with them is never matched; the digits must be ASCII, so
    /// digits from other scripts are not read; the marker must be followed immediately by the
    /// ordinal text <c>-WA</c>; and the eight digits must form a real calendar date. Scanning
    /// continues past a structurally correct group whose digits are not a date, so a later valid
    /// group in the same name still wins.
    /// </para>
    /// <para>
    /// Deliberately not gated on source type. <see cref="MediaClassification.DeriveFileDate"/> reads
    /// nothing outside a WhatsApp media directory, but the census must be able to ask what a name
    /// looks like whatever source it came from.
    /// </para>
    /// </remarks>
    internal static class WhatsAppNameMarker
    {
        internal const string TrailingMarker = "-WA";

        internal const int DateDigitCount = 8;

        private const string DateFormat = "yyyyMMdd";

        /// <summary>
        /// Locates the marker in <paramref name="fileName"/>.
        /// </summary>
        /// <param name="suffixStartIndex">
        /// The index of the first character after <c>-WA</c>, which may be the end of the string.
        /// </param>
        /// <param name="fileDate">The date the eight digits encode.</param>
        /// <returns>Whether a marker was found.</returns>
        internal static bool TryLocate(
            string fileName, out int suffixStartIndex, out DateOnly fileDate)
        {
            ArgumentNullException.ThrowIfNull(fileName);

            var span = fileName.AsSpan();

            for (var start = 1;
                 start + DateDigitCount + TrailingMarker.Length <= span.Length;
                 start++)
            {
                if (span[start - 1] != '-')
                {
                    continue;
                }

                var digits = span.Slice(start, DateDigitCount);

                if (!IsAllAsciiDigits(digits))
                {
                    continue;
                }

                if (!span.Slice(start + DateDigitCount, TrailingMarker.Length)
                        .Equals(TrailingMarker, StringComparison.Ordinal))
                {
                    continue;
                }

                if (DateOnly.TryParseExact(
                        digits,
                        DateFormat,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out fileDate))
                {
                    suffixStartIndex = start + DateDigitCount + TrailingMarker.Length;

                    return true;
                }
            }

            suffixStartIndex = 0;
            fileDate = default;

            return false;
        }

        /// <summary>
        /// Whether <paramref name="fileName"/> contains <c>-</c>, eight ASCII digits and
        /// <c>-WA</c>, whether or not those digits form a real date.
        /// </summary>
        /// <param name="withInvalidDateOnly">
        /// When true, only a group whose digits are <em>not</em> a real date counts.
        /// </param>
        /// <remarks>
        /// The structural half of <see cref="TryLocate"/>, separated so the names carrying no
        /// <c>FileDate</c> can be asked whether they hold the complete shape with an impossible date
        /// inside it — the one case that explains a null <c>FileDate</c> on an otherwise
        /// WhatsApp-shaped name.
        /// </remarks>
        internal static bool ContainsFullStructure(string fileName, bool withInvalidDateOnly)
        {
            ArgumentNullException.ThrowIfNull(fileName);

            var span = fileName.AsSpan();

            for (var start = 1;
                 start + DateDigitCount + TrailingMarker.Length <= span.Length;
                 start++)
            {
                if (span[start - 1] != '-')
                {
                    continue;
                }

                var digits = span.Slice(start, DateDigitCount);

                if (!IsAllAsciiDigits(digits))
                {
                    continue;
                }

                if (!span.Slice(start + DateDigitCount, TrailingMarker.Length)
                        .Equals(TrailingMarker, StringComparison.Ordinal))
                {
                    continue;
                }

                var isRealDate = DateOnly.TryParseExact(
                    digits, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

                if (!withInvalidDateOnly || !isRealDate)
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsAllAsciiDigits(ReadOnlySpan<char> span)
        {
            foreach (var character in span)
            {
                if (!char.IsAsciiDigit(character))
                {
                    return false;
                }
            }

            return span.Length > 0;
        }
    }
}
