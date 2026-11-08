using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Mobile.Tests
{
    /// <summary>A private folder under the system temporary path, removed on dispose.</summary>
    internal sealed class TemporaryFolder : IDisposable
    {
        internal TemporaryFolder()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "Chatcheology.Mobile.Tests", Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
        }
    }

    /// <summary>
    /// Small synthetic archives whose message times are chosen by the test, so a page join can be put
    /// exactly where a day starts, in the middle of a day, or inside a run of one day.
    /// </summary>
    internal static class TestArchives
    {
        internal const long Local = 1;
        internal const long Other = 2;

        /// <summary>
        /// Writes a database-only archive of <paramref name="count"/> text messages numbered 1..n, the
        /// message with sequence number <c>s</c> at <paramref name="timeOf"/>(s). Enough for the reader.
        /// </summary>
        internal static void Write(string folder, int count, Func<int, DateTime> timeOf)
        {
            using var writer = ArchiveWriter.Create(System.IO.Path.Combine(folder, ArchiveFormat.DatabaseFileName), new ArchiveInfo
            {
                FormatVersion = ArchiveFormat.FormatVersion,
                CreatedUtc = new DateTime(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc),
                ProducerVersion = "Synthetic producer 1.0",
                ConversationTitle = "Alex and Sam",
                SourceExportSha256 = string.Empty,
                TimeZoneID = string.Empty,
                LocalParticipantID = Local,
                ArchiveId = "3f8a2c61-0e4d-4b79-a5c3-9d1e7f20b854",
            });

            writer.WriteParticipant(new ArchiveParticipant { ParticipantID = Local, DisplayName = "Alex", IsLocal = true });
            writer.WriteParticipant(new ArchiveParticipant { ParticipantID = Other, DisplayName = "Sam", IsLocal = false });

            for (var sequence = 1; sequence <= count; sequence++)
            {
                writer.WriteMessage(new ArchiveMessage
                {
                    MessageID = 100 + sequence,
                    SequenceNumber = sequence,
                    DateTimeLocal = timeOf(sequence),
                    Kind = ArchiveMessageKind.User,
                    SenderParticipantID = sequence % 2 == 0 ? Local : Other,
                    Text = "Message " + sequence,
                    IsForwarded = null,
                    ForwardScore = null,
                    Attachments = [],
                });
            }

            writer.Complete();
        }

        /// <summary>The time of message <paramref name="sequence"/> when every message up to it is on day one, and so on.</summary>
        internal static DateTime OnDay(int day, int sequence) =>
            new DateTime(2025, 3, 1, 8, 0, 0).AddDays(day - 1).AddSeconds(sequence);

        /// <summary>The rows as short text: <c>D yyyy-MM-dd</c> for a separator, <c>M n</c> for a message.</summary>
        internal static List<string> Describe(IEnumerable<ConversationRow> rows) =>
            rows.Select(row => row switch
            {
                DateSeparatorRow separator => "D " + separator.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                MessageRow message => "M " + message.SequenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => "?",
            }).ToList();
    }
}
