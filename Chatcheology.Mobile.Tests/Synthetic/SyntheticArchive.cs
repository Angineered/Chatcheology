using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Chatcheology.Archive;

namespace Chatcheology.Mobile.Synthetic
{
    /// <summary>What a synthetic archive holds.</summary>
    /// <param name="Messages">How many messages, numbered 1..n.</param>
    /// <param name="Days">Over how many days with messages; calendar days in between are left empty.</param>
    /// <param name="Images">How many restored images, of mixed aspect ratios.</param>
    /// <param name="Missing">How many Missing attachments.</param>
    /// <param name="Unmatched">How many Unmatched attachments.</param>
    /// <param name="Seed">The seed of the invented text, so the same options give the same archive.</param>
    internal sealed record SyntheticArchiveOptions(
        int Messages = 3000, int Days = 120, int Images = 40, int Missing = 3, int Unmatched = 3, int Seed = 26);

    /// <summary>
    /// Writes a whole portable archive (format v1) of invented conversation through
    /// <see cref="ArchiveWriter"/>: database, media, manifest and contents list.
    /// </summary>
    /// <remarks>
    /// <para>Everything is invented: two fictional participants, generated text and generated images.
    /// No real name, path, hash or figure appears here.</para>
    /// <para>Compiled into the tests and, as a linked file, into Debug builds of the Android app only,
    /// where it stands in for an import during development. A Release build does not contain it.</para>
    /// </remarks>
    internal static class SyntheticArchive
    {
        internal const long Local = 1;
        internal const long Other = 2;

        internal const string Title = "Alex and Sam (synthetic)";

        internal static readonly DateTime Created = new(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc);

        internal static readonly DateOnly FirstDay = new(2025, 1, 6);

        private static readonly (int Width, int Height)[] Sizes =
        [
            (640, 480), (480, 640), (800, 450), (600, 600), (360, 960), (960, 360),
        ];

        private static readonly string[] Words =
        [
            "morning", "coffee", "later", "train", "garden", "rain", "lunch", "call", "tomorrow", "weekend",
            "parcel", "market", "bread", "walk", "photo", "kettle", "bus", "dinner", "film", "book",
            "evening", "sunny", "cold", "back", "home", "soon", "great", "thanks", "sure", "maybe",
        ];

        /// <summary>The day of each message, by zero-based message index, as <see cref="Write"/> lays them out.</summary>
        internal static DateOnly DayOf(SyntheticArchiveOptions options, int index)
        {
            var dayIndex = (int)((long)index * options.Days / options.Messages);

            // Every third day is skipped, so the calendar has gaps as a real chat does.
            return FirstDay.AddDays(dayIndex + dayIndex / 2);
        }

        /// <summary>Writes the archive into <paramref name="folder"/>, which must be empty.</summary>
        internal static ArchiveCounts Write(string folder, SyntheticArchiveOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentOutOfRangeException.ThrowIfNegative(options.Messages);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Days);

            var attachmentSlots = options.Images + options.Missing + options.Unmatched;

            if (attachmentSlots > options.Messages / 2)
            {
                throw new ArgumentException("Too many attachments for that many messages.", nameof(options));
            }

            var random = new Random(options.Seed);
            var mediaFolder = Path.Combine(folder, ArchiveFormat.MediaFolderName);
            Directory.CreateDirectory(mediaFolder);

            using var writer = ArchiveWriter.Create(Path.Combine(folder, ArchiveFormat.DatabaseFileName), new ArchiveInfo
            {
                FormatVersion = ArchiveFormat.FormatVersion,
                CreatedUtc = Created,
                ProducerVersion = "Chatcheology synthetic generator 1.0",
                ConversationTitle = Title,
                SourceExportSha256 = string.Empty,
                TimeZoneID = string.Empty,
                LocalParticipantID = Local,
            });

            writer.WriteParticipant(new ArchiveParticipant { ParticipantID = Local, DisplayName = "Alex", IsLocal = true });
            writer.WriteParticipant(new ArchiveParticipant { ParticipantID = Other, DisplayName = "Sam", IsLocal = false });

            // Attachments are spread evenly through the conversation: images first in each stride,
            // then the Missing and Unmatched slots.
            var stride = attachmentSlots == 0 ? int.MaxValue : options.Messages / attachmentSlots;
            var images = new List<(string Sha, string Name)>();

            for (var i = 0; i < options.Images; i++)
            {
                var (width, height) = Sizes[i % Sizes.Length];
                var bytes = SyntheticImage.Png(
                    width,
                    height,
                    ((byte)random.Next(40, 220), (byte)random.Next(40, 220), (byte)random.Next(40, 220)),
                    ((byte)random.Next(40, 220), (byte)random.Next(40, 220), (byte)random.Next(40, 220)));

                var sha = ArchiveHashing.Hex(SHA256.HashData(bytes));
                var name = string.Create(CultureInfo.InvariantCulture, $"IMG-SYNTH-{i + 1:D4}.png");

                File.WriteAllBytes(Path.Combine(mediaFolder, ArchiveFormat.MediaFileName(sha, name)), bytes);

                writer.WriteMedia(new ArchiveMedia
                {
                    Sha256 = sha,
                    MediaType = "Image",
                    SizeBytes = bytes.Length,
                    DurationMS = null,
                    Width = width,
                    Height = height,
                });

                images.Add((sha, name));
            }

            // Times within a day rise from 07:00 to before 22:00.
            var perDay = Enumerable.Range(0, options.Messages)
                .GroupBy(index => DayOf(options, index))
                .ToDictionary(group => group.Key, group => group.Count());

            var positionInDay = 0;
            DateOnly? currentDay = null;
            var slot = 0;

            for (var index = 0; index < options.Messages; index++)
            {
                var day = DayOf(options, index);

                if (day != currentDay)
                {
                    currentDay = day;
                    positionInDay = 0;
                }

                var seconds = 7 * 3600 + (int)((long)positionInDay * 15 * 3600 / perDay[day]);
                positionInDay++;

                var time = day.ToDateTime(TimeOnly.MinValue).AddSeconds(seconds);
                var messageID = 1000L + index;

                var attachments = new List<ArchiveAttachment>();
                var isSystem = index == 0 || index % 997 == 500;
                var text = isSystem ? "Synthetic system notice." : Sentence(random, index);

                if (!isSystem && stride != int.MaxValue && index % stride == stride / 2 && slot < attachmentSlots)
                {
                    var attachmentID = 50_000L + slot;

                    attachments.Add(slot < options.Images
                        ? Attachment(attachmentID, messageID, ArchiveAttachmentState.Restored, images[slot].Sha, images[slot].Name)
                        : Attachment(
                            attachmentID,
                            messageID,
                            slot < options.Images + options.Missing ? ArchiveAttachmentState.Missing : ArchiveAttachmentState.Unmatched,
                            null,
                            null));

                    text = "<Media omitted>";
                    slot++;
                }

                writer.WriteMessage(new ArchiveMessage
                {
                    MessageID = messageID,
                    SequenceNumber = index + 1,
                    DateTimeLocal = DateTime.SpecifyKind(time, DateTimeKind.Unspecified),
                    Kind = isSystem ? ArchiveMessageKind.System : ArchiveMessageKind.User,
                    SenderParticipantID = isSystem ? null : (random.Next(2) == 0 ? Local : Other),
                    Text = text,
                    IsForwarded = null,
                    ForwardScore = null,
                    Attachments = attachments,
                });
            }

            var counts = writer.Complete();

            var manifest = new ArchiveManifest(
                ArchiveManifest.FormatName, ArchiveFormat.FormatVersion, "Chatcheology synthetic generator 1.0", Created, counts);

            File.WriteAllBytes(Path.Combine(folder, ArchiveFormat.ManifestFileName), manifest.ToBytes());
            File.WriteAllBytes(Path.Combine(folder, ArchiveContents.FileName), ArchiveContents.Build(folder));

            return counts;
        }

        private static ArchiveAttachment Attachment(
            long id, long messageID, ArchiveAttachmentState state, string? sha, string? name) => new()
        {
            AttachmentID = id,
            MessageID = messageID,
            Ordinal = 1,
            State = state,
            MediaSha256 = sha,
            OriginalFileName = name,
            IsForwarded = null,
            ForwardScore = null,
            LowQualityCopyOfAttachmentID = null,
            Basis = state == ArchiveAttachmentState.Restored
                ? new ArchiveAttachmentBasis
                {
                    BasisType = "Synthetic",
                    BasisText = "Generated for testing.",
                    Actor = null,
                    ConfirmedUtc = Created,
                }
                : null,
        };

        /// <summary>
        /// Invented text of one to forty words, sometimes on two lines, with the message number in it
        /// so a screenshot shows which message is where.
        /// </summary>
        private static string Sentence(Random random, int index)
        {
            var text = new StringBuilder();
            text.Append('#').Append((index + 1).ToString(CultureInfo.InvariantCulture));

            var words = random.Next(100) < 70 ? random.Next(1, 10) : random.Next(10, 40);

            for (var i = 0; i < words; i++)
            {
                text.Append(i == words / 2 && words > 20 ? '\n' : ' ').Append(Words[random.Next(Words.Length)]);
            }

            return text.ToString();
        }
    }
}
