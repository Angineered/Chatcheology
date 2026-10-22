using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Synthetic;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// The installed archives, the opening rule, and an opened archive: position, "me", day jumps.
    /// </summary>
    public sealed class OpeningAndSessionTests : IDisposable
    {
        private static readonly DateTime Monday = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

        private readonly TemporaryFolder _root = new();
        private readonly ArchiveLibrary _library;
        private readonly MemorySettings _store = new();
        private readonly ArchiveSettings _settings;

        public OpeningAndSessionTests()
        {
            _library = new ArchiveLibrary(_root.Path);
            _settings = new ArchiveSettings(_store);
        }

        public void Dispose() => _root.Dispose();

        /// <summary>Installs an archive written by <paramref name="write"/> and returns it as listed.</summary>
        private InstalledArchive Install(Action<string> write)
        {
            var staging = _library.CreateStagingFolder();
            write(staging);

            var outcome = _library.Install(staging);
            Assert.True(outcome.Installed);

            return _library.ListArchives(_settings).Single(archive => archive.Folder == outcome.InstalledFolder);
        }

        private InstalledArchive InstallText(int messages, string title = "Alex and Sam", Func<int, DateTime>? at = null)
        {
            var builder = new ArchiveBuilder(title);

            for (var i = 1; i <= messages; i++)
            {
                builder.Add("Message " + i, i % 2 == 0 ? ArchiveBuilder.Local : ArchiveBuilder.Other, at?.Invoke(i));
            }

            return Install(folder => builder.Write(folder));
        }

        private static InstalledArchive Listed(string id, DateTime? lastOpened, DateTime? created) =>
            new(id, "folder-" + id, "hash-" + id, "Title " + id, created, 10, lastOpened, IsReadable: true);

        // ---------------------------------------------------------------------------------------
        // Opening rule.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void No_archive_shows_the_start_screen()
        {
            var opening = OpeningRule.Decide(_library.ListArchives(_settings));

            Assert.Equal(StartScreen.NoArchive, opening.Screen);
            Assert.Null(opening.Archive);
            Assert.Empty(opening.Archives);
        }

        [Fact]
        public void Exactly_one_archive_opens_its_conversation_directly()
        {
            var archive = InstallText(5);

            var opening = OpeningRule.Decide(_library.ListArchives(_settings));

            Assert.Equal(StartScreen.OpenConversation, opening.Screen);
            Assert.Equal(archive, opening.Archive);
        }

        [Fact]
        public void Several_archives_are_listed_last_opened_first_then_never_opened_newest_first()
        {
            var archives = new[]
            {
                Listed("a", null, Monday.AddDays(-30)),
                Listed("b", Monday.AddDays(-1), Monday.AddDays(-60)),
                Listed("c", null, Monday.AddDays(-2)),
                Listed("d", Monday, Monday.AddDays(-90)),
            };

            var opening = OpeningRule.Decide(archives);

            Assert.Equal(StartScreen.ChooseArchive, opening.Screen);
            Assert.Null(opening.Archive);
            Assert.Equal(["d", "b", "c", "a"], opening.Archives.Select(archive => archive.Id));
        }

        [Fact]
        public void The_list_shows_title_created_date_message_count_and_last_opened()
        {
            var archive = InstallText(7, title: "Alex and Sam (two)");

            using (ArchiveSession.Open(archive, _settings, new FixedClock(Monday)))
            {
            }

            var listed = Assert.Single(_library.ListArchives(_settings));

            Assert.Equal("Alex and Sam (two)", listed.ConversationTitle);
            Assert.Equal(new DateTime(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc), listed.CreatedUtc);
            Assert.Equal(7, listed.MessageCount);
            Assert.Equal(Monday, listed.LastOpenedUtc);
            Assert.True(listed.IsReadable);
            Assert.Equal(ArchiveLibrary.ContentsSha256Of(listed.Folder), listed.ContentsSha256);
        }

        [Fact]
        public void An_archive_that_cannot_be_read_is_still_listed_so_it_can_be_removed()
        {
            var archive = InstallText(3);
            File.WriteAllText(Path.Combine(archive.Folder, ArchiveFormat.DatabaseFileName), "not a database");

            var listed = Assert.Single(_library.ListArchives(_settings));

            Assert.False(listed.IsReadable);
            Assert.Null(listed.ConversationTitle);

            _library.Remove(listed, _settings);

            Assert.Empty(_library.ListInstalled());
        }

        [Fact]
        public void Removing_deletes_only_the_apps_copy_and_forgets_its_state()
        {
            var source = Path.Combine(_root.Path, "source");
            Directory.CreateDirectory(source);
            new ArchiveBuilder().Write(source);

            var archive = Install(staging =>
            {
                foreach (var relative in ArchiveContents.RelativeFiles(source))
                {
                    var target = ArchiveContents.FullPath(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(ArchiveContents.FullPath(source, relative), target);
                }
            });

            _settings.SetPosition(archive.ContentsSha256!, 3);
            _settings.SetMe(archive.ContentsSha256!, ArchiveBuilder.Other);

            _library.Remove(archive, _settings);

            Assert.False(Directory.Exists(archive.Folder));
            Assert.Empty(_store.Values);
            Assert.True(ArchiveContents.Check(source).Ok);
        }

        [Fact]
        public void Only_an_installed_archive_folder_can_be_removed()
        {
            var elsewhere = Listed("x", null, null) with { Folder = _root.Path };

            Assert.Throws<ArgumentException>(() => _library.Remove(elsewhere, _settings));
            Assert.True(Directory.Exists(_root.Path));
        }

        // ---------------------------------------------------------------------------------------
        // Empty archives and archives without media.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void An_archive_with_no_messages_opens_to_an_empty_state_without_date_picker_or_search()
        {
            var archive = InstallText(0);

            using var session = ArchiveSession.Open(archive, _settings);
            var timeline = session.CreateTimeline();
            var opened = session.OpenTimeline(timeline);

            Assert.True(archive.IsEmpty);
            Assert.True(session.IsEmpty);
            Assert.False(session.CanPickDate);
            Assert.False(session.CanSearch);
            Assert.Equal(new OpenedAt(-1, AtLatest: true), opened);
            Assert.Empty(timeline.Rows);
            Assert.Empty(session.ReadDays());
        }

        [Fact]
        public void An_archive_with_no_media_opens_with_plain_bubbles()
        {
            var archive = InstallText(20);

            using var session = ArchiveSession.Open(archive, _settings);
            var timeline = session.CreateTimeline();
            session.OpenTimeline(timeline);

            var messages = timeline.Rows.OfType<MessageRow>().ToList();

            Assert.Equal(20, messages.Count);
            Assert.All(messages, message => Assert.Empty(message.Attachments));
            Assert.True(session.CanSearch);
        }

        // ---------------------------------------------------------------------------------------
        // Position.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void The_first_open_is_at_the_newest_messages()
        {
            var archive = InstallText(400);

            using var session = ArchiveSession.Open(archive, _settings);
            var timeline = session.CreateTimeline();
            var opened = session.OpenTimeline(timeline);

            Assert.True(opened.AtLatest);
            Assert.Equal(timeline.Rows.Count - 1, opened.RowIndex);
            Assert.Equal(400, Assert.IsType<MessageRow>(timeline.Rows[opened.RowIndex]).SequenceNumber);
            Assert.Null(session.Position);
        }

        [Fact]
        public void A_stored_position_reopens_with_a_page_before_and_a_page_from_it()
        {
            var archive = InstallText(1000);

            using (var first = ArchiveSession.Open(archive, _settings))
            {
                first.SavePosition(500);
            }

            using var session = ArchiveSession.Open(archive, _settings);
            var timeline = session.CreateTimeline();
            var opened = session.OpenTimeline(timeline);

            Assert.False(opened.AtLatest);
            Assert.Equal(500, Assert.IsType<MessageRow>(timeline.Rows[opened.RowIndex]).SequenceNumber);

            var loaded = timeline.Rows.OfType<MessageRow>().Select(message => message.SequenceNumber).ToList();
            Assert.Equal(Enumerable.Range(350, 300), loaded);
            Assert.True(timeline.HasEarlier);
            Assert.True(timeline.HasLater);
        }

        [Fact]
        public void A_stored_position_on_a_hidden_message_opens_at_the_next_shown_message()
        {
            // Message 3 has no text and only a hidden low-quality copy: it gets no row.
            var builder = new ArchiveBuilder();
            builder.Add("One");
            builder.Media(new AttachmentSpec(1));
            builder.Media(new AttachmentSpec(2, LowQualityOf: 1));
            builder.Add("Four");
            builder.Add("Five");

            var archive = Install(folder => builder.Write(folder));
            _settings.SetPosition(archive.ContentsSha256!, 3);

            using var session = ArchiveSession.Open(archive, _settings);
            var timeline = session.CreateTimeline();
            var opened = session.OpenTimeline(timeline);

            Assert.DoesNotContain(timeline.Rows.OfType<MessageRow>(), message => message.SequenceNumber == 3);
            Assert.False(opened.AtLatest);
            Assert.Equal(4, Assert.IsType<MessageRow>(timeline.Rows[opened.RowIndex]).SequenceNumber);
        }

        [Fact]
        public void Opening_a_conversation_loads_its_timeline_where_the_reader_last_was()
        {
            var archive = InstallText(1000);
            _settings.SetPosition(archive.ContentsSha256!, 500);

            var opened = ArchiveSession.OpenConversation(archive, _settings);

            using (opened.Session)
            {
                Assert.False(opened.At.AtLatest);
                Assert.Equal(500, Assert.IsType<MessageRow>(opened.Timeline.Rows[opened.At.RowIndex]).SequenceNumber);
                Assert.Equal(opened.Session.Me, opened.Timeline.LocalParticipantID);
            }
        }

        [Fact]
        public void Opening_a_conversation_that_cannot_be_read_leaves_nothing_open()
        {
            var archive = InstallText(10);
            var broken = archive with { ContentsSha256 = null };

            Assert.Throws<ArgumentException>(() => ArchiveSession.OpenConversation(broken, _settings));

            // Nothing holds the database: the archive can be removed straight away.
            _library.Remove(archive, _settings);
            Assert.Empty(_library.ListInstalled());
        }

        [Fact]
        public void A_stored_position_past_the_end_opens_at_the_newest_messages()
        {
            var archive = InstallText(10);
            _settings.SetPosition(archive.ContentsSha256!, 99);

            using var session = ArchiveSession.Open(archive, _settings);
            var timeline = session.CreateTimeline();

            var opened = session.OpenTimeline(timeline);

            Assert.True(opened.AtLatest);
            Assert.Equal(10, Assert.IsType<MessageRow>(timeline.Rows[opened.RowIndex]).SequenceNumber);
        }

        [Fact]
        public void The_position_is_kept_per_archive()
        {
            var one = InstallText(300, title: "One");
            var two = InstallText(301, title: "Two");

            using (var session = ArchiveSession.Open(one, _settings))
            {
                session.SavePosition(42);
            }

            using var other = ArchiveSession.Open(two, _settings);

            Assert.Null(other.Position);
            Assert.Equal(42, _settings.GetPosition(one.ContentsSha256!));
        }

        [Fact]
        public void A_day_jump_opens_at_the_first_message_on_or_after_the_day()
        {
            // Days 1, 2 and 4 have messages; day 3 has none.
            var archive = InstallText(600, at: s => TestArchives.OnDay(s <= 200 ? 1 : s <= 400 ? 2 : 4, s));

            using var session = ArchiveSession.Open(archive, _settings);
            var timeline = session.CreateTimeline();

            Assert.Equal([new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 2), new DateOnly(2025, 3, 4)], session.ReadDays());

            var index = timeline.OpenAtDay(new DateOnly(2025, 3, 2));
            Assert.Equal(201, Assert.IsType<MessageRow>(timeline.Rows[index]).SequenceNumber);
            Assert.IsType<DateSeparatorRow>(timeline.Rows[index - 1]);

            index = timeline.OpenAtDay(new DateOnly(2025, 3, 3));
            Assert.Equal(401, Assert.IsType<MessageRow>(timeline.Rows[index]).SequenceNumber);

            Assert.Equal(-1, timeline.OpenAtDay(new DateOnly(2025, 3, 5)));
        }

        // ---------------------------------------------------------------------------------------
        // Me.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Me_defaults_to_the_archives_local_participant()
        {
            var archive = InstallText(4);

            using var session = ArchiveSession.Open(archive, _settings);
            var timeline = session.CreateTimeline();
            session.OpenTimeline(timeline);

            Assert.Equal(ArchiveBuilder.Local, session.DefaultMe);
            Assert.Equal(ArchiveBuilder.Local, session.Me);
            Assert.False(session.IsMeChanged);
            Assert.Equal([false, true, false, true], timeline.Rows.OfType<MessageRow>().Select(m => m.IsMine));
        }

        [Fact]
        public void A_changed_me_is_remembered_for_that_archive_only_and_flips_the_bubbles()
        {
            var one = InstallText(4, title: "One");
            var two = InstallText(5, title: "Two");

            using (var session = ArchiveSession.Open(one, _settings))
            {
                session.SetMe(ArchiveBuilder.Other);
            }

            using (var session = ArchiveSession.Open(one, _settings))
            {
                var timeline = session.CreateTimeline();
                session.OpenTimeline(timeline);

                Assert.Equal(ArchiveBuilder.Other, session.Me);
                Assert.True(session.IsMeChanged);
                Assert.Equal([true, false, true, false], timeline.Rows.OfType<MessageRow>().Select(m => m.IsMine));
            }

            using (var session = ArchiveSession.Open(two, _settings))
            {
                Assert.Equal(ArchiveBuilder.Local, session.Me);
            }
        }

        [Fact]
        public void Choosing_the_archives_own_me_again_returns_to_the_default()
        {
            var archive = InstallText(4);

            using var session = ArchiveSession.Open(archive, _settings);
            session.SetMe(ArchiveBuilder.Other);
            session.SetMe(ArchiveBuilder.Local);

            Assert.False(session.IsMeChanged);
            Assert.Null(_settings.GetMe(archive.ContentsSha256!));
        }

        [Fact]
        public void Only_a_participant_can_be_me_and_a_stored_stranger_is_ignored()
        {
            var archive = InstallText(4);

            using var session = ArchiveSession.Open(archive, _settings);

            Assert.Throws<ArgumentException>(() => session.SetMe(99));

            _settings.SetMe(archive.ContentsSha256!, 99);
            Assert.Equal(ArchiveBuilder.Local, session.Me);
        }

        [Fact]
        public void A_re_exported_archive_has_a_new_identity_and_starts_from_its_own_me()
        {
            var original = InstallText(4, title: "Alex and Sam");
            var reExported = InstallText(5, title: "Alex and Sam");

            Assert.NotEqual(original.ContentsSha256, reExported.ContentsSha256);

            using (var session = ArchiveSession.Open(original, _settings))
            {
                session.SetMe(ArchiveBuilder.Other);
            }

            using var again = ArchiveSession.Open(reExported, _settings);

            Assert.Equal(ArchiveBuilder.Local, again.Me);
        }

        [Fact]
        public void The_synthetic_archive_opens_through_a_session_at_its_newest_page()
        {
            var archive = Install(folder => SyntheticArchive.Write(folder, new SyntheticArchiveOptions(Messages: 600, Days: 20, Images: 6, Missing: 2, Unmatched: 2)));

            using var session = ArchiveSession.Open(archive, _settings);
            var timeline = session.CreateTimeline();
            var opened = session.OpenTimeline(timeline);

            Assert.Equal(SyntheticArchive.Title, session.Title);
            Assert.Equal(600, Assert.IsType<MessageRow>(timeline.Rows[opened.RowIndex]).SequenceNumber);
        }
    }
}
