using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Mobile.Memories
{
    /// <summary>
    /// The words the phone shows for memories (CH-42), in one place.
    /// </summary>
    /// <remarks>
    /// <para><strong>Observed times only.</strong> A card names the first and last trophy times it holds;
    /// never "played together", a session, or a duration.</para>
    /// <para><strong>Two labels, named differently.</strong> A period's label (derived at import) is shown
    /// as "This period: …"; the source record's own day level as "Day evidence: …". Neither is combined
    /// with the other, re-ranked or scored.</para>
    /// </remarks>
    public static class MemoryWording
    {
        /// <summary>The card's period line, such as "This period: same period".</summary>
        public static string PeriodLabelLine(string label) => "This period: " + PeriodLabel(label);

        /// <summary>A period label in words: strong, supporting, same period, one account.</summary>
        public static string PeriodLabel(string label) => label switch
        {
            "same-period" => "same period",
            ArchiveMemoryVocabulary.OneAccount => "one account",
            _ => label,
        };

        /// <summary>The details' day line, such as "Day evidence: same-day": the source's own level, copied.</summary>
        public static string DayEvidenceLine(string dayLevel) => "Day evidence: " + dayLevel;

        /// <summary>The muted line of a record with no shared period.</summary>
        public static string DifferentTimesLine(string title) =>
            "Both earned trophies in " + title + " today, at different times";

        /// <summary>"1 trophy" or "3 trophies".</summary>
        public static string Trophies(int count) =>
            count == 1 ? "1 trophy" : count.ToString(CultureInfo.CurrentCulture) + " trophies";

        /// <summary>
        /// Observed times as a card shows them: "at 20:18" for one minute, otherwise "20:18 to 22:15".
        /// </summary>
        public static string TimeSpan(DateTime first, DateTime last)
        {
            var from = MessageRow.TimeTextOf(first);
            var to = MessageRow.TimeTextOf(last);

            return from == to ? "at " + from : from + " to " + to;
        }

        /// <summary>
        /// A period's observed times as the Memories list shows them (CH-56): as <see cref="TimeSpan"/> within
        /// one day; "23:40 to 00:35 next day" when the last time is on the next day; "23:40 to 3 March, 00:35"
        /// when it is later (with the year when the year differs). The dates are compared first, so a period
        /// from 20:18 on one day to 20:18 on a later one never reads "at 20:18". Never a duration.
        /// </summary>
        public static string SpanText(DateTime first, DateTime last)
        {
            var fromDay = DateOnly.FromDateTime(first);
            var toDay = DateOnly.FromDateTime(last);

            if (fromDay == toDay)
            {
                return TimeSpan(first, last);
            }

            var from = MessageRow.TimeTextOf(first);
            var to = MessageRow.TimeTextOf(last);

            if (toDay == fromDay.AddDays(1))
            {
                return from + " to " + to + " next day";
            }

            var day = toDay.Year == fromDay.Year
                ? toDay.ToString("d MMMM", CultureInfo.CurrentCulture)
                : DateSeparatorRow.DayText(toDay);

            return from + " to " + day + ", " + to;
        }

        /// <summary>One participant's line on a card: "Alex: 3 trophies, 20:18 to 22:15".</summary>
        public static string ParticipantLine(string name, int count, DateTime first, DateTime last) =>
            name + ": " + Trophies(count) + ", " + TimeSpan(first, last);

        /// <summary>
        /// The name of an item without a definition, such as "Bronze trophy #12" (the type with a capital,
        /// "trophy", and the item's number in its set).
        /// </summary>
        public static string FallbackItemName(string itemType, string itemId)
        {
            var type = itemType.Length == 0
                ? "Trophy"
                : char.ToUpper(itemType[0], CultureInfo.InvariantCulture) + itemType[1..];

            return type + " trophy #" + itemId;
        }

        /// <summary>The details' heading over the trophies.</summary>
        public const string ItemsHeading = "Trophies";

        /// <summary>The details' heading over the day evidence.</summary>
        public const string DayHeading = "This day";

        /// <summary>The details' heading over a personal memory's links (to messages and memories).</summary>
        public const string LinksHeading = "Linked";

        /// <summary>The card's badge for a memory's extra photos after the cover: "+4" (CH-46).</summary>
        public static string ExtraPhotosBadge(int count) => "+" + count.ToString(CultureInfo.CurrentCulture);

        /// <summary>The full-screen viewer's title for one of a memory's photos: "Photo 2 of 5", or "Photo".</summary>
        public static string PhotoTitle(int index, int count) =>
            count > 1
                ? "Photo " + (index + 1).ToString(CultureInfo.CurrentCulture) + " of " + count.ToString(CultureInfo.CurrentCulture)
                : "Photo";

        /// <summary>
        /// The line over the day's periods: "Periods this day:" for a "different times" line (no period
        /// chosen), otherwise "Other periods this day:".
        /// </summary>
        public static string OtherPeriodsHeading(bool forPeriod) =>
            forPeriod ? "Other periods this day:" : "Periods this day:";

        /// <summary>A trophy's second line in the details: "20:18:03 · Alex", with " · hidden trophy" when the source marks it hidden.</summary>
        public static string ItemMeta(MemoryItemLine item)
        {
            ArgumentNullException.ThrowIfNull(item);

            return item.TimeText + " · " + item.ParticipantName + (item.IsHidden ? " · hidden trophy" : string.Empty);
        }

        /// <summary>
        /// A game memory's date line (CH-67): the day and the play window, first to last shown trophy, such as
        /// "Sat 8 Nov 2026, 19:40-22:15"; "Sat 8 Nov 2026, 19:40" when both are the same minute; "23:40-00:35 next day"
        /// when the last is on the next day; "23:40 to 11 Nov, 00:35" when later. Observed times, never a duration.
        /// </summary>
        public static string GameDateLine(DateTime first, DateTime last)
        {
            var from = MessageRow.TimeTextOf(first);
            var to = MessageRow.TimeTextOf(last);
            var fromDay = DateOnly.FromDateTime(first);
            var toDay = DateOnly.FromDateTime(last);

            var window = fromDay == toDay
                ? from == to ? from : from + "-" + to
                : toDay == fromDay.AddDays(1)
                    ? from + "-" + to + " next day"
                    : from + " to " + last.ToString(toDay.Year == fromDay.Year ? "d MMM" : "d MMM yyyy", CultureInfo.CurrentCulture) + ", " + to;

            return GameDay(first) + ", " + window;
        }

        /// <summary>A game memory's day without times (no trophy shown): "Sat 8 Nov 2026".</summary>
        public static string GameDay(DateTime local) => local.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture);

        /// <summary>
        /// A game memory's day evidence as a colour block (CH-67): 🟩 strong, 🟨 supporting, 🟧 same day. Any other
        /// level, or none, has no block: nothing is guessed.
        /// </summary>
        public static string? DayEvidenceBlock(string? dayLevel) => dayLevel switch
        {
            "strong" => "\U0001F7E9",
            "supporting" => "\U0001F7E8",
            "same-day" => "\U0001F7E7",
            _ => null,
        };

        /// <summary>
        /// The one line a tap on the evidence block shows (CH-67), from the import's rules: the same trophy (same
        /// trophy set) earned by both that day within a minute (strong), or not within a minute (supporting), or
        /// trophies by both that day with no identical trophy matched (same day; also every game whose two trophy sets differ, since sets are never matched across). <see langword="null"/> when there is no block.
        /// </summary>
        public static string? DayEvidenceMeaning(string? dayLevel) => dayLevel switch
        {
            "strong" => "Strong: you both earned the same trophy within a minute of each other this day.",
            "supporting" => "Supporting: you both earned the same trophy this day, but not within a minute.",
            "same-day" => "Same day: you both earned trophies in this game this day, with no identical trophy matched.",
            _ => null,
        };

        /// <summary>What a screen reader hears for the evidence block: "Day evidence: strong. Tap for what it means."</summary>
        public static string? DayEvidenceBlockDescription(string? dayLevel) =>
            DayEvidenceBlock(dayLevel) is null
                ? null
                : "Day evidence: " + (dayLevel == "same-day" ? "same day" : dayLevel) + ". Tap for what it means.";

        /// <summary>
        /// A game trophy row's second line (CH-67): as <see cref="ItemMeta"/> for one trophy; both times and names for
        /// a together trophy, earlier first: "19:41:05 Alex · 19:41:12 Sam", with " · hidden trophy" once when hidden.
        /// Never a trophy type.
        /// </summary>
        public static string GameItemMeta(GameTrophyLine line)
        {
            ArgumentNullException.ThrowIfNull(line);

            if (line.Second is not { } second)
            {
                return ItemMeta(line.First);
            }

            var first = line.First;

            return first.TimeText + " " + first.ParticipantName + " · " + second.TimeText + " " + second.ParticipantName
                   + (first.IsHidden ? " · hidden trophy" : string.Empty);
        }

        /// <summary>The details' source line, "Source: PSN trophies".</summary>
        public static string SourceLine(string source) => "Source: " + source;

        /// <summary>An exact time as the details show it, to the second.</summary>
        public static string ExactTime(DateTime local) => local.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

        /// <summary>
        /// An entry's date as its card shows it, by its precision: "2 January 2018" (Day), "January 2018"
        /// (Month), "2 January 2018, 14:05" (Second); a range as "… to …".
        /// </summary>
        public static string EntryDate(string precision, DateTime start, DateTime? end)
        {
            var from = DateOf(precision, start);

            return end is { } last && DateOf(precision, last) is var to && to != from ? from + " to " + to : from;
        }

        /// <summary>The mark on the phone owner's own memories in the Saved screen's Memories tab (CH-54).</summary>
        public const string MineMark = "Mine";

        /// <summary>
        /// What a screen reader hears for a row of a Saved memory tab: its title, "mine" for one of the phone owner's own
        /// (CH-54), then its lines, comma separated.
        /// </summary>
        public static string ListRowScreenReaderText(MemoryListItem item)
        {
            ArgumentNullException.ThrowIfNull(item);

            return string.Join(
                ", ",
                new[] { item.Title, item.IsOwn ? "mine" : null, item.Line, item.MutedLine }.Where(part => !string.IsNullOrEmpty(part)));
        }

        private static string DateOf(string precision, DateTime local) => precision switch
        {
            "Month" => local.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
            "Second" => DateSeparatorRow.DayText(DateOnly.FromDateTime(local)) + ", " + MessageRow.TimeTextOf(local),
            _ => DateSeparatorRow.DayText(DateOnly.FromDateTime(local)),
        };
    }
}
