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

        /// <summary>The details' heading over a personal memory's links.</summary>
        public const string LinksHeading = "Linked";

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

        private static string DateOf(string precision, DateTime local) => precision switch
        {
            "Month" => local.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
            "Second" => DateSeparatorRow.DayText(DateOnly.FromDateTime(local)) + ", " + MessageRow.TimeTextOf(local),
            _ => DateSeparatorRow.DayText(DateOnly.FromDateTime(local)),
        };
    }
}
