using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Chatcheology.Mobile.Conversation
{
    /// <summary>
    /// The rows a conversation list binds to, with one change notification per inserted page.
    /// </summary>
    /// <remarks>
    /// <see cref="ObservableCollection{T}"/> raises one notification per item, so adding a page of
    /// 150 messages above the reader would make the list re-anchor 150 times. A single
    /// multi-item <see cref="NotifyCollectionChangedAction.Add"/> lets the list insert the whole range
    /// at once and keep the visible row in place.
    /// </remarks>
    public sealed class ConversationRows : ObservableCollection<ConversationRow>
    {
        /// <summary>Inserts <paramref name="rows"/> at <paramref name="index"/> with one notification.</summary>
        public void InsertRange(int index, IReadOnlyList<ConversationRow> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(index, Count);

            if (rows.Count == 0)
            {
                return;
            }

            CheckReentrancy();

            for (var i = 0; i < rows.Count; i++)
            {
                Items.Insert(index + i, rows[i]);
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add, rows.ToList(), index));
        }
    }
}
