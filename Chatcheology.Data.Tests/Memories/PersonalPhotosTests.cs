using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Personal;
using Chatcheology.Data.Memories.Psn;
using Chatcheology.Tests.Memories;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Memories.PersonalMemoryStoreTests;
using static Chatcheology.Tests.Memories.SyntheticPsnFile;

namespace Chatcheology.Data.Tests.Memories
{
    /// <summary>
    /// CH-46 in the memories file: format 3 and its upgrade, several photos per memory, deleting a
    /// personal collection, and tidying up unused images. Invented names and bytes only.
    /// </summary>
    public sealed class PersonalPhotosTests : IDisposable
    {
        private static readonly byte[] Gif = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 2, 3];

        private static readonly byte[] ThirdPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 7, 7, 7, 7];

        private readonly MemoriesTestFolder _folder = new();
        private readonly PersonalMemoryStore _store;

        public PersonalPhotosTests()
        {
            _store = new PersonalMemoryStore(_folder.MemoriesPath, new FakeMessages(1, 2, 3), maxImageBytes: 64);
        }

        public void Dispose() => _folder.Dispose();

        // ---------------------------------------------------------------------------------------
        // Format 3.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ANewFile_IsTheCurrentFormat_WithTheEntryImageTable()
        {
            _folder.CreateMemories();

            Assert.Equal(MemoriesDatabase.CurrentFormatVersion, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'EntryImage';"));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void AnOlderFile_OpensReadOnly_WithNoExtraPhotos_AndIsUpgradedOnTheFirstWrite(int version)
        {
            CreateOlder(version);
            var collection = InsertPersonalCollectionRaw();
            var entry = InsertPersonalEntryRaw(collection);
            var before = DumpRows(["MediaFile", "Collection", "Entry", "EntryLink"]);

            // Read-only: works, shows no extra photos, writes nothing.
            var details = _store.ReadEntry(entry);
            Assert.Empty(details.ExtraPhotos);
            Assert.Single(_store.ReadOverview().Personal);
            Assert.Equal(version, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'EntryImage';"));

            // The first write upgrades, in the same transaction, keeping every row.
            _store.RenameCollection(collection, "Renamed");

            Assert.Equal(MemoriesDatabase.CurrentFormatVersion, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'EntryImage';"));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'ItemDefinition';"));
            Assert.Equal(before.Where(r => !r.StartsWith("Collection", StringComparison.Ordinal)),
                DumpRows(["MediaFile", "Collection", "Entry", "EntryLink"]).Where(r => !r.StartsWith("Collection", StringComparison.Ordinal)));
            Assert.Equal(["2018-01-01T00:00:00Z"], _folder.Strings("SELECT CreatedDateTimeUtc FROM MemoriesInfo;"));
        }

        [Fact]
        public void AFormatThreeMarkerWithoutTheEntryImageTable_IsRefused()
        {
            _folder.CreateMemories();
            Execute("DROP TABLE EntryImage;");

            Assert.Throws<InvalidOperationException>(() => MemoriesDatabase.OpenConnection(_folder.MemoriesPath));
            Assert.Throws<InvalidOperationException>(() => MemoriesDatabase.OpenReadOnlyConnection(_folder.MemoriesPath));
        }

        [Fact]
        public void AFailedFirstWrite_LeavesAnOlderFileAtItsFormat()
        {
            CreateOlder(2);
            var collection = InsertPersonalCollectionRaw();
            _store.BeforeCommit = () => throw new InvalidOperationException("Synthetic failure.");

            Assert.Throws<InvalidOperationException>(() => _store.RenameCollection(collection, "Renamed"));

            Assert.Equal(2, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'EntryImage';"));
        }

        [Fact]
        public void AnUpgradedFile_HasTheSameLayoutAsANewOne()
        {
            using var created = new MemoriesTestFolder();
            created.CreateMemories();

            foreach (var version in new[] { 1, 2 })
            {
                using var older = new MemoriesTestFolder();
                Create(older, version);

                using (var connection = MemoriesDatabase.OpenConnection(older.MemoriesPath))
                using (var transaction = connection.BeginTransaction())
                {
                    Assert.True(MemoriesDatabase.UpgradeToCurrent(connection, transaction));
                    transaction.Commit();
                }

                SqliteConnection.ClearAllPools();

                const string Layout = "SELECT type || '|' || name || '|' || ifnull(sql, '') FROM sqlite_master ORDER BY type, name;";
                Assert.Equal(created.Strings(Layout), older.Strings(Layout));
            }
        }

        [Fact]
        public void TheFile_RefusesExtraPhotosOnAnImportedEntry_AndAWrongExtension()
        {
            _folder.CreateMemories();
            var (_, imported) = AddImported(_folder);
            var collection = _store.CreateCollection("Holidays", false, MemoriesTestFolder.Now);
            var personal = _store.AddEntry(Draft(collection) with { Image = PersonalImageChoice.FromFile(Source("a.png", Png)) });

            Assert.Throws<SqliteException>(() => Execute(
                $"INSERT INTO EntryImage VALUES ({imported}, 1, '{Sha(Png)}', 'png', NULL);"));
            Assert.Throws<SqliteException>(() => Execute(
                $"INSERT INTO EntryImage VALUES ({personal}, 1, '{Sha(Png)}', 'jpg', NULL);"));
        }

        // ---------------------------------------------------------------------------------------
        // Several photos.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void SeveralPhotos_AreStoredByHash_InOrder_WithCaptions()
        {
            var collection = Holidays();
            var cover = Source("cover.png", Png);
            var second = Source("second.png", OtherPng);
            var third = Source("third.gif", Gif);

            var id = _store.AddEntry(Draft(collection) with
            {
                Image = PersonalImageChoice.FromFile(cover),
                ExtraPhotos =
                [
                    new PersonalPhotoDraft(PersonalImageChoice.FromFile(second), "  By the sea  "),
                    new PersonalPhotoDraft(PersonalImageChoice.FromFile(third), "   "),
                ],
            });

            Assert.Equal([Sha(Png)], _folder.Strings($"SELECT ImageSha256 FROM Entry WHERE EntryID = {id};"));
            Assert.Equal(
                [$"1|{Sha(OtherPng)}|png|By the sea", $"2|{Sha(Gif)}|gif|<null>"],
                _folder.Strings($"SELECT Ordinal || '|' || Sha256 || '|' || Extension || '|' || ifnull(Caption, '<null>') FROM EntryImage WHERE EntryID = {id} ORDER BY Ordinal;"));
            Assert.Equal(3, _folder.Scalar("SELECT COUNT(*) FROM MediaFile;"));
            Assert.True(File.Exists(Path.Combine(_folder.MediaFolder, Sha(Gif) + ".gif")));

            // The chosen files are only read.
            Assert.Equal(OtherPng, File.ReadAllBytes(second));

            var details = _store.ReadEntry(id);
            Assert.Equal([Sha(OtherPng), Sha(Gif)], details.ExtraPhotos.Select(p => p.Sha256));
            Assert.Equal(["By the sea", null], details.ExtraPhotos.Select(p => p.Caption));
            Assert.All(details.ExtraPhotos, p => Assert.True(File.Exists(p.Path)));
        }

        [Fact]
        public void AnEdit_CanReorderRecaptionRemoveAndChangeTheCover_KeepingStoredFiles()
        {
            var collection = Holidays();
            var id = _store.AddEntry(Draft(collection) with
            {
                Image = PersonalImageChoice.FromFile(Source("cover.png", Png)),
                ExtraPhotos =
                [
                    new PersonalPhotoDraft(PersonalImageChoice.FromFile(Source("b.png", OtherPng)), "B"),
                    new PersonalPhotoDraft(PersonalImageChoice.FromFile(Source("c.gif", Gif)), "C"),
                ],
            });

            // The gif becomes the cover; the old cover moves after the second photo; captions change.
            _store.UpdateEntry(id, Draft(collection) with
            {
                Image = PersonalImageChoice.Existing(Sha(Gif)),
                ExtraPhotos =
                [
                    new PersonalPhotoDraft(PersonalImageChoice.Existing(Sha(OtherPng)), "B again"),
                    new PersonalPhotoDraft(PersonalImageChoice.Existing(Sha(Png)), null),
                ],
            });

            Assert.Equal([Sha(Gif)], _folder.Strings($"SELECT ImageSha256 FROM Entry WHERE EntryID = {id};"));
            Assert.Equal(
                [$"1|{Sha(OtherPng)}|B again", $"2|{Sha(Png)}|<null>"],
                _folder.Strings($"SELECT Ordinal || '|' || Sha256 || '|' || ifnull(Caption, '<null>') FROM EntryImage ORDER BY Ordinal;"));

            // Removing the extra photos keeps their files.
            _store.UpdateEntry(id, Draft(collection) with { Image = PersonalImageChoice.Existing(Sha(Gif)) });

            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM EntryImage;"));
            Assert.True(File.Exists(Path.Combine(_folder.MediaFolder, Sha(Png) + ".png")));
            Assert.True(File.Exists(Path.Combine(_folder.MediaFolder, Sha(OtherPng) + ".png")));
        }

        [Fact]
        public void ExtraPhotos_NeedACover_AndEachPhotoOnce()
        {
            var collection = Holidays();

            var noCover = Assert.Throws<PersonalMemoryValidationException>(() => _store.AddEntry(Draft(collection) with
            {
                ExtraPhotos = [new PersonalPhotoDraft(PersonalImageChoice.FromFile(Source("b.png", OtherPng)), null)],
            }));
            Assert.True(noCover.Errors.ContainsKey(PersonalFields.Image));

            var twice = Assert.Throws<PersonalMemoryValidationException>(() => _store.AddEntry(Draft(collection) with
            {
                Image = PersonalImageChoice.FromFile(Source("a.png", Png)),
                ExtraPhotos = [new PersonalPhotoDraft(PersonalImageChoice.FromFile(Source("copy.png", Png)), null)],
            }));
            Assert.Equal("The same photo is in this memory more than once.", twice.Errors[PersonalFields.Image]);

            var longCaption = Assert.Throws<PersonalMemoryValidationException>(() => _store.AddEntry(Draft(collection) with
            {
                Image = PersonalImageChoice.FromFile(Source("a.png", Png)),
                ExtraPhotos = [new PersonalPhotoDraft(PersonalImageChoice.FromFile(Source("b.png", OtherPng)), new string('x', 501))],
            }));
            Assert.True(longCaption.Errors.ContainsKey(PersonalFields.Image));

            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM Entry;"));
            Assert.False(Directory.Exists(_folder.MediaFolder) && Directory.EnumerateFiles(_folder.MediaFolder).Any());
        }

        [Fact]
        public void AFailingPhoto_IsNamedByItsFile_AndTheWholeSaveIsRefused()
        {
            var collection = Holidays();

            var refused = Assert.Throws<PersonalMemoryValidationException>(() => _store.AddEntry(Draft(collection) with
            {
                Image = PersonalImageChoice.FromFile(Source("a.png", Png)),
                ExtraPhotos =
                [
                    new PersonalPhotoDraft(PersonalImageChoice.FromFile(Source("notes.png", "not an image"u8.ToArray())), null),
                    new PersonalPhotoDraft(PersonalImageChoice.FromFile(Source("huge.png", [.. Png, .. new byte[100]])), null),
                ],
            }));

            var message = refused.Errors[PersonalFields.Image];
            Assert.Contains("notes.png: The chosen file is not a PNG, JPEG, GIF or WebP image.", message, StringComparison.Ordinal);
            Assert.Contains("huge.png: The chosen image is larger than", message, StringComparison.Ordinal);
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM Entry;"));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM MediaFile;"));
        }

        [Fact]
        public void AFailureBeforeCommit_RemovesEveryPhotoFileThisSaveCopiedIn()
        {
            var collection = Holidays();
            _store.BeforeCommit = () => throw new InvalidOperationException("Synthetic failure.");

            Assert.Throws<InvalidOperationException>(() => _store.AddEntry(Draft(collection) with
            {
                Image = PersonalImageChoice.FromFile(Source("a.png", Png)),
                ExtraPhotos = [new PersonalPhotoDraft(PersonalImageChoice.FromFile(Source("b.gif", Gif)), "B")],
            }));

            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM EntryImage;"));
            Assert.Empty(Directory.EnumerateFiles(_folder.MediaFolder));
        }

        [Fact]
        public void DeletingAMemory_RemovesItsPhotoRows_AndKeepsTheFiles()
        {
            var collection = Holidays();
            var id = _store.AddEntry(Draft(collection) with
            {
                Image = PersonalImageChoice.FromFile(Source("a.png", Png)),
                ExtraPhotos = [new PersonalPhotoDraft(PersonalImageChoice.FromFile(Source("b.gif", Gif)), "B")],
            });

            _store.DeleteEntry(id);

            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM EntryImage;"));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM Entry;"));
            Assert.True(File.Exists(Path.Combine(_folder.MediaFolder, Sha(Gif) + ".gif")));
        }

        [Fact]
        public void APsnReImportAndEnrichment_OnAFormatThreeFile_KeepThePhotoRows()
        {
            var path = PsnTrophyEnricherTests.ImportStandard(_folder);
            var collection = _store.CreateCollection("Holidays", false, MemoriesTestFolder.Now);
            _store.AddEntry(Draft(collection) with
            {
                Image = PersonalImageChoice.FromFile(Source("a.png", Png)),
                ExtraPhotos = [new PersonalPhotoDraft(PersonalImageChoice.FromFile(Source("b.gif", Gif)), "B")],
            });
            var photos = _folder.Strings("SELECT EntryID || '|' || Ordinal || '|' || Sha256 || '|' || Caption FROM EntryImage;");

            var file = Standard();
            new PsnImporter().Import(path, PsnSharedActivityReader.Parse(file.ToBytes(), LocalPrefix, OtherPrefix, file.PairAPrefix));
            PsnTrophyEnricher.Enrich(path, PsnTrophyDefinitionsReader.Parse(SyntheticTrophyDefinitions.Standard().ToBytes()));
            SqliteConnection.ClearAllPools();

            Assert.Equal(photos, _folder.Strings("SELECT EntryID || '|' || Ordinal || '|' || Sha256 || '|' || Caption FROM EntryImage;"));
            Assert.Equal(MemoriesDatabase.CurrentFormatVersion, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
        }

        // ---------------------------------------------------------------------------------------
        // Deleting a collection.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AnEmptyCollection_IsDeleted()
        {
            var collection = Holidays();

            Assert.Equal(new CollectionDeleteResult(0, 0, 0), _store.DeleteCollection(collection, 0));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM Collection;"));
        }

        [Fact]
        public void ACollectionWithMemories_IsDeletedWithThem_TheirLinksBothWays_AndTheirPhotos()
        {
            var holidays = Holidays();
            var other = _store.CreateCollection("Birthdays", false, MemoriesTestFolder.Now);

            var first = _store.AddEntry(Draft(holidays) with
            {
                Image = PersonalImageChoice.FromFile(Source("a.png", Png)),
                ExtraPhotos = [new PersonalPhotoDraft(PersonalImageChoice.FromFile(Source("b.gif", Gif)), "B")],
                Links = [PersonalLinkTarget.ToMessage(2)],
            });
            var second = _store.AddEntry(Draft(holidays) with { Links = [PersonalLinkTarget.ToEntry(first)] });
            var kept = _store.AddEntry(Draft(other) with { Links = [PersonalLinkTarget.ToEntry(second), PersonalLinkTarget.ToMessage(3)] });

            // Told 2, the collection holds 2: deleted.
            var result = _store.DeleteCollection(holidays, 2);

            Assert.Equal(new CollectionDeleteResult(2, 3, 1), result);
            Assert.Equal(["Birthdays"], _folder.Strings("SELECT Name FROM Collection;"));
            Assert.Equal([kept.ToString(System.Globalization.CultureInfo.InvariantCulture)], _folder.Strings("SELECT EntryID FROM Entry;"));
            Assert.Equal(["<null>|3"], _folder.Strings("SELECT ifnull(ToEntryID, '<null>') || '|' || ToMessageSequenceNumber FROM EntryLink;"));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM EntryImage;"));
            Assert.True(File.Exists(Path.Combine(_folder.MediaFolder, Sha(Gif) + ".gif")));
        }

        [Fact]
        public void ADeleteIsRefused_ForAnImportedCollection_OrWhenTheCountChanged()
        {
            _folder.CreateMemories();
            var (imported, _) = AddImported(_folder);
            var holidays = _store.CreateCollection("Holidays", false, MemoriesTestFolder.Now);
            _store.AddEntry(Draft(holidays));

            var refused = Assert.Throws<PersonalMemoryValidationException>(() => _store.DeleteCollection(imported, 1));
            Assert.Equal("Imported collections are read-only.", refused.Errors[PersonalFields.Collection]);

            Assert.Throws<PersonalMemoryValidationException>(() => _store.DeleteCollection(holidays, 0));
            Assert.Throws<PersonalMemoryValidationException>(() => _store.DeleteCollection(999, 0));

            Assert.Equal(2, _folder.Scalar("SELECT COUNT(*) FROM Collection;"));
            Assert.Equal(2, _folder.Scalar("SELECT COUNT(*) FROM Entry;"));
        }

        // ---------------------------------------------------------------------------------------
        // Tidying up images.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void TidyUp_ListsAndDeletesOnlyUnusedStoredFiles_NeverUsedOnesOrOtherFiles()
        {
            var path = PsnTrophyEnricherTests.ImportStandard(_folder);
            PsnTrophyEnricher.Enrich(path, PsnTrophyDefinitionsReader.Parse(SyntheticTrophyDefinitions.Standard().ToBytes()));

            // A game image and a trophy icon, fetched as the PSN import does.
            MemoryImageFetcher.FetchMissing(path, new MemoryImageFetcherTests.FakeSource(new()
            {
                [Quest.Icon!] = MemoryImageFetcherTests.Png,
                [SyntheticTrophyDefinitions.QuestIconOne] = MemoryImageFetcherTests.Jpeg,
            }));
            SqliteConnection.ClearAllPools();

            var collection = _store.CreateCollection("Holidays", false, MemoriesTestFolder.Now);
            var id = _store.AddEntry(Draft(collection) with
            {
                Image = PersonalImageChoice.FromFile(Source("a.png", Png)),
                ExtraPhotos = [new PersonalPhotoDraft(PersonalImageChoice.FromFile(Source("b.gif", Gif)), "B")],
            });

            // Replacing the extra photo leaves the gif unused; ThirdPng is a stray stored file.
            _store.UpdateEntry(id, Draft(collection) with
            {
                Image = PersonalImageChoice.Existing(Sha(Png)),
                ExtraPhotos = [new PersonalPhotoDraft(PersonalImageChoice.FromFile(Source("c.png", OtherPng)), null)],
            });

            File.WriteAllBytes(Path.Combine(_folder.MediaFolder, Sha(ThirdPng) + ".png"), ThirdPng);
            File.WriteAllText(Path.Combine(_folder.MediaFolder, "notes.txt"), "synthetic");
            File.WriteAllBytes(Path.Combine(_folder.MediaFolder, Sha(ThirdPng) + ".bmp"), ThirdPng);
            Directory.CreateDirectory(Path.Combine(_folder.MediaFolder, "sub"));

            var survey = _store.SurveyUnusedImages();

            Assert.Equal(
                new[] { Sha(Gif) + ".gif", Sha(ThirdPng) + ".png" }.Order(StringComparer.Ordinal),
                survey.UnusedFiles);
            Assert.Equal(Gif.Length + ThirdPng.Length, survey.UnusedBytes);
            Assert.Equal(4, survey.UsedFiles);
            Assert.Equal(3, survey.OtherItems);

            var result = _store.DeleteUnusedImages(survey.UnusedFiles);

            Assert.Equal(new UnusedImagesDeleteResult(2, Gif.Length + ThirdPng.Length, 0, 0, 0), result);

            var left = Directory.EnumerateFileSystemEntries(_folder.MediaFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(
                new[]
                {
                    Sha(Png) + ".png", Sha(OtherPng) + ".png",
                    Sha(MemoryImageFetcherTests.Png) + ".png", Sha(MemoryImageFetcherTests.Jpeg) + ".jpg",
                    "notes.txt", Sha(ThirdPng) + ".bmp", "sub",
                }.Order(StringComparer.Ordinal),
                left);

            // The memories file was not changed.
            Assert.Equal(1, _folder.Scalar($"SELECT COUNT(*) FROM MediaFile WHERE Sha256 = '{Sha(Gif)}';"));
        }

        [Fact]
        public void TidyUp_ChecksAgainWhenDeleting_AndNeverDeletesAFileNowInUse()
        {
            var collection = Holidays();
            var stray = Path.Combine(_folder.MediaFolder, Sha(Gif) + ".gif");
            Directory.CreateDirectory(_folder.MediaFolder);
            File.WriteAllBytes(stray, Gif);

            var survey = _store.SurveyUnusedImages();
            Assert.Equal([Sha(Gif) + ".gif"], survey.UnusedFiles);

            // Between the listing and the confirmation, a memory starts using it.
            _store.AddEntry(Draft(collection) with { Image = PersonalImageChoice.FromFile(Source("again.gif", Gif)) });

            var result = _store.DeleteUnusedImages([.. survey.UnusedFiles, "..\\memories.db", "notes.txt"]);

            Assert.Equal(new UnusedImagesDeleteResult(0, 0, 1, 0, 2), result);
            Assert.True(File.Exists(stray));
            Assert.True(File.Exists(_folder.MemoriesPath));
        }

        [Fact]
        public void TidyUp_EveryColumnCounts_EvenTheUnusedItemIconColumn()
        {
            _folder.CreateMemories();
            AddImported(_folder);
            Directory.CreateDirectory(_folder.MediaFolder);
            File.WriteAllBytes(Path.Combine(_folder.MediaFolder, Sha(Gif) + ".gif"), Gif);
            Execute($"INSERT INTO MediaFile VALUES ('{Sha(Gif)}', 'gif', {Gif.Length}); UPDATE EntryItem SET IconSha256 = '{Sha(Gif)}';");

            var survey = _store.SurveyUnusedImages();

            Assert.Empty(survey.UnusedFiles);
            Assert.Equal(1, survey.UsedFiles);
        }

        [Fact]
        public void TidyUp_WithNoMemoriesFile_ListsNothing_AndRefusesToDelete()
        {
            Directory.CreateDirectory(_folder.MediaFolder);
            File.WriteAllBytes(Path.Combine(_folder.MediaFolder, Sha(Gif) + ".gif"), Gif);

            Assert.Equal(UnusedImagesSurvey.Empty, _store.SurveyUnusedImages());
            Assert.Throws<FileNotFoundException>(() => _store.DeleteUnusedImages([Sha(Gif) + ".gif"]));
            Assert.True(File.Exists(Path.Combine(_folder.MediaFolder, Sha(Gif) + ".gif")));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private long Holidays()
        {
            _folder.CreateMemories();

            return _store.CreateCollection("Holidays", false, MemoriesTestFolder.Now);
        }

        private void CreateOlder(int version) => Create(_folder, version);

        private static void Create(MemoriesTestFolder folder, int version)
        {
            var created = new DateTime(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            if (version == 1)
            {
                MemoriesDatabase.CreateFormatVersionOne(folder.MemoriesPath, created);
            }
            else
            {
                MemoriesDatabase.CreateFormatVersionTwo(folder.MemoriesPath, created);
            }
        }

        private long InsertPersonalCollectionRaw()
        {
            Execute("INSERT INTO Collection VALUES (5, 'Personal', 'Holidays', NULL, 1, NULL);");
            return 5;
        }

        private long InsertPersonalEntryRaw(long collection)
        {
            Execute(
                $"""
                INSERT INTO Entry (EntryID, CollectionID, SourceKey, Title, StartLocal, EndLocal, StartUtc, EndUtc,
                                   DatePrecision, Summary, ImageSha256, ImageSourceUrl, Source, DayLevel)
                VALUES (7, {collection}, NULL, 'Synthetic trip', '2018-06-01T00:00:00', NULL, NULL, NULL, 'Day', NULL, NULL, NULL, 'Synthetic note', NULL);
                """);
            return 7;
        }

        /// <summary>An imported collection with one entry (period and item), as the importer writes them.</summary>
        private static (long Collection, long Entry) AddImported(MemoriesTestFolder folder)
        {
            using (var connection = MemoriesDatabase.OpenConnection(folder.MemoriesPath))
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO Collection VALUES (1, 'Imported', 'Shared gaming', 'Synthetic shared activity', 0, 60);
                    INSERT INTO Entry (EntryID, CollectionID, SourceKey, Title, StartLocal, EndLocal, StartUtc, EndUtc,
                                       DatePrecision, Summary, ImageSha256, ImageSourceUrl, Source, DayLevel)
                    VALUES (10, 1, 'setA|setB|2016-05-01', 'Synthetic Game', '2016-05-01T20:00:00', NULL,
                            '2016-05-01T18:00:00Z', NULL, 'Second', NULL, NULL, NULL, 'PSN trophies', 'strong');
                    INSERT INTO EntryPeriod VALUES (10, 0, '2016-05-01T18:00:00Z', '2016-05-01T18:00:00Z',
                            '2016-05-01T20:00:00', '2016-05-01T20:00:00', 'one-account', 1, 0);
                    INSERT INTO EntryItem (EntryItemID, EntryID, PeriodIndex, Role, SetId, ItemId, ItemType, TimeUtc, TimeLocal, Name, IconSha256)
                    VALUES (1, 10, 0, 'Local', 'setA', '7', 'bronze', '2016-05-01T18:00:00Z', '2016-05-01T20:00:00', NULL, NULL);
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            return (1, 10);
        }

        private void Execute(string sql)
        {
            try
            {
                using var connection = new SqliteConnection($"Data Source={_folder.MemoriesPath};Foreign Keys=True");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        private List<string> DumpRows(string[] tables)
        {
            var rows = new List<string>();

            foreach (var table in tables)
            {
                using var connection = new SqliteConnection($"Data Source={_folder.MemoriesPath};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM {table} ORDER BY 1;";
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    var values = Enumerable.Range(0, reader.FieldCount)
                        .Select(i => reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture));
                    rows.Add(table + "|" + string.Join("|", values));
                }
            }

            SqliteConnection.ClearAllPools();

            return rows;
        }

        private string Source(string name, byte[] bytes)
        {
            var folder = Path.Combine(_folder.Root, "chosen");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, bytes);

            return path;
        }
    }
}
