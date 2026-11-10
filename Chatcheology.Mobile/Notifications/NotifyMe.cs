using System.Globalization;
using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Mobile.Notifications
{
    /// <summary>
    /// The one "Notify me" switch of Settings (CH-78): it stands for the existing stored notify settings, never a new key.
    /// Those are the archive's noted-date mutes (<c>mutedNotedDates</c>), its yearly-memory mutes
    /// (<c>mutedYearlyMemories</c>) and, with the own memories store, the per-phone My memories reminders switch.
    /// </summary>
    /// <remarks>
    /// Shown on when anything it stands for notifies (so values that differ show on; the separate switches under More
    /// settings keep showing the real values), and on when it stands for nothing. Turning it on sets every one on;
    /// turning it off sets every one off. The On this day switch (card and notifications) is not part of it.
    /// </remarks>
    /// <param name="NotedDates">The archive's noted dates as Settings lists them; only gifted ones count.</param>
    /// <param name="Yearly">The archive's yearly memories as Settings lists them.</param>
    /// <param name="RemindersOn">The My memories reminders switch, or <see langword="null"/> without the own store (or before it was read).</param>
    public sealed record NotifyMeState(IReadOnlyList<NotedDateListing> NotedDates, IReadOnlyList<YearlyMemoryListing> Yearly, bool? RemindersOn)
    {
        private IEnumerable<bool> Values =>
            NotedDates.Where(date => date.IsGifted).Select(date => date.Notify)
                .Concat(Yearly.Select(memory => memory.Notify))
                .Concat(RemindersOn is { } reminders ? [reminders] : []);

        /// <summary>Whether the switch stands for anything at all.</summary>
        public bool HasAny => Values.Any();

        /// <summary>What the switch shows: on when anything notifies, or when it stands for nothing.</summary>
        public bool IsOn => !HasAny || Values.Any(value => value);

        /// <summary>Whether the values differ (some on, some off): the switch shows on, More settings shows each.</summary>
        public bool Differs => Values.Distinct().Count() > 1;

        /// <summary>What turning the switch to <paramref name="on"/> changes: only the values that are not already <paramref name="on"/>.</summary>
        public NotifyMeChanges ChangesFor(bool on) => new(
            on,
            NotedDates.Where(date => date.IsGifted && date.Notify != on)
                .Select(date => long.Parse(date.Id, CultureInfo.InvariantCulture))
                .ToList(),
            Yearly.Where(memory => memory.Notify != on).Select(memory => memory.EntryID).ToList(),
            RemindersOn is { } reminders && reminders != on ? on : null);
    }

    /// <summary>What one turn of the Notify me switch writes (CH-78).</summary>
    /// <param name="On">The new value.</param>
    /// <param name="NotedDateIDs">Gifted noted dates whose mute changes (muted when off, unmuted when on).</param>
    /// <param name="YearlyEntryIDs">Yearly memories whose mute changes.</param>
    /// <param name="Reminders">The new My memories reminders value, or <see langword="null"/> when it stays as it is.</param>
    public sealed record NotifyMeChanges(bool On, IReadOnlyList<long> NotedDateIDs, IReadOnlyList<long> YearlyEntryIDs, bool? Reminders)
    {
        /// <summary>Whether anything changes.</summary>
        public bool IsEmpty => NotedDateIDs.Count == 0 && YearlyEntryIDs.Count == 0 && Reminders is null;
    }
}
