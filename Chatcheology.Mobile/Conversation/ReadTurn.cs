namespace Chatcheology.Mobile.Conversation
{
    /// <summary>
    /// The conversation screen's own reads, one at a time (a reopen, the date picker's day read), and a
    /// change of "me" that comes while one of them runs: it waits, and is rebuilt when that read ends,
    /// whichever read it was.
    /// </summary>
    /// <remarks>
    /// <para>Nothing here reads. The reads themselves, the rebuild included, run through the
    /// <see cref="ArchiveReadGate"/> given here, so once the page has begun closing no read starts and no
    /// waiting rebuild is asked for.</para>
    /// <para>Call it from one thread (the UI thread), as the gate is.</para>
    /// </remarks>
    public sealed class ReadTurn
    {
        private readonly ArchiveReadGate _gate;
        private bool _meChangePending;

        /// <summary>The turns of a screen whose reads run through <paramref name="gate"/>.</summary>
        public ReadTurn(ArchiveReadGate gate)
        {
            ArgumentNullException.ThrowIfNull(gate);

            _gate = gate;
        }

        /// <summary>Whether one of the screen's reads is under way.</summary>
        public bool IsBusy { get; private set; }

        /// <summary>Whether a change of "me" is waiting for the read under way to end.</summary>
        public bool IsMeChangePending => _meChangePending;

        /// <summary>
        /// Takes the turn for a read. False when another read has it or the page is closing: do not read.
        /// </summary>
        public bool TryBegin()
        {
            if (IsBusy || _gate.IsClosed)
            {
                return false;
            }

            IsBusy = true;
            return true;
        }

        /// <summary>
        /// "Me" changed. True when the bubbles can be rebuilt now (take the turn with
        /// <see cref="TryBegin"/> to do it); false when a read is under way, in which case the change waits
        /// for <see cref="End"/>, or when the page is closing.
        /// </summary>
        public bool MeChanged()
        {
            if (_gate.IsClosed)
            {
                return false;
            }

            if (IsBusy)
            {
                _meChangePending = true;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Gives the turn back. True when a change of "me" waited for it and the page is not closing: rebuild
        /// now, so the latest choice wins. A waiting change is reported once.
        /// </summary>
        public bool End()
        {
            IsBusy = false;

            if (_meChangePending && !_gate.IsClosed)
            {
                _meChangePending = false;
                return true;
            }

            return false;
        }
    }
}
