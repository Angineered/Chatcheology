using System.Globalization;

namespace Chatcheology.Data.Workspace
{
    /// <summary>
    /// The exact text formats the workspace stores its two kinds of calendar value in, and the
    /// only sanctioned way to read them back.
    /// </summary>
    /// <remarks>
    /// Written and read through one owner. Until Phase 6 nothing read these values back, so each
    /// format was spelled out beside the code that wrote it; a second spelling in a reader is
    /// exactly how a writer and a reader come to disagree about what is stored, and a date read
    /// under the wrong format is either an exception or — worse — a plausible wrong day.
    /// <para>
    /// The separators are quoted so <c>-</c>, <c>T</c> and <c>:</c> are literal characters rather
    /// than culture-dependent placeholders. Every format and parse here pairs that with
    /// <see cref="CultureInfo.InvariantCulture"/> and exact parsing, so a workspace written on one
    /// machine reads identically on another.
    /// </para>
    /// <para>
    /// Nothing here converts, shifts or normalises a value. These are the persisted spellings of a
    /// wall-clock reading and of a naming-derived calendar date, and both mean what they meant when
    /// they were written.
    /// </para>
    /// </remarks>
    internal static class WorkspaceDateFormats
    {
        /// <summary>
        /// How <c>Message.MessageDateTimeLocal</c> is stored: a local wall-clock reading with no
        /// <c>Z</c> and no offset, which also sorts correctly as text.
        /// </summary>
        internal const string MessageDateTimeLocal = "yyyy'-'MM'-'dd'T'HH':'mm':'ss";

        /// <summary>
        /// How <c>MediaFile.FileDate</c> is stored: a calendar date with no time and no zone.
        /// </summary>
        internal const string FileDate = "yyyy'-'MM'-'dd";

        /// <summary>
        /// Reads a stored <c>Message.MessageDateTimeLocal</c>.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> and the wall-clock reading, or <see langword="false"/> when the
        /// text is not exactly what this workspace writes. Nothing is guessed from a near miss.
        /// </returns>
        /// <remarks>
        /// <see cref="DateTimeStyles.None"/> keeps the result
        /// <see cref="DateTimeKind.Unspecified"/>, matching what was written: a reading with no
        /// zone must not become one that claims local time or UTC just by being parsed.
        /// </remarks>
        internal static bool TryParseMessageDateTimeLocal(string text, out DateTime value) =>
            DateTime.TryParseExact(
                text,
                MessageDateTimeLocal,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out value);

        /// <summary>
        /// Reads a stored non-null <c>MediaFile.FileDate</c>.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> and the calendar date, or <see langword="false"/> when the text
        /// is not exactly what this workspace writes.
        /// </returns>
        internal static bool TryParseFileDate(string text, out DateOnly value) =>
            DateOnly.TryParseExact(
                text,
                FileDate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out value);
    }
}
