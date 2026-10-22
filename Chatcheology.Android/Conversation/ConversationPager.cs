using AndroidX.RecyclerView.Widget;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Android.Conversation
{
    /// <summary>
    /// Loads the next page in either direction when the list nears an end, one page at a time.
    /// </summary>
    /// <remarks>
    /// The page is read on a worker thread and applied on the UI thread as one range insert. The CH-26
    /// trial found MAUI's CollectionView keeps the message on screen exactly in place through such an
    /// insert, so nothing here moves the list.
    /// <para>Every read of the archive goes through here, one at a time, so <see cref="StopAsync"/> can
    /// wait for the one under way before the page closes the reader.</para>
    /// <para>After each page it asks <see cref="PagingRule"/> again with the rows on screen (moved by
    /// what a page above added), so a list shorter than the screen fills both ways without waiting for a
    /// scroll that may never come.</para>
    /// </remarks>
    internal sealed class ConversationPager
    {
        private readonly ConversationTimeline _timeline;
        private readonly string _listName;
        private Task _running = Task.CompletedTask;
        private bool _loading;
        private bool _stopped;
        private int _first = -1;
        private int _last = -1;

        internal ConversationPager(ConversationTimeline timeline, string listName)
        {
            _timeline = timeline;
            _listName = listName;
        }

        /// <summary>The list's Android view, once its handler is connected.</summary>
        internal RecyclerView? List { get; set; }

        /// <summary>Whether loading may start; off until the list has been placed at its first position.</summary>
        internal bool Ready { get; set; }

        /// <summary>Opens the timeline off the UI thread, before the list is bound to its rows.</summary>
        internal async Task<T> OpenAsync<T>(Func<T> open)
        {
            _loading = true;

            try
            {
                var read = Task.Run(open);
                _running = read;

                return await read;
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>
        /// Stops any further loading and waits for a read under way to finish, so the reader can be
        /// closed safely afterwards.
        /// </summary>
        internal async Task StopAsync()
        {
            _stopped = true;
            Ready = false;

            try
            {
                await _running;
            }
            catch (Exception exception) when (exception is ArchiveFormatException or SqliteException)
            {
                // The read failed; there is nothing left to wait for.
            }
        }

        /// <summary>Records the rows on screen and loads whatever the paging rule wants, one page at a time.</summary>
        internal void OnVisibleRange(int first, int last)
        {
            _first = first;
            _last = last;

            _ = PumpAsync();
        }

        /// <summary>Loads the page before the loaded rows now, unless a read is under way (the trial's controlled step).</summary>
        internal async Task LoadEarlierAsync()
        {
            if (!Ready || _stopped || _loading || !_timeline.HasEarlier)
            {
                return;
            }

            _loading = true;

            try
            {
                await LoadEarlierCoreAsync();
            }
            finally
            {
                _loading = false;
            }
        }

        private async Task PumpAsync()
        {
            if (!Ready || _stopped || _loading)
            {
                return;
            }

            _loading = true;

            try
            {
                while (Ready && !_stopped)
                {
                    var want = PagingRule.Wanted(_first, _last, _timeline.Rows.Count, _timeline.HasEarlier, _timeline.HasLater);

                    if (want.Earlier)
                    {
                        var added = await LoadEarlierCoreAsync();

                        if (added is not { } moved)
                        {
                            break;
                        }

                        // Everything on screen moved down by the rows added above it.
                        _first += moved;
                        _last += moved;
                    }
                    else if (!want.Later || !await LoadLaterCoreAsync())
                    {
                        break;
                    }
                }
            }
            catch (Exception exception) when (exception is ArchiveFormatException or SqliteException)
            {
                // The archive could not be read further; what is loaded stays.
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>Reads and adds the page above; how far the loaded rows moved, or null when nothing was added.</summary>
        private async Task<int?> LoadEarlierCoreAsync()
        {
            var read = Task.Run(_timeline.ReadEarlier);
            _running = read;
            var page = await read;

            if (_stopped || page is null)
            {
                return null;
            }

#if DEBUG
            var join = _timeline.Rows.Count > 0 && page.Messages.Count > 0 && DateOnly.FromDateTime(page.Messages[^1].DateTimeLocal) == _timeline.Rows[0].Day
                ? "inside-day" : "day-start";
            var top = ScrollAnchor.TopRowKind(List, _timeline.Rows);
            var anchor = ScrollAnchor.Capture(List, _timeline.Rows);
            var added = _timeline.Prepend(page);
            ScrollTrialLog.AfterPrepend($"{_listName} join={join} topRow={top}", List, _timeline.Rows, anchor, added);

            return added;
#else
            return _timeline.Prepend(page);
#endif
        }

        /// <summary>Reads and adds the page below; false when there was none.</summary>
        private async Task<bool> LoadLaterCoreAsync()
        {
            var read = Task.Run(_timeline.ReadLater);
            _running = read;
            var page = await read;

            if (_stopped || page is null)
            {
                return false;
            }

            _timeline.Append(page);

            return true;
        }
    }

#if DEBUG
    /// <summary>Debug builds only: the first message row on screen and its top edge, in the list's pixels.</summary>
    internal sealed record ScrollAnchor(int Index, int SequenceNumber, int Top)
    {
        /// <summary>Whether the topmost row on screen is a separator or a message.</summary>
        internal static string TopRowKind(RecyclerView? list, IList<ConversationRow> rows)
        {
            var kind = "none";
            var best = int.MaxValue;

            for (var i = 0; list is not null && i < list.ChildCount; i++)
            {
                var child = list.GetChildAt(i);
                var index = child is null ? -1 : list.GetChildAdapterPosition(child);

                if (child is null || child.Bottom <= list.PaddingTop || index < 0 || index >= rows.Count || child.Top >= best)
                {
                    continue;
                }

                best = child.Top;
                kind = rows[index] is DateSeparatorRow ? $"separator@{index}" : "message";
            }

            return kind;
        }

        /// <summary>The topmost message row with any part on screen, or <see langword="null"/>.</summary>
        internal static ScrollAnchor? Capture(RecyclerView? list, IList<ConversationRow> rows)
        {
            if (list is null)
            {
                return null;
            }

            ScrollAnchor? anchor = null;

            for (var i = 0; i < list.ChildCount; i++)
            {
                var child = list.GetChildAt(i);

                if (child is null || child.Bottom <= list.PaddingTop)
                {
                    continue;
                }

                var index = list.GetChildAdapterPosition(child);

                if (index < 0 || index >= rows.Count || rows[index] is not MessageRow message)
                {
                    continue;
                }

                if (anchor is null || child.Top < anchor.Top)
                {
                    anchor = new ScrollAnchor(index, message.SequenceNumber, child.Top);
                }
            }

            return anchor;
        }
    }
#endif
}
