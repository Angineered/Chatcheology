namespace Chatcheology.Mobile.OwnMemories
{
    /// <summary>
    /// The photos an open add or edit form holds (CH-54 step 1a gap 6): while leased, a photo file is never deleted by an
    /// update, a delete, Delete all or a sweep, so the form can still save it. Dispose it when the form closes.
    /// </summary>
    /// <remarks>Used from the UI thread; the store's write lock guards the shared counts.</remarks>
    public sealed class OwnPhotoLease : IDisposable
    {
        private readonly OwnMemoryStore _store;
        private readonly HashSet<string> _held = new(StringComparer.Ordinal);
        private bool _disposed;

        internal OwnPhotoLease(OwnMemoryStore store)
        {
            _store = store;
        }

        /// <summary>The photo files held.</summary>
        public IReadOnlyCollection<string> Held => _held;

        /// <summary>
        /// Holds <paramref name="fileName"/>. False when it is not a stored photo on disk any more (it was deleted
        /// before it could be held: add it again), or the lease is disposed.
        /// </summary>
        public bool TryHold(string fileName)
        {
            if (_disposed)
            {
                return false;
            }

            if (_held.Contains(fileName))
            {
                return true;
            }

            if (!_store.TryLease(fileName))
            {
                return false;
            }

            _held.Add(fileName);
            return true;
        }

        /// <summary>Gives every held photo back.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var name in _held)
            {
                _store.Release(name);
            }

            _held.Clear();
        }
    }
}
