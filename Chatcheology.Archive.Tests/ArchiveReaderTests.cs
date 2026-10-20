using System.Security.Cryptography;

namespace Chatcheology.Archive.Tests
{
    /// <summary>
    /// What <see cref="ArchiveWriter"/> writes, <see cref="ArchiveReader"/> reads back, page by page and
    /// day by day, without change.
    /// </summary>
    public sealed class ArchiveReaderTests : IDisposable
    {
        private readonly TemporaryFolder _folder = new();

        public ArchiveReaderTests() => ArchiveSample.WriteFolder(_folder.Path);

        public void Dispose() => _folder.Dispose();

        [Fact]
        public void Info_RoundTrips()
        {
            using var reader = ArchiveReader.Open(_folder.Path);
            var expected = ArchiveSample.Info();

            Assert.Equal(ArchiveFormat.FormatVersion, reader.Info.FormatVersion);
            Assert.Equal(expected.CreatedUtc, reader.Info.CreatedUtc);
            Assert.Equal(DateTimeKind.Utc, reader.Info.CreatedUtc.Kind);
            Assert.Equal(expected.ProducerVersion, reader.Info.ProducerVersion);
            Assert.Equal(expected.ConversationTitle, reader.Info.ConversationTitle);
            Assert.Equal(expected.SourceExportSha256, reader.Info.SourceExportSha256);
            Assert.Equal(expected.TimeZoneID, reader.Info.TimeZoneID);
            Assert.Equal(expected.LocalParticipantID, reader.Info.LocalParticipantID);
        }

        [Fact]
        public void Participants_RoundTrip()
        {
            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.Equal(
                ArchiveSample.Participants.Select(p => (p.ParticipantID, p.DisplayName, p.IsLocal)),
                reader.ReadParticipants().Select(p => (p.ParticipantID, p.DisplayName, p.IsLocal)));
        }

        [Fact]
        public void EveryMessageAndAttachment_RoundTrips()
        {
            using var reader = ArchiveReader.Open(_folder.Path);
            var page = reader.ReadFirstPage(ArchiveReader.MaximumPageSize);

            Assert.Equal(ArchiveSample.Messages.Count, reader.ReadMessageCount());
            Assert.Equal(ArchiveSample.Messages.Count, page.Messages.Count);

            foreach (var (expected, actual) in ArchiveSample.Messages.Zip(page.Messages))
            {
                Assert.Equal(expected.MessageID, actual.MessageID);
                Assert.Equal(expected.SequenceNumber, actual.SequenceNumber);
                Assert.Equal(expected.DateTimeLocal, actual.DateTimeLocal);
                Assert.Equal(DateTimeKind.Unspecified, actual.DateTimeLocal.Kind);
                Assert.Equal(expected.Kind, actual.Kind);
                Assert.Equal(expected.SenderParticipantID, actual.SenderParticipantID);
                Assert.Equal(expected.Text, actual.Text);
                Assert.Equal(expected.IsForwarded, actual.IsForwarded);
                Assert.Equal(expected.ForwardScore, actual.ForwardScore);
                Assert.Equal(expected.Attachments.Count, actual.Attachments.Count);

                foreach (var (a, b) in expected.Attachments.Zip(actual.Attachments))
                {
                    Assert.Equal(a.AttachmentID, b.AttachmentID);
                    Assert.Equal(a.MessageID, b.MessageID);
                    Assert.Equal(a.Ordinal, b.Ordinal);
                    Assert.Equal(a.State, b.State);
                    Assert.Equal(a.MediaSha256, b.MediaSha256);
                    Assert.Equal(a.OriginalFileName, b.OriginalFileName);
                    Assert.Equal(a.IsForwarded, b.IsForwarded);
                    Assert.Equal(a.ForwardScore, b.ForwardScore);
                    Assert.Equal(a.LowQualityCopyOfAttachmentID, b.LowQualityCopyOfAttachmentID);
                    Assert.Equal(a.Basis?.BasisType, b.Basis?.BasisType);
                    Assert.Equal(a.Basis?.BasisText, b.Basis?.BasisText);
                    Assert.Equal(a.Basis?.Actor, b.Basis?.Actor);
                    Assert.Equal(a.Basis?.ConfirmedUtc, b.Basis?.ConfirmedUtc);
                }
            }

            Assert.Equal(2, page.Media.Count);
            Assert.Equal(1500, page.Media[ArchiveSample.BetaSha].DurationMS);
            Assert.Equal(640, page.Media[ArchiveSample.AlphaSha].Width);
        }

        [Fact]
        public void EveryState_IsPresent()
        {
            using var reader = ArchiveReader.Open(_folder.Path);

            var states = reader.ReadFirstPage(ArchiveReader.MaximumPageSize).Messages
                .SelectMany(m => m.Attachments)
                .Select(a => a.State)
                .Distinct()
                .Order();

            Assert.Equal(
                [ArchiveAttachmentState.Restored, ArchiveAttachmentState.Missing, ArchiveAttachmentState.Unmatched],
                states);
        }

        [Fact]
        public void MediaPath_NamesTheStoredFile_AndItsBytesHashToTheRecordedSha()
        {
            using var reader = ArchiveReader.Open(_folder.Path);

            var restored = reader.ReadFirstPage(ArchiveReader.MaximumPageSize).Messages
                .SelectMany(m => m.Attachments)
                .Where(a => a.State == ArchiveAttachmentState.Restored)
                .ToList();

            Assert.Equal(3, restored.Count);

            foreach (var attachment in restored)
            {
                var path = reader.MediaFullPath(attachment)!;

                Assert.True(File.Exists(path));
                Assert.Equal(attachment.MediaSha256, ArchiveHashing.Hex(SHA256.HashData(File.ReadAllBytes(path))));
            }

            // The extension is lower-cased from the original name, which keeps its own spelling.
            Assert.Equal("media/" + ArchiveSample.AlphaSha + ".jpg", restored[0].MediaRelativePath);
            Assert.Equal(ArchiveSample.AlphaName, restored[0].OriginalFileName);

            var unrestored = reader.ReadFirstPage(ArchiveReader.MaximumPageSize).Messages
                .SelectMany(m => m.Attachments)
                .First(a => a.State != ArchiveAttachmentState.Restored);

            Assert.Null(reader.MediaFullPath(unrestored));
        }

        [Fact]
        public void Pages_WalkForwardAndBackByKey()
        {
            using var reader = ArchiveReader.Open(_folder.Path);

            var first = reader.ReadFirstPage(2);
            Assert.Equal([1, 2], first.Messages.Select(m => m.SequenceNumber));
            Assert.False(first.HasEarlier);
            Assert.True(first.HasLater);
            Assert.Single(first.Media);

            // Sequence 4 does not exist: a page from it starts at the next message.
            var middle = reader.ReadPageFrom(4, 2);
            Assert.Equal([5, 6], middle.Messages.Select(m => m.SequenceNumber));
            Assert.True(middle.HasEarlier);
            Assert.True(middle.HasLater);

            var before = reader.ReadPageBefore(5, 2);
            Assert.Equal([2, 3], before.Messages.Select(m => m.SequenceNumber));

            var last = reader.ReadPageFrom(7, 2);
            Assert.Equal([7], last.Messages.Select(m => m.SequenceNumber));
            Assert.False(last.HasLater);

            var empty = reader.ReadPageFrom(8, 2);
            Assert.Empty(empty.Messages);
            Assert.Null(empty.FirstSequenceNumber);
        }

        [Fact]
        public void Days_AreListedAndReadWhole()
        {
            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.Equal(
                [new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 3), new DateOnly(2026, 3, 4), new DateOnly(2026, 3, 5)],
                reader.ReadDays());

            Assert.Equal([5, 6], reader.ReadDay(new DateOnly(2026, 3, 4)).Messages.Select(m => m.SequenceNumber));

            // Midnight belongs to the day it starts.
            Assert.Equal([7], reader.ReadDay(new DateOnly(2026, 3, 5)).Messages.Select(m => m.SequenceNumber));
            Assert.Empty(reader.ReadDay(new DateOnly(2026, 3, 6)).Messages);

            Assert.Equal(3, reader.FindFirstSequenceNumberOnOrAfter(new DateOnly(2026, 3, 3)));
            Assert.Equal(1, reader.FindFirstSequenceNumberOnOrAfter(new DateOnly(2020, 1, 1)));
            Assert.Null(reader.FindFirstSequenceNumberOnOrAfter(new DateOnly(2026, 3, 6)));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(ArchiveReader.MaximumPageSize + 1)]
        public void PageSize_OutsideTheBounds_IsRefused(int pageSize)
        {
            using var reader = ArchiveReader.Open(_folder.Path);

            Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadFirstPage(pageSize));
        }

        [Fact]
        public void Reader_WritesNothing_AndHoldsNoHandleAfterDispose()
        {
            var before = Snapshot();

            using (var reader = ArchiveReader.Open(_folder.Path))
            {
                reader.ReadFirstPage(ArchiveReader.MaximumPageSize);
                reader.ReadDays();
            }

            Assert.Equal(before, Snapshot());

            // No pooled handle: the database can be moved straight away.
            var database = Path.Combine(_folder.Path, ArchiveFormat.DatabaseFileName);
            File.Move(database, database + ".moved");
            File.Move(database + ".moved", database);
        }

        [Fact]
        public void Manifest_RoundTrips()
        {
            var manifest = ArchiveManifest.Parse(
                File.ReadAllBytes(Path.Combine(_folder.Path, ArchiveFormat.ManifestFileName)));

            Assert.Equal(ArchiveManifest.FormatName, manifest.Format);
            Assert.Equal(ArchiveFormat.FormatVersion, manifest.FormatVersion);
            Assert.Equal(ArchiveSample.Created, manifest.CreatedUtc);
            Assert.Equal(new ArchiveCounts(2, 6, 5, 3, 1, 1, 2), manifest.Counts);
        }

        private List<string> Snapshot() =>
            ArchiveContents.RelativeFiles(_folder.Path)
                .Select(r => r + ":" + ArchiveHashing.Sha256OfFile(ArchiveContents.FullPath(_folder.Path, r)))
                .ToList();
    }
}
