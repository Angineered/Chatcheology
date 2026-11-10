using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Notifications;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// The simpler Settings for the gift (CH-78): the one Notify me switch over the existing notify settings (noted-date
    /// mutes, yearly-memory mutes, My memories reminders; values that differ show on, each keeps its own value), the start
    /// screen's import placement and the More control. Invented titles and dates only.
    /// </summary>
    public sealed class GiftSettingsTests : IDisposable
    {
        private readonly TemporaryFolder _root = new();
        private readonly MemorySettings _store = new();
        private readonly ArchiveSettings _settings;

        public GiftSettingsTests() => _settings = new ArchiveSettings(_store);

        public void Dispose() => _root.Dispose();

        private static NotedDateListing Noted(long id, bool notify) =>
            new(id.ToString(System.Globalization.CultureInfo.InvariantCulture), "Invented date " + id, 3, 4, 2019, null, IsGifted: true, notify);

        private static YearlyMemoryListing Yearly(long id, bool notify) => new(id, "Invented memory " + id, new DateOnly(2020, 5, 6), notify);

        // ---------------------------------------------------------------------------------------------
        // Notify me: what the switch shows.
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public void Notify_me_shows_on_when_everything_notifies_and_off_when_nothing_does()
        {
            Assert.True(new NotifyMeState([Noted(1, true)], [Yearly(2, true)], true).IsOn);
            Assert.False(new NotifyMeState([Noted(1, true)], [Yearly(2, true)], true).Differs);

            var off = new NotifyMeState([Noted(1, false)], [Yearly(2, false)], false);
            Assert.False(off.IsOn);
            Assert.False(off.Differs);
        }

        [Fact]
        public void Values_that_differ_show_on()
        {
            // Noted dates on, yearly memories off (and the other way round): on, and the values differ.
            var notedOnly = new NotifyMeState([Noted(1, true)], [Yearly(2, false)], null);
            Assert.True(notedOnly.IsOn);
            Assert.True(notedOnly.Differs);

            var yearlyOnly = new NotifyMeState([Noted(1, false)], [Yearly(2, true)], null);
            Assert.True(yearlyOnly.IsOn);
            Assert.True(yearlyOnly.Differs);

            // One of two noted dates muted.
            Assert.True(new NotifyMeState([Noted(1, true), Noted(3, false)], [], null).IsOn);

            // Only the reminders on.
            var reminders = new NotifyMeState([Noted(1, false)], [Yearly(2, false)], true);
            Assert.True(reminders.IsOn);
            Assert.True(reminders.Differs);

            // Only the reminders off.
            Assert.True(new NotifyMeState([Noted(1, true)], [], false).IsOn);
        }

        [Fact]
        public void Notify_me_standing_for_nothing_shows_on()
        {
            var none = new NotifyMeState([], [], null);

            Assert.False(none.HasAny);
            Assert.True(none.IsOn);
            Assert.False(none.Differs);
            Assert.True(none.ChangesFor(false).IsEmpty);

            // The reminders alone are something.
            Assert.True(new NotifyMeState([], [], false).HasAny);
            Assert.False(new NotifyMeState([], [], false).IsOn);
        }

        [Fact]
        public void A_noted_date_that_is_not_gifted_is_not_part_of_it()
        {
            var own = Noted(1, false) with { IsGifted = false };
            var state = new NotifyMeState([own], [], null);

            Assert.False(state.HasAny);
            Assert.Empty(state.ChangesFor(true).NotedDateIDs);
        }

        // ---------------------------------------------------------------------------------------------
        // Notify me: what a turn writes.
        // ---------------------------------------------------------------------------------------------

        [Fact]
        public void Off_turns_every_value_off_and_changes_only_those_that_were_on()
        {
            var state = new NotifyMeState([Noted(1, true), Noted(3, false)], [Yearly(2, true), Yearly(4, false)], true);
            var changes = state.ChangesFor(false);

            Assert.False(changes.On);
            Assert.Equal([1L], changes.NotedDateIDs);
            Assert.Equal([2L], changes.YearlyEntryIDs);
            Assert.False(changes.Reminders);
        }

        [Fact]
        public void On_turns_every_value_on_and_changes_only_those_that_were_off()
        {
            var state = new NotifyMeState([Noted(1, true), Noted(3, false)], [Yearly(2, true), Yearly(4, false)], false);
            var changes = state.ChangesFor(true);

            Assert.True(changes.On);
            Assert.Equal([3L], changes.NotedDateIDs);
            Assert.Equal([4L], changes.YearlyEntryIDs);
            Assert.True(changes.Reminders);
        }

        [Fact]
        public void Reminders_already_at_the_value_or_unknown_are_left_as_they_are()
        {
            Assert.Null(new NotifyMeState([], [], true).ChangesFor(true).Reminders);
            Assert.Null(new NotifyMeState([], [], null).ChangesFor(false).Reminders);
            Assert.True(new NotifyMeState([Noted(1, true)], [Yearly(2, true)], true).ChangesFor(true).IsEmpty);
        }

        // ---------------------------------------------------------------------------------------------
        // Notify me through the session: the existing keys only, each separate switch keeps its value.
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// A version 2 archive with two gifted noted dates (only version 2 carries them), or a current one with two yearly
        /// memories (version 3 and later); the first of each on 4 March, with a message that day.
        /// </summary>
        private ArchiveSession OpenSample(bool gifted, out string folder)
        {
            var builder = new MemoryArchiveBuilder();
            builder.Message(new DateTime(2019, 3, 4, 10, 0, 0));

            if (gifted)
            {
                builder.NotedDate(5, "Invented noted day", 3, 4, 2019);
                builder.NotedDate(6, "Invented other day", 8, 9);
            }
            else
            {
                builder.Yearly(31, "Invented anniversary", new DateOnly(2019, 3, 4));
                builder.Yearly(32, "Invented first trip", new DateOnly(2019, 5, 6));
            }

            var library = new ArchiveLibrary(Path.Combine(_root.Path, "library"));
            var staging = library.CreateStagingFolder();
            builder.Write(staging);

            var outcome = library.Install(staging);
            Assert.True(outcome.Installed, string.Join(", ", outcome.Validation.FirstProblems));
            folder = outcome.InstalledFolder!;

            var installed = folder;
            return ArchiveSession.Open(library.ListArchives(_settings).Single(archive => archive.Folder == installed), _settings);
        }

        private Dictionary<string, string> KeysOtherThanMutes() =>
            _store.Values
                .Where(pair => !pair.Key.EndsWith(".mutedNotedDates", StringComparison.Ordinal)
                               && !pair.Key.EndsWith(".mutedYearlyMemories", StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        private string? Mutes(string key) =>
            _store.Values.SingleOrDefault(pair => pair.Key.EndsWith("." + key, StringComparison.Ordinal)).Value;

        /// <summary>Each separate switch's value, in id order: noted dates, then yearly memories.</summary>
        private static List<bool> Separate(ArchiveSession session) =>
            session.NotedDateListings.OrderBy(d => d.Id, StringComparer.Ordinal).Select(d => d.Notify)
                .Concat(session.YearlyMemoryListings.OrderBy(m => m.EntryID).Select(m => m.Notify))
                .ToList();

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Off_then_on_through_the_session_writes_only_the_existing_mute_keys(bool gifted)
        {
            using var session = OpenSample(gifted, out var folder);
            var before = ArchiveContents.Build(folder);
            var otherKeys = KeysOtherThanMutes();
            var onThisDay = session.OnThisDayEnabled;

            var state = session.NotifyMeState(null);
            Assert.True(state.HasAny);
            Assert.True(state.IsOn);
            Assert.False(state.Differs);

            session.ApplyNotifyMe(state.ChangesFor(false));

            Assert.Equal([false, false], Separate(session));
            Assert.False(session.NotifyMeState(null).IsOn);
            Assert.Equal(gifted ? "5,6" : null, Mutes("mutedNotedDates"));
            Assert.Equal(gifted ? null : "31,32", Mutes("mutedYearlyMemories"));

            session.ApplyNotifyMe(session.NotifyMeState(null).ChangesFor(true));

            Assert.Equal([true, true], Separate(session));
            Assert.True(session.NotifyMeState(null).IsOn);

            // None muted removes the key again; nothing else was written, On this day untouched, the archive unchanged.
            Assert.Null(Mutes("mutedNotedDates"));
            Assert.Null(Mutes("mutedYearlyMemories"));
            Assert.Equal(otherKeys, KeysOtherThanMutes());
            Assert.Equal(onThisDay, session.OnThisDayEnabled);
            Assert.Equal(before, ArchiveContents.Build(folder));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Differing_values_show_on_and_each_separate_switch_keeps_its_real_value(bool gifted)
        {
            using var session = OpenSample(gifted, out _);

            // One muted, as the separate switches under More settings do.
            if (gifted)
            {
                session.SetNotedDateMuted(6, true);
            }
            else
            {
                session.SetYearlyMemoryMuted(32, true);
            }

            var state = session.NotifyMeState(null);
            Assert.True(state.IsOn);
            Assert.True(state.Differs);
            Assert.Equal([true, false], Separate(session));

            // Both muted while the reminders are on: still on; with the reminders off, off.
            if (gifted)
            {
                session.SetNotedDateMuted(5, true);
            }
            else
            {
                session.SetYearlyMemoryMuted(31, true);
            }

            Assert.True(session.NotifyMeState(true).IsOn);
            Assert.True(session.NotifyMeState(true).Differs);
            Assert.False(session.NotifyMeState(false).IsOn);
            Assert.Equal([false, false], Separate(session));

            // On from a differing state turns every one on, the reminders included.
            var changes = session.NotifyMeState(true).ChangesFor(false);
            Assert.False(changes.Reminders);
            Assert.Empty(changes.NotedDateIDs);
            Assert.Empty(changes.YearlyEntryIDs);

            changes = session.NotifyMeState(false).ChangesFor(true);
            Assert.True(changes.Reminders);
            session.ApplyNotifyMe(changes);
            Assert.Equal([true, true], Separate(session));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Turning_notify_me_off_stops_the_archive_from_notifying_and_keeps_the_card(bool gifted)
        {
            using var session = OpenSample(gifted, out _);

            session.ApplyNotifyMe(session.NotifyMeState(null).ChangesFor(false));

            var schedule = new ArchiveNotedDates("install", "hash", session.OnThisDayEnabled, session.NotedDateListings)
            {
                Yearly = session.YearlyMemoryListings,
            };

            Assert.True(session.OnThisDayEnabled);
            Assert.False(schedule.CanNotify);

            // The card still shows them: muting only stops notifications.
            var card = session.OnThisDayFor(session.ReadDays(), new DateOnly(2026, 3, 4));
            Assert.Equal(1, gifted ? card.Noted.Count : card.Yearly.Count);
        }

        // ---------------------------------------------------------------------------------------------
        // Start screen and More control.
        // ---------------------------------------------------------------------------------------------

        private static InstalledArchive Installed(string id, bool readable = true) =>
            new(id, "folder-" + id, readable ? "hash-" + id : null, readable ? "Invented chat" : null, null, 3, null, readable);

        [Fact]
        public void Import_is_the_main_button_with_no_archive_and_under_more_with_any()
        {
            Assert.Equal(ImportPlacement.Main, StartImport.For(OpeningRule.Decide([])));
            Assert.Equal(ImportPlacement.Main, StartImport.For(null));
            Assert.Equal(ImportPlacement.UnderMore, StartImport.For(OpeningRule.Decide([Installed("a")])));
            Assert.Equal(ImportPlacement.UnderMore, StartImport.For(OpeningRule.Decide([Installed("a"), Installed("b")])));

            // An archive that cannot be opened is still installed: Remove it is shown, Import under More.
            Assert.Equal(ImportPlacement.UnderMore, StartImport.For(OpeningRule.Decide([Installed("a", readable: false)])));
        }

        [Fact]
        public void More_control_starts_closed_and_says_so_to_screen_readers()
        {
            var more = new MoreControl("More settings");

            Assert.False(more.IsExpanded);
            Assert.Equal("More settings, collapsed", more.ScreenReaderText);
            Assert.Equal("▶︎", more.Mark);

            var open = more.Toggled();
            Assert.True(open.IsExpanded);
            Assert.Equal("More settings, expanded", open.ScreenReaderText);
            Assert.Equal("▼︎", open.Mark);
            Assert.False(open.Toggled().IsExpanded);
        }
    }
}
