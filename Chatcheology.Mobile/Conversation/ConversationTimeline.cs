using Chatcheology.Archive;
using Chatcheology.Mobile.Memories;

namespace Chatcheology.Mobile.Conversation
{
    /// <summary>
    /// The loaded part of one archived conversation, as list rows, grown a page at a time in either
    /// direction as the reader scrolls.
    /// </summary>
    /// <remarks>
    /// <para><strong>Reading and applying are separate.</strong> <see cref="ReadEarlier"/> and
    /// <see cref="ReadLater"/> only read, so a caller can run them off the UI thread;
    /// <see cref="Prepend"/> and <see cref="Append"/> change <see cref="Rows"/> and belong on the
    /// thread the list is bound on. One read at a time: the reader's connection is not shared
    /// between threads.</para>
    /// <para><strong>Separators are right where pages join.</strong> A day's separator sits before its
    /// first loaded message. When an earlier page ends on the day the loaded rows begin with, the
    /// separator at the top is taken out and the page, which brings that day's separator at the start
    /// of its own run of the day, goes in above, so the day is neither shown twice nor lost. The rows
    /// always equal what <see cref="BuildRows"/> gives for the loaded messages in one go.</para>
    /// <para>Taking the separator out, rather than slipping the page's messages of that day in below it,
    /// is what keeps the screen still: every message that was loaded keeps the rows directly above it,
    /// so a list that holds its first visible row in place holds the message too, even when that
    /// separator was the top row on screen (CH-26 trial).</para>
    /// <para>Page size is presentation only; it never changes what the archive holds.</para>
    /// <para><strong>Memory rows (CH-42).</strong> With a memory layer on, a page also carries the memory
    /// rows placed after its messages (and, for the page that starts the chat, those before the first
    /// message), each at its local time. They get day separators like messages, so days that have only
    /// memory rows (before, between or after the messages) appear too. They never change a message's
    /// sequence number, and paging still counts messages only. With every layer off the rows are exactly
    /// what they were without memories.</para>
    /// </remarks>
    public sealed class ConversationTimeline
    {
        /// <summary>How many messages one page holds.</summary>
        public const int PageSize = 150;

        private readonly ArchiveReader _reader;
        private readonly long _localParticipantID;
        private readonly IReadOnlyDictionary<long, string> _names;
        private readonly LowQualityIndex _lowQuality;
        private readonly Stars? _stars;
        private readonly MemoryLayout _memories;

        private int? _firstSequenceNumber;
        private int? _lastSequenceNumber;

        /// <summary>A timeline over <paramref name="reader"/>, with "me" as <paramref name="localParticipantID"/>.</summary>
        /// <param name="reader">The open archive. The timeline reads through it; one read at a time.</param>
        /// <param name="localParticipantID">Whose messages go on the right.</param>
        /// <param name="lowQuality">
        /// The conversation's low-quality copy links. When none is given it is built here, which reads the
        /// whole conversation once; pass the one an <see cref="ArchiveSession"/> holds to avoid that.
        /// </param>
        /// <param name="stars">The archive's stars, so rows are built with their star flags; none when not given.</param>
        /// <param name="memories">The visible memory rows (CH-42); none when not given.</param>
        public ConversationTimeline(
            ArchiveReader reader, long localParticipantID, LowQualityIndex? lowQuality = null, Stars? stars = null, MemoryLayout? memories = null)
        {
            ArgumentNullException.ThrowIfNull(reader);

            _reader = reader;
            _localParticipantID = localParticipantID;
            _names = reader.ReadParticipants().ToDictionary(p => p.ParticipantID, p => p.DisplayName);
            _lowQuality = lowQuality ?? LowQualityIndex.Build(reader);
            _stars = stars;
            _memories = memories ?? MemoryLayout.Empty;
        }

        /// <summary>Whose messages go on the right.</summary>
        public long LocalParticipantID => _localParticipantID;

        /// <summary>The rows the list shows.</summary>
        public ConversationRows Rows { get; } = new();

        /// <summary>Whether the archive holds messages before the loaded ones.</summary>
        public bool HasEarlier { get; private set; }

        /// <summary>Whether the archive holds messages after the loaded ones.</summary>
        public bool HasLater { get; private set; }

        /// <summary>Loads the newest page. Returns the index of the last row, or -1 when there is none.</summary>
        public int OpenAtLatest()
        {
            Reset();
            Append(_reader.ReadPageBefore(int.MaxValue, PageSize), first: true);

            return Rows.Count - 1;
        }

        /// <summary>
        /// Loads the page before <paramref name="sequenceNumber"/> and the page from it. Returns the
        /// index of the row of the first shown message at or after it, or -1 when there is none.
        /// </summary>
        /// <remarks>
        /// The message at <paramref name="sequenceNumber"/> may be hidden (a bubble of hidden low-quality
        /// copies only); the next shown message stands in for it.
        /// </remarks>
        public int OpenAt(int sequenceNumber)
        {
            Reset();

            var before = _reader.ReadPageBefore(sequenceNumber, PageSize);
            var from = _reader.ReadPageFrom(sequenceNumber, PageSize);

            Append(before, first: true);
            Append(from, first: before.Messages.Count == 0);

            return from.FirstSequenceNumber is { } target ? IndexOfMessageAtOrAfter(target) : -1;
        }

        /// <summary>
        /// Loads the conversation around the first message on or after <paramref name="day"/> (the
        /// date picker's jump). Returns that message's row, or -1 when no message is that late, in which
        /// case nothing is loaded.
        /// </summary>
        /// <remarks>
        /// With memory rows, a day that has only memory rows can be picked too (the calendar includes it):
        /// the conversation opens around the first row on or after the day, message or memory, and that
        /// row's index is returned.
        /// </remarks>
        public int OpenAtDay(DateOnly day)
        {
            var sequence = _reader.FindFirstSequenceNumberOnOrAfter(day);

            if (_memories.IsEmpty)
            {
                return sequence is { } found ? OpenAt(found) : -1;
            }

            var memory = _memories.Placements
                .Where(p => DateOnly.FromDateTime(p.TimeLocal) >= day)
                .Select(p => (int?)p.After)
                .Min();

            int target;

            if (memory is { } after && (sequence is null || after < sequence))
            {
                target = Math.Max(1, after);
            }
            else if (sequence is { } found)
            {
                target = found;
            }
            else
            {
                return -1;
            }

            Reset();

            var before = _reader.ReadPageBefore(target, PageSize);
            var from = _reader.ReadPageFrom(target, PageSize);

            Append(before, first: true);
            Append(from, first: before.Messages.Count == 0);

            for (var i = 0; i < Rows.Count; i++)
            {
                if (Rows[i] is not DateSeparatorRow && Rows[i].Day >= day)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Loads the conversation around a memory row (a search hit or a link) and returns its row, or -1
        /// when the row is not visible (its layer or filter is off), in which case nothing is loaded.
        /// </summary>
        public int OpenAtMemory(MemoryKey key)
        {
            if (_memories.Find(key) is not { } placement)
            {
                return -1;
            }

            OpenAt(Math.Max(1, placement.After));

            return IndexOfMemory(key);
        }

        /// <summary>The index of a memory row, or -1.</summary>
        public int IndexOfMemory(MemoryKey key)
        {
            for (var i = 0; i < Rows.Count; i++)
            {
                if (Rows[i] is MemoryRow memory && memory.Key == key)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>The page before the loaded rows, or <see langword="null"/> when there is none.</summary>
        public ArchivePage? ReadEarlier() =>
            HasEarlier && _firstSequenceNumber is { } first ? _reader.ReadPageBefore(first, PageSize) : null;

        /// <summary>The page after the loaded rows, or <see langword="null"/> when there is none.</summary>
        public ArchivePage? ReadLater() =>
            HasLater && _lastSequenceNumber is { } last && last < int.MaxValue
                ? _reader.ReadPageFrom(last + 1, PageSize)
                : null;

        /// <summary>
        /// Reads the page before the loaded rows and builds its rows, without changing the timeline, so
        /// all of it can run on a worker thread; <see langword="null"/> when there is none.
        /// </summary>
        public PreparedPage? PrepareEarlier() => ReadEarlier() is { } page ? Prepare(page) : null;

        /// <summary>
        /// Reads the page after the loaded rows and builds its rows, without changing the timeline, so
        /// all of it can run on a worker thread; <see langword="null"/> when there is none.
        /// </summary>
        public PreparedPage? PrepareLater() => ReadLater() is { } page ? Prepare(page) : null;

        /// <summary>
        /// Builds the rows of <paramref name="page"/> (including the check for each media file) without
        /// changing the timeline. Safe on a worker thread while the list shows <see cref="Rows"/>.
        /// </summary>
        public PreparedPage Prepare(ArchivePage page)
        {
            ArgumentNullException.ThrowIfNull(page);

            if (_memories.IsEmpty || page.Messages.Count == 0)
            {
                return new PreparedPage(page, BuildRows(page.Messages, page.Media, previousDay: null));
            }

            // The rows after this page's messages, and before the first message for the page that starts the chat.
            var first = page.FirstSequenceNumber!.Value;
            var placements = _memories.Between(first == 1 ? 0 : first, page.LastSequenceNumber!.Value).ToList();

            return new PreparedPage(page, BuildRows(page.Messages, page.Media, previousDay: null, placements));
        }

        /// <summary>
        /// Adds an earlier page above the loaded rows.
        /// </summary>
        /// <returns>
        /// How far every message row that was loaded before moves down: the rows added, less the
        /// separator taken out when the page ends on the first loaded day.
        /// </returns>
        public int Prepend(ArchivePage page) => Prepend(Prepare(page));

        /// <summary>
        /// Adds an earlier page whose rows were built by <see cref="Prepare"/>, so the thread the list is
        /// bound on only inserts them.
        /// </summary>
        /// <returns>
        /// How far every message row that was loaded before moves down: the rows added, less the
        /// separator taken out when the page ends on the first loaded day.
        /// </returns>
        public int Prepend(PreparedPage prepared)
        {
            ArgumentNullException.ThrowIfNull(prepared);

            var page = prepared.Page;

            if (page.Messages.Count == 0)
            {
                HasEarlier = false;
                return 0;
            }

            if (_firstSequenceNumber is { } first && page.LastSequenceNumber >= first)
            {
                throw new ArgumentException("The page is not before the loaded rows.", nameof(page));
            }

            var rows = prepared.Rows;

            HasEarlier = page.HasEarlier;
            _firstSequenceNumber = page.FirstSequenceNumber;
            _lastSequenceNumber ??= page.LastSequenceNumber;

            // Rows[0] is the separator of the first loaded day. When the page ends on that day, the
            // page brings that day's separator itself, at the start of its last run: the old one goes.
            var removed = 0;

            if (Rows.Count > 0 && Rows[0] is DateSeparatorRow existing && rows[^1].Day == existing.Day)
            {
                Rows.RemoveAt(0);
                removed = 1;
            }

            Rows.InsertRange(0, rows);

            return rows.Count - removed;
        }

        /// <summary>Adds a later page below the loaded rows.</summary>
        public void Append(ArchivePage page) => Append(Prepare(page), first: false);

        /// <summary>
        /// Adds a later page whose rows were built by <see cref="Prepare"/>, so the thread the list is
        /// bound on only inserts them.
        /// </summary>
        public void Append(PreparedPage prepared) => Append(prepared, first: false);

        /// <summary>The index of the row of the message with this sequence number, or -1.</summary>
        public int IndexOfMessage(int sequenceNumber)
        {
            for (var i = 0; i < Rows.Count; i++)
            {
                if (Rows[i] is MessageRow message && message.SequenceNumber == sequenceNumber)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>The index of the row of the first shown message at or after <paramref name="sequenceNumber"/>, or -1.</summary>
        public int IndexOfMessageAtOrAfter(int sequenceNumber)
        {
            for (var i = 0; i < Rows.Count; i++)
            {
                if (Rows[i] is MessageRow message && message.SequenceNumber >= sequenceNumber)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// The rows for a run of messages: a separator before each message whose day differs from the
        /// one before it (<paramref name="previousDay"/> for the first), then the message if it is shown.
        /// </summary>
        /// <remarks>
        /// <para>Chat order decides, never time order: nothing here assumes the stored times rise with the
        /// sequence numbers, the same rule as the desktop's page.</para>
        /// <para>A message that is not shown (<see cref="MessageRow.IsShown"/>) gets no row, but still
        /// counts for its day, so its day keeps its separator, as the desktop does it.</para>
        /// </remarks>
        public List<ConversationRow> BuildRows(
            IReadOnlyList<ArchiveMessage> messages,
            IReadOnlyDictionary<string, ArchiveMedia> media,
            DateOnly? previousDay) => BuildRows(messages, media, previousDay, []);

        /// <summary>
        /// <see cref="BuildRows(IReadOnlyList{ArchiveMessage}, IReadOnlyDictionary{string, ArchiveMedia}, DateOnly?)"/>
        /// with memory rows: each placement goes after the message it follows (placements before the first
        /// message, <see cref="MemoryPlacement.After"/> below it, go first), with a separator when its day
        /// differs from the row before.
        /// </summary>
        private List<ConversationRow> BuildRows(
            IReadOnlyList<ArchiveMessage> messages,
            IReadOnlyDictionary<string, ArchiveMedia> media,
            DateOnly? previousDay,
            IReadOnlyList<MemoryPlacement> placements)
        {
            ArgumentNullException.ThrowIfNull(messages);
            ArgumentNullException.ThrowIfNull(media);

            var rows = new List<ConversationRow>(messages.Count + placements.Count + 4);
            var day = previousDay;
            var next = 0;

            void Add(ConversationRow row, bool shown)
            {
                if (day != row.Day)
                {
                    rows.Add(new DateSeparatorRow(row.Day));
                    day = row.Day;
                }

                if (shown)
                {
                    rows.Add(row);
                }
            }

            void AddMemoriesUpTo(int sequenceNumber)
            {
                while (next < placements.Count && placements[next].After <= sequenceNumber)
                {
                    Add(placements[next].CreateRow(), shown: true);
                    next++;
                }
            }

            if (messages.Count > 0)
            {
                AddMemoriesUpTo(messages[0].SequenceNumber - 1);
            }

            foreach (var message in messages)
            {
                var row = BuildMessage(message, media);
                Add(row, row.IsShown);
                AddMemoriesUpTo(message.SequenceNumber);
            }

            return rows;
        }

        /// <summary>
        /// Sets the star flags of every loaded row from the stars (after a star is toggled), so the
        /// bubbles on screen follow at once. Call it on the thread the list is bound on.
        /// </summary>
        public void RefreshStars()
        {
            if (_stars is null)
            {
                return;
            }

            foreach (var row in Rows)
            {
                if (row is MessageRow message)
                {
                    _stars.Apply(message);
                }
            }
        }

        /// <summary>
        /// One message as its bubble would show it, shown or not, with its star flags: for the gallery and
        /// the starred list, which use the chat's own card rules.
        /// </summary>
        public MessageRow BuildMessageRow(ArchiveMessage message, IReadOnlyDictionary<string, ArchiveMedia> media)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(media);

            return BuildMessage(message, media);
        }

        private void Append(ArchivePage page, bool first) => Append(Prepare(page), first);

        private void Append(PreparedPage prepared, bool first)
        {
            ArgumentNullException.ThrowIfNull(prepared);

            var page = prepared.Page;

            // Checked before anything changes, so a refused page leaves the timeline as it was.
            if (page.Messages.Count > 0 && _lastSequenceNumber is { } last && page.FirstSequenceNumber <= last)
            {
                throw new ArgumentException("The page is not after the loaded rows.", nameof(page));
            }

            if (first)
            {
                HasEarlier = page.HasEarlier;
            }

            HasLater = page.HasLater;

            if (page.Messages.Count == 0)
            {
                return;
            }

            // The rows were built without knowing the last loaded day: when the page starts on that
            // day, its leading separator is one the loaded rows already have.
            var rows = prepared.Rows;

            if (Rows.Count > 0 && rows.Count > 0 && rows[0] is DateSeparatorRow separator && separator.Day == Rows[^1].Day)
            {
                rows = rows.Skip(1).ToList();
            }

            Rows.InsertRange(Rows.Count, rows);

            _firstSequenceNumber ??= page.FirstSequenceNumber;
            _lastSequenceNumber = page.LastSequenceNumber;
        }

        private MessageRow BuildMessage(ArchiveMessage message, IReadOnlyDictionary<string, ArchiveMedia> media)
        {
            var sender = message.SenderParticipantID is { } id
                ? _names.TryGetValue(id, out var name) ? name : "Unknown sender"
                : "System";

            // For a photo's Info: the participant's name only, never the bubble's stand-in words.
            var senderName = message.SenderParticipantID is { } participant && _names.TryGetValue(participant, out var known) ? known : null;

            var attachments = message.Attachments
                .Select(attachment =>
                {
                    var path = _reader.MediaFullPath(attachment);

                    return new AttachmentCard(
                        attachment,
                        attachment.MediaSha256 is { } sha && media.TryGetValue(sha, out var payload) ? payload : null,
                        path,
                        path is not null && File.Exists(path),
                        _lowQuality,
                        message.DateTimeLocal,
                        senderName,
                        message.SequenceNumber);
                })
                .ToList();

            var row = new MessageRow(message, sender, message.SenderParticipantID == _localParticipantID, attachments);
            _stars?.Apply(row);

            return row;
        }

        private void Reset()
        {
            Rows.Clear();
            _firstSequenceNumber = null;
            _lastSequenceNumber = null;
            HasEarlier = false;
            HasLater = false;
        }
    }

    /// <summary>
    /// A page read from the archive with its rows already built, ready to be inserted on the thread the
    /// list is bound on.
    /// </summary>
    public sealed class PreparedPage
    {
        internal PreparedPage(ArchivePage page, IReadOnlyList<ConversationRow> rows)
        {
            Page = page;
            Rows = rows;
        }

        /// <summary>The page as read.</summary>
        public ArchivePage Page { get; }

        /// <summary>Its rows, built as if no day came before it.</summary>
        public IReadOnlyList<ConversationRow> Rows { get; }
    }
}
