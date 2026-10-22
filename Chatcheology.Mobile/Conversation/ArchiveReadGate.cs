namespace Chatcheology.Mobile.Conversation
{
    /// <summary>The result of a read run through an <see cref="ArchiveReadGate"/>.</summary>
    /// <typeparam name="T">What the read returns.</typeparam>
    /// <param name="Ran">False when the gate was closed before the read could start: nothing was read.</param>
    /// <param name="Value">What the read returned, when it ran.</param>
    public readonly record struct GatedRead<T>(bool Ran, T? Value);

    /// <summary>
    /// Keeps a screen's reads of an open archive and the archive's closing apart: no read starts once
    /// closing has begun, and the archive is closed only after every read that did start has finished.
    /// </summary>
    /// <remarks>
    /// <para>A read is "prepare, then read on a worker": preparing is what has to happen first on the
    /// calling thread (stopping the pager's own reads, say), and it may await. Closing can begin while
    /// a read is preparing; the gate checks again before the read itself starts, so a read never runs on
    /// a closed connection.</para>
    /// <para>Call <see cref="RunAsync{T}"/> and <see cref="CloseAsync"/> from one thread (the UI thread):
    /// the gate records a read before its first await, so a close that comes later always sees it.</para>
    /// </remarks>
    public sealed class ArchiveReadGate
    {
        private Task _started = Task.CompletedTask;

        /// <summary>Whether closing has begun: nothing more is read.</summary>
        public bool IsClosed { get; private set; }

        /// <summary>
        /// Runs <paramref name="prepare"/>, then <paramref name="read"/> on a worker thread, unless the
        /// gate is closed by then. An exception from either reaches the caller.
        /// </summary>
        public Task<GatedRead<T>> RunAsync<T>(Func<Task> prepare, Func<T> read)
        {
            ArgumentNullException.ThrowIfNull(prepare);
            ArgumentNullException.ThrowIfNull(read);

            if (IsClosed)
            {
                return Task.FromResult(new GatedRead<T>(false, default));
            }

            var run = RunCoreAsync(prepare, read);
            _started = BothQuietly(_started, run);

            return run;
        }

        /// <summary>
        /// Begins closing: no read starts from now on. Runs <paramref name="stop"/> (stopping reads the
        /// gate does not run), waits for every read already started, then runs <paramref name="close"/>.
        /// Calling it again does nothing.
        /// </summary>
        public async Task CloseAsync(Func<Task> stop, Action close)
        {
            ArgumentNullException.ThrowIfNull(stop);
            ArgumentNullException.ThrowIfNull(close);

            if (IsClosed)
            {
                return;
            }

            IsClosed = true;

            try
            {
                await stop();
            }
            finally
            {
                await _started;
                close();
            }
        }

        private async Task<GatedRead<T>> RunCoreAsync<T>(Func<Task> prepare, Func<T> read)
        {
            await prepare();

            // Closing may have begun while preparing: then the connection is about to close.
            if (IsClosed)
            {
                return new GatedRead<T>(false, default);
            }

            return new GatedRead<T>(true, await Task.Run(read));
        }

        private static async Task BothQuietly(Task first, Task second)
        {
            try
            {
                await first;
            }
            catch (Exception)
            {
                // Reported to whoever ran it; here it only has to be over.
            }

            try
            {
                await second;
            }
            catch (Exception)
            {
                // As above.
            }
        }
    }
}
