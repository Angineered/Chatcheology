using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chatcheology.Archive;
using Chatcheology.Mobile.OwnMemories;
using Chatcheology.Mobile.Presentation;
using Chatcheology.Mobile.Synthetic;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// CH-54 step 3: Save to a file / Load from a file. A round trip keeps everything; a load never overwrites; every way a
    /// file can be refused changes nothing and leaves no stage behind. Synthetic data only; coordinates are invented.
    /// </summary>
    public sealed class OwnMemoryFileTests : IDisposable
    {
        private static readonly DateTime Now = new(2026, 11, 7, 10, 30, 0, DateTimeKind.Utc);

        private readonly TemporaryFolder _root = new();
        private readonly OwnMemoryStore _source;
        private readonly OwnMemoryStore _target;

        public OwnMemoryFileTests()
        {
            _source = new OwnMemoryStore(Path.Combine(_root.Path, "source"));
            _target = new OwnMemoryStore(Path.Combine(_root.Path, "target"));
        }

        public void Dispose() => _root.Dispose();

        // ---------------------------------------------------------------- Round trip

        [Fact]
        public async Task A_saved_file_loads_into_an_empty_store_as_identical_memories_mutes_and_photo_bytes()
        {
            var (walk, yearly, month) = await Seed(_source);
            Assert.True(_source.Settings.SetMuted(yearly.Id, true));

            var file = Save(_source, out var saved);
            Assert.Equal(new OwnMemorySaveResult(3, 3, 0), saved);
            Assert.Equal("Saved 3 memories and 3 photos.", OwnMemoryFile.MessageFor(saved));

            var result = OwnMemoryFile.Load(_target, new MemoryStream(file));

            Assert.Equal(new OwnMemoryLoadResult(3, 0, 0, 0, 0, 3), result);
            Assert.Equal("Added 3. Already here: 0. Already here but different (yours kept): 0.", OwnMemoryFile.MessageFor(result));

            var before = _source.List();
            var after = _target.List();
            Assert.Equal(before.Select(Fields), after.Select(Fields));

            foreach (var (a, b) in before.Zip(after))
            {
                Assert.Equal(a.Photos.Select(p => (p.FileName, p.Caption)), b.Photos.Select(p => (p.FileName, p.Caption)));
                Assert.Equal(a.Links, b.Links);

                foreach (var (pa, pb) in a.Photos.Zip(b.Photos))
                {
                    Assert.Equal(File.ReadAllBytes(pa.Path), File.ReadAllBytes(pb.Path));
                }
            }

            Assert.Equal(new HashSet<Guid> { yearly.Id }, _target.Settings.Muted());
            Assert.Equal(walk.Id, after.Single(m => m.Title == walk.Title).Id);
            Assert.Equal(MemoryDatePrecision.Month, after.Single(m => m.Id == month.Id).Precision);
            AssertNoStage(_target);
        }

        [Fact]
        public async Task The_file_holds_exactly_the_JSON_and_the_photos_with_no_path_device_row_id_or_location()
        {
            await Seed(_source);
            var file = Save(_source, out _);

            using var zip = new ZipArchive(new MemoryStream(file), ZipArchiveMode.Read);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Equal(OwnMemoryFile.JsonName, names[^1]);
            Assert.All(names.Take(names.Count - 1), name => Assert.True(OwnMemoryFile.IsPhotoEntryName(name)));
            Assert.All(zip.Entries, e => Assert.Equal(new DateTime(1980, 1, 1), e.LastWriteTime.DateTime));

            foreach (var entry in zip.Entries.Where(e => e.FullName != OwnMemoryFile.JsonName))
            {
                using var stream = entry.Open();
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                var bytes = copy.ToArray();
                var format = PhotoShare.Sniff(bytes);
                Assert.False(PhotoShare.HasGpsBlock(bytes));
                Assert.False(PhotoShare.MayStillCarryLocation(new MemoryStream(bytes), format, format == PhotoFormat.Jpeg ? PhotoShareMethod.RemoveLocation : PhotoShareMethod.PlainCopy));
                Assert.Equal(OwnMemoryFile.PhotoPrefix + Hash(bytes) + "." + OwnPhotoIntake.ExtensionFor(format), entry.FullName);
            }

            var json = ReadEntry(zip, OwnMemoryFile.JsonName);
            Assert.DoesNotContain(_root.Path.Replace("\\", "\\\\", StringComparison.Ordinal), json, StringComparison.Ordinal);
            Assert.DoesNotContain("\\\\", json, StringComparison.Ordinal);
            Assert.DoesNotContain("own-memories.db", json, StringComparison.Ordinal);
            Assert.DoesNotContain("photos/", json, StringComparison.Ordinal);
            Assert.DoesNotContain("RowID", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Added on the phone", json, StringComparison.Ordinal);

            var root = JsonNode.Parse(json)!.AsObject();
            Assert.Equal(["format", "version", "created", "memories", "muted"], root.Select(p => p.Key));
            Assert.Equal("chatcheology-own-memories", (string?)root["format"]);
            Assert.Equal(1, (int?)root["version"]);
            Assert.Equal("2026-11-07T10:30:00Z", (string?)root["created"]);
            Assert.Equal(
                ["id", "start", "end", "precision", "title", "summary", "everyYear", "photos", "links"],
                root["memories"]![0]!.AsObject().Select(p => p.Key));
        }

        [Fact]
        public void Saving_with_no_memories_is_refused_and_writes_nothing()
        {
            using var target = new MemoryStream();

            var refused = Assert.Throws<OwnMemoryFileException>(() => OwnMemoryFile.Save(_source, target, Now));

            Assert.Equal(OwnMemoryFileProblem.NoMemories, refused.Problem);
            Assert.Equal(0, target.Length);
            Assert.Equal("There are no memories to save.", OwnMemoryFile.SaveMessageFor(refused.Problem));
        }

        [Fact]
        public async Task A_stored_photo_that_fails_its_checks_is_left_out_and_counted_and_its_memory_still_saved()
        {
            var (walk, _, _) = await Seed(_source);

            // Its bytes no longer match its name (damage on the phone): left out, never written.
            File.AppendAllText(walk.Photos[0].Path, "x");

            var file = Save(_source, out var saved);

            Assert.Equal(new OwnMemorySaveResult(3, 2, 1), saved);
            Assert.Equal("Saved 3 memories and 2 photos. Left out: 1 photo that could not be checked.", OwnMemoryFile.MessageFor(saved));

            OwnMemoryFile.Load(_target, new MemoryStream(file));
            var loaded = _target.Get(walk.Id)!;
            Assert.Equal(walk.Photos.Skip(1).Select(p => p.FileName), loaded.Photos.Select(p => p.FileName));
        }

        [Fact]
        public async Task A_store_past_the_total_limit_is_refused_before_anything_is_written()
        {
            await Seed(_source);
            using var target = new MemoryStream();

            var refused = Assert.Throws<OwnMemoryFileException>(() =>
                OwnMemoryFile.Save(_source, target, Now, new OwnMemoryFileLimits { MaxTotalBytes = 1000, MaxJsonBytes = 900 }));

            Assert.Equal(OwnMemoryFileProblem.TooLarge, refused.Problem);
            Assert.Equal(0, target.Length);
        }

        // ---------------------------------------------------------------- Never overwrite

        [Fact]
        public async Task Loading_into_a_store_with_the_same_ids_counts_same_and_different_and_keeps_the_phones_copy()
        {
            var (walk, yearly, month) = await Seed(_source);
            var file = Save(_source, out _);

            // Into the store it came from: everything already here.
            var again = OwnMemoryFile.Load(_source, new MemoryStream(file));
            Assert.Equal(new OwnMemoryLoadResult(0, 3, 0, 0, 0, 0), again);

            // Edited since (yours kept), deleted since (comes back), and the third unchanged.
            _source.Update(walk.Id, new OwnMemoryDraft
            {
                Title = "Invented lake walk, edited",
                Start = walk.Start,
                Precision = walk.Precision,
                Photos = [.. walk.Photos.Select(p => new OwnPhotoDraft(p.FileName, p.Caption))],
                Links = walk.Links,
            });
            Assert.True(_source.Delete(month.Id));

            var result = OwnMemoryFile.Load(_source, new MemoryStream(file));

            Assert.Equal(new OwnMemoryLoadResult(1, 1, 1, 0, 0, 0), result);
            Assert.Equal("Invented lake walk, edited", _source.Get(walk.Id)!.Title);
            Assert.NotNull(_source.Get(month.Id));
            Assert.Equal(
                "Added 1. Already here: 1. Already here but different (yours kept): 1.",
                OwnMemoryFile.MessageFor(result));
            _ = yearly;
        }

        [Fact]
        public async Task Mutes_in_the_file_apply_only_to_memories_this_load_adds()
        {
            var (walk, yearly, _) = await Seed(_source);
            Assert.True(_source.Settings.SetMuted(yearly.Id, true));
            Assert.True(_source.Settings.SetMuted(walk.Id, true));
            var file = Save(_source, out _);

            // The target already has the yearly one, unmuted; the others are added with their mutes.
            OwnMemoryFile.Load(_target, new MemoryStream(Save(_source, out _)));
            Assert.True(_target.Settings.SetMuted(yearly.Id, false));
            Assert.True(_target.Delete(walk.Id));

            OwnMemoryFile.Load(_target, new MemoryStream(file));

            Assert.Equal(new HashSet<Guid> { walk.Id }, _target.Settings.Muted());
        }

        // ---------------------------------------------------------------- The file's own refusals

        [Theory]
        [InlineData("other-format", 1, OwnMemoryFileProblem.NotAMemoriesFile)]
        [InlineData("chatcheology-own-memories", 2, OwnMemoryFileProblem.NewerVersion)]
        [InlineData("chatcheology-own-memories", 0, OwnMemoryFileProblem.NotAMemoriesFile)]
        public void A_wrong_marker_or_version_is_refused(string format, int version, OwnMemoryFileProblem problem)
        {
            var json = Json(format, version, [Memory()]);

            AssertRefused(Zip((OwnMemoryFile.JsonName, json)), problem);
        }

        [Fact]
        public void A_zip_without_memories_json_or_an_empty_zip_is_not_a_memories_file()
        {
            var photo = SyntheticImage.Png(4, 4, (1, 2, 3), (4, 5, 6));

            AssertRefused(Zip((PhotoName(photo, "png"), photo)), OwnMemoryFileProblem.NotAMemoriesFile);
            AssertRefused(EmptyZip(), OwnMemoryFileProblem.NotAMemoriesFile);
        }

        [Theory]
        [InlineData("{not json")]
        [InlineData("[1,2]")]
        [InlineData("{\"format\":\"chatcheology-own-memories\",\"version\":1,\"memories\":{}}")]
        [InlineData("{\"format\":\"chatcheology-own-memories\",\"version\":1,\"memories\":[],\"muted\":\"x\"}")]
        [InlineData("{\"format\":\"chatcheology-own-memories\",\"format\":\"chatcheology-own-memories\",\"version\":1,\"memories\":[]}")]
        [InlineData("{\"format\":\"chatcheology-own-memories\",\"version\":1,\"memories\":[[[[[[[[1]]]]]]]]}")]
        public void Bad_JSON_refuses_the_file(string json)
        {
            var problem = json.StartsWith("[1", StringComparison.Ordinal)
                ? OwnMemoryFileProblem.NotAMemoriesFile
                : OwnMemoryFileProblem.Damaged;

            AssertRefused(Zip((OwnMemoryFile.JsonName, Encoding.UTF8.GetBytes(json))), problem);
        }

        [Fact]
        public void The_same_memory_id_twice_refuses_the_file()
        {
            var id = Guid.NewGuid();

            AssertRefused(Zip((OwnMemoryFile.JsonName, Json([Memory(id), Memory(id, "Another")]))), OwnMemoryFileProblem.Damaged);
        }

        [Fact]
        public void A_duplicate_entry_name_refuses_the_file()
        {
            var json = Json([Memory()]);

            AssertRefused(Zip((OwnMemoryFile.JsonName, json), (OwnMemoryFile.JsonName, json)), OwnMemoryFileProblem.Damaged);
        }

        [Fact]
        public void A_photo_whose_bytes_are_not_its_name_refuses_the_file()
        {
            var photo = SyntheticImage.Png(4, 4, (1, 2, 3), (4, 5, 6));
            var name = OwnMemoryFile.PhotoPrefix + new string('a', 64) + ".png";

            AssertRefused(Zip((name, photo), (OwnMemoryFile.JsonName, Json([Memory(photos: [(name[7..], null)])]))), OwnMemoryFileProblem.Damaged);
        }

        [Fact]
        public void Limits_on_photo_size_total_entries_memories_and_JSON_size_refuse_the_file()
        {
            var photo = SyntheticImage.Png(64, 64, (1, 2, 3), (4, 5, 6));
            var file = Zip((PhotoName(photo, "png"), photo), (OwnMemoryFile.JsonName, Json([Memory(photos: [(PhotoName(photo, "png")[7..], null)])])));

            AssertRefused(file, OwnMemoryFileProblem.TooLarge, new OwnMemoryFileLimits { MaxPhotoBytes = photo.Length - 1 });
            AssertRefused(file, OwnMemoryFileProblem.TooLarge, new OwnMemoryFileLimits { MaxTotalBytes = photo.Length + 10 });
            AssertRefused(file, OwnMemoryFileProblem.TooLarge, new OwnMemoryFileLimits { MaxEntries = 1 });
            AssertRefused(file, OwnMemoryFileProblem.TooLarge, new OwnMemoryFileLimits { MaxJsonBytes = 20 });

            var two = Zip((OwnMemoryFile.JsonName, Json([Memory(), Memory()])));
            AssertRefused(two, OwnMemoryFileProblem.TooLarge, new OwnMemoryFileLimits { MaxMemories = 1 });

            // Within every limit it loads.
            Assert.Equal(1, OwnMemoryFile.Load(_target, new MemoryStream(file)).Added);
        }

        [Fact]
        public void A_zip_bomb_with_a_small_packed_size_is_stopped_while_unpacking_whatever_its_header_says()
        {
            // 64 MB of spaces after valid JSON packs to well under 100 KB; the header claims only 100 bytes.
            var json = Json([Memory()]);
            var huge = new byte[64 * 1024 * 1024];
            huge.AsSpan().Fill((byte)' ');
            json.CopyTo(huge, 0);
            var bomb = RawZip(new RawEntry(OwnMemoryFile.JsonName, huge) { Deflate = true, ClaimedSize = 100 });
            Assert.True(bomb.Length < 200_000);

            AssertRefused(bomb, OwnMemoryFileProblem.TooLarge);

            // Counted against the total too, not only per entry.
            var photos = Enumerable.Range(0, 3).Select(i => new byte[5_000_000]).Select((b, i) => { b[0] = (byte)i; return b; }).ToArray();
            var many = RawZip([.. photos.Select(b => new RawEntry(OwnMemoryFile.PhotoPrefix + Hash(b) + ".png", b) { Deflate = true })]);
            AssertRefused(many, OwnMemoryFileProblem.TooLarge, new OwnMemoryFileLimits { MaxTotalBytes = 12_000_000 });
        }

        [Fact]
        public void A_truncated_zip_is_damaged_wherever_it_is_cut()
        {
            var photo = SyntheticImage.Png(8, 8, (1, 2, 3), (4, 5, 6));
            var file = Zip((PhotoName(photo, "png"), photo), (OwnMemoryFile.JsonName, Json([Memory(photos: [(PhotoName(photo, "png")[7..], null)])])));

            foreach (var length in new[] { 0, 3, 20, 40, file.Length / 2, file.Length - 30, file.Length - 1 })
            {
                AssertRefused(file[..length], OwnMemoryFileProblem.Damaged);
            }
        }

        [Fact]
        public void Bytes_after_the_end_record_a_CRC_mismatch_or_a_central_directory_that_disagrees_are_damage()
        {
            var json = Json([Memory()]);
            var file = Zip((OwnMemoryFile.JsonName, json));

            AssertRefused([.. file, 0], OwnMemoryFileProblem.Damaged);
            AssertRefused(RawZip(new RawEntry(OwnMemoryFile.JsonName, json) { Crc = 1 }), OwnMemoryFileProblem.Damaged);
            AssertRefused(RawZip(new RawEntry(OwnMemoryFile.JsonName, json) { CentralName = "memories.jsoN" }), OwnMemoryFileProblem.Damaged);
        }

        [Fact]
        public void Encryption_zip64_a_data_descriptor_or_another_method_is_not_supported()
        {
            var json = Json([Memory()]);

            AssertRefused(RawZip(new RawEntry(OwnMemoryFile.JsonName, json) { Flags = 0x0001 }), OwnMemoryFileProblem.NotSupported);
            AssertRefused(RawZip(new RawEntry(OwnMemoryFile.JsonName, json) { Flags = 0x0040 }), OwnMemoryFileProblem.NotSupported);
            AssertRefused(RawZip(new RawEntry(OwnMemoryFile.JsonName, json) { Method = 99 }), OwnMemoryFileProblem.NotSupported);
            AssertRefused(RawZip(new RawEntry(OwnMemoryFile.JsonName, json) { Method = 12 }), OwnMemoryFileProblem.NotSupported);
            AssertRefused(RawZip(new RawEntry(OwnMemoryFile.JsonName, json) { Extra = [1, 0, 16, 0, .. new byte[16]] }), OwnMemoryFileProblem.NotSupported);
            AssertRefused(RawZip(new RawEntry(OwnMemoryFile.JsonName, json) { ClaimedSize = uint.MaxValue }), OwnMemoryFileProblem.NotSupported);
            AssertRefused(RawZip(new RawEntry(OwnMemoryFile.JsonName, json) { Version = 45 }), OwnMemoryFileProblem.NotSupported);

            // A zip written by .NET to a stream that cannot seek uses data descriptors.
            using var unseekable = new WriteOnlyStream();

            using (var zip = new ZipArchive(unseekable, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var entry = zip.CreateEntry(OwnMemoryFile.JsonName).Open();
                entry.Write(json);
            }

            AssertRefused(unseekable.Inner.ToArray(), OwnMemoryFileProblem.NotSupported);
        }

        // ---------------------------------------------------------------- Entries ignored, memories refused

        [Fact]
        public void Unknown_and_path_like_entries_are_ignored_counted_and_never_written_anywhere()
        {
            var bytes = "invented"u8.ToArray();
            var file = Zip(
                (OwnMemoryFile.JsonName, Json([Memory()])),
                ("notes.txt", bytes),
                ("../escape.jpg", bytes),
                ("photos/../../escape.jpg", bytes),
                ("/absolute.jpg", bytes),
                ("C:/absolute.jpg", bytes),
                ("photos\\" + new string('a', 64) + ".jpg", bytes),
                ("photos/" + new string('A', 64) + ".jpg", bytes),
                ("photos/" + new string('a', 64) + ".heic", bytes),
                ("photos/", []));

            var result = OwnMemoryFile.Load(_target, new MemoryStream(file));

            Assert.Equal(new OwnMemoryLoadResult(1, 0, 0, 0, 9, 0), result);
            Assert.Equal(
                "Added 1. Already here: 0. Already here but different (yours kept): 0. Ignored: 9 items that are not part of a memories file.",
                OwnMemoryFile.MessageFor(result));
            Assert.Empty(Directory.GetFiles(_root.Path, "*escape*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(_root.Path, "*absolute*", SearchOption.AllDirectories));
            AssertNoStage(_target);
        }

        [Fact]
        public void A_memory_that_breaks_a_rule_or_names_a_missing_photo_is_not_loaded_and_counted_and_the_rest_are()
        {
            var photo = SyntheticImage.Png(4, 4, (1, 2, 3), (4, 5, 6));
            var present = PhotoName(photo, "png")[7..];
            var missing = new string('b', 64) + ".png";
            var memories = new JsonNode?[]
            {
                Memory(title: "Good", photos: [(present, "Kept")]),
                Memory(title: "Names a photo not in the file", photos: [(missing, null)]),
                Memory(title: "Names a path", photos: [("../" + present, null)]),
                Memory(title: new string('t', 201)),
                Memory(title: "   "),
                Memory(title: "End before start", end: "2001-01-01"),
                Memory(title: "Every year with an end", end: "2003-01-01", everyYear: true),
                Memory(title: "Bad link", links: [("http://example.org", null)]),
                Memory(title: "Too many links", links: [.. Enumerable.Range(0, 11).Select(i => ($"https://example.org/{i}", (string?)null))]),
                Memory(title: "Bad caption", photos: [(present, new string('c', 501))]),
                Memory(title: "Bad precision", precision: "Year"),
                Memory(title: "Bad date", start: "2002-13-40"),
                Memory(title: "Bad id", idText: "not-a-guid"),
                Memory(title: "Upper-case id", idText: Guid.NewGuid().ToString("D").ToUpperInvariant()),
                Memory(title: "Empty id", idText: Guid.Empty.ToString("D")),
                new JsonObject { ["id"] = Guid.NewGuid().ToString("D"), ["title"] = 5 },
                JsonValue.Create("not an object"),
            };

            var result = OwnMemoryFile.Load(_target, new MemoryStream(Zip(
                (PhotoName(photo, "png"), photo),
                (OwnMemoryFile.JsonName, Json(memories)))));

            Assert.Equal(new OwnMemoryLoadResult(1, 0, 0, memories.Length - 1, 0, 1), result);
            Assert.EndsWith($"Not loaded: {memories.Length - 1} memories that did not pass the checks.", OwnMemoryFile.MessageFor(result), StringComparison.Ordinal);
            var loaded = Assert.Single(_target.List());
            Assert.Equal("Good", loaded.Title);
            Assert.Equal("Kept", loaded.Photos.Single().Caption);
        }

        [Fact]
        public void A_photo_in_a_format_other_than_its_extension_or_that_does_not_rebuild_refuses_its_memory()
        {
            var png = SyntheticImage.Png(4, 4, (1, 2, 3), (4, 5, 6));
            var asJpg = OwnMemoryFile.PhotoPrefix + Hash(png) + ".jpg";
            var broken = (byte[])[.. "GIF89a"u8, 1, 2];
            var brokenName = PhotoName(broken, "gif");

            var result = OwnMemoryFile.Load(_target, new MemoryStream(Zip(
                (asJpg, png),
                (brokenName, broken),
                (OwnMemoryFile.JsonName, Json([Memory(title: "Wrong extension", photos: [(asJpg[7..], null)]), Memory(title: "Broken", photos: [(brokenName[7..], null)])])))));

            Assert.Equal(new OwnMemoryLoadResult(0, 0, 0, 2, 0, 0), result);
            Assert.Empty(_target.List());
            Assert.Empty(Directory.Exists(_target.PhotosFolder) ? Directory.GetFiles(_target.PhotosFolder) : []);
        }

        public static TheoryData<string> PlantedFormats => ["jpg", "png", "webp", "gif", "bmp"];

        [Theory]
        [MemberData(nameof(PlantedFormats))]
        public void A_photo_with_a_planted_location_is_rebuilt_without_it_and_kept_under_the_rebuilt_hash(string extension)
        {
            var planted = extension switch
            {
                "jpg" => SyntheticJpeg.WithLocation(),
                "png" => PlantedPng(),
                "webp" => PlantedWebP(),
                "gif" => PlantedGif(),
                _ => PlantedBmp(),
            };
            var name = PhotoName(planted, extension);

            var result = OwnMemoryFile.Load(_target, new MemoryStream(Zip(
                (name, planted),
                (OwnMemoryFile.JsonName, Json([Memory(photos: [(name[7..], "Invented")])])))));

            Assert.Equal(1, result.Added);
            var photo = _target.List().Single().Photos.Single();
            var stored = File.ReadAllBytes(photo.Path);
            var format = PhotoShare.Sniff(stored);

            Assert.NotEqual(name[7..], photo.FileName);
            Assert.Equal(Hash(stored) + "." + extension, photo.FileName);
            Assert.False(PhotoShare.HasGpsBlock(stored));
            Assert.False(PhotoShare.MayStillCarryLocation(new MemoryStream(stored), format, format == PhotoFormat.Jpeg ? PhotoShareMethod.RemoveLocation : PhotoShareMethod.PlainCopy));
            Assert.Equal(0, Count(stored, "12.58"u8.ToArray()));
            Assert.Equal(0, Count(stored, "Invented Harbour"u8.ToArray()));
        }

        // ---------------------------------------------------------------- Stage and all or nothing

        [Fact]
        public void A_refused_file_changes_nothing_in_a_store_with_memories_and_leaves_no_stage()
        {
            var photo = SyntheticImage.Png(4, 4, (1, 2, 3), (4, 5, 6));
            var existing = _target.Add(new OwnMemoryDraft { Title = "Here before", Start = new DateOnly(2010, 1, 1), Precision = MemoryDatePrecision.Day });
            var before = Snapshot(_target);

            // The JSON comes first and is valid; the photo after it is damaged, so its photo has already been staged.
            var good = SyntheticImage.Png(5, 5, (9, 9, 9), (8, 8, 8));
            var file = Zip(
                (OwnMemoryFile.JsonName, Json([Memory(photos: [(PhotoName(good, "png")[7..], null)])])),
                (PhotoName(good, "png"), good),
                (OwnMemoryFile.PhotoPrefix + new string('d', 64) + ".png", photo));

            AssertRefused(file, OwnMemoryFileProblem.Damaged);

            Assert.Equal(before, Snapshot(_target));
            Assert.NotNull(_target.Get(existing.Id));
        }

        [Fact]
        public void A_failure_while_reading_the_source_leaves_no_stage_and_changes_nothing()
        {
            var file = Zip((OwnMemoryFile.JsonName, Json([Memory()])));

            Assert.Throws<IOException>(() => OwnMemoryFile.Load(_target, new FailingStream(file, failAt: file.Length / 2)));

            Assert.Empty(_target.List());
            AssertNoStage(_target);
        }

        [Fact]
        public void A_failure_in_the_transaction_deletes_the_photos_it_moved_in_and_adds_nothing()
        {
            var photo = SyntheticImage.Png(4, 4, (1, 2, 3), (4, 5, 6));
            var id = Guid.NewGuid();
            var file = Zip((PhotoName(photo, "png"), photo), (OwnMemoryFile.JsonName, Json([Memory(id, photos: [(PhotoName(photo, "png")[7..], null)])])));
            Assert.Empty(_target.List());
            var moved = false;

            // The photo is already in the photos folder when the commit fails (disk full, a killed write).
            _target.BeforeLoadCommit = () =>
            {
                moved = Directory.GetFiles(_target.PhotosFolder).Length == 1;
                throw new IOException("Invented failure before the commit.");
            };

            Assert.Throws<IOException>(() => OwnMemoryFile.Load(_target, new MemoryStream(file)));
            _target.BeforeLoadCommit = null;

            Assert.True(moved);
            Assert.Empty(_target.List());
            Assert.Empty(Directory.Exists(_target.PhotosFolder) ? Directory.GetFiles(_target.PhotosFolder) : []);
            AssertNoStage(_target);
        }

        [Fact]
        public void A_stage_left_by_a_killed_load_is_swept_at_first_use_when_old_and_a_young_one_is_kept()
        {
            using var root = new TemporaryFolder();
            var own = Path.Combine(root.Path, OwnMemoriesDatabase.FolderName);
            var old = Path.Combine(own, ".incoming-load-" + Guid.NewGuid().ToString("N"));
            var young = Path.Combine(own, ".incoming-load-" + Guid.NewGuid().ToString("N"));
            var other = Path.Combine(own, ".incoming-load-not-a-guid");

            foreach (var folder in new[] { old, young, other })
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, "x.png"), [1]);
            }

            Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));
            Directory.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddHours(-1));

            Assert.Empty(new OwnMemoryStore(root.Path).List());

            Assert.False(Directory.Exists(old));
            Assert.True(Directory.Exists(young));
            Assert.True(Directory.Exists(other));
            Assert.True(OwnMemoryStore.IsLoadStageName(Path.GetFileName(young)));
            Assert.False(OwnMemoryStore.IsLoadStageName(Path.GetFileName(other)));
        }

        // ---------------------------------------------------------------- Words

        [Fact]
        public void The_words_are_plain_and_name_nothing_of_the_file()
        {
            Assert.Equal("my-memories-2026-11-07.zip", OwnMemoryFile.SuggestedName(new DateOnly(2026, 11, 7)));
            Assert.Equal("This file holds your memories and photos (without locations). Anyone who has the file can see them.", OwnMemoryFile.SaveWarning);
            Assert.Equal("Memories you deleted since this file was saved will come back.", OwnMemoryFile.LoadWarning);
            Assert.Equal("Saved 1 memory and 0 photos.", OwnMemoryFile.MessageFor(new OwnMemorySaveResult(1, 0, 0)));
            Assert.Equal(
                "This file couldn't be loaded: it was saved by a newer version of Chatcheology. Nothing was changed.",
                OwnMemoryFile.LoadMessageFor(OwnMemoryFileProblem.NewerVersion));

            foreach (var problem in Enum.GetValues<OwnMemoryFileProblem>())
            {
                Assert.StartsWith("This file couldn't be loaded: ", OwnMemoryFile.LoadMessageFor(problem), StringComparison.Ordinal);
                Assert.EndsWith(" Nothing was changed.", OwnMemoryFile.LoadMessageFor(problem), StringComparison.Ordinal);
            }

            Assert.Equal(
                "added=1 same=2 different=3 refused=4 ignored=5 photos=6",
                OwnMemoryFile.DescribeCounts(new OwnMemoryLoadResult(1, 2, 3, 4, 5, 6)));
            Assert.Equal("memories=1 photos=2 leftOut=3", OwnMemoryFile.DescribeCounts(new OwnMemorySaveResult(1, 2, 3)));
        }

        // ---------------------------------------------------------------- Helpers

        private static async Task<(OwnMemory Walk, OwnMemory Yearly, OwnMemory Month)> Seed(OwnMemoryStore store)
        {
            var intake = new OwnPhotoIntake(store.PhotosFolder);
            var png = await intake.AddAsync(new MemoryStream(SyntheticImage.Png(32, 24, (0x2E, 0x6F, 0x9E), (0xE8, 0xD5, 0x9A))));
            var jpg = await intake.AddAsync(new MemoryStream(SyntheticJpeg.WithLocation()));
            var other = await intake.AddAsync(new MemoryStream(SyntheticImage.Png(16, 16, (1, 1, 1), (200, 200, 200))));

            var walk = store.Add(new OwnMemoryDraft
            {
                Title = "Invented lake walk",
                Start = new DateOnly(2025, 3, 20),
                Precision = MemoryDatePrecision.Day,
                Summary = "An invented walk.",
                Photos = [new OwnPhotoDraft(jpg.FileName!, "Invented shore"), new OwnPhotoDraft(png.FileName!, null), new OwnPhotoDraft(other.FileName!, "Third")],
                Links = [new OwnLink("https://example.org/lake", "Invented album"), new OwnLink("https://example.net/b", null)],
            });

            var yearly = store.Add(new OwnMemoryDraft
            {
                Title = "Invented picnic day",
                Start = new DateOnly(2023, 6, 1),
                Precision = MemoryDatePrecision.Day,
                EveryYear = true,
                Photos = [new OwnPhotoDraft(png.FileName!, "Same photo, another memory")],
            });

            var month = store.Add(new OwnMemoryDraft
            {
                Title = "Invented summer",
                Start = new DateOnly(2019, 7, 1),
                End = new DateOnly(2019, 8, 31),
                Precision = MemoryDatePrecision.Month,
            });

            return (walk, yearly, month);
        }

        private static byte[] Save(OwnMemoryStore store, out OwnMemorySaveResult result)
        {
            using var target = new WriteOnlyStream();
            result = OwnMemoryFile.Save(store, target, Now);
            return target.Inner.ToArray();
        }

        private static object Fields(OwnMemory m) => (m.Id, m.Start, m.End, m.Precision, m.Title, m.Summary, m.EveryYear);

        private void AssertRefused(byte[] file, OwnMemoryFileProblem problem, OwnMemoryFileLimits? limits = null)
        {
            var before = Snapshot(_target);

            var refused = Assert.Throws<OwnMemoryFileException>(() => OwnMemoryFile.Load(_target, new MemoryStream(file), limits));

            Assert.Equal(problem, refused.Problem);
            Assert.Equal(before, Snapshot(_target));
            AssertNoStage(_target);
        }

        /// <summary>Every memory's fields, photos and links, the mutes and the photo files: what a refusal must leave as it was.</summary>
        private static string Snapshot(OwnMemoryStore store)
        {
            var text = new StringBuilder();

            foreach (var m in store.List())
            {
                text.Append(Fields(m)).Append(string.Join(",", m.Photos.Select(p => p.FileName + p.Caption))).Append(string.Join(",", m.Links)).Append('\n');
            }

            text.Append(string.Join(",", store.Settings.Muted().Order())).Append('\n');

            if (Directory.Exists(store.PhotosFolder))
            {
                text.Append(string.Join(",", Directory.GetFiles(store.PhotosFolder).Select(Path.GetFileName).Order()));
            }

            return text.ToString();
        }

        private static void AssertNoStage(OwnMemoryStore store)
        {
            var own = Path.GetDirectoryName(store.DatabasePath)!;
            Assert.Empty(Directory.Exists(own) ? Directory.GetDirectories(own, ".incoming-load-*") : []);
        }

        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        private static string PhotoName(byte[] bytes, string extension) => OwnMemoryFile.PhotoPrefix + Hash(bytes) + "." + extension;

        private static string ReadEntry(ZipArchive zip, string name)
        {
            using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static JsonObject Memory(
            Guid? id = null,
            string title = "Invented memory",
            string start = "2002-06-15",
            string? end = null,
            string precision = "Day",
            bool everyYear = false,
            (string File, string? Caption)[]? photos = null,
            (string Url, string? Label)[]? links = null,
            string? idText = null) => new()
            {
                ["id"] = idText ?? (id ?? Guid.NewGuid()).ToString("D"),
                ["start"] = start,
                ["end"] = end,
                ["precision"] = precision,
                ["title"] = title,
                ["summary"] = null,
                ["everyYear"] = everyYear,
                ["photos"] = new JsonArray([.. (photos ?? []).Select(p => (JsonNode)new JsonObject { ["file"] = p.File, ["caption"] = p.Caption })]),
                ["links"] = new JsonArray([.. (links ?? []).Select(l => (JsonNode)new JsonObject { ["url"] = l.Url, ["label"] = l.Label })]),
            };

        private static byte[] Json(JsonNode?[] memories) => Json(OwnMemoryFile.FormatName, OwnMemoryFile.FormatVersion, memories);

        private static byte[] Json(string format, int version, JsonNode?[] memories) =>
            Encoding.UTF8.GetBytes(new JsonObject
            {
                ["format"] = format,
                ["version"] = version,
                ["created"] = "2026-11-07T10:30:00Z",
                ["memories"] = new JsonArray(memories),
                ["muted"] = new JsonArray(),
            }.ToJsonString());

        /// <summary>A file as this app writes it, with the entries in the order given.</summary>
        private static byte[] Zip(params (string Name, byte[] Data)[] entries) =>
            RawZip([.. entries.Select(e => new RawEntry(e.Name, e.Data) { Deflate = e.Name == OwnMemoryFile.JsonName })]);

        private static byte[] EmptyZip() => [0x50, 0x4B, 0x05, 0x06, .. new byte[18]];

        /// <summary>One entry of a crafted zip; every field can be bent.</summary>
        private sealed record RawEntry(string Name, byte[] Data)
        {
            public bool Deflate { get; init; }

            public ushort Flags { get; init; }

            public ushort? Method { get; init; }

            public ushort Version { get; init; } = 20;

            public uint? Crc { get; init; }

            public uint? ClaimedSize { get; init; }

            public byte[] Extra { get; init; } = [];

            public string? CentralName { get; init; }
        }

        private static byte[] RawZip(params RawEntry[] entries)
        {
            using var output = new MemoryStream();
            var central = new MemoryStream();

            foreach (var entry in entries)
            {
                var packed = entry.Data;

                if (entry.Deflate)
                {
                    using var buffer = new MemoryStream();

                    using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        deflate.Write(entry.Data);
                    }

                    packed = buffer.ToArray();
                }

                var method = entry.Method ?? (ushort)(entry.Deflate ? 8 : 0);
                var crc = entry.Crc ?? PhotoRebuild.Crc32(entry.Data);
                var size = entry.ClaimedSize ?? (uint)entry.Data.Length;
                var offset = (uint)output.Position;
                var name = Encoding.ASCII.GetBytes(entry.Name);
                var centralName = Encoding.ASCII.GetBytes(entry.CentralName ?? entry.Name);

                var local = new byte[30];
                BinaryPrimitives.WriteUInt32LittleEndian(local, 0x04034B50);
                BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(4), entry.Version);
                BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(6), entry.Flags);
                BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(8), method);
                BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(14), crc);
                BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(18), (uint)packed.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(22), size);
                BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(26), (ushort)name.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(28), (ushort)entry.Extra.Length);
                output.Write(local);
                output.Write(name);
                output.Write(entry.Extra);
                output.Write(packed);

                var head = new byte[46];
                BinaryPrimitives.WriteUInt32LittleEndian(head, 0x02014B50);
                BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(4), 20);
                BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(6), entry.Version);
                BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(8), entry.Flags);
                BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), method);
                BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(16), crc);
                BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(20), (uint)packed.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(24), size);
                BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(28), (ushort)centralName.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(30), (ushort)entry.Extra.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(42), offset);
                central.Write(head);
                central.Write(centralName);
                central.Write(entry.Extra);
            }

            var centralStart = (uint)output.Position;
            central.Position = 0;
            central.CopyTo(output);

            var end = new byte[22];
            BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054B50);
            BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), (ushort)entries.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), (ushort)entries.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), (uint)central.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), centralStart);
            output.Write(end);
            return output.ToArray();
        }

        // Photos with a location planted where each format can carry one; the rebuild keeps none of it.

        private static byte[] PlantedPng()
        {
            var plain = SyntheticImage.Png(4, 4, (1, 2, 3), (4, 5, 6));
            var exif = (byte[])[.. "Exif\0\0"u8, .. SyntheticJpeg.GpsTiff()];
            var chunk = new byte[12 + exif.Length];
            BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)exif.Length);
            "eXIf"u8.CopyTo(chunk.AsSpan(4));
            exif.CopyTo(chunk, 8);
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + exif.Length), PhotoRebuild.Crc32(chunk.AsSpan(4, 4 + exif.Length)));

            // After the signature and IHDR (8 + 25 bytes).
            return [.. plain[..33], .. chunk, .. plain[33..]];
        }

        private static byte[] PlantedWebP()
        {
            byte[] vp8 = [0x10, 0x02, 0, 0x9D, 0x01, 0x2A, 2, 0, 2, 0, 1];
            byte[] vp8x = [0x08 | 0x04, 0, 0, 0, 1, 0, 0, 1, 0, 0];
            var exif = SyntheticJpeg.GpsTiff();
            var xmp = Encoding.UTF8.GetBytes("<x:xmpmeta><photoshop:City>Invented Harbour</photoshop:City><exif:GPSLatitude>12.58</exif:GPSLatitude></x:xmpmeta>");
            var body = new List<byte>("WEBP"u8.ToArray());

            foreach (var (type, data) in new[] { ("VP8X", vp8x), ("VP8 ", vp8), ("EXIF", exif), ("XMP ", xmp) })
            {
                body.AddRange(Encoding.ASCII.GetBytes(type));
                var size = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
                body.AddRange(size);
                body.AddRange(data);

                if (data.Length % 2 == 1)
                {
                    body.Add(0);
                }
            }

            var riff = new byte[8];
            "RIFF"u8.CopyTo(riff);
            BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), (uint)body.Count);
            return [.. riff, .. body];
        }

        private static byte[] PlantedGif()
        {
            var comment = "Taken at 12.5823 N, 65.7225 E"u8.ToArray();
            byte[] image = [0x2C, 0, 0, 0, 0, 2, 0, 2, 0, 0, 2, 4, 0, 13, 26, 39, 0];
            return [.. "GIF89a"u8, 2, 0, 2, 0, 0x80, 0, 0, 0, 0, 0, 255, 255, 255, 0x21, 0xFE, (byte)comment.Length, .. comment, 0, .. image, 0x3B];
        }

        private static byte[] PlantedBmp()
        {
            var gap = "GPS 12.5823 65.7225 Invented Harbour"u8.ToArray();
            var pixelOffset = 54 + gap.Length;
            var bytes = new byte[pixelOffset + 16];
            "BM"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), (uint)bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), (uint)pixelOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 2);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 24);
            gap.CopyTo(bytes, 54);

            for (var i = pixelOffset; i < bytes.Length; i++)
            {
                bytes[i] = (byte)(i * 3);
            }

            return bytes;
        }

        private static int Count(byte[] data, byte[] pattern)
        {
            var count = 0;

            for (var i = 0; i + pattern.Length <= data.Length; i++)
            {
                if (data.AsSpan(i).StartsWith(pattern))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>A target that cannot seek or be read, as a system Save dialog's stream.</summary>
        private sealed class WriteOnlyStream : Stream
        {
            internal MemoryStream Inner { get; } = new();

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => Inner.Write(buffer, offset, count);
        }

        /// <summary>A source that fails with an I/O error part way, as a provider can.</summary>
        private sealed class FailingStream(byte[] data, int failAt) : Stream
        {
            private int _position;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position >= failAt)
                {
                    throw new IOException("Invented provider failure.");
                }

                var read = Math.Min(count, failAt - _position);
                Array.Copy(data, _position, buffer, offset, read);
                _position += read;
                return read;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
