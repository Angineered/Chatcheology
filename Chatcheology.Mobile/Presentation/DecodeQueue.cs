namespace Chatcheology.Mobile.Presentation
{
    /// <summary>
    /// Runs picture decodes on a few worker threads, newest request first (CH-66 scroll follow-up).
    /// </summary>
    /// <remarks>
    /// <para>Before it, every thumbnail bound while scrolling started its own thread-pool decode at once: a fling
    /// through the gallery queued hundreds, the pool grew threads for them and they took the processor from the
    /// UI thread, while the thumbnails now on screen waited behind those already scrolled past.</para>
    /// <para>Here at most <see cref="Workers"/> run at a time and the latest request goes first, so the cells the
    /// reader has just reached are drawn before older ones. Each request checks for itself whether it is still
    /// wanted when its turn comes (a cell bound again has moved on) and returns at once if not.</para>
    /// <para>A request that throws does not stop the queue; requests report their own results.</para>
    /// </remarks>
    public sealed class DecodeQueue
    {
        private readonly Lock _lock = new();
        private readonly Stack<Action> _waiting = new();
        private int _running;

        /// <summary>A queue running at most <paramref name="workers"/> requests at a time.</summary>
        public DecodeQueue(int workers)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workers);
            Workers = workers;
        }

        /// <summary>How many requests run at a time.</summary>
        public int Workers { get; }

        /// <summary>The workers for a device with <paramref name="processorCount"/> processors: half of them, 1 to 3.</summary>
        public static int WorkersFor(int processorCount) => Math.Clamp(processorCount / 2, 1, 3);

        /// <summary>Adds a request; it runs on a worker thread, before every request added earlier that has not started.</summary>
        public void Post(Action work)
        {
            ArgumentNullException.ThrowIfNull(work);

            lock (_lock)
            {
                _waiting.Push(work);

                if (_running >= Workers)
                {
                    return;
                }

                _running++;
            }

            ThreadPool.UnsafeQueueUserWorkItem(static queue => queue.Drain(), this, preferLocal: false);
        }

        private void Drain()
        {
            while (true)
            {
                Action? work;

                lock (_lock)
                {
                    if (!_waiting.TryPop(out work))
                    {
                        _running--;
                        return;
                    }
                }

                try
                {
                    work();
                }
                catch (Exception)
                {
                    // The request reports its own result; one that fails must not stop the others.
                }
            }
        }
    }
}
