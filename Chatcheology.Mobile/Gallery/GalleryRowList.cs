using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Chatcheology.Mobile.Gallery
{
    /// <summary>
    /// The gallery's rows as its list shows them, with older batches added below in one change each
    /// (CH-66 scroll follow-up).
    /// </summary>
    /// <remarks>
    /// Added one row at a time, a batch of 500 photos raised about 170 changes in a row, each one an insert the
    /// list had to process while the reader was scrolling; <see cref="AddRange"/> raises one Add with every new
    /// row and the index of the first, which the list takes as a single range insert below what is shown.
    /// </remarks>
    public sealed class GalleryRowList : ObservableCollection<GalleryRow>
    {
        /// <summary>The rows given, in order.</summary>
        public GalleryRowList(IEnumerable<GalleryRow> rows)
            : base(rows)
        {
        }

        /// <summary>Adds <paramref name="rows"/> at the end, in order, as one change; nothing when there are none.</summary>
        public void AddRange(IEnumerable<GalleryRow> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);

            var added = rows.ToList();

            if (added.Count == 0)
            {
                return;
            }

            CheckReentrancy();

            var start = Count;

            foreach (var row in added)
            {
                Items.Add(row);
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, start));
        }
    }
}
