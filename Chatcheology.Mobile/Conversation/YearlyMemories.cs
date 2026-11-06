using System.Globalization;
using Chatcheology.Archive;
using Chatcheology.Mobile.Memories;

namespace Chatcheology.Mobile.Conversation
{
    /// <summary>
    /// One yearly memory (CH-53) of a version 3 archive that keeps the rule: a personal memory dated to the day,
    /// without an end, with Every year on.
    /// </summary>
    /// <param name="EntryID">The memory's entry.</param>
    /// <param name="Title">The title.</param>
    /// <param name="Start">Its own date, year included.</param>
    public sealed record YearlyMemory(long EntryID, string Title, DateOnly Start);

    /// <summary>One yearly memory as Settings lists it, with its mute switch.</summary>
    /// <param name="EntryID">The memory's entry.</param>
    /// <param name="Title">The title.</param>
    /// <param name="Start">Its own date.</param>
    /// <param name="Notify">Whether it notifies: it does unless muted on this phone.</param>
    public sealed record YearlyMemoryListing(long EntryID, string Title, DateOnly Start, bool Notify)
    {
        /// <summary>"14 February 2019".</summary>
        public string DateText => NotedDates.DateText(Start.Month, Start.Day, Start.Year);
    }

    /// <summary>A yearly memory that falls on today, for the On this day card.</summary>
    /// <param name="EntryID">The memory's entry: a tap opens its details.</param>
    /// <param name="Title">The title.</param>
    /// <param name="Years">Whole years since its own date; none in its own year.</param>
    /// <param name="ShowInChatDay">Its own date when that day has messages (details then offer Show in chat); otherwise none.</param>
    public sealed record YearlyToday(long EntryID, string Title, int? Years, DateOnly? ShowInChatDay)
    {
        /// <summary>The cover thumbnail: the memory's first photo whose file is there, as its memory card draws; or none.</summary>
        public string? ImagePath { get; init; }

        /// <summary>Its external gallery links (CH-48), in order.</summary>
        public IReadOnlyList<GalleryLink> Galleries { get; init; } = [];

        /// <summary>"Link · photos.google.com", "3 links", or empty.</summary>
        public string GalleryText => GalleryLinks.Summary(Galleries);

        /// <summary>Whether there is a thumbnail to draw.</summary>
        public bool HasImage => ImagePath is not null;

        /// <summary>The row's text: "Our first date · 7 years", or the title alone in its own year.</summary>
        public string Text => Years is { } years
            ? $"{Title} · {years.ToString(CultureInfo.CurrentCulture)} {(years == 1 ? "year" : "years")}"
            : Title;
    }

    /// <summary>
    /// Yearly memories on the phone (CH-53): which entries count, on which days they show and notify, and the
    /// card's rows.
    /// </summary>
    /// <remarks>
    /// <para><strong>Rule.</strong> Only an entry of a personal collection, dated to the day, without an end. The
    /// validator refuses an archive with any other, but the phone filters again: a row that breaks the rule is
    /// never shown and never notifies.</para>
    /// <para><strong>Days.</strong> On its month and day, 29 February on 28 February in a year without one
    /// (CH-39, <see cref="NotedDates.Matches"/>), and only on or after its own date: a memory dated in the future
    /// shows nothing until then. In its own year it shows without a year count.</para>
    /// <para>Shown whatever the memory layer switches say; only the On this day switch turns them off.</para>
    /// </remarks>
    public static class YearlyMemories
    {
        /// <summary>The only date precision a yearly memory has.</summary>
        private const string Day = "Day";

        /// <summary>Whether a stored yearly row keeps the rule: personal, dated to the day, without an end.</summary>
        public static bool KeepsRule(string? collectionKind, string datePrecision, DateTime? endLocal) =>
            collectionKind == ArchiveMemoryVocabulary.KindPersonal
            && datePrecision == Day
            && endLocal is null;

        /// <summary>The yearly memories of an archive's rows, by identifier; a row that breaks the rule is left out.</summary>
        public static IReadOnlyList<YearlyMemory> From(IEnumerable<ArchiveYearlyEntry> entries)
        {
            ArgumentNullException.ThrowIfNull(entries);

            return entries
                .Where(entry => KeepsRule(entry.CollectionKind, entry.DatePrecision, entry.EndLocal))
                .Select(entry => new YearlyMemory(entry.EntryID, entry.Title, DateOnly.FromDateTime(entry.StartLocal)))
                .OrderBy(memory => memory.EntryID)
                .ToList();
        }

        /// <summary>
        /// Whether a yearly memory dated <paramref name="start"/> shows (and notifies) on <paramref name="day"/>: on
        /// its month and day (29 February on the 28th in a year without one), and not before its own date.
        /// </summary>
        public static bool ShowsOn(DateOnly start, DateOnly day) =>
            day >= start && NotedDates.Matches(start.Month, start.Day, day);

        /// <summary>Whole years from its own date's year to <paramref name="day"/>'s, or none in its own year.</summary>
        public static int? YearsOn(DateOnly start, DateOnly day) => day.Year > start.Year ? day.Year - start.Year : null;

        /// <summary>The yearly memories with their mute switches, by title, for Settings.</summary>
        public static IReadOnlyList<YearlyMemoryListing> List(Settings.ArchiveSettings settings, string contentsSha256, IEnumerable<YearlyMemory> memories)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(memories);

            var muted = settings.GetMutedYearlyMemories(contentsSha256);

            return memories
                .Select(memory => new YearlyMemoryListing(memory.EntryID, memory.Title, memory.Start, Notify: !muted.Contains(memory.EntryID)))
                .OrderBy(memory => memory.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(memory => memory.EntryID)
                .ToList();
        }

        /// <summary>
        /// Today's yearly memories, by title. <paramref name="days"/> are the days with messages (for Show in
        /// chat); <paramref name="index"/> gives the cover thumbnail and gallery links, when there is one.
        /// </summary>
        public static IReadOnlyList<YearlyToday> For(
            IEnumerable<YearlyMemory> memories, IReadOnlySet<DateOnly> days, DateOnly today, MemoryIndex? index = null)
        {
            ArgumentNullException.ThrowIfNull(memories);
            ArgumentNullException.ThrowIfNull(days);

            return memories
                .Where(memory => ShowsOn(memory.Start, today))
                .Select(memory => new YearlyToday(
                    memory.EntryID,
                    memory.Title,
                    YearsOn(memory.Start, today),
                    days.Contains(memory.Start) ? memory.Start : null)
                {
                    ImagePath = index?.CoverOf(memory.EntryID),
                    Galleries = index?.GalleriesOf(memory.EntryID) ?? [],
                })
                .OrderBy(memory => memory.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(memory => memory.EntryID)
                .ToList();
        }
    }
}
