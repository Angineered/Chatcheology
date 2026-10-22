using Chatcheology.Archive;

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
    /// </remarks>
    public sealed class ConversationTimeline
    {
        /// <summary>How many messages one page holds.</summary>
        public const int PageSize = 150;

        private readonly ArchiveReader _reader;
        private readonly long _localParticipantID;
        private readonly IReadOnlyDictionary<long, string> _names;

        private int? _firstSequenceNumber;
        private int? _lastSequenceNumber;

        /// <summary>A timeline over <paramref name="reader"/>, with "me" as <paramref name="localParticipantID"/>.</summary>
        public ConversationTimeline(ArchiveReader reader, long localParticipantID)
        {
            ArgumentNullException.ThrowIfNull(reader);

            _reader = reader;
            _localParticipantID = localParticipantID;
            _names = reader.ReadParticipants().ToDictionary(p => p.ParticipantID, p => p.DisplayName);
        }

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
        /// index of the row of the first message at or after it, or -1 when there is none.
        /// </summary>
        public int OpenAt(int sequenceNumber)
        {
            Reset();

            var before = _reader.ReadPageBefore(sequenceNumber, PageSize);
            var from = _reader.ReadPageFrom(sequenceNumber, PageSize);

            Append(before, first: true);
            Append(from, first: before.Messages.Count == 0);

            return from.FirstSequenceNumber is { } target ? IndexOfMessage(target) : -1;
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
        /// Adds an earlier page above the loaded rows.
        /// </summary>
        /// <returns>
        /// How far every message row that was loaded before moves down: the rows added, less the
        /// separator taken out when the page ends on the first loaded day.
        /// </returns>
        public int Prepend(ArchivePage page)
        {
            ArgumentNullException.ThrowIfNull(page);

            if (page.Messages.Count == 0)
            {
                HasEarlier = false;
                return 0;
            }

            if (_firstSequenceNumber is { } first && page.LastSequenceNumber >= first)
            {
                throw new ArgumentException("The page is not before the loaded rows.", nameof(page));
            }

            var rows = BuildRows(page.Messages, page.Media, previousDay: null);

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
        public void Append(ArchivePage page) => Append(page, first: false);

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

        /// <summary>
        /// The rows for a run of messages: a separator before each message whose day differs from the
        /// one before it (<paramref name="previousDay"/> for the first), then the message.
        /// </summary>
        /// <remarks>
        /// Chat order decides, never time order: nothing here assumes the stored times rise with the
        /// sequence numbers, the same rule as the desktop's page.
        /// </remarks>
        public List<ConversationRow> BuildRows(
            IReadOnlyList<ArchiveMessage> messages,
            IReadOnlyDictionary<string, ArchiveMedia> media,
            DateOnly? previousDay)
        {
            ArgumentNullException.ThrowIfNull(messages);
            ArgumentNullException.ThrowIfNull(media);

            var rows = new List<ConversationRow>(messages.Count + 4);
            var day = previousDay;

            foreach (var message in messages)
            {
                var row = BuildMessage(message, media);

                if (day != row.Day)
                {
                    rows.Add(new DateSeparatorRow(row.Day));
                    day = row.Day;
                }

                rows.Add(row);
            }

            return rows;
        }

        private void Append(ArchivePage page, bool first)
        {
            ArgumentNullException.ThrowIfNull(page);

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

            DateOnly? previousDay = Rows.Count > 0 ? Rows[^1].Day : null;

            Rows.InsertRange(Rows.Count, BuildRows(page.Messages, page.Media, previousDay));

            _firstSequenceNumber ??= page.FirstSequenceNumber;
            _lastSequenceNumber = page.LastSequenceNumber;
        }

        private MessageRow BuildMessage(ArchiveMessage message, IReadOnlyDictionary<string, ArchiveMedia> media)
        {
            var sender = message.SenderParticipantID is { } id
                ? _names.TryGetValue(id, out var name) ? name : "Unknown sender"
                : "System";

            var attachments = message.Attachments
                .Select(attachment => new AttachmentCard(
                    attachment,
                    attachment.MediaSha256 is { } sha && media.TryGetValue(sha, out var payload) ? payload : null,
                    _reader.MediaFullPath(attachment)))
                .ToList();

            return new MessageRow(message, sender, message.SenderParticipantID == _localParticipantID, attachments);
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
}
