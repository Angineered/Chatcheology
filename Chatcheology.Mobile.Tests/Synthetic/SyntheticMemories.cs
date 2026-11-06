using System.Globalization;
using System.Security.Cryptography;
using Chatcheology.Archive;

namespace Chatcheology.Mobile.Synthetic
{
    /// <summary>
    /// Invented memory layers for the synthetic archive (CH-42): a "Shared gaming" collection (imported,
    /// off by default) and a "Holidays" collection (personal, on by default), so the phone's memory rows
    /// can be looked at on an emulator.
    /// </summary>
    /// <remarks>
    /// <para>Every game, trophy, name and picture is invented. Times are local wall clock with UTC two
    /// hours earlier, as the PSN import records them.</para>
    /// <para>What it holds: period cards of every label (strong, supporting, same period); one-account
    /// periods beside shared ones and alone; a record with no shared period (a "different times" line);
    /// records before the first and after the last message and on a day without messages; trophy
    /// definitions with icons (one hidden, and items without a definition); one personal memory with an
    /// image, three extra photos (one portrait, one without a caption, one a JPEG with an invented camera location
    /// for CH-59) and links to a message and to a game.</para>
    /// </remarks>
    internal static class SyntheticMemories
    {
        internal const long Gaming = 1;
        internal const long Holidays = 2;

        /// <summary>Writes the collections, entries, definitions, links and their pictures.</summary>
        /// <param name="writer">The open writer, messages already written.</param>
        /// <param name="mediaFolder">The archive's media folder.</param>
        /// <param name="messageTimes">Each message's local time, by sequence number less one.</param>
        internal static void Write(ArchiveWriter writer, string mediaFolder, IReadOnlyList<DateTime> messageTimes)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(messageTimes);

            if (messageTimes.Count < 200)
            {
                throw new ArgumentException("Synthetic memories need at least 200 messages.", nameof(messageTimes));
            }

            var messageDays = messageTimes.Select(DateOnly.FromDateTime).ToHashSet();
            var first = DateOnly.FromDateTime(messageTimes[0]);
            var last = DateOnly.FromDateTime(messageTimes[^1]);
            var busy = DateOnly.FromDateTime(messageTimes[100]);
            var gap = Enumerable.Range(10, 400).Select(first.AddDays).First(day => !messageDays.Contains(day));
            var later = DateOnly.FromDateTime(messageTimes[150]);
            var holiday = DateOnly.FromDateTime(messageTimes[60]);

            writer.WriteCollection(new ArchiveCollection
            {
                CollectionID = Gaming,
                Kind = ArchiveMemoryVocabulary.KindImported,
                Name = "Shared gaming",
                SourceDescription = "Invented shared trophy activity",
                ShownByDefault = false,
                PeriodGapMinutes = 60,
            });

            writer.WriteCollection(new ArchiveCollection
            {
                CollectionID = Holidays,
                Kind = ArchiveMemoryVocabulary.KindPersonal,
                Name = "Holidays",
                SourceDescription = null,
                ShownByDefault = true,
                PeriodGapMinutes = null,
            });

            var kart = Picture(writer, mediaFolder, 320, 180, (200, 60, 60), (240, 180, 40));
            var tower = Picture(writer, mediaFolder, 320, 180, (40, 90, 200), (120, 200, 240));
            var seaside = Picture(writer, mediaFolder, 640, 360, (30, 120, 180), (240, 220, 160));
            var ferry = Picture(writer, mediaFolder, 480, 320, (20, 60, 110), (200, 200, 210));
            var lighthouse = Picture(writer, mediaFolder, 360, 640, (230, 230, 235), (190, 50, 50));

            // A JPEG with an invented camera location (CH-59): sharing it must hand out a copy without it.
            var sunset = Media(writer, mediaFolder, SyntheticJpeg.WithLocation(), ".jpg", SyntheticJpeg.Width, SyntheticJpeg.Height);

            // A busy day: strong, one account, same period. Day level strong.
            writer.WriteEntry(Game(1, "Invented Kart Rally", "strong", kart, busy,
                ("strong", [(SyntheticArchive.Local, 19, 0, 0, "1", "bronze"), (SyntheticArchive.Other, 19, 0, 40, "1", "bronze"), (SyntheticArchive.Local, 19, 25, 0, "2", "silver")]),
                ("one-account", [(SyntheticArchive.Other, 20, 40, 0, "3", "bronze")]),
                ("same-period", [(SyntheticArchive.Local, 22, 0, 0, "4", "gold"), (SyntheticArchive.Other, 22, 35, 10, "5", "bronze")])));

            // A day without messages: supporting.
            writer.WriteEntry(Game(2, "Invented Tower Puzzle", "supporting", tower, gap,
                ("supporting", [(SyntheticArchive.Local, 15, 0, 0, "6", "bronze"), (SyntheticArchive.Other, 15, 20, 0, "6", "bronze")])));

            // No shared period: a "different times" line.
            writer.WriteEntry(Game(3, "Invented Star Pilot", "same-day", null, later,
                ("one-account", [(SyntheticArchive.Local, 9, 0, 0, "7", "bronze")]),
                ("one-account", [(SyntheticArchive.Other, 13, 30, 0, "8", "silver")])));

            // Before the first message, and after the last.
            writer.WriteEntry(Game(4, "Invented Kart Rally", "strong", kart, first.AddDays(-30),
                ("strong", [(SyntheticArchive.Local, 18, 0, 0, "9", "bronze"), (SyntheticArchive.Other, 18, 0, 30, "9", "bronze")])));

            writer.WriteEntry(Game(5, "Invented Garden Builder", "same-day", null, last.AddDays(10),
                ("same-period", [(SyntheticArchive.Local, 11, 0, 0, "10", "bronze"), (SyntheticArchive.Other, 11, 45, 0, "11", "platinum")])));

            writer.WriteEntry(new ArchiveEntry
            {
                EntryID = 10,
                CollectionID = Holidays,
                SourceKey = null,
                Title = "Invented seaside weekend",
                StartLocal = holiday.ToDateTime(TimeOnly.MinValue),
                EndLocal = holiday.AddDays(2).ToDateTime(TimeOnly.MinValue),
                StartUtc = null,
                EndUtc = null,
                DatePrecision = "Day",
                Summary = "Two invented days by an invented sea, with an invented ferry.",
                ImageSha256 = seaside.Sha,
                ImageExtension = seaside.Extension,
                Source = null,
                DayLevel = null,
                Images =
                [
                    new ArchiveEntryImage { EntryID = 10, Ordinal = 1, Sha256 = ferry.Sha, Extension = ferry.Extension, Caption = "Invented ferry" },
                    new ArchiveEntryImage { EntryID = 10, Ordinal = 2, Sha256 = lighthouse.Sha, Extension = lighthouse.Extension, Caption = null },
                    new ArchiveEntryImage { EntryID = 10, Ordinal = 3, Sha256 = sunset.Sha, Extension = sunset.Extension, Caption = "Invented sunset over an invented sea" },
                ],
            });

            writer.WriteEntryLink(new ArchiveEntryLink { EntryLinkID = 1, FromEntryID = 10, ToEntryID = null, ToMessageSequenceNumber = 61 });
            writer.WriteEntryLink(new ArchiveEntryLink { EntryLinkID = 2, FromEntryID = 10, ToEntryID = 1, ToMessageSequenceNumber = null });

            // CH-48: two invented external gallery links on reserved example domains, one labelled.
            writer.WriteExternalLink(new ArchiveExternalLink { ExternalLinkID = 1, OwnerKind = ExternalLinkOwnerKind.Entry, OwnerID = 10, Ordinal = 1, Url = "https://example.org/synthetic-album/day-1", Label = "Invented day 1" });
            writer.WriteExternalLink(new ArchiveExternalLink { ExternalLinkID = 2, OwnerKind = ExternalLinkOwnerKind.Entry, OwnerID = 10, Ordinal = 2, Url = "https://www.example.com/synthetic-album/day-2", Label = null });

            // Definitions for some trophies; the others show as "Bronze trophy #7" and so on.
            var icons = new[]
            {
                Picture(writer, mediaFolder, 64, 64, (205, 127, 50), (120, 70, 20)),
                Picture(writer, mediaFolder, 64, 64, (192, 192, 192), (90, 90, 90)),
                Picture(writer, mediaFolder, 64, 64, (230, 190, 40), (140, 100, 10)),
            };

            Definition(writer, "SYNTH-A", "1", "bronze", "First Invented Lap", "Finish an invented lap.", false, icons[0]);
            Definition(writer, "SYNTH-B", "1", "bronze", "First Invented Lap", "Finish an invented lap.", false, icons[0]);
            Definition(writer, "SYNTH-A", "2", "silver", "Invented Drift", "Drift through an invented corner.", false, icons[1]);
            Definition(writer, "SYNTH-A", "4", "gold", "Invented Secret Track", "Find the invented secret track.", true, icons[2]);
            Definition(writer, "SYNTH-B", "6", "bronze", "Invented Tower Top", null, false, null);
        }

        private static ArchiveEntry Game(
            long entryID,
            string title,
            string dayLevel,
            (string Sha, string Extension)? image,
            DateOnly day,
            params (string Label, (long Participant, int Hour, int Minute, int Second, string ItemId, string Type)[] Trophies)[] periods)
        {
            DateTime Local((long Participant, int Hour, int Minute, int Second, string ItemId, string Type) t) =>
                day.ToDateTime(new TimeOnly(t.Hour, t.Minute, t.Second));

            var all = periods.SelectMany(p => p.Trophies).Select(Local).ToList();
            var item = entryID * 100;

            return new ArchiveEntry
            {
                EntryID = entryID,
                CollectionID = Gaming,
                SourceKey = "SYNTH-A|SYNTH-B|" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + entryID.ToString(CultureInfo.InvariantCulture),
                Title = title,
                StartLocal = all.Min(),
                EndLocal = all.Max(),
                StartUtc = Utc(all.Min()),
                EndUtc = Utc(all.Max()),
                DatePrecision = "Second",
                Summary = null,
                ImageSha256 = image?.Sha,
                ImageExtension = image?.Extension,
                Source = "PSN trophies",
                DayLevel = dayLevel,
                Periods = periods.Select((period, index) =>
                {
                    var times = period.Trophies.Select(Local).ToList();

                    return new ArchiveEntryPeriod
                    {
                        EntryID = entryID,
                        PeriodIndex = index,
                        StartUtc = Utc(times.Min()),
                        EndUtc = Utc(times.Max()),
                        StartLocal = times.Min(),
                        EndLocal = times.Max(),
                        Label = period.Label,
                        LocalItemCount = period.Trophies.Count(t => t.Participant == SyntheticArchive.Local),
                        OtherItemCount = period.Trophies.Count(t => t.Participant != SyntheticArchive.Local),
                        Items = period.Trophies.OrderBy(Local).Select(t => new ArchiveEntryItem
                        {
                            EntryItemID = ++item,
                            EntryID = entryID,
                            PeriodIndex = index,
                            ParticipantID = t.Participant,
                            SetId = t.Participant == SyntheticArchive.Local ? "SYNTH-A" : "SYNTH-B",
                            ItemId = t.ItemId,
                            ItemType = t.Type,
                            TimeUtc = Utc(Local(t)),
                            TimeLocal = Local(t),
                        }).ToList(),
                    };
                }).ToList(),
            };
        }

        private static void Definition(
            ArchiveWriter writer, string setId, string itemId, string type, string name, string? detail, bool hidden, (string Sha, string Extension)? icon) =>
            writer.WriteItemDefinition(new ArchiveItemDefinition
            {
                SetId = setId,
                ItemId = itemId,
                ItemType = type,
                Name = name,
                Detail = detail,
                Hidden = hidden,
                GroupId = "default",
                IconSha256 = icon?.Sha,
                IconExtension = icon?.Extension,
            });

        /// <summary>Writes an invented picture into media and its Media row.</summary>
        private static (string Sha, string Extension) Picture(
            ArchiveWriter writer, string mediaFolder, int width, int height, (byte, byte, byte) a, (byte, byte, byte) b) =>
            Media(writer, mediaFolder, SyntheticImage.Png(width, height, a, b), ".png", width, height);

        /// <summary>Writes picture bytes into media under their hash and the extension, and their Media row.</summary>
        private static (string Sha, string Extension) Media(
            ArchiveWriter writer, string mediaFolder, byte[] bytes, string extension, int width, int height)
        {
            var sha = ArchiveHashing.Hex(SHA256.HashData(bytes));

            File.WriteAllBytes(Path.Combine(mediaFolder, sha + extension), bytes);

            writer.WriteMedia(new ArchiveMedia
            {
                Sha256 = sha,
                MediaType = "Image",
                SizeBytes = bytes.Length,
                DurationMS = null,
                Width = width,
                Height = height,
            });

            return (sha, extension);
        }

        private static DateTime Utc(DateTime local) => DateTime.SpecifyKind(local.AddHours(-2), DateTimeKind.Utc);
    }
}
