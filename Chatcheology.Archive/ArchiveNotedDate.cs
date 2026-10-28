using System.Globalization;

namespace Chatcheology.Archive
{
    /// <summary>
    /// A noted date (CH-47): a day of the year that matters (an anniversary, a birthday), shown every
    /// year on that day. Not a memory: no collection, no entry, no link. Carried in a version 2 archive's
    /// <c>NotedDate</c> table exactly as the memories file holds it.
    /// </summary>
    public sealed class ArchiveNotedDate
    {
        /// <summary>The noted date, with the identifier the memories file gave it.</summary>
        public required long NotedDateID { get; init; }

        /// <summary>The title, 1 to <see cref="NotedDateRules.MaxTitleLength"/> characters.</summary>
        public required string Title { get; init; }

        /// <summary>The month, 1 to 12.</summary>
        public required int Month { get; init; }

        /// <summary>The day, valid for the month (29 February allowed).</summary>
        public required int Day { get; init; }

        /// <summary>The year it started, or <see langword="null"/> when not given.</summary>
        public required int? Year { get; init; }

        /// <summary>A short note, or <see langword="null"/>.</summary>
        public required string? Note { get; init; }
    }

    /// <summary>The fields of a noted date a rule names.</summary>
    public enum NotedDateField
    {
        /// <summary>The title.</summary>
        Title,

        /// <summary>The month and day (and, for 29 February, the year's being a leap year).</summary>
        Date,

        /// <summary>The year.</summary>
        Year,

        /// <summary>The note.</summary>
        Note,
    }

    /// <summary>
    /// The field rules of a noted date (CH-47), in one place: the desktop store refuses a save that breaks
    /// one, the archive writer refuses such a row, and the archive validator counts each.
    /// </summary>
    /// <remarks>
    /// <para>Title: 1 to 80 characters after trimming. Month 1 to 12; day valid for the month, 29 February
    /// allowed without a year or with a leap year. Year optional, from 1900 to the latest year allowed
    /// (the current year; for an archive, the year of its creation). Note optional: when present not blank
    /// and at most 500 characters.</para>
    /// <para>Lengths are counted in UTF-16 code units (<see cref="string.Length"/>), so a text that passes
    /// here also passes the schemas' <c>length()</c> checks, which count characters.</para>
    /// </remarks>
    public static class NotedDateRules
    {
        /// <summary>The longest title kept.</summary>
        public const int MaxTitleLength = 80;

        /// <summary>The longest note kept.</summary>
        public const int MaxNoteLength = 500;

        /// <summary>The earliest year allowed.</summary>
        public const int MinYear = 1900;

        /// <summary>
        /// The <c>NotedDate</c> table's <c>CHECK</c> clauses, shared by the memories file and the archive so the
        /// two state the same rules. Everything but the latest year, which a constraint cannot know.
        /// </summary>
        public const string TableChecks =
            """
            CHECK (typeof(Title) = 'text' AND length(trim(Title)) > 0 AND length(Title) <= 80),
                CHECK (typeof(Month) = 'integer' AND Month BETWEEN 1 AND 12),
                CHECK (typeof(Day) = 'integer' AND Day >= 1
                    AND Day <= CASE WHEN Month = 2 THEN 29 WHEN Month IN (4, 6, 9, 11) THEN 30 ELSE 31 END),
                CHECK (Year IS NULL OR (typeof(Year) = 'integer' AND Year >= 1900)),
                CHECK (NOT (Month = 2 AND Day = 29 AND Year IS NOT NULL
                    AND NOT (Year % 4 = 0 AND (Year % 100 <> 0 OR Year % 400 = 0)))),
                CHECK (Note IS NULL OR (typeof(Note) = 'text' AND length(trim(Note)) > 0 AND length(Note) <= 500))
            """;

        /// <summary>
        /// The latest year allowed for an archive created at <paramref name="createdUtc"/>: the year anywhere
        /// on Earth at that moment (UTC+14), so a date saved on New Year's morning in an eastern zone is not
        /// refused by an archive written within the same hour.
        /// </summary>
        public static int LatestYearAt(DateTime createdUtc) => createdUtc.AddHours(14).Year;

        /// <summary>Every rule the noted date breaks, one message per field; empty when it is fine.</summary>
        /// <param name="title">The title, as it would be stored.</param>
        /// <param name="month">The month.</param>
        /// <param name="day">The day.</param>
        /// <param name="year">The year, or null.</param>
        /// <param name="note">The note as it would be stored (null for none).</param>
        /// <param name="latestYear">The latest year allowed.</param>
        public static IReadOnlyDictionary<NotedDateField, string> Validate(
            string? title, int month, int day, int? year, string? note, int latestYear)
        {
            var errors = new Dictionary<NotedDateField, string>();

            if (TitleError(title) is { } titleError)
            {
                errors[NotedDateField.Title] = titleError;
            }

            if (DateError(month, day, year) is { } dateError)
            {
                errors[NotedDateField.Date] = dateError;
            }

            if (YearError(year, latestYear) is { } yearError)
            {
                errors[NotedDateField.Year] = yearError;
            }

            if (NoteError(note) is { } noteError)
            {
                errors[NotedDateField.Note] = noteError;
            }

            return errors;
        }

        /// <summary>The title's problem, or null.</summary>
        public static string? TitleError(string? title)
        {
            var trimmed = title?.Trim() ?? string.Empty;

            if (trimmed.Length == 0)
            {
                return "Give the date a title.";
            }

            return trimmed.Length > MaxTitleLength
                ? $"Keep the title to {MaxTitleLength.ToString(CultureInfo.CurrentCulture)} characters."
                : null;
        }

        /// <summary>
        /// The month and day's problem, or null. 29 February needs no year or a leap year; a year outside the
        /// allowed range is the year's problem, not the date's.
        /// </summary>
        public static string? DateError(int month, int day, int? year)
        {
            if (month is < 1 or > 12)
            {
                return "Choose a month.";
            }

            if (day < 1 || day > DateTime.DaysInMonth(2000, month))
            {
                return "That day is not in that month.";
            }

            if (month == 2 && day == 29 && year is { } y and >= 1 and <= 9999 && !DateTime.IsLeapYear(y))
            {
                return "29 February is only in a leap year: choose a leap year or leave the year out.";
            }

            return null;
        }

        /// <summary>The year's problem, or null. No year is fine.</summary>
        public static string? YearError(int? year, int latestYear) =>
            year is { } y && (y < MinYear || y > latestYear)
                ? $"The year must be from {MinYear.ToString(CultureInfo.CurrentCulture)} to " +
                  $"{latestYear.ToString(CultureInfo.CurrentCulture)}, or left out."
                : null;

        /// <summary>The note's problem, or null. No note is fine; a stored note is never blank.</summary>
        public static string? NoteError(string? note)
        {
            if (note is null)
            {
                return null;
            }

            var trimmed = note.Trim();

            if (trimmed.Length == 0)
            {
                return "A note cannot be blank; leave it out instead.";
            }

            return trimmed.Length > MaxNoteLength
                ? $"Keep the note to {MaxNoteLength.ToString(CultureInfo.CurrentCulture)} characters."
                : null;
        }

        /// <summary>A note as stored: trimmed, and null when empty.</summary>
        public static string? NormaliseNote(string? note) => note?.Trim() is { Length: > 0 } text ? text : null;
    }
}
