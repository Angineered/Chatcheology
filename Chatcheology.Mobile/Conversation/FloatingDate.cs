namespace Chatcheology.Mobile.Conversation
{
    /// <summary>
    /// The floating date over the top of the conversation (CH-65): the day of the topmost visible row, shown
    /// only while the reader scrolls and hidden <see cref="HideDelay"/> after the list comes to rest.
    /// </summary>
    /// <remarks>
    /// <para>The wording is the day separator's (<see cref="DateSeparatorRow.DayText"/>); no second date
    /// format. The text is built only when the top row's day changes, so a scroll event costs an index check
    /// and a date compare.</para>
    /// <para>Only a scroll the reader starts shows it (the screen reports a drag); a programmatic placement
    /// (open, date jump, search hit, a page added above) never does. Each start or stop takes a new
    /// generation, so a hide scheduled by an earlier stop is ignored once scrolling starts again.</para>
    /// </remarks>
    public sealed class FloatingDate
    {
        /// <summary>How long the date stays after the list comes to rest.</summary>
        public static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(1500);

        private int _generation;

        /// <summary>Whether the date should be on screen now.</summary>
        public bool IsShown { get; private set; }

        /// <summary>The day shown, or none before the first row was seen.</summary>
        public DateOnly? Day { get; private set; }

        /// <summary>The day as the separators show it; empty before the first row was seen.</summary>
        public string Text { get; private set; } = string.Empty;

        /// <summary>The day of the row at <paramref name="firstVisible"/>, or none when that index is not a row.</summary>
        public static DateOnly? DayAt(IReadOnlyList<ConversationRow> rows, int firstVisible)
        {
            ArgumentNullException.ThrowIfNull(rows);

            return firstVisible >= 0 && firstVisible < rows.Count ? rows[firstVisible].Day : null;
        }

        /// <summary>
        /// The topmost visible row is now <paramref name="firstVisible"/>. True when the shown day changed (the
        /// screen updates its text); an index outside the rows keeps the day as it was.
        /// </summary>
        public bool TopRowChanged(IReadOnlyList<ConversationRow> rows, int firstVisible)
        {
            if (DayAt(rows, firstVisible) is not { } day || day == Day)
            {
                return false;
            }

            Day = day;
            Text = DateSeparatorRow.DayText(day);
            return true;
        }

        /// <summary>The reader started scrolling: the date shows, and a pending hide no longer applies.</summary>
        public void ScrollStarted()
        {
            _generation++;
            IsShown = true;
        }

        /// <summary>
        /// The list came to rest. Returns the ticket to pass to <see cref="HideIfStill"/> after
        /// <see cref="HideDelay"/>, or none when the date is not shown (nothing to hide).
        /// </summary>
        public int? ScrollStopped()
        {
            if (!IsShown)
            {
                return null;
            }

            return ++_generation;
        }

        /// <summary>
        /// The delay after a stop has passed. True when the date should now fade out: nothing has started or
        /// stopped since <paramref name="ticket"/> was given.
        /// </summary>
        public bool HideIfStill(int ticket)
        {
            if (ticket != _generation || !IsShown)
            {
                return false;
            }

            IsShown = false;
            return true;
        }

        /// <summary>Hides at once (new rows bound, the page closing); any pending hide no longer applies.</summary>
        public void Reset()
        {
            _generation++;
            IsShown = false;
        }
    }
}
