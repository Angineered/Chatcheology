using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// Favourites (CH-38): stars added and removed, kept per archive hash, kept through a remove, the flags on
    /// screen and the Starred list; and On this day (CH-39): matching, 29 February, once a day, the switch.
    /// </summary>
    public sealed class StarsAndOnThisDayTests : IDisposable
    {
        private readonly TemporaryFolder _root = new();
        private readonly ArchiveLibrary _library;
        private readonly MemorySettings _store = new();
        private readonly ArchiveSettings _settings;

        public StarsAndOnThisDayTests()
        {
            _library = new ArchiveLibrary(_root.Path);
            _settings = new ArchiveSettings(_store);
        }

        public void Dispose() => _root.Dispose();

        private InstalledArchive Install(ArchiveBuilder builder)
        {
            var staging = _library.CreateStagingFolder();
            builder.Write(staging);

            var outcome = _library.Install(staging);
            Assert.True(outcome.Installed);

            return _library.ListArchives(_settings).Single(archive => archive.Folder == outcome.InstalledFolder);
        }

        /// <summary>Five messages: 2 has a photo, 4 has two photos, 5 is a system line.</summary>
        private static ArchiveBuilder Builder(string title = "Alex and Sam")
        {
            var builder = new ArchiveBuilder(title);
            builder.Add("First message");
            builder.Media(new AttachmentSpec(20));
            builder.Add("Third, from me", ArchiveBuilder.Local);
            builder.Add("Two photos", ArchiveBuilder.Other, null, null, null, new AttachmentSpec(40), new AttachmentSpec(41));
            builder.Add("System line", sender: null);

            return builder;
        }

        private static MessageRow Row(ConversationTimeline timeline, int sequence) =>
            timeline.Rows.OfType<MessageRow>().Single(row => row.SequenceNumber == sequence);

        // ---------------------------------------------------------------------------------------
        // Stars.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void A_star_is_added_and_removed_and_kept_in_settings_only()
        {
            var archive = Install(Builder());
            var before = Directory.EnumerateFiles(archive.Folder, "*", SearchOption.AllDirectories)
                .ToDictionary(file => file, File.ReadAllBytes);

            using (var session = ArchiveSession.Open(archive, _settings))
            {
                Assert.True(session.Stars.SetMessage(3, true));
                Assert.True(session.Stars.SetAttachment(4, 41, true));
                Assert.True(session.Stars.IsMessageStarred(3));
                Assert.True(session.Stars.IsAttachmentStarred(41));
                Assert.False(session.Stars.IsAttachmentStarred(40));
                Assert.Equal(2, session.Stars.Count);

                Assert.False(session.Stars.SetMessage(3, false));
                Assert.False(session.Stars.IsMessageStarred(3));
                Assert.Equal(1, session.Stars.Count);
            }

            Assert.Equal([], _settings.GetStarredMessages(archive.ContentsSha256!));
            Assert.Equal([new StarredAttachment(4, 41)], _settings.GetStarredAttachments(archive.ContentsSha256!));

            // Nothing inside the archive changed.
            var after = Directory.EnumerateFiles(archive.Folder, "*", SearchOption.AllDirectories).ToList();
            Assert.Equal(before.Keys.Order(), after.Order());
            Assert.All(after, file => Assert.Equal(before[file], File.ReadAllBytes(file)));
        }

        [Fact]
        public void Stars_persist_per_archive_hash_and_not_across_archives()
        {
            var first = Install(Builder());
            var other = Install(Builder("Another chat"));

            using (var session = ArchiveSession.Open(first, _settings))
            {
                session.Stars.SetMessage(1, true);
            }

            using (var again = ArchiveSession.Open(first, _settings))
            {
                Assert.True(again.Stars.IsMessageStarred(1));
            }

            using var another = ArchiveSession.Open(other, _settings);
            Assert.False(another.Stars.IsMessageStarred(1));
            Assert.Equal(0, another.Stars.Count);
        }

        [Fact]
        public void Stars_survive_removing_the_archive_and_come_back_when_the_same_archive_is_imported_again()
        {
            var archive = Install(Builder());

            // The folder she would import again: the same files, so the same hash.
            var source = Path.Combine(_root.Path, "source-folder");
            CopyFolder(archive.Folder, source);

            using (var session = ArchiveSession.Open(archive, _settings))
            {
                session.Stars.SetMessage(1, true);
                session.Stars.SetAttachment(2, 20, true);
                session.SavePosition(3);
                session.OnThisDayEnabled = false;
            }

            _library.Remove(archive, _settings);

            // Everything else about it is forgotten; the stars are kept (Angie, 2026-10-22).
            Assert.Null(_settings.GetPosition(archive.ContentsSha256!));
            Assert.True(_settings.GetOnThisDayEnabled(archive.ContentsSha256!));
            Assert.Equal([1], _settings.GetStarredMessages(archive.ContentsSha256!));

            var staging = _library.CreateStagingFolder();
            CopyFolder(source, staging);
            var outcome = _library.Install(staging);
            Assert.True(outcome.Installed);

            var reimported = _library.ListArchives(_settings).Single(listed => listed.Folder == outcome.InstalledFolder);
            Assert.Equal(archive.ContentsSha256, reimported.ContentsSha256);

            using var again = ArchiveSession.Open(reimported, _settings);

            Assert.True(again.Stars.IsMessageStarred(1));
            Assert.True(again.Stars.IsAttachmentStarred(20));
            Assert.Equal(2, again.Stars.Count);
        }

        [Fact]
        public void After_a_remove_an_archive_with_another_hash_starts_without_stars()
        {
            var archive = Install(Builder());

            using (var session = ArchiveSession.Open(archive, _settings))
            {
                session.Stars.SetMessage(1, true);
            }

            _library.Remove(archive, _settings);

            var reExport = Builder();
            reExport.Add("Added later");
            var other = Install(reExport);

            using var fresh = ArchiveSession.Open(other, _settings);

            Assert.NotEqual(archive.ContentsSha256, other.ContentsSha256);
            Assert.Equal(0, fresh.Stars.Count);
        }

        [Fact]
        public void The_same_archive_under_another_folder_has_its_stars_and_a_re_export_has_none()
        {
            var archive = Install(Builder());

            using (var session = ArchiveSession.Open(archive, _settings))
            {
                session.Stars.SetMessage(3, true);
                session.Stars.SetAttachment(4, 40, true);
            }

            // The same contents (same hash) installed under a new folder id, as after the app's data was
            // moved: the stars are found by hash, not by folder.
            var copy = Path.Combine(_library.ArchivesFolder, "copy-of-the-same-archive");
            CopyFolder(archive.Folder, copy);
            var same = _library.ListArchives(_settings).Single(listed => listed.Folder == copy);
            Assert.Equal(archive.ContentsSha256, same.ContentsSha256);

            using (var reopened = ArchiveSession.Open(same, _settings))
            {
                Assert.True(reopened.Stars.IsMessageStarred(3));
                Assert.True(reopened.Stars.IsAttachmentStarred(40));
            }

            // A re-exported archive (one more message: a new hash) starts with none.
            var reExport = Builder();
            reExport.Add("Added later");
            var reExported = Install(reExport);

            using var fresh = ArchiveSession.Open(reExported, _settings);
            Assert.NotEqual(archive.ContentsSha256, reExported.ContentsSha256);
            Assert.Equal(0, fresh.Stars.Count);
        }

        [Fact]
        public void Rows_carry_their_star_flags_when_built_and_follow_a_toggle()
        {
            var archive = Install(Builder());

            using var session = ArchiveSession.Open(archive, _settings);
            session.Stars.SetMessage(1, true);

            var timeline = session.CreateTimeline();
            session.OpenTimeline(timeline);

            Assert.True(Row(timeline, 1).IsStarred);
            Assert.True(Row(timeline, 1).ShowsStar);
            Assert.False(Row(timeline, 3).ShowsStar);

            var changes = new List<string?>();
            Row(timeline, 4).PropertyChanged += (_, e) => changes.Add(e.PropertyName);

            session.Stars.SetAttachment(4, 41, true);
            session.Stars.SetMessage(1, false);
            timeline.RefreshStars();

            Assert.False(Row(timeline, 1).IsStarred);
            Assert.False(Row(timeline, 4).IsStarred);
            Assert.True(Row(timeline, 4).ShowsStar);
            Assert.True(Row(timeline, 4).Attachments.Single(card => card.AttachmentID == 41).IsStarred);
            Assert.False(Row(timeline, 4).Attachments.Single(card => card.AttachmentID == 40).IsStarred);
            Assert.Contains(nameof(MessageRow.ShowsStar), changes);
        }

        [Fact]
        public void A_page_loaded_later_carries_the_star_flags()
        {
            var builder = new ArchiveBuilder();

            for (var i = 1; i <= ConversationTimeline.PageSize * 2; i++)
            {
                builder.Add("Message " + i);
            }

            var archive = Install(builder);

            using var session = ArchiveSession.Open(archive, _settings);
            session.Stars.SetMessage(5, true);

            var timeline = session.CreateTimeline();
            timeline.OpenAtLatest();
            Assert.Equal(-1, timeline.IndexOfMessage(5));

            while (timeline.ReadEarlier() is { } page)
            {
                timeline.Prepend(page);
            }

            Assert.True(Row(timeline, 5).IsStarred);
            Assert.False(Row(timeline, 6).IsStarred);
        }

        [Fact]
        public void A_system_line_cannot_be_starred_and_every_other_message_can()
        {
            var archive = Install(Builder());

            using var session = ArchiveSession.Open(archive, _settings);
            var timeline = session.CreateTimeline();
            session.OpenTimeline(timeline);

            Assert.False(Row(timeline, 5).CanStar);
            Assert.True(Row(timeline, 2).CanStar);
            Assert.True(Row(timeline, 2).HasActions);
            Assert.False(Row(timeline, 2).CanShareText);
        }

        [Fact]
        public async Task The_starred_list_is_newest_first_with_messages_before_their_photos()
        {
            var archive = Install(Builder());

            using var session = ArchiveSession.Open(archive, _settings);
            session.Stars.SetMessage(1, true);
            session.Stars.SetMessage(2, true);
            session.Stars.SetMessage(4, true);
            session.Stars.SetAttachment(4, 41, true);
            session.Stars.SetAttachment(4, 40, true);
            session.Stars.SetMessage(99, true);

            var list = await session.ReadStarredAsync();

            Assert.Equal(
                [(4, false, (long?)40), (4, true, 40), (4, true, 41), (2, false, 20), (1, false, null)],
                list.Select(entry => (entry.SequenceNumber, entry.IsPhoto, entry.Thumbnail?.AttachmentID)).ToList());

            Assert.Equal("Two photos", list[0].Snippet);
            Assert.Equal("Photo", list[3].Snippet);
            Assert.Equal("First message", list[4].Snippet);
            Assert.Equal("Sam", list[4].SenderText);
            Assert.Equal(DateSeparatorRow.DayText(new DateOnly(2025, 3, 1)), list[4].DateText);
            Assert.Equal([40L, 41L], StarredList.Photos(list).Select(card => card.AttachmentID).ToList());
        }

        [Fact]
        public async Task An_empty_starred_list_reads_nothing()
        {
            var archive = Install(Builder());

            using var session = ArchiveSession.Open(archive, _settings);

            Assert.Empty(await session.ReadStarredAsync());
        }

        [Fact]
        public void A_long_snippet_is_cut_on_one_line()
        {
            var text = "line one\nline two " + new string('x', 200);

            var snippet = StarredList.Snippet(text);

            Assert.Equal(StarredList.SnippetLength + 1, snippet.Length);
            Assert.EndsWith("…", snippet, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', snippet);
            Assert.Equal("short", StarredList.Snippet("short"));
        }

        [Fact]
        public async Task Gallery_cells_carry_star_flags_and_follow_a_toggle()
        {
            var archive = Install(Builder());

            using var session = ArchiveSession.Open(archive, _settings);
            session.Stars.SetAttachment(2, 20, true);

            var gallery = await session.GetGalleryAsync();

            Assert.Equal([41L, 40L, 20L], gallery.Photos.Select(photo => photo.AttachmentID).ToList());
            Assert.True(gallery.Photos[2].IsStarred);
            Assert.False(gallery.Photos[0].IsStarred);

            session.Stars.SetAttachment(4, 41, true);
            gallery.RefreshStars(session.Stars);

            Assert.True(gallery.Photos[0].IsStarred);
            Assert.Same(gallery, await session.GetGalleryAsync());
        }

        [Fact]
        public void Stored_stars_that_are_not_numbers_are_ignored()
        {
            _settings.SetStarredMessages("hash", [3, 1, 3]);
            _store.Values["archive.hash.starredAttachments"] = "4:41,bad,7:x,:9,2:20";

            Assert.Equal([1, 3], _settings.GetStarredMessages("hash"));
            Assert.Equal([new StarredAttachment(2, 20), new StarredAttachment(4, 41)], _settings.GetStarredAttachments("hash"));

            _settings.SetStarredMessages("hash", []);
            Assert.False(_store.Values.ContainsKey("archive.hash.starredMessages"));
        }

        // ---------------------------------------------------------------------------------------
        // On this day.
        // ---------------------------------------------------------------------------------------

        private static DateOnly D(int year, int month, int day) => new(year, month, day);

        [Fact]
        public void Earlier_years_with_messages_on_today_s_day_and_month_match_oldest_first()
        {
            DateOnly[] days = [D(2016, 3, 14), D(2016, 3, 15), D(2019, 3, 14), D(2021, 3, 14), D(2021, 3, 13), D(2026, 3, 14), D(2027, 3, 14)];

            var years = OnThisDay.Find(days, D(2026, 3, 14));

            Assert.Equal([(2016, D(2016, 3, 14)), (2019, D(2019, 3, 14)), (2021, D(2021, 3, 14))], years.Select(y => (y.Year, y.Day)).ToList());
        }

        [Fact]
        public void Nothing_matches_when_no_earlier_year_has_the_day()
        {
            Assert.Empty(OnThisDay.Find([D(2025, 3, 15), D(2026, 3, 14)], D(2026, 3, 14)));
            Assert.Empty(OnThisDay.Find([], D(2026, 3, 14)));
        }

        [Fact]
        public void The_29th_of_February_shows_on_the_28th_in_a_year_without_one()
        {
            DateOnly[] days = [D(2020, 2, 29), D(2023, 2, 28), D(2024, 2, 28), D(2024, 2, 29)];

            // 2027 has no 29 February: 2020's 29th, 2023's 28th and 2024 (its 28th first) all match.
            Assert.Equal(
                [(2020, D(2020, 2, 29)), (2023, D(2023, 2, 28)), (2024, D(2024, 2, 28))],
                OnThisDay.Find(days, D(2027, 2, 28)).Select(y => (y.Year, y.Day)).ToList());

            // 2028 has one: on its 28th only the 28ths match, on its 29th only the 29ths.
            Assert.Equal([2023, 2024], OnThisDay.Find(days, D(2028, 2, 28)).Select(y => y.Year).ToList());
            Assert.Equal(
                [(2020, D(2020, 2, 29)), (2024, D(2024, 2, 29))],
                OnThisDay.Find(days, D(2028, 2, 29)).Select(y => (y.Year, y.Day)).ToList());

            // 1 March is never moved.
            Assert.Empty(OnThisDay.Find(days, D(2027, 3, 1)));
        }

        [Fact]
        public void The_card_opens_by_itself_once_a_day_and_the_icon_stays()
        {
            DateOnly[] days = [D(2020, 6, 1), D(2026, 6, 1)];
            var today = D(2026, 6, 1);

            var first = OnThisDay.For(_settings, "hash", days, today);
            Assert.True(first.ShowsIcon);
            Assert.True(first.OpensByItself);
            Assert.Equal("On this day: 2020", first.Text);

            first.MarkShown();

            var later = OnThisDay.For(_settings, "hash", days, today);
            Assert.True(later.ShowsIcon);
            Assert.False(later.OpensByItself);

            var tomorrow = OnThisDay.For(_settings, "hash", [.. days, D(2020, 6, 2)], D(2026, 6, 2));
            Assert.True(tomorrow.OpensByItself);

            // Per archive: another archive's card is not marked.
            Assert.True(OnThisDay.For(_settings, "other", days, today).OpensByItself);
        }

        [Fact]
        public void Turned_off_there_is_neither_card_nor_icon_and_nothing_found_shows_neither()
        {
            DateOnly[] days = [D(2020, 6, 1)];

            _settings.SetOnThisDayEnabled("hash", false);
            var off = OnThisDay.For(_settings, "hash", days, D(2026, 6, 1));
            Assert.False(off.ShowsIcon);
            Assert.False(off.OpensByItself);

            _settings.SetOnThisDayEnabled("hash", true);
            Assert.True(OnThisDay.For(_settings, "hash", days, D(2026, 6, 1)).ShowsIcon);
            Assert.False(_store.Values.ContainsKey("archive.hash.onThisDay"));

            var none = OnThisDay.For(_settings, "hash", days, D(2026, 6, 2));
            Assert.False(none.ShowsIcon);
            Assert.False(none.OpensByItself);
        }

        [Fact]
        public void The_session_finds_the_years_from_the_archive_s_days_and_remove_forgets_the_switch()
        {
            var builder = new ArchiveBuilder();
            builder.Add("Long ago", ArchiveBuilder.Other, new DateTime(2019, 7, 4, 10, 0, 0));
            builder.Add("Later", ArchiveBuilder.Other, new DateTime(2022, 7, 4, 23, 59, 0));
            builder.Add("Other day", ArchiveBuilder.Other, new DateTime(2023, 7, 5, 0, 0, 0));
            var archive = Install(builder);

            using (var session = ArchiveSession.Open(archive, _settings))
            {
                var onThisDay = session.OnThisDayFor(session.ReadDays(), D(2026, 7, 4));

                Assert.Equal([2019, 2022], onThisDay.Years.Select(year => year.Year).ToList());
                Assert.Equal(D(2022, 7, 4), onThisDay.Years[1].Day);

                session.OnThisDayEnabled = false;
                onThisDay.MarkShown();
                Assert.False(session.OnThisDayEnabled);
            }

            _library.Remove(archive, _settings);
            Assert.DoesNotContain(_store.Values.Keys, key => key.Contains("onThisDay", StringComparison.Ordinal));
        }

        [Fact]
        public void Today_is_the_phone_s_local_date()
        {
            var clock = new FixedClock(new DateTime(2026, 2, 28, 12, 0, 0, DateTimeKind.Utc));

            Assert.Equal(DateOnly.FromDateTime(new DateTime(2026, 2, 28, 12, 0, 0, DateTimeKind.Utc).ToLocalTime()), OnThisDay.TodayFrom(clock));
        }

        private static void CopyFolder(string from, string to)
        {
            Directory.CreateDirectory(to);

            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
    }
}
