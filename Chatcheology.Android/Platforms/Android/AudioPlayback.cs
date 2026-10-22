using Android.Media;
using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Android
{
    /// <summary>
    /// Plays restored voice notes and audio in the app (CH-36), with Android's <see cref="MediaPlayer"/>
    /// reading the archive's file directly. One plays at a time: starting another stops the first. The
    /// conversation stops it when its screen is left.
    /// </summary>
    /// <remarks>
    /// Call everything on the UI thread. Cards follow through <see cref="Changed"/>, raised when playing
    /// starts, pauses, stops or ends and about five times a second while it plays; a card shows the state
    /// only when it is <see cref="Current"/>, so a recycled view never shows another card's progress.
    /// </remarks>
    internal static class AudioPlayback
    {
        private static MediaPlayer? _player;
        private static IDispatcherTimer? _ticks;

        /// <summary>Raised when the state or the position changes.</summary>
        internal static event EventHandler? Changed;

        /// <summary>The card being played or paused, or <see langword="null"/>.</summary>
        internal static AttachmentCard? Current { get; private set; }

        /// <summary>Whether <see cref="Current"/> is playing (not paused).</summary>
        internal static bool IsPlaying => _player is { IsPlaying: true };

        /// <summary>How far <see cref="Current"/> has played, from 0 to 1.</summary>
        internal static double Progress
        {
            get
            {
                try
                {
                    return _player is { Duration: > 0 } player ? Math.Clamp((double)player.CurrentPosition / player.Duration, 0, 1) : 0;
                }
                catch (Java.Lang.IllegalStateException)
                {
                    return 0;
                }
            }
        }

        /// <summary>
        /// Play or pause <paramref name="card"/>: a tap on its play button. False when the file cannot be
        /// played, so the caller can say so (and offer another app).
        /// </summary>
        internal static bool Toggle(AttachmentCard card)
        {
            ArgumentNullException.ThrowIfNull(card);

            if (ReferenceEquals(card, Current) && _player is { } player)
            {
                if (player.IsPlaying)
                {
                    player.Pause();
                    _ticks?.Stop();
                }
                else
                {
                    player.Start();
                    _ticks?.Start();
                }

                Changed?.Invoke(null, EventArgs.Empty);
                return true;
            }

            Stop();

            if (card.FullPath is not { } path)
            {
                return false;
            }

            var created = new MediaPlayer();

            try
            {
                created.SetDataSource(path);
                created.Prepare();
            }
            catch (Exception exception) when (exception is Java.Lang.Throwable or IOException)
            {
                created.Release();
                return false;
            }

            created.Completion += (_, _) =>
            {
                if (ReferenceEquals(_player, created))
                {
                    Stop();
                }
            };

            _player = created;
            Current = card;
            created.Start();

            _ticks ??= CreateTicks();
            _ticks.Start();

            Changed?.Invoke(null, EventArgs.Empty);
            return true;
        }

        /// <summary>Stops and releases whatever plays. Does nothing when nothing does.</summary>
        internal static void Stop()
        {
            _ticks?.Stop();

            if (_player is { } player)
            {
                _player = null;

                try
                {
                    player.Stop();
                }
                catch (Java.Lang.IllegalStateException)
                {
                    // Never started or already stopped: released below either way.
                }

                player.Release();
            }

            if (Current is not null)
            {
                Current = null;
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }

        private static IDispatcherTimer CreateTicks()
        {
            var ticks = Application.Current!.Dispatcher.CreateTimer();
            ticks.Interval = TimeSpan.FromMilliseconds(200);
            ticks.Tick += (_, _) => Changed?.Invoke(null, EventArgs.Empty);

            return ticks;
        }
    }
}
