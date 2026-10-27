using System.Security.Cryptography;
using System.Text;

namespace Chatcheology.Archive.Tests
{
    /// <summary>
    /// Format version 2: a version 1 archive still reads and validates; message sources, export
    /// periods and the memory tables round trip through the writer, reader and validator.
    /// </summary>
    /// <remarks>Invented names, text and bytes only.</remarks>
    public sealed class ArchiveVersionTwoTests : IDisposable
    {
        private readonly TemporaryFolder _folder = new();

        public void Dispose() => _folder.Dispose();

        private static readonly byte[] ImageBytes = Encoding.ASCII.GetBytes("synthetic entry artwork");

        private static string ImageSha => ArchiveHashing.Hex(SHA256.HashData(ImageBytes));

        private static readonly byte[] IconBytes = Encoding.ASCII.GetBytes("synthetic trophy icon");

        private static string IconSha => ArchiveHashing.Hex(SHA256.HashData(IconBytes));

        // ---------------------------------------------------------------------------------------
        // Version 1.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AVersionOneArchive_StillReadsAndValidates_WithNoMemories()
        {
            ArchiveSample.WriteFolder(_folder.Path, gapless: true, formatVersion: 1);

            // The manifest exactly as a version 1 build wrote it: no version 2 counts at all.
            var manifestPath = Path.Combine(_folder.Path, ArchiveFormat.ManifestFileName);
            File.WriteAllText(
                manifestPath,
                """
                {
                  "format": "chatcheology-portable-archive",
                  "formatVersion": 1,
                  "producerVersion": "Synthetic producer 1.0",
                  "createdUtc": "2026-10-19T08:30:15Z",
                  "counts": {
                    "participants": 2,
                    "messages": 6,
                    "attachments": 5,
                    "restored": 3,
                    "missing": 1,
                    "unmatched": 1,
                    "mediaFiles": 2
                  }
                }

                """.Replace("\r", "", StringComparison.Ordinal));
            File.WriteAllBytes(Path.Combine(_folder.Path, ArchiveContents.FileName), ArchiveContents.Build(_folder.Path));

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.True(result.Passed, string.Join(", ", result.FirstProblems));

            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.Equal(1, reader.Info.FormatVersion);
            Assert.False(reader.HasMemories);
            Assert.Empty(reader.Info.ExportPeriods);
            Assert.All(reader.ReadAllPages().SelectMany(p => p.Messages), m => Assert.Equal(ArchiveMessageSource.Workspace, m.Source));
            Assert.Empty(reader.ReadCollections());
            Assert.Empty(reader.ReadEntries(DateTime.MinValue, DateTime.MaxValue));
            Assert.Empty(reader.ReadEntriesOf(1));
            Assert.Empty(reader.ReadPeriods(1));
            Assert.Empty(reader.ReadLinks(1));
        }

        // ---------------------------------------------------------------------------------------
        // Version 2.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void SourcesExportPeriodsAndMemories_RoundTrip_AndValidate()
        {
            var counts = WriteMemoryArchive(_folder.Path);

            Assert.Equal(1, counts.ExportMessages);
            Assert.Equal(2, counts.WorkspaceMessages);
            Assert.Equal(1, counts.ExportPeriods);
            Assert.Equal(2, counts.EntryLinks);
            Assert.Equal(2, counts.ItemDefinitions);
            Assert.Equal(
                [new ArchiveCollectionCounts(1, 1, 2, 3), new ArchiveCollectionCounts(2, 1, 0, 0)],
                counts.Collections);

            var result = ArchiveValidator.Validate(_folder.Path);
            Assert.True(result.Passed, string.Join(", ", result.FirstProblems));

            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.Equal(2, reader.Info.FormatVersion);
            Assert.Equal(
                [new ArchiveExportPeriod(new DateTime(2015, 1, 1, 10, 0, 0), new DateTime(2015, 1, 1, 10, 0, 0))],
                reader.Info.ExportPeriods);

            var messages = reader.ReadAllPages().SelectMany(p => p.Messages).ToList();
            Assert.Equal(
                [ArchiveMessageSource.Export, ArchiveMessageSource.Workspace, ArchiveMessageSource.Workspace],
                messages.Select(m => m.Source));

            var collections = reader.ReadCollections();
            Assert.Equal(["Shared gaming", "Holidays"], collections.Select(c => c.Name));
            Assert.Equal(60, collections[0].PeriodGapMinutes);
            Assert.False(collections[0].ShownByDefault);
            Assert.Null(collections[1].PeriodGapMinutes);

            var all = reader.ReadEntries(new DateTime(2000, 1, 1), new DateTime(2100, 1, 1));
            Assert.Equal([10L, 20L], all.Select(e => e.EntryID));

            var game = all[0];
            Assert.Equal("setA|setB|2016-05-01", game.SourceKey);
            Assert.Equal("strong", game.DayLevel);
            Assert.Equal(ImageSha, game.ImageSha256);
            Assert.Equal("media/" + ImageSha + ".png", game.ImageRelativePath);
            Assert.True(File.Exists(reader.ImageFullPath(game)));
            Assert.Equal("Image", reader.ReadMediaOf(ImageSha)!.MediaType);

            // A range is start-inclusive and end-exclusive on the entry's start.
            Assert.Equal([10L], reader.ReadEntries(game.StartLocal, game.StartLocal.AddSeconds(1)).Select(e => e.EntryID));
            Assert.Empty(reader.ReadEntries(game.StartLocal.AddSeconds(1), new DateTime(2016, 5, 2)));
            Assert.Equal([20L], reader.ReadEntriesOf(2).Select(e => e.EntryID));

            var periods = reader.ReadPeriods(10);
            Assert.Equal(["strong", "one-account"], periods.Select(p => p.Label));
            Assert.Equal([ArchiveSample.Local, ArchiveSample.Other], periods[0].Items.Select(i => i.ParticipantID));
            Assert.Equal(new DateTime(2016, 5, 1, 18, 0, 0, DateTimeKind.Utc), periods[0].Items[0].TimeUtc);
            Assert.Equal(DateTimeKind.Utc, periods[0].Items[0].TimeUtc.Kind);
            Assert.Single(periods[1].Items);

            var links = reader.ReadLinks(20);
            Assert.Equal(2, links.Count);
            Assert.Equal(10L, links[0].ToEntryID);
            Assert.Equal(3, links[1].ToMessageSequenceNumber);
            Assert.Equal(2, reader.ReadAllLinks().Count);
        }

        [Fact]
        public void ItemDefinitions_RoundTrip_WithTheirIcons_ForAnEntrysItemsOnly()
        {
            WriteMemoryArchive(_folder.Path);

            var result = ArchiveValidator.Validate(_folder.Path);
            Assert.True(result.Passed, string.Join(", ", result.FirstProblems));

            using var reader = ArchiveReader.Open(_folder.Path);

            var definitions = reader.ReadItemDefinitions(10);
            Assert.Equal([("setA", "1"), ("setB", "2")], definitions.Select(d => (d.SetId, d.ItemId)));

            var named = definitions[0];
            Assert.Equal("Invented First Steps", named.Name);
            Assert.Equal("Reach the invented first door.", named.Detail);
            Assert.True(named.Hidden);
            Assert.Equal("default", named.GroupId);
            Assert.Equal(IconSha, named.IconSha256);
            Assert.Equal("media/" + IconSha + ".png", named.IconRelativePath);
            Assert.True(File.Exists(reader.IconFullPath(named)));
            Assert.Equal("Image", reader.ReadMediaOf(IconSha)!.MediaType);

            // A definition without a name or icon is carried as it is.
            Assert.Null(definitions[1].Name);
            Assert.Null(definitions[1].IconRelativePath);
            Assert.Null(reader.IconFullPath(definitions[1]));

            // Item 3 (setB, "3") has no definition; the personal entry has no items.
            Assert.Empty(reader.ReadItemDefinitions(20));
            Assert.Equal(2, reader.ReadAllItemDefinitions().Count);
        }

        [Fact]
        public void AnItemIconWhoseFileIsGoneOrDifferent_FailsMedia()
        {
            WriteMemoryArchive(_folder.Path);
            var icon = Path.Combine(_folder.Path, "media", IconSha + ".png");
            File.WriteAllBytes(icon, Encoding.ASCII.GetBytes("altered"));

            Assert.Equal(1, ArchiveValidator.Validate(_folder.Path).ProblemCounts[ArchiveProblemKind.ItemIconFileDiffers]);

            File.Delete(icon);
            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.ItemIconFileMissing]);
            Assert.False(result.Checks.Single(c => c.Check == ArchiveCheck.Media).Passed);
        }

        [Fact]
        public void AnUnusedItemDefinition_IsFound_AndTheWriterRefusesOne()
        {
            WriteMemoryArchive(_folder.Path);
            ArchiveSample.Tamper(_folder.Path, "INSERT INTO ItemDefinition (SetId, ItemId, ItemType) VALUES ('setZ', '99', 'gold');");

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.ItemDefinitionUnused]);
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.ManifestCountDiffers]);
            Assert.False(result.Checks.Single(c => c.Check == ArchiveCheck.LinksAndBasis).Passed);

            var database = Path.Combine(_folder.Path, "second.db");

            using var writer = Begin(database);
            writer.WriteItemDefinition(new ArchiveItemDefinition
            {
                SetId = "setZ",
                ItemId = "99",
                ItemType = "gold",
                Name = null,
                Detail = null,
                Hidden = null,
                GroupId = null,
                IconSha256 = null,
                IconExtension = null,
            });

            var refusal = Assert.Throws<InvalidOperationException>(() => writer.Complete());
            Assert.Contains("item definitions", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TheWriter_RefusesAnIconWithoutAnExtension()
        {
            using var writer = Begin(Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName));

            Assert.Throws<InvalidOperationException>(() => writer.WriteItemDefinition(new ArchiveItemDefinition
            {
                SetId = "setA",
                ItemId = "1",
                ItemType = "bronze",
                Name = null,
                Detail = null,
                Hidden = null,
                GroupId = null,
                IconSha256 = IconSha,
                IconExtension = null,
            }));
        }

        [Fact]
        public void AnEntryImageWhoseFileIsGone_FailsMedia()
        {
            WriteMemoryArchive(_folder.Path);
            File.Delete(Path.Combine(_folder.Path, "media", ImageSha + ".png"));

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.False(result.Passed);
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.EntryImageFileMissing]);
            Assert.False(result.Checks.Single(c => c.Check == ArchiveCheck.Media).Passed);
        }

        [Fact]
        public void AnEntryImageWithOtherBytes_FailsMedia()
        {
            WriteMemoryArchive(_folder.Path);
            File.WriteAllBytes(Path.Combine(_folder.Path, "media", ImageSha + ".png"), Encoding.ASCII.GetBytes("altered"));

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.EntryImageFileDiffers]);
        }

        [Fact]
        public void ATamperedPeriodCount_AndALinkFromAnImportedEntry_FailLinks()
        {
            WriteMemoryArchive(_folder.Path);
            ArchiveSample.Tamper(_folder.Path, "UPDATE EntryPeriod SET LocalItemCount = 2 WHERE EntryID = 10 AND PeriodIndex = 0;");
            ArchiveSample.Tamper(_folder.Path, "UPDATE EntryLink SET FromEntryID = 10, ToEntryID = 20 WHERE EntryLinkID = 1;");

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.PeriodCountsDiffer]);
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.LinkNotFromPersonal]);
            Assert.False(result.Checks.Single(c => c.Check == ArchiveCheck.LinksAndBasis).Passed);
        }

        [Fact]
        public void ADanglingLink_AndAManifestCollectionCount_AreFound()
        {
            WriteMemoryArchive(_folder.Path);
            ArchiveSample.Tamper(
                _folder.Path, "PRAGMA foreign_keys = OFF; UPDATE EntryLink SET ToMessageSequenceNumber = 99 WHERE EntryLinkID = 2;");
            ArchiveSample.Tamper(_folder.Path, "DELETE FROM EntryItem WHERE EntryItemID = 3;");

            var result = ArchiveValidator.Validate(_folder.Path);

            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.MemoryRowDangling]);

            // The deleted item leaves its period's count wrong and the manifest's collection count wrong.
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.PeriodCountsDiffer]);
            Assert.Equal(1, result.ProblemCounts[ArchiveProblemKind.ManifestCountDiffers]);
        }

        [Fact]
        public void TheWriter_RefusesALinkFromAnImportedEntry_AndCountsThatDifferFromItems()
        {
            var database = Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName);

            using (var writer = Begin(database))
            {
                writer.WriteEntryLink(new ArchiveEntryLink { EntryLinkID = 9, FromEntryID = 10, ToEntryID = 20, ToMessageSequenceNumber = null });

                var refusal = Assert.Throws<InvalidOperationException>(() => writer.Complete());
                Assert.Contains("personal", refusal.Message, StringComparison.Ordinal);
            }

            File.Delete(database);

            using (var writer = Begin(database, localItems: 2))
            {
                var refusal = Assert.Throws<InvalidOperationException>(() => writer.Complete());
                Assert.Contains("item counts", refusal.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void TheWriter_RefusesAnExportMessageWithAForwardedFact()
        {
            using var writer = ArchiveWriter.Create(Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName), ArchiveSample.Info());

            Assert.Throws<InvalidOperationException>(() => writer.WriteMessage(Message(1, 1, "2015-01-01T10:00:00", ArchiveMessageSource.Export, forwarded: false)));
        }

        [Fact]
        public void AnExportPeriodKeyGap_IsRefusedByTheReader()
        {
            WriteMemoryArchive(_folder.Path);
            ArchiveSample.Tamper(_folder.Path, "INSERT INTO ArchiveInfo (Key, Value) VALUES ('ExportPeriod.3', '2015-01-01T10:00:00/2015-01-01T10:00:00');");

            Assert.Throws<ArchiveFormatException>(() => ArchiveReader.Open(_folder.Path));
        }

        // ---------------------------------------------------------------------------------------
        // The archive.
        // ---------------------------------------------------------------------------------------

        private static ArchiveMessage Message(
            long id, int sequence, string time, ArchiveMessageSource source, bool? forwarded = null) => new()
        {
            MessageID = id,
            SequenceNumber = sequence,
            DateTimeLocal = DateTime.Parse(time, System.Globalization.CultureInfo.InvariantCulture),
            Kind = ArchiveMessageKind.User,
            SenderParticipantID = sequence % 2 == 0 ? ArchiveSample.Local : ArchiveSample.Other,
            Text = "Message " + sequence,
            IsForwarded = forwarded,
            ForwardScore = null,
            Attachments = [],
            Source = source,
        };

        private static ArchiveEntryItem Item(long id, long entry, int period, long participant, string utc) => new()
        {
            EntryItemID = id,
            EntryID = entry,
            PeriodIndex = period,
            ParticipantID = participant,
            SetId = participant == ArchiveSample.Local ? "setA" : "setB",
            ItemId = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ItemType = "bronze",
            TimeUtc = DateTime.SpecifyKind(DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc),
            TimeLocal = DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture).AddHours(2),
        };

        /// <summary>A writer with participants, messages and both collections written, not completed.</summary>
        private static ArchiveWriter Begin(string database, int localItems = 1)
        {
            var info = ArchiveSample.Info();

            var writer = ArchiveWriter.Create(database, new ArchiveInfo
            {
                FormatVersion = info.FormatVersion,
                CreatedUtc = info.CreatedUtc,
                ProducerVersion = info.ProducerVersion,
                ConversationTitle = info.ConversationTitle,
                SourceExportSha256 = info.SourceExportSha256,
                TimeZoneID = info.TimeZoneID,
                LocalParticipantID = info.LocalParticipantID,
                ExportPeriods = [new ArchiveExportPeriod(new DateTime(2015, 1, 1, 10, 0, 0), new DateTime(2015, 1, 1, 10, 0, 0))],
            });

            foreach (var participant in ArchiveSample.Participants)
            {
                writer.WriteParticipant(participant);
            }

            writer.WriteMedia(new ArchiveMedia
            {
                Sha256 = ImageSha,
                MediaType = "Image",
                SizeBytes = ImageBytes.Length,
                DurationMS = null,
                Width = null,
                Height = null,
            });

            writer.WriteMessage(Message(900, 1, "2015-01-01T10:00:00", ArchiveMessageSource.Export));
            writer.WriteMessage(Message(1, 2, "2017-09-06T20:39:00", ArchiveMessageSource.Workspace));
            writer.WriteMessage(Message(2, 3, "2017-09-06T20:40:00", ArchiveMessageSource.Workspace, forwarded: true));

            writer.WriteCollection(new ArchiveCollection
            {
                CollectionID = 1,
                Kind = ArchiveMemoryVocabulary.KindImported,
                Name = "Shared gaming",
                SourceDescription = "Synthetic activity",
                ShownByDefault = false,
                PeriodGapMinutes = 60,
            });

            writer.WriteCollection(new ArchiveCollection
            {
                CollectionID = 2,
                Kind = ArchiveMemoryVocabulary.KindPersonal,
                Name = "Holidays",
                SourceDescription = null,
                ShownByDefault = true,
                PeriodGapMinutes = null,
            });

            writer.WriteEntry(new ArchiveEntry
            {
                EntryID = 10,
                CollectionID = 1,
                SourceKey = "setA|setB|2016-05-01",
                Title = "Synthetic Game",
                StartLocal = new DateTime(2016, 5, 1, 20, 0, 0),
                EndLocal = new DateTime(2016, 5, 1, 23, 30, 0),
                StartUtc = new DateTime(2016, 5, 1, 18, 0, 0, DateTimeKind.Utc),
                EndUtc = new DateTime(2016, 5, 1, 21, 30, 0, DateTimeKind.Utc),
                DatePrecision = "Second",
                Summary = null,
                ImageSha256 = ImageSha,
                ImageExtension = ".png",
                Source = "PSN trophies",
                DayLevel = "strong",
                Periods =
                [
                    new ArchiveEntryPeriod
                    {
                        EntryID = 10,
                        PeriodIndex = 0,
                        StartUtc = new DateTime(2016, 5, 1, 18, 0, 0, DateTimeKind.Utc),
                        EndUtc = new DateTime(2016, 5, 1, 18, 0, 30, DateTimeKind.Utc),
                        StartLocal = new DateTime(2016, 5, 1, 20, 0, 0),
                        EndLocal = new DateTime(2016, 5, 1, 20, 0, 30),
                        Label = "strong",
                        LocalItemCount = localItems,
                        OtherItemCount = 1,
                        Items =
                        [
                            Item(1, 10, 0, ArchiveSample.Local, "2016-05-01T18:00:00"),
                            Item(2, 10, 0, ArchiveSample.Other, "2016-05-01T18:00:30"),
                        ],
                    },
                    new ArchiveEntryPeriod
                    {
                        EntryID = 10,
                        PeriodIndex = 1,
                        StartUtc = new DateTime(2016, 5, 1, 21, 30, 0, DateTimeKind.Utc),
                        EndUtc = new DateTime(2016, 5, 1, 21, 30, 0, DateTimeKind.Utc),
                        StartLocal = new DateTime(2016, 5, 1, 23, 30, 0),
                        EndLocal = new DateTime(2016, 5, 1, 23, 30, 0),
                        Label = "one-account",
                        LocalItemCount = 0,
                        OtherItemCount = 1,
                        Items = [Item(3, 10, 1, ArchiveSample.Other, "2016-05-01T21:30:00")],
                    },
                ],
            });

            writer.WriteEntry(new ArchiveEntry
            {
                EntryID = 20,
                CollectionID = 2,
                SourceKey = null,
                Title = "Beach week",
                StartLocal = new DateTime(2018, 1, 2),
                EndLocal = new DateTime(2018, 1, 9),
                StartUtc = null,
                EndUtc = null,
                DatePrecision = "Day",
                Summary = "Invented",
                ImageSha256 = null,
                ImageExtension = null,
                Source = null,
                DayLevel = null,
            });

            return writer;
        }

        /// <summary>A whole version 2 archive folder with one export message, two collections and links.</summary>
        private static ArchiveCounts WriteMemoryArchive(string folder)
        {
            ArchiveCounts counts;

            using (var writer = Begin(Path.Combine(folder, ArchiveFormat.DatabaseFileName)))
            {
                writer.WriteEntryLink(new ArchiveEntryLink { EntryLinkID = 1, FromEntryID = 20, ToEntryID = 10, ToMessageSequenceNumber = null });
                writer.WriteEntryLink(new ArchiveEntryLink { EntryLinkID = 2, FromEntryID = 20, ToEntryID = null, ToMessageSequenceNumber = 3 });

                writer.WriteMedia(new ArchiveMedia
                {
                    Sha256 = IconSha,
                    MediaType = "Image",
                    SizeBytes = IconBytes.Length,
                    DurationMS = null,
                    Width = null,
                    Height = null,
                });

                writer.WriteItemDefinition(new ArchiveItemDefinition
                {
                    SetId = "setA",
                    ItemId = "1",
                    ItemType = "bronze",
                    Name = "Invented First Steps",
                    Detail = "Reach the invented first door.",
                    Hidden = true,
                    GroupId = "default",
                    IconSha256 = IconSha,
                    IconExtension = ".png",
                });

                writer.WriteItemDefinition(new ArchiveItemDefinition
                {
                    SetId = "setB",
                    ItemId = "2",
                    ItemType = "bronze",
                    Name = null,
                    Detail = null,
                    Hidden = null,
                    GroupId = null,
                    IconSha256 = null,
                    IconExtension = null,
                });

                counts = writer.Complete();
            }

            var media = Path.Combine(folder, ArchiveFormat.MediaFolderName);
            Directory.CreateDirectory(media);
            File.WriteAllBytes(Path.Combine(media, ImageSha + ".png"), ImageBytes);
            File.WriteAllBytes(Path.Combine(media, IconSha + ".png"), IconBytes);

            File.WriteAllBytes(
                Path.Combine(folder, ArchiveFormat.ManifestFileName),
                new ArchiveManifest(ArchiveManifest.FormatName, ArchiveFormat.FormatVersion, "Synthetic producer 1.0", ArchiveSample.Created, counts).ToBytes());
            File.WriteAllBytes(Path.Combine(folder, ArchiveContents.FileName), ArchiveContents.Build(folder));

            return counts;
        }
    }
}
