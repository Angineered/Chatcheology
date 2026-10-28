using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Settings;
using Chatcheology.Mobile.Tests.Support;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// A format version 2 archive (older-export messages before the workspace's, and memory tables)
    /// installs, validates and opens; the timeline shows the combined messages as messages, and the
    /// shown-by-default personal memory as a memory card.
    /// </summary>
    public sealed class VersionTwoArchiveTests : IDisposable
    {
        private readonly TemporaryFolder _root = new();

        public void Dispose() => _root.Dispose();

        private static ArchiveMessage Message(long id, int sequence, DateTime at, ArchiveMessageSource source) => new()
        {
            MessageID = id,
            SequenceNumber = sequence,
            DateTimeLocal = at,
            Kind = ArchiveMessageKind.User,
            SenderParticipantID = sequence % 2 == 0 ? TestArchives.Local : TestArchives.Other,
            Text = "Message " + sequence,
            IsForwarded = null,
            ForwardScore = null,
            Attachments = [],
            Source = source,
        };

        private static void Write(string folder)
        {
            var created = new DateTime(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);
            ArchiveCounts counts;

            using (var writer = ArchiveWriter.Create(Path.Combine(folder, ArchiveFormat.DatabaseFileName), new ArchiveInfo
            {
                FormatVersion = ArchiveFormat.FormatVersion,
                CreatedUtc = created,
                ProducerVersion = "Synthetic producer 1.0",
                ConversationTitle = "Alex and Sam",
                SourceExportSha256 = string.Empty,
                TimeZoneID = string.Empty,
                LocalParticipantID = TestArchives.Local,
                ExportPeriods = [new ArchiveExportPeriod(new DateTime(2015, 1, 1, 9, 0, 0), new DateTime(2015, 1, 2, 9, 0, 0))],
            }))
            {
                writer.WriteParticipant(new ArchiveParticipant { ParticipantID = TestArchives.Local, DisplayName = "Alex", IsLocal = true });
                writer.WriteParticipant(new ArchiveParticipant { ParticipantID = TestArchives.Other, DisplayName = "Sam", IsLocal = false });

                writer.WriteMessage(Message(900, 1, new DateTime(2015, 1, 1, 9, 0, 0), ArchiveMessageSource.Export));
                writer.WriteMessage(Message(901, 2, new DateTime(2015, 1, 2, 9, 0, 0), ArchiveMessageSource.Export));
                writer.WriteMessage(Message(1, 3, new DateTime(2017, 9, 6, 20, 39, 0), ArchiveMessageSource.Workspace));
                writer.WriteMessage(Message(2, 4, new DateTime(2017, 9, 6, 20, 40, 0), ArchiveMessageSource.Workspace));

                writer.WriteCollection(new ArchiveCollection
                {
                    CollectionID = 1,
                    Kind = ArchiveMemoryVocabulary.KindPersonal,
                    Name = "Holidays",
                    SourceDescription = null,
                    ShownByDefault = true,
                    PeriodGapMinutes = null,
                });

                writer.WriteEntry(new ArchiveEntry
                {
                    EntryID = 1,
                    CollectionID = 1,
                    SourceKey = null,
                    Title = "Beach week",
                    StartLocal = new DateTime(2016, 1, 2),
                    EndLocal = null,
                    StartUtc = null,
                    EndUtc = null,
                    DatePrecision = "Day",
                    Summary = null,
                    ImageSha256 = null,
                    ImageExtension = null,
                    Source = null,
                    DayLevel = null,
                });

                writer.WriteEntryLink(new ArchiveEntryLink { EntryLinkID = 1, FromEntryID = 1, ToEntryID = null, ToMessageSequenceNumber = 3 });

                counts = writer.Complete();
            }

            File.WriteAllBytes(
                Path.Combine(folder, ArchiveFormat.ManifestFileName),
                new ArchiveManifest(ArchiveManifest.FormatName, ArchiveFormat.FormatVersion, "Synthetic producer 1.0", created, counts).ToBytes());
            File.WriteAllBytes(Path.Combine(folder, ArchiveContents.FileName), ArchiveContents.Build(folder));
        }

        [Fact]
        public void A_version_two_archive_installs_opens_and_shows_the_combined_messages_and_its_memory_card()
        {
            var library = new ArchiveLibrary(Path.Combine(_root.Path, "library"));
            var settings = new ArchiveSettings(new MemorySettings());

            var staging = library.CreateStagingFolder();
            Write(staging);

            var outcome = library.Install(staging);
            Assert.True(outcome.Installed);

            var archive = library.ListArchives(settings).Single();
            var opened = ArchiveSession.OpenConversation(archive, settings);

            using var session = opened.Session;
            Assert.Equal(2, session.FormatVersion);

            opened.Timeline.OpenAt(1);

            var rows = TestArchives.Describe(opened.Timeline.Rows);

            Assert.Equal(["M 1", "M 2", "M 3", "M 4"], rows.Where(r => r.StartsWith('M')).ToList());

            // The personal collection is shown by default (CH-42 step 3): its one entry is a memory card,
            // never a message, and the messages keep their numbers.
            var memory = Assert.Single(opened.Timeline.Rows.OfType<MemoryRow>());
            Assert.IsType<EntryCardRow>(memory);
            Assert.Equal(new DateOnly(2016, 1, 2), memory.Day);
            Assert.Equal([1, 2, 3, 4], opened.Timeline.Rows.OfType<MessageRow>().Select(m => m.SequenceNumber));
        }
    }
}
