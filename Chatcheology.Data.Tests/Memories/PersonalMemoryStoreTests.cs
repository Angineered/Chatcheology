using System.Security.Cryptography;
using System.Text;
using Chatcheology.Data.Memories;
using Chatcheology.Data.Memories.Personal;
using Chatcheology.Data.Tests.Matching;

namespace Chatcheology.Data.Tests.Memories
{
    /// <summary>
    /// Personal memories in the memories file (CH-42 step 4): collections, entries, images and links.
    /// Invented names, dates and bytes only.
    /// </summary>
    public sealed class PersonalMemoryStoreTests : IDisposable
    {
        internal static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5];

        internal static readonly byte[] OtherPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9, 9];

        private readonly MemoriesTestFolder _folder = new();
        private readonly FakeMessages _messages = new(1, 2, 3);
        private readonly PersonalMemoryStore _store;

        public PersonalMemoryStoreTests()
        {
            _store = new PersonalMemoryStore(_folder.MemoriesPath, _messages, maxImageBytes: 64);
        }

        public void Dispose() => _folder.Dispose();

        // ---------------------------------------------------------------------------------------
        // The file.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void NoFile_ReadsAsMissing_AndAWriteWithoutConsentCreatesNothing()
        {
            Assert.False(_store.FileExists);
            Assert.Equal(MemoriesOverview.Missing, _store.ReadOverview());
            Assert.Empty(_store.ReadEntriesOn(new DateOnly(2018, 1, 2)));

            Assert.Throws<FileNotFoundException>(() => _store.CreateCollection("Holidays", createFileIfMissing: false, MemoriesTestFolder.Now));
            Assert.False(File.Exists(_folder.MemoriesPath));

            Assert.Throws<FileNotFoundException>(() => _store.AddEntry(Draft(1)));
            Assert.False(File.Exists(_folder.MemoriesPath));
        }

        [Fact]
        public void TheFirstSave_WithConsent_CreatesTheFileInTheCurrentFormat()
        {
            var id = _store.CreateCollection("  Holidays ", createFileIfMissing: true, MemoriesTestFolder.Now);

            Assert.True(File.Exists(_folder.MemoriesPath));
            Assert.Equal(MemoriesDatabase.CurrentFormatVersion, _folder.Scalar("SELECT FormatVersion FROM MemoriesInfo;"));
            Assert.Equal(["Personal|Holidays|1|<null>"], _folder.Strings(
                $"SELECT Kind || '|' || Name || '|' || ShownByDefault || '|' || ifnull(PeriodGapMinutes, '<null>') FROM Collection WHERE CollectionID = {id};"));
        }

        [Fact]
        public void AFailedFirstSave_RemovesTheNewFile()
        {
            _store.BeforeCommit = () => throw new InvalidOperationException("Synthetic failure.");

            Assert.Throws<InvalidOperationException>(() => _store.CreateCollection("Holidays", true, MemoriesTestFolder.Now));
            Assert.False(File.Exists(_folder.MemoriesPath));
        }

        [Fact]
        public void AnExistingFile_IsNeverRecreated()
        {
            _folder.CreateMemories();
            _store.CreateCollection("One", false, MemoriesTestFolder.Now);
            _store.CreateCollection("Two", true, MemoriesTestFolder.Now);

            Assert.Equal(2, _folder.Scalar("SELECT COUNT(*) FROM Collection;"));
        }

        // ---------------------------------------------------------------------------------------
        // Collections.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void CollectionNames_AreRequired_AndUniqueAmongPersonalOnes()
        {
            _folder.CreateMemories();
            AddImported();
            var holidays = _store.CreateCollection("Holidays", false, MemoriesTestFolder.Now);

            var empty = Assert.Throws<PersonalMemoryValidationException>(() => _store.CreateCollection("  ", false, MemoriesTestFolder.Now));
            Assert.True(empty.Errors.ContainsKey(PersonalFields.CollectionName));

            var taken = Assert.Throws<PersonalMemoryValidationException>(() => _store.CreateCollection(" holidays ", false, MemoriesTestFolder.Now));
            Assert.Equal("You already have a collection with that name.", taken.Errors[PersonalFields.CollectionName]);

            // Unique per kind: an imported collection's name may be reused.
            _store.CreateCollection("Shared gaming", false, MemoriesTestFolder.Now);

            var second = _store.CreateCollection("Birthdays", false, MemoriesTestFolder.Now);
            Assert.Throws<PersonalMemoryValidationException>(() => _store.RenameCollection(second, "HOLIDAYS"));

            // Renaming to its own name (another case) is fine.
            _store.RenameCollection(holidays, "holidays");
            _store.RenameCollection(second, "Family birthdays");

            Assert.Equal(["Family birthdays", "holidays", "Shared gaming"], _folder.Strings(
                "SELECT Name FROM Collection WHERE Kind = 'Personal' ORDER BY Name COLLATE NOCASE;"));
        }

        [Fact]
        public void CollectionNames_AreUniqueIgnoringCaseBeyondAscii()
        {
            _folder.CreateMemories();
            var summer = _store.CreateCollection("Été", false, MemoriesTestFolder.Now);

            var taken = Assert.Throws<PersonalMemoryValidationException>(() => _store.CreateCollection(" été ", false, MemoriesTestFolder.Now));
            Assert.Equal("You already have a collection with that name.", taken.Errors[PersonalFields.CollectionName]);

            var other = _store.CreateCollection("Hiver", false, MemoriesTestFolder.Now);
            Assert.Throws<PersonalMemoryValidationException>(() => _store.RenameCollection(other, "ÉTÉ"));

            // Its own name in another case is fine.
            _store.RenameCollection(summer, "été");
            Assert.Equal(["été", "Hiver"], _folder.Strings("SELECT Name FROM Collection ORDER BY CollectionID;"));
        }

        [Fact]
        public void ImportedCollections_CannotBeRenamedOrAddedTo()
        {
            _folder.CreateMemories();
            var (imported, _) = AddImported();

            var rename = Assert.Throws<PersonalMemoryValidationException>(() => _store.RenameCollection(imported, "Mine now"));
            Assert.Equal("Imported collections are read-only.", rename.Errors[PersonalFields.CollectionName]);

            var add = Assert.Throws<PersonalMemoryValidationException>(() => _store.AddEntry(Draft(imported)));
            Assert.True(add.Errors.ContainsKey(PersonalFields.Collection));

            Assert.Equal("Shared gaming", Assert.Single(_folder.Strings("SELECT Name FROM Collection;")));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM Entry;"));
        }

        [Fact]
        public void TheOverview_ListsPersonalEntriesInDateOrder_AndImportedCollectionsByCount()
        {
            _folder.CreateMemories();
            AddImported();
            var holidays = _store.CreateCollection("Holidays", false, MemoriesTestFolder.Now);
            _store.AddEntry(Draft(holidays) with { Title = "Later", Start = new DateOnly(2019, 3, 1) });
            _store.AddEntry(Draft(holidays) with { Title = "Earlier", Start = new DateOnly(2018, 1, 2), End = new DateOnly(2018, 1, 9) });

            var overview = _store.ReadOverview();

            Assert.True(overview.FileExists);
            var collection = Assert.Single(overview.Personal);
            Assert.Equal(["Earlier", "Later"], collection.Entries.Select(e => e.Title));
            Assert.Equal("2 January 2018 to 9 January 2018", collection.Entries[0].DateText);
            Assert.Equal(new ImportedCollectionSummary(1, "Shared gaming", 1), Assert.Single(overview.Imported));
        }

        // ---------------------------------------------------------------------------------------
        // Entries.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AnEntry_IsStoredInTheExistingConventions()
        {
            var holidays = Holidays();

            var day = _store.AddEntry(Draft(holidays) with
            {
                Title = "  Beach week ",
                Start = new DateOnly(2018, 1, 2),
                End = new DateOnly(2018, 1, 9),
                Summary = "  Invented days by the sea. ",
                Source = " My own memory ",
            });

            var month = _store.AddEntry(Draft(holidays) with
            {
                Start = new DateOnly(2018, 3, 17),
                End = new DateOnly(2018, 5, 30),
                Precision = PersonalDatePrecision.Month,
                Summary = "   ",
            });

            Assert.Equal(["Beach week|2018-01-02T00:00:00|2018-01-09T00:00:00|Day|Invented days by the sea.|My own memory|<n>|<n>|<n>|<n>|<n>|<n>"], Row(day));
            Assert.Equal(["Synthetic trip|2018-03-01T00:00:00|2018-05-01T00:00:00|Month|<n>|Synthetic note|<n>|<n>|<n>|<n>|<n>|<n>"], Row(month));
        }

        [Theory]
        [InlineData("", "Synthetic note", 2018, 2018, PersonalFields.Title)]
        [InlineData("Trip", " ", 2018, 2018, PersonalFields.Source)]
        [InlineData("Trip", "Synthetic note", 2018, 2017, PersonalFields.End)]
        public void AnInvalidDraft_IsRefusedByField(string title, string source, int startYear, int endYear, string field)
        {
            var holidays = Holidays();

            var refused = Assert.Throws<PersonalMemoryValidationException>(() => _store.AddEntry(Draft(holidays) with
            {
                Title = title,
                Source = source,
                Start = new DateOnly(startYear, 6, 1),
                End = new DateOnly(endYear, 6, 1),
            }));

            Assert.True(refused.Errors.ContainsKey(field));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM Entry;"));
        }

        [Fact]
        public void AStartIsRequired_AndAMonthRangeComparesByMonth()
        {
            var holidays = Holidays();

            var missing = Assert.Throws<PersonalMemoryValidationException>(() => _store.AddEntry(Draft(holidays) with { Start = null }));
            Assert.True(missing.Errors.ContainsKey(PersonalFields.Start));

            // The same month, an earlier day: fine at Month precision, refused at Day.
            _store.AddEntry(Draft(holidays) with
            {
                Start = new DateOnly(2018, 3, 20),
                End = new DateOnly(2018, 3, 5),
                Precision = PersonalDatePrecision.Month,
            });

            Assert.Throws<PersonalMemoryValidationException>(() => _store.AddEntry(Draft(holidays) with
            {
                Start = new DateOnly(2018, 3, 20),
                End = new DateOnly(2018, 3, 5),
            }));

            Assert.Equal(["2018-03-01T00:00:00|2018-03-01T00:00:00"], _folder.Strings("SELECT StartLocal || '|' || EndLocal FROM Entry;"));
        }

        [Fact]
        public void Editing_KeepsTheEntryIDAndItsLinks()
        {
            var holidays = Holidays();
            var target = _store.AddEntry(Draft(holidays) with { Title = "Target" });
            var id = _store.AddEntry(Draft(holidays) with
            {
                Links = [PersonalLinkTarget.ToMessage(2), PersonalLinkTarget.ToEntry(target)],
            });

            var linkIDs = _folder.Strings($"SELECT EntryLinkID FROM EntryLink WHERE FromEntryID = {id} ORDER BY EntryLinkID;");

            var details = _store.ReadEntry(id);
            _store.UpdateEntry(id, Draft(holidays) with
            {
                Title = "Renamed",
                Links = details.Links.Select(l => l.Target).ToList(),
            });

            Assert.Equal(linkIDs, _folder.Strings($"SELECT EntryLinkID FROM EntryLink WHERE FromEntryID = {id} ORDER BY EntryLinkID;"));
            Assert.Equal("Renamed", _store.ReadEntry(id).Title);
            Assert.Equal(2, _folder.Scalar("SELECT COUNT(*) FROM Entry;"));

            // Dropping one link removes only that one; adding one appends it.
            _store.UpdateEntry(id, Draft(holidays) with
            {
                Links = [PersonalLinkTarget.ToEntry(target), PersonalLinkTarget.ToMessage(3)],
            });

            var after = _store.ReadEntry(id).Links;
            Assert.Equal([PersonalLinkTarget.ToEntry(target), PersonalLinkTarget.ToMessage(3)], after.Select(l => l.Target));
            Assert.Equal(linkIDs[1], after[0].EntryLinkID.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.StartsWith("Target, ", after[0].EntryDescription, StringComparison.Ordinal);
        }

        [Fact]
        public void Deleting_RemovesTheEntryAndEveryLinkFromOrToIt()
        {
            var holidays = Holidays();
            var a = _store.AddEntry(Draft(holidays) with { Title = "A" });
            var b = _store.AddEntry(Draft(holidays) with { Title = "B", Links = [PersonalLinkTarget.ToEntry(a), PersonalLinkTarget.ToMessage(1)] });
            var c = _store.AddEntry(Draft(holidays) with { Title = "C", Links = [PersonalLinkTarget.ToEntry(b)] });

            Assert.Equal(3, _store.DeleteEntry(b));

            Assert.Equal(["A", "C"], _folder.Strings("SELECT Title FROM Entry ORDER BY Title;"));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM EntryLink;"));
            Assert.Empty(_store.ReadEntry(c).Links);
        }

        [Fact]
        public void ImportedEntries_AreRefusedForReadingIntoTheFormEditingAndDeleting()
        {
            _folder.CreateMemories();
            var (_, importedEntry) = AddImported();
            var holidays = _store.CreateCollection("Holidays", false, MemoriesTestFolder.Now);

            Assert.Throws<PersonalMemoryValidationException>(() => _store.ReadEntry(importedEntry));

            var edit = Assert.Throws<PersonalMemoryValidationException>(() => _store.UpdateEntry(importedEntry, Draft(holidays)));
            Assert.Equal("Imported memories are read-only.", edit.Errors[PersonalFields.Entry]);

            Assert.Throws<PersonalMemoryValidationException>(() => _store.DeleteEntry(importedEntry));

            Assert.Equal(["Synthetic Game|Second"], _folder.Strings("SELECT Title || '|' || DatePrecision FROM Entry;"));
        }

        [Fact]
        public void APersonalMemory_CanLinkToAnImportedEntry()
        {
            _folder.CreateMemories();
            var (_, importedEntry) = AddImported();
            var holidays = _store.CreateCollection("Holidays", false, MemoriesTestFolder.Now);

            var id = _store.AddEntry(Draft(holidays) with { Links = [PersonalLinkTarget.ToEntry(importedEntry)] });

            Assert.Equal(PersonalLinkTarget.ToEntry(importedEntry), Assert.Single(_store.ReadEntry(id).Links).Target);

            var candidates = _store.ReadEntriesOn(new DateOnly(2016, 5, 1));
            Assert.Equal("Synthetic Game", Assert.Single(candidates).Title);
            Assert.Equal("Shared gaming", candidates[0].CollectionName);
        }

        // ---------------------------------------------------------------------------------------
        // Links.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void LinkTargets_MustExist()
        {
            var holidays = Holidays();
            var id = _store.AddEntry(Draft(holidays));

            var message = Assert.Throws<PersonalMemoryValidationException>(() =>
                _store.AddEntry(Draft(holidays) with { Links = [PersonalLinkTarget.ToMessage(99)] }));
            Assert.Equal("A linked message is not in the open conversation.", message.Errors[PersonalFields.Links]);

            var entry = Assert.Throws<PersonalMemoryValidationException>(() =>
                _store.AddEntry(Draft(holidays) with { Links = [PersonalLinkTarget.ToEntry(999)] }));
            Assert.Equal("A linked memory is no longer there.", entry.Errors[PersonalFields.Links]);

            var self = Assert.Throws<PersonalMemoryValidationException>(() =>
                _store.UpdateEntry(id, Draft(holidays) with { Links = [PersonalLinkTarget.ToEntry(id)] }));
            Assert.Equal("A memory cannot link to itself.", self.Errors[PersonalFields.Links]);

            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM Entry;"));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM EntryLink;"));
        }

        [Fact]
        public void DuplicateLinks_AreKeptOnce()
        {
            var holidays = Holidays();
            var id = _store.AddEntry(Draft(holidays) with
            {
                Links = [PersonalLinkTarget.ToMessage(1), PersonalLinkTarget.ToMessage(1)],
            });

            Assert.Single(_store.ReadEntry(id).Links);
        }

        // ---------------------------------------------------------------------------------------
        // Images.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AnImage_IsCopiedByHash_AndTheSourceIsOnlyRead()
        {
            var holidays = Holidays();
            var source = SourceImage("holiday-photo.jpeg", Png);
            var before = File.GetLastWriteTimeUtc(source);

            var id = _store.AddEntry(Draft(holidays) with { Image = PersonalImageChoice.FromFile(source) });

            var sha = Sha(Png);
            var stored = Path.Combine(_folder.MediaFolder, sha + ".png");

            Assert.Equal(Png, File.ReadAllBytes(stored));
            Assert.Equal([$"{sha}|png|{Png.Length}"], _folder.Strings("SELECT Sha256 || '|' || Extension || '|' || SizeBytes FROM MediaFile;"));
            Assert.Equal(sha, _store.ReadEntry(id).ImageSha256);
            Assert.Equal(stored, _store.ReadEntry(id).ImagePath);

            // The chosen file is untouched and still where it was.
            Assert.Equal(Png, File.ReadAllBytes(source));
            Assert.Equal(before, File.GetLastWriteTimeUtc(source));
        }

        [Fact]
        public void TheSameImageTwice_IsStoredOnce()
        {
            var holidays = Holidays();

            _store.AddEntry(Draft(holidays) with { Image = PersonalImageChoice.FromFile(SourceImage("a.png", Png)) });
            _store.AddEntry(Draft(holidays) with { Image = PersonalImageChoice.FromFile(SourceImage("b.png", Png)) });

            Assert.Single(Directory.GetFiles(_folder.MediaFolder));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM MediaFile;"));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(DISTINCT ImageSha256) FROM Entry;"));
        }

        [Fact]
        public void AnEditCanKeepReplaceOrRemoveTheImage()
        {
            var holidays = Holidays();
            var id = _store.AddEntry(Draft(holidays) with { Image = PersonalImageChoice.FromFile(SourceImage("a.png", Png)) });

            _store.UpdateEntry(id, Draft(holidays) with { Image = PersonalImageChoice.Existing(Sha(Png)) });
            Assert.Equal(Sha(Png), _store.ReadEntry(id).ImageSha256);

            _store.UpdateEntry(id, Draft(holidays) with { Image = PersonalImageChoice.FromFile(SourceImage("b.png", OtherPng)) });
            Assert.Equal(Sha(OtherPng), _store.ReadEntry(id).ImageSha256);

            _store.UpdateEntry(id, Draft(holidays));
            Assert.Null(_store.ReadEntry(id).ImageSha256);

            // Stored files stay: another memory, or an archive, may use them.
            Assert.Equal(2, Directory.GetFiles(_folder.MediaFolder).Length);
        }

        [Fact]
        public void AWrongTypeOrAnOversizedImage_IsRefused_AndNothingIsCopied()
        {
            var holidays = Holidays();

            var text = Assert.Throws<PersonalMemoryValidationException>(() => _store.AddEntry(Draft(holidays) with
            {
                Image = PersonalImageChoice.FromFile(SourceImage("looks-like.png", Encoding.ASCII.GetBytes("not an image at all"))),
            }));
            Assert.Equal("The chosen file is not a PNG, JPEG, GIF or WebP image.", text.Errors[PersonalFields.Image]);

            var large = new byte[65];
            Png.CopyTo(large, 0);

            var oversized = Assert.Throws<PersonalMemoryValidationException>(() => _store.AddEntry(Draft(holidays) with
            {
                Image = PersonalImageChoice.FromFile(SourceImage("large.png", large)),
            }));
            Assert.StartsWith("The chosen image is larger than", oversized.Errors[PersonalFields.Image], StringComparison.Ordinal);

            var absent = Assert.Throws<PersonalMemoryValidationException>(() => _store.AddEntry(Draft(holidays) with
            {
                Image = PersonalImageChoice.FromFile(Path.Combine(_folder.Root, "not-there.png")),
            }));
            Assert.True(absent.Errors.ContainsKey(PersonalFields.Image));

            Assert.False(Directory.Exists(_folder.MediaFolder));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM Entry;"));
        }

        [Theory]
        [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1 }, "jpg")]
        [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1 }, "gif")]
        [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50, 1 }, "webp")]
        public void TheTypeComesFromTheContent(byte[] bytes, string extension)
        {
            var file = PersonalImageFile.Read(SourceImage("named-wrongly.png", bytes), 64, out var error);

            Assert.Null(error);
            Assert.Equal(extension, file!.Extension);
        }

        // ---------------------------------------------------------------------------------------
        // One transaction per save.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AFailureBeforeCommit_RollsTheWholeSaveBack_AndRemovesTheCopiedImage()
        {
            var holidays = Holidays();
            var kept = _store.AddEntry(Draft(holidays) with { Title = "Kept", Links = [PersonalLinkTarget.ToMessage(1)] });

            _store.BeforeCommit = () => throw new InvalidOperationException("Synthetic failure.");

            Assert.Throws<InvalidOperationException>(() => _store.AddEntry(Draft(holidays) with
            {
                Image = PersonalImageChoice.FromFile(SourceImage("a.png", Png)),
                Links = [PersonalLinkTarget.ToMessage(2), PersonalLinkTarget.ToEntry(kept)],
            }));

            Assert.Throws<InvalidOperationException>(() => _store.UpdateEntry(kept, Draft(holidays) with { Title = "Changed" }));
            Assert.Throws<InvalidOperationException>(() => _store.DeleteEntry(kept));

            Assert.Equal(["Kept"], _folder.Strings("SELECT Title FROM Entry;"));
            Assert.Equal(1, _folder.Scalar("SELECT COUNT(*) FROM EntryLink;"));
            Assert.Equal(0, _folder.Scalar("SELECT COUNT(*) FROM MediaFile;"));
            Assert.Empty(Directory.Exists(_folder.MediaFolder) ? Directory.GetFiles(_folder.MediaFolder) : []);
        }

        [Fact]
        public void AFailedSave_KeepsAnImageFileThatWasAlreadyThere()
        {
            var holidays = Holidays();
            _store.AddEntry(Draft(holidays) with { Image = PersonalImageChoice.FromFile(SourceImage("a.png", Png)) });

            _store.BeforeCommit = () => throw new InvalidOperationException("Synthetic failure.");

            Assert.Throws<InvalidOperationException>(() =>
                _store.AddEntry(Draft(holidays) with { Image = PersonalImageChoice.FromFile(SourceImage("b.png", Png)) }));

            Assert.Single(Directory.GetFiles(_folder.MediaFolder));
        }

        // ---------------------------------------------------------------------------------------
        // The workspace's messages, read-only.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void WorkspaceMessages_AreFoundByNumberAndDay_InTheOneConversation()
        {
            using var workspace = new MatchingTestWorkspace();
            var day = new DateOnly(2019, 4, 5);

            workspace.AddMessage(day, time: "09:00:00");
            workspace.AddMessage(day, time: "23:59:59");
            workspace.AddMessage(day.AddDays(1), time: "00:00:00");
            workspace.AddMessage(day, MatchingTestWorkspace.OutsiderParticipantID, MatchingTestWorkspace.OtherConversationID, "10:00:00");
            workspace.Execute("UPDATE Message SET MessageContent = 'Synthetic line one' || char(10) || 'and two' WHERE SequenceNumber = 1;");

            var targets = new WorkspaceMessageLinkTargets(workspace.DatabasePath, MatchingTestWorkspace.ConversationID);

            Assert.True(targets.Exists(1));
            Assert.True(targets.Exists(3));
            Assert.False(targets.Exists(4));
            Assert.False(targets.Exists(0));

            var messages = targets.ReadDay(day);
            Assert.Equal([1, 2], messages.Select(m => m.SequenceNumber));
            Assert.Equal("Synthetic line one and two", messages[0].Preview);
            Assert.Equal(new DateTime(2019, 4, 5, 23, 59, 59), messages[1].TimeLocal);
            Assert.Equal("Alex", messages[0].SenderName);
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private long Holidays()
        {
            _folder.CreateMemories();

            return _store.CreateCollection("Holidays", false, MemoriesTestFolder.Now);
        }

        internal static PersonalEntryDraft Draft(long collectionID) => new()
        {
            CollectionID = collectionID,
            Title = "Synthetic trip",
            Start = new DateOnly(2018, 6, 1),
            Precision = PersonalDatePrecision.Day,
            Source = "Synthetic note",
        };

        /// <summary>An imported collection with one entry (period and item), as the importer writes them.</summary>
        private (long Collection, long Entry) AddImported()
        {
            using var connection = MemoriesDatabase.OpenConnection(_folder.MemoriesPath);
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

            return (1, 10);
        }

        private List<string> Row(long entryID) => _folder.Strings(
            $"""
            SELECT Title || '|' || StartLocal || '|' || ifnull(EndLocal, '<n>') || '|' || DatePrecision || '|' ||
                   ifnull(Summary, '<n>') || '|' || ifnull(Source, '<n>') || '|' || ifnull(SourceKey, '<n>') || '|' ||
                   ifnull(StartUtc, '<n>') || '|' || ifnull(EndUtc, '<n>') || '|' || ifnull(ImageSha256, '<n>') || '|' ||
                   ifnull(ImageSourceUrl, '<n>') || '|' || ifnull(DayLevel, '<n>')
            FROM Entry WHERE EntryID = {entryID};
            """);

        private string SourceImage(string name, byte[] bytes)
        {
            var folder = Path.Combine(_folder.Root, "chosen");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, bytes);

            return path;
        }

        internal static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        /// <summary>A conversation holding the given sequence numbers.</summary>
        internal sealed class FakeMessages(params int[] sequenceNumbers) : IMessageLinkTargets
        {
            public bool Exists(int sequenceNumber) => sequenceNumbers.Contains(sequenceNumber);

            public MessageLinkCandidate? Read(int sequenceNumber) =>
                Exists(sequenceNumber) ? new MessageLinkCandidate(sequenceNumber, new DateTime(2019, 4, 5, 9, 0, 0), "Ada", "Synthetic") : null;

            public IReadOnlyList<MessageLinkCandidate> ReadDay(DateOnly day) => [];
        }
    }
}
