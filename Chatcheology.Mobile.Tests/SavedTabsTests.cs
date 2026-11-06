using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Memories;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// The Saved screen's tabs (CH-59): which tabs exist, when the bookmark is shown, the last chosen tab per open
    /// archive, each tab's kept state, and Starred read again after an unstar. Invented titles and messages only.
    /// </summary>
    public sealed class SavedTabsTests : IDisposable
    {
        private readonly TemporaryFolder _root = new();
        private readonly MemorySettings _store = new();
        private readonly ArchiveSettings _settings;

        public SavedTabsTests() => _settings = new ArchiveSettings(_store);

        public void Dispose() => _root.Dispose();

        private static ArchiveCollection Collection(long id, string kind, string name) => new()
        {
            CollectionID = id,
            Kind = kind,
            Name = name,
            SourceDescription = null,
            ShownByDefault = false,
            PeriodGapMinutes = kind == ArchiveMemoryVocabulary.KindImported ? 60 : null,
        };

        private static List<(string Id, SavedTabKind Kind, string Name)> Describe(IEnumerable<SavedTab> tabs) =>
            tabs.Select(tab => (tab.Id, tab.Kind, tab.Name)).ToList();

        private InstalledArchive Install(Action<string> write)
        {
            var library = new ArchiveLibrary(Path.Combine(_root.Path, "library-" + Guid.NewGuid().ToString("N")));
            var staging = library.CreateStagingFolder();
            write(staging);

            var outcome = library.Install(staging);
            Assert.True(outcome.Installed, string.Join(", ", outcome.Validation.FirstProblems));

            return library.ListArchives(_settings).Single();
        }

        /// <summary>Messages 1-3; 2 carries a photo (attachment 20).</summary>
        private InstalledArchive MessagesOnly(string title = "Alex and Sam")
        {
            var builder = new ArchiveBuilder(title);
            builder.Add("First message");
            builder.Media(new AttachmentSpec(20));
            builder.Add("Third, from me", ArchiveBuilder.Local);

            return Install(folder => builder.Write(folder));
        }

        /// <summary>No messages; a shared game with a both-account period and a personal memory.</summary>
        private InstalledArchive MemoriesOnly()
        {
            var builder = new MemoryArchiveBuilder();
            builder.Game(
                10, "Invented Racer", "supporting", false,
                new PeriodSpec("strong", new TrophySpec(MemoryArchiveBuilder.Local, new DateTime(2025, 3, 10, 20, 10, 0), "1"), new TrophySpec(MemoryArchiveBuilder.Other, new DateTime(2025, 3, 10, 20, 10, 30), "2")));
            builder.Memory(20, "Beach week", new DateTime(2025, 3, 20), new DateTime(2025, 3, 25), image: false);

            return Install(folder => builder.Write(folder));
        }

        // ---------------------------------------------------------------------------------------
        // Which tabs exist.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void With_messages_Starred_comes_first_then_Memories_then_each_imported_collection_by_id()
        {
            var tabs = SavedTabs.TabsFor(
                hasMessages: true,
                [
                    Collection(4, ArchiveMemoryVocabulary.KindImported, "Invented other source"),
                    Collection(2, ArchiveMemoryVocabulary.KindPersonal, "Holidays"),
                    Collection(1, ArchiveMemoryVocabulary.KindImported, "Shared gaming"),
                    Collection(3, ArchiveMemoryVocabulary.KindPersonal, "Moved noted dates"),
                ]);

            Assert.Equal(
                [
                    ("starred", SavedTabKind.Starred, "Starred"),
                    ("personal", SavedTabKind.Memories, "Memories"),
                    ("collection:1", SavedTabKind.Collection, "Shared gaming"),
                    ("collection:4", SavedTabKind.Collection, "Invented other source"),
                ],
                Describe(tabs));
        }

        [Fact]
        public void Without_messages_there_is_no_Starred_tab()
        {
            var tabs = SavedTabs.TabsFor(hasMessages: false, [Collection(1, ArchiveMemoryVocabulary.KindImported, "Shared gaming")]);

            Assert.Equal([("personal", SavedTabKind.Memories, "Memories"), ("collection:1", SavedTabKind.Collection, "Shared gaming")], Describe(tabs));
        }

        [Fact]
        public void Without_memories_Starred_and_an_empty_Memories_tab_remain()
        {
            var tabs = SavedTabs.TabsFor(hasMessages: true, []);

            Assert.Equal([("starred", SavedTabKind.Starred, "Starred"), ("personal", SavedTabKind.Memories, "Memories")], Describe(tabs));
        }

        [Fact]
        public async Task An_archive_with_no_stars_still_has_Starred_with_its_empty_line()
        {
            using var session = ArchiveSession.Open(MessagesOnly(), _settings);

            var tabs = session.CreateSavedTabs();

            Assert.True(session.HasSaved);
            Assert.Equal(["starred", "personal"], tabs.Tabs.Select(tab => tab.Id));
            Assert.Equal("starred", tabs.Selected.Id);
            Assert.Empty(await session.ReadStarredAsync());
            Assert.Equal("Star a message or photo to keep it here.", SavedTabs.NoStarsText);

            var memories = await session.GetMemoryListAsync();
            Assert.Equal(
                "No memories yet. Memories you add will show here.",
                memories.EmptyText(MemoryList.PersonalGroup, memories.Rows(MemoryList.PersonalGroup, null)));
        }

        [Fact]
        public async Task Empty_and_filled_collections_both_get_a_tab()
        {
            var builder = new MemoryArchiveBuilder();
            builder.Message(new DateTime(2025, 3, 10, 9, 0, 0));

            // Only a one-account record: the collection lists nothing, and its tab is still there.
            builder.Game(
                14, "Invented Solo", "same-day", false,
                new PeriodSpec("one-account", new TrophySpec(MemoryArchiveBuilder.Local, new DateTime(2025, 3, 12, 9, 4, 0), "12")));

            using var session = ArchiveSession.Open(Install(folder => builder.Write(folder)), _settings);
            var tabs = session.CreateSavedTabs();
            var list = await session.GetMemoryListAsync();
            var gaming = MemoryList.CollectionGroup(MemoryArchiveBuilder.Gaming);

            Assert.Equal(["starred", "personal", gaming], tabs.Tabs.Select(tab => tab.Id));
            Assert.Equal("Shared gaming", tabs.Tabs[2].Name);
            Assert.Equal("Nothing shared to list in this collection.", list.EmptyText(gaming, list.Rows(gaming, null)));

            using var filled = ArchiveSession.Open(MemoriesOnly(), _settings);
            var filledList = await filled.GetMemoryListAsync();

            Assert.Contains(filled.CreateSavedTabs().Tabs, tab => tab.Id == gaming);
            Assert.Single(filledList.Rows(gaming, null).OfType<MemoryListItem>());
            Assert.Equal(string.Empty, filledList.EmptyText(gaming, filledList.Rows(gaming, null)));
        }

        // ---------------------------------------------------------------------------------------
        // The bookmark.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void The_bookmark_shows_for_messages_or_memories()
        {
            Assert.True(SavedTabs.IsShown(hasMessages: true, hasMemories: false));
            Assert.True(SavedTabs.IsShown(hasMessages: false, hasMemories: true));
            Assert.True(SavedTabs.IsShown(hasMessages: true, hasMemories: true));
            Assert.False(SavedTabs.IsShown(hasMessages: false, hasMemories: false));
        }

        [Fact]
        public void A_message_less_archive_with_memories_shows_the_bookmark_and_opens_on_Memories()
        {
            using var session = ArchiveSession.Open(MemoriesOnly(), _settings);

            Assert.True(session.IsEmpty);
            Assert.True(session.HasSaved);

            var tabs = session.CreateSavedTabs();

            Assert.Equal(["personal", "collection:1"], tabs.Tabs.Select(tab => tab.Id));
            Assert.Equal(SavedTabKind.Memories, tabs.Selected.Kind);
        }

        // ---------------------------------------------------------------------------------------
        // Last chosen tab, per open archive.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void The_screen_reopens_on_the_tab_last_chosen_for_that_archive_only()
        {
            var archive = MessagesOnly("Alex and Sam");
            var first = ArchiveSession.Open(archive, _settings);
            using var second = ArchiveSession.Open(MessagesOnly("Sam and Robin"), _settings);

            var tabs = first.CreateSavedTabs();
            Assert.Equal("starred", tabs.Selected.Id);

            Assert.True(tabs.Select("personal"));
            Assert.False(tabs.Select("personal"));
            Assert.False(tabs.Select("collection:99"));
            Assert.Equal("personal", tabs.Selected.Id);

            // Reopened while the archive is open: the last tab; another archive starts on its first tab.
            Assert.Equal("personal", first.CreateSavedTabs().Selected.Id);
            Assert.Equal("starred", second.CreateSavedTabs().Selected.Id);

            // Closed and opened again: the first tab.
            first.Dispose();
            using var again = ArchiveSession.Open(archive, _settings);
            Assert.Equal("starred", again.CreateSavedTabs().Selected.Id);
        }

        [Fact]
        public void A_remembered_tab_that_is_not_there_falls_back_to_the_first()
        {
            var tabs = new SavedTabs(SavedTabs.TabsFor(hasMessages: false, []), lastUsed: "starred");

            Assert.Equal("personal", tabs.Selected.Id);
        }

        // ---------------------------------------------------------------------------------------
        // Per-tab state.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Each_tab_keeps_its_own_search_and_first_visible_row()
        {
            var tabs = new SavedTabs(SavedTabs.TabsFor(hasMessages: true, [Collection(1, ArchiveMemoryVocabulary.KindImported, "Shared gaming")]));
            var memories = tabs.Tabs[1];
            var gaming = tabs.Tabs[2];

            tabs.StateOf(memories).Search = "beach";
            tabs.StateOf(memories).Remember("e:20", 4);
            tabs.StateOf(gaming).Remember("e:10:2", 7);

            Assert.Same(tabs.StateOf(memories), tabs.StateOf(tabs.Tabs[1]));
            Assert.Equal("beach", tabs.StateOf(memories).Search);
            Assert.Equal(string.Empty, tabs.StateOf(gaming).Search);
            Assert.Equal("e:20", tabs.StateOf(memories).AnchorKey);
            Assert.Equal("e:10:2", tabs.StateOf(gaming).AnchorKey);
            Assert.Null(tabs.StateOf(tabs.Tabs[0]).AnchorKey);

            // Restored by key, wherever the row is now.
            Assert.Equal(1, tabs.StateOf(memories).RestoreIndex(["y:2025", "e:20", "e:21"]));
        }

        [Fact]
        public void A_state_restores_by_key_then_by_place_and_never_past_the_end()
        {
            var state = new SavedTabState();

            Assert.Equal(-1, state.RestoreIndex([]));
            Assert.Equal(0, state.RestoreIndex(["m:3"]));

            state.Remember("m:5", 3);
            Assert.Equal(0, state.RestoreIndex(["m:5", "m:4"]));
            Assert.Equal(3, state.RestoreIndex(["m:9", "m:8", "m:7", "m:6", "m:4"]));
            Assert.Equal(1, state.RestoreIndex(["m:9", "m:8"]));
        }

        [Fact]
        public void Row_keys_tell_messages_headings_entries_and_periods_apart()
        {
            // Starred keys (m: and p:) are checked on read entries in the unstar test below.
            Assert.Equal("m:4", SavedTabs.KeyOf(new StarredEntry(4, "d", "t", "s", "text", null, IsPhoto: false)));
            Assert.Equal("y:2021", SavedTabs.KeyOf(new MemoryListYearRow(2021)));
            Assert.Equal("e:7", SavedTabs.KeyOf(new MemoryListItem(new MemoryKey(7, null), "x", "d", "x", null, false, default)));
            Assert.Equal("e:7:2", SavedTabs.KeyOf(new MemoryListItem(new MemoryKey(7, 2), "x", "d", "x", null, false, default)));
        }

        [Fact]
        public void Tab_names_for_screen_readers_and_the_log()
        {
            var tabs = SavedTabs.TabsFor(hasMessages: true, [Collection(1, ArchiveMemoryVocabulary.KindImported, "Shared gaming")]);

            Assert.Equal("Starred tab, selected", tabs[0].ScreenReaderName(selected: true));
            Assert.Equal("Shared gaming tab", tabs[2].ScreenReaderName(selected: false));

            // The log never names a collection.
            Assert.Equal(["starred", "memories", "collection"], tabs.Select(tab => tab.LogName));
            Assert.Equal([false, true, true], tabs.Select(tab => tab.HasSearch));
        }

        // ---------------------------------------------------------------------------------------
        // Starred read again after an unstar.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public async Task Starred_read_again_after_an_unstar_no_longer_lists_it_and_keeps_the_place()
        {
            using var session = ArchiveSession.Open(MessagesOnly(), _settings);
            session.Stars.SetMessage(1, true);
            session.Stars.SetAttachment(2, 20, true);
            session.Stars.SetMessage(3, true);

            var tabs = session.CreateSavedTabs();
            var state = tabs.StateOf(tabs.Selected);
            var before = await session.ReadStarredAsync();

            Assert.Equal(["m:3", "p:2:20", "m:1"], before.Select(SavedTabs.KeyOf));

            // The photo is the first visible row; it is unstarred in the viewer.
            state.Remember(SavedTabs.KeyOf(before[1]), 1);
            session.Stars.SetAttachment(2, 20, false);

            var after = await session.ReadStarredAsync();
            var keys = after.Select(SavedTabs.KeyOf).ToList();

            Assert.Equal(["m:3", "m:1"], keys);
            Assert.Equal(1, state.RestoreIndex(keys));
        }
    }
}
