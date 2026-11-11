using Chatcheology.Archive;
using Chatcheology.Mobile.Import;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// CH-71 step 2: a refreshed archive (same archive id, every earlier message unchanged) replaces the installed one and
    /// keeps all its settings; anything else with that id is refused; an archive installed before version 4 is adopted
    /// only when the owner answers Replace.
    /// </summary>
    public sealed class ArchiveRefreshTests : IDisposable
    {
        private const string Id = "6e0b3f5a-2c47-4d81-9a3e-5f7b1c0d2e94";
        private const string OtherId = "0d9c7e21-5b3a-4f60-8e4d-a2c6b1f3e578";

        private static readonly DateTime FirstImport = new(2026, 10, 22, 7, 30, 0, DateTimeKind.Utc);
        private static readonly DateTime SecondImport = new(2026, 11, 8, 18, 0, 0, DateTimeKind.Utc);

        private readonly TemporaryFolder _root = new();
        private readonly ArchiveLibrary _library;
        private readonly MemorySettings _store = new();
        private readonly ArchiveSettings _settings;
        private readonly FixedClock _clock = new(FirstImport);
        private int _sources;

        public ArchiveRefreshTests()
        {
            _library = new ArchiveLibrary(Path.Combine(_root.Path, "files"));
            _settings = new ArchiveSettings(_store);
        }

        public void Dispose() => _root.Dispose();

        // -------------------------------------------------------------------------------------
        // Synthetic archives: the earlier part, and later exports of the same conversation.
        // -------------------------------------------------------------------------------------

        /// <summary>The earlier messages: text, a system line, two photos (one a low-quality copy), an unmatched slot.</summary>
        private static void Earlier(ArchiveBuilder builder, string secondText = "Synthetic second line", ArchiveAttachmentState photoState = ArchiveAttachmentState.Restored, string? caption = null, bool? forwarded = null)
        {
            builder.Add("Synthetic hello", ArchiveBuilder.Local);
            builder.Add(secondText, ArchiveBuilder.Other, forwarded: forwarded);
            builder.Add("Synthetic system line", sender: null);
            builder.Media(new AttachmentSpec(501, photoState, Caption: caption), new AttachmentSpec(502, LowQualityOf: 501));
            builder.Media(new AttachmentSpec(503, ArchiveAttachmentState.Unmatched));
            builder.Add("Synthetic last earlier line", ArchiveBuilder.Local);
        }

        /// <summary>Newer messages from WhatsApp's message store, added at the end.</summary>
        private static void Newer(ArchiveBuilder builder, int count = 3)
        {
            builder.Source = ArchiveMessageSource.Msgstore;

            for (var index = 0; index < count; index++)
            {
                builder.Add("Synthetic newer line " + index, index % 2 == 0 ? ArchiveBuilder.Other : ArchiveBuilder.Local);
            }

            builder.Media(new AttachmentSpec(601));
        }

        private string Write(Action<ArchiveBuilder> messages, string? archiveId = Id, int? version = null)
        {
            var builder = new ArchiveBuilder { ArchiveId = archiveId ?? Id, FormatVersion = version };
            messages(builder);

            var folder = Path.Combine(_root.Path, "source-" + ++_sources);
            Directory.CreateDirectory(folder);

            return builder.Write(folder);
        }

        private ImportResult Import(string source, IImportQuestions? questions = null) =>
            new ArchiveImporter(_library, new FixedSpace(long.MaxValue), _settings, _clock).Import(new FolderSource(source), null, default, questions);

        private string[] StagingLeft() =>
            Directory.Exists(_library.StagingFolder) ? Directory.GetFileSystemEntries(_library.StagingFolder) : [];

        private InstalledArchive Installed(string installId) =>
            _library.ListArchives(_settings).Single(archive => archive.InstallId == installId);

        /// <summary>Stars, position, me, layers, filters, mutes, On this day and the noted-date posted day on an archive.</summary>
        private void SetEverything(string hash)
        {
            _settings.SetStarredMessages(hash, [2, 6]);
            _settings.SetStarredAttachments(hash, [new StarredAttachment(4, 501), new StarredAttachment(5, 503)]);
            _settings.SetPosition(hash, 5);
            _settings.SetMe(hash, ArchiveBuilder.Other);
            _settings.SetLastOpenedUtc(hash, new DateTime(2026, 11, 1, 20, 0, 0, DateTimeKind.Utc));
            _settings.SetOnThisDayEnabled(hash, false);
            _settings.SetOnThisDayShown(hash, new DateOnly(2026, 11, 1));
            _settings.SetLayerShown(hash, 7, true);
            _settings.SetMemoryFilter(hash, 7, "strong", false);
            _settings.SetNotedDateMuted(hash, 3, true);
            _settings.SetYearlyMemoryMuted(hash, 9, true);
            _settings.SetNotedDatesPostedThrough(hash, new DateOnly(2026, 11, 7));
        }

        private void AssertEverything(string hash)
        {
            Assert.Equal([2, 6], _settings.GetStarredMessages(hash));
            Assert.Equal([new StarredAttachment(4, 501), new StarredAttachment(5, 503)], _settings.GetStarredAttachments(hash));
            Assert.Equal(5, _settings.GetPosition(hash));
            Assert.Equal(ArchiveBuilder.Other, _settings.GetMe(hash));
            Assert.Equal(new DateTime(2026, 11, 1, 20, 0, 0, DateTimeKind.Utc), _settings.GetLastOpenedUtc(hash));
            Assert.False(_settings.GetOnThisDayEnabled(hash));
            Assert.Equal(new DateOnly(2026, 11, 1), _settings.GetOnThisDayShown(hash));
            Assert.True(_settings.GetLayerShown(hash, 7, shownByDefault: false));
            Assert.False(_settings.GetMemoryFilter(hash, 7, "strong"));
            Assert.Equal([3L], _settings.GetMutedNotedDates(hash));
            Assert.Equal([9L], _settings.GetMutedYearlyMemories(hash));
            Assert.Equal(new DateOnly(2026, 11, 7), _settings.GetNotedDatesPostedThrough(hash));
        }

        private Dictionary<string, string> KeysOf(string hash) =>
            _store.Values.Where(pair => pair.Key.StartsWith("archive." + hash + ".", StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value);

        // -------------------------------------------------------------------------------------
        // Refresh accepted.
        // -------------------------------------------------------------------------------------

        [Fact]
        public void A_refresh_with_messages_added_replaces_the_archive_and_keeps_every_setting_valid()
        {
            var first = Import(Write(b => Earlier(b)));
            Assert.Equal(ImportOutcome.Installed, first.Outcome);
            SetEverything(first.ContentsSha256!);

            _clock.Utc = SecondImport;
            var refreshed = Import(Write(builder => { Earlier(builder); Newer(builder); }));

            Assert.Equal(ImportOutcome.Refreshed, refreshed.Outcome);
            Assert.False(refreshed.Adopted);
            Assert.True(refreshed.ReplacedRemoved);
            Assert.Equal(first.InstallId, refreshed.ReplacedInstallId);
            Assert.Equal(4, refreshed.AddedMessages);
            Assert.Equal("The archive was updated with 4 new messages. Its stars and settings were kept.", refreshed.Message);

            // One archive installed: the new one; the old folder is gone and staging is empty.
            var installed = Assert.Single(_library.ListInstalled());
            Assert.Equal(refreshed.InstallId, Path.GetFileName(installed));
            Assert.Empty(StagingLeft());

            // Every setting moved to the new hash; the old hash keeps nothing.
            var hash = refreshed.ContentsSha256!;
            Assert.NotEqual(first.ContentsSha256, hash);
            AssertEverything(hash);
            Assert.Empty(KeysOf(first.ContentsSha256!));

            // Validation is the new copy's own, from this import, not the old one's.
            Assert.Equal((SecondImport, 6, 6), _settings.GetValidation(hash));

            // The stars still name the same messages and attachments in the new archive.
            using var reader = ArchiveReader.Open(installed);
            Assert.Equal(10, reader.ReadMessageCount());

            foreach (var sequence in _settings.GetStarredMessages(hash))
            {
                Assert.Equal(sequence, reader.ReadPageFrom(sequence, 1).Messages.Single().SequenceNumber);
            }

            foreach (var star in _settings.GetStarredAttachments(hash))
            {
                Assert.Contains(reader.ReadPageFrom(star.SequenceNumber, 1).Messages.Single().Attachments, a => a.AttachmentID == star.AttachmentID);
            }

            Assert.Equal("Synthetic second line", reader.ReadPageFrom(2, 1).Messages.Single().Text);
        }

        [Fact]
        public void A_refresh_keeps_the_saved_thumbnails_of_unchanged_attachments_and_drops_the_rest()
        {
            // CH-66 saved thumbnails: keyed by the archive's contents hash; a refresh moves them to the new hash.
            var first = Import(Write(b => Earlier(b)));
            var oldHash = first.ContentsSha256!;
            var photo = PhotoOf(first.InstallId!, 501);
            var store = _library.Thumbnails;
            var other = new string('c', 64);

            ThumbnailKey Key(string hash, long id, string sha) => new(hash, id, sha, 300, 300);

            Assert.True(store.Save(Key(oldHash, 501, photo), Jpeg));
            Assert.True(store.Save(Key(oldHash, 501, photo) with { Width = 200, Height = 200 }, Jpeg));
            Assert.True(store.Save(Key(oldHash, 502, other), Jpeg));   // that attachment's media is different now
            Assert.True(store.Save(Key(oldHash, 999, photo), Jpeg));   // no such attachment in the new copy

            var refreshed = Import(Write(builder => { Earlier(builder); Newer(builder); }));
            Assert.Equal(ImportOutcome.Refreshed, refreshed.Outcome);
            var newHash = refreshed.ContentsSha256!;

            Assert.NotNull(store.Find(Key(newHash, 501, photo)));
            Assert.NotNull(store.Find(Key(newHash, 501, photo) with { Width = 200, Height = 200 }));
            Assert.Null(store.Find(Key(newHash, 502, other)));
            Assert.Null(store.Find(Key(newHash, 999, photo)));
            Assert.False(Directory.Exists(Path.Combine(store.Root, oldHash)));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(store.Root, newHash)).Length);
        }

        [Fact]
        public void Removing_an_archive_deletes_its_saved_thumbnails_only()
        {
            var first = Import(Write(b => Earlier(b)));
            var second = Import(Write(b => Earlier(b, secondText: "Synthetic other line"), archiveId: OtherId));
            Assert.Equal(ImportOutcome.Installed, second.Outcome);

            var store = _library.Thumbnails;
            var firstKey = new ThumbnailKey(first.ContentsSha256!, 501, PhotoOf(first.InstallId!, 501), 300, 300);
            var secondKey = firstKey with { ContentsSha256 = second.ContentsSha256! };
            Assert.True(store.Save(firstKey, Jpeg));
            Assert.True(store.Save(secondKey, Jpeg));

            _library.Remove(Installed(first.InstallId!), _settings);

            Assert.Null(store.Find(firstKey));
            Assert.False(Directory.Exists(Path.Combine(store.Root, first.ContentsSha256!)));
            Assert.NotNull(store.Find(secondKey));
        }

        private static bool Jpeg(Stream stream)
        {
            stream.Write([0xFF, 0xD8, 0xFF, 0xD9]);
            return true;
        }

        private string PhotoOf(string installId, long attachmentID)
        {
            using var reader = ArchiveReader.Open(Installed(installId).Folder);

            return reader.ReadAllPages().SelectMany(p => p.Messages).SelectMany(m => m.Attachments)
                .Single(a => a.AttachmentID == attachmentID).MediaSha256!;
        }

        [Fact]
        public void A_refresh_with_no_new_messages_and_a_caption_or_forwarded_fact_added_to_an_earlier_message_is_still_accepted()
        {
            // Captions and forwarded facts are not in the digest: a later export may add them to earlier messages.
            var first = Import(Write(builder => Earlier(builder)));
            SetEverything(first.ContentsSha256!);

            var refreshed = Import(Write(builder => Earlier(builder, caption: "Synthetic caption found later", forwarded: true)));

            Assert.Equal(ImportOutcome.Refreshed, refreshed.Outcome);
            Assert.Equal(0, refreshed.AddedMessages);
            AssertEverything(refreshed.ContentsSha256!);
        }

        [Fact]
        public void Refreshes_chain_on_the_stored_digest_and_fall_back_to_reading_the_installed_archive_when_none_is_stored()
        {
            var first = Import(Write(b => Earlier(b)));
            var digest = _settings.GetMessageDigest(first.ContentsSha256!);
            Assert.NotNull(digest);
            Assert.Equal(6, digest.Count);
            Assert.Equal(6, digest.LastSequenceNumber);

            var second = Import(Write(builder => { Earlier(builder); Newer(builder, 2); }));
            Assert.Equal(ImportOutcome.Refreshed, second.Outcome);
            Assert.Equal(9, _settings.GetMessageDigest(second.ContentsSha256!)!.Count);

            // An archive installed by an earlier build of the app has no stored digest: it is read instead.
            _settings.SetMessageDigest(second.ContentsSha256!, null);
            var third = Import(Write(builder => { Earlier(builder); Newer(builder, 2); builder.Add("Synthetic third export line"); }));

            Assert.Equal(ImportOutcome.Refreshed, third.Outcome);
            Assert.Equal(1, third.AddedMessages);
            Assert.Single(_library.ListInstalled());
        }

        [Fact]
        public void The_digest_is_deterministic_and_its_stored_form_reads_back()
        {
            var one = Write(b => Earlier(b));
            var two = Write(b => Earlier(b));

            using var a = ArchiveReader.Open(one);
            using var b = ArchiveReader.Open(two);
            var digest = MessageDigest.Of(a);

            Assert.Equal(digest, MessageDigest.Of(b));
            Assert.Equal(digest, MessageDigest.Parse(digest.Format()));
            Assert.Null(MessageDigest.Parse("2:" + digest.Format()[2..]));
            Assert.Null(MessageDigest.Parse("1:6:6:not-a-hash"));
            Assert.Null(MessageDigest.Parse(null));

            // Through the last earlier sequence number, a longer archive's digest equals the shorter one's.
            var longer = Write(builder => { Earlier(builder); Newer(builder); });
            using var c = ArchiveReader.Open(longer);
            var (whole, through) = MessageDigest.Compute(c, digest.LastSequenceNumber);

            Assert.True(MessageDigest.Continues(digest, through));
            Assert.False(MessageDigest.Continues(digest, whole));
        }

        // -------------------------------------------------------------------------------------
        // Refresh refused: nothing changes.
        // -------------------------------------------------------------------------------------

        public static TheoryData<string> Changes => new() { "text", "removed", "attachment-state", "attachment-id", "low-quality", "sender", "time", "prepended", "older" };

        [Theory]
        [MemberData(nameof(Changes))]
        public void A_refresh_that_does_not_contain_every_earlier_message_unchanged_is_refused_and_nothing_changes(string change)
        {
            var first = Import(Write(builder => { Earlier(builder); if (change == "older") { Newer(builder); } }));
            SetEverything(first.ContentsSha256!);
            var before = new Dictionary<string, string>(_store.Values);
            var installedBefore = _library.ListInstalled();

            Action<ArchiveBuilder> changed = change switch
            {
                "text" => builder => { Earlier(builder, secondText: "Synthetic second line, edited"); Newer(builder); },
                "removed" => builder =>
                {
                    builder.Add("Synthetic hello", ArchiveBuilder.Local);
                    builder.Add("Synthetic system line", sender: null);
                    builder.Media(new AttachmentSpec(501), new AttachmentSpec(502, LowQualityOf: 501));
                    builder.Media(new AttachmentSpec(503, ArchiveAttachmentState.Unmatched));
                    builder.Add("Synthetic last earlier line", ArchiveBuilder.Local);
                    Newer(builder);
                },
                "attachment-state" => builder => { Earlier(builder, photoState: ArchiveAttachmentState.Missing); Newer(builder); },
                "attachment-id" => builder =>
                {
                    builder.Add("Synthetic hello", ArchiveBuilder.Local);
                    builder.Add("Synthetic second line", ArchiveBuilder.Other);
                    builder.Add("Synthetic system line", sender: null);
                    builder.Media(new AttachmentSpec(701), new AttachmentSpec(502, LowQualityOf: 701));
                    builder.Media(new AttachmentSpec(503, ArchiveAttachmentState.Unmatched));
                    builder.Add("Synthetic last earlier line", ArchiveBuilder.Local);
                    Newer(builder);
                },
                "low-quality" => builder =>
                {
                    builder.Add("Synthetic hello", ArchiveBuilder.Local);
                    builder.Add("Synthetic second line", ArchiveBuilder.Other);
                    builder.Add("Synthetic system line", sender: null);
                    builder.Media(new AttachmentSpec(501), new AttachmentSpec(502));
                    builder.Media(new AttachmentSpec(503, ArchiveAttachmentState.Unmatched));
                    builder.Add("Synthetic last earlier line", ArchiveBuilder.Local);
                    Newer(builder);
                },
                "sender" => builder =>
                {
                    builder.Add("Synthetic hello", ArchiveBuilder.Other);
                    builder.Add("Synthetic second line", ArchiveBuilder.Other);
                    builder.Add("Synthetic system line", sender: null);
                    builder.Media(new AttachmentSpec(501), new AttachmentSpec(502, LowQualityOf: 501));
                    builder.Media(new AttachmentSpec(503, ArchiveAttachmentState.Unmatched));
                    builder.Add("Synthetic last earlier line", ArchiveBuilder.Local);
                    Newer(builder);
                },
                "time" => builder =>
                {
                    builder.Add("Synthetic hello", ArchiveBuilder.Local, at: new DateTime(2025, 3, 1, 7, 59, 0));
                    builder.Add("Synthetic second line", ArchiveBuilder.Other, at: new DateTime(2025, 3, 1, 8, 1, 0));
                    builder.Add("Synthetic system line", sender: null);
                    builder.Media(new AttachmentSpec(501), new AttachmentSpec(502, LowQualityOf: 501));
                    builder.Media(new AttachmentSpec(503, ArchiveAttachmentState.Unmatched));
                    builder.Add("Synthetic last earlier line", ArchiveBuilder.Local);
                    Newer(builder);
                },
                "prepended" => builder =>
                {
                    // An earlier message added before the installed ones: every later sequence number shifts by one.
                    builder.Add("Synthetic message found before the start", ArchiveBuilder.Other, at: new DateTime(2025, 2, 28, 21, 0, 0));
                    builder.Add("Synthetic hello", ArchiveBuilder.Local, at: new DateTime(2025, 3, 1, 8, 0, 0));
                    builder.Add("Synthetic second line", ArchiveBuilder.Other);
                    builder.Add("Synthetic system line", sender: null);
                    builder.Media(new AttachmentSpec(501), new AttachmentSpec(502, LowQualityOf: 501));
                    builder.Media(new AttachmentSpec(503, ArchiveAttachmentState.Unmatched));
                    builder.Add("Synthetic last earlier line", ArchiveBuilder.Local);
                    Newer(builder);
                },
                _ => builder => Earlier(builder), // an older copy: fewer messages than installed
            };

            var result = Import(Write(changed));

            Assert.Equal(ImportOutcome.Refused, result.Outcome);
            Assert.Equal(ImportRefusal.RefreshMismatch, result.Refusal);
            Assert.EndsWith("Nothing was changed.", result.Message, StringComparison.Ordinal);
            Assert.Null(result.InstallId);

            Assert.Equal(installedBefore, _library.ListInstalled());
            Assert.Equal(before, _store.Values);
            Assert.Empty(StagingLeft());
        }

        [Fact]
        public void Two_installed_archives_with_the_same_id_make_a_refresh_ambiguous_and_it_is_refused()
        {
            // Possible only from a Debug install or a refresh cut short: both copies installed.
            foreach (var messages in new Action<ArchiveBuilder>[] { builder => Earlier(builder), builder => { Earlier(builder); Newer(builder, 1); } })
            {
                var staging = _library.CreateStagingFolder();
                var builder = new ArchiveBuilder { ArchiveId = Id };
                messages(builder);
                builder.Write(staging);
                Assert.True(_library.Install(staging).Installed);
            }

            var before = new Dictionary<string, string>(_store.Values);
            var result = Import(Write(builder => { Earlier(builder); Newer(builder, 5); }));

            Assert.Equal(ImportRefusal.RefreshAmbiguous, result.Refusal);
            Assert.Equal(2, _library.ListInstalled().Count);
            Assert.Equal(before, _store.Values);
            Assert.Empty(StagingLeft());
        }

        [Fact]
        public void A_different_archive_id_is_a_new_archive_installed_beside_the_first_with_no_settings_carried()
        {
            var first = Import(Write(b => Earlier(b)));
            SetEverything(first.ContentsSha256!);

            var other = Import(Write(builder => { Earlier(builder); Newer(builder); }, archiveId: OtherId));

            Assert.Equal(ImportOutcome.Installed, other.Outcome);
            Assert.Equal(2, _library.ListInstalled().Count);
            AssertEverything(first.ContentsSha256!);
            Assert.Empty(_settings.GetStarredMessages(other.ContentsSha256!));
            Assert.Null(_settings.GetPosition(other.ContentsSha256!));
        }

        [Fact]
        public void The_same_archive_again_is_still_already_installed_not_a_refresh()
        {
            var source = Write(b => Earlier(b));
            Import(source);

            var again = Import(source);

            Assert.Equal(ImportOutcome.AlreadyInstalled, again.Outcome);
            Assert.Single(_library.ListInstalled());
        }

        // -------------------------------------------------------------------------------------
        // Removing and forgetting still behave as documented.
        // -------------------------------------------------------------------------------------

        [Fact]
        public void After_a_refresh_removing_the_archive_keeps_only_its_stars_and_importing_it_again_brings_them_back()
        {
            var first = Import(Write(b => Earlier(b)));
            SetEverything(first.ContentsSha256!);

            var refreshedSource = Write(builder => { Earlier(builder); Newer(builder); });
            var refreshed = Import(refreshedSource);
            var hash = refreshed.ContentsSha256!;

            _library.Remove(Installed(refreshed.InstallId!), _settings);

            Assert.Empty(_library.ListInstalled());
            Assert.Equal([2, 6], _settings.GetStarredMessages(hash));
            Assert.Equal(2, _settings.GetStarredAttachments(hash).Count);
            Assert.Null(_settings.GetPosition(hash));
            Assert.Null(_settings.GetMe(hash));
            Assert.True(_settings.GetOnThisDayEnabled(hash));
            Assert.Null(_settings.GetValidation(hash));
            Assert.Null(_settings.GetMessageDigest(hash));
            Assert.Empty(_settings.GetMutedYearlyMemories(hash));

            var again = Import(refreshedSource);

            Assert.Equal(ImportOutcome.Installed, again.Outcome);
            Assert.Equal([2, 6], _settings.GetStarredMessages(hash));
            Assert.Null(_settings.GetPosition(hash));
        }

        [Fact]
        public void Forget_still_keeps_the_stars_and_purge_removes_everything()
        {
            const string hash = "aa";
            SetEverything(hash);
            _settings.SetValidation(hash, FirstImport, 6, 6);

            _settings.Forget(hash);

            Assert.Equal(["archive.aa.starredAttachments", "archive.aa.starredMessages"], KeysOf(hash).Keys.Order(StringComparer.Ordinal));

            _settings.Purge(hash);

            Assert.Empty(KeysOf(hash));
        }

        [Fact]
        public void Copying_settings_joins_stars_already_kept_under_the_new_hash_and_the_rest_takes_the_old_values()
        {
            // Stars left under the new hash by removing that same archive earlier (the remove rule) are not discarded.
            SetEverything("old");
            _settings.SetStarredMessages("new", [6, 9]);
            _settings.SetStarredAttachments("new", [new StarredAttachment(8, 801), new StarredAttachment(4, 501)]);
            _settings.SetPosition("new", 99);

            _settings.CopyArchive("old", "new");

            Assert.Equal([2, 6, 9], _settings.GetStarredMessages("new"));
            Assert.Equal(
                [new StarredAttachment(4, 501), new StarredAttachment(5, 503), new StarredAttachment(8, 801)],
                _settings.GetStarredAttachments("new"));
            Assert.Equal(5, _settings.GetPosition("new"));
            Assert.Equal(ArchiveBuilder.Other, _settings.GetMe("new"));
        }

        [Fact]
        public void The_carried_settings_are_flushed_before_any_folder_moves()
        {
            var first = Import(Write(b => Earlier(b)));
            SetEverything(first.ContentsSha256!);
            var oldFolder = Assert.Single(_library.ListInstalled());
            var source = Write(builder => { Earlier(builder); Newer(builder); });
            var newHash = ArchiveLibrary.ContentsSha256Of(source)!;

            var seen = new List<(IReadOnlyList<string> Installed, IReadOnlyList<int> Stars, bool Validated, bool Digest)>();
            _store.OnFlush = () => seen.Add((
                _library.ListInstalled(), _settings.GetStarredMessages(newHash), _settings.GetValidation(newHash) is not null,
                _settings.GetMessageDigest(newHash) is not null));

            var refreshed = Import(source);

            Assert.Equal(ImportOutcome.Refreshed, refreshed.Outcome);
            Assert.True(seen.Count >= 1);

            // The first flush: carried stars, validation and digest are written, and only the old copy is installed yet.
            var first_ = seen[0];
            Assert.Equal([oldFolder], first_.Installed);
            Assert.Equal([2, 6], first_.Stars);
            Assert.True(first_.Validated);
            Assert.True(first_.Digest);
        }

        [Fact]
        public void A_failed_flush_refuses_the_refresh_before_anything_moves()
        {
            var first = Import(Write(b => Earlier(b)));
            SetEverything(first.ContentsSha256!);
            var oldFolder = Assert.Single(_library.ListInstalled());
            _store.FlushSucceeds = false;

            var result = Import(Write(builder => { Earlier(builder); Newer(builder); }));

            Assert.Equal(ImportRefusal.CopyFailed, result.Refusal);
            Assert.Equal([oldFolder], _library.ListInstalled());
            AssertEverything(first.ContentsSha256!);
            Assert.Empty(StagingLeft());
        }

        [Fact]
        public void A_failure_of_any_kind_removing_the_old_copy_still_reports_the_update_with_the_old_copy_left()
        {
            var first = Import(Write(b => Earlier(b)));
            SetEverything(first.ContentsSha256!);
            var oldFolder = Assert.Single(_library.ListInstalled());

            // The old folder vanishes after the flush and before it is retired: Retire throws ArgumentException, not IO.
            _store.OnFlush = () =>
            {
                if (Directory.Exists(oldFolder))
                {
                    Directory.Delete(oldFolder, recursive: true);
                }
            };

            var result = Import(Write(builder => { Earlier(builder); Newer(builder); }));

            Assert.Equal(ImportOutcome.Refreshed, result.Outcome);
            Assert.False(result.ReplacedRemoved);
            Assert.Contains("The earlier copy could not be removed", result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Nothing was kept", result.Message, StringComparison.Ordinal);
            Assert.Equal(result.InstallId, Path.GetFileName(Assert.Single(_library.ListInstalled())));
            AssertEverything(result.ContentsSha256!);
        }

        [Fact]
        public void Copying_settings_is_exact_and_leaves_validation_and_digest_behind()
        {
            SetEverything("old");
            _settings.SetValidation("old", FirstImport, 6, 6);
            _settings.SetMessageDigest("old", new MessageDigest(6, 6, new string('a', 64)));
            _settings.SetPosition("new", 99);
            _settings.SetValidation("new", SecondImport, 5, 6);

            _settings.CopyArchive("old", "new");

            AssertEverything("new");
            Assert.Equal((SecondImport, 5, 6), _settings.GetValidation("new"));
            Assert.Null(_settings.GetMessageDigest("new"));

            // A setting the old archive does not have is not left over on the new one.
            _settings.SetPosition("new", 99);
            _settings.SetMe("old", null);
            _settings.CopyArchive("old", "new");
            Assert.Null(_settings.GetMe("new"));
            Assert.Equal(5, _settings.GetPosition("new"));
        }

        // -------------------------------------------------------------------------------------
        // Adoption of an archive installed before version 4.
        // -------------------------------------------------------------------------------------

        private sealed class Answers(AdoptionAnswer answer) : IImportQuestions
        {
            internal List<AdoptionQuestion> Asked { get; } = [];

            public AdoptionAnswer AskAdoption(AdoptionQuestion question, CancellationToken cancellationToken)
            {
                Asked.Add(question);
                return answer;
            }
        }

        private ImportResult InstallVersionThree(Action<ArchiveBuilder> messages)
        {
            var result = Import(Write(messages, version: 3));
            Assert.Equal(ImportOutcome.Installed, result.Outcome);

            using var reader = ArchiveReader.Open(Assert.Single(_library.ListInstalled()));
            Assert.Equal(3, reader.Info.FormatVersion);
            Assert.Null(reader.Info.ArchiveId);

            return result;
        }

        [Fact]
        public void Adoption_asks_and_Replace_moves_every_setting_to_the_new_archive_and_removes_the_old_install()
        {
            var old = InstallVersionThree(b => Earlier(b));
            SetEverything(old.ContentsSha256!);
            Assert.Null(_settings.GetMessageDigest(old.ContentsSha256!));

            var answers = new Answers(AdoptionAnswer.Replace);
            _clock.Utc = SecondImport;
            var result = Import(Write(builder => { Earlier(builder); Newer(builder); }), answers);

            var question = Assert.Single(answers.Asked);
            Assert.Equal("Alex and Sam", question.ConversationTitle);
            Assert.Equal("Replace “Alex and Sam” and keep its stars and settings?", question.Text);
            Assert.Equal(6, question.InstalledMessages);
            Assert.Equal(10, question.NewMessages);
            Assert.Contains("all 6 of its messages", question.Detail, StringComparison.Ordinal);

            Assert.Equal(ImportOutcome.Refreshed, result.Outcome);
            Assert.True(result.Adopted);
            Assert.Equal(old.InstallId, result.ReplacedInstallId);
            Assert.Equal(4, result.AddedMessages);

            var installed = Assert.Single(_library.ListInstalled());
            Assert.Equal(result.InstallId, Path.GetFileName(installed));
            AssertEverything(result.ContentsSha256!);
            Assert.Empty(KeysOf(old.ContentsSha256!));
            Assert.Equal((SecondImport, 6, 6), _settings.GetValidation(result.ContentsSha256!));
            Assert.NotNull(_settings.GetMessageDigest(result.ContentsSha256!));
            Assert.Empty(StagingLeft());

            // From now on it is refreshed by its archive id, with no question.
            var next = new Answers(AdoptionAnswer.Cancel);
            var later = Import(Write(builder => { Earlier(builder); Newer(builder); builder.Add("Synthetic next month"); }), next);

            Assert.Equal(ImportOutcome.Refreshed, later.Outcome);
            Assert.False(later.Adopted);
            Assert.Empty(next.Asked);
            AssertEverything(later.ContentsSha256!);
        }

        [Fact]
        public void Adoption_Import_as_new_installs_beside_the_old_one_and_carries_nothing()
        {
            var old = InstallVersionThree(b => Earlier(b));
            SetEverything(old.ContentsSha256!);

            var answers = new Answers(AdoptionAnswer.ImportAsNew);
            var result = Import(Write(builder => { Earlier(builder); Newer(builder); }), answers);

            Assert.Single(answers.Asked);
            Assert.Equal(ImportOutcome.Installed, result.Outcome);
            Assert.Equal(2, _library.ListInstalled().Count);
            AssertEverything(old.ContentsSha256!);
            Assert.Empty(_settings.GetStarredMessages(result.ContentsSha256!));
            Assert.Null(_settings.GetPosition(result.ContentsSha256!));
        }

        [Fact]
        public void Adoption_Cancel_keeps_nothing_and_changes_nothing()
        {
            var old = InstallVersionThree(b => Earlier(b));
            SetEverything(old.ContentsSha256!);
            var before = new Dictionary<string, string>(_store.Values);

            var result = Import(Write(builder => { Earlier(builder); Newer(builder); }), new Answers(AdoptionAnswer.Cancel));

            Assert.Equal(ImportOutcome.Cancelled, result.Outcome);
            Assert.Equal(old.InstallId, Path.GetFileName(Assert.Single(_library.ListInstalled())));
            Assert.Equal(before, _store.Values);
            Assert.Empty(StagingLeft());
        }

        [Fact]
        public void Adoption_is_never_automatic_without_the_question()
        {
            var old = InstallVersionThree(b => Earlier(b));
            SetEverything(old.ContentsSha256!);

            var result = Import(Write(builder => { Earlier(builder); Newer(builder); }), questions: null);

            Assert.Equal(ImportOutcome.Installed, result.Outcome);
            Assert.Equal(2, _library.ListInstalled().Count);
            AssertEverything(old.ContentsSha256!);
            Assert.Equal(0, result.UnmatchedOlderArchives);
            Assert.Equal("The archive was imported.", result.Message);
        }

        [Fact]
        public void An_old_archive_that_is_not_exactly_the_earlier_part_is_never_offered()
        {
            InstallVersionThree(builder => Earlier(builder, secondText: "Synthetic different second line"));

            var answers = new Answers(AdoptionAnswer.Replace);
            var result = Import(Write(builder => { Earlier(builder); Newer(builder); }), answers);

            Assert.Empty(answers.Asked);
            Assert.Equal(ImportOutcome.Installed, result.Outcome);
            Assert.Equal(2, _library.ListInstalled().Count);

            // Never silent: the outcome says no older archive matched, so no stars moved (a count, no title or text).
            Assert.Equal(1, result.UnmatchedOlderArchives);
            Assert.Equal(
                "The archive was imported as a new archive. It did not match the older archive on this phone, so no stars were moved. " +
                "Remove this copy before starring if that is wrong.",
                result.Message);

            // Two unmatched older archives are counted.
            InstallVersionThreeBeside(builder => builder.Add("Synthetic unrelated line"));
            var third = Import(Write(builder => { Earlier(builder); Newer(builder); builder.Add("Synthetic another"); }, archiveId: OtherId), answers);
            Assert.Equal(2, third.UnmatchedOlderArchives);
            Assert.Contains("did not match 2 older archives", third.Message, StringComparison.Ordinal);
        }

        private void InstallVersionThreeBeside(Action<ArchiveBuilder> messages) =>
            Assert.Equal(ImportOutcome.Installed, Import(Write(messages, version: 3)).Outcome);

        [Fact]
        public void An_installed_version_four_archive_with_another_id_is_never_offered_for_adoption()
        {
            Import(Write(b => Earlier(b), archiveId: OtherId));

            var answers = new Answers(AdoptionAnswer.Replace);
            var result = Import(Write(builder => { Earlier(builder); Newer(builder); }), answers);

            Assert.Empty(answers.Asked);
            Assert.Equal(ImportOutcome.Installed, result.Outcome);
            Assert.Equal(2, _library.ListInstalled().Count);
        }

        [Fact]
        public void A_version_three_import_is_installed_as_before_with_no_question_and_no_digest()
        {
            var answers = new Answers(AdoptionAnswer.Replace);
            Import(Write(b => Earlier(b), version: 3), answers);
            var result = Import(Write(builder => { Earlier(builder); builder.Add("Synthetic later line"); }, version: 3), answers);

            Assert.Equal(ImportOutcome.Installed, result.Outcome);
            Assert.Empty(answers.Asked);
            Assert.Equal(2, _library.ListInstalled().Count);
            Assert.Null(_settings.GetMessageDigest(result.ContentsSha256!));
        }

        [Theory]
        [InlineData("Replace", AdoptionAnswer.Replace)]
        [InlineData("Import as new", AdoptionAnswer.ImportAsNew)]
        [InlineData("Cancel", AdoptionAnswer.Cancel)]
        [InlineData(null, AdoptionAnswer.Cancel)]
        [InlineData("replace", AdoptionAnswer.Cancel)]
        public void The_dialog_button_maps_to_an_answer_and_anything_else_is_Cancel(string? button, AdoptionAnswer answer) =>
            Assert.Equal(answer, AdoptionQuestion.AnswerFor(button));

        [Fact]
        public void The_question_without_a_title_names_the_earlier_copy()
        {
            Assert.Equal("Replace the earlier copy and keep its stars and settings?", new AdoptionQuestion(null, 1, 2).Text);
            Assert.Equal("Replace the earlier copy and keep its stars and settings?", new AdoptionQuestion(" ", 1, 2).Text);
        }
    }
}
