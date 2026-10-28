using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Memories;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>
    /// CH-42 step 5: the memory index built from a source other than an archive (the desktop's memories
    /// file and workspace), whose message sequence numbers need not run 1 to N. Invented data only.
    /// </summary>
    public sealed class MemorySourceTests
    {
        private static readonly ArchiveCollection Holidays = new()
        {
            CollectionID = 2,
            Kind = ArchiveMemoryVocabulary.KindPersonal,
            Name = "Holidays",
            SourceDescription = null,
            ShownByDefault = true,
            PeriodGapMinutes = null,
        };

        [Fact]
        public void SequenceNumbersOfTheSource_PlaceRowsAndNameLinkedMessages()
        {
            var index = MemoryIndex.Build(new MemorySource
            {
                Collections = [Holidays],
                Entries =
                [
                    Entry(20, new DateTime(2026, 4, 6, 9, 30, 0), [Link(1, 20, 40)]),
                    Entry(21, new DateTime(2026, 4, 1), []),
                    Entry(99, new DateTime(2026, 4, 9), [], collectionID: 7),
                ],
                ParticipantNames = new Dictionary<long, string>(),
                Definitions = [],
                IconPath = _ => null,
                MessageTimes = [new DateTime(2026, 4, 6, 9, 0, 0), new DateTime(2026, 4, 6, 10, 0, 0), new DateTime(2026, 4, 7, 9, 0, 0)],
                MessageSequenceNumbers = [10, 40, 41],
            });

            var placements = index.Layout(new MemoryView([2])).Placements;

            Assert.Equal([(21L, 0), (20L, 10)], placements.Select(p => (p.Key.EntryID, p.After)));
            Assert.Null(index.CollectionOf(99));
            Assert.Equal(Holidays, index.CollectionOf(20));

            var link = Assert.Single(index.Details(new MemoryKey(20, null))!.Links);
            Assert.Equal(40, link.SequenceNumber);
            Assert.Equal("A message on " + DateSeparatorRow.DayText(new DateOnly(2026, 4, 6)) + ", 10:00", link.Text);
        }

        [Fact]
        public void ARowAfterTheLastMessage_FollowsItsSequenceNumber()
        {
            var index = MemoryIndex.Build(new MemorySource
            {
                Collections = [Holidays],
                Entries = [Entry(20, new DateTime(2026, 5, 1), [])],
                ParticipantNames = new Dictionary<long, string>(),
                Definitions = [],
                IconPath = _ => null,
                MessageTimes = [new DateTime(2026, 4, 6, 9, 0, 0), new DateTime(2026, 4, 6, 10, 0, 0)],
                MessageSequenceNumbers = [3, 8],
            });

            Assert.Equal(8, Assert.Single(index.Layout(new MemoryView([2])).Placements).After);
        }

        private static MemorySourceEntry Entry(long id, DateTime start, IReadOnlyList<ArchiveEntryLink> links, long collectionID = 2) =>
            new(
                new ArchiveEntry
                {
                    EntryID = id,
                    CollectionID = collectionID,
                    SourceKey = null,
                    Title = "Invented memory " + id,
                    StartLocal = start,
                    EndLocal = null,
                    StartUtc = null,
                    EndUtc = null,
                    DatePrecision = "Day",
                    Summary = null,
                    ImageSha256 = null,
                    ImageExtension = null,
                    Source = "My own memory",
                    DayLevel = null,
                },
                [],
                null,
                links);

        private static ArchiveEntryLink Link(long id, long from, int sequence) => new()
        {
            EntryLinkID = id,
            FromEntryID = from,
            ToEntryID = null,
            ToMessageSequenceNumber = sequence,
        };
    }
}
