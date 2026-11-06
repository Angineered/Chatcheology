using Chatcheology.Archive;
using Chatcheology.Mobile.OwnMemories;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>CH-54 step 1a: the phone owner's own memories store. Synthetic data only.</summary>
    public sealed class OwnMemoryStoreTests : IDisposable
    {
        private readonly TemporaryFolder _root = new();
        private readonly OwnMemoryStore _store;

        public OwnMemoryStoreTests()
        {
            _store = new OwnMemoryStore(_root.Path);
        }

        public void Dispose() => _root.Dispose();

        [Fact]
        public void The_file_is_created_on_first_use_under_files_own_outside_the_archives()
        {
            Assert.False(File.Exists(_store.DatabasePath));

            Assert.Empty(_store.List());

            Assert.Equal(Path.Combine(_root.Path, "own", "own-memories.db"), _store.DatabasePath);
            Assert.Equal(Path.Combine(_root.Path, "own", "photos"), _store.PhotosFolder);
            Assert.True(File.Exists(_store.DatabasePath));

            using var connection = Raw();
            Assert.Equal(1L, Scalar(connection, "PRAGMA user_version;"));
            Assert.Equal("delete", Scalar(connection, "PRAGMA journal_mode;"));
        }

        [Fact]
        public void A_memory_is_added_listed_and_read_back_with_every_field()
        {
            var photos = Photos(2);
            var added = _store.Add(Draft("A walk on the pier") with
            {
                Start = new DateOnly(2031, 5, 9),
                Summary = "  Windy.  ",
                EveryYear = true,
                Photos = [new OwnPhotoDraft(photos[0], " The cover "), new OwnPhotoDraft(photos[1], "   ")],
                Links = [new OwnLink(" https://photos.example.org/album/1 ", " Album "), new OwnLink("https://example.net/b", null)],
            });

            Assert.True(added.RowID > 0);
            Assert.NotEqual(Guid.Empty, added.Id);
            Assert.Equal(new DateOnly(2031, 5, 9), added.Start);
            Assert.Null(added.End);
            Assert.Equal(MemoryDatePrecision.Day, added.Precision);
            Assert.Equal("A walk on the pier", added.Title);
            Assert.Equal("Windy.", added.Summary);
            Assert.True(added.EveryYear);
            Assert.Equal("Added on the phone", added.Source);
            Assert.Equal([photos[0], photos[1]], added.Photos.Select(p => p.FileName));
            Assert.Equal(["The cover", null], added.Photos.Select(p => p.Caption));
            Assert.Equal(Path.Combine(_store.PhotosFolder, photos[0]), added.Photos[0].Path);
            Assert.Equal([new OwnLink("https://photos.example.org/album/1", "Album"), new OwnLink("https://example.net/b", null)], added.Links);

            Assert.Equal(added, _store.Get(added.Id), new OwnMemoryComparer());
            Assert.Single(_store.List());
            Assert.Null(_store.Get(Guid.NewGuid()));
        }

        [Fact]
        public void A_month_memory_keeps_only_its_month_and_the_list_is_in_date_order()
        {
            var late = _store.Add(Draft("Late") with { Start = new DateOnly(2020, 3, 1) });
            var month = _store.Add(Draft("Month") with
            {
                Start = new DateOnly(2019, 7, 23),
                End = new DateOnly(2019, 8, 2),
                Precision = MemoryDatePrecision.Month,
            });
            var early = _store.Add(Draft("Early") with { Start = new DateOnly(2018, 1, 1) });

            Assert.Equal(new DateOnly(2019, 7, 1), month.Start);
            Assert.Equal(new DateOnly(2019, 8, 1), month.End);
            Assert.Equal([early.Id, month.Id, late.Id], _store.List().Select(m => m.Id));
        }

        [Fact]
        public void A_draft_that_breaks_a_rule_is_refused_and_nothing_is_written()
        {
            var refused = Assert.Throws<OwnMemoryValidationException>(() => _store.Add(Draft(new string('x', 201)) with
            {
                End = new DateOnly(2001, 1, 1),
                EveryYear = true,
                Summary = new string('s', 2001),
                Photos = [new OwnPhotoDraft("../escape.jpg", null)],
                Links = [new OwnLink("http://example.org", null)],
            }));

            Assert.Equal("Keep the title to 200 characters.", refused.Errors[OwnMemoryField.Title]);
            Assert.Equal("The end cannot be before the start.", refused.Errors[OwnMemoryField.End]);
            Assert.Equal("Every year is only for a memory without an end date.", refused.Errors[OwnMemoryField.EveryYear]);
            Assert.Equal("Keep the description to 2000 characters.", refused.Errors[OwnMemoryField.Summary]);
            Assert.Equal("Every photo needs an image.", refused.Errors[OwnMemoryField.Photos]);
            Assert.Equal("Only links starting with https:// can be kept.", refused.Errors[OwnMemoryField.Links]);
            Assert.EndsWith("Nothing was written.", refused.Message, StringComparison.Ordinal);
            Assert.Equal(0, _store.Count());
        }

        [Fact]
        public void The_rules_follow_the_desktop()
        {
            Assert.Empty(OwnMemoryRules.Validate(Draft(new string('x', 200)) with
            {
                Summary = new string('s', 2000),
                Start = new DateOnly(2099, 12, 31),
            }));

            Assert.Equal("Give the memory a title.", OwnMemoryRules.Validate(Draft("   "))[OwnMemoryField.Title]);
            Assert.Equal("Choose the date it starts.", OwnMemoryRules.Validate(Draft("t") with { Start = null })[OwnMemoryField.Start]);
            Assert.Equal("Choose Day or Month.", OwnMemoryRules.Validate(Draft("t") with { Precision = (MemoryDatePrecision)7 })[OwnMemoryField.Start]);
            Assert.Equal(
                "Every year needs a memory dated to the day, not a month.",
                OwnMemoryRules.Validate(Draft("t") with { Precision = MemoryDatePrecision.Month, EveryYear = true })[OwnMemoryField.EveryYear]);

            // The end may be earlier in the month than the start at Month precision.
            Assert.Empty(OwnMemoryRules.Validate(Draft("t") with
            {
                Start = new DateOnly(2010, 4, 20),
                End = new DateOnly(2010, 4, 2),
                Precision = MemoryDatePrecision.Month,
            }));

            var name = new string('a', 64) + ".jpg";
            Assert.Equal(
                "Keep each caption to 500 characters.",
                OwnMemoryRules.Validate(Draft("t") with { Photos = [new OwnPhotoDraft(name, new string('c', 501))] })[OwnMemoryField.Photos]);
            Assert.Equal(
                "Each photo can be in a memory only once.",
                OwnMemoryRules.Validate(Draft("t") with { Photos = [new OwnPhotoDraft(name, null), new OwnPhotoDraft(name, null)] })[OwnMemoryField.Photos]);
            Assert.Equal(
                "Keep to 10 links.",
                OwnMemoryRules.Validate(Draft("t") with { Links = [.. Enumerable.Range(0, 11).Select(i => new OwnLink($"https://example.org/{i}", null))] })[OwnMemoryField.Links]);
        }

        [Theory]
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.jpg", true)]
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.webp", true)]
        [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef.jpg", false)]
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.heic", false)]
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.jpg\n", false)]
        [InlineData("../0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd.jpg", false)]
        [InlineData("", false)]
        public void A_photo_name_is_a_hash_and_an_extension_never_a_path(string name, bool valid)
        {
            Assert.Equal(valid, OwnMemoryRules.IsPhotoFileName(name));
        }

        [Fact]
        public void A_photo_that_is_not_in_the_folder_is_refused()
        {
            var refused = Assert.Throws<OwnMemoryValidationException>(() =>
                _store.Add(Draft("t") with { Photos = [new OwnPhotoDraft(new string('b', 64) + ".png", null)] }));

            Assert.Equal("A photo is no longer there: add it again.", refused.Errors[OwnMemoryField.Photos]);
            Assert.Equal(0, _store.Count());
        }

        [Fact]
        public void An_update_replaces_every_field_and_keeps_the_identity_and_row()
        {
            var photos = Photos(3);
            var added = _store.Add(Draft("Before") with
            {
                Photos = [new OwnPhotoDraft(photos[0], "one"), new OwnPhotoDraft(photos[1], "two")],
                Links = [new OwnLink("https://example.org/a", null)],
            });

            var updated = _store.Update(added.Id, Draft("After") with
            {
                Start = new DateOnly(2012, 2, 29),
                Summary = "Now with a summary",
                Photos = [new OwnPhotoDraft(photos[2], "three"), new OwnPhotoDraft(photos[0], "one again")],
            });

            Assert.Equal(added.Id, updated.Id);
            Assert.Equal(added.RowID, updated.RowID);
            Assert.Equal("After", updated.Title);
            Assert.Equal(new DateOnly(2012, 2, 29), updated.Start);
            Assert.Equal([photos[2], photos[0]], updated.Photos.Select(p => p.FileName));
            Assert.Empty(updated.Links);

            // The photo dropped by the update is no longer used by anything: its file is gone.
            Assert.False(File.Exists(Path.Combine(_store.PhotosFolder, photos[1])));
            Assert.True(File.Exists(Path.Combine(_store.PhotosFolder, photos[0])));
            Assert.True(File.Exists(Path.Combine(_store.PhotosFolder, photos[2])));
        }

        [Fact]
        public void A_refused_update_or_one_of_a_memory_no_longer_there_changes_nothing()
        {
            var added = _store.Add(Draft("Kept"));

            Assert.Throws<OwnMemoryValidationException>(() => _store.Update(added.Id, Draft("")));
            Assert.Equal("Kept", _store.Get(added.Id)!.Title);

            var gone = Assert.Throws<OwnMemoryValidationException>(() => _store.Update(Guid.NewGuid(), Draft("New")));
            Assert.Equal("This memory is no longer there.", gone.Errors[OwnMemoryField.Memory]);
            Assert.Single(_store.List());
        }

        [Fact]
        public void A_delete_removes_only_the_photo_files_no_other_memory_uses()
        {
            var photos = Photos(3);
            var first = _store.Add(Draft("First") with { Photos = [new OwnPhotoDraft(photos[0], null), new OwnPhotoDraft(photos[1], null)] });
            var second = _store.Add(Draft("Second") with { Photos = [new OwnPhotoDraft(photos[1], null), new OwnPhotoDraft(photos[2], null)] });
            _store.Settings.SetMuted(first.Id, true);

            Assert.True(_store.Delete(first.Id));

            Assert.Null(_store.Get(first.Id));
            Assert.False(File.Exists(Path.Combine(_store.PhotosFolder, photos[0])));
            Assert.True(File.Exists(Path.Combine(_store.PhotosFolder, photos[1])));
            Assert.True(File.Exists(Path.Combine(_store.PhotosFolder, photos[2])));
            Assert.Empty(_store.Settings.Muted());
            Assert.True(_store.IsPhotoUsed(photos[1]));

            Assert.True(_store.Delete(second.Id));
            Assert.Empty(Directory.GetFiles(_store.PhotosFolder));
            Assert.False(_store.Delete(second.Id));
        }

        [Fact]
        public void A_row_id_is_never_reused_after_a_delete()
        {
            var first = _store.Add(Draft("First"));
            _store.Delete(first.Id);

            var second = _store.Add(Draft("Second"));

            Assert.True(second.RowID > first.RowID);
        }

        [Fact]
        public void Delete_all_removes_every_memory_mute_and_photo_and_keeps_the_switches()
        {
            var photos = Photos(2);
            var memory = _store.Add(Draft("One") with { Photos = [new OwnPhotoDraft(photos[0], null)] });
            _store.Add(Draft("Two"));
            _store.Settings.SetMuted(memory.Id, true);
            _store.Settings.LayerOn = false;
            File.WriteAllBytes(Path.Combine(_store.PhotosFolder, ".incoming-" + Guid.NewGuid().ToString("N") + ".tmp"), [1]);
            File.WriteAllBytes(Path.Combine(_store.PhotosFolder, "not-ours.txt"), [1]);

            Assert.Equal(2, _store.DeleteAll());

            Assert.Empty(_store.List());
            Assert.Empty(_store.Settings.Muted());
            Assert.False(_store.Settings.LayerOn);
            Assert.Equal(["not-ours.txt"], Directory.GetFiles(_store.PhotosFolder).Select(Path.GetFileName));
        }

        [Fact]
        public void The_per_phone_settings_are_stored_with_defaults()
        {
            Assert.True(_store.Settings.LayerOn);
            Assert.True(_store.Settings.RemindersOn);
            Assert.Null(_store.Settings.PostedDay);

            _store.Settings.LayerOn = false;
            _store.Settings.RemindersOn = false;
            _store.Settings.PostedDay = new DateOnly(2026, 11, 6);

            var again = new OwnMemoryStore(_root.Path).Settings;
            Assert.False(again.LayerOn);
            Assert.False(again.RemindersOn);
            Assert.Equal(new DateOnly(2026, 11, 6), again.PostedDay);

            again.LayerOn = true;
            again.PostedDay = null;
            Assert.True(_store.Settings.LayerOn);
            Assert.Null(_store.Settings.PostedDay);
        }

        [Fact]
        public void A_mute_is_kept_by_identity_only_for_a_memory_that_is_there()
        {
            var memory = _store.Add(Draft("Muted"));

            Assert.False(_store.Settings.IsMuted(memory.Id));
            Assert.True(_store.Settings.SetMuted(memory.Id, true));
            Assert.True(_store.Settings.SetMuted(memory.Id, true));
            Assert.True(_store.Settings.IsMuted(memory.Id));
            Assert.Equal([memory.Id], _store.Settings.Muted());

            Assert.True(_store.Settings.SetMuted(memory.Id, false));
            Assert.False(_store.Settings.IsMuted(memory.Id));

            Assert.False(_store.Settings.SetMuted(Guid.NewGuid(), true));
            Assert.Empty(_store.Settings.Muted());
        }

        [Fact]
        public void A_WAL_file_is_refused_and_left_as_it_is()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_store.DatabasePath)!);

            using (var connection = Raw())
            {
                Assert.Equal("wal", Scalar(connection, "PRAGMA journal_mode = WAL;"));
            }

            var refused = Assert.Throws<OwnMemoriesStoreException>(() => _store.List());
            Assert.Contains("WAL", refused.Message, StringComparison.Ordinal);

            using var check = Raw();
            Assert.Equal("wal", Scalar(check, "PRAGMA journal_mode;"));
            Assert.Equal(0L, Scalar(check, "PRAGMA user_version;"));
        }

        [Fact]
        public void An_unknown_or_higher_version_is_refused()
        {
            _store.Add(Draft("Written by version 1"));

            using (var connection = Raw())
            {
                Execute(connection, "PRAGMA user_version = 2;");
            }

            Assert.Throws<OwnMemoriesStoreException>(() => _store.List());
            Assert.Throws<OwnMemoriesStoreException>(() => _store.Add(Draft("t")));

            using var check = Raw();
            Assert.Equal(2L, Scalar(check, "PRAGMA user_version;"));
            Assert.Equal(1L, Scalar(check, "SELECT count(*) FROM OwnMemory;"));
        }

        [Fact]
        public void A_version_0_file_that_holds_tables_is_someone_elses_and_refused()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_store.DatabasePath)!);

            using (var connection = Raw())
            {
                Execute(connection, "CREATE TABLE Something (Value TEXT);");
            }

            Assert.Throws<OwnMemoriesStoreException>(() => _store.List());
        }

        [Fact]
        public async Task A_read_during_a_write_sees_the_last_commit_and_a_read_meeting_a_lock_waits()
        {
            var kept = _store.Add(Draft("Committed"));

            // A write in progress (reserved lock, separate connection): a read still sees the committed state.
            using (var writer = Raw())
            {
                using var transaction = writer.BeginTransaction();
                using var command = writer.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE OwnMemory SET Title = 'Uncommitted';";
                command.ExecuteNonQuery();

                Assert.Equal("Committed", Assert.Single(_store.List()).Title);
                transaction.Rollback();
            }

            // An exclusive lock (a commit in progress): the read waits on the busy timeout instead of failing.
            using var blocker = Raw();
            Execute(blocker, "BEGIN EXCLUSIVE;");

            var read = Task.Run(() => _store.Get(kept.Id));
            await Task.Delay(400);
            Assert.False(read.IsCompleted);

            Execute(blocker, "COMMIT;");
            var memory = await read.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("Committed", memory!.Title);
        }

        [Fact]
        public void Two_stores_on_the_same_file_write_in_turn()
        {
            var other = new OwnMemoryStore(_root.Path);

            Parallel.For(0, 20, i => (i % 2 == 0 ? _store : other).Add(Draft($"Memory {i}")));

            Assert.Equal(20, _store.Count());
        }

        private static OwnMemoryDraft Draft(string title) => new()
        {
            Title = title,
            Start = new DateOnly(2002, 6, 15),
            Precision = MemoryDatePrecision.Day,
        };

        /// <summary>Distinct synthetic photo files put straight into the photos folder.</summary>
        private string[] Photos(int count)
        {
            Directory.CreateDirectory(_store.PhotosFolder);

            return [.. Enumerable.Range(0, count).Select(i =>
            {
                var name = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([(byte)i, 42])) + ".gif";
                File.WriteAllBytes(Path.Combine(_store.PhotosFolder, name), [.. "GIF89a"u8, (byte)i]);
                return name;
            })];
        }

        private SqliteConnection Raw()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _store.DatabasePath,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        private static object? Scalar(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        private static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private sealed class OwnMemoryComparer : IEqualityComparer<OwnMemory?>
        {
            public bool Equals(OwnMemory? x, OwnMemory? y) =>
                x is not null && y is not null
                && (x.RowID, x.Id, x.Start, x.End, x.Precision, x.Title, x.Summary, x.EveryYear)
                    == (y.RowID, y.Id, y.Start, y.End, y.Precision, y.Title, y.Summary, y.EveryYear)
                && x.Photos.SequenceEqual(y.Photos)
                && x.Links.SequenceEqual(y.Links);

            public int GetHashCode(OwnMemory? obj) => obj?.Id.GetHashCode() ?? 0;
        }
    }
}
