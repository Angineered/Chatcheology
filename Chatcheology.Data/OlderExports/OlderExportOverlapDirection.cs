using Chatcheology.Core.Models;

namespace Chatcheology.Data.OlderExports
{
    /// <summary>Why a line of one export has no exact counterpart in the other.</summary>
    /// <remarks>Each unmatched line is put in the first category that applies, in this order.</remarks>
    public enum OverlapCategory
    {
        /// <summary>
        /// At the same timestamp and sender, one side has a <c>(file attached)</c> line and the
        /// other a <c>&lt;Media omitted&gt;</c> line.
        /// </summary>
        FileAttachedVersusMediaOmitted,

        /// <summary>The other export has the identical line, but fewer times.</summary>
        RepeatedMoreOften,

        /// <summary>
        /// The other export has a line at the same timestamp and sender whose content differs only
        /// in leading or trailing whitespace (per line or overall).
        /// </summary>
        WhitespaceOnly,

        /// <summary>
        /// The other export has lines at the same timestamp and sender, but their content differs
        /// in some other way.
        /// </summary>
        OtherContent,

        /// <summary>The other export has no line at all with the same timestamp and sender.</summary>
        NoLineAtTimestampAndSender,
    }

    /// <summary>What kind of line an unmatched line is.</summary>
    public enum OverlapLineKind
    {
        /// <summary>A user message that is neither of the two media kinds.</summary>
        UserText,

        /// <summary>A user message whose first line names an attached file.</summary>
        UserFileAttached,

        /// <summary>A user message that is exactly <c>&lt;Media omitted&gt;</c>.</summary>
        UserMediaOmitted,

        /// <summary>A system message.</summary>
        System,
    }

    /// <summary>One unmatched line: where it starts in its source file, and how it was sorted.</summary>
    /// <param name="SourceLineStart">The 1-based physical line the message starts on.</param>
    /// <param name="Category">Why it has no counterpart.</param>
    /// <param name="Kind">What kind of line it is.</param>
    public readonly record struct OverlapUnmatchedLine(
        int SourceLineStart,
        OverlapCategory Category,
        OverlapLineKind Kind);

    /// <summary>
    /// One direction of the overlap comparison: the lines of one export that have no exact
    /// counterpart in the other, sorted by category and kind, with clock-shift hints for the lines
    /// that have no counterpart at the same timestamp and sender.
    /// </summary>
    /// <remarks>
    /// Counts and source line numbers only. No text, name or timestamp is carried.
    /// </remarks>
    public sealed class OlderExportOverlapDirection
    {
        /// <summary>How many lines of this export were compared.</summary>
        public required int ComparedCount { get; init; }

        /// <summary>The unmatched lines in source order.</summary>
        public required IReadOnlyList<OverlapUnmatchedLine> Unmatched { get; init; }

        /// <summary>
        /// Of the <see cref="OverlapCategory.NoLineAtTimestampAndSender"/> lines, how many have a
        /// line by the same sender in the other export within one minute either way.
        /// </summary>
        public required int SameSenderWithinOneMinuteCount { get; init; }

        /// <summary>
        /// Of the <see cref="OverlapCategory.NoLineAtTimestampAndSender"/> lines, how many have an
        /// identical line (same sender, type and content) in the other export within 60 minutes
        /// either way.
        /// </summary>
        public required int IdenticalWithinSixtyMinutesCount { get; init; }

        /// <summary>
        /// As <see cref="IdenticalWithinSixtyMinutesCount"/>, within 120 minutes. Cumulative: it
        /// includes the lines counted within 60 minutes.
        /// </summary>
        public required int IdenticalWithinOneHundredTwentyMinutesCount { get; init; }

        /// <summary>How many lines have no exact counterpart.</summary>
        public int NotFoundCount => Unmatched.Count;

        /// <summary>How many unmatched lines are in <paramref name="category"/>.</summary>
        public int Count(OverlapCategory category) =>
            Unmatched.Count(line => line.Category == category);

        /// <summary>How many unmatched lines are in <paramref name="category"/> and of <paramref name="kind"/>.</summary>
        public int Count(OverlapCategory category, OverlapLineKind kind) =>
            Unmatched.Count(line => line.Category == category && line.Kind == kind);

        /// <summary>The kind of <paramref name="message"/>.</summary>
        public static OverlapLineKind KindOf(ParsedMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            if (message.MessageType == MessageType.System)
            {
                return OverlapLineKind.System;
            }

            if (message.IsMediaPlaceholder)
            {
                return OverlapLineKind.UserMediaOmitted;
            }

            return message.AttachedFileName is null
                ? OverlapLineKind.UserText
                : OverlapLineKind.UserFileAttached;
        }
    }
}
