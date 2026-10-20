using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Archive.Tests
{
    /// <summary>A private folder under the system temporary path, removed on dispose.</summary>
    internal sealed class TemporaryFolder : IDisposable
    {
        internal TemporaryFolder()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "Chatcheology.Archive.Tests", Guid.NewGuid().ToString("N"));

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
    /// A small synthetic archive written through <see cref="ArchiveWriter"/>, with every state, a shared
    /// payload, a low-quality link, a system message, a gap in the sequence and several days.
    /// </summary>
    /// <remarks>
    /// Everything is invented: two fictional participants, invented text and invented bytes. No real
    /// name, path, hash or figure appears in this project.
    /// </remarks>
    internal static class ArchiveSample
    {
        internal const long Local = 1;
        internal const long Other = 2;

        internal static readonly byte[] AlphaBytes = Encoding.ASCII.GetBytes("synthetic alpha payload");
        internal static readonly byte[] BetaBytes = Encoding.ASCII.GetBytes("synthetic beta payload, longer");

        internal static string AlphaSha => ArchiveHashing.Hex(SHA256.HashData(AlphaBytes));
        internal static string BetaSha => ArchiveHashing.Hex(SHA256.HashData(BetaBytes));

        internal const string AlphaName = "IMG-20260302-WA0001.JPG";
        internal const string BetaName = "clip-one.mp4";

        internal static readonly DateTime Created = new(2026, 10, 19, 8, 30, 15, DateTimeKind.Utc);

        internal static ArchiveInfo Info(int formatVersion = ArchiveFormat.FormatVersion) => new()
        {
            FormatVersion = formatVersion,
            CreatedUtc = Created,
            ProducerVersion = "Synthetic producer 1.0",
            ConversationTitle = "Alex and Sam",
            SourceExportSha256 = new string('a', 64),
            TimeZoneID = "Synthetic/Zone",
            LocalParticipantID = Local,
        };

        internal static IReadOnlyList<ArchiveParticipant> Participants =>
        [
            new() { ParticipantID = Local, DisplayName = "Alex", IsLocal = true },
            new() { ParticipantID = Other, DisplayName = "Sam", IsLocal = false },
        ];

        internal static IReadOnlyList<ArchiveMedia> Media =>
        [
            new() { Sha256 = AlphaSha, MediaType = "Image", SizeBytes = AlphaBytes.Length, DurationMS = null, Width = 640, Height = 480 },
            new() { Sha256 = BetaSha, MediaType = "Video", SizeBytes = BetaBytes.Length, DurationMS = 1500, Width = null, Height = null },
        ];

        internal static ArchiveAttachmentBasis Basis(string text) => new()
        {
            BasisType = "ExternalProvenanceOrGroundTruth",
            BasisText = text,
            Actor = "Reviewer A",
            ConfirmedUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
        };

        internal static ArchiveAttachment Attachment(
            long id,
            long messageID,
            ArchiveAttachmentState state,
            int ordinal = 1,
            string? sha = null,
            string? name = null,
            bool? forwarded = null,
            int? score = null,
            long? lowQualityOf = null) => new()
        {
            AttachmentID = id,
            MessageID = messageID,
            Ordinal = ordinal,
            State = state,
            MediaSha256 = sha,
            OriginalFileName = name,
            IsForwarded = forwarded,
            ForwardScore = score,
            LowQualityCopyOfAttachmentID = lowQualityOf,
            Basis = state == ArchiveAttachmentState.Restored ? Basis($"Basis for {id}") : null,
        };

        internal static ArchiveMessage Message(
            long id,
            int sequence,
            string time,
            long? sender,
            string text,
            params ArchiveAttachment[] attachments) => new()
        {
            MessageID = id,
            SequenceNumber = sequence,
            DateTimeLocal = DateTime.Parse(time, System.Globalization.CultureInfo.InvariantCulture),
            Kind = sender is null ? ArchiveMessageKind.System : ArchiveMessageKind.User,
            SenderParticipantID = sender,
            Text = text,
            IsForwarded = null,
            ForwardScore = null,
            Attachments = attachments,
        };

        /// <summary>The sample's messages, in sequence order. Sequence 4 is deliberately absent.</summary>
        internal static IReadOnlyList<ArchiveMessage> Messages =>
        [
            Forwarded(Message(101, 1, "2026-03-02T09:00:00", Local, "Hello\nsecond line"), true, 5),
            Message(102, 2, "2026-03-02T09:05:00", Other, "<Media omitted>",
                Attachment(10, 102, ArchiveAttachmentState.Restored, 1, AlphaSha, AlphaName, forwarded: true, score: 3),
                Attachment(11, 102, ArchiveAttachmentState.Missing, 2, forwarded: false)),
            Message(103, 3, "2026-03-03T10:00:00", null, "System line"),
            Message(104, 5, "2026-03-04T08:00:00", Local, "<Media omitted>",
                Attachment(12, 104, ArchiveAttachmentState.Restored, 1, BetaSha, BetaName)),
            Message(105, 6, "2026-03-04T08:01:00", Other, "<Media omitted>",
                Attachment(13, 105, ArchiveAttachmentState.Restored, 1, AlphaSha, AlphaName, forwarded: false,
                    lowQualityOf: 12)),
            Forwarded(
                Message(106, 7, "2026-03-05T00:00:00", Other, "Late",
                    Attachment(14, 106, ArchiveAttachmentState.Unmatched)),
                false,
                null),
        ];

        /// <summary>The same message with a known forwarded status.</summary>
        internal static ArchiveMessage Forwarded(ArchiveMessage message, bool forwarded, int? score) => new()
        {
            MessageID = message.MessageID,
            SequenceNumber = message.SequenceNumber,
            DateTimeLocal = message.DateTimeLocal,
            Kind = message.Kind,
            SenderParticipantID = message.SenderParticipantID,
            Text = message.Text,
            IsForwarded = forwarded,
            ForwardScore = score,
            Attachments = message.Attachments,
        };

        /// <summary>Writes <c>archive.db</c> only, into <paramref name="folder"/>.</summary>
        internal static ArchiveCounts WriteDatabase(string folder, int formatVersion = ArchiveFormat.FormatVersion)
        {
            using var writer = ArchiveWriter.Create(
                System.IO.Path.Combine(folder, ArchiveFormat.DatabaseFileName), Info(formatVersion));

            foreach (var participant in Participants)
            {
                writer.WriteParticipant(participant);
            }

            foreach (var media in Media)
            {
                writer.WriteMedia(media);
            }

            foreach (var message in Messages)
            {
                writer.WriteMessage(message);
            }

            return writer.Complete();
        }

        /// <summary>Writes a whole archive folder: database, media, manifest and contents list.</summary>
        internal static void WriteFolder(string folder)
        {
            var counts = WriteDatabase(folder);

            var media = System.IO.Path.Combine(folder, ArchiveFormat.MediaFolderName);
            Directory.CreateDirectory(media);
            File.WriteAllBytes(System.IO.Path.Combine(media, ArchiveFormat.MediaFileName(AlphaSha, AlphaName)), AlphaBytes);
            File.WriteAllBytes(System.IO.Path.Combine(media, ArchiveFormat.MediaFileName(BetaSha, BetaName)), BetaBytes);

            var manifest = new ArchiveManifest(
                ArchiveManifest.FormatName, ArchiveFormat.FormatVersion, "Synthetic producer 1.0", Created, counts);

            File.WriteAllBytes(System.IO.Path.Combine(folder, ArchiveFormat.ManifestFileName), manifest.ToBytes());
            File.WriteAllBytes(System.IO.Path.Combine(folder, ArchiveContents.FileName), ArchiveContents.Build(folder));
        }

        /// <summary>Runs SQL on the archive through a writable connection, to damage it on purpose.</summary>
        internal static void Tamper(string folder, string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = System.IO.Path.Combine(folder, ArchiveFormat.DatabaseFileName),
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());

            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        /// <summary>Every row of a query as one comma-joined line.</summary>
        internal static List<string> Rows(string databasePath, string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());

            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = sql;

            var rows = new List<string>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                rows.Add(string.Join(
                    ",",
                    Enumerable.Range(0, reader.FieldCount).Select(i =>
                        reader.IsDBNull(i)
                            ? "<null>"
                            : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
            }

            return rows;
        }
    }
}
