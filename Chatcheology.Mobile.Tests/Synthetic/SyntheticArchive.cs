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
    /// <param name="Audio">How many restored audio files (short WAV tones).</param>
    /// <param name="Documents">How many restored documents (one-page PDFs).</param>
    /// <param name="BrokenImages">How many restored "images" whose bytes no decoder can read.</param>
    /// <param name="LowQualityCopies">
    /// How many restored low-quality copies. Each sits beside an image as its hidden smaller duplicate,
    /// except the last, which is the copy of the first Missing attachment and so is shown, labelled.
    /// </param>
    /// <param name="Forwarded">How many messages are marked forwarded; every third of them many times.</param>
    /// <param name="Links">How many messages carry a web or email address.</param>
    /// <param name="Start">
    /// The first day, when not <see cref="SyntheticArchive.FirstDay"/>: an earlier start with many days
    /// spreads the chat over several years, so the same day and month has messages in more than one year
    /// (On this day, CH-39).
    /// </param>
    /// <param name="Videos">How many restored videos, after the undecodable images; needs <paramref name="VideoBytes"/>.</param>
    /// <param name="VideoBytes">
    /// A synthetic video (a generated test pattern, never a real recording) used for every video, each
    /// made distinct by trailing bytes. The generator cannot write a video by hand, so the caller brings it.
    /// </param>
    /// <param name="Memories">
    /// Whether to add the invented memory layers (<see cref="SyntheticMemories"/>, CH-42); needs at least
    /// 200 messages.
    /// </param>
    /// <param name="NotedDates">Whether to add three invented noted dates (CH-47).</param>
    internal sealed record SyntheticArchiveOptions(
        int Messages = 3000,
        int Days = 120,
        int Images = 40,
        int Missing = 3,
        int Unmatched = 3,
        int Seed = 26,
        int Audio = 0,
        int Documents = 0,
        int BrokenImages = 0,
        int LowQualityCopies = 0,
        int Forwarded = 0,
        int Links = 0,
        DateOnly? Start = null,
        int Videos = 0,
        byte[]? VideoBytes = null,
        bool Memories = false,
        bool NotedDates = false);

    /// <summary>
    /// Writes a whole portable archive (the current format) of invented conversation through
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
            return (options.Start ?? FirstDay).AddDays(dayIndex + dayIndex / 2);
        }

        /// <summary>Writes the archive into <paramref name="folder"/>, which must be empty.</summary>
        internal static ArchiveCounts Write(string folder, SyntheticArchiveOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentOutOfRangeException.ThrowIfNegative(options.Messages);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Days);

            if (options.Videos > 0 && options.VideoBytes is not { Length: > 0 })
            {
                throw new ArgumentException("Videos need the video bytes.", nameof(options));
            }

            var restoredOthers = options.Audio + options.Documents + options.BrokenImages + options.Videos;
            var attachmentSlots = options.Images + restoredOthers + options.Missing + options.Unmatched;

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

            // Audio, documents, undecodable "images" and videos, in that order after the images.
            var others = new List<(string Sha, string Name)>();
            var firstVideo = options.Audio + options.Documents + options.BrokenImages;

            for (var i = 0; i < restoredOthers; i++)
            {
                var (bytes, name, type, duration) = i < options.Audio
                    ? (SyntheticMedia.Wav(i + 1), $"AUD-SYNTH-{i + 1:D4}.wav", "Audio", (long?)SyntheticMedia.ToneMilliseconds)
                    : i < options.Audio + options.Documents
                        ? (SyntheticMedia.Pdf(i + 1), $"DOC-SYNTH-{i + 1:D4}.pdf", "Document", null)
                        : i < firstVideo
                            ? (SyntheticMedia.BrokenImage(i + 1), $"IMG-BROKEN-{i + 1:D4}.jpg", "Image", null)
                            : ([.. options.VideoBytes!, .. new byte[i - firstVideo + 1]], $"VID-SYNTH-{i - firstVideo + 1:D4}.mp4", "Video", (long?)2000);

                var sha = ArchiveHashing.Hex(SHA256.HashData(bytes));
                File.WriteAllBytes(Path.Combine(mediaFolder, ArchiveFormat.MediaFileName(sha, name)), bytes);

                writer.WriteMedia(new ArchiveMedia
                {
                    Sha256 = sha,
                    MediaType = type,
                    SizeBytes = bytes.Length,
                    DurationMS = duration,
                    Width = type is "Image" or "Video" ? 640 : null,
                    Height = type is "Image" or "Video" ? 480 : null,
                });

                others.Add((sha, name));
            }

            // Small copies for the low-quality links.
            var copies = new List<(string Sha, string Name)>();

            for (var i = 0; i < options.LowQualityCopies; i++)
            {
                var bytes = SyntheticImage.Png(
                    160,
                    120,
                    ((byte)random.Next(40, 220), (byte)random.Next(40, 220), (byte)random.Next(40, 220)),
                    ((byte)random.Next(40, 220), (byte)random.Next(40, 220), (byte)random.Next(40, 220)));

                var sha = ArchiveHashing.Hex(SHA256.HashData(bytes));
                var name = string.Create(CultureInfo.InvariantCulture, $"IMG-SYNTH-SMALL-{i + 1:D4}.png");

                File.WriteAllBytes(Path.Combine(mediaFolder, ArchiveFormat.MediaFileName(sha, name)), bytes);

                writer.WriteMedia(new ArchiveMedia
                {
                    Sha256 = sha,
                    MediaType = "Image",
                    SizeBytes = bytes.Length,
                    DurationMS = null,
                    Width = 160,
                    Height = 120,
                });

                copies.Add((sha, name));
            }

            var forwardedStride = options.Forwarded > 0 ? Math.Max(1, options.Messages / options.Forwarded) : int.MaxValue;
            var linkStride = options.Links > 0 ? Math.Max(1, options.Messages / options.Links) : int.MaxValue;

            // Times within a day rise from 07:00 to before 22:00.
            var perDay = Enumerable.Range(0, options.Messages)
                .GroupBy(index => DayOf(options, index))
                .ToDictionary(group => group.Key, group => group.Count());

            var messageTimes = new List<DateTime>(options.Messages);
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
                messageTimes.Add(time);
                var messageID = 1000L + index;

                var attachments = new List<ArchiveAttachment>();
                var isSystem = index == 0 || index % 997 == 500;
                var text = isSystem ? "Synthetic system notice." : Sentence(random, index);

                if (!isSystem && stride != int.MaxValue && index % stride == stride / 2 && slot < attachmentSlots)
                {
                    var attachmentID = 50_000L + slot;
                    var firstMissing = options.Images + restoredOthers;

                    attachments.Add(slot < options.Images
                        ? Attachment(attachmentID, messageID, ArchiveAttachmentState.Restored, images[slot].Sha, images[slot].Name)
                        : slot < firstMissing
                            ? Attachment(attachmentID, messageID, ArchiveAttachmentState.Restored, others[slot - options.Images].Sha, others[slot - options.Images].Name)
                            : Attachment(
                                attachmentID,
                                messageID,
                                slot < firstMissing + options.Missing ? ArchiveAttachmentState.Missing : ArchiveAttachmentState.Unmatched,
                                null,
                                null));

                    // The low-quality copies: beside an image (hidden), the last beside the first Missing
                    // attachment (shown, labelled, as the only copy there is).
                    var copy = slot < options.Images && slot < options.LowQualityCopies - (options.Missing > 0 ? 1 : 0)
                        ? slot
                        : slot == firstMissing && options.Missing > 0 && options.LowQualityCopies > 0
                            ? options.LowQualityCopies - 1
                            : -1;

                    if (copy >= 0)
                    {
                        attachments.Add(Attachment(
                            60_000L + copy, messageID, ArchiveAttachmentState.Restored, copies[copy].Sha, copies[copy].Name, ordinal: 2, copyOf: attachmentID));
                    }

                    text = "<Media omitted>";
                    slot++;
                }

                bool? forwarded = null;
                int? forwardScore = null;

                if (!isSystem && index % forwardedStride == 5)
                {
                    forwarded = true;
                    forwardScore = (index / forwardedStride) % 3 == 2 ? 5 : 1;
                }

                if (!isSystem && attachments.Count == 0 && index % linkStride == 9)
                {
                    text += (index / linkStride) % 2 == 0
                        ? " see www.example.org/synthetic and https://example.com/page?id=7."
                        : " write to someone@example.org";
                }

                writer.WriteMessage(new ArchiveMessage
                {
                    MessageID = messageID,
                    SequenceNumber = index + 1,
                    DateTimeLocal = DateTime.SpecifyKind(time, DateTimeKind.Unspecified),
                    Kind = isSystem ? ArchiveMessageKind.System : ArchiveMessageKind.User,
                    SenderParticipantID = isSystem ? null : (random.Next(2) == 0 ? Local : Other),
                    Text = text,
                    IsForwarded = forwarded,
                    ForwardScore = forwardScore,
                    Attachments = attachments,
                });
            }

            if (options.Memories)
            {
                SyntheticMemories.Write(writer, mediaFolder, messageTimes);
            }

            if (options.NotedDates)
            {
                // Invented dates: an anniversary with a note and a jumpable year, a date with no year, a leap day.
                writer.WriteNotedDate(new ArchiveNotedDate { NotedDateID = 1, Title = "Our synthetic anniversary", Month = 3, Day = 14, Year = 2016, Note = "An invented note.", });
                writer.WriteNotedDate(new ArchiveNotedDate { NotedDateID = 2, Title = "Invented birthday", Month = 8, Day = 2, Year = null, Note = null, });
                writer.WriteNotedDate(new ArchiveNotedDate { NotedDateID = 3, Title = "Invented leap day", Month = 2, Day = 29, Year = 2020, Note = null, });

                // CH-48: one invented gallery link on the anniversary, on a reserved example domain.
                writer.WriteExternalLink(new ArchiveExternalLink { ExternalLinkID = 101, OwnerKind = ExternalLinkOwnerKind.NotedDate, OwnerID = 1, Ordinal = 1, Url = "https://example.org/synthetic-anniversary", Label = null });
            }

            var counts = writer.Complete();

            var manifest = new ArchiveManifest(
                ArchiveManifest.FormatName, ArchiveFormat.FormatVersion, "Chatcheology synthetic generator 1.0", Created, counts);

            File.WriteAllBytes(Path.Combine(folder, ArchiveFormat.ManifestFileName), manifest.ToBytes());
            File.WriteAllBytes(Path.Combine(folder, ArchiveContents.FileName), ArchiveContents.Build(folder));

            return counts;
        }

        private static ArchiveAttachment Attachment(
            long id, long messageID, ArchiveAttachmentState state, string? sha, string? name, int ordinal = 1, long? copyOf = null) => new()
        {
            AttachmentID = id,
            MessageID = messageID,
            Ordinal = ordinal,
            State = state,
            MediaSha256 = sha,
            OriginalFileName = name,
            IsForwarded = null,
            ForwardScore = null,
            LowQualityCopyOfAttachmentID = copyOf,
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
