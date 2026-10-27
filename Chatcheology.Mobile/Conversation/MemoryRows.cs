using System.ComponentModel;
using Chatcheology.Mobile.Memories;

namespace Chatcheology.Mobile.Conversation
{
    /// <summary>
    /// Which memory row: an entry, and for an imported entry's period card the period. A personal card
    /// and a "different times" line have no period.
    /// </summary>
    /// <param name="EntryID">The entry.</param>
    /// <param name="PeriodIndex">The period of a period card; otherwise <see langword="null"/>.</param>
    public readonly record struct MemoryKey(long EntryID, int? PeriodIndex);

    /// <summary>
    /// One memory row of the conversation list (CH-42): never a message. It is centred and drawn
    /// differently from bubbles and system lines, has no sequence number, and offers no Share or Star.
    /// </summary>
    /// <remarks>
    /// Placed at its local time among the messages: after every message up to that second, before the
    /// first later one. It never changes a message's sequence number.
    /// </remarks>
    public abstract class MemoryRow : ConversationRow, INotifyPropertyChanged
    {
        private bool _isHighlighted;

        private protected MemoryRow(MemoryKey key, DateTime timeLocal, long collectionID)
            : base(DateOnly.FromDateTime(timeLocal))
        {
            Key = key;
            TimeLocal = timeLocal;
            CollectionID = collectionID;
        }

        /// <summary>Which entry (and period) the row shows.</summary>
        public MemoryKey Key { get; }

        /// <summary>The local time the row is placed at.</summary>
        public DateTime TimeLocal { get; }

        /// <summary>The collection (layer) it belongs to.</summary>
        public long CollectionID { get; }

        /// <summary>Raised when <see cref="IsHighlighted"/> changes.</summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Whether the row is briefly highlighted (opened from search), like a message.</summary>
        public bool IsHighlighted
        {
            get => _isHighlighted;
            set
            {
                if (_isHighlighted != value)
                {
                    _isHighlighted = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
                }
            }
        }
    }

    /// <summary>
    /// A period card of an imported collection (PSN): one period in which both accounts earned trophies,
    /// labelled strong, supporting or same period.
    /// </summary>
    public sealed class PeriodCardRow : MemoryRow
    {
        internal PeriodCardRow(MemoryEntry entry, int periodIndex)
            : base(new MemoryKey(entry.Entry.EntryID, periodIndex), entry.Period(periodIndex).StartLocal, entry.Collection.CollectionID)
        {
            var period = entry.Period(periodIndex);

            Title = entry.Entry.Title;
            DateText = DateSeparatorRow.DayText(DateOnly.FromDateTime(period.StartLocal));
            ImagePath = entry.ImagePath;
            ParticipantLines = entry.ParticipantLines(period);
            LabelText = MemoryWording.PeriodLabelLine(period.Label);
            SourceText = entry.SourceText;
        }

        /// <summary>The game's title.</summary>
        public string Title { get; }

        /// <summary>The period's day.</summary>
        public string DateText { get; }

        /// <summary>The game's image when its file is there; otherwise <see langword="null"/>.</summary>
        public string? ImagePath { get; }

        /// <summary>Whether there is an image to draw.</summary>
        public bool HasImage => ImagePath is not null;

        /// <summary>Each participant's trophy count and first-to-last time in the period.</summary>
        public IReadOnlyList<string> ParticipantLines { get; }

        /// <summary>"This period: strong / supporting / same period".</summary>
        public string LabelText { get; }

        /// <summary>The source line ("PSN trophies"), or empty.</summary>
        public string SourceText { get; }

        /// <summary>Whether there is a source line.</summary>
        public bool HasSource => SourceText.Length > 0;
    }

    /// <summary>
    /// The muted line of a game-day record with no shared period: "Both earned trophies in … today, at
    /// different times", placed at its first trophy. Not a claim of simultaneous play.
    /// </summary>
    public sealed class DifferentTimesRow : MemoryRow
    {
        internal DifferentTimesRow(MemoryEntry entry)
            : base(new MemoryKey(entry.Entry.EntryID, null), entry.FirstItemLocal, entry.Collection.CollectionID)
        {
            Text = MemoryWording.DifferentTimesLine(entry.Entry.Title);
        }

        /// <summary>The line.</summary>
        public string Text { get; }
    }

    /// <summary>
    /// A memory card at its start (a personal memory, or an imported entry without periods). A range shows
    /// one card at its start.
    /// </summary>
    public sealed class EntryCardRow : MemoryRow
    {
        internal EntryCardRow(MemoryEntry entry)
            : base(new MemoryKey(entry.Entry.EntryID, null), entry.Entry.StartLocal, entry.Collection.CollectionID)
        {
            Title = entry.Entry.Title;
            DateText = MemoryWording.EntryDate(entry.Entry.DatePrecision, entry.Entry.StartLocal, entry.Entry.EndLocal);
            Summary = entry.Entry.Summary ?? string.Empty;
            ImagePath = entry.ImagePath;
            SourceText = entry.SourceText;
            CollectionName = entry.Collection.Name;
        }

        /// <summary>The title.</summary>
        public string Title { get; }

        /// <summary>The date, or the range "… to …".</summary>
        public string DateText { get; }

        /// <summary>The description, or empty.</summary>
        public string Summary { get; }

        /// <summary>Whether there is a description.</summary>
        public bool HasSummary => Summary.Length > 0;

        /// <summary>The image when its file is there; otherwise <see langword="null"/>.</summary>
        public string? ImagePath { get; }

        /// <summary>Whether there is an image to draw.</summary>
        public bool HasImage => ImagePath is not null;

        /// <summary>The source line, or empty.</summary>
        public string SourceText { get; }

        /// <summary>Whether there is a source line.</summary>
        public bool HasSource => SourceText.Length > 0;

        /// <summary>The collection's name, such as "Holidays".</summary>
        public string CollectionName { get; }
    }
}
