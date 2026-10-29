using System.Security.Cryptography;
using System.Text;
using Chatcheology.Archive;
using Chatcheology.Data.ArchiveExport;
using Chatcheology.Data.Memories;
using Chatcheology.Data.OlderExports;
using Chatcheology.Data.Tests.Matching;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.ArchiveExport.ArchiveExportFixture;

namespace Chatcheology.Data.Tests.ArchiveExport
{
    /// <summary>
    /// Archive format v2 from the three files (CH-33 3.4, CH-42): the older exports before the
    /// workspace conversation in one timeline, exact-name restored older files, and the memory tables.
    /// </summary>
    /// <remarks>Invented names, lines and bytes only; every folder is a private temporary one.</remarks>
    public sealed class ArchiveExportVersionTwoTests : IDisposable
    {
        private const long Local = MatchingTestWorkspace.LocalParticipantID;
        private const long Other = MatchingTestWorkspace.OtherParticipantID;

        private const string ImageUrl = "https://image.api.playstation.com/synthetic/art-one.png";

        private static readonly DateTime Now = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);

        private static readonly byte[] ImageBytes = Encoding.ASCII.GetBytes("synthetic game artwork bytes");

        private static string ImageSha => Convert.ToHexStringLower(SHA256.HashData(ImageBytes));

        private const string IconUrl = "https://image.api.playstation.com/synthetic/trophy-icon.png";

        private static readonly byte[] IconBytes = Encoding.ASCII.GetBytes("synthetic trophy icon bytes");

        private static string IconSha => Convert.ToHexStringLower(SHA256.HashData(IconBytes));

        private static readonly byte[] UnusedIconBytes = Encoding.ASCII.GetBytes("synthetic icon nobody uses");

        private static string UnusedIconSha => Convert.ToHexStringLower(SHA256.HashData(UnusedIconBytes));

        private readonly ArchiveExportFixture _fixture = new();
        private readonly string _exports;

        public ArchiveExportVersionTwoTests()
        {
            _exports = Path.Combine(Path.GetTempPath(), "Chatcheology.Data.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_exports);
        }

        public void Dispose()
        {
            _fixture.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_exports, recursive: true);
        }

        private string OlderPath => OlderExportsDatabase.PathBeside(_fixture.DatabasePath);

        private string MemoriesPath => MemoriesDatabase.PathBeside(_fixture.DatabasePath);

        // ---------------------------------------------------------------------------------------
        // The three files.
        // ---------------------------------------------------------------------------------------

        /// <summary>Three text messages (workspace sequence numbers 1 to 3).</summary>
        private void Workspace()
        {
            _fixture.AddText("Workspace one", Local);
            _fixture.AddText("Workspace two", Other);
            _fixture.AddText("Workspace three", Local);
            _fixture.RunEvidence();
        }

        private const string ExportA =
            """
            14/06/2013, 10:00 - A. Example: first ever line
            14/06/2013, 10:05 - Sam S: pic.jpg (file attached)
            a caption
            14/06/2013, 10:06 - A. Example: gone.jpg (file attached)
            15/06/2013, 09:00 - Sam S: <Media omitted>
            """;

        private const string ExportInner =
            """
            26/03/2016, 10:00 - Alex Example: inner one
            01/04/2016, 09:00 - Sam Sample: inner last
            """;

        private const string ExportOuter =
            """
            26/03/2016, 10:00 - Alex Example: inner one
            01/04/2016, 09:00 - Sam Sample: inner last
            02/04/2016, 10:00 - Alex Example: after the cut
            03/04/2016, 11:00 - Sam Sample: after the cut too
            """;

        private const string ExportHidden =
            """
            05/05/2014, 10:00 - Alex Example: hidden line
            05/05/2014, 10:01 - Sam Sample: hidden reply
            """;

        private string CreateExport(string name, string chat, params (string Name, string Content)[] media)
        {
            var folder = Path.Combine(_exports, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "WhatsApp Chat with Sam Sample.txt"), chat.Replace("\r", "", StringComparison.Ordinal), new UTF8Encoding(false));

            foreach (var (file, content) in media)
            {
                File.WriteAllText(Path.Combine(folder, file), content, new UTF8Encoding(false));
            }

            return folder;
        }

        private static void Import(
            string folder, int position, bool shown, string localName, string otherName, OlderExportSource? after, string path) =>
            new OlderExportImporter().Import(path, new OlderExportImportRequest
            {
                Export = OlderExportSource.Load(folder),
                Roles = new Dictionary<string, OlderExportRole>
                {
                    [localName] = OlderExportRole.Local,
                    [otherName] = OlderExportRole.Other,
                },
                Position = position,
                Shown = shown,
                ShownAfter = after,
                ImportedDateTimeUtc = Now,
            });

        /// <summary>
        /// Export A (shown, with an exact-name file, an absent file and a media-omitted line), an inner
        /// export, its outer export shown from the cut, and a hidden export.
        /// </summary>
        /// <returns>Export A's folder.</returns>
        private string OlderExports()
        {
            OlderExportsDatabase.Create(OlderPath, Now);

            var a = CreateExport("export-a", ExportA, ("pic.jpg", "synthetic picture bytes"));
            var inner = CreateExport("export-inner", ExportInner);
            var outer = CreateExport("export-outer", ExportOuter);
            var hidden = CreateExport("export-hidden", ExportHidden);

            Import(a, 1, true, "A. Example", "Sam S", null, OlderPath);
            Import(inner, 2, true, "Alex Example", "Sam Sample", null, OlderPath);
            Import(outer, 3, true, "Alex Example", "Sam Sample", OlderExportSource.Load(inner), OlderPath);
            Import(hidden, 4, false, "Alex Example", "Sam Sample", null, OlderPath);

            return a;
        }

        /// <summary>
        /// An imported collection with two entries sharing one image (periods and items), and a
        /// personal collection with one entry linking to an imported entry and to workspace message 2.
        /// </summary>
        private void Memories(int linkedSequence = 2, bool formatOne = false, bool formatTwo = false, bool formatThree = false)
        {
            if (formatOne)
            {
                MemoriesDatabase.CreateFormatVersionOne(MemoriesPath, Now);
            }
            else if (formatTwo)
            {
                MemoriesDatabase.CreateFormatVersionTwo(MemoriesPath, Now);
            }
            else if (formatThree)
            {
                MemoriesDatabase.CreateFormatVersionThree(MemoriesPath, Now);
            }
            else
            {
                MemoriesDatabase.Create(MemoriesPath, Now);
            }

            var media = MemoriesDatabase.MediaFolderFor(MemoriesPath);
            Directory.CreateDirectory(media);
            File.WriteAllBytes(Path.Combine(media, ImageSha + ".png"), ImageBytes);

            using var connection = MemoriesDatabase.OpenConnection(MemoriesPath);
            using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                INSERT INTO MediaFile (Sha256, Extension, SizeBytes) VALUES ('{ImageSha}', 'png', {ImageBytes.Length});
                INSERT INTO Collection VALUES (1, 'Imported', 'Shared gaming', 'Synthetic shared activity', 0, 60);
                INSERT INTO Collection VALUES (2, 'Personal', 'Holidays', NULL, 1, NULL);

                INSERT INTO Entry (EntryID, CollectionID, SourceKey, Title, StartLocal, EndLocal, StartUtc, EndUtc,
                                   DatePrecision, Summary, ImageSha256, ImageSourceUrl, Source, DayLevel)
                VALUES (10, 1, 'setA|setB|2016-05-01', 'Synthetic Game', '2016-05-01T20:00:00', '2016-05-01T20:00:30',
                        '2016-05-01T18:00:00Z', '2016-05-01T18:00:30Z', 'Second', NULL, '{ImageSha}', '{ImageUrl}',
                        'PSN trophies', 'strong');
                INSERT INTO EntryPeriod VALUES (10, 0, '2016-05-01T18:00:00Z', '2016-05-01T18:00:30Z',
                        '2016-05-01T20:00:00', '2016-05-01T20:00:30', 'strong', 1, 1);
                INSERT INTO EntryItem (EntryItemID, EntryID, PeriodIndex, Role, SetId, ItemId, ItemType, TimeUtc, TimeLocal, Name, IconSha256)
                VALUES (1, 10, 0, 'Local', 'setA', '7', 'bronze', '2016-05-01T18:00:00Z', '2016-05-01T20:00:00', NULL, NULL),
                       (2, 10, 0, 'Other', 'setB', '7', 'bronze', '2016-05-01T18:00:30Z', '2016-05-01T20:00:30', NULL, NULL);

                INSERT INTO Entry (EntryID, CollectionID, SourceKey, Title, StartLocal, EndLocal, StartUtc, EndUtc,
                                   DatePrecision, Summary, ImageSha256, ImageSourceUrl, Source, DayLevel)
                VALUES (11, 1, 'setA|setB|2016-05-02', 'Synthetic Game', '2016-05-02T21:00:00', NULL,
                        '2016-05-02T19:00:00Z', NULL, 'Second', NULL, '{ImageSha}', '{ImageUrl}', 'PSN trophies', 'same-day');
                INSERT INTO EntryPeriod VALUES (11, 0, '2016-05-02T19:00:00Z', '2016-05-02T19:00:00Z',
                        '2016-05-02T21:00:00', '2016-05-02T21:00:00', 'one-account', 0, 1);
                INSERT INTO EntryItem (EntryItemID, EntryID, PeriodIndex, Role, SetId, ItemId, ItemType, TimeUtc, TimeLocal, Name, IconSha256)
                VALUES (3, 11, 0, 'Other', 'setB', '8', 'silver', '2016-05-02T19:00:00Z', '2016-05-02T21:00:00', NULL, NULL);

                INSERT INTO Entry (EntryID, CollectionID, SourceKey, Title, StartLocal, EndLocal, StartUtc, EndUtc,
                                   DatePrecision, Summary, ImageSha256, ImageSourceUrl, Source, DayLevel)
                VALUES (20, 2, NULL, 'Beach week', '2018-01-02T00:00:00', '2018-01-09T00:00:00', NULL, NULL, 'Day',
                        'Invented', NULL, NULL, NULL, NULL);
                INSERT INTO EntryLink VALUES (1, 20, 10, NULL);
                INSERT INTO EntryLink VALUES (2, 20, NULL, {linkedSequence});
                """;
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Trophy definitions (CH-43) in the format 2 memories file: item (setA, 7) named, hidden and with
        /// an icon; item (setB, 7) without a name or icon; and (setZ, 1), which no item uses, with an icon
        /// of its own.
        /// </summary>
        private void Definitions()
        {
            var media = MemoriesDatabase.MediaFolderFor(MemoriesPath);
            File.WriteAllBytes(Path.Combine(media, IconSha + ".png"), IconBytes);
            File.WriteAllBytes(Path.Combine(media, UnusedIconSha + ".jpg"), UnusedIconBytes);

            using var connection = MemoriesDatabase.OpenConnection(MemoriesPath);
            using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                INSERT INTO MediaFile (Sha256, Extension, SizeBytes) VALUES ('{IconSha}', 'png', {IconBytes.Length});
                INSERT INTO MediaFile (Sha256, Extension, SizeBytes) VALUES ('{UnusedIconSha}', 'jpg', {UnusedIconBytes.Length});
                INSERT INTO ItemDefinition (SetId, ItemId, ItemType, Name, Detail, Hidden, GroupId, IconSourceUrl, IconSha256)
                VALUES ('setA', '7', 'bronze', 'Invented Opening Move', 'Open the invented door.', 1, 'default', '{IconUrl}', '{IconSha}'),
                       ('setB', '7', 'bronze', NULL, NULL, NULL, NULL, NULL, NULL),
                       ('setZ', '1', 'gold', 'Never Earned Here', NULL, 0, NULL, '{IconUrl}', '{UnusedIconSha}');
                """;
            command.ExecuteNonQuery();
        }

        private static readonly byte[] CoverBytes = Encoding.ASCII.GetBytes("synthetic personal cover");

        private static string CoverSha => Convert.ToHexStringLower(SHA256.HashData(CoverBytes));

        private static readonly byte[] PhotoBytes = Encoding.ASCII.GetBytes("synthetic extra photo");

        private static string PhotoSha => Convert.ToHexStringLower(SHA256.HashData(PhotoBytes));

        /// <summary>
        /// CH-46: the personal entry gets a cover and two extra photos; the second is the game image both
        /// imported entries use, so its payload is shared.
        /// </summary>
        private void Photos()
        {
            var media = MemoriesDatabase.MediaFolderFor(MemoriesPath);
            File.WriteAllBytes(Path.Combine(media, CoverSha + ".png"), CoverBytes);
            File.WriteAllBytes(Path.Combine(media, PhotoSha + ".jpg"), PhotoBytes);

            using var connection = MemoriesDatabase.OpenConnection(MemoriesPath);
            using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                INSERT INTO MediaFile (Sha256, Extension, SizeBytes) VALUES ('{CoverSha}', 'png', {CoverBytes.Length});
                INSERT INTO MediaFile (Sha256, Extension, SizeBytes) VALUES ('{PhotoSha}', 'jpg', {PhotoBytes.Length});
                UPDATE Entry SET ImageSha256 = '{CoverSha}' WHERE EntryID = 20;
                INSERT INTO EntryImage VALUES (20, 1, '{PhotoSha}', 'jpg', 'By the sea');
                INSERT INTO EntryImage VALUES (20, 2, '{ImageSha}', 'png', NULL);
                """;
            command.ExecuteNonQuery();
        }

        /// <summary>CH-47: three invented noted dates (one with a year and a note, 29 February without a year, one at the year's end).</summary>
        private void NotedDates() => ExecuteMemories(
            """
            INSERT INTO NotedDate VALUES (1, 'Invented anniversary', 2, 14, 2019, 'An invented note');
            INSERT INTO NotedDate VALUES (2, 'Leap day', 2, 29, NULL, NULL);
            INSERT INTO NotedDate VALUES (5, 'Year end', 12, 31, 2000, NULL);
            """);

        private void ExecuteMemories(string sql)
        {
            using (var connection = MemoriesDatabase.OpenConnection(MemoriesPath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();
        }

        private ArchiveExportResult Export(string output) =>
            new ArchiveExporter().Export(_fixture.Request(output, expectResolved: 0, expectMissingReviewed: 0));

        private ArchiveComparisonResult Compare(string output) =>
            new ArchiveWorkspaceComparer().Compare(new ArchiveComparisonRequest
            {
                WorkspacePath = _fixture.DatabasePath,
                ConversationID = MatchingTestWorkspace.ConversationID,
                LocalParticipantID = Local,
                ArchivePath = output,
            });

        private static List<ArchiveMessage> Messages(ArchiveReader reader) =>
            reader.ReadAllPages().SelectMany(p => p.Messages).ToList();

        // ---------------------------------------------------------------------------------------
        // Tests.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void NoOlderExportsAndNoMemoriesFile_IsAVersionTwoArchiveOfTheWorkspaceOnly()
        {
            Workspace();
            var output = _fixture.Output();

            var result = Export(output);

            Assert.Equal(3, result.Counts.WorkspaceMessages);
            Assert.Equal(0, result.Counts.ExportMessages);
            Assert.Equal(0, result.Counts.ExportPeriods);
            Assert.Empty(result.Counts.Collections);
            Assert.True(ArchiveValidator.Validate(output).Passed);

            using (var reader = ArchiveReader.Open(output))
            {
                Assert.Equal(2, reader.Info.FormatVersion);
                Assert.Equal([1, 2, 3], Messages(reader).Select(m => m.SequenceNumber));
                Assert.Empty(reader.ReadCollections());
            }

            var comparison = Compare(output);
            Assert.True(comparison.Passed);
            Assert.Equal(0, comparison.ExportMessages);
            Assert.Empty(comparison.Exports);
            Assert.Empty(comparison.Collections);
        }

        [Fact]
        public void OlderExports_ComeFirst_InPositionOrder_FromTheCut_WithoutTheHiddenExport()
        {
            Workspace();
            OlderExports();
            var output = _fixture.Output();

            var result = Export(output);

            // A: 4 messages; inner: 2; outer from the cut: 2; hidden: none. Then the workspace's 3.
            Assert.Equal(8, result.Counts.ExportMessages);
            Assert.Equal(3, result.Counts.WorkspaceMessages);
            Assert.Equal(3, result.Counts.ExportPeriods);

            var validation = ArchiveValidator.Validate(output);
            Assert.True(validation.Passed, string.Join(", ", validation.FirstProblems));

            using (var reader = ArchiveReader.Open(output))
            {
                var messages = Messages(reader);

                Assert.Equal(Enumerable.Range(1, 11), messages.Select(m => m.SequenceNumber));
                Assert.Equal(
                    [
                        "first ever line", "pic.jpg (file attached)\na caption", "gone.jpg (file attached)", "<Media omitted>",
                        "inner one", "inner last", "after the cut", "after the cut too",
                        "Workspace one", "Workspace two", "Workspace three",
                    ],
                    messages.Select(m => m.Text));
                Assert.All(messages.Take(8), m => Assert.Equal(ArchiveMessageSource.Export, m.Source));
                Assert.All(messages.Skip(8), m => Assert.Equal(ArchiveMessageSource.Workspace, m.Source));
                Assert.All(messages.Take(8), m => Assert.Null(m.IsForwarded));

                // Older identifiers sit above the workspace conversation's.
                var workspaceMax = messages.Skip(8).Max(m => m.MessageID);
                Assert.All(messages.Take(8), m => Assert.True(m.MessageID > workspaceMax));
                Assert.Equal(messages.Take(8).Select(m => m.MessageID).Order(), messages.Take(8).Select(m => m.MessageID));

                Assert.Equal(
                    [
                        new ArchiveExportPeriod(new DateTime(2013, 6, 14, 10, 0, 0), new DateTime(2013, 6, 15, 9, 0, 0)),
                        new ArchiveExportPeriod(new DateTime(2016, 3, 26, 10, 0, 0), new DateTime(2016, 4, 1, 9, 0, 0)),
                        new ArchiveExportPeriod(new DateTime(2016, 4, 2, 10, 0, 0), new DateTime(2016, 4, 3, 11, 0, 0)),
                    ],
                    reader.Info.ExportPeriods);
            }

            var comparison = Compare(output);
            Assert.True(comparison.Passed, string.Join(", ", comparison.FirstMismatches));
            Assert.Equal(8, comparison.ExportMessages);
            Assert.Equal(3, comparison.WorkspaceMessages);
            Assert.Equal(
                [
                    new ArchiveComparisonExportCounts(1, true, 4, 4, 0),
                    new ArchiveComparisonExportCounts(2, true, 2, 2, 0),
                    new ArchiveComparisonExportCounts(3, true, 4, 2, 2),
                    new ArchiveComparisonExportCounts(4, false, 2, 0, 2),
                ],
                comparison.Exports);
        }

        [Fact]
        public void OlderSenders_MapOntoTheArchiveParticipants_ByRole()
        {
            Workspace();
            OlderExports();
            var output = _fixture.Output();
            Export(output);

            using var reader = ArchiveReader.Open(output);

            Assert.Equal([Local, Other], reader.ReadParticipants().Select(p => p.ParticipantID));

            // A. Example and Alex Example are Local; Sam S and Sam Sample are Other, whatever the name.
            Assert.Equal(
                [Local, Other, Local, Other, Local, Other, Local, Other],
                Messages(reader).Take(8).Select(m => m.SenderParticipantID));
        }

        [Fact]
        public void AnExactNameFile_IsRestoredWithItsBasis_AndEverythingElseIsUnmatched()
        {
            Workspace();
            OlderExports();
            var output = _fixture.Output();
            var result = Export(output);

            Assert.Equal(1, result.Counts.Restored);
            Assert.Equal(2, result.Counts.Unmatched);
            Assert.Equal(0, result.Counts.Missing);

            using var reader = ArchiveReader.Open(output);
            var messages = Messages(reader);

            var restored = Assert.Single(messages[1].Attachments);
            Assert.Equal(ArchiveAttachmentState.Restored, restored.State);
            Assert.Equal("pic.jpg", restored.OriginalFileName);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic picture bytes"))), restored.MediaSha256);
            Assert.Equal("ExportFileAttached", restored.Basis!.BasisType);
            Assert.Null(restored.Basis.Actor);
            Assert.Equal(Now, restored.Basis.ConfirmedUtc);
            Assert.DoesNotContain(_exports, restored.Basis.BasisText, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(reader.MediaFullPath(restored)));
            Assert.Equal("synthetic picture bytes", File.ReadAllText(reader.MediaFullPath(restored)!));

            Assert.Equal(ArchiveAttachmentState.Unmatched, Assert.Single(messages[2].Attachments).State);
            Assert.Equal(ArchiveAttachmentState.Unmatched, Assert.Single(messages[3].Attachments).State);
        }

        [Fact]
        public void AnOlderFileWhoseBytesChanged_StopsTheExport()
        {
            Workspace();
            var a = OlderExports();
            File.WriteAllText(Path.Combine(a, "pic.jpg"), "changed after import");
            var output = _fixture.Output();

            var stop = Assert.Throws<ArchiveExportStoppedException>(() => Export(output));

            Assert.Contains("older-export media", stop.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(_exports, stop.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void Memories_RoundTrip_WithPeriodsItemsAndRemappedLinks_AndImagesStoredOnce()
        {
            Workspace();
            OlderExports();
            Memories();
            var output = _fixture.Output();

            var result = Export(output);

            Assert.Equal(
                [new ArchiveCollectionCounts(1, 2, 2, 3), new ArchiveCollectionCounts(2, 1, 0, 0)],
                result.Counts.Collections);
            Assert.Equal(2, result.Counts.EntryLinks);

            var validation = ArchiveValidator.Validate(output);
            Assert.True(validation.Passed, string.Join(", ", validation.FirstProblems));

            using (var reader = ArchiveReader.Open(output))
            {
                var entries = reader.ReadEntries(new DateTime(2000, 1, 1), new DateTime(2100, 1, 1));
                Assert.Equal([10L, 11L, 20L], entries.Select(e => e.EntryID));
                Assert.All(entries.Take(2), e => Assert.Equal(".png", e.ImageExtension));
                Assert.Equal("strong", entries[0].DayLevel);

                var items = reader.ReadPeriods(10).Single().Items;
                Assert.Equal([Local, Other], items.Select(i => i.ParticipantID));
                Assert.Equal(Other, reader.ReadPeriods(11).Single().Items.Single().ParticipantID);

                // Workspace message 2 is archive message 8 + 2 after the eight older messages.
                var links = reader.ReadLinks(20);
                Assert.Equal(10L, links[0].ToEntryID);
                Assert.Equal(10, links[1].ToMessageSequenceNumber);
                Assert.Equal("Workspace two", Messages(reader).Single(m => m.SequenceNumber == 10).Text);

                // One file and one Media row for the image both entries use.
                Assert.Single(Directory.GetFiles(Path.Combine(output, "media"), ImageSha + "*"));
                Assert.Equal(ImageBytes.Length, reader.ReadMediaOf(ImageSha)!.SizeBytes);
            }

            var comparison = Compare(output);
            Assert.True(comparison.Passed, string.Join(", ", comparison.FirstMismatches));
            Assert.Equal(result.Counts.Collections, comparison.Collections);
        }

        [Fact]
        public void UsedItemDefinitions_RoundTrip_WithTheirIcons_AndUnusedOnesAreLeftOut()
        {
            Workspace();
            Memories();
            Definitions();
            var output = _fixture.Output();

            var result = Export(output);

            Assert.Equal(2, result.Counts.ItemDefinitions);

            var validation = ArchiveValidator.Validate(output);
            Assert.True(validation.Passed, string.Join(", ", validation.FirstProblems));

            using (var reader = ArchiveReader.Open(output))
            {
                var definitions = reader.ReadItemDefinitions(10);
                Assert.Equal([("setA", "7"), ("setB", "7")], definitions.Select(d => (d.SetId, d.ItemId)));
                Assert.Equal("Invented Opening Move", definitions[0].Name);
                Assert.Equal("Open the invented door.", definitions[0].Detail);
                Assert.True(definitions[0].Hidden);
                Assert.Equal("default", definitions[0].GroupId);
                Assert.Equal(IconSha, definitions[0].IconSha256);
                Assert.Equal(".png", definitions[0].IconExtension);
                Assert.Equal(IconBytes, File.ReadAllBytes(reader.IconFullPath(definitions[0])!));
                Assert.Null(definitions[1].Name);
                Assert.Null(definitions[1].IconSha256);

                // Entry 11's item (setB, 8) has no definition.
                Assert.Empty(reader.ReadItemDefinitions(11));
                Assert.Equal(2, reader.ReadAllItemDefinitions().Count);

                // The unused definition's icon is not copied and has no Media row.
                Assert.Null(reader.ReadMediaOf(UnusedIconSha));
                Assert.Empty(Directory.GetFiles(Path.Combine(output, "media"), UnusedIconSha + "*"));
            }

            var comparison = Compare(output);
            Assert.True(comparison.Passed, string.Join(", ", comparison.FirstMismatches));
            Assert.Equal(2, comparison.ItemDefinitions);
        }

        [Fact]
        public void AFormatOneMemoriesFile_HasNoDefinitions()
        {
            Workspace();
            Memories(formatOne: true);
            var output = _fixture.Output();

            var result = Export(output);

            Assert.Equal(0, result.Counts.ItemDefinitions);
            Assert.True(ArchiveValidator.Validate(output).Passed);

            using (var reader = ArchiveReader.Open(output))
            {
                Assert.Empty(reader.ReadAllItemDefinitions());
            }

            var comparison = Compare(output);
            Assert.True(comparison.Passed, string.Join(", ", comparison.FirstMismatches));
            Assert.Equal(0, comparison.ItemDefinitions);
        }

        [Fact]
        public void AnItemIconMissingFromTheMemoriesFolder_StopsTheExport()
        {
            Workspace();
            Memories();
            Definitions();
            File.Delete(Path.Combine(MemoriesDatabase.MediaFolderFor(MemoriesPath), IconSha + ".png"));
            var output = _fixture.Output();

            var stop = Assert.Throws<ArchiveExportStoppedException>(() => Export(output));

            Assert.Contains("item icons", stop.Message, StringComparison.Ordinal);
            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void TheComparer_FindsAChangedDefinition_AndANewlyUsedOne()
        {
            Workspace();
            Memories();
            Definitions();
            var output = _fixture.Output();
            Export(output);

            using (var connection = MemoriesDatabase.OpenConnection(MemoriesPath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    UPDATE ItemDefinition SET Name = 'Renamed' WHERE SetId = 'setA' AND ItemId = '7';
                    INSERT INTO ItemDefinition (SetId, ItemId, ItemType) VALUES ('setB', '8', 'silver');
                    """;
                command.ExecuteNonQuery();
            }

            var comparison = Compare(output);

            Assert.Equal(2, comparison.MismatchCounts[ArchiveMismatchKind.ItemDefinitionDiffers]);
            Assert.Equal(0, comparison.MismatchCounts[ArchiveMismatchKind.EntryDiffers]);
        }

        [Fact]
        public void TheArchive_CarriesNoImageSourceUrl()
        {
            Workspace();
            Memories();
            Definitions();
            var output = _fixture.Output();
            Export(output);

            SqliteConnection.ClearAllPools();
            var bytes = File.ReadAllBytes(Path.Combine(output, ArchiveFormat.DatabaseFileName));

            foreach (var forbidden in new[] { ImageUrl, IconUrl, "playstation", "https://", "ImageSourceUrl", "IconSourceUrl", "Never Earned Here" })
            {
                Assert.Equal(-1, IndexOf(bytes, Encoding.UTF8.GetBytes(forbidden)));
                Assert.Equal(-1, IndexOf(bytes, Encoding.Unicode.GetBytes(forbidden)));
            }
        }

        [Fact]
        public void ALinkToAMessageTheWorkspaceDoesNotHold_RefusesTheExport()
        {
            Workspace();
            Memories(linkedSequence: 99);
            var output = _fixture.Output();

            var refusal = Assert.Throws<ArchiveExportRefusedException>(() => Export(output));

            Assert.Contains("memory link", refusal.Message, StringComparison.Ordinal);
            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void ExtraPhotos_RoundTrip_InOrder_WithCaptions_AndPayloadsStoredOnce()
        {
            Workspace();
            Memories();
            Photos();
            var output = _fixture.Output();

            var result = Export(output);

            Assert.Equal(2, result.Counts.EntryImages);

            var validation = ArchiveValidator.Validate(output);
            Assert.True(validation.Passed, string.Join(", ", validation.FirstProblems));

            using (var reader = ArchiveReader.Open(output))
            {
                var entry = reader.ReadEntriesOf(2).Single();
                Assert.Equal(CoverSha, entry.ImageSha256);

                var photos = reader.ReadEntryImages(20);
                Assert.Equal([(1, PhotoSha, ".jpg", "By the sea"), (2, ImageSha, ".png", (string?)null)],
                    photos.Select(p => (p.Ordinal, p.Sha256, p.Extension, p.Caption)));
                Assert.Equal(PhotoBytes, File.ReadAllBytes(reader.EntryImageFullPath(photos[0])));

                // The game image both imported entries and the photo use is one file and one Media row.
                Assert.Single(Directory.GetFiles(Path.Combine(output, "media"), ImageSha + "*"));
                Assert.Equal("Image", reader.ReadMediaOf(PhotoSha)!.MediaType);
            }

            var comparison = Compare(output);
            Assert.True(comparison.Passed, string.Join(", ", comparison.FirstMismatches));

            // A changed caption is an entry difference.
            using (var connection = MemoriesDatabase.OpenConnection(MemoriesPath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE EntryImage SET Caption = 'Renamed' WHERE Ordinal = 1;";
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            Assert.Equal(1, Compare(output).MismatchCounts[ArchiveMismatchKind.EntryDiffers]);
        }

        [Fact]
        public void AFormatTwoMemoriesFile_HasNoExtraPhotos()
        {
            Workspace();
            Memories(formatTwo: true);
            var output = _fixture.Output();

            var result = Export(output);

            Assert.Equal(0, result.Counts.EntryImages);
            Assert.True(ArchiveValidator.Validate(output).Passed);

            using (var reader = ArchiveReader.Open(output))
            {
                Assert.True(reader.HasEntryImages);
                Assert.Empty(reader.ReadAllEntryImages());
            }

            Assert.True(Compare(output).Passed);
        }

        [Fact]
        public void AnExtraPhotoMissingFromTheMemoriesFolder_StopsTheExport()
        {
            Workspace();
            Memories();
            Photos();
            File.Delete(Path.Combine(MemoriesDatabase.MediaFolderFor(MemoriesPath), PhotoSha + ".jpg"));
            var output = _fixture.Output();

            Assert.Throws<ArchiveExportStoppedException>(() => Export(output));
            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void AnEntryImageMissingFromTheMemoriesFolder_StopsTheExport()

        {
            Workspace();
            Memories();
            File.Delete(Path.Combine(MemoriesDatabase.MediaFolderFor(MemoriesPath), ImageSha + ".png"));
            var output = _fixture.Output();

            Assert.Throws<ArchiveExportStoppedException>(() => Export(output));
            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void TheComparer_FindsAChangedEntry_AndAChangedLink()
        {
            Workspace();
            Memories();
            var output = _fixture.Output();
            Export(output);

            using (var connection = MemoriesDatabase.OpenConnection(MemoriesPath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "UPDATE Entry SET Title = 'Renamed' WHERE EntryID = 20; UPDATE EntryLink SET ToMessageSequenceNumber = 3 WHERE EntryLinkID = 2;";
                command.ExecuteNonQuery();
            }

            var comparison = Compare(output);

            Assert.Equal(1, comparison.MismatchCounts[ArchiveMismatchKind.EntryDiffers]);
            Assert.Equal(1, comparison.MismatchCounts[ArchiveMismatchKind.EntryLinkDiffers]);
            Assert.Equal(0, comparison.MismatchCounts[ArchiveMismatchKind.CollectionCountsDiffer]);
        }

        // ---------------------------------------------------------------------------------------
        // Noted dates (CH-47).
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void NotedDates_RoundTrip_AsStored_WithTheirIdentifiers_AndValidate()
        {
            Workspace();
            Memories();
            NotedDates();
            var output = _fixture.Output();

            var result = Export(output);

            Assert.Equal(3, result.Counts.NotedDates);
            Assert.Contains("\"notedDates\": 3", File.ReadAllText(Path.Combine(output, ArchiveFormat.ManifestFileName)), StringComparison.Ordinal);

            var validation = ArchiveValidator.Validate(output);
            Assert.True(validation.Passed, string.Join(", ", validation.FirstProblems));

            using (var reader = ArchiveReader.Open(output))
            {
                Assert.True(reader.HasNotedDates);
                Assert.Equal(
                    [
                        (1L, "Invented anniversary", 2, 14, (int?)2019, (string?)"An invented note"),
                        (2L, "Leap day", 2, 29, null, null),
                        (5L, "Year end", 12, 31, 2000, null),
                    ],
                    reader.ReadNotedDates().Select(d => (d.NotedDateID, d.Title, d.Month, d.Day, d.Year, d.Note)));

                // Not memories: no collection, entry or link was added for them.
                Assert.Equal(2, reader.ReadCollections().Count);
                Assert.Equal(3, reader.ReadAllEntries().Count);
                Assert.Equal(2, reader.ReadAllLinks().Count);
            }

            var comparison = Compare(output);
            Assert.True(comparison.Passed, string.Join(", ", comparison.FirstMismatches));
            Assert.Equal(3, comparison.NotedDates);
        }

        [Fact]
        public void TheComparer_FindsAChangedNotedDate_AMissingOne_AndAnExtraOne()
        {
            Workspace();
            Memories();
            NotedDates();
            var output = _fixture.Output();
            Export(output);

            // Each field on its own is a difference.
            foreach (var change in new[]
                     {
                         "UPDATE NotedDate SET Title = 'Renamed' WHERE NotedDateID = 1;",
                         "UPDATE NotedDate SET Month = 3 WHERE NotedDateID = 1;",
                         "UPDATE NotedDate SET Day = 15 WHERE NotedDateID = 1;",
                         "UPDATE NotedDate SET Year = 2018 WHERE NotedDateID = 1;",
                         "UPDATE NotedDate SET Year = NULL WHERE NotedDateID = 1;",
                         "UPDATE NotedDate SET Note = 'Another note' WHERE NotedDateID = 1;",
                         "UPDATE NotedDate SET Note = NULL WHERE NotedDateID = 1;",
                     })
            {
                ExecuteMemories(change);
                Assert.Equal(1, Compare(output).MismatchCounts[ArchiveMismatchKind.NotedDateDiffers]);
                ExecuteMemories("UPDATE NotedDate SET Title = 'Invented anniversary', Month = 2, Day = 14, Year = 2019, Note = 'An invented note' WHERE NotedDateID = 1;");
                Assert.True(Compare(output).Passed);
            }

            // Missing from the archive (added since), and extra in it (deleted since).
            ExecuteMemories("INSERT INTO NotedDate VALUES (7, 'Added later', 6, 1, NULL, NULL); DELETE FROM NotedDate WHERE NotedDateID = 2;");

            var comparison = Compare(output);

            Assert.Equal(2, comparison.MismatchCounts[ArchiveMismatchKind.NotedDateDiffers]);
            Assert.Equal(0, comparison.MismatchCounts[ArchiveMismatchKind.EntryDiffers]);
            Assert.Equal(0, comparison.MismatchCounts[ArchiveMismatchKind.CollectionCountsDiffer]);
        }

        [Fact]
        public void AFormatThreeMemoriesFile_HasNoNotedDates()
        {
            Workspace();
            Memories(formatThree: true);
            var output = _fixture.Output();

            var result = Export(output);

            Assert.Equal(0, result.Counts.NotedDates);
            Assert.True(ArchiveValidator.Validate(output).Passed);

            using (var reader = ArchiveReader.Open(output))
            {
                Assert.True(reader.HasNotedDates);
                Assert.Empty(reader.ReadNotedDates());
            }

            var comparison = Compare(output);
            Assert.True(comparison.Passed);
            Assert.Equal(0, comparison.NotedDates);

            // The export only read the file: it is still format 3.
            using var connection = MemoriesDatabase.OpenReadOnlyConnection(MemoriesPath);
            Assert.Equal(3, MemoriesDatabase.ReadFormatVersion(connection));
        }

        [Fact]
        public void ANotedDateWithAYearAfterTheExport_StopsTheExport()
        {
            Workspace();
            Memories();

            // The schema cannot know the current year; the archive writer refuses it.
            ExecuteMemories("INSERT INTO NotedDate VALUES (1, 'Invented future', 5, 1, 2999, NULL);");
            var output = _fixture.Output();

            var stop = Assert.Throws<ArchiveExportStoppedException>(() => Export(output));

            Assert.Contains("noted date", stop.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Invented future", stop.Message, StringComparison.Ordinal);
            Assert.Empty(_fixture.ExportsContent());
        }

        [Fact]
        public void ANewOlderExportAfterExporting_IsASourceCountDifference()
        {
            Workspace();
            OlderExports();
            var output = _fixture.Output();
            Export(output);

            Import(CreateExport("export-late", "01/01/2017, 10:00 - Alex Example: late line\n01/01/2017, 10:01 - Sam Sample: late reply\n"), 5, true, "Alex Example", "Sam Sample", null, OlderPath);

            var comparison = Compare(output);

            Assert.False(comparison.Passed);
            Assert.True(comparison.MismatchCounts[ArchiveMismatchKind.SourceCountDiffers] > 0);
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
