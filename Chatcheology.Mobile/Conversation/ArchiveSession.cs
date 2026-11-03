using Chatcheology.Archive;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Memories;
using Chatcheology.Mobile.Search;
using Chatcheology.Mobile.Settings;

namespace Chatcheology.Mobile.Conversation
{
    /// <summary>Where a timeline was opened, and how the list should be placed.</summary>
    /// <param name="RowIndex">The row to place, or -1 when there are no rows.</param>
    /// <param name="AtLatest">
    /// True when opened at the newest messages (first open, or a stored position no longer valid): place
    /// the row at the bottom. False when opened at a stored position: place the row at the top.
    /// </param>
    public readonly record struct OpenedAt(int RowIndex, bool AtLatest);

    /// <summary>An archive opened with its timeline loaded, ready for the conversation screen.</summary>
    /// <param name="Session">The open session; whoever receives it closes it.</param>
    /// <param name="Timeline">The timeline, loaded and not yet bound to a list.</param>
    /// <param name="At">Where it was opened and how to place the list.</param>
    public sealed record OpenedConversation(ArchiveSession Session, ConversationTimeline Timeline, OpenedAt At);

    /// <summary>One participant Settings offers as "me".</summary>
    /// <param name="ParticipantID">The participant.</param>
    /// <param name="Label">The name, marked when it is the archive's own choice.</param>
    public sealed record MeChoice(long ParticipantID, string Label);

    /// <summary>The outcome of saving one of the phone's own noted dates with its gallery links (CH-48).</summary>
    /// <param name="Errors">The date's broken rules, by field.</param>
    /// <param name="Links">The links' problems, by row, and the links stored.</param>
    public sealed record OwnNotedDateSave(IReadOnlyDictionary<NotedDateField, string> Errors, GalleryLinkCheck Links)
    {
        /// <summary>Whether the date was stored.</summary>
        public bool Saved => Errors.Count == 0 && Links.IsValid;
    }

    /// <summary>
    /// One opened archive: its reader, who "me" is, where the reader last was, and its search index.
    /// </summary>
    /// <remarks>
    /// <para><strong>Me.</strong> The archive's choice (<c>Participant.IsLocal</c>) unless one was stored
    /// for this archive's identity; a stored choice that is not a participant of the archive is ignored.</para>
    /// <para><strong>Position.</strong> The sequence number at the top of the screen, stored per archive.
    /// Opening loads the page before it and the page from it; the first time, the newest page.</para>
    /// <para><strong>Threads.</strong> <see cref="Open"/> reads the whole conversation once (the
    /// low-quality links), so it runs off the UI thread. The reader is used by one thread at a time; the
    /// search index has a reader of its own, so it can be built while pages load.</para>
    /// <para><strong>Memories (CH-42).</strong> An archive with memory collections has them read once at
    /// open (<see cref="Memories"/>), so placing rows, details and memory search read nothing more. Each
    /// collection is a layer, on or off per archive (default: the collection's own), with filters for an
    /// imported one. Gallery, Starred and On this day stay message-only, except that On this day shows
    /// yearly memories (CH-53) whatever the layer switches say.</para>
    /// </remarks>
    public sealed class ArchiveSession : IDisposable
    {
        private readonly ArchiveSettings _settings;
        private readonly string _identity;
        private readonly IReadOnlyList<ArchiveNotedDate> _giftedNotedDates;
        private readonly IReadOnlyDictionary<long, IReadOnlyList<GalleryLink>> _giftedNotedDateLinks;
        private readonly IReadOnlyList<YearlyMemory> _yearly;
        private readonly CancellationTokenSource _closing = new();
        private readonly object _searchLock = new();
        private Task<ConversationSearch>? _search;
        private Task<Gallery.PhotoGallery>? _gallery;
        private Task<MemoryList>? _memoryList;

        private ArchiveSession(InstalledArchive archive, ArchiveReader reader, ArchiveSettings settings, string identity)
        {
            Archive = archive;
            Reader = reader;
            _settings = settings;
            _identity = identity;

            Participants = reader.ReadParticipants();
            DefaultMe = Participants.FirstOrDefault(p => p.IsLocal)?.ParticipantID ?? reader.Info.LocalParticipantID;
            MessageCount = reader.ReadMessageCount();
            LowQuality = LowQualityIndex.Build(reader, _closing.Token);
            Stars = new Stars(settings, identity);
            Memories = MemoryIndex.Build(reader, _closing.Token);
            _giftedNotedDates = reader.ReadNotedDates();

            // The gifted dates' gallery links (CH-48), read once like the dates; a row that breaks a rule is left out.
            _giftedNotedDateLinks = _giftedNotedDates.Count == 0
                ? new Dictionary<long, IReadOnlyList<GalleryLink>>()
                : GalleryLinks.GroupFromArchive(reader.ReadAllExternalLinks())
                    .Where(pair => pair.Key.Kind == ExternalLinkOwnerKind.NotedDate)
                    .ToDictionary(pair => pair.Key.OwnerID, pair => pair.Value);

            // Yearly memories (CH-53, version 3), read once; a row that breaks the rule is left out.
            _yearly = YearlyMemories.From(reader.ReadYearlyEntries());
        }

        /// <summary>The archive's memories (CH-42), read at open; empty for an archive without any.</summary>
        public MemoryIndex Memories { get; }

        /// <summary>Which memory rows are visible now, from the layer switches and filters in Settings.</summary>
        public MemoryView MemoryView => new(
            Memories.Collections
                .Where(c => _settings.GetLayerShown(_identity, c.CollectionID, c.ShownByDefault))
                .Select(c => c.CollectionID),
            Memories.Collections
                .Where(MemoryIndex.HasFilters)
                .SelectMany(c => MemoryFilters.All.Select(f => (c.CollectionID, f)))
                .Where(pair => !_settings.GetMemoryFilter(_identity, pair.CollectionID, pair.f))
                .Select(pair => (pair.CollectionID, pair.f)));

        /// <summary>Every layer with its switch and filters, for Settings, in collection order.</summary>
        public IReadOnlyList<MemoryLayerChoice> MemoryLayers => Memories.Collections
            .Select(c => new MemoryLayerChoice(
                c.CollectionID,
                c.Name,
                _settings.GetLayerShown(_identity, c.CollectionID, c.ShownByDefault),
                MemoryIndex.HasFilters(c)
                    ? MemoryFilters.All.Select(f => (f, _settings.GetMemoryFilter(_identity, c.CollectionID, f))).ToList()
                    : []))
            .ToList();

        /// <summary>Switches a layer on or off. The conversation is rebuilt by the caller (a new timeline).</summary>
        public void SetLayerShown(long collectionID, bool shown) => _settings.SetLayerShown(_identity, collectionID, shown);

        /// <summary>Turns a filter of an imported layer on or off.</summary>
        public void SetMemoryFilter(long collectionID, string filter, bool on) =>
            _settings.SetMemoryFilter(_identity, collectionID, filter, on);

        /// <summary>The memories whose titles match <paramref name="query"/>, among the visible ones only.</summary>
        public IReadOnlyList<MemorySearchHit> FindMemories(string query) => Memories.Find(query, MemoryView);

        /// <summary>The details of a memory row, or <see langword="null"/>.</summary>
        public MemoryDetails? MemoryDetails(MemoryKey key) => Memories.Details(key);

        /// <summary>The archive's starred messages and photos (CH-38), kept in the app's settings.</summary>
        public Stars Stars { get; }

        /// <summary>The archive.</summary>
        public InstalledArchive Archive { get; }

        /// <summary>The open reader. One read at a time.</summary>
        public ArchiveReader Reader { get; }

        /// <summary>The conversation's title, for the conversation screen's heading.</summary>
        public string Title => Reader.Info.ConversationTitle;

        /// <summary>Every participant, by identifier.</summary>
        public IReadOnlyList<ArchiveParticipant> Participants { get; }

        /// <summary>The archive's own "me".</summary>
        public long DefaultMe { get; }

        /// <summary>"Me": the stored choice when there is a valid one, otherwise <see cref="DefaultMe"/>.</summary>
        public long Me => _settings.GetMe(_identity) is { } chosen && Participants.Any(p => p.ParticipantID == chosen)
            ? chosen
            : DefaultMe;

        /// <summary>Whether "me" is a choice made in Settings rather than the archive's.</summary>
        public bool IsMeChanged => Me != DefaultMe;

        /// <summary>How many messages the archive holds.</summary>
        public int MessageCount { get; }

        /// <summary>Whether the archive holds no messages: an empty state, with no date picker and no search.</summary>
        public bool IsEmpty => MessageCount == 0;

        /// <summary>Whether the date picker is offered.</summary>
        public bool CanPickDate => !IsEmpty;

        /// <summary>Whether search is offered.</summary>
        public bool CanSearch => !IsEmpty;

        /// <summary>The conversation's low-quality copy links.</summary>
        public LowQualityIndex LowQuality { get; }

        /// <summary>
        /// Opens <paramref name="archive"/> and records that it was opened now. Reads the whole
        /// conversation once; call it off the UI thread.
        /// </summary>
        /// <exception cref="ArchiveFormatException">The archive cannot be read.</exception>
        /// <exception cref="ArgumentException">The archive's identity is not known.</exception>
        public static ArchiveSession Open(InstalledArchive archive, ArchiveSettings settings, TimeProvider? clock = null)
        {
            ArgumentNullException.ThrowIfNull(archive);
            ArgumentNullException.ThrowIfNull(settings);

            if (archive.ContentsSha256 is not { } identity)
            {
                throw new ArgumentException("The archive's contents list cannot be read, so it cannot be opened.", nameof(archive));
            }

            var reader = ArchiveReader.Open(archive.Folder);

            try
            {
                var session = new ArchiveSession(archive, reader, settings, identity);
                settings.SetLastOpenedUtc(identity, (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);

                return session;
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Opens <paramref name="archive"/>, builds its timeline with the current "me" and opens it where
        /// the reader last was, all on the calling thread (call it off the UI thread). The session is
        /// closed again when any step fails, so nothing is left open.
        /// </summary>
        /// <exception cref="ArchiveFormatException">The archive cannot be read.</exception>
        /// <exception cref="ArgumentException">The archive's identity is not known.</exception>
        public static OpenedConversation OpenConversation(InstalledArchive archive, ArchiveSettings settings, TimeProvider? clock = null)
        {
            var session = Open(archive, settings, clock);

            try
            {
                var timeline = session.CreateTimeline();

                return new OpenedConversation(session, timeline, session.OpenTimeline(timeline));
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        /// <summary>Chooses "me"; choosing the archive's own returns to it.</summary>
        /// <exception cref="ArgumentException">Not a participant of the archive.</exception>
        public void SetMe(long participantID)
        {
            if (Participants.All(p => p.ParticipantID != participantID))
            {
                throw new ArgumentException("Only a participant of the conversation can be me.", nameof(participantID));
            }

            _settings.SetMe(_identity, participantID == DefaultMe ? null : participantID);
        }

        /// <summary>
        /// The participants Settings offers as "me", in identifier order, the archive's own choice marked;
        /// system senders are not participants and are not offered.
        /// </summary>
        public IReadOnlyList<MeChoice> MeChoices => Participants
            .OrderBy(p => p.ParticipantID)
            .Select(p => new MeChoice(
                p.ParticipantID,
                p.ParticipantID == DefaultMe ? p.DisplayName + " (the archive's choice)" : p.DisplayName))
            .ToList();

        /// <summary>The archive's format version, for Settings &gt; About.</summary>
        public int FormatVersion => Reader.Info.FormatVersion;

        /// <summary>The last validation of this archive as About shows it.</summary>
        public string ValidationText => ArchiveSettings.DescribeValidation(_settings.GetValidation(_identity));

        /// <summary>A timeline with the current "me". After "me" changes, a new one is made and opened.</summary>
        public ConversationTimeline CreateTimeline() => new(Reader, Me, LowQuality, Stars, Memories.Layout(MemoryView));

        /// <summary>
        /// Opens <paramref name="timeline"/> where the reader last was, or at the newest messages the
        /// first time (or when the stored position is past the end).
        /// </summary>
        public OpenedAt OpenTimeline(ConversationTimeline timeline)
        {
            ArgumentNullException.ThrowIfNull(timeline);

            if (_settings.GetPosition(_identity) is { } anchor)
            {
                var index = timeline.OpenAt(anchor);

                if (index >= 0)
                {
                    return new OpenedAt(index, AtLatest: false);
                }
            }

            return new OpenedAt(timeline.OpenAtLatest(), AtLatest: true);
        }

        /// <summary>The stored position: the sequence number at the top of the screen, or <see langword="null"/>.</summary>
        public int? Position => _settings.GetPosition(_identity);

        /// <summary>Stores the sequence number at the top of the screen.</summary>
        public void SavePosition(int sequenceNumber) => _settings.SetPosition(_identity, sequenceNumber);

        /// <summary>The days that have messages, for On this day (which stays message-only).</summary>
        public IReadOnlyList<DateOnly> ReadDays() => Reader.ReadDays();

        /// <summary>
        /// The days the date picker offers: days with messages and, with a memory layer on, days with a
        /// visible memory row (before, between or after the messages).
        /// </summary>
        public IReadOnlyList<DateOnly> ReadCalendarDays()
        {
            var days = Reader.ReadDays();
            var layout = Memories.Layout(MemoryView);

            return layout.IsEmpty ? days : days.Concat(layout.Days).Distinct().Order().ToList();
        }

        /// <summary>
        /// The search index, built in the background the first time it is asked for, with a reader of its
        /// own. Later calls return the same task.
        /// </summary>
        public Task<ConversationSearch> GetSearchAsync()
        {
            lock (_searchLock)
            {
                ObjectDisposedException.ThrowIf(_closing.IsCancellationRequested, this);

                var token = _closing.Token;
                var folder = Reader.FolderPath;

                return _search ??= Task.Run(() => ConversationSearch.Build(folder, token), token);
            }
        }

        /// <summary>
        /// The photo gallery (CH-37), built in the background the first time it is asked for, with a reader
        /// of its own like the search index. Later calls return the same task.
        /// </summary>
        public Task<Gallery.PhotoGallery> GetGalleryAsync()
        {
            lock (_searchLock)
            {
                ObjectDisposedException.ThrowIf(_closing.IsCancellationRequested, this);

                var token = _closing.Token;
                var folder = Reader.FolderPath;
                var me = Me;

                return _gallery ??= Task.Run(
                    () =>
                    {
                        using var reader = ArchiveReader.Open(folder);

                        return Gallery.PhotoGallery.Build(reader, me, LowQuality, Stars, token);
                    },
                    token);
            }
        }

        /// <summary>
        /// The Memories list (CH-56), built in the background the first time it is asked for, from the memories
        /// read at open (no reader is opened). Later calls return the same task.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The session is closed.</exception>
        public Task<MemoryList> GetMemoryListAsync()
        {
            lock (_searchLock)
            {
                ObjectDisposedException.ThrowIf(_closing.IsCancellationRequested, this);

                var token = _closing.Token;
                var memories = Memories;
                var yearly = _yearly.Select(memory => memory.EntryID).ToHashSet();

                return _memoryList ??= Task.Run(() => MemoryList.Build(memories, yearly, token), token);
            }
        }

        /// <summary>
        /// Where Show in chat goes for a row of <paramref name="list"/> with the layers and filters as Settings
        /// has them now, or <see langword="null"/> for no button.
        /// </summary>
        public MemoryShowTarget? MemoryShowInChat(MemoryList list, MemoryKey key)
        {
            ArgumentNullException.ThrowIfNull(list);

            return list.ShowInChat(key, MemoryView, !IsEmpty);
        }

        /// <summary>The gallery when it has been built, so a star toggle can update it; otherwise <see langword="null"/>.</summary>
        public Gallery.PhotoGallery? GalleryIfBuilt
        {
            get
            {
                lock (_searchLock)
                {
                    return _gallery is { IsCompletedSuccessfully: true } built ? built.Result : null;
                }
            }
        }

        /// <summary>
        /// The Starred list (CH-38), read afresh each time with a reader of its own, so it reflects the
        /// latest stars and runs beside the page reads.
        /// </summary>
        public Task<IReadOnlyList<StarredEntry>> ReadStarredAsync()
        {
            lock (_searchLock)
            {
                ObjectDisposedException.ThrowIf(_closing.IsCancellationRequested, this);

                var token = _closing.Token;
                var folder = Reader.FolderPath;
                var me = Me;

                return Task.Run(
                    () =>
                    {
                        using var reader = ArchiveReader.Open(folder);

                        return StarredList.Build(reader, me, LowQuality, Stars, token);
                    },
                    token);
            }
        }

        /// <summary>
        /// On this day (CH-39) for <paramref name="days"/> (from <see cref="ReadDays"/>) on <paramref name="today"/>:
        /// yearly memories (CH-53), noted dates (CH-47) and message years.
        /// </summary>
        public OnThisDay OnThisDayFor(IEnumerable<DateOnly> days, DateOnly today) =>
            OnThisDay.For(_settings, _identity, days, today, _giftedNotedDates, _giftedNotedDateLinks, _yearly, Memories);

        /// <summary>
        /// The archive's yearly memories (CH-53) with their mute switches, by title, for Settings. Read from the
        /// settings each time, so a change shows at once.
        /// </summary>
        public IReadOnlyList<YearlyMemoryListing> YearlyMemoryListings => YearlyMemories.List(_settings, _identity, _yearly);

        /// <summary>
        /// Mutes or unmutes a yearly memory (it stays on the card; muting only stops its notification). Only this
        /// archive's yearly memories can be muted.
        /// </summary>
        public void SetYearlyMemoryMuted(long entryID, bool muted)
        {
            if (_yearly.Any(memory => memory.EntryID == entryID))
            {
                _settings.SetYearlyMemoryMuted(_identity, entryID, muted);
            }
        }

        /// <summary>
        /// Every noted date (CH-47) for Settings: the archive's gifted ones, which can only be muted, then the
        /// phone's own. Read from the settings each time, so a change shows at once.
        /// </summary>
        public IReadOnlyList<NotedDateListing> NotedDateListings =>
            NotedDates.List(_settings, _identity, _giftedNotedDates, _giftedNotedDateLinks);

        /// <summary>Mutes or unmutes a gifted noted date (it stays on the card; muting only stops its notification).</summary>
        public void SetNotedDateMuted(long notedDateID, bool muted)
        {
            if (_giftedNotedDates.Any(date => date.NotedDateID == notedDateID))
            {
                _settings.SetNotedDateMuted(_identity, notedDateID, muted);
            }
        }

        /// <summary>
        /// Adds a date of the phone's own, or replaces the one with <paramref name="id"/>. Nothing is stored
        /// when a rule of <see cref="NotedDateRules"/> is broken; the broken rules are returned.
        /// </summary>
        /// <param name="id">
        /// The own date to replace; when none has this id, a new one is added under it (so a repeated save of the
        /// same new date, with an id chosen once, replaces rather than duplicates). <see langword="null"/> adds one
        /// with a fresh id.
        /// </param>
        /// <param name="title">The title.</param>
        /// <param name="month">The month.</param>
        /// <param name="day">The day.</param>
        /// <param name="year">The year, or none.</param>
        /// <param name="note">The note, or none (blank is none).</param>
        /// <param name="today">The phone's date; a year after it is refused.</param>
        /// <remarks>An existing date keeps its gallery links (CH-48); a new one has none.</remarks>
        public IReadOnlyDictionary<NotedDateField, string> SaveOwnNotedDate(
            string? id, string? title, int month, int day, int? year, string? note, DateOnly today)
        {
            var links = id is null
                ? []
                : _settings.GetOwnNotedDates(_identity).FirstOrDefault(date => date.Id == id)?.Links ?? [];

            return SaveOwnNotedDate(id, title, month, day, year, note, links.Select(link => ((string?)link.Url, link.Label)).ToList(), today).Errors;
        }

        /// <summary>
        /// Adds or replaces a date of the phone's own with its ordered gallery links (CH-48). Nothing is stored when
        /// a rule of <see cref="NotedDateRules"/> or <see cref="ExternalLinkRules"/> is broken; every broken rule
        /// is returned at once. Rows with neither address nor label are left out.
        /// </summary>
        /// <param name="id">As for the overload without links.</param>
        /// <param name="title">The title.</param>
        /// <param name="month">The month.</param>
        /// <param name="day">The day.</param>
        /// <param name="year">The year, or none.</param>
        /// <param name="note">The note, or none (blank is none).</param>
        /// <param name="links">The edited links, in order: address and optional label.</param>
        /// <param name="today">The phone's date; a year after it is refused.</param>
        public OwnNotedDateSave SaveOwnNotedDate(
            string? id, string? title, int month, int day, int? year, string? note, IReadOnlyList<(string? Url, string? Label)> links, DateOnly today)
        {
            ArgumentNullException.ThrowIfNull(links);

            var stored = NotedDateRules.NormaliseNote(note);
            var errors = NotedDateRules.Validate(title, month, day, year, stored, today.Year);
            var check = GalleryLinks.Check(links);

            if (errors.Count > 0 || !check.IsValid)
            {
                return new OwnNotedDateSave(errors, check);
            }

            var own = _settings.GetOwnNotedDates(_identity).ToList();
            var existing = id is null ? -1 : own.FindIndex(date => date.Id == id);
            var date = new OwnNotedDate(id ?? Guid.NewGuid().ToString("N"), title!.Trim(), month, day, year, stored) { Links = check.Links };

            if (existing >= 0)
            {
                own[existing] = date;
            }
            else
            {
                own.Add(date);
            }

            _settings.SetOwnNotedDates(_identity, own);

            return new OwnNotedDateSave(errors, check);
        }

        /// <summary>Deletes one of the phone's own noted dates; a gifted one cannot be deleted here.</summary>
        public void DeleteOwnNotedDate(string id) =>
            _settings.SetOwnNotedDates(_identity, _settings.GetOwnNotedDates(_identity).Where(date => date.Id != id));

        /// <summary>Whether On this day is on for this archive (Settings).</summary>
        public bool OnThisDayEnabled
        {
            get => _settings.GetOnThisDayEnabled(_identity);
            set => _settings.SetOnThisDayEnabled(_identity, value);
        }

        /// <summary>Stops a search index being built and closes the reader.</summary>
        public void Dispose()
        {
            lock (_searchLock)
            {
                if (_closing.IsCancellationRequested)
                {
                    return;
                }

                _closing.Cancel();
            }

            // The token source is not disposed: a search build still running reads its token, and a
            // source without timers holds nothing that needs releasing.
            Reader.Dispose();
        }
    }
}
