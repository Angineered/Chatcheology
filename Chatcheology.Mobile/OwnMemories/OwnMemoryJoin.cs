using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Chatcheology.Mobile.Memories;

namespace Chatcheology.Mobile.OwnMemories
{
    /// <summary>
    /// The phone owner's own memories as one open archive sees them (CH-54 step 1b): their own <see cref="MemoryIndex"/>
    /// over the archive's message times, their yearly memories and the My memories layer switch, read at one moment.
    /// Immutable; the session swaps in a new one after a save or delete.
    /// </summary>
    /// <param name="Index">The own index: one personal collection, every key owned <see cref="MemoryOwner.Own"/>.</param>
    /// <param name="Yearly">The own yearly memories that keep the rule, by row id.</param>
    /// <param name="LayerOn">Whether the My memories layer is on (per phone, default on).</param>
    public sealed record OwnMemoryJoin(MemoryIndex Index, IReadOnlyList<YearlyMemory> Yearly, bool LayerOn)
    {
        /// <summary>No own memories, layer off: an archive opened without the own store.</summary>
        public static OwnMemoryJoin None { get; } = new(MemoryIndex.Empty, [], false);

        /// <summary>How many own memories there are.</summary>
        public int Count => Index.EntryCount;

        /// <summary>Which own rows are visible: all of them while the layer is on, none while it is off.</summary>
        public MemoryView View => LayerOn && Count > 0 ? new MemoryView([OwnMemoryIndex.CollectionID]) : MemoryView.None;

        /// <summary>The own yearly memories' row ids, for the Memories list.</summary>
        public IReadOnlySet<long> YearlyRowIDs => Yearly.Select(memory => memory.EntryID).ToHashSet();
    }

    /// <summary>
    /// How the phone owner's own memories (CH-54) become memory rows: one personal collection
    /// (<see cref="ArchiveMemoryVocabulary.KindPersonal"/>, so links show and no filter applies), built through
    /// <see cref="MemorySource"/> with the message times the archive's session has already read. Nothing reads the
    /// archive, so <see cref="ArchiveReadGate"/> is never bypassed.
    /// </summary>
    /// <remarks>
    /// <para><strong>Identity.</strong> Each own memory is an entry whose id is its local row id, and every key carries
    /// <see cref="MemoryOwner.Own"/>: an archive entry with the same number is a different memory. Archive ids are
    /// never offset or reused.</para>
    /// <para><strong>Placement.</strong> As a personal archive memory: one card at its start day's midnight, so a date
    /// before the first message or after the last is placed before or after the messages.</para>
    /// <para><strong>Photos.</strong> All of them, in order, each with its caption (the first is the cover); a photo whose
    /// file is gone is not shown. <strong>Source</strong> is not shown (fixed, D-36); no participants, no message links.</para>
    /// </remarks>
    public static class OwnMemoryIndex
    {
        /// <summary>The own collection's identifier inside the own index (it has its own key space).</summary>
        public const long CollectionID = 1;

        /// <summary>The own collection's name, as the details and the Memories list show it.</summary>
        public const string CollectionName = "My memories";

        /// <summary>The own collection.</summary>
        public static ArchiveCollection Collection { get; } = new()
        {
            CollectionID = CollectionID,
            Kind = ArchiveMemoryVocabulary.KindPersonal,
            Name = CollectionName,
            SourceDescription = null,
            ShownByDefault = true,
            PeriodGapMinutes = null,
        };

        /// <summary>
        /// The own index over <paramref name="messageTimes"/> (in chat order, with their <paramref name="sequenceNumbers"/>
        /// when they are not 1 to the count).
        /// </summary>
        public static MemoryIndex Build(
            IReadOnlyList<OwnMemory> memories, IReadOnlyList<DateTime> messageTimes, IReadOnlyList<int>? sequenceNumbers = null)
        {
            ArgumentNullException.ThrowIfNull(memories);
            ArgumentNullException.ThrowIfNull(messageTimes);

            return MemoryIndex.Build(new MemorySource
            {
                Collections = [Collection],
                Entries = memories.Select(EntryOf).ToList(),
                ParticipantNames = new Dictionary<long, string>(),
                Definitions = [],
                IconPath = _ => null,
                MessageTimes = messageTimes,
                MessageSequenceNumbers = sequenceNumbers,
                Owner = MemoryOwner.Own,
            });
        }

        /// <summary>The own yearly memories that keep the rule (Every year, Day, no end), by row id.</summary>
        public static IReadOnlyList<YearlyMemory> Yearly(IEnumerable<OwnMemory> memories)
        {
            ArgumentNullException.ThrowIfNull(memories);

            return memories
                .Where(KeepsYearlyRule)
                .Select(memory => new YearlyMemory(memory.RowID, memory.Title, memory.Start, MemoryOwner.Own))
                .OrderBy(memory => memory.EntryID)
                .ToList();
        }

        /// <summary>
        /// The own yearly memories with their Notify switches (mutes by identity, kept in the own store), by title, for
        /// the My memories settings and the notification schedule.
        /// </summary>
        public static IReadOnlyList<OwnYearlyMemoryListing> YearlyListings(IEnumerable<OwnMemory> memories, IReadOnlySet<Guid> muted)
        {
            ArgumentNullException.ThrowIfNull(memories);
            ArgumentNullException.ThrowIfNull(muted);

            return memories
                .Where(KeepsYearlyRule)
                .Select(memory => new OwnYearlyMemoryListing(memory.Id, memory.RowID, memory.Title, memory.Start, Notify: !muted.Contains(memory.Id)))
                .OrderBy(memory => memory.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(memory => memory.RowID)
                .ToList();
        }

        /// <summary>The own yearly memories of <paramref name="store"/> with their Notify switches; read in one go.</summary>
        public static IReadOnlyList<OwnYearlyMemoryListing> YearlyListings(OwnMemoryStore store)
        {
            ArgumentNullException.ThrowIfNull(store);

            return YearlyListings(store.List(), store.Settings.Muted());
        }

        /// <summary>
        /// Reads <paramref name="store"/> and joins it to an archive's <paramref name="messageTimes"/>: the index, the
        /// yearly memories and the layer switch.
        /// </summary>
        public static OwnMemoryJoin Join(OwnMemoryStore store, IReadOnlyList<DateTime> messageTimes, IReadOnlyList<int>? sequenceNumbers = null)
        {
            ArgumentNullException.ThrowIfNull(store);

            var memories = store.List();

            return new OwnMemoryJoin(Build(memories, messageTimes, sequenceNumbers), Yearly(memories), store.Settings.LayerOn);
        }

        /// <summary>Whether an own memory is a yearly one: Every year, dated to the day, without an end (checked again here).</summary>
        private static bool KeepsYearlyRule(OwnMemory memory) =>
            memory.EveryYear && memory.Precision == MemoryDatePrecision.Day && memory.End is null;

        private static MemorySourceEntry EntryOf(OwnMemory memory) =>
            new(
                new ArchiveEntry
                {
                    EntryID = memory.RowID,
                    CollectionID = CollectionID,
                    SourceKey = null,
                    Title = memory.Title,
                    StartLocal = memory.Start.ToDateTime(TimeOnly.MinValue),
                    EndLocal = memory.End?.ToDateTime(TimeOnly.MinValue),
                    StartUtc = null,
                    EndUtc = null,
                    DatePrecision = memory.Precision == MemoryDatePrecision.Month ? "Month" : "Day",
                    Summary = memory.Summary,
                    ImageSha256 = null,
                    ImageExtension = null,

                    // Fixed and not shown (D-36).
                    Source = null,
                    DayLevel = null,
                    EveryYear = KeepsYearlyRule(memory),
                },
                [],

                // Every photo is listed with its caption; none is a separate cover without one.
                null,
                [])
            {
                ExtraPhotos = memory.Photos
                    .Select(photo => new MemoryPhoto(File.Exists(photo.Path) ? photo.Path : null, photo.Caption))
                    .ToList(),
                Galleries = memory.Links
                    .Select(link => GalleryLinks.Keep(link.Url, link.Label))
                    .OfType<GalleryLink>()
                    .ToList(),
            };
    }

    /// <summary>One own yearly memory as the My memories settings and the notification schedule see it.</summary>
    /// <param name="Id">The memory's identity: what a Notify mute is kept by.</param>
    /// <param name="RowID">Its local row id (the key of its row and details).</param>
    /// <param name="Title">The title (never in a notification).</param>
    /// <param name="Start">Its own date.</param>
    /// <param name="Notify">Whether it notifies: it does unless muted.</param>
    public sealed record OwnYearlyMemoryListing(Guid Id, long RowID, string Title, DateOnly Start, bool Notify)
    {
        /// <summary>"14 February 2019".</summary>
        public string DateText => NotedDates.DateText(Start.Month, Start.Day, Start.Year);

        /// <summary>The key of its memory row and details.</summary>
        public MemoryKey Key => new(RowID, null, MemoryOwner.Own);
    }
}
