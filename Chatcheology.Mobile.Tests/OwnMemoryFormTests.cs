using Chatcheology.Archive;
using Chatcheology.Mobile.Memories;
using Chatcheology.Mobile.OwnMemories;
using Chatcheology.Mobile.Presentation;
using Chatcheology.Mobile.Synthetic;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// CH-54 step 2: the add / edit form without a screen (Every year rule, photo order, links, every problem at once,
    /// unsaved changes), the open form's photo lease against a delete (step 1a gap 6), the second share root and the
    /// Debug synthetic own memories. Invented titles, dates and links only.
    /// </summary>
    public sealed class OwnMemoryFormTests : IDisposable
    {
        private const string One = "https://photos.example.org/one";
        private const string Two = "https://photos.example.org/two";

        private readonly TemporaryFolder _root = new();
        private readonly OwnMemoryStore _store;

        public OwnMemoryFormTests()
        {
            _store = new OwnMemoryStore(_root.Path);
        }

        public void Dispose() => _root.Dispose();

        private async Task<string> StorePhoto(byte seed)
        {
            var result = await new OwnPhotoIntake(_store.PhotosFolder).AddAsync(
                new MemoryStream(SyntheticImage.Png(8, 8, (seed, 1, 2), (3, seed, 4))));
            return result.FileName!;
        }

        // ---- Fields and rules ----

        [Fact]
        public void Every_year_is_only_for_Day_without_an_end_and_turns_off_with_a_reason()
        {
            var form = new OwnMemoryForm(new DateOnly(2026, 11, 6)) { Title = "Invented day" };
            Assert.True(form.CanEveryYear);
            Assert.Null(form.EveryYearReason);

            form.EveryYear = true;
            Assert.True(form.EveryYear);

            form.Precision = MemoryDatePrecision.Month;
            Assert.False(form.EveryYear);
            Assert.False(form.CanEveryYear);
            Assert.Equal("Only for a memory dated to the day.", form.EveryYearReason);

            form.EveryYear = true;
            Assert.False(form.EveryYear);

            form.Precision = MemoryDatePrecision.Day;
            form.EveryYear = true;
            form.End = new DateOnly(2026, 11, 8);
            Assert.False(form.EveryYear);
            Assert.Equal("Only for a memory without an end date.", form.EveryYearReason);

            form.End = null;
            Assert.True(form.CanEveryYear);
        }

        [Fact]
        public void Check_shows_every_problem_at_once_and_gives_no_draft()
        {
            var form = new OwnMemoryForm(new DateOnly(2026, 11, 6))
            {
                Title = "  ",
                End = new DateOnly(2026, 11, 1),
                Summary = new string('x', PersonalMemoryFieldRules.MaxSummaryLength + 1),
            };
            form.Links.Add(new OwnMemoryFormLink(One, null));
            form.Links.Add(new OwnMemoryFormLink("http://photos.example.org/x", null));
            form.Links.Add(new OwnMemoryFormLink(Two, new string('x', 61)));
            form.Links.Add(new OwnMemoryFormLink(null, "Label only"));

            var check = form.Check();

            Assert.False(check.IsValid);
            Assert.Null(check.Draft);
            Assert.Equal(
                [OwnMemoryField.Title, OwnMemoryField.End, OwnMemoryField.Summary, OwnMemoryField.Links],
                check.Errors.Keys.Order());
            Assert.Equal([1, 2, 3], check.LinkRowErrors.Keys.Order());
            Assert.Equal(ExternalLinkRules.UrlError("http://photos.example.org/x"), check.LinkRowErrors[1]);
            Assert.Equal(ExternalLinkRules.LabelError(new string('x', 61)), check.LinkRowErrors[2]);
            Assert.Equal("Some links need fixing.", check.Errors[OwnMemoryField.Links]);

            form.Start = null;
            Assert.Contains(OwnMemoryField.Start, form.Check().Errors.Keys);
        }

        [Fact]
        public void A_good_form_gives_the_draft_with_links_in_order_and_empty_rows_left_out()
        {
            var form = new OwnMemoryForm(new DateOnly(2026, 11, 6)) { Title = " Invented walk ", Summary = "  ", EveryYear = true };
            form.Links.Add(new OwnMemoryFormLink(Two, "Second first"));
            form.Links.Add(new OwnMemoryFormLink("  ", null));
            form.Links.Add(new OwnMemoryFormLink(" " + One + " ", "  "));

            var check = form.Check();

            Assert.True(check.IsValid);
            Assert.Empty(check.Errors);
            var draft = check.Draft!;
            Assert.Equal([Two, One], draft.Links.Select(link => link.Url));
            Assert.Equal(["Second first", null], draft.Links.Select(link => link.Label));
            Assert.True(draft.EveryYear);

            var saved = _store.Add(draft);
            Assert.Equal("Invented walk", saved.Title);
            Assert.Null(saved.Summary);
            Assert.Equal([Two, One], saved.Links.Select(link => link.Url));
        }

        [Fact]
        public void Eleven_links_are_refused_and_no_more_than_ten_rows_can_be_added()
        {
            var form = new OwnMemoryForm(new DateOnly(2026, 11, 6)) { Title = "Invented" };

            for (var i = 0; i < ExternalLinkRules.MaxLinksPerOwner; i++)
            {
                Assert.True(form.AddLink());
            }

            Assert.False(form.CanAddLink);
            Assert.False(form.AddLink());

            form.Links.Add(new OwnMemoryFormLink(One, null));
            for (var i = 0; i < form.Links.Count; i++)
            {
                form.Links[i].Url = One + i;
            }

            var check = form.Check();
            Assert.Equal(ExternalLinkRules.CountError(11), check.Errors[OwnMemoryField.Links]);
        }

        [Fact]
        public async Task Photos_keep_their_order_move_and_remove_and_the_first_is_the_cover()
        {
            var a = await StorePhoto(10);
            var b = await StorePhoto(20);
            var c = await StorePhoto(30);
            var form = new OwnMemoryForm(new DateOnly(2026, 11, 6)) { Title = "Invented photos" };

            Assert.True(form.AddPhoto(a, Path.Combine(_store.PhotosFolder, a)));
            Assert.True(form.AddPhoto(b, Path.Combine(_store.PhotosFolder, b)));
            Assert.True(form.AddPhoto(c, Path.Combine(_store.PhotosFolder, c)));
            Assert.False(form.AddPhoto(b, Path.Combine(_store.PhotosFolder, b)));

            Assert.True(form.MovePhoto(2, 0));
            Assert.False(form.MovePhoto(0, 3));
            form.Photos[0].Caption = "  Invented cover  ";
            form.RemovePhoto(2);

            var saved = _store.Add(form.Check().Draft!);

            Assert.Equal([c, a], saved.Photos.Select(photo => photo.FileName));
            Assert.Equal(["Invented cover", null], saved.Photos.Select(photo => photo.Caption));

            // The edit form opens with the stored order and captions.
            var edit = new OwnMemoryForm(saved);
            Assert.Equal([c, a], edit.Photos.Select(photo => photo.FileName));
            Assert.False(edit.IsNew);
            Assert.False(edit.HasChanges);
        }

        [Fact]
        public void Changes_are_noticed_and_undoing_them_is_no_change()
        {
            var form = new OwnMemoryForm(new DateOnly(2026, 11, 6));
            Assert.False(form.HasChanges);
            Assert.True(form.IsNew);

            form.Title = "Invented";
            Assert.True(form.HasChanges);
            form.Title = string.Empty;
            Assert.False(form.HasChanges);

            form.AddLink();
            Assert.True(form.HasChanges);
            form.RemoveLink(0);
            Assert.False(form.HasChanges);

            form.Precision = MemoryDatePrecision.Month;
            Assert.True(form.HasChanges);
        }

        // ---- Step 1a gap 6: an open form's photos against a delete ----

        [Fact]
        public async Task A_photo_held_by_an_open_form_survives_the_delete_of_the_memory_that_had_it_and_can_be_saved()
        {
            var shared = await StorePhoto(40);
            var first = _store.Add(new OwnMemoryDraft
            {
                Title = "Invented first",
                Start = new DateOnly(2025, 3, 1),
                Precision = MemoryDatePrecision.Day,
                Photos = [new OwnPhotoDraft(shared, null)],
            });

            // The form adds the same photo (the intake says AlreadyStored) and holds it.
            var again = await new OwnPhotoIntake(_store.PhotosFolder).AddAsync(new MemoryStream(SyntheticImage.Png(8, 8, (40, 1, 2), (3, 40, 4))));
            Assert.Equal(OwnPhotoStatus.AlreadyStored, again.Status);

            using (var lease = _store.LeasePhotos())
            {
                Assert.True(lease.TryHold(shared));
                var form = new OwnMemoryForm(new DateOnly(2025, 4, 1)) { Title = "Invented second" };
                form.AddPhoto(shared, Path.Combine(_store.PhotosFolder, shared));

                // Meanwhile the first memory is deleted, and a sweep runs: the held file stays.
                Assert.True(_store.Delete(first.Id));
                _store.SweepPhotos(DateTime.UtcNow.AddDays(30));
                Assert.True(File.Exists(Path.Combine(_store.PhotosFolder, shared)));

                var saved = _store.Add(form.Check().Draft!);
                Assert.Equal([shared], saved.Photos.Select(photo => photo.FileName));
            }

            // Without the lease the delete takes an unused file, and a form then cannot save it.
            var loose = await StorePhoto(50);
            var third = _store.Add(new OwnMemoryDraft
            {
                Title = "Invented third",
                Start = new DateOnly(2025, 3, 1),
                Precision = MemoryDatePrecision.Day,
                Photos = [new OwnPhotoDraft(loose, null)],
            });
            Assert.True(_store.Delete(third.Id));
            Assert.False(File.Exists(Path.Combine(_store.PhotosFolder, loose)));

            var refused = Assert.Throws<OwnMemoryValidationException>(() => _store.Add(new OwnMemoryDraft
            {
                Title = "Invented fourth",
                Start = new DateOnly(2025, 3, 1),
                Precision = MemoryDatePrecision.Day,
                Photos = [new OwnPhotoDraft(loose, null)],
            }));
            Assert.Contains(OwnMemoryField.Photos, refused.Errors.Keys);
        }

        [Fact]
        public async Task A_released_lease_lets_an_unused_photo_go_and_Delete_all_keeps_a_held_one_until_then()
        {
            var held = await StorePhoto(60);
            var lease = _store.LeasePhotos();
            Assert.True(lease.TryHold(held));
            Assert.False(lease.TryHold("not a photo name"));

            _store.DeleteAll();
            Assert.True(File.Exists(Path.Combine(_store.PhotosFolder, held)));

            lease.Dispose();
            Assert.False(lease.TryHold(held));

            // Old and unused, no longer held: swept.
            File.SetLastWriteTimeUtc(Path.Combine(_store.PhotosFolder, held), DateTime.UtcNow.AddDays(-2));
            _store.SweepPhotos(DateTime.UtcNow);
            Assert.False(File.Exists(Path.Combine(_store.PhotosFolder, held)));

            // A file already gone cannot be held: add it again.
            Assert.False(_store.LeasePhotos().TryHold(held));
        }

        [Fact]
        public void A_refused_photo_gets_a_plain_message_without_a_name_and_the_location_case_says_only_it_couldnt_be_added()
        {
            Assert.Null(OwnPhotoIntake.MessageFor(OwnPhotoStatus.Stored));
            Assert.Null(OwnPhotoIntake.MessageFor(OwnPhotoStatus.AlreadyStored));
            Assert.Contains("20 MB", OwnPhotoIntake.MessageFor(OwnPhotoStatus.TooLarge), StringComparison.Ordinal);
            Assert.StartsWith("This photo couldn't be added", OwnPhotoIntake.MessageFor(OwnPhotoStatus.NotSupported), StringComparison.Ordinal);
            Assert.Equal("This photo couldn't be added.", OwnPhotoIntake.MessageFor(OwnPhotoStatus.HasLocation));
        }

        [Fact]
        public void Screen_readers_hear_mine_on_an_own_row_only()
        {
            var own = new MemoryListItem(new Mobile.Conversation.MemoryKey(1, null, Mobile.Conversation.MemoryOwner.Own), "Invented walk", "d", "x", null, false, default)
            {
                Line = "20 March 2025",
                MutedLine = "My memories",
            };
            var archive = own with { Key = new Mobile.Conversation.MemoryKey(1, null) };

            Assert.Equal("Invented walk, mine, 20 March 2025, My memories", MemoryWording.ListRowScreenReaderText(own));
            Assert.Equal("Invented walk, 20 March 2025, My memories", MemoryWording.ListRowScreenReaderText(archive));
            Assert.Equal("Mine", MemoryWording.MineMark);
        }

        // ---- Share roots ----

        [Fact]
        public void Share_accepts_the_archives_and_the_own_photos_folder_and_nothing_else()
        {
            var files = Path.Combine(_root.Path, "files");
            var archives = Path.Combine(files, "archives");
            var own = Path.Combine(files, "own", "photos");
            string[] roots = [archives, own];

            Assert.True(PhotoShare.IsInsideAny(Path.Combine(archives, "a1", "media", "x.jpg"), roots));
            Assert.True(PhotoShare.IsInsideAny(Path.Combine(own, new string('a', 64) + ".jpg"), roots));
            Assert.False(PhotoShare.IsInsideAny(Path.Combine(files, "own", "own-memories.db"), roots));
            Assert.False(PhotoShare.IsInsideAny(Path.Combine(own, "..", "own-memories.db"), roots));
            Assert.False(PhotoShare.IsInsideAny(own, roots));
            Assert.False(PhotoShare.IsInsideAny(Path.Combine(files, "own", "photos-other", "x.jpg"), roots));
        }

        // ---- Debug synthetic ----

        [Fact]
        public async Task The_Debug_synthetic_own_memories_are_one_yearly_one_with_photos_without_location_and_a_duplicate()
        {
            var today = new DateOnly(2026, 11, 6);

            Assert.Equal(3, await SyntheticOwnMemories.SeedAsync(_store, today));
            Assert.Equal(0, await SyntheticOwnMemories.SeedAsync(_store, today));

            var memories = _store.List();
            Assert.Equal(3, memories.Count);

            var yearly = Assert.Single(memories, memory => memory.EveryYear);
            Assert.Equal(SyntheticOwnMemories.YearlyTitle, yearly.Title);
            Assert.Equal((11, 6), (yearly.Start.Month, yearly.Start.Day));

            var photos = memories.Single(memory => memory.Title == SyntheticOwnMemories.PhotosTitle);
            Assert.Equal(2, photos.Photos.Count);
            Assert.All(photos.Photos, photo => Assert.False(PhotoShare.HasGpsBlock(File.ReadAllBytes(photo.Path))));
            Assert.Single(photos.Links);

            Assert.Single(memories, memory => memory.Title == SyntheticOwnMemories.DuplicateTitle && memory.Start == new DateOnly(2016, 3, 14));

            // 29 February falls back to the 28th in the yearly memory's common year.
            using var other = new TemporaryFolder();
            var leap = new OwnMemoryStore(other.Path);
            await SyntheticOwnMemories.SeedAsync(leap, new DateOnly(2028, 2, 29));
            Assert.Equal(new DateOnly(2023, 2, 28), leap.List().Single(memory => memory.EveryYear).Start);
        }
    }
}
