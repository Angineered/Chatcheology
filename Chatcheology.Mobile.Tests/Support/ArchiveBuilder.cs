using System.Security.Cryptography;
using System.Text;
using Chatcheology.Archive;

namespace Chatcheology.Mobile.Tests.Support
{
    /// <summary>One attachment of a message an <see cref="ArchiveBuilder"/> writes.</summary>
    /// <param name="Id">Its attachment identifier, chosen by the test so links can name it.</param>
    /// <param name="State">Restored, Missing or Unmatched.</param>
    /// <param name="MediaType">The media category of a restored attachment.</param>
    /// <param name="Extension">The extension of a restored attachment's file.</param>
    /// <param name="DurationMS">The recorded duration, or none.</param>
    /// <param name="Width">The recorded width, or none.</param>
    /// <param name="Height">The recorded height, or none.</param>
    /// <param name="LowQualityOf">The full copy this is the low-quality copy of, or none.</param>
    /// <param name="IsForwarded">The attachment's forwarded fact.</param>
    /// <param name="ForwardScore">The attachment's forward score.</param>
    /// <param name="Caption">The picture's caption (CH-71, version 4), or none.</param>
    internal sealed record AttachmentSpec(
        long Id,
        ArchiveAttachmentState State = ArchiveAttachmentState.Restored,
        string MediaType = "Image",
        string Extension = ".jpg",
        long? DurationMS = null,
        int? Width = null,
        int? Height = null,
        long? LowQualityOf = null,
        bool? IsForwarded = null,
        int? ForwardScore = null,
        string? Caption = null);

    /// <summary>
    /// Writes a complete, valid v1 archive message by message, with exactly the attachments, links and
    /// forwarded facts a test needs. Invented text and invented media bytes only.
    /// </summary>
    internal sealed class ArchiveBuilder
    {
        internal const long Local = 1;
        internal const long Other = 2;

        private readonly List<ArchiveMessage> _messages = [];
        private readonly List<ArchiveNotedDate> _notedDates = [];
        private DateTime _next = new(2025, 3, 1, 8, 0, 0);

        internal ArchiveBuilder(string title = "Alex and Sam")
        {
            Title = title;
        }

        internal string Title { get; }

        /// <summary>The time the next message gets unless one is given: one minute after the last.</summary>
        internal DateTime Next => _next;

        /// <summary>Adds a message and returns its sequence number.</summary>
        internal int Add(
            string text,
            long? sender = Other,
            DateTime? at = null,
            bool? forwarded = null,
            int? score = null,
            params AttachmentSpec[] attachments)
        {
            var sequence = _messages.Count + 1;
            var messageID = 1000L + sequence;
            var time = at ?? _next;
            _next = time.AddMinutes(1);

            _messages.Add(new ArchiveMessage
            {
                MessageID = messageID,
                SequenceNumber = sequence,
                DateTimeLocal = time,
                Kind = sender is null ? ArchiveMessageKind.System : ArchiveMessageKind.User,
                SenderParticipantID = sender,
                Text = text,
                IsForwarded = forwarded,
                ForwardScore = score,
                Attachments = attachments.Select((spec, index) => Attachment(spec, messageID, index + 1)).ToList(),
                Source = Source,
            });

            return sequence;
        }

        /// <summary>Adds a gifted noted date (CH-47; the archive is created in October 2026).</summary>
        internal void NotedDate(long id, string title, int month, int day, int? year = null, string? note = null) =>
            _notedDates.Add(new ArchiveNotedDate { NotedDateID = id, Title = title, Month = month, Day = day, Year = year, Note = note });

        /// <summary>Adds a placeholder-only message with the attachments.</summary>
        internal int Media(params AttachmentSpec[] attachments) =>
            Add("<Media omitted>", Other, null, null, null, attachments);

        /// <summary>The synthetic archive id a version 4 archive carries (CH-71).</summary>
        internal string ArchiveId { get; set; } = "2c6b8e14-3a5f-4d07-b9e2-71a0c4d58f36";

        /// <summary>A format version to write instead of the usual one (CH-71 step 2: a version 3 archive installed before version 4).</summary>
        internal int? FormatVersion { get; set; }

        /// <summary>The source the next messages get (CH-71: <see cref="ArchiveMessageSource.Msgstore"/> for newer ones).</summary>
        internal ArchiveMessageSource Source { get; set; } = ArchiveMessageSource.Workspace;

        /// <summary>
        /// The format version: <see cref="FormatVersion"/> when set, else version 2 when there are gifted noted dates (only
        /// version 2 carries them, CH-53), otherwise the current one.
        /// </summary>
        private int Version => FormatVersion ?? (_notedDates.Count > 0 ? ArchiveFormat.NotedDatesFormatVersion : ArchiveFormat.FormatVersion);

        /// <summary>Writes the archive into <paramref name="folder"/>: database, media, manifest, contents list.</summary>
        internal string Write(string folder)
        {
            var mediaFolder = Path.Combine(folder, ArchiveFormat.MediaFolderName);
            Directory.CreateDirectory(mediaFolder);

            var created = new DateTime(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc);

            using var writer = ArchiveWriter.Create(Path.Combine(folder, ArchiveFormat.DatabaseFileName), new ArchiveInfo
            {
                FormatVersion = Version,
                CreatedUtc = created,
                ProducerVersion = "Test builder 1.0",
                ConversationTitle = Title,
                SourceExportSha256 = string.Empty,
                TimeZoneID = string.Empty,
                LocalParticipantID = Local,
                ArchiveId = Version >= ArchiveFormat.FirstVersionWithCaptionsAndArchiveId ? ArchiveId : null,
            });

            writer.WriteParticipant(new ArchiveParticipant { ParticipantID = Local, DisplayName = "Alex", IsLocal = true });
            writer.WriteParticipant(new ArchiveParticipant { ParticipantID = Other, DisplayName = "Sam", IsLocal = false });

            var written = new HashSet<string>(StringComparer.Ordinal);

            foreach (var attachment in _messages.SelectMany(message => message.Attachments).Where(a => a.State == ArchiveAttachmentState.Restored))
            {
                var spec = _specs[attachment.AttachmentID];
                var bytes = Bytes(spec.Id);

                if (written.Add(attachment.MediaSha256!))
                {
                    File.WriteAllBytes(Path.Combine(mediaFolder, ArchiveFormat.MediaFileName(attachment.MediaSha256!, attachment.OriginalFileName!)), bytes);

                    writer.WriteMedia(new ArchiveMedia
                    {
                        Sha256 = attachment.MediaSha256!,
                        MediaType = spec.MediaType,
                        SizeBytes = bytes.Length,
                        DurationMS = spec.DurationMS,
                        Width = spec.Width,
                        Height = spec.Height,
                    });
                }
            }

            foreach (var message in _messages)
            {
                writer.WriteMessage(message);
            }

            foreach (var notedDate in _notedDates)
            {
                writer.WriteNotedDate(notedDate);
            }

            var counts = writer.Complete();

            File.WriteAllBytes(
                Path.Combine(folder, ArchiveFormat.ManifestFileName),
                new ArchiveManifest(ArchiveManifest.FormatName, Version, "Test builder 1.0", created, counts).ToBytes());
            File.WriteAllBytes(Path.Combine(folder, ArchiveContents.FileName), ArchiveContents.Build(folder));

            return folder;
        }

        private readonly Dictionary<long, AttachmentSpec> _specs = [];

        private ArchiveAttachment Attachment(AttachmentSpec spec, long messageID, int ordinal)
        {
            _specs.Add(spec.Id, spec);

            var restored = spec.State == ArchiveAttachmentState.Restored;

            return new ArchiveAttachment
            {
                AttachmentID = spec.Id,
                MessageID = messageID,
                Ordinal = ordinal,
                State = spec.State,
                MediaSha256 = restored ? ArchiveHashing.Hex(SHA256.HashData(Bytes(spec.Id))) : null,
                OriginalFileName = restored ? "FILE-" + spec.Id + spec.Extension : null,
                IsForwarded = spec.IsForwarded,
                ForwardScore = spec.ForwardScore,
                LowQualityCopyOfAttachmentID = spec.LowQualityOf,
                Caption = spec.Caption,
                Basis = restored
                    ? new ArchiveAttachmentBasis
                    {
                        BasisType = "Synthetic",
                        BasisText = "Generated for testing.",
                        Actor = null,
                        ConfirmedUtc = new DateTime(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc),
                    }
                    : null,
            };
        }

        private static byte[] Bytes(long id) => Encoding.UTF8.GetBytes("synthetic media payload " + id);
    }

    /// <summary>A settings store in memory.</summary>
    internal sealed class MemorySettings : Settings.ISettingsStore
    {
        internal Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public string? Get(string key) => Values.TryGetValue(key, out var value) ? value : null;

        public void Set(string key, string? value)
        {
            if (value is null)
            {
                Values.Remove(key);
            }
            else
            {
                Values[key] = value;
            }
        }
    }

    /// <summary>A clock that says what the test tells it.</summary>
    internal sealed class FixedClock(DateTime utc) : TimeProvider
    {
        internal DateTime Utc { get; set; } = utc;

        public override DateTimeOffset GetUtcNow() => new(Utc, TimeSpan.Zero);
    }
}
