using System.Globalization;

namespace Chatcheology.Archive
{
    /// <summary>How exactly a personal memory is dated: a day, or a month. No time of day.</summary>
    public enum MemoryDatePrecision
    {
        /// <summary>A day, such as 2 January 2018.</summary>
        Day,

        /// <summary>A month, such as January 2018.</summary>
        Month,
    }

    /// <summary>
    /// The field rules of a personal memory, in one place (CH-54): the desktop's personal memory form and store,
    /// and the phone's own memories store, check a memory against these. Pure: no file, no database.
    /// </summary>
    /// <remarks>
    /// <para>Title: 1 to 200 characters after trimming. Summary: at most 2,000 characters after trimming. Source:
    /// 1 to 200 characters after trimming. Photo caption: at most 500 characters after trimming. The end, when
    /// given, is not before the start, compared at the precision. Every year needs Day precision and no end.
    /// Future dates are allowed.</para>
    /// <para>Lengths are counted in UTF-16 code units (<see cref="string.Length"/>).</para>
    /// </remarks>
    public static class PersonalMemoryFieldRules
    {
        /// <summary>The longest title kept.</summary>
        public const int MaxTitleLength = 200;

        /// <summary>The longest summary kept.</summary>
        public const int MaxSummaryLength = 2000;

        /// <summary>The longest source line kept.</summary>
        public const int MaxSourceLength = 200;

        /// <summary>The longest photo caption kept.</summary>
        public const int MaxCaptionLength = 500;

        /// <summary>The title's problem, or null.</summary>
        public static string? TitleError(string? title)
        {
            var trimmed = title?.Trim() ?? string.Empty;

            if (trimmed.Length == 0)
            {
                return "Give the memory a title.";
            }

            return trimmed.Length > MaxTitleLength
                ? $"Keep the title to {MaxTitleLength.ToString(CultureInfo.CurrentCulture)} characters."
                : null;
        }

        /// <summary>The source line's problem, or null.</summary>
        public static string? SourceError(string? source)
        {
            var trimmed = source?.Trim() ?? string.Empty;

            if (trimmed.Length == 0)
            {
                return "Say where this memory comes from.";
            }

            return trimmed.Length > MaxSourceLength
                ? $"Keep the source to {MaxSourceLength.ToString(CultureInfo.CurrentCulture)} characters."
                : null;
        }

        /// <summary>The summary's problem, or null. No summary is fine.</summary>
        public static string? SummaryError(string? summary) =>
            (summary?.Trim().Length ?? 0) > MaxSummaryLength
                ? $"Keep the description to {MaxSummaryLength.ToString(CultureInfo.CurrentCulture)} characters."
                : null;

        /// <summary>A photo caption's problem, or null. No caption is fine.</summary>
        public static string? CaptionError(string? caption) =>
            (caption?.Trim().Length ?? 0) > MaxCaptionLength
                ? $"Keep each caption to {MaxCaptionLength.ToString(CultureInfo.CurrentCulture)} characters."
                : null;

        /// <summary>A text as stored: trimmed, and null when empty.</summary>
        public static string? NormaliseText(string? text) => text?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

        /// <summary>A day as stored at a precision: the day itself, or the first of its month.</summary>
        public static DateOnly Normalise(DateOnly day, MemoryDatePrecision precision) =>
            precision == MemoryDatePrecision.Month ? new DateOnly(day.Year, day.Month, 1) : day;

        /// <summary>The end's problem, or null: an end before the start (compared at the precision). No end is fine.</summary>
        public static string? EndError(DateOnly start, DateOnly? end, MemoryDatePrecision precision) =>
            end is { } last && Normalise(last, precision) < Normalise(start, precision)
                ? "The end cannot be before the start."
                : null;

        /// <summary>
        /// Why a memory cannot be shown every year (CH-53), or <see langword="null"/> when it can or the switch is
        /// off: Every year needs a memory dated to the day without an end date.
        /// </summary>
        public static string? EveryYearError(bool everyYear, MemoryDatePrecision precision, DateOnly? end)
        {
            if (!everyYear)
            {
                return null;
            }

            if (precision != MemoryDatePrecision.Day)
            {
                return "Every year needs a memory dated to the day, not a month.";
            }

            return end is null ? null : "Every year is only for a memory without an end date.";
        }
    }
}
