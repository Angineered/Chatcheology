using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Chatcheology.Archive;

namespace Chatcheology.Mobile.Tests.Support
{
    /// <summary>One trophy of a period an <see cref="MemoryArchiveBuilder"/> writes.</summary>
    /// <param name="Participant">Whose it is.</param>
    /// <param name="Local">Its local time.</param>
    /// <param name="ItemId">Its number in the set.</param>
    /// <param name="ItemType">Its grade.</param>
    internal sealed record TrophySpec(long Participant, DateTime Local, string ItemId, string ItemType = "bronze");

    /// <summary>One period: its label and its trophies.</summary>
    internal sealed record PeriodSpec(string Label, params TrophySpec[] Trophies);

    /// <summary>
    /// Writes a complete, valid version 2 archive with text messages at chosen times and the memory rows a
    /// test needs: imported (PSN-like) entries with periods and items, personal entries, definitions and
    /// links. Invented games, trophies, names and bytes only.
    /// </summary>
    internal sealed class MemoryArchiveBuilder
    {
        internal const long Local = 1;
        internal const long Other = 2;

        /// <summary>The imported collection's identifier.</summary>
        internal const long Gaming = 1;

        /// <summary>The personal collection's identifier.</summary>
        internal const long Personal = 2;

        private readonly List<ArchiveMessage> _messages = [];
        private readonly List<ArchiveEntry> _entries = [];
        private readonly List<ArchiveEntryLink> _links = [];
        private readonly List<ArchiveItemDefinition> _definitions = [];
        private readonly Dictionary<string, (byte[] Bytes, string Extension)> _media = new(StringComparer.Ordinal);
        private long _nextItem = 1;

        /// <summary>Whether the imported collection is shown by default (it is not: Shared gaming starts off).</summary>
        internal bool GamingShownByDefault { get; set; }

        /// <summary>Whether the personal collection is shown by default (it is).</summary>
        internal bool PersonalShownByDefault { get; set; } = true;

        /// <summary>Adds a message at <paramref name="at"/> and returns its sequence number.</summary>
        internal int Message(DateTime at, string? text = null, long sender = Other, params ArchiveAttachmentState[] attachments)
        {
            var sequence = _messages.Count + 1;
            var id = 1000L + sequence;

            _messages.Add(new ArchiveMessage
            {
                MessageID = id,
                SequenceNumber = sequence,
                DateTimeLocal = at,
                Kind = ArchiveMessageKind.User,
                SenderParticipantID = sender,
                Text = text ?? "Message " + sequence.ToString(CultureInfo.InvariantCulture),
                IsForwarded = null,
                ForwardScore = null,
                Attachments = attachments.Select((state, index) => new ArchiveAttachment
                {
                    AttachmentID = id * 10 + index,
                    MessageID = id,
                    Ordinal = index + 1,
                    State = state,
                    MediaSha256 = null,
                    OriginalFileName = null,
                    IsForwarded = null,
                    ForwardScore = null,
                    LowQualityCopyOfAttachmentID = null,
                    Basis = null,
                }).ToList(),
                Source = ArchiveMessageSource.Workspace,
            });

            return sequence;
        }

        /// <summary>Adds a game-day record of the imported collection.</summary>
        internal void Game(long entryID, string title, string dayLevel, bool image = false, params PeriodSpec[] periods)
        {
            var items = periods.SelectMany(p => p.Trophies).Select(t => t.Local).ToList();
            var start = items.Min();
            var end = items.Max();
            var sha = image ? Image("game artwork " + title, ".png") : null;

            _entries.Add(new ArchiveEntry
            {
                EntryID = entryID,
                CollectionID = Gaming,
                SourceKey = "setA|setB|" + start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + entryID.ToString(CultureInfo.InvariantCulture),
                Title = title,
                StartLocal = start,
                EndLocal = end,
                StartUtc = Utc(start),
                EndUtc = Utc(end),
                DatePrecision = "Second",
                Summary = null,
                ImageSha256 = sha,
                ImageExtension = sha is null ? null : ".png",
                Source = "PSN trophies",
                DayLevel = dayLevel,
                Periods = periods.Select((period, index) =>
                {
                    var times = period.Trophies.Select(t => t.Local).ToList();

                    return new ArchiveEntryPeriod
                    {
                        EntryID = entryID,
                        PeriodIndex = index,
                        StartUtc = Utc(times.Min()),
                        EndUtc = Utc(times.Max()),
                        StartLocal = times.Min(),
                        EndLocal = times.Max(),
                        Label = period.Label,
                        LocalItemCount = period.Trophies.Count(t => t.Participant == Local),
                        OtherItemCount = period.Trophies.Count(t => t.Participant != Local),
                        Items = period.Trophies.OrderBy(t => t.Local).Select(t => new ArchiveEntryItem
                        {
                            EntryItemID = _nextItem++,
                            EntryID = entryID,
                            PeriodIndex = index,
                            ParticipantID = t.Participant,
                            SetId = t.Participant == Local ? "setA" : "setB",
                            ItemId = t.ItemId,
                            ItemType = t.ItemType,
                            TimeUtc = Utc(t.Local),
                            TimeLocal = t.Local,
                        }).ToList(),
                    };
                }).ToList(),
            });
        }

        /// <summary>Adds a personal memory.</summary>
        internal void Memory(
            long entryID, string title, DateTime start, DateTime? end = null, string precision = "Day", string? summary = null, bool image = false)
        {
            var sha = image ? Image("memory photo " + title, ".jpg") : null;

            _entries.Add(new ArchiveEntry
            {
                EntryID = entryID,
                CollectionID = Personal,
                SourceKey = null,
                Title = title,
                StartLocal = start,
                EndLocal = end,
                StartUtc = null,
                EndUtc = null,
                DatePrecision = precision,
                Summary = summary,
                ImageSha256 = sha,
                ImageExtension = sha is null ? null : ".jpg",
                Source = null,
                DayLevel = null,
            });
        }

        /// <summary>Adds a definition for (set, item), with an icon when asked.</summary>
        internal void Definition(string setId, string itemId, string? name, bool hidden = false, bool icon = false, string? detail = null)
        {
            var sha = icon ? Image("icon " + setId + itemId, ".png") : null;

            _definitions.Add(new ArchiveItemDefinition
            {
                SetId = setId,
                ItemId = itemId,
                ItemType = "bronze",
                Name = name,
                Detail = detail,
                Hidden = hidden,
                GroupId = null,
                IconSha256 = sha,
                IconExtension = sha is null ? null : ".png",
            });
        }

        /// <summary>Adds a link from a personal memory to a message or an entry.</summary>
        internal void Link(long fromEntryID, int? toMessage = null, long? toEntry = null) =>
            _links.Add(new ArchiveEntryLink
            {
                EntryLinkID = _links.Count + 1,
                FromEntryID = fromEntryID,
                ToEntryID = toEntry,
                ToMessageSequenceNumber = toMessage,
            });

        /// <summary>Writes the archive into <paramref name="folder"/>: database, media, manifest, contents list.</summary>
        internal string Write(string folder)
        {
            var mediaFolder = Path.Combine(folder, ArchiveFormat.MediaFolderName);
            Directory.CreateDirectory(mediaFolder);

            var created = new DateTime(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);
            ArchiveCounts counts;

            using (var writer = ArchiveWriter.Create(Path.Combine(folder, ArchiveFormat.DatabaseFileName), new ArchiveInfo
            {
                FormatVersion = ArchiveFormat.FormatVersion,
                CreatedUtc = created,
                ProducerVersion = "Memory test builder 1.0",
                ConversationTitle = "Alex and Sam",
                SourceExportSha256 = string.Empty,
                TimeZoneID = string.Empty,
                LocalParticipantID = Local,
            }))
            {
                writer.WriteParticipant(new ArchiveParticipant { ParticipantID = Local, DisplayName = "Alex", IsLocal = true });
                writer.WriteParticipant(new ArchiveParticipant { ParticipantID = Other, DisplayName = "Sam", IsLocal = false });

                foreach (var (sha, (bytes, extension)) in _media)
                {
                    File.WriteAllBytes(Path.Combine(mediaFolder, sha + extension), bytes);
                    writer.WriteMedia(new ArchiveMedia
                    {
                        Sha256 = sha,
                        MediaType = "Image",
                        SizeBytes = bytes.Length,
                        DurationMS = null,
                        Width = null,
                        Height = null,
                    });
                }

                foreach (var message in _messages)
                {
                    writer.WriteMessage(message);
                }

                writer.WriteCollection(new ArchiveCollection
                {
                    CollectionID = Gaming,
                    Kind = ArchiveMemoryVocabulary.KindImported,
                    Name = "Shared gaming",
                    SourceDescription = "Synthetic shared activity",
                    ShownByDefault = GamingShownByDefault,
                    PeriodGapMinutes = 60,
                });

                writer.WriteCollection(new ArchiveCollection
                {
                    CollectionID = Personal,
                    Kind = ArchiveMemoryVocabulary.KindPersonal,
                    Name = "Holidays",
                    SourceDescription = null,
                    ShownByDefault = PersonalShownByDefault,
                    PeriodGapMinutes = null,
                });

                foreach (var entry in _entries)
                {
                    writer.WriteEntry(entry);
                }

                foreach (var link in _links)
                {
                    writer.WriteEntryLink(link);
                }

                foreach (var definition in _definitions)
                {
                    writer.WriteItemDefinition(definition);
                }

                counts = writer.Complete();
            }

            File.WriteAllBytes(
                Path.Combine(folder, ArchiveFormat.ManifestFileName),
                new ArchiveManifest(ArchiveManifest.FormatName, ArchiveFormat.FormatVersion, "Memory test builder 1.0", created, counts).ToBytes());
            File.WriteAllBytes(Path.Combine(folder, ArchiveContents.FileName), ArchiveContents.Build(folder));

            return folder;
        }

        private string Image(string seed, string extension)
        {
            var bytes = Encoding.UTF8.GetBytes("synthetic " + seed);
            var sha = ArchiveHashing.Hex(SHA256.HashData(bytes));
            _media[sha] = (bytes, extension);

            return sha;
        }

        private static DateTime Utc(DateTime local) => DateTime.SpecifyKind(local.AddHours(-2), DateTimeKind.Utc);
    }
}
