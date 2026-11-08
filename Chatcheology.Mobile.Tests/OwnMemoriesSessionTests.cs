using System.Globalization;
using System.Security.Cryptography;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Memories;
using Chatcheology.Mobile.Notifications;
using Chatcheology.Mobile.OwnMemories;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// CH-54 step 1b: the phone owner's own memories joined into an archive's session (keys with an owner, a second
    /// index, the runtime refresh), where they show, the per-phone settings and the own daily notification. Synthetic
    /// archives, invented titles and dates only.
    /// </summary>
    public sealed class OwnMemoriesSessionTests : IDisposable
    {
        private static readonly TimeOnly Nine = new(9, 0);

        private readonly TemporaryFolder _root = new();
        private readonly MemorySettings _values = new();
        private readonly ArchiveSettings _settings;
        private readonly ArchiveLibrary _library;
        private readonly OwnMemoryStore _own;

        public OwnMemoriesSessionTests()
        {
            _settings = new ArchiveSettings(_values);
            _library = new ArchiveLibrary(_root.Path);
            _own = new OwnMemoryStore(_root.Path);
        }

        public void Dispose() => _root.Dispose();

        private static DateTime At(int day, int hour, int minute = 0) => new(2025, 3, day, hour, minute, 0);

        /// <summary>Three messages on 10 and 12 March 2025; an archive personal memory "Beach week" (entry 1) on 20 March.</summary>
        private static MemoryArchiveBuilder Sample(string firstText = "Hello", Action<MemoryArchiveBuilder>? more = null)
        {
            var builder = new MemoryArchiveBuilder();
            builder.Message(At(10, 20), firstText);
            builder.Message(At(10, 21));
            builder.Message(At(12, 9));
            builder.Memory(1, "Beach week", At(20, 0), summary: "Invented days by the sea.");
            more?.Invoke(builder);
            return builder;
        }

        private InstalledArchive Install(MemoryArchiveBuilder builder)
        {
            var staging = _library.CreateStagingFolder();
            builder.Write(staging);

            var outcome = _library.Install(staging);
            Assert.True(outcome.Installed, string.Join(", ", outcome.Validation.FirstProblems));

            return _library.ListArchives(_settings).Single(archive => archive.Folder == outcome.InstalledFolder);
        }

        /// <summary>An archive without memory collections (version 1 style, no MemoryIndex read of the message times).</summary>
        private InstalledArchive InstallPlain(ArchiveBuilder builder)
        {
            var staging = _library.CreateStagingFolder();
            builder.Write(staging);

            var outcome = _library.Install(staging);
            Assert.True(outcome.Installed, string.Join(", ", outcome.Validation.FirstProblems));

            return _library.ListArchives(_settings).Single(archive => archive.Folder == outcome.InstalledFolder);
        }

        private ArchiveSession Open(InstalledArchive archive, TimeProvider? clock = null) => ArchiveSession.Open(archive, _settings, clock, _own);

        private static OwnMemoryDraft Draft(string title, DateOnly start, bool everyYear = false, IReadOnlyList<OwnPhotoDraft>? photos = null, IReadOnlyList<OwnLink>? links = null) => new()
        {
            Title = title,
            Start = start,
            Precision = MemoryDatePrecision.Day,
            EveryYear = everyYear,
            Photos = photos ?? [],
            Links = links ?? [],
        };

        private static List<string> Describe(ConversationTimeline timeline) =>
            timeline.Rows.Select(row => row switch
            {
                DateSeparatorRow separator => "D " + separator.Day.ToString("MM-dd", CultureInfo.InvariantCulture),
                MessageRow message => "M " + message.SequenceNumber.ToString(CultureInfo.InvariantCulture),
                EntryCardRow card => (card.Key.IsOwn ? "O " : "E ") + card.Key.EntryID.ToString(CultureInfo.InvariantCulture),
                _ => "?",
            }).ToList();

        private static List<string> All(ArchiveSession session)
        {
            var timeline = session.CreateTimeline();
            timeline.OpenAtLatest();
            return Describe(timeline);
        }

        private string Photo(byte seed)
        {
            Directory.CreateDirectory(_own.PhotosFolder);
            var name = Convert.ToHexStringLower(SHA256.HashData([seed, 7])) + ".gif";
            File.WriteAllBytes(Path.Combine(_own.PhotosFolder, name), [.. "GIF89a"u8, seed]);
            return name;
        }

        // ---- Keys ----

        [Fact]
        public void An_own_memory_with_the_same_id_number_as_an_archive_memory_does_not_collide()
        {
            var mine = _own.Add(Draft("My beach day", new DateOnly(2025, 3, 20)));
            Assert.Equal(1, mine.RowID);

            using var session = Open(Install(Sample()));

            // Both rows, the archive's first at the same moment.
            Assert.Equal(["D 03-10", "M 1", "M 2", "D 03-12", "M 3", "D 03-20", "E 1", "O 1"], All(session));

            var archiveKey = new MemoryKey(1, null);
            var ownKey = new MemoryKey(1, null, MemoryOwner.Own);
            Assert.NotEqual(archiveKey, ownKey);
            Assert.Equal("Beach week", session.MemoryDetails(archiveKey)!.Title);
            Assert.Equal("My beach day", session.MemoryDetails(ownKey)!.Title);
            Assert.Equal(ownKey, session.MemoryDetails(ownKey)!.Key);

            // Neither index answers for the other owner's key.
            Assert.Null(session.Memories.Details(ownKey));
            Assert.Null(session.OwnMemories.Details(archiveKey));

            var timeline = session.CreateTimeline();
            Assert.True(timeline.OpenAtMemory(ownKey) >= 0);
            Assert.True(timeline.OpenAtMemory(archiveKey) >= 0);
            Assert.NotEqual(timeline.OpenAtMemory(ownKey), timeline.OpenAtMemory(archiveKey));
        }

        [Fact]
        public void A_key_built_with_two_arguments_is_an_archive_key_and_archive_rows_keep_their_ids()
        {
            Assert.Equal(MemoryOwner.Archive, new MemoryKey(5, null).Owner);
            Assert.Equal(MemoryOwner.Archive, new YearlyMemory(5, "t", new DateOnly(2020, 1, 1)).Owner);

            _own.Add(Draft("Mine", new DateOnly(2025, 3, 11)));
            using var session = Open(Install(Sample()));
            var timeline = session.CreateTimeline();
            timeline.OpenAtLatest();

            var archiveRow = timeline.Rows.OfType<EntryCardRow>().Single(row => !row.Key.IsOwn);
            Assert.Equal(new MemoryKey(1, null), archiveRow.Key);
            Assert.Equal(MemoryArchiveBuilder.Personal, archiveRow.CollectionID);
        }

        [Fact]
        public void A_session_opened_without_the_own_store_or_with_an_empty_one_is_as_before()
        {
            var archive = Install(Sample());

            using var plain = ArchiveSession.Open(archive, _settings);
            using var joined = Open(archive);

            Assert.Equal(All(plain), All(joined));
            Assert.Equal(plain.ReadCalendarDays(), joined.ReadCalendarDays());
            Assert.Equal(0, joined.Own.Count);
            Assert.False(plain.OwnLayerShown);
            Assert.False(plain.RefreshOwnMemories());
        }

        // ---- Where they show ----

        [Fact]
        public void Own_memories_show_in_the_timeline_calendar_date_jump_and_search_and_dates_outside_the_chat_are_placed_as_now()
        {
            var before = _own.Add(Draft("Invented first meeting", new DateOnly(2025, 3, 1)));
            _own.Add(Draft("Invented quiet day", new DateOnly(2025, 3, 11)));
            _own.Add(Draft("Invented later trip", new DateOnly(2025, 4, 2)));

            using var session = Open(Install(Sample()));

            Assert.Equal(
                ["D 03-01", "O 1", "D 03-10", "M 1", "M 2", "D 03-11", "O 2", "D 03-12", "M 3", "D 03-20", "E 1", "D 04-02", "O 3"],
                All(session));

            Assert.Equal(
                [new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 10), new DateOnly(2025, 3, 11), new DateOnly(2025, 3, 12), new DateOnly(2025, 3, 20), new DateOnly(2025, 4, 2)],
                session.ReadCalendarDays());

            // On this day stays message-only.
            Assert.Equal([new DateOnly(2025, 3, 10), new DateOnly(2025, 3, 12)], session.ReadDays());

            var timeline = session.CreateTimeline();
            var first = timeline.OpenAtDay(new DateOnly(2025, 3, 1));
            Assert.Equal(new MemoryKey(before.RowID, null, MemoryOwner.Own), Assert.IsType<EntryCardRow>(timeline.Rows[first]).Key);

            var hits = session.FindMemories("invented");
            Assert.Equal(["Invented later trip", "Invented quiet day", "Invented first meeting"], hits.Select(hit => hit.Title));
            Assert.All(hits, hit => Assert.True(hit.Key.IsOwn));
            Assert.All(hits, hit => Assert.Equal(OwnMemoryIndex.CollectionName, hit.CollectionName));

            // Search across both, newest first.
            Assert.Equal(["Invented later trip", "Beach week"], session.FindMemories("e").Select(hit => hit.Title).Take(2));
        }

        [Fact]
        public async Task A_duplicate_of_an_archive_memory_shows_twice_and_is_listed_twice()
        {
            _own.Add(Draft("Beach week", new DateOnly(2025, 3, 20)) with { End = new DateOnly(2025, 3, 25) });

            using var session = Open(Install(Sample(more: b => b.Memory(2, "Beach week", At(20, 0), At(25, 0))) ));

            Assert.Equal(["E 1", "E 2", "O 1"], All(session).Where(row => row[0] is 'E' or 'O'));
            Assert.Equal(2, session.FindMemories("Beach week").Count(hit => !hit.Key.IsOwn));
            Assert.Single(session.FindMemories("Beach week"), hit => hit.Key.IsOwn);

            var list = (await session.GetMemoryListAsync());
            Assert.Equal(3, list.Rows(MemoryList.PersonalGroup, "Beach week").OfType<MemoryListItem>().Count());
        }

        [Fact]
        public async Task Own_rows_are_in_the_Memories_tab_marked_own_and_never_in_a_collection_tab_or_Group_by_Title()
        {
            _own.Add(Draft("Invented Racer", new DateOnly(2025, 3, 11)));

            using var session = Open(Install(Sample(more: b => b.Game(
                40, "Invented Racer", "strong", false,
                new PeriodSpec("strong", new TrophySpec(MemoryArchiveBuilder.Local, At(12, 9), "1"), new TrophySpec(MemoryArchiveBuilder.Other, At(12, 9, 1), "2"))))));

            var list = (await session.GetMemoryListAsync());
            var own = Assert.Single(list.Items, item => item.IsOwn);

            Assert.Equal(MemoryList.PersonalGroup, own.Group);
            Assert.Equal(OwnMemoryIndex.CollectionName, own.MutedLine);
            Assert.Equal(new MemoryKey(1, null, MemoryOwner.Own), own.Key);
            Assert.DoesNotContain(list.Items, item => item.IsOwn && item.Group != MemoryList.PersonalGroup);

            var gaming = MemoryList.CollectionGroup(MemoryArchiveBuilder.Gaming);
            Assert.DoesNotContain(list.Rows(gaming, null).OfType<MemoryListItem>(), item => item.IsOwn);
            Assert.NotEmpty(MemoryTitleGroups.Groups(list, gaming, null));
            Assert.All(MemoryTitleGroups.Groups(list, gaming, null), group => Assert.DoesNotContain(group.Items, item => item.IsOwn));

            // Show in chat: the own row while the layer is on, its day (with messages) once it is off.
            Assert.Equal(own.Key, session.MemoryShowInChat(list, own.Key)!.Row);
            session.OwnLayerShown = false;
            Assert.Null(session.MemoryShowInChat(list, own.Key));

            _own.Add(Draft("On a day with messages", new DateOnly(2025, 3, 10)));
            session.RefreshOwnMemories();
            var fresh = (await session.GetMemoryListAsync());
            var onMessageDay = fresh.Items.Single(item => item.Title == "On a day with messages");
            Assert.Equal(new DateOnly(2025, 3, 10), session.MemoryShowInChat(fresh, onMessageDay.Key)!.Day);
        }

        [Fact]
        public void An_own_memorys_details_show_its_photos_with_captions_and_its_links_and_no_source()
        {
            var cover = Photo(1);
            var second = Photo(2);
            var mine = _own.Add(Draft(
                "Invented picnic",
                new DateOnly(2025, 3, 11),
                photos: [new OwnPhotoDraft(cover, "The cover's caption"), new OwnPhotoDraft(second, null)],
                links: [new OwnLink("https://example.org/invented-album", "Album")]));

            using var session = Open(Install(Sample()));

            var details = session.MemoryDetails(new MemoryKey(mine.RowID, null, MemoryOwner.Own))!;
            Assert.Equal(OwnMemoryIndex.CollectionName, details.CollectionName);
            Assert.Equal(new string?[] { "The cover's caption", null }, details.Photos.Select(photo => photo.Caption));
            Assert.Equal(Path.Combine(_own.PhotosFolder, cover), details.ImagePath);
            Assert.Equal("https://example.org/invented-album", Assert.Single(details.Galleries).Url);
            Assert.Equal(string.Empty, details.SourceText);
            Assert.Empty(details.Links);

            var card = session.CreateTimeline().Also(t => t.OpenAtDay(new DateOnly(2025, 3, 11))).Rows.OfType<EntryCardRow>().Single(row => row.Key.IsOwn);
            Assert.Equal("+1", card.ExtraPhotosBadge);
            Assert.True(card.HasGalleries);
            Assert.False(card.HasSource);
        }

        // ---- Per phone ----

        [Fact]
        public void A_reimport_with_a_new_hash_still_shows_own_memories_and_removing_an_archive_keeps_them()
        {
            _own.Add(Draft("Mine", new DateOnly(2025, 3, 11)));
            var first = Install(Sample("Hello"));
            var reimport = Install(Sample("Hello again"));
            Assert.NotEqual(first.ContentsSha256, reimport.ContentsSha256);

            using (var one = Open(first))
            using (var two = Open(reimport))
            {
                Assert.Contains("O 1", All(one));
                Assert.Contains("O 1", All(two));
            }

            _library.Remove(first, _settings);

            Assert.Equal(1, _own.Count());
            using var after = Open(_library.ListArchives(_settings).Single());
            Assert.Contains("O 1", All(after));
        }

        [Fact]
        public void The_My_memories_switch_is_per_phone_and_never_under_an_archive_key()
        {
            _own.Add(Draft("Mine", new DateOnly(2025, 3, 11)));
            var first = Install(Sample("Hello"));
            var second = Install(Sample("Other"));

            using (var one = Open(first))
            {
                Assert.True(one.OwnLayerShown);
                var archiveSettings = new Dictionary<string, string>(_values.Values);

                one.OwnLayerShown = false;

                // Nothing was written to the app's per-archive settings.
                Assert.Equal(archiveSettings, _values.Values);
                Assert.DoesNotContain("O 1", All(one));
            }

            using (var two = Open(second))
            {
                Assert.False(two.OwnLayerShown);
                Assert.DoesNotContain("O 1", All(two));
            }

            Assert.False(_own.Settings.LayerOn);

            // Forgetting an archive leaves it as it is.
            _settings.Forget(first.ContentsSha256!);
            Assert.False(_own.Settings.LayerOn);
        }

        // ---- On this day ----

        [Fact]
        public void On_this_day_lists_archive_yearly_by_title_then_own_yearly_by_title_whatever_the_layer_switches_say()
        {
            _own.Add(Draft("Zulu own", new DateOnly(2024, 3, 15), everyYear: true));
            _own.Add(Draft("Alpha own", new DateOnly(2023, 3, 15), everyYear: true));
            _own.Add(Draft("Not yearly", new DateOnly(2023, 3, 15)));

            var archive = Install(Sample(more: b =>
            {
                b.Yearly(30, "Yankee archive", new DateOnly(2022, 3, 15));
                b.Yearly(31, "Bravo archive", new DateOnly(2022, 3, 15));
            }));

            using var session = Open(archive);
            session.SetLayerShown(MemoryArchiveBuilder.Personal, false);
            session.OwnLayerShown = false;

            var card = session.OnThisDayFor(session.ReadDays(), new DateOnly(2026, 3, 15));

            Assert.Equal(["Bravo archive", "Yankee archive", "Alpha own", "Zulu own"], card.Yearly.Select(y => y.Title));
            Assert.Equal([false, false, true, true], card.Yearly.Select(y => y.IsOwn));
            Assert.Equal([3, 2], card.Yearly.Where(y => y.IsOwn).Select(y => y.Years!.Value));
            Assert.True(card.ShowsIcon);

            // A tap opens the right details by key.
            Assert.Equal("Alpha own", session.MemoryDetails(card.Yearly[2].Key)!.Title);
            Assert.Equal("Bravo archive", session.MemoryDetails(card.Yearly[0].Key)!.Title);

            // Only On this day hides them.
            session.OnThisDayEnabled = false;
            Assert.False(session.OnThisDayFor(session.ReadDays(), new DateOnly(2026, 3, 15)).ShowsIcon);
        }

        [Fact]
        public async Task An_own_yearly_memory_is_listed_once_at_its_own_date_and_marked_every_year()
        {
            _own.Add(Draft("Our day", new DateOnly(2025, 3, 10), everyYear: true));

            using var session = Open(Install(Sample()));
            var item = Assert.Single((await session.GetMemoryListAsync()).Items, item => item.IsOwn);

            Assert.True(item.IsYearly);
            Assert.Equal(OwnMemoryIndex.CollectionName + " · Every year", item.DetailText);
            Assert.Equal(new DateTime(2025, 3, 10), item.SortTime);
        }

        // ---- Refresh ----

        [Fact]
        public async Task Refresh_after_add_edit_and_delete_updates_the_list_calendar_and_yearly_set()
        {
            // The schedule's Next() reads the own store itself, so its lines here follow the store, not the refresh
            // (1b review S5); the page reschedules after every save or delete.
            var archive = Install(Sample());
            using var session = Open(archive);
            var today = new DateOnly(2026, 3, 15);
            var now = new DateTime(2026, 3, 1, 8, 0, 0);

            Assert.DoesNotContain((await session.GetMemoryListAsync()).Items, item => item.IsOwn);
            Assert.Null(Next(now));

            // Add.
            var added = _own.Add(Draft("Invented day", new DateOnly(2024, 3, 15), everyYear: true));
            Assert.True(session.RefreshOwnMemories());

            var list = (await session.GetMemoryListAsync());
            Assert.Single(list.Items, item => item.IsOwn && item.IsYearly);
            Assert.Contains(new DateOnly(2024, 3, 15), session.ReadCalendarDays());
            Assert.Single(session.OnThisDayFor(session.ReadDays(), today).Yearly);
            Assert.Contains("O 1", All(session));
            Assert.Equal(new DateOnly(2026, 3, 15), Next(now)!.Day);

            // Nothing changed: no rebuild needed, but the list is built again anyway.
            Assert.False(session.RefreshOwnMemories());
            Assert.NotSame(list, (await session.GetMemoryListAsync()));

            // Edit: a new date and no Every year.
            _own.Update(added.Id, Draft("Invented day, moved", new DateOnly(2025, 3, 11)));
            Assert.True(session.RefreshOwnMemories());

            Assert.Equal("Invented day, moved", (await session.GetMemoryListAsync()).Items.Single(item => item.IsOwn).Title);
            Assert.DoesNotContain(new DateOnly(2024, 3, 15), session.ReadCalendarDays());
            Assert.Contains(new DateOnly(2025, 3, 11), session.ReadCalendarDays());
            Assert.Empty(session.OnThisDayFor(session.ReadDays(), today).Yearly);
            Assert.Null(Next(now));

            // A title edit alone also asks for a rebuild.
            _own.Update(added.Id, Draft("Invented day, renamed", new DateOnly(2025, 3, 11)));
            Assert.True(session.RefreshOwnMemories());

            // Delete.
            Assert.True(_own.Delete(added.Id));
            Assert.True(session.RefreshOwnMemories());

            Assert.DoesNotContain((await session.GetMemoryListAsync()).Items, item => item.IsOwn);
            Assert.Equal(session.ReadDays().Concat([new DateOnly(2025, 3, 20)]).Order(), session.ReadCalendarDays());
            Assert.DoesNotContain("O 1", All(session));
            Assert.Null(session.MemoryDetails(new MemoryKey(added.RowID, null, MemoryOwner.Own)));
        }

        [Fact]
        public void Refresh_on_a_closed_session_is_refused()
        {
            var session = Open(Install(Sample()));
            session.Dispose();

            Assert.Throws<ObjectDisposedException>(() => session.RefreshOwnMemories());
        }

        [Fact]
        public void An_unreadable_own_store_shows_no_own_memories_and_the_archive_still_opens()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_own.DatabasePath)!);

            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _own.DatabasePath, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version = 99;";
                command.ExecuteNonQuery();
            }

            using var session = Open(Install(Sample()));

            Assert.True(session.OwnMemoriesUnreadable);
            Assert.Equal(0, session.Own.Count);
            Assert.Equal(["D 03-10", "M 1", "M 2", "D 03-12", "M 3", "D 03-20", "E 1"], All(session));
        }

        // ---- Notifications ----

        private NextNotification? Next(DateTime now)
        {
            var archives = NotedDateSchedule.Read(_library, _settings);
            return NotedDateSchedule.Next(archives, NotedDateSchedule.ReadOwn(_own, archives, _settings), Nine, now);
        }

        [Fact]
        public void An_own_yearly_memory_notifies_once_with_two_archives_installed_with_generic_text()
        {
            _own.Add(Draft("Invented anniversary", new DateOnly(2024, 3, 15), everyYear: true));
            var first = Install(Sample("Hello"));
            var second = Install(Sample("Hello again"));

            var clock = new FixedClock(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc));
            using (Open(first, clock))
            {
            }

            clock.Utc = clock.Utc.AddHours(1);
            using (Open(second, clock))
            {
            }

            var next = Next(new DateTime(2026, 3, 2, 8, 0, 0))!;

            Assert.Equal(new DateOnly(2026, 3, 15), next.Day);
            Assert.Equal(new DateTime(2026, 3, 15, 9, 0, 0), next.LocalMoment);
            Assert.Empty(next.Due);
            Assert.NotNull(next.Own);
            Assert.Equal(1, next.Own.Count);
            Assert.Equal("A memory today", next.Own.Text);
            Assert.Equal(Path.GetFileName(second.Folder), next.Own.TapInstallId);
            Assert.DoesNotContain("Invented", next.Own.Text, StringComparison.Ordinal);

            // Each archive's own notification never counts it.
            Assert.All(NotedDateSchedule.Read(_library, _settings), archive => Assert.Equal(0, archive.CountOn(new DateOnly(2026, 3, 15))));
            Assert.NotEqual(NotedDateSchedule.OwnNotificationId, NotedDateSchedule.NotificationId(Path.GetFileName(first.Folder)));
        }

        [Fact]
        public void A_mute_or_reminders_off_silence_it_and_the_card_still_shows_it()
        {
            var memory = _own.Add(Draft("Invented anniversary", new DateOnly(2024, 3, 15), everyYear: true));
            var archive = Install(Sample());
            var now = new DateTime(2026, 3, 2, 8, 0, 0);

            Assert.NotNull(Next(now)!.Own);

            Assert.True(_own.Settings.SetMuted(memory.Id, true));
            Assert.Null(Next(now));
            Assert.False(Assert.Single(OwnMemoryIndex.YearlyListings(_own)).Notify);

            Assert.True(_own.Settings.SetMuted(memory.Id, false));
            _own.Settings.RemindersOn = false;
            Assert.Null(Next(now));

            using var session = Open(archive);
            Assert.Single(session.OnThisDayFor(session.ReadDays(), new DateOnly(2026, 3, 15)).Yearly);
        }

        [Fact]
        public void The_next_alarm_is_the_earlier_of_the_archive_and_own_moments_and_both_on_one_day()
        {
            var own = new OwnMemoryReminders(true, [new OwnYearlyMemoryListing(Guid.NewGuid(), 1, "Own", new DateOnly(2024, 6, 1), true)]);
            ArchiveNotedDates archive = new("a1", "hash", true, [])
            {
                Yearly = [new YearlyMemoryListing(7, "Archive", new DateOnly(2024, 5, 1), true)],
            };
            var now = new DateTime(2026, 4, 1, 8, 0, 0);

            var archiveFirst = NotedDateSchedule.Next([archive], own, Nine, now)!;
            Assert.Equal(new DateOnly(2026, 5, 1), archiveFirst.Day);
            Assert.Single(archiveFirst.Due);
            Assert.Null(archiveFirst.Own);

            var ownFirst = NotedDateSchedule.Next([archive with { Yearly = [new YearlyMemoryListing(7, "Archive", new DateOnly(2024, 7, 1), true)] }], own, Nine, now)!;
            Assert.Equal(new DateOnly(2026, 6, 1), ownFirst.Day);
            Assert.Empty(ownFirst.Due);
            Assert.Equal(1, ownFirst.Own!.Count);

            var both = NotedDateSchedule.Next([archive with { Yearly = [new YearlyMemoryListing(7, "Archive", new DateOnly(2024, 6, 1), true)] }], own, Nine, now)!;
            Assert.Single(both.Due);
            Assert.NotNull(both.Own);

            // Without own memories the archive schedule is as before.
            var alone = NotedDateSchedule.Next([archive], Nine, now)!;
            Assert.Equal(archiveFirst.Day, alone.Day);
            Assert.Equal(archiveFirst.LocalMoment, alone.LocalMoment);
            Assert.Equal(archiveFirst.Due, alone.Due);
            Assert.Null(alone.Own);
        }

        [Fact]
        public void A_due_unposted_own_day_after_the_time_is_scheduled_one_minute_from_now_and_a_posted_one_is_not()
        {
            var listing = new OwnYearlyMemoryListing(Guid.NewGuid(), 1, "Own", new DateOnly(2024, 6, 1), true);
            var late = new DateTime(2026, 6, 1, 15, 30, 0);

            var catchUp = NotedDateSchedule.Next([], new OwnMemoryReminders(true, [listing], TapInstallId: "a1"), Nine, late)!;
            Assert.Equal(new DateOnly(2026, 6, 1), catchUp.Day);
            Assert.Equal(late + NotedDateSchedule.CatchUpDelay, catchUp.LocalMoment);
            Assert.Equal("a1", catchUp.Own!.TapInstallId);

            var posted = NotedDateSchedule.Next([], new OwnMemoryReminders(true, [listing], new DateOnly(2026, 6, 1)), Nine, late)!;
            Assert.Equal(new DateOnly(2027, 6, 1), posted.Day);

            // A far-future own memory is found on its own date.
            var future = new OwnYearlyMemoryListing(Guid.NewGuid(), 2, "Later", new DateOnly(2031, 2, 2), true);
            Assert.Equal(new DateOnly(2031, 2, 2), NotedDateSchedule.Next([], new OwnMemoryReminders(true, [future]), Nine, late)!.Day);

            Assert.Equal("3 memories today", NotedDateSchedule.TextForOwn(3));
        }

        [Fact]
        public void A_tap_opens_the_last_opened_archive_or_the_start_screen()
        {
            Assert.Null(NotedDateSchedule.OwnTapTarget([]));
            Assert.Null(NotedDateSchedule.OwnTapTarget([("a1", null), ("a2", null)]));
            Assert.Equal("a2", NotedDateSchedule.OwnTapTarget([("a1", new DateTime(2026, 1, 1)), ("a2", new DateTime(2026, 2, 1)), ("a3", null)]));
            Assert.Equal("a1", NotedDateSchedule.OwnTapTarget([("a2", new DateTime(2026, 1, 1)), ("a1", new DateTime(2026, 1, 1))]));

            // No archive installed: the start screen.
            _own.Add(Draft("Invented anniversary", new DateOnly(2024, 3, 15), everyYear: true));
            Assert.Null(Next(new DateTime(2026, 3, 2, 8, 0, 0))!.Own!.TapInstallId);
        }

        [Fact]
        public async Task The_alarm_reads_the_own_store_while_the_UI_holds_a_write_lock_by_waiting_for_it()
        {
            _own.Add(Draft("Invented anniversary", new DateOnly(2024, 3, 15), everyYear: true));

            using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _own.DatabasePath, Pooling = false }.ToString());
            writer.Open();
            using (var begin = writer.CreateCommand())
            {
                begin.CommandText = "BEGIN EXCLUSIVE; UPDATE OwnMemory SET Title = 'Invented anniversary, edited';";
                begin.ExecuteNonQuery();
            }

            var read = Task.Run(() => NotedDateSchedule.ReadOwn(_own, [], _settings));

            await Task.Delay(400);
            Assert.False(read.IsCompleted);

            using (var commit = writer.CreateCommand())
            {
                commit.CommandText = "COMMIT;";
                commit.ExecuteNonQuery();
            }

            var reminders = await read;
            Assert.NotNull(reminders);
            Assert.Equal("Invented anniversary, edited", Assert.Single(reminders.Yearly).Title);
        }

        [Fact]
        public void An_unreadable_own_store_notifies_nothing_for_it_and_the_archives_still_do()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_own.DatabasePath)!);
            File.WriteAllText(_own.DatabasePath, "not a database, invented");

            // One archive that notifies on 15 March (1b review S5: the archives are really there).
            ArchiveNotedDates archive = new("a1", "hash", true, [])
            {
                Yearly = [new YearlyMemoryListing(7, "Invented archive day", new DateOnly(2024, 3, 15), true)],
            };

            var own = NotedDateSchedule.ReadOwnWithRetry(_own, [archive], _settings, attempts: 1);
            Assert.Null(own.Reminders);
            Assert.False(own.MayPass);
            Assert.Null(NotedDateSchedule.ReadOwn(_own, [archive], _settings));

            var next = NotedDateSchedule.WithRetry(NotedDateSchedule.Next([archive], own.Reminders, Nine, new DateTime(2026, 3, 2, 8, 0, 0)), own.MayPass, new DateTime(2026, 3, 2, 8, 0, 0))!;
            Assert.Equal(new DateOnly(2026, 3, 15), next.Day);
            Assert.False(next.IsRetry);
            Assert.Equal("a1", Assert.Single(next.Due).InstallId);
            Assert.Null(next.Own);
            Assert.Contains("own=0", NotedDateSchedule.DescribeCounts([archive], null, next), StringComparison.Ordinal);
        }

        // ---- 1b review S2: a failure that may pass is retried, not taken for "no own memories" ----

        [Fact]
        public void A_busy_or_IO_failure_may_pass_and_a_damaged_or_newer_store_does_not()
        {
            Assert.True(NotedDateSchedule.MayPass(new SqliteException("invented busy", 5)));
            Assert.True(NotedDateSchedule.MayPass(new SqliteException("invented locked", 6)));
            Assert.True(NotedDateSchedule.MayPass(new OwnMemoriesStoreException("x", new SqliteException("invented io", 10))));
            Assert.True(NotedDateSchedule.MayPass(new IOException("invented")));
            Assert.False(NotedDateSchedule.MayPass(new SqliteException("invented not a database", 26)));
            Assert.False(NotedDateSchedule.MayPass(new OwnMemoriesStoreException("The own memories file has version 99.")));
            Assert.False(NotedDateSchedule.MayPass(new UnauthorizedAccessException()));
        }

        [Fact]
        public async Task A_store_still_busy_after_the_retries_schedules_a_short_retry_instead_of_going_silent()
        {
            _own.Add(Draft("Invented anniversary", new DateOnly(2024, 3, 15), everyYear: true));

            using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _own.DatabasePath, Pooling = false }.ToString());
            writer.Open();
            using (var begin = writer.CreateCommand())
            {
                begin.CommandText = "BEGIN EXCLUSIVE;";
                begin.ExecuteNonQuery();
            }

            // The busy timeout (5 s) passes once: still busy.
            var read = await Task.Run(() => NotedDateSchedule.ReadOwnWithRetry(_own, [], _settings, attempts: 1));

            using (var rollback = writer.CreateCommand())
            {
                rollback.CommandText = "ROLLBACK;";
                rollback.ExecuteNonQuery();
            }

            Assert.Null(read.Reminders);
            Assert.True(read.MayPass);

            var now = new DateTime(2026, 3, 2, 8, 0, 0);
            var retry = NotedDateSchedule.WithRetry(NotedDateSchedule.Next([], read.Reminders, Nine, now), read.MayPass, now)!;
            Assert.True(retry.IsRetry);
            Assert.Equal(now + NotedDateSchedule.RetryDelay, retry.LocalMoment);
            Assert.Empty(retry.Due);
            Assert.Null(retry.Own);
            Assert.Contains("retry=yes", NotedDateSchedule.DescribeCounts([], null, retry), StringComparison.Ordinal);

            // Free again: the read succeeds and the memory's own day is scheduled.
            var again = NotedDateSchedule.ReadOwnWithRetry(_own, [], _settings);
            Assert.False(again.MayPass);
            Assert.Equal(new DateOnly(2026, 3, 15), NotedDateSchedule.WithRetry(NotedDateSchedule.Next([], again.Reminders, Nine, now), again.MayPass, now)!.Day);
        }

        [Fact]
        public void A_retry_never_delays_a_moment_due_sooner()
        {
            var now = new DateTime(2026, 6, 1, 8, 55, 0);
            var soon = new NextNotification(new DateOnly(2026, 6, 1), new DateTime(2026, 6, 1, 9, 0, 0), []);

            Assert.Same(soon, NotedDateSchedule.WithRetry(soon, ownMayPass: true, now));
            Assert.Same(soon, NotedDateSchedule.WithRetry(soon, ownMayPass: false, now));
            Assert.Null(NotedDateSchedule.WithRetry(null, ownMayPass: false, now));
            Assert.True(NotedDateSchedule.WithRetry(null, ownMayPass: true, now)!.IsRetry);
        }

        // ---- Step 2 review S1: a retry alarm never posts early ----

        [Fact]
        public void A_retry_at_0715_for_a_0900_time_posts_nothing_and_the_0900_alarm_does()
        {
            var at = new DateTime(2026, 3, 15, 7, 0, 0);
            var retry = NotedDateSchedule.WithRetry(null, ownMayPass: true, at)!;

            Assert.True(retry.IsRetry);
            Assert.Equal(new DateTime(2026, 3, 15, 7, 15, 0), retry.LocalMoment);
            Assert.False(NotedDateSchedule.MayPostNow(retry.Day, Nine, retry.LocalMoment));
            Assert.False(NotedDateSchedule.MayPostNow(retry.Day, Nine, new DateTime(2026, 3, 15, 8, 59, 59)));
            Assert.True(NotedDateSchedule.MayPostNow(retry.Day, Nine, new DateTime(2026, 3, 15, 9, 0, 0)));
            Assert.True(NotedDateSchedule.MayPostNow(retry.Day, Nine, new DateTime(2026, 3, 15, 9, 0, 30)));
        }

        [Fact]
        public void A_retry_at_0005_after_a_busy_2350_posts_nothing_for_the_next_day()
        {
            var at = new DateTime(2026, 3, 14, 23, 50, 0);
            var retry = NotedDateSchedule.WithRetry(null, ownMayPass: true, at)!;

            Assert.Equal(new DateOnly(2026, 3, 15), retry.Day);
            Assert.Equal(new DateTime(2026, 3, 15, 0, 5, 0), retry.LocalMoment);
            Assert.False(NotedDateSchedule.MayPostNow(retry.Day, Nine, retry.LocalMoment));
        }

        [Fact]
        public void A_late_catch_up_after_the_time_still_posts_and_a_stale_day_never_does()
        {
            var now = new DateTime(2026, 3, 15, 18, 0, 0);

            Assert.True(NotedDateSchedule.MayPostNow(new DateOnly(2026, 3, 15), Nine, now + NotedDateSchedule.CatchUpDelay));
            Assert.False(NotedDateSchedule.MayPostNow(new DateOnly(2026, 3, 14), Nine, now));
            Assert.False(NotedDateSchedule.MayPostNow(new DateOnly(2026, 3, 16), Nine, now));
        }

        // ---- 1b review S3: what a refresh always refreshes, and what its result gates ----

        [Fact]
        public async Task Turning_Every_year_alone_needs_no_timeline_rebuild_but_On_this_day_and_the_list_follow()
        {
            var archive = Install(Sample());
            using var session = Open(archive);
            var today = new DateOnly(2026, 3, 15);
            var memory = _own.Add(Draft("Invented day", new DateOnly(2025, 3, 15)));
            Assert.True(session.RefreshOwnMemories());
            Assert.Empty(session.OnThisDayFor(session.ReadDays(), today).Yearly);
            var rows = All(session);

            _own.Update(memory.Id, Draft("Invented day", new DateOnly(2025, 3, 15), everyYear: true));

            // The same rows at the same places: no rebuild. The yearly set and the list are new all the same.
            Assert.False(session.RefreshOwnMemories());
            Assert.Equal(rows, All(session));
            Assert.Equal("Invented day", Assert.Single(session.OnThisDayFor(session.ReadDays(), today).Yearly).Title);
            Assert.True(Assert.Single((await session.GetMemoryListAsync()).Items, item => item.IsOwn).IsYearly);

            _own.Update(memory.Id, Draft("Invented day", new DateOnly(2025, 3, 15)));
            Assert.False(session.RefreshOwnMemories());
            Assert.Empty(session.OnThisDayFor(session.ReadDays(), today).Yearly);
        }

        [Fact]
        public async Task Adding_while_the_layer_is_off_needs_no_rebuild_and_the_memory_is_still_listed()
        {
            using var session = Open(Install(Sample()));
            session.OwnLayerShown = false;
            var rows = All(session);

            _own.Add(Draft("Invented hidden day", new DateOnly(2025, 3, 11)));

            Assert.False(session.RefreshOwnMemories());
            Assert.Equal(rows, All(session));
            Assert.Single((await session.GetMemoryListAsync()).Items, item => item.IsOwn);
            Assert.Empty(session.FindMemories("hidden"));

            session.OwnLayerShown = true;
            Assert.Contains("O 1", All(session));
        }

        [Fact]
        public void The_first_own_memory_in_an_archive_with_no_messages_or_memories_turns_Saved_on()
        {
            using var session = Open(InstallPlain(new ArchiveBuilder()));
            Assert.True(session.IsEmpty);
            Assert.False(session.HasSaved);
            Assert.True(session.HasOwnStore);

            _own.Add(Draft("Invented first", new DateOnly(2025, 3, 11)));

            // The refresh result is about rows; Saved follows the own count whatever it says.
            session.RefreshOwnMemories();
            Assert.True(session.HasSaved);

            Assert.True(_own.Delete(_own.List().Single().Id));
            session.RefreshOwnMemories();
            Assert.False(session.HasSaved);
        }

        // ---- 1b review N2, N3 ----

        [Fact]
        public void The_layer_switch_never_throws_when_the_store_cannot_be_written_and_keeps_its_old_value()
        {
            using var session = Open(Install(Sample()));
            Assert.True(session.OwnLayerShown);

            using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _own.DatabasePath, Pooling = false, DefaultTimeout = 1 }.ToString());
            writer.Open();
            using (var begin = writer.CreateCommand())
            {
                begin.CommandText = "BEGIN EXCLUSIVE;";
                begin.ExecuteNonQuery();
            }

            session.OwnLayerShown = false;
            Assert.True(session.OwnLayerShown);

            using (var rollback = writer.CreateCommand())
            {
                rollback.CommandText = "ROLLBACK;";
                rollback.ExecuteNonQuery();
            }

            session.OwnLayerShown = false;
            Assert.False(session.OwnLayerShown);
        }

        [Fact]
        public async Task An_archive_without_memories_whose_message_times_cannot_be_read_still_opens_with_own_rows_not_placed()
        {
            var builder = new ArchiveBuilder();
            builder.Add("Hello", ArchiveBuilder.Other, At(10, 20));
            builder.Add("Again", ArchiveBuilder.Local, At(10, 21));
            builder.Add("Later", ArchiveBuilder.Other, At(12, 9));
            var archive = InstallPlain(builder);

            // A gap in the sequence numbers (invented damage): reading the message times fails.
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(archive.Folder, ArchiveFormat.DatabaseFileName), Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE Message SET SequenceNumber = 9 WHERE SequenceNumber = 3;";
                command.ExecuteNonQuery();
            }

            _own.Add(Draft("Invented unplaced day", new DateOnly(2025, 3, 11), everyYear: true));

            using var session = Open(archive);

            Assert.False(session.OwnRowsPlaceable);
            Assert.Equal(MemoryView.None, session.OwnMemoryView);
            Assert.True(session.VisibleMemoryLayout().IsEmpty);
            Assert.Empty(session.FindMemories("unplaced"));

            // Still listed, with details and on On this day; never placed at a wrong row.
            var item = Assert.Single((await session.GetMemoryListAsync()).Items, i => i.IsOwn);
            Assert.NotNull(session.MemoryDetails(item.Key));
            Assert.Null(session.MemoryShowInChat(await session.GetMemoryListAsync(), item.Key)?.Row);
            Assert.Single(session.OnThisDayFor([], new DateOnly(2026, 3, 11)).Yearly);
        }
    }

    /// <summary>A small helper to act on a value inline.</summary>
    internal static class TestValueExtensions
    {
        internal static T Also<T>(this T value, Action<T> act)
        {
            act(value);
            return value;
        }
    }
}
